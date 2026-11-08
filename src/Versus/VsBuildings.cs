using System;
using System.Collections.Generic;
using System.IO;
using Controllers;
using Data.Resorces;
using Gameplay.Buildings;
using Gameplay.GameResources;
using Gameplay.Rebuilding;
using Gameplay.Units;
using Gameplay.Units.Equipment;
using Gameplay.Units.Interfaces;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using UnityEngine;

namespace IfzCoop.Versus
{
    /// <summary>
    /// Fremde Gebäude angreifen (Versus).
    ///
    /// - Jeder schickt alle 3 s die Liste seiner Gebäude (HQ und in Besitz genommene): OSM-ID, HQ ja/nein, Leben 0..1
    ///   (VsBuildings, Msg 115). Die IDs sind in allen Welten gleich (gleiche Karte). Host verteilt weiter.
    /// - Beim Empfänger wird das gleiche Gebäude seiner Welt dadurch zum fremden Gebäude: nicht verbündet → mit
    ///   Rechtsklick angreifbar wie ein Banditen-Versteck (Spiel-Auftrag AttackBuildingOrder, Postfix CanAttackBuilding).
    /// - Treffer (Schüsse und Explosionen der eigenen Trupps) zieht der Empfänger NICHT selbst ab, er schickt sie an den
    ///   Besitzer (VsBuildingHit, Msg 116). Der zieht sie am echten Gebäude ab, Leute drin können getroffen werden wie
    ///   im Spiel. Das Leben kommt mit der nächsten Liste zurück; ist es 0, fällt das Gebäude auch beim Angreifer.
    /// </summary>
    internal sealed class VsBuildings
    {
        private const float SendInterval = 3f;
        private const int MaxEntries = 400;

        public static VsBuildings Instance { get; private set; }

        private struct Foreign
        {
            public byte Slot;
            public bool Hq;
            public float Hp;
        }

        private readonly VersusSession _vs;
        private readonly Dictionary<long, Foreign> _foreign = new Dictionary<long, Foreign>();
        private readonly Dictionary<byte, HashSet<long>> _bySlot = new Dictionary<byte, HashSet<long>>();
        private float _nextSend, _nextNotice;
        private static bool _applying;

        public int HitsSent { get; private set; }
        public int HitsTaken { get; private set; }
        public float DamageSent { get; private set; }
        public float DamageTaken { get; private set; }
        public int OwnCount { get; private set; }

        public VsBuildings(VersusSession vs)
        {
            _vs = vs;
            Instance = this;
        }

        public void Reset()
        {
            _foreign.Clear();
            _bySlot.Clear();
            _nextSend = 0f;
        }

        private bool Running => _vs.Phase == VsPhase.Running && GameAccess.IsGameStarted && _vs.Start.MyHqConfirmed;

        // ================================================================ Senden

        public void Update(float now)
        {
            if (!Running || now < _nextSend)
                return;
            _nextSend = now + SendInterval;
            var buildings = GameAccess.Game<BuildingsController>()?.Buildings;
            if (buildings == null)
                return;
            var list = new List<(long id, bool hq, float hp)>();
            for (int i = 0; i < buildings.Count && list.Count < MaxEntries; i++)
            {
                var b = buildings[i];
                if (b == null || !(b.IsHq || b.IsClaimedByPlayer))
                    continue;
                var h = b.Health;
                float hp = h == null || h.MaxHp <= 0f ? 1f : Mathf.Clamp01(h.Hp / h.MaxHp);
                if (h != null && h.IsDead()) hp = 0f;
                list.Add((b.ID, b.IsHq, hp));
            }
            OwnCount = list.Count;
            Send(Build(_vs.MySlot, list), null);
        }

        private static byte[] Build(byte slot, List<(long id, bool hq, float hp)> list)
        {
            return VsNet.Build(Msg.VsBuildings, w =>
            {
                w.Write(slot);
                w.Write((ushort)list.Count);
                foreach (var e in list)
                {
                    w.Write(e.id);
                    w.Write(e.hq);
                    w.Write((byte)Mathf.Clamp(Mathf.CeilToInt(e.hp * 255f), 0, 255));
                }
            });
        }

        private static List<(long id, bool hq, float hp)> Read(BinaryReader r, out byte slot)
        {
            slot = r.ReadByte();
            int n = r.ReadUInt16();
            if (n > MaxEntries) throw new InvalidDataException("zu viele Gebäude");
            var list = new List<(long, bool, float)>(n);
            for (int i = 0; i < n; i++)
                list.Add((r.ReadInt64(), r.ReadBoolean(), r.ReadByte() / 255f));
            return list;
        }

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

        // ================================================================ Empfangen

        public void HostOnList(VsPeer peer, BinaryReader r)
        {
            var list = Read(r, out _);
            Send(Build(peer.Slot, list), peer.Slot);
            Apply(peer.Slot, list);
        }

        public void GuestOnList(BinaryReader r)
        {
            var list = Read(r, out var slot);
            if (slot != _vs.MySlot)
                Apply(slot, list);
        }

