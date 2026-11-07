using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Controllers;
using Gameplay.GameResources;
using Gameplay.Units;
using Gameplay.Units.Characters;
using Gameplay.Units.Enemy;
using Gameplay.Units.Equipment;
using Gameplay.Units.Interfaces;
using Data.Resorces;
using Gameplay.Vehicles;
using IfzCoop.Game;
using IfzCoop.Net;
using UnityEngine;

namespace IfzCoop.Versus
{
    /// <summary>
    /// Fremde Trupps im Versus (KONZEPT-VERSUS Schritt 2 und 6).
    ///
    /// - Jeder Spieler schickt 5× pro Sekunde seine Trupps (VsUnitSnap): Ort, je Figur ID, Ort, Blick, Tempo, Leben, Waffe;
    ///   sitzt der Trupp im Auto: Auto-ID, Art, Ort, Blick. Alle 2 s die Liste seiner Trupps (VsUnitRoster).
    ///   Gäste schicken an den Host, der Host verteilt an alle anderen (Sternform).
    /// - Beim Empfänger je fremdem Trupp ein Stellvertreter (Einwanderer-Vorlage, „Überlebende“ gibt es in den Spieldaten
    ///   nicht): Schild in Spielerfarbe, Name des Spielers, Waffen wie beim Besitzer, keine eigene KI (Prefix
    ///   Group.CustomUpdate → Tick). Im Auto: ein Stellvertreter-Auto derselben Art, der Trupp sitzt drin; übernehmen lässt
    ///   es sich nicht.
    /// - Sicht: nur im eigenen Nebel, Verbündete immer.
    /// - Kampf: nicht verbündet = feindlich (die eigenen Trupps und Türme greifen von selbst an). Treffer an Stellvertretern
    ///   zieht der Empfänger NICHT selbst ab, er schickt sie an den Besitzer (VsHit); der zieht sie bei seiner echten Figur
    ///   ab (mit Schutzkleidung usw.). Tote und Lebenspunkte kommen mit dem nächsten Schnappschuss zurück.
    ///   Zombies und Banditen der eigenen Welt richten an Stellvertretern nichts an (jeder rechnet noch seine eigene Welt).
    /// </summary>
    internal sealed class VsUnits
    {
        private const float SendInterval = 0.2f;
        private const float RosterInterval = 2f;
        private const float ProxyTimeout = 12f;
        private const float TeleportDistance = 25f;
        private const int MaxGroups = 64;
        private const int MaxMembers = 40;

        public static VsUnits Instance { get; private set; }

        /// <summary>true, solange ein Stellvertreter gebaut, verändert oder entfernt wird (Sperren lassen dann durch).</summary>
        public static bool Applying;

        private sealed class Member
        {
            public string CharId;
            public Vector3 Position;
            public float Yaw;
            public float Speed;
            public float Hp;
            public int Weapon;
        }

        private sealed class GroupData
        {
            public string Id;
            public Vector3 Anchor;
            public readonly List<Member> Members = new List<Member>();
            public string VehicleId;   // null = zu Fuß
            public int VehicleType;
            public Vector3 VehiclePos;
            public float VehicleYaw;
        }

        private sealed class Proxy
        {
            public byte Slot;
            public string OwnerId;
            public Group Group;
            public Vector3 Anchor;
            public readonly List<Member> Targets = new List<Member>();
            public readonly List<Character> Order = new List<Character>(); // Figur i ↔ Targets[i]
            public readonly Dictionary<Character, int> Weapons = new Dictionary<Character, int>();
            public float LastHeard;
            public Color Color;
            public Affiliation Aff = Affiliation.Neutral;
            public Vehicle Vehicle;
            public string VehicleOwnerId;
            public Vector3 VehiclePos;
            public float VehicleYaw;
        }

        private readonly VersusSession _vs;
        private readonly Dictionary<string, Proxy> _proxies = new Dictionary<string, Proxy>();
        private readonly Dictionary<Group, Proxy> _byGroup = new Dictionary<Group, Proxy>();
        private readonly HashSet<Vehicle> _proxyVehicles = new HashSet<Vehicle>();
        private float _nextSend, _nextRoster, _nextCleanup;

