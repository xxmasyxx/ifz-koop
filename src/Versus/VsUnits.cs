using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Controllers;
using Gameplay.FogOfWar;
using Gameplay.Units;
using Gameplay.Units.Characters;
using Gameplay.Units.Enemy;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using UnityEngine;

namespace IfzCoop.Versus
{
    /// <summary>
    /// Fremde Trupps sehen (KONZEPT-VERSUS Schritt 2, erste Stufe).
    ///
    /// - Jeder Spieler schickt 5× pro Sekunde, wo seine Trupps und deren Leute stehen (VsUnitSnap), alle 2 s die Liste seiner
    ///   Trupps (VsUnitRoster). Gäste schicken an den Host, der Host verteilt an alle anderen (Sternform).
    /// - Beim Empfänger entsteht je fremdem Trupp ein Stellvertreter: eine Gruppe aus der Vorlage der Überlebenden
    ///   (GroupBuilder.SpawnGroupAt), für den Spieler neutral, Schild in Spielerfarbe. Er hat keine eigene KI
    ///   (Prefix Group.CustomUpdate → Tick), läuft nur den gemeldeten Positionen nach und nimmt keinen Schaden
    ///   (Prefix Character.SubtractHp). Tote beim Besitzer verschwinden auch beim Empfänger.
    /// - Sichtbar wie andere fremde Gruppen nur in der Sichtweite der eigenen Leute (Nebel des Spiels, FogOfWarDetectable);
    ///   Verbündete immer.
    /// - Noch nicht: Fahrzeuge als eigene Stellvertreter, Waffen-Aussehen, Kampf zwischen Spielern.
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

        /// <summary>true, solange ein Stellvertreter gebaut, verändert oder entfernt wird (Schadens-Sperre lässt dann durch).</summary>
        public static bool Applying;

        private static readonly AccessTools.FieldRef<Group, FogOfWarObserver> ObserverField =
            AccessTools.FieldRefAccess<Group, FogOfWarObserver>("_fogOfWarObserver");

        private sealed class Member
        {
            public Vector3 Position;
            public float Yaw;
            public float Speed;
            public float Hp;
        }

        private sealed class GroupData
        {
            public string Id;
            public Vector3 Anchor;
            public readonly List<Member> Members = new List<Member>();
        }

        private sealed class Proxy
        {
            public byte Slot;
            public string OwnerId;
            public Group Group;
            public Vector3 Anchor;
            public readonly List<Member> Targets = new List<Member>();
            public float LastHeard;
            public Color Color;
        }

        private readonly VersusSession _vs;
        private readonly Dictionary<string, Proxy> _proxies = new Dictionary<string, Proxy>();
        private readonly Dictionary<Group, Proxy> _byGroup = new Dictionary<Group, Proxy>();
        private float _nextSend, _nextRoster, _nextCleanup;

        public int SnapsSent { get; private set; }
        public int SnapsReceived { get; private set; }
        public int ProxiesBuilt { get; private set; }
        public int ProxiesRemoved { get; private set; }

        public VsUnits(VersusSession vs)
        {
            _vs = vs;
            Instance = this;
        }

        public static bool IsProxy(Group g) => g != null && Instance != null && Instance._byGroup.ContainsKey(g);