        private void Apply(byte slot, List<(long id, bool hq, float hp)> list)
        {
            if (!_bySlot.TryGetValue(slot, out var set))
                _bySlot[slot] = set = new HashSet<long>();
            var fresh = new HashSet<long>();
            foreach (var e in list) fresh.Add(e.id);
            foreach (var id in set)
                if (!fresh.Contains(id)) _foreign.Remove(id);
            set.Clear();
            var controller = GameAccess.Game<BuildingsController>();
            foreach (var e in list)
            {
                set.Add(e.id);
                _foreign[e.id] = new Foreign { Slot = slot, Hq = e.hq, Hp = e.hp };
                if (controller != null && Running)
                    ShowHp(controller, e.id, e.hp);
            }
        }

        /// <summary>Leben des Gebäudes in der eigenen Welt wie beim Besitzer; 0 = auch hier zerstört.</summary>
        private static void ShowHp(BuildingsController controller, long id, float hp)
        {
            try
            {
                var b = controller.GetBuildingById(id);
                var h = b != null ? b.Health : null;
                if (h == null || h.MaxHp <= 0f || h.IsDead() || b.IsHq || b.IsClaimedByPlayer)
                    return;
                if (hp <= 0f)
                {
                    _applying = true;
                    try { h.SubtractHp(h.Hp + 1f, SubtractionHpReason.Enemy); }
                    finally { _applying = false; }
                    return;
                }
                float want = Mathf.Max(1f, hp * h.MaxHp);
                if (Mathf.Abs(h.Hp - want) > h.MaxHp * 0.02f)
                    h.SetHp(want);
            }
            catch (Exception e) { Plugin.Log.LogDebug("IFZVS fremdes Gebäude: " + e.Message); }
        }

        // ================================================================ Abfragen und Sperren

        /// <summary>Gehört das Gebäude (in meiner Welt) einem anderen Spieler? slot = Besitzer.</summary>
        public bool IsForeign(Building b, out byte slot)
        {
            slot = 0;
            if (b == null || !_foreign.TryGetValue(b.ID, out var f))
                return false;
            // dasselbe Gebäude kann in meiner Welt auch mir gehören (jede Welt ist eigen): dann ist es meins
            if (b.IsHq || b.IsClaimedByPlayer)
                return false;
            slot = f.Slot;
            return f.Slot != _vs.MySlot;
        }

        public static bool IsAttackableForeign(Building b)
        {
            var inst = Instance;
            if (inst == null || inst._foreign.Count == 0 || !inst.Running)
                return false;
            return inst.IsForeign(b, out var slot) && !inst._vs.Diplomacy.IsAlliedWithMe(slot);
        }

        /// <summary>Postfix BuildingExtensions.CanAttackBuilding: fremde Gebäude (nicht verbündet) lassen sich angreifen.</summary>
        public static void CanAttackPostfix(Building building, ref bool __result)
        {
            if (!__result && IsAttackableForeign(building))
                __result = true;
        }

        private static bool MineAttacker(IDamageApplier applier)
        {
            var g = Sync.StatsSync.AttackerGroup(applier);
            if (g == null)
                return applier == null;
            return g.Fraction == Fraction.Player && !VsUnits.IsProxy(g) && VsUnits.AttacksPlayers(g);
        }

        /// <summary>
        /// Prefix Structure.ReceiveDamage: fremdes Gebäude verliert in meiner Welt selbst kein Leben; Treffer meiner Trupps
        /// gehen an den Besitzer.
        /// </summary>
        public static bool ReceiveDamagePrefix(Structure __instance, Weapon attackedWith, float dmg, IDamageApplier damageApplier, ref bool __result)
        {
            var inst = Instance;
            if (_applying || inst == null || inst._foreign.Count == 0 || !(__instance is Building b) || !inst.IsForeign(b, out var slot))
                return true;
            __result = false;
            try
            {
                if (dmg <= 0f || inst._vs.Diplomacy.IsAlliedWithMe(slot) || !MineAttacker(damageApplier))
                    return false;
                inst.SendHit(slot, b.ID, dmg, attackedWith != null ? (int)attackedWith.ResourceType : 0);
                __result = true;
            }
            catch (Exception e) { Plugin.Log.LogDebug("IFZVS Treffer am Gebäude: " + e.Message); }
            return false;
        }