        public int SnapsSent { get; private set; }
        public int SnapsReceived { get; private set; }
        public int ProxiesBuilt { get; private set; }
        public int ProxiesRemoved { get; private set; }
        public int HitsSent { get; private set; }
        public int HitsApplied { get; private set; }
        public float DamageSent { get; private set; }
        public float DamageTaken { get; private set; }

        public VsUnits(VersusSession vs)
        {
            _vs = vs;
            Instance = this;
        }

        public static bool IsProxy(Group g) => g != null && Instance != null && Instance._byGroup.ContainsKey(g);
        public static bool IsProxyVehicle(Vehicle v) => v != null && Instance != null && Instance._proxyVehicles.Contains(v);

        public void Reset()
        {
            foreach (var p in new List<Proxy>(_proxies.Values))
                Destroy(p);
            _proxies.Clear();
            _byGroup.Clear();
            _proxyVehicles.Clear();
            _nextSend = _nextRoster = _nextCleanup = 0f;
        }

        private bool Running => _vs.Phase == VsPhase.Running && GameAccess.IsGameStarted && _vs.Start.MyHqConfirmed;

        // ================================================================ Senden

        public void Update(float now)
        {
            if (!Running)
                return;
            if (now >= _nextSend)
            {
                _nextSend = now + SendInterval;
                var groups = OwnSquads();
                if (groups.Count > 0)
                {
                    var data = new List<GroupData>();
                    foreach (var g in groups) data.Add(Capture(g));
                    Send(BuildSnap(_vs.MySlot, data), null);
                    SnapsSent++;
                }
            }
            if (now >= _nextRoster)
            {
                _nextRoster = now + RosterInterval;
                var ids = new List<string>();
                foreach (var g in OwnSquads()) ids.Add(g.Id);
                Send(BuildRoster(_vs.MySlot, ids), null);
            }
            if (now >= _nextCleanup)
            {
                _nextCleanup = now + 2f;
                foreach (var p in new List<Proxy>(_proxies.Values))
                    if (p.Group == null || now - p.LastHeard > ProxyTimeout || _vs.Roster.BySlot(p.Slot) == null)
                        Destroy(p);
            }
        }

        /// <summary>Host: an alle geladenen Gäste außer exceptSlot. Gast: an den Host.</summary>
        private void Send(byte[] msg, byte? exceptSlot)
        {
            if (_vs.Role == VsRole.Host)
            {
                foreach (var peer in _vs.Peers)
                    if (peer.Welcomed && peer.Loaded && (!exceptSlot.HasValue || peer.Slot != exceptSlot.Value))
                        _vs.SendTo(peer.Id, msg);
            }
            else
            {
                _vs.SendToHost(msg);
            }
        }

        private static List<Group> OwnSquads()
        {
            var list = new List<Group>();
            var squads = GameAccess.Game<SquadsController>()?.Squads;
            if (squads == null)
                return list;
            for (int i = 0; i < squads.Count && list.Count < MaxGroups; i++)
            {
                var g = squads[i];
                if (g == null || g.IsEmpty() || string.IsNullOrEmpty(g.Id) || g.Fraction != Fraction.Player || IsProxy(g))
                    continue;
                list.Add(g);
            }
            return list;
        }

        private static GroupData Capture(Group g)
        {
            var d = new GroupData { Id = g.Id, Anchor = g.Position };
            var chars = g.Characters;
            for (int i = 0; i < chars.Count && d.Members.Count < MaxMembers; i++)
            {
                var c = chars[i];
                if (!IsAlive(c) || c.Movement == null)
                    continue;
                var h = c.Health;
                int weapon = 0;
                try { var w = c.ItemsContainer?.WeaponHandler?.CurrentWeapon; if (w != null) weapon = (int)w.ResourceType; } catch { }
                d.Members.Add(new Member
                {
                    CharId = c.Id ?? "",
                    Position = c.Position,
                    Yaw = c.Movement.transform.eulerAngles.y,
                    Speed = c.Movement.NormalizedSpeed,
                    Hp = h == null || h.MaxHp <= 0f ? 1f : h.Hp / h.MaxHp,
                    Weapon = weapon,
                });
            }
            try
            {
                var v = g.VehicleHandler != null && g.VehicleHandler.HaveVehicle() ? g.VehicleHandler.Vehicle : null;
                if (v != null && !string.IsNullOrEmpty(v.Id))
                {
                    d.VehicleId = v.Id;
                    d.VehicleType = (int)v.Data.GetVehicleType();
                    d.VehiclePos = v.Position;
                    d.VehicleYaw = v.Movement.transform.eulerAngles.y;
                }
            }
            catch { }
            return d;
        }