        public void Reset()
        {
            foreach (var p in new List<Proxy>(_proxies.Values))
                Destroy(p);
            _proxies.Clear();
            _byGroup.Clear();
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
                d.Members.Add(new Member
                {
                    Position = c.Position,
                    Yaw = c.Movement.transform.eulerAngles.y,
                    Speed = c.Movement.NormalizedSpeed,
                    Hp = h == null || h.MaxHp <= 0f ? 1f : h.Hp / h.MaxHp,
                });
            }
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
                    w.Write((byte)g.Members.Count);
                    foreach (var m in g.Members)
                    {
                        WriteVector(w, m.Position);
                        w.Write((byte)Mathf.RoundToInt(Mathf.Repeat(m.Yaw, 360f) / 360f * 255f));
                        w.Write((byte)Mathf.Clamp(Mathf.RoundToInt(m.Speed * 100f), 0, 255));
                        w.Write((byte)Mathf.Clamp(Mathf.RoundToInt(m.Hp * 255f), 0, 255));
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
                int m = r.ReadByte();
                if (m > MaxMembers) throw new InvalidDataException("zu viele Leute");
                for (int j = 0; j < m; j++)
                {
                    g.Members.Add(new Member
                    {
                        Position = ReadVector(r),
                        Yaw = r.ReadByte() / 255f * 360f,
                        Speed = r.ReadByte() / 100f,
                        Hp = r.ReadByte() / 255f,
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
                // Weniger Leute: die überzähligen sterben
                int alive = AliveCount(p.Group);
                if (alive > g.Members.Count)
                    KillExtra(p.Group, alive - g.Members.Count);
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
                try { group.ChangeAffiliation(Fraction.Player, Affiliation.Neutral); } catch (Exception e) { Plugin.Log.LogDebug("IFZVS Stellvertreter neutral: " + e.Message); }
                try { group.SetGroupName(player != null ? player.Name : "Spieler " + (slot + 1)); } catch { }
                // sofort an die richtigen Stellen
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

        private void Destroy(Proxy p)
        {
            _proxies.Remove(Key(p.Slot, p.OwnerId));
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

        /// <summary>Prefix Character.SubtractHp: Stellvertreter nehmen keinen Schaden (der Besitzer rechnet).</summary>
        public static bool DamagePrefix(Character __instance)
        {
            if (Applying || __instance == null || Instance == null || Instance._byGroup.Count == 0)
                return true;
            return !IsProxy(__instance.Group);
        }

        private void Tick(Proxy p)
        {
            var group = p.Group;
            if (group == null || group.IsEmpty())
                return;
            float k = 1f - Mathf.Exp(-12f * Time.deltaTime);
            var chars = group.Characters;
            int ti = 0;
            for (int i = 0; i < chars.Count; i++)
            {
                var c = chars[i];
                if (!IsAlive(c))
                    continue;
                if (ti >= p.Targets.Count)
                    break;
                var t = p.Targets[ti++];
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
                var h = c.Health;
                if (h != null && h.MaxHp > 0f)
                {
                    float hp = Mathf.Max(1f, t.Hp * h.MaxHp);
                    if (Mathf.Abs(h.Hp - hp) > h.MaxHp * 0.02f) h.SetHp(hp);
                }
            }
            var gt = group.transform;
            if ((gt.position - p.Anchor).sqrMagnitude > TeleportDistance * TeleportDistance)
                gt.position = p.Anchor;
            else
                gt.position = Vector3.Lerp(gt.position, p.Anchor, k);
            group.CachePosition();

            // Sicht: Verbündete immer, sonst nur in der Sichtweite der eigenen Leute (Nebel des Spiels)
            var fog = group.FogOfWarDetectable;
            if (_vs.Diplomacy.IsAlliedWithMe(p.Slot))
            {
                fog?.SetObserversCount(1);
                if (!group.IsVisible()) group.Show();
            }
            else
            {
                fog?.CustomUpdate();
            }
            // Schild in Spielerfarbe
            var pointer = group.Pointer;
            if (pointer != null)
                try { pointer.SetColor(p.Color); } catch { }
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

        private static void WriteVector(BinaryWriter w, Vector3 v)
        {
            w.Write(v.x);
            w.Write(v.y);
            w.Write(v.z);
        }

        private static Vector3 ReadVector(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        public string Compact()
        {
            int visible = 0;
            var sb = new StringBuilder();
            foreach (var p in _proxies.Values)
            {
                bool vis = p.Group != null && p.Group.IsVisible();
                if (vis) visible++;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(p.Slot).Append(':').Append(p.OwnerId).Append("->").Append(p.Group != null ? p.Group.Id : "-")
                  .Append(vis ? "(sichtbar)" : "(versteckt)");
            }
            return $"proxies={_proxies.Count} visible={visible} sent={SnapsSent} recv={SnapsReceived} built={ProxiesBuilt} removed={ProxiesRemoved} list={(sb.Length == 0 ? "-" : sb.ToString())}";
        }
    }
}
