using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gameplay.Rebuilding;
using Gameplay.Rebuilding.Gates;
using Gameplay.Rebuilding.Towers;
using Gameplay.Units.Equipment;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using UI.InfoPanels.NestedUIElements;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Waffe der Türme und Tore („Waffe auswählen“ im Fenster: Bogen, Pistole, Sturmgewehr …). Lief bisher gar nicht mit
    /// (Bericht 2026-11-08: „die Türme werden nicht synchronisiert“): jeder hatte seine eigene Wahl, der Gast sah die
    /// des Hosts nicht und seine Klicks wirkten nur bei ihm.
    /// Host: 1×/s Liste (Anlagen-ID, Nummer der Waffe; 0 = Standardwaffe, n = WeaponsUsable[n-1]), bei Änderung und alle 10 s.
    /// Gast: stellt sie mit StructureDefenceModule.SetupWeapon nach (alle 2 s erneut). Klick beim Gast
    /// (SelectWeaponPanel.OnWeaponItemClicked) geht als Wunsch an den Host, mit „alle gleichen“ (Umschalt).
    /// </summary>
    internal sealed class TowerWeaponSync
    {
        private const float SendInterval = 1f;
        private const float ForceInterval = 10f;

        private static TowerWeaponSync _instance;
        private static bool _applying;

        private readonly Session _session;
        private float _nextSend, _nextForce, _nextReapply;
        private string _sentSig;
        private Dictionary<string, int> _last;

        public int Sent { get; private set; }
        public int Applied { get; private set; }
        public int Requests { get; private set; }

        private static readonly AccessTools.FieldRef<SelectWeaponPanel, IDefensible> DefensableF =
            AccessTools.FieldRefAccess<SelectWeaponPanel, IDefensible>("_defensable");

        public TowerWeaponSync(Session session)
        {
            _session = session;
            _instance = this;
        }

        public void ResendAll()
        {
            _sentSig = null;
            _nextSend = 0f;
        }

        public void Reset()
        {
            ResendAll();
            _last = null;
        }

        private static IEnumerable<(string id, PlaceableStructure s, IDefensible d)> Defensibles()
        {
            foreach (var s in StructureSync.All())
            {
                if (s == null || s.IsGameObjectDestroyed || !(s is IDefensible d) || d.DefenceModule == null)
                    continue;
                yield return (s.GetId(), s, d);
            }
        }

        private static Weapon WeaponAt(Structure s, int idx)
        {
            var dd = s?.Draft?.DefenceData;
            if (dd == null || idx < 0)
                return null;
            if (idx == 0)
                return dd.DefaultWeapon;
            var arr = dd.WeaponsUsable;
            return arr != null && idx - 1 < arr.Length ? arr[idx - 1] : null;
        }

        private static int IndexOf(Structure s, Weapon w)
        {
            var dd = s?.Draft?.DefenceData;
            if (dd == null || w == null)
                return -1;
            if (w == dd.DefaultWeapon)
                return 0;
            var arr = dd.WeaponsUsable;
            if (arr != null)
                for (int i = 0; i < arr.Length; i++)
                    if (arr[i] == w) return i + 1;
            return -1;
        }

        private static bool SetWeapon(IDefensible d, Weapon w)
        {
            if (w == null || d.DefenceModule.SelectedWeapon == w)
                return false;
            _applying = true;
            try { d.DefenceModule.SetupWeapon(w); }
            finally { _applying = false; }
            return true;
        }

        // ================================================================ Host

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend || _session.ActiveGuestCount == 0 || !GameAccess.IsGameStarted)
                return;
            _nextSend = now + SendInterval;
            var list = new List<(string id, int idx)>();
            var sb = new StringBuilder();
            foreach (var (id, s, d) in Defensibles())
            {
                int idx = IndexOf(s, d.DefenceModule.SelectedWeapon);
                if (idx < 0)
                    continue;
                list.Add((id, idx));
                sb.Append(id).Append('=').Append(idx).Append('|');
            }
            string sig = sb.ToString();
            bool force = now >= _nextForce;
            if (!force && sig == _sentSig)
                return;
            if (force) _nextForce = now + ForceInterval;
            _sentSig = sig;
            _session.BroadcastToGuests(Protocol.Build(Msg.TowerWeapon, w =>
            {
                w.Write((ushort)list.Count);
                foreach (var e in list)
                {
                    w.Write(e.id);
                    w.Write((byte)e.idx);
                }
            }));
            Sent++;
        }

        /// <summary>Host: Wunsch eines Gastes (string Anlage, byte Waffe, bool alle gleichen).</summary>
        public void HostOnRequest(Peer from, BinaryReader r)
        {
            string id = r.ReadString();
            int idx = r.ReadByte();
            bool all = r.ReadBoolean();
            Requests++;
            (string id, PlaceableStructure s, IDefensible d) target = default;
            var each = new List<(string id, PlaceableStructure s, IDefensible d)>(Defensibles());
            foreach (var e in each)
                if (e.id == id) { target = e; break; }
            if (target.s == null)
            {
                Plugin.Log.LogInfo($"IFZCOOP Turm-Waffe: Anlage {id} fehlt beim Host (von {from.Name})");
                return;
            }
            var weapon = WeaponAt(target.s, idx);
            if (weapon == null)
                return;
            int n = SetWeapon(target.d, weapon) ? 1 : 0;
            if (all)
                foreach (var e in each)
                {
                    if (ReferenceEquals(e.s, target.s) || e.s.GetType() != target.s.GetType() || e.s.Draft?.Id != target.s.Draft?.Id)
                        continue;
                    if (SetWeapon(e.d, weapon)) n++;
                }
            Plugin.Log.LogInfo($"IFZCOOP Turm-Waffe {id} → {idx}{(all ? " (alle gleichen)" : "")}: {n} geändert (von {from.Name})");
            _nextSend = 0f;
        }

        // ================================================================ Gast

        public void GuestOnState(BinaryReader r)
        {
            int n = r.ReadUInt16();
            var d = new Dictionary<string, int>(n);
            for (int i = 0; i < n; i++)
            {
                string id = r.ReadString();
                d[id] = r.ReadByte();
            }
            _last = d;
            Apply();
        }

        public void GuestFrame()
        {
            if (_last == null || !_session.GuestControlsLocked)
                return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextReapply)
                return;
            _nextReapply = now + 2f;
            Apply();
        }

        private void Apply()
        {
            if (_last == null)
                return;
            try
            {
                foreach (var (id, s, d) in Defensibles())
                    if (_last.TryGetValue(id, out int idx) && SetWeapon(d, WeaponAt(s, idx)))
                        Applied++;
            }
            catch (Exception e) { Plugin.Log.LogDebug("Turm-Waffe anwenden: " + e.Message); }
        }

        // ================================================================ Klick beim Gast

        /// <summary>Prefix SelectWeaponPanel.OnWeaponItemClicked: beim Gast als Wunsch an den Host (lokal läuft es weiter).</summary>
        private static void WeaponClickedPrefix(SelectWeaponPanel __instance, WeaponItem selectedItem, bool allStructures)
        {
            try
            {
                var inst = _instance;
                if (inst == null || !inst._session.GuestControlsLocked || selectedItem == null)
                    return;
                var d = DefensableF(__instance);
                if (!(d is Structure s))
                    return;
                int idx = IndexOf(s, selectedItem.WeaponData);
                if (idx < 0)
                    return;
                string id = s.GetId();
                inst._session.SendToHost(Protocol.Build(Msg.TowerWeapon, w =>
                {
                    w.Write(id);
                    w.Write((byte)idx);
                    w.Write(allStructures);
                }));
                inst.Requests++;
                // Bis der Host bestätigt: die eigene Wahl gilt beim nächsten Nachstellen als Stand (kein Zurückspringen)
                if (inst._last != null)
                {
                    inst._last[id] = idx;
                    if (allStructures)
                        foreach (var (oid, os, _) in Defensibles())
                            if (os.GetType() == s.GetType() && os.Draft?.Id == s.Draft?.Id) inst._last[oid] = idx;
                }
            }
            catch (Exception e) { Plugin.Log.LogDebug("Turm-Waffe Wunsch: " + e.Message); }
        }

        public static void ApplyPatches(Harmony harmony)
        {
            try
            {
                var m = AccessTools.DeclaredMethod(typeof(SelectWeaponPanel), "OnWeaponItemClicked");
                if (m == null) { Plugin.Log.LogWarning("IFZCOOP Turm-Waffe: Haken fehlt"); return; }
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(TowerWeaponSync), nameof(WeaponClickedPrefix)));
                Plugin.Log.LogInfo("Turm-Waffen-Abgleich: Haken gesetzt");
            }
            catch (Exception e) { Plugin.Log.LogWarning("IFZCOOP Turm-Waffe Haken: " + e.Message); }
        }

        public string Compact() => $"towerWeapon sent={Sent} applied={Applied} requests={Requests} last={(_last == null ? "-" : _last.Count.ToString())}";
    }
}