        private static byte[] BuildSnap(byte slot, List<GroupData> groups)
        {
            return VsNet.Build(Msg.VsUnitSnap, w =>
            {
                w.Write(slot);
                w.Write((byte)groups.Count);
                foreach (var g in groups)
                {
                    w.Write(g.Id);
                    WriteVector(w, g.Anchor);
                    w.Write(g.VehicleId != null);
                    if (g.VehicleId != null)
                    {
                        w.Write(g.VehicleId);
                        w.Write(g.VehicleType);
                        WriteVector(w, g.VehiclePos);
                        w.Write(YawToByte(g.VehicleYaw));
                    }
                    w.Write((byte)g.Members.Count);
                    foreach (var m in g.Members)
                    {
                        w.Write(m.CharId ?? "");
                        WriteVector(w, m.Position);
                        w.Write(YawToByte(m.Yaw));
                        w.Write((byte)Mathf.Clamp(Mathf.RoundToInt(m.Speed * 100f), 0, 255));
                        w.Write((byte)Mathf.Clamp(Mathf.RoundToInt(m.Hp * 255f), 0, 255));
                        w.Write(m.Weapon);
                    }
                }
            });
        }

        private static List<GroupData> ReadSnap(BinaryReader r, out byte slot)
        {
            slot = r.ReadByte();
            int n = r.ReadByte();
            if (n > MaxGroups) throw new InvalidDataException("zu viele Trupps");
            var list = new List<GroupData>(n);
            for (int i = 0; i < n; i++)
            {
                var g = new GroupData { Id = r.ReadString(), Anchor = ReadVector(r) };
                if (r.ReadBoolean())
                {
                    g.VehicleId = r.ReadString();
                    g.VehicleType = r.ReadInt32();
                    g.VehiclePos = ReadVector(r);
                    g.VehicleYaw = ByteToYaw(r.ReadByte());
                }
                int m = r.ReadByte();
                if (m > MaxMembers) throw new InvalidDataException("zu viele Leute");
                for (int j = 0; j < m; j++)
                {
                    g.Members.Add(new Member
                    {
                        CharId = r.ReadString(),
                        Position = ReadVector(r),
                        Yaw = ByteToYaw(r.ReadByte()),
                        Speed = r.ReadByte() / 100f,
                        Hp = r.ReadByte() / 255f,
                        Weapon = r.ReadInt32(),
                    });
                }
                list.Add(g);
            }
            return list;
        }

        private static byte[] BuildRoster(byte slot, List<string> ids)
        {
            return VsNet.Build(Msg.VsUnitRoster, w =>
            {
                w.Write(slot);
                w.Write((byte)Mathf.Min(ids.Count, MaxGroups));
                for (int i = 0; i < ids.Count && i < MaxGroups; i++) w.Write(ids[i]);
            });
        }

        // ================================================================ Empfangen

        /// <summary>Host: Meldung eines Gastes. Platz kommt vom Absender, weiter an alle anderen, selbst anzeigen.</summary>
        public void HostOnSnap(VsPeer peer, BinaryReader r)
        {
            var groups = ReadSnap(r, out _);
            Send(BuildSnap(peer.Slot, groups), peer.Slot);
            Apply(peer.Slot, groups);
        }

        public void HostOnRoster(VsPeer peer, BinaryReader r)
        {
            var ids = ReadRoster(r, out _);
            Send(BuildRoster(peer.Slot, ids), peer.Slot);
            ApplyRoster(peer.Slot, ids);
        }

        public void GuestOnSnap(BinaryReader r)
        {
            var groups = ReadSnap(r, out var slot);
            if (slot != _vs.MySlot)
                Apply(slot, groups);
        }

