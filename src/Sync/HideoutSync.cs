using System;
using System.Collections.Generic;
using System.IO;
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

        public void ResendAll() => _sent.Clear();

        public void Reset() => _sent.Clear();

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend || _session.ActiveGuestCount == 0)
                return;
            _nextSend = now + SendInterval;
            var hideouts = GameAccess.Game<HideoutsController>()?.Hideouts;
            if (hideouts == null)
                return;
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
