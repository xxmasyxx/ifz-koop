using System;
using System.Collections.Generic;
using System.IO;
using Controllers;
using Gameplay.FogOfWar;
using Gameplay.Units;
using Gameplay.Units.Characters;
using Gameplay.Units.Enemy;
using Gameplay.Vehicles;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using Newtonsoft.Json;
using Steamworks;
using UnityEngine;
using GameJson = Utilities.JsonSerializer;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Einheiten-Abgleich, Stufe 1: Trupps, Horden, Plünderer, Tiere, Zuwanderer und Fahrzeuge.
    ///
    /// Host:  schickt 5-mal pro Sekunde für sichtbare Gruppen und alle eigenen Trupps je Figur Position,
    ///        Blickrichtung, Laufgeschwindigkeit und Lebenspunkte; unsichtbare Gruppen nur ihren
    ///        Ankerpunkt (1-mal pro Sekunde). Neue Gruppen gehen als Spawn mit dem Speicherformat des Spiels
    ///        (GroupSerializedInfo) raus. Alle 2 Sekunden kommt die Liste aller Gruppen-IDs (für Verschwinden).
    /// Gast:  Gruppen sind Marionetten. Group.CustomUpdate läuft nur noch als „Marionetten-Takt“
    ///        (Position glätten, Nebel des Krieges). KI, Spawner und Schaden sind gesperrt (siehe Patches).
    ///        Figuren, die beim Host fehlen, sterben beim Gast; unbekannte Gruppen fordert der Gast an.
    ///
    /// Figuren werden über die Host-ID zugeordnet. Zombies bekommen beim Laden auf jedem Rechner neue IDs,
    /// deshalb wird beim ersten Kontakt nach Reihenfolge in der Gruppe zugeordnet und danach die Host-ID gemerkt.
    /// </summary>
    internal sealed class UnitSync
    {
        private const float SnapshotInterval = 0.2f;
        private const int AnchorEveryNth = 5;
        private const float RosterInterval = 2f;
        private const int GroupsPerMessage = 30;
        private const float TeleportDistance = 25f;
        private const float SpawnRequestCooldown = 5f;

        /// <summary>true, solange der Gast etwas ausführt, das vom Host kommt (Spawn, Tod, Verschwinden).</summary>
        public static bool NetApplying;

        private static readonly AccessTools.FieldRef<Group, FogOfWarObserver> GroupObserverField =
            AccessTools.FieldRefAccess<Group, FogOfWarObserver>("_fogOfWarObserver");

        private sealed class Target
        {
            public Vector3 Position;
            public float Yaw;
            public float Speed;
            public bool HasYaw;
            // Vorhersage zwischen zwei Schnappschüssen (0,2 s): Figuren laufen gleichmäßig weiter statt ruckweise
            public Vector3 Velocity;
            public float ReceivedAt;

            /// <summary>Wo die Figur jetzt beim Host ungefähr ist: letzte Position plus Geschwindigkeit (höchstens 0,35 s weit).</summary>
            public Vector3 Predicted(float now)
            {
                if (Velocity.sqrMagnitude < 0.0001f || ReceivedAt <= 0f)
                    return Position;
                float age = Mathf.Clamp(now - ReceivedAt, 0f, MaxPredict);
                return Position + Velocity * age;
            }
        }

        private const float MaxPredict = 0.35f;
        private const float MaxPredictSpeed = 14f;

        private sealed class GroupMap
        {
            public readonly Dictionary<string, Character> ByHostId = new Dictionary<string, Character>();
            public Vector3 LastAnchor;
            public bool HasAnchor;
            public int MissingInRoster;
            public Vector3 AnchorTarget;   // Gruppenposition beim Host (Gruppenobjekt, nicht die Figuren)
            public bool HasAnchorTarget;
            public bool HostVisible;       // sieht der Host die Gruppe? (= Schnappschuss mit Figuren)
            public bool HasHostVisible;
        }

        private readonly Session _session;

        // Host
        private float _nextSnapshot;
        private float _nextRoster;
        private int _tick;

        // Gast
        private readonly Dictionary<Character, Target> _targets = new Dictionary<Character, Target>();
        private readonly Dictionary<string, GroupMap> _maps = new Dictionary<string, GroupMap>();
        private readonly Dictionary<string, float> _spawnRequested = new Dictionary<string, float>();
        private readonly Dictionary<string, Target> _vehicleTargets = new Dictionary<string, Target>();
        private float _nextCleanup;

        public int SnapshotsReceived { get; private set; }
        public int SpawnsApplied { get; private set; }
        public int DeathsApplied { get; private set; }

        public UnitSync(Session session)
        {
            _session = session;
        }

        /// <summary>Trupp-Abgleich (neue Trupps, Auffüllen, Auflösen, Tode eigener Leute).</summary>
        public SquadSync Squads { get; set; }

        /// <summary>Gast: Figur über Gruppen-ID und Host-Figuren-ID finden.</summary>
        public Character ResolveCharacter(string groupId, string hostCharacterId)
        {
            if (string.IsNullOrEmpty(groupId))
                return null;
            if (_maps.TryGetValue(groupId, out var map) && hostCharacterId != null
                && map.ByHostId.TryGetValue(hostCharacterId, out var mapped) && IsAlive(mapped))
                return mapped;
            var group = FindGroup(groupId);
            if (group == null)
                return null;
            var characters = group.Characters;
            for (int i = 0; i < characters.Count; i++)
                if (IsAlive(characters[i]) && characters[i].Id == hostCharacterId)
                    return characters[i];
            return null;
        }

        /// <summary>Gast: Figur aus allen Zuordnungen nehmen (z. B. nach gezieltem Tod).</summary>
        public void Forget(Character c)
        {
            if (c == null)
                return;
            _targets.Remove(c);
            foreach (var map in _maps.Values)
                foreach (var key in new List<string>(map.ByHostId.Keys))
                    if (ReferenceEquals(map.ByHostId[key], c)) map.ByHostId.Remove(key);
        }

        internal static Group FindGroupPublic(string id) => FindGroup(id);

        /// <summary>Gast im Koop-Spiel: Einheiten sind Marionetten.</summary>
        public bool PuppetMode => _session.GuestControlsLocked;

        private bool HostWithGuests => _session.Role == Role.Host && _session.ActiveGuestCount > 0;

        public void Reset()
        {
            _targets.Clear();
            _maps.Clear();
            _spawnRequested.Clear();
            _vehicleTargets.Clear();
            NetApplying = false;
        }

        // ================================================================ Host

        public void HostUpdate()
        {
            if (!HostWithGuests || !GameAccess.IsGameStarted)
                return;
            float now = Time.realtimeSinceStartup;

            if (now >= _nextSnapshot)
            {
                _nextSnapshot = now + SnapshotInterval;
                _tick++;
                SendSnapshots(_tick % AnchorEveryNth == 0);
                SendVehicles();
            }

            if (now >= _nextRoster)
            {
                _nextRoster = now + RosterInterval;
                SendRoster();
            }
        }

        private static List<Group> AllGroups()
        {
            var list = new List<Group>();
            var squads = GameAccess.Game<SquadsController>()?.Squads;
            if (squads != null)
                for (int i = 0; i < squads.Count; i++)
                    if (squads[i] != null) list.Add(squads[i]);
            var groups = GameAccess.Game<GroupsController>()?.Groups;
            if (groups != null)
                for (int i = 0; i < groups.Count; i++)
                    if (groups[i] != null && !list.Contains(groups[i])) list.Add(groups[i]);
            return list;
        }

        private void SendSnapshots(bool includeInvisible)
        {
            var batch = new List<Group>(GroupsPerMessage);
            foreach (var group in AllGroups())
            {
                if (group.IsEmpty() || string.IsNullOrEmpty(group.Id))
                    continue;
                bool full = group.Fraction == Fraction.Player || group.IsVisible();
                if (!full && !includeInvisible)
                    continue;
                batch.Add(group);
                if (batch.Count >= GroupsPerMessage)
                {
                    _session.BroadcastToGuests(BuildSnapshot(batch));
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
                _session.BroadcastToGuests(BuildSnapshot(batch));
        }

        private static byte[] BuildSnapshot(List<Group> groups)
        {
            return Protocol.Build(Msg.UnitSnapshot, w =>
            {
                w.Write(groups.Count);
                foreach (var group in groups)
                {
                    bool full = group.Fraction == Fraction.Player || group.IsVisible();
                    w.Write(group.Id);
                    w.Write(full);
                    WriteVector(w, group.Position);
                    if (!full)
                        continue;

                    var characters = group.Characters;
                    int alive = 0;
                    for (int i = 0; i < characters.Count; i++)
                        if (IsAlive(characters[i])) alive++;
                    w.Write((ushort)alive);
                    for (int i = 0; i < characters.Count; i++)
                    {
                        var c = characters[i];
                        if (!IsAlive(c))
                            continue;
                        w.Write(c.Id ?? string.Empty);
                        WriteVector(w, c.Position);
                        w.Write(YawToByte(c.Movement.transform.eulerAngles.y));
                        w.Write((byte)Mathf.Clamp(Mathf.RoundToInt(c.Movement.NormalizedSpeed * 100f), 0, 255));
                        var health = c.Health;
                        float ratio = health == null || health.MaxHp <= 0f ? 1f : health.Hp / health.MaxHp;
                        w.Write((byte)Mathf.Clamp(Mathf.RoundToInt(ratio * 255f), 0, 255));
                    }
                }
            });
        }

        private void SendRoster()
        {
            var ids = new List<string>();
            foreach (var group in AllGroups())
                if (!group.IsEmpty() && !string.IsNullOrEmpty(group.Id))
                    ids.Add(group.Id);
            _session.BroadcastToGuests(Protocol.Build(Msg.UnitRoster, w =>
            {
                w.Write(ids.Count);
                foreach (var id in ids) w.Write(id);
            }));
        }

        private void SendVehicles()
        {
            var vehicles = GameAccess.Game<VehiclesController>()?.Vehicles;
            if (vehicles == null || vehicles.Count == 0)
                return;
            _session.BroadcastToGuests(Protocol.Build(Msg.VehicleSnapshot, w =>
            {
                int count = 0;
                for (int i = 0; i < vehicles.Count; i++)
                    if (vehicles[i] != null && !string.IsNullOrEmpty(vehicles[i].Id)) count++;
                w.Write(count);
                for (int i = 0; i < vehicles.Count; i++)
                {
                    var v = vehicles[i];
                    if (v == null || string.IsNullOrEmpty(v.Id))
                        continue;
                    w.Write(v.Id);
                    WriteVector(w, v.Position);
                    w.Write(YawToByte(v.Movement.transform.eulerAngles.y));
                }
            }));
        }

        /// <summary>Postfix von GroupBuilder.Build beim Host: neue Gruppe an alle Gäste.</summary>
        public void OnGroupBuilt(Group group)
        {
            if (!HostWithGuests || group == null || string.IsNullOrEmpty(group.Id))
                return;
            if (group.Fraction == Fraction.Player)
            {
                // Neue Trupps entstehen aus vorhandenen Arbeitern: eigener Weg über SquadSync.
                Squads?.OnPlayerSquadBuilt(group);
                return;
            }
            // Sitzt die neue Gruppe in einem Auto (Banditen im Wagen), das Auto zuerst schicken: LoadGroup sucht es per ID.
            if (group.VehicleHandler != null && group.VehicleHandler.Vehicle != null)
                _session.Vehicles.HostSendNow(group.VehicleHandler.Vehicle);
            var spawn = BuildSpawn(group);
            if (spawn != null)
                _session.BroadcastToGuests(spawn);
        }

        public void HostOnSpawnRequest(Peer from, BinaryReader r)
        {
            string id = r.ReadString();
            foreach (var group in AllGroups())
            {
                if (group.Id != id)
                    continue;
                if (group.Fraction == Fraction.Player)
                {
                    Squads?.SendSquadTo(from, group);
                    return;
                }
                var spawn = BuildSpawn(group);
                if (spawn != null)
                    _session.SendTo(from.Id, spawn);
                return;
            }
        }

        private static byte[] BuildSpawn(Group group)
        {
            try
            {
                var info = new GroupSerializedInfo(group);
                string json = JsonConvert.SerializeObject(info, GameJson.DefaultSettings);
                return Protocol.Build(Msg.GroupSpawn, w => w.Write(json));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Gruppe {group.Id} lässt sich nicht übertragen: {e.Message}");
                return null;
            }
        }

        // ================================================================ Gast: empfangen

        public void GuestOnSpawn(BinaryReader r)
        {
            string json = r.ReadString();
            GroupSerializedInfo info;
            try
            {
                info = JsonConvert.DeserializeObject<GroupSerializedInfo>(json, GameJson.DefaultSettings);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Gruppen-Spawn nicht lesbar: " + e.Message);
                return;
            }
            if (info == null || string.IsNullOrEmpty(info.Id) || FindGroup(info.Id) != null)
                return;

            var builder = GameAccess.Game<GroupBuilder>();
            var draft = JutsuGamesConfig.Runtime.Configs.GetConfig<GroupsConfig>()?.GetDraftByName(info.GroupDraftName);
            if (builder == null || draft == null)
            {
                Plugin.Log.LogWarning($"Gruppe {info.Id} ({info.GroupDraftName}) kann hier nicht gebaut werden");
                return;
            }

            NetApplying = true;
            try
            {
                var group = builder.LoadGroup(info, draft);
                if (group != null)
                {
                    var map = MapFor(group.Id);
                    map.ByHostId.Clear(); // Zombie-IDs sind neu vergeben: beim ersten Schnappschuss nach Reihenfolge
                    SpawnsApplied++;
                    Plugin.Log.LogInfo($"IFZCOOP spawn {info.Id} ({info.GroupDraftName}, {info.MembersCount})");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Gruppe {info.Id} bauen: {e}");
            }
            finally
            {
                NetApplying = false;
            }
        }

        public void GuestOnSnapshot(BinaryReader r)
        {
            SnapshotsReceived++;
            int count = r.ReadInt32();
            if (count < 0 || count > 10000)
                throw new InvalidDataException("Schnappschuss zu groß");
            var hostIds = new List<string>();
            var hostData = new List<Target>();

            for (int g = 0; g < count; g++)
            {
                string id = r.ReadString();
                bool full = r.ReadBoolean();
                var anchor = ReadVector(r);
                hostIds.Clear();
                hostData.Clear();
                if (full)
                {
                    int members = r.ReadUInt16();
                    for (int m = 0; m < members; m++)
                    {
                        hostIds.Add(r.ReadString());
                        var pos = ReadVector(r);
                        float yaw = ByteToYaw(r.ReadByte());
                        float speed = r.ReadByte() / 100f;
                        float hp = r.ReadByte() / 255f;
                        hostData.Add(new Target { Position = pos, Yaw = yaw, Speed = speed, HasYaw = true });
                        _hpScratch.Add(hp);
                    }
                }

                var group = FindGroup(id);
                if (group == null)
                {
                    RequestSpawn(id);
                    _hpScratch.Clear();
                    continue;
                }

                var map = MapFor(id);
                map.MissingInRoster = 0;
                map.HostVisible = full;
                map.HasHostVisible = true;
                if (full)
                    ApplyMembers(group, map, hostIds, hostData);
                else
                    ApplyAnchor(group, map, anchor);
                map.LastAnchor = anchor;
                map.HasAnchor = true;
                map.AnchorTarget = anchor;
                map.HasAnchorTarget = true;
                _hpScratch.Clear();
            }
        }

        private readonly List<float> _hpScratch = new List<float>();

        private void ApplyMembers(Group group, GroupMap map, List<string> hostIds, List<Target> hostData)
        {
            var characters = group.Characters;

            // Bekannte Zuordnungen aufräumen (Figuren können zerstört sein).
            var stale = new List<string>();
            foreach (var kv in map.ByHostId)
                if (kv.Value == null || !IsAlive(kv.Value)) stale.Add(kv.Key);
            foreach (var key in stale) map.ByHostId.Remove(key);

            var used = new HashSet<Character>(map.ByHostId.Values);

            // Zuerst gleiche Figuren-ID (eigene Trupps und Figuren aus demselben Spielstand).
            for (int i = 0; i < hostIds.Count; i++)
            {
                if (map.ByHostId.ContainsKey(hostIds[i]) || string.IsNullOrEmpty(hostIds[i]))
                    continue;
                for (int j = 0; j < characters.Count; j++)
                {
                    var c = characters[j];
                    if (!used.Contains(c) && IsAlive(c) && c.Id == hostIds[i])
                    {
                        map.ByHostId[hostIds[i]] = c;
                        used.Add(c);
                        break;
                    }
                }
            }

            // Eigene Trupps: ein neues Mitglied beim Host (Auffüllen) ist beim Gast noch ein Arbeiter mit derselben ID.
            if (group.Fraction == Fraction.Player && Squads != null)
            {
                for (int i = 0; i < hostIds.Count; i++)
                {
                    if (map.ByHostId.ContainsKey(hostIds[i]))
                        continue;
                    var adopted = Squads.AdoptWorker(group, hostIds[i]);
                    if (adopted != null)
                    {
                        map.ByHostId[hostIds[i]] = adopted;
                        used.Add(adopted);
                    }
                }
                characters = group.Characters;
            }

            // Dann unzugeordnete Host-IDs der Reihe nach auf unzugeordnete Gast-Figuren legen
            // (Zombies bekommen beim Laden auf jedem Rechner neue IDs). Nicht bei eigenen Trupps:
            // dort stimmen die IDs, und eine falsche Zuordnung würde Figuren vertauschen.
            int next = group.Fraction == Fraction.Player ? characters.Count : 0;
            for (int i = 0; i < hostIds.Count; i++)
            {
                if (map.ByHostId.ContainsKey(hostIds[i]))
                    continue;
                while (next < characters.Count && (used.Contains(characters[next]) || !IsAlive(characters[next])))
                    next++;
                if (next >= characters.Count)
                    break;
                map.ByHostId[hostIds[i]] = characters[next];
                used.Add(characters[next]);
            }

            // Ziele und Lebenspunkte setzen.
            var present = new HashSet<string>();
            for (int i = 0; i < hostIds.Count; i++)
            {
                present.Add(hostIds[i]);
                if (!map.ByHostId.TryGetValue(hostIds[i], out var c) || c == null)
                    continue;
                SetTarget(c, hostData[i]);
                var health = c.Health;
                if (health != null && health.MaxHp > 0f && i < _hpScratch.Count)
                {
                    float hp = Mathf.Max(1f, _hpScratch[i] * health.MaxHp);
                    if (Mathf.Abs(health.Hp - hp) > health.MaxHp * 0.01f)
                        health.SetHp(hp);
                }
            }

            // Was der Host nicht mehr hat: bei Horden & Co. gestorben, bei eigenen Trupps wieder Arbeiter
            // (Tode eigener Leute kommen vorher als eigenes Ereignis, Nachrichten sind geordnet).
            var gone = new List<Character>();
            foreach (var kv in map.ByHostId)
                if (!present.Contains(kv.Key)) gone.Add(kv.Value);
            for (int i = 0; i < characters.Count; i++)
                if (IsAlive(characters[i]) && !used.Contains(characters[i]) && !gone.Contains(characters[i]))
                    gone.Add(characters[i]); // Gast hat mehr Figuren als der Host
            foreach (var c in gone)
            {
                if (group.Fraction == Fraction.Player && Squads != null)
                {
                    foreach (var key in new List<string>(map.ByHostId.Keys))
                        if (ReferenceEquals(map.ByHostId[key], c)) map.ByHostId.Remove(key);
                    _targets.Remove(c);
                    Squads.ReleaseToWorker(c);
                }
                else
                {
                    KillCharacter(c, map);
                }
            }
        }

        private void ApplyAnchor(Group group, GroupMap map, Vector3 anchor)
        {
            if (!map.HasAnchor)
                return;
            var delta = anchor - map.LastAnchor;
            if (delta.sqrMagnitude < 0.0001f)
                return;
            var characters = group.Characters;
            for (int i = 0; i < characters.Count; i++)
            {
                var c = characters[i];
                if (!IsAlive(c))
                    continue;
                var basePos = _targets.TryGetValue(c, out var t) ? t.Position : c.Position;
                SetTarget(c, new Target { Position = basePos + delta, Speed = 0.5f });
            }
        }

        public void GuestOnRoster(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 100000)
                return;
            var ids = new HashSet<string>();
            for (int i = 0; i < n; i++)
                ids.Add(r.ReadString());

            var gone = new List<Group>();
            foreach (var g in AllGroups())
            {
                if (g == null || string.IsNullOrEmpty(g.Id) || g.IsEmpty() || ids.Contains(g.Id))
                    continue;
                var map = MapFor(g.Id);
                map.MissingInRoster++;
                if (map.MissingInRoster >= 2)
                    gone.Add(g);
            }
            foreach (var g in gone)
            {
                NetApplying = true;
                try
                {
                    if (g.Fraction == Fraction.Player)
                        g.Disband(); // Trupp beim Host aufgelöst: Mitglieder werden wieder Arbeiter
                    else
                        g.DestroySilent();
                    _maps.Remove(g.Id);
                    Plugin.Log.LogInfo("IFZCOOP despawn " + g.Id);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Gruppe entfernen: " + e.Message);
                }
                finally
                {
                    NetApplying = false;
                }
            }
        }

        public void GuestOnVehicles(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 10000)
                return;
            for (int i = 0; i < n; i++)
            {
                string id = r.ReadString();
                var pos = ReadVector(r);
                float yaw = ByteToYaw(r.ReadByte());
                _vehicleTargets[id] = new Target { Position = pos, Yaw = yaw, HasYaw = true };
            }
        }

        private void RequestSpawn(string id)
        {
            float now = Time.realtimeSinceStartup;
            if (_spawnRequested.TryGetValue(id, out var last) && now - last < SpawnRequestCooldown)
                return;
            _spawnRequested[id] = now;
            _session.SendToHost(Protocol.Build(Msg.GroupSpawnRequest, w => w.Write(id)));
        }

        private void KillCharacter(Character c, GroupMap map)
        {
            if (c == null)
                return;
            foreach (var key in new List<string>(map.ByHostId.Keys))
                if (ReferenceEquals(map.ByHostId[key], c)) map.ByHostId.Remove(key);
            _targets.Remove(c);
            if (!IsAlive(c))
                return;
            NetApplying = true;
            try
            {
                c.Kill(SubtractionHpReason.Enemy);
                DeathsApplied++;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Figur sterben lassen: " + e.Message);
            }
            finally
            {
                NetApplying = false;
            }
        }

        // ================================================================ Gast: jeden Frame

        /// <summary>
        /// Ersatz für Group.CustomUpdate beim Gast: Figuren zum Ziel gleiten lassen, Laufanimation setzen,
        /// Gruppenposition und Nebel des Krieges aktualisieren. Keine KI, keine Befehle, kein Angriff.
        /// </summary>
        public void PuppetTick(Group group)
        {
            if (group == null || group.IsEmpty())
                return;
            float k = 1f - Mathf.Exp(-12f * Time.deltaTime);
            float nowReal = Time.realtimeSinceStartup;
            var characters = group.Characters;
            for (int i = 0; i < characters.Count; i++)
            {
                var c = characters[i];
                if (c == null || !_targets.TryGetValue(c, out var t))
                    continue;
                var movement = c.Movement;
                if (movement == null)
                    continue;
                var current = movement.Position;
                var goal = t.Predicted(nowReal);
                if ((current - t.Position).sqrMagnitude > TeleportDistance * TeleportDistance)
                    movement.Teleport(t.Position);
                else
                    movement.SetPosition(Vector3.Lerp(current, goal, k), updateOnExpedition: false);
                if (t.HasYaw)
                {
                    var tr = movement.transform;
                    tr.rotation = Quaternion.Slerp(tr.rotation, Quaternion.Euler(0f, t.Yaw, 0f), k);
                }
                try
                {
                    c.AnimationController?.SetSpeed(t.Speed);
                }
                catch (Exception)
                {
                    // Manche Figurentypen haben keinen passenden Animationsparameter.
                }
            }

            // Die Gruppenposition ist die des Gruppenobjekts (GroupMovement.Position = transform.position),
            // nicht die der Figuren: Symbol, Auswahl und Nebel des Krieges hängen daran.
            if (!string.IsNullOrEmpty(group.Id) && _maps.TryGetValue(group.Id, out var gmap) && gmap.HasAnchorTarget)
            {
                var tr = group.transform;
                if ((tr.position - gmap.AnchorTarget).sqrMagnitude > TeleportDistance * TeleportDistance)
                    tr.position = gmap.AnchorTarget;
                else
                    tr.position = Vector3.Lerp(tr.position, gmap.AnchorTarget, k);
            }

            group.CachePosition();
            if (group.Fraction != Fraction.Player)
                ApplyVisibility(group);
            var observer = GroupObserverField(group);
            if (observer != null)
                observer.CustomUpdate(group.Position);
        }

        /// <summary>
        /// Sichtbarkeit (Nebel des Krieges) fremder Gruppen: wie beim Host. Beide haben dieselben Trupps und Gebäude,
        /// sollen also dasselbe sehen. Selbst gerechnet kam beim Gast etwas anderes heraus (Horden erst beim Kontakt).
        /// Solange der Host noch nichts zur Gruppe geschickt hat, rechnet der Gast selbst.
        /// </summary>
        private void ApplyVisibility(Group group)
        {
            var fog = group.FogOfWarDetectable;
            if (fog == null)
                return;
            if (string.IsNullOrEmpty(group.Id) || !_maps.TryGetValue(group.Id, out var map) || !map.HasHostVisible)
            {
                fog.CustomUpdate();
                return;
            }
            // Direkt Show/Hide statt UpdateVisibility: die überspringt Gruppen in Gebäuden (Horden stehen meist in
            // einem), die der Host über andere Wege zeigt (Trupp betritt das Gebäude, Gruppe kommt heraus).
            fog.SetObserversCount(map.HostVisible ? 1 : 0);
            if (map.HostVisible && !group.IsVisible())
                group.Show();
            else if (!map.HostVisible && group.IsVisible())
                group.Hide();
        }

        /// <summary>Fahrzeuge beim Gast jeden Frame zum Ziel gleiten lassen.</summary>
        public void GuestFrame()
        {
            if (!PuppetMode)
                return;

            if (_vehicleTargets.Count > 0)
            {
                var vehicles = GameAccess.Game<VehiclesController>()?.Vehicles;
                if (vehicles != null)
                {
                    float k = 1f - Mathf.Exp(-10f * Time.deltaTime);
                    for (int i = 0; i < vehicles.Count; i++)
                    {
                        var v = vehicles[i];
                        if (v == null || string.IsNullOrEmpty(v.Id) || !_vehicleTargets.TryGetValue(v.Id, out var t))
                            continue;
                        var movement = v.Movement;
                        if (movement == null)
                            continue;
                        if ((movement.Position - t.Position).sqrMagnitude > TeleportDistance * TeleportDistance)
                            movement.Teleport(t.Position);
                        else
                            movement.SetPosition(Vector3.Lerp(movement.Position, t.Position, k), updateOnExpedition: false);
                        var tr = movement.transform;
                        tr.rotation = Quaternion.Slerp(tr.rotation, Quaternion.Euler(0f, t.Yaw, 0f), k);
                    }
                }
            }

            float now = Time.realtimeSinceStartup;
            if (now >= _nextCleanup)
            {
                _nextCleanup = now + 5f;
                var dead = new List<Character>();
                foreach (var c in _targets.Keys)
                    if (c == null || !IsAlive(c)) dead.Add(c);
                foreach (var c in dead) _targets.Remove(c);
            }
        }

        // ================================================================ Hilfen

        private void SetTarget(Character c, Target t)
        {
            float now = Time.realtimeSinceStartup;
            if (!_targets.TryGetValue(c, out var existing))
            {
                t.ReceivedAt = now;
                _targets[c] = t;
                return;
            }
            // Geschwindigkeit aus den letzten zwei Schnappschüssen (ruhig gestellt), für gleichmäßiges Laufen
            float dt = now - existing.ReceivedAt;
            if (existing.ReceivedAt > 0f && dt >= 0.05f && dt <= 1.2f)
            {
                var v = (t.Position - existing.Position) / dt;
                v.y = 0f;
                if (v.sqrMagnitude > MaxPredictSpeed * MaxPredictSpeed || v.sqrMagnitude < 0.04f)
                    v = Vector3.zero; // Sprung (Teleport, Gebäude) oder Stillstand
                existing.Velocity = existing.Velocity.sqrMagnitude > 0f && v.sqrMagnitude > 0f
                    ? Vector3.Lerp(existing.Velocity, v, 0.6f)
                    : v;
            }
            else
                existing.Velocity = Vector3.zero;
            existing.ReceivedAt = now;
            existing.Position = t.Position;
            existing.Speed = t.Speed;
            if (t.HasYaw)
            {
                existing.Yaw = t.Yaw;
                existing.HasYaw = true;
            }
        }

        private GroupMap MapFor(string id)
        {
            if (!_maps.TryGetValue(id, out var map))
            {
                map = new GroupMap();
                _maps[id] = map;
            }
            return map;
        }

        private static Group FindGroup(string id)
        {
            var squads = GameAccess.Game<SquadsController>()?.Squads;
            if (squads != null)
                for (int i = 0; i < squads.Count; i++)
                    if (squads[i] != null && squads[i].Id == id) return squads[i];
            return GameAccess.Game<GroupsController>()?.Get(id);
        }

        private static bool IsAlive(Character c)
        {
            if (c == null)
                return false;
            var h = c.Health;
            return h == null || !h.IsDead();
        }

        private static byte YawToByte(float yaw) => (byte)Mathf.RoundToInt(Mathf.Repeat(yaw, 360f) / 360f * 255f);

        private static float ByteToYaw(byte b) => b / 255f * 360f;

        private static void WriteVector(BinaryWriter w, Vector3 v)
        {
            w.Write(v.x);
            w.Write(v.y);
            w.Write(v.z);
        }

        private static Vector3 ReadVector(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }
}