        public void GuestOnRoster(BinaryReader r)
        {
            var ids = ReadRoster(r, out var slot);
            if (slot != _vs.MySlot)
                ApplyRoster(slot, ids);
        }

        private static List<string> ReadRoster(BinaryReader r, out byte slot)
        {
            slot = r.ReadByte();
            int n = r.ReadByte();
            var ids = new List<string>(n);
            for (int i = 0; i < n; i++) ids.Add(r.ReadString());
            return ids;
        }

        private static string Key(byte slot, string id) => slot + ":" + id;

        private void Apply(byte slot, List<GroupData> groups)
        {
            SnapsReceived++;
            if (!Running)
                return;
            float now = Time.realtimeSinceStartup;
            foreach (var g in groups)
            {
                if (g.Members.Count == 0)
                    continue;
                string key = Key(slot, g.Id);
                _proxies.TryGetValue(key, out var p);
                // Mehr Leute als der Stellvertreter hat (aufgefüllt): neu bauen
                if (p != null && (p.Group == null || p.Group.IsEmpty() || AliveCount(p.Group) < g.Members.Count))
                {
                    Destroy(p);
                    p = null;
                }
                if (p == null)
                {
                    p = Create(slot, g);
                    if (p == null)
                        continue;
                }
                p.LastHeard = now;
                p.Anchor = g.Anchor;
                p.Targets.Clear();
                p.Targets.AddRange(g.Members);
                int alive = AliveCount(p.Group);
                if (alive > g.Members.Count)
                    KillExtra(p.Group, alive - g.Members.Count);
                ApplyWeapons(p);
                ApplyVehicle(p, g);
            }
        }

        private void ApplyRoster(byte slot, List<string> ids)
        {
            var keep = new HashSet<string>();
            foreach (var id in ids) keep.Add(Key(slot, id));
            foreach (var p in new List<Proxy>(_proxies.Values))
                if (p.Slot == slot && !keep.Contains(Key(p.Slot, p.OwnerId)))
                    Destroy(p);
        }

        private Proxy Create(byte slot, GroupData g)
        {
            var builder = GameAccess.Game<GroupBuilder>();
            var draft = ProxyDraft();
            if (builder == null || draft == null)
            {
                if (!_warnedDraft) { _warnedDraft = true; Plugin.Log.LogWarning("IFZVS fremde Trupps: keine Vorlage (builder=" + (builder != null) + ")"); }
                return null;
            }
            Applying = true;
            try
            {
                var group = builder.Clear().SpawnGroupAt(g.Members[0].Position, draft, g.Members.Count);
                if (group == null)
                    return null;
                var player = _vs.Roster.BySlot(slot);
                var p = new Proxy
                {
                    Slot = slot,
                    OwnerId = g.Id,
                    Group = group,
                    Anchor = g.Anchor,
                    Color = player != null ? (Color)player.Color : Color.white,
                };
                _proxies[Key(slot, g.Id)] = p;
                _byGroup[group] = p;
                // Einwanderer-Vorlage: nie „Einwanderer gesichtet → aufnehmen?“ (sonst ließe sich der fremde Trupp aufnehmen)
                try
                {
                    group.ImmigrantsTransmissionPlayed = true;
                    group.ImmigrantsState = Gameplay.Immigrants.ImmigrantsState.Decline;
                }
                catch { }
                SetAffiliation(p, _vs.Diplomacy.IsAlliedWithMe(slot) ? Affiliation.Neutral : Affiliation.Hostile);
                try { group.SetGroupName(player != null ? player.Name : "Spieler " + (slot + 1)); } catch { }
                var chars = group.Characters;
                for (int i = 0; i < chars.Count && i < g.Members.Count; i++)
                    try { chars[i].Movement?.Teleport(g.Members[i].Position); } catch { }
                group.transform.position = g.Anchor;
                ProxiesBuilt++;
                Plugin.Log.LogInfo($"IFZVS fremder Trupp {g.Id} von Platz {slot} ({g.Members.Count} Leute) als {group.Id}");
                return p;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("IFZVS fremden Trupp bauen: " + e.Message);
                return null;
            }
            finally
            {
                Applying = false;
            }
        }