        /// <summary>Prefix Building.ApplyAreaDamage: Explosion am fremden Gebäude → Schaden ausrechnen wie das Spiel, an den Besitzer.</summary>
        public static bool AreaDamagePrefix(Building __instance, ref float totalDamage, Vector3 damageOrigin, float radius, IAreaDamageApplier areaDamageApplier)
        {
            var inst = Instance;
            if (_applying || inst == null || inst._foreign.Count == 0 || !inst.IsForeign(__instance, out var slot))
                return true;
            try
            {
                if (inst._vs.Diplomacy.IsAlliedWithMe(slot) || !MineAttacker(areaDamageApplier))
                    return false;
                var origin = new Vector2(damageOrigin.x, damageOrigin.z);
                float distance = float.MaxValue;
                foreach (var polygon in __instance.RawBuilding.GetOutlinePolygons())
                {
                    if (polygon.Contains(origin, includeHoles: true)) { distance = 0f; break; }
                    distance = Mathf.Min(distance, polygon.DistanceToPolygon(origin, includeHoles: true));
                }
                var cfg = Traverse.Create(__instance).Field("AreaDamageConfig").GetValue();
                if (cfg == null)
                    return false;
                float dmg = Traverse.Create(cfg).Method("GetDamage", radius, distance).GetValue<float>();
                var h = __instance.Health;
                if (h != null) dmg = Mathf.Min(dmg, h.Hp);
                if (dmg <= 0f)
                    return false;
                totalDamage -= dmg;
                int weapon = 0;
                try { var w = areaDamageApplier?.Weapon; if (w != null) weapon = (int)w.ResourceType; } catch { }
                inst.SendHit(slot, __instance.ID, dmg, weapon);
            }
            catch (Exception e) { Plugin.Log.LogDebug("IFZVS Explosion am Gebäude: " + e.Message); }
            return false;
        }

        // ================================================================ Treffer

        private void SendHit(byte owner, long id, float dmg, int weapon)
        {
            HitsSent++;
            DamageSent += dmg;
            var msg = BuildHit(owner, id, dmg, weapon);
            if (_vs.Role == VsRole.Host)
            {
                var peer = _vs.PeerBySlot(owner);
                if (peer != null) _vs.SendTo(peer.Id, msg);
            }
            else
            {
                _vs.SendToHost(msg);
            }
        }

        private static byte[] BuildHit(byte owner, long id, float dmg, int weapon)
        {
            return VsNet.Build(Msg.VsBuildingHit, w =>
            {
                w.Write(owner);
                w.Write(id);
                w.Write(dmg);
                w.Write(weapon);
            });
        }

        public void HostOnHit(VsPeer from, BinaryReader r)
        {
            byte owner = r.ReadByte();
            long id = r.ReadInt64();
            float dmg = r.ReadSingle();
            int weapon = r.ReadInt32();
            if (owner == _vs.MySlot)
            {
                ApplyHit(id, dmg, weapon);
                return;
            }
            var peer = _vs.PeerBySlot(owner);
            if (peer != null && peer.Slot != from.Slot)
                _vs.SendTo(peer.Id, BuildHit(owner, id, dmg, weapon));
        }

        public void GuestOnHit(BinaryReader r)
        {
            byte owner = r.ReadByte();
            long id = r.ReadInt64();
            float dmg = r.ReadSingle();
            int weapon = r.ReadInt32();
            if (owner == _vs.MySlot)
                ApplyHit(id, dmg, weapon);
        }

        /// <summary>Besitzer: Treffer am echten Gebäude (Leute drin können ihn abfangen, wie im Spiel).</summary>
        private void ApplyHit(long id, float dmg, int weapon)
        {
            if (!Running || dmg <= 0f || dmg > 100000f)
                return;
            try
            {
                var b = GameAccess.Game<BuildingsController>()?.GetBuildingById(id);
                if (b == null || !(b.IsHq || b.IsClaimedByPlayer) || b.IsDead())
                    return;
                Weapon w = null;
                try { if (weapon != 0) w = ResourcesDataContainer.Instance.GetWeaponById((ResourceID)weapon); } catch { }
                _applying = true;
                try
                {
                    if (!(b.IsProtected(w, out var protector) && protector != null && protector.ReceiveDamage(w, dmg, SubtractionHpReason.Enemy, null)))
                        b.ReceiveDamage(w, dmg, SubtractionHpReason.Enemy, null);
                }
                finally { _applying = false; }
                HitsTaken++;
                DamageTaken += dmg;
                float now = Time.realtimeSinceStartup;
                if (now >= _nextNotice)
                {
                    _nextNotice = now + 30f;
                    _vs.Notice(b.IsHq ? "Versus: Dein HQ wird angegriffen!" : "Versus: Ein Gebäude von dir wird angegriffen!", 6f);
                }
            }
            catch (Exception e) { Plugin.Log.LogDebug("IFZVS Treffer am eigenen Gebäude: " + e.Message); }
        }

        public string Compact()
        {
            int hq = 0, attackable = 0;
            foreach (var kv in _foreign)
            {
                if (kv.Value.Hq) hq++;
                if (kv.Value.Slot != _vs.MySlot && !_vs.Diplomacy.IsAlliedWithMe(kv.Value.Slot)) attackable++;
            }
            return $"own={OwnCount} foreign={_foreign.Count} foreignHq={hq} attackable={attackable} hitsSent={HitsSent} dmgSent={DamageSent:0} hitsTaken={HitsTaken} dmgTaken={DamageTaken:0}";
        }

        /// <summary>QA: OSM-ID eines fremden HQ (für Angriffs-Tests), sonst 0.</summary>
        public long ForeignHqId()
        {
            foreach (var kv in _foreign)
                if (kv.Value.Hq && kv.Value.Slot != _vs.MySlot) return kv.Key;
            return 0;
        }
    }
}
