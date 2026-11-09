using System;
using System.Collections.Generic;
using System.IO;
using Gameplay.Units;
using Gameplay.Units.Characters;
using Gameplay.Units.Enemy.Hideouts;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Aufgedeckte Verstecke. Ein Versteck deckt sich auf, wenn ein Soldat oder Arbeiter das Gebäude betritt
    /// (Hideout.Reveal). Beim Gast betreten die Marionetten nichts, also bliebe dort die Hauptgruppe des Verstecks
    /// unsichtbar (Group.CanShow fragt Hideout.IsRevealed), auch wenn der Host sie sieht.
    /// Der Host meldet jede Sekunde neu aufgedeckte Verstecke, nach dem Laden eines Gastes alle.
    /// </summary>
    internal sealed class HideoutSync
    {
        private const float SendInterval = 1f;

        private static readonly Action<Hideout> RevealMethod =
            AccessTools.MethodDelegate<Action<Hideout>>(AccessTools.Method(typeof(Hideout), "Reveal"));

        private readonly Session _session;
        private readonly HashSet<string> _sent = new HashSet<string>();
        private float _nextSend;

        public int Applied { get; private set; }

        public HideoutSync(Session session)
        {
            _session = session;
        }

        public void ResendAll() { _sent.Clear(); _countSig = null; }

        public void Reset() { _sent.Clear(); _countSig = null; _pending.Clear(); }

        // ================================================================ Anzahl in der Höhle
        // Die Hauptgruppe einer Höhle wächst beim Host (Hideout.TryAddMainGroupMember); beim Gast ist HideoutsController.CustomUpdate
        // gesperrt, und unsichtbare Gruppen bekommen nur ihren Ankerpunkt. Darum stand beim Gast z. B. 39 statt 47 Infizierte
        // (Bericht 2026-11-09). Der Host meldet die Zahl, der Gast füllt auf oder nimmt weg.

        private const float CountForce = 10f;
        private string _countSig;
        private float _nextCountForce;
        private readonly Dictionary<string, int> _pending = new Dictionary<string, int>();
        public int MembersAdded { get; private set; }
        public int MembersRemoved { get; private set; }

        private static int AliveCount(Group g)
        {
            int n = 0;
            var list = g.Characters;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c != null && (c.Health == null || !c.Health.IsDead())) n++;
            }
            return n;
        }

        private void HostSendCounts(IReadOnlyCollection<Hideout> hideouts, float now)
        {
            var items = new List<(string id, string group, int count)>();
            var sb = new System.Text.StringBuilder();
            foreach (var h in hideouts)
            {
                var g = h?.MainGroup;
                if (g == null || string.IsNullOrEmpty(h.Id) || string.IsNullOrEmpty(g.Id))
                    continue;
                int n = AliveCount(g);
                items.Add((h.Id, g.Id, n));
                sb.Append(h.Id).Append(':').Append(n).Append(',');
            }
            string sig = sb.ToString();
            if (sig == _countSig && now < _nextCountForce)
                return;
            _countSig = sig;
            _nextCountForce = now + CountForce;
            _session.BroadcastToGuests(Protocol.Build(Msg.HideoutCount, w =>
            {
                w.Write((ushort)items.Count);
                foreach (var it in items)
                {
                    w.Write(it.id);
                    w.Write(it.group);
                    w.Write((ushort)Mathf.Clamp(it.count, 0, ushort.MaxValue));
                }
            }));
        }

        public void GuestOnCount(BinaryReader r)
        {
            int n = r.ReadUInt16();
            var host = new Dictionary<string, (string group, int count)>(n);
            for (int i = 0; i < n; i++)
            {
                string id = r.ReadString();
                string group = r.ReadString();
                host[id] = (group, r.ReadUInt16());
            }
            var hc = GameAccess.Game<HideoutsController>();
            var hideouts = hc?.Hideouts;
            if (hideouts == null)
                return;
            foreach (var h in hideouts)
            {
                if (h == null || string.IsNullOrEmpty(h.Id) || !host.TryGetValue(h.Id, out var want))
                    continue;
                var g = h.MainGroup;
                if (g == null || g.IsEmpty() || g.Id != want.group)
                {
                    _pending.Remove(h.Id);
                    continue;
                }
                int diff = want.count - AliveCount(g);
                if (diff == 0 || want.count == 0) // leer geräumt: das regelt das Ausräumen der Höhle selbst
                {
                    _pending.Remove(h.Id);
                    continue;
                }
                // erst beim zweiten gleichen Unterschied: Trupps, die gerade aus der Höhle ausrücken, kommen als eigene Gruppe
                if (!_pending.TryGetValue(h.Id, out var lastDiff) || lastDiff != diff)
                {
                    _pending[h.Id] = diff;
                    continue;
                }
                _pending.Remove(h.Id);
                UnitSync.NetApplying = true;
                try
                {
                    if (diff > 0) AddMembers(hc, h, g, Mathf.Min(diff, 100));
                    else RemoveMembers(g, Mathf.Min(-diff, 100));
                    Plugin.Log.LogInfo($"IFZCOOP Höhle {h.Id}: {want.count - diff} -> {want.count}");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Höhle {h.Id} angleichen: {e.Message}");
                }
                finally
                {
                    UnitSync.NetApplying = false;
                }
            }
        }

        private void AddMembers(HideoutsController hc, Hideout h, Group g, int count)
        {
            for (int i = 0; i < count; i++)
            {
                // wie Hideout.TryAddMainGroupMember
                var info = new Controllers.CharacterLogic.CharacterInfo { Name = "Enemy" };
                Character m = hc.CreateHideoutMember(g.Draft.CharacterDraft, info);
                if (m == null)
                    return;
                m.AssignGroup(g, silent: true);
                m.Movement.Teleport(h.Building);
                if (h.Draft.AssignMainGroupToVirtualFight)
                {
                    m.Movement.StopMoving();
                    m.Movement.Block();
                }
                m.DisableObject();
                MembersAdded++;
            }
        }

        private void RemoveMembers(Group g, int count)
        {
            var list = g.Characters;
            for (int i = list.Count - 1; i >= 0 && count > 0; i--)
            {
                var c = list[i];
                if (c == null || (c.Health != null && c.Health.IsDead()))
                    continue;
                c.Kill(SubtractionHpReason.None);
                MembersRemoved++;
                count--;
                list = g.Characters;
                if (i > list.Count) i = list.Count;
            }
        }

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend || _session.ActiveGuestCount == 0)
                return;
            _nextSend = now + SendInterval;
            var hideouts = GameAccess.Game<HideoutsController>()?.Hideouts;
            if (hideouts == null)
                return;
            HostSendCounts(hideouts, now);
            var revealed = new List<string>();
            foreach (var h in hideouts)
                if (h != null && h.IsRevealed && !string.IsNullOrEmpty(h.Id) && _sent.Add(h.Id))
                    revealed.Add(h.Id);
            if (revealed.Count == 0)
                return;
            _session.BroadcastToGuests(Protocol.Build(Msg.HideoutRevealed, w =>
            {
                w.Write(revealed.Count);
                foreach (var id in revealed) w.Write(id);
            }));
        }

        public void GuestOnRevealed(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 10000)
                throw new InvalidDataException("zu viele Verstecke");
            var ids = new HashSet<string>();
            for (int i = 0; i < n; i++) ids.Add(r.ReadString());
            var hideouts = GameAccess.Game<HideoutsController>()?.Hideouts;
            if (hideouts == null || RevealMethod == null)
                return;
            foreach (var h in hideouts)
            {
                if (h == null || h.IsRevealed || !ids.Contains(h.Id))
                    continue;
                UnitSync.NetApplying = true;
                try
                {
                    RevealMethod(h);
                    Applied++;
                    Plugin.Log.LogInfo("IFZCOOP Versteck aufgedeckt: " + h.Id);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Versteck {h.Id} aufdecken: {e.Message}");
                }
                finally
                {
                    UnitSync.NetApplying = false;
                }
            }
        }
    }
}