        private bool _warnedDraft;
        private static GroupDraft _proxyDraft;

        /// <summary>Vorlage für Stellvertreter: Überlebende, sonst Einwanderer, Händler, Armee (je nach Spieldaten).</summary>
        private static GroupDraft ProxyDraft()
        {
            if (_proxyDraft != null)
                return _proxyDraft;
            var cfg = JutsuGamesConfig.Runtime.Configs.GetConfig<GroupsConfig>();
            if (cfg == null)
                return null;
            foreach (var f in new[] { Fraction.Survivors, Fraction.Immigrants, Fraction.Vendor, Fraction.Army, Fraction.Bandits })
            {
                var d = cfg.GetDraftByFraction(f);
                if (d != null)
                {
                    _proxyDraft = d;
                    Plugin.Log.LogInfo("IFZVS Vorlage für fremde Trupps: " + d.name + " (" + f + ")");
                    return d;
                }
            }
            return null;
        }

        private static void SetAffiliation(Proxy p, Affiliation aff)
        {
            if (p.Group == null) return;
            Applying = true;
            try
            {
                p.Group.ChangeAffiliation(Fraction.Player, aff);
                p.Aff = aff;
            }
            catch (Exception e) { Plugin.Log.LogDebug("IFZVS Zugehörigkeit: " + e.Message); }
            finally { Applying = false; }
        }

        private void Destroy(Proxy p)
        {
            _proxies.Remove(Key(p.Slot, p.OwnerId));
            RemoveVehicle(p);
            if (p.Group != null)
            {
                _byGroup.Remove(p.Group);
                Applying = true;
                try { p.Group.DestroySilent(); }
                catch (Exception e) { Plugin.Log.LogDebug("IFZVS fremden Trupp entfernen: " + e.Message); }
                finally { Applying = false; }
            }
            ProxiesRemoved++;
        }

        private static void KillExtra(Group g, int count)
        {
            var chars = g.Characters;
            Applying = true;
            try
            {
                for (int i = chars.Count - 1; i >= 0 && count > 0; i--)
                {
                    if (!IsAlive(chars[i])) continue;
                    try { chars[i].Kill(SubtractionHpReason.Enemy); } catch { }
                    count--;
                }
            }
            finally { Applying = false; }
        }

        // ================================================================ Waffen

        private static void ApplyWeapons(Proxy p)
        {
            var chars = p.Group.Characters;
            int ti = 0;
            for (int i = 0; i < chars.Count && ti < p.Targets.Count; i++)
            {
                var c = chars[i];
                if (!IsAlive(c)) continue;
                int want = p.Targets[ti++].Weapon;
                if (want == 0 || (p.Weapons.TryGetValue(c, out var have) && have == want))
                    continue;
                p.Weapons[c] = want;
                try
                {
                    var id = (ResourceID)want;
                    if (c.ItemsContainer?.WeaponHandler != null && c.ItemsContainer.WeaponHandler.TryAssignWeapon(id, dropPreviousWeapon: false))
                        c.CharacterTextureRandomizer?.SetWeaponAppearance(id);
                }
                catch (Exception e) { Plugin.Log.LogDebug("IFZVS Waffe zeigen: " + e.Message); }
            }
        }

        // ================================================================ Fahrzeuge

        private void ApplyVehicle(Proxy p, GroupData g)
        {
            if (g.VehicleId == null)
            {
                if (p.Vehicle != null) RemoveVehicle(p);
                return;
            }
            p.VehiclePos = g.VehiclePos;
            p.VehicleYaw = g.VehicleYaw;
            if (p.Vehicle != null && p.VehicleOwnerId == g.VehicleId)
                return;
            RemoveVehicle(p);
            var vc = GameAccess.Game<VehiclesController>();
            var ids = GameAccess.Game<Serialization.Data.IdProvider>();
            var factory = vc != null ? CheatWindow.VehicleFactory(vc) : null;
            if (vc == null || ids == null || factory == null)
                return;
            Applying = true;
            try
            {
                var v = factory.Create(ids.GetNextId(), (VehicleType)g.VehicleType, g.VehiclePos, Quaternion.Euler(0f, g.VehicleYaw, 0f));
                vc.AddVehicle(v);
                try { v.DiscoverVehicle(); } catch { }
                try { v.SetState(Vehicle.VehicleStates.Recovered); } catch { }
                _proxyVehicles.Add(v);
                p.Vehicle = v;
                p.VehicleOwnerId = g.VehicleId;
                try { p.Group.VehicleHandler.AssignVehicle(v); }
                catch (Exception e) { Plugin.Log.LogDebug("IFZVS Trupp ins fremde Auto: " + e.Message); }
                Plugin.Log.LogInfo($"IFZVS fremdes Auto {g.VehicleId} ({(VehicleType)g.VehicleType}) für Trupp {p.OwnerId}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("IFZVS fremdes Auto bauen: " + e.Message);
            }
            finally
            {
                Applying = false;
            }
        }

        private void RemoveVehicle(Proxy p)
        {
            var v = p.Vehicle;
            if (v == null)
                return;
            p.Vehicle = null;
            p.VehicleOwnerId = null;
            Applying = true;
            try
            {
                try { if (p.Group != null && p.Group.VehicleHandler != null && p.Group.VehicleHandler.HaveVehicle()) p.Group.VehicleHandler.AbandonVehicle(v); } catch { }
                _proxyVehicles.Remove(v);
                if (v != null && v.gameObject != null) UnityEngine.Object.Destroy(v.gameObject);
            }
            catch (Exception e) { Plugin.Log.LogDebug("IFZVS fremdes Auto entfernen: " + e.Message); }
            finally { Applying = false; }
        }

        // ================================================================ jedes Bild (statt Group.CustomUpdate)

        /// <summary>Prefix Group.CustomUpdate: false = Stellvertreter, die KI läuft nicht.</summary>
        public static bool GroupUpdatePrefix(Group __instance)
        {
            var inst = Instance;
            if (inst == null || !inst._byGroup.TryGetValue(__instance, out var p))
                return true;
            try { inst.Tick(p); }
            catch (Exception e) { Plugin.Log.LogDebug("IFZVS Stellvertreter-Takt: " + e.Message); }
            return false;
        }

        public static bool GroupFixedPrefix(Group __instance) => !IsProxy(__instance);

        /// <summary>Prefix Character.SubtractHp: Stellvertreter verlieren selbst nie Leben (der Besitzer rechnet).</summary>
        public static bool DamagePrefix(Character __instance)
        {
            if (Applying || __instance == null || Instance == null || Instance._byGroup.Count == 0)
                return true;
            return !IsProxy(__instance.Group);
        }

        /// <summary>
        /// Prefix Character.ReceiveDamage: Treffer an einem Stellvertreter → an den Besitzer schicken (nur Treffer der eigenen
        /// Trupps und Türme), selbst nichts abziehen.
        /// </summary>
        public static bool ReceiveDamagePrefix(Character __instance, Weapon attackedWith, float dmg, IDamageApplier damageApplier, ref bool __result)
        {
            var inst = Instance;
            if (Applying || inst == null || __instance == null || inst._byGroup.Count == 0)
                return true;
            if (!inst._byGroup.TryGetValue(__instance.Group, out var p))
                return true;
            __result = false;
            try
            {
                var attacker = Sync.StatsSync.AttackerGroup(damageApplier);
                bool mine = damageApplier == null || (attacker != null && attacker.Fraction == Fraction.Player && !IsProxy(attacker));
                if (!mine || dmg <= 0f || p.Aff != Affiliation.Hostile)
                    return false;
                int idx = p.Order.IndexOf(__instance);
                if (idx < 0 || idx >= p.Targets.Count || string.IsNullOrEmpty(p.Targets[idx].CharId))
                    return false;
                int weapon = attackedWith != null ? (int)attackedWith.ResourceType : 0;
                inst.SendHit(p.Slot, p.OwnerId, p.Targets[idx].CharId, dmg, weapon);
                __result = true;
            }
            catch (Exception e) { Plugin.Log.LogDebug("IFZVS Treffer melden: " + e.Message); }
            return false;
        }

        /// <summary>Prefix Vehicle.SubtractHp: fremde Autos verlieren selbst kein Leben.</summary>
        public static bool VehicleDamagePrefix(Vehicle __instance) => Applying || !IsProxyVehicle(__instance);

        private void Tick(Proxy p)
        {
            var group = p.Group;
            if (group == null || group.IsEmpty())
                return;
            float k = 1f - Mathf.Exp(-12f * Time.deltaTime);
            var chars = group.Characters;
            p.Order.Clear();
            int ti = 0;
            bool inVehicle = p.Vehicle != null;
            for (int i = 0; i < chars.Count; i++)
            {
                var c = chars[i];
                if (!IsAlive(c))
                    continue;
                if (ti >= p.Targets.Count)
                    break;
                p.Order.Add(c);
                var t = p.Targets[ti++];
                var h = c.Health;
                if (h != null && h.MaxHp > 0f)
                {
                    float hp = Mathf.Max(1f, t.Hp * h.MaxHp);
                    if (Mathf.Abs(h.Hp - hp) > h.MaxHp * 0.02f) { Applying = true; try { h.SetHp(hp); } finally { Applying = false; } }
                }
                if (inVehicle)
                    continue; // sitzt im Auto
                var mv = c.Movement;
                if (mv == null)
                    continue;
                if ((mv.Position - t.Position).sqrMagnitude > TeleportDistance * TeleportDistance)
                    mv.Teleport(t.Position);
                else
                    mv.SetPosition(Vector3.Lerp(mv.Position, t.Position, k), updateOnExpedition: false);
                var tr = mv.transform;
                tr.rotation = Quaternion.Slerp(tr.rotation, Quaternion.Euler(0f, t.Yaw, 0f), k);
                try { c.AnimationController?.SetSpeed(t.Speed); } catch { }
            }
            if (inVehicle)
            {
                var vm = p.Vehicle.Movement;
                if (vm != null)
                {
                    if ((vm.Position - p.VehiclePos).sqrMagnitude > TeleportDistance * TeleportDistance)
                        vm.Teleport(p.VehiclePos);
                    else
                        vm.SetPosition(Vector3.Lerp(vm.Position, p.VehiclePos, k), updateOnExpedition: false);
                    var vt = vm.transform;
                    vt.rotation = Quaternion.Slerp(vt.rotation, Quaternion.Euler(0f, p.VehicleYaw, 0f), k);
                }
            }
            var gt = group.transform;
            var anchor = inVehicle ? p.VehiclePos : p.Anchor;
            if ((gt.position - anchor).sqrMagnitude > TeleportDistance * TeleportDistance)
                gt.position = anchor;
            else
                gt.position = Vector3.Lerp(gt.position, anchor, k);
            group.CachePosition();

            // Freund oder Feind: verbündet = neutral, sonst feindlich (eigene Trupps greifen von selbst an)
            bool allied = _vs.Diplomacy.IsAlliedWithMe(p.Slot);
            var want = allied ? Affiliation.Neutral : Affiliation.Hostile;
            if (p.Aff != want)
                SetAffiliation(p, want);

            // Sicht: Verbündete immer, sonst nur in der Sichtweite der eigenen Leute (Nebel des Spiels)
            var fog = group.FogOfWarDetectable;
            if (allied)
            {
                fog?.SetObserversCount(1);
                if (!group.IsVisible()) group.Show();
            }
            else
            {
                fog?.CustomUpdate();
            }
            var pointer = group.Pointer;
            if (pointer != null)
                try { pointer.SetColor(p.Color); } catch { }
        }

        // ================================================================ Treffer

        private void SendHit(byte targetSlot, string groupId, string charId, float dmg, int weapon)
        {
            HitsSent++;
            DamageSent += dmg;
            var msg = VsNet.Build(Msg.VsHit, w =>
            {
                w.Write(targetSlot);
                w.Write(groupId);
                w.Write(charId);
                w.Write(dmg);
                w.Write(weapon);
            });
            if (_vs.Role == VsRole.Host)
            {
                var peer = _vs.PeerBySlot(targetSlot);
                if (peer != null) _vs.SendTo(peer.Id, msg);
            }
            else
            {
                _vs.SendToHost(msg);
            }
        }

        /// <summary>Host: Treffer eines Gastes weiterleiten (an den Besitzer) oder selbst abziehen (Besitzer = Host).</summary>
        public void HostOnHit(VsPeer from, BinaryReader r)
        {
            byte target = r.ReadByte();
            string groupId = r.ReadString();
            string charId = r.ReadString();
            float dmg = r.ReadSingle();
            int weapon = r.ReadInt32();
            if (target == _vs.MySlot)
            {
                ApplyHit(groupId, charId, dmg, weapon);
                return;
            }
            var peer = _vs.PeerBySlot(target);
            if (peer == null || peer.Slot == from.Slot)
                return;
            _vs.SendTo(peer.Id, VsNet.Build(Msg.VsHit, w =>
            {
                w.Write(target);
                w.Write(groupId);
                w.Write(charId);
                w.Write(dmg);
                w.Write(weapon);
            }));
        }

        public void GuestOnHit(BinaryReader r)
        {
            byte target = r.ReadByte();
            string groupId = r.ReadString();
            string charId = r.ReadString();
            float dmg = r.ReadSingle();
            int weapon = r.ReadInt32();
            if (target == _vs.MySlot)
                ApplyHit(groupId, charId, dmg, weapon);
        }

        /// <summary>Besitzer: Treffer bei der echten Figur abziehen (wie ein normaler Treffer, mit Schutzkleidung).</summary>
        private void ApplyHit(string groupId, string charId, float dmg, int weapon)
        {
            if (!Running || dmg <= 0f || dmg > 100000f)
                return;
            var squads = GameAccess.Game<SquadsController>()?.Squads;
            if (squads == null)
                return;
            foreach (var g in squads)
            {
                if (g == null || g.Id != groupId)
                    continue;
                foreach (var c in g.Characters)
                {
                    if (c == null || c.Id != charId || !IsAlive(c))
                        continue;
                    Weapon w = null;
                    try { if (weapon != 0) w = ResourcesDataContainer.Instance.GetWeaponById((ResourceID)weapon); } catch { }
                    try
                    {
                        c.ReceiveDamage(w, dmg, SubtractionHpReason.Enemy, null);
                        HitsApplied++;
                        DamageTaken += dmg;
                    }
                    catch (Exception e) { Plugin.Log.LogDebug("IFZVS Treffer abziehen: " + e.Message); }
                    return;
                }
            }
        }

        // ================================================================ Hilfen

        private static int AliveCount(Group g)
        {
            int n = 0;
            if (g == null) return 0;
            var chars = g.Characters;
            for (int i = 0; i < chars.Count; i++) if (IsAlive(chars[i])) n++;
            return n;
        }

        private static bool IsAlive(Character c)
        {
            if (c == null) return false;
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

        public string Compact()
        {
            int visible = 0, vehicles = 0, hostile = 0, armed = 0;
            var sb = new StringBuilder();
            foreach (var p in _proxies.Values)
            {
                bool vis = p.Group != null && p.Group.IsVisible();
                if (vis) visible++;
                if (p.Vehicle != null) vehicles++;
                if (p.Aff == Affiliation.Hostile) hostile++;
                foreach (var kv in p.Weapons) if (kv.Value != 0 && kv.Value != (int)ResourceID.eq_basic_melee_weapon) armed++;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(p.Slot).Append(':').Append(p.OwnerId).Append("->").Append(p.Group != null ? p.Group.Id : "-")
                  .Append(vis ? "(sichtbar" : "(versteckt").Append(p.Aff == Affiliation.Hostile ? ",feind" : ",neutral")
                  .Append(p.Vehicle != null ? ",auto)" : ")");
            }
            return $"proxies={_proxies.Count} visible={visible} hostile={hostile} vehicles={vehicles} armed={armed} sent={SnapsSent} recv={SnapsReceived} built={ProxiesBuilt} removed={ProxiesRemoved} hitsSent={HitsSent} hitsApplied={HitsApplied} dmgSent={DamageSent:0} dmgTaken={DamageTaken:0} list={(sb.Length == 0 ? "-" : sb.ToString())}";
        }
    }
}
