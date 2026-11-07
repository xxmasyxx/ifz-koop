using System;
using System.Collections.Generic;
using System.IO;
using Controllers;
using Gameplay.Buildings;
using IfzCoop.Game;
using IfzCoop.Net;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Durchsuchte (geplünderte) Häuser. Beim Gast durchsuchen die Marionetten-Trupps nichts, also bliebe dort
    /// überall das Fragezeichen (Ansicht mit V). Der Host schickt jede Sekunde, welche Häuser wie weit durchsucht
    /// sind (Building.ScavengeWork: Gesamtzeit und Restzeit), nur Änderungen. Nach dem Laden eines Gastes alles neu.
    ///
    /// Der Gast legt dafür eine ScavengeWork ohne eigene Funde an und setzt die Restzeit. Was gefunden wurde
    /// (Rohstoffe im Haus), gleicht das noch nicht ab: das gehört zu den Rohstoffen (DESIGN Nr. 10).
    /// </summary>
    internal sealed class ScavengeSync
    {
        private const float SendInterval = 1f;
        private const int MaxPerMessage = 400;

        private readonly Session _session;
        private readonly Dictionary<long, float> _sent = new Dictionary<long, float>();
        private float _nextSend;

        public int Applied { get; private set; }

        public ScavengeSync(Session session)
        {
            _session = session;
        }

        /// <summary>Host: beim nächsten Senden alle durchsuchten Häuser schicken (ein Gast hat neu geladen).</summary>
        public void ResendAll() => _sent.Clear();

        public void Reset() => _sent.Clear();

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend || _session.ActiveGuestCount == 0)
                return;
            _nextSend = now + SendInterval;
            var buildings = GameAccess.Game<BuildingsController>()?.Buildings;
            if (buildings == null)
                return;

            var changed = new List<Building>();
            for (int i = 0; i < buildings.Count; i++)
            {
                var b = buildings[i];
                var work = b != null ? b.ScavengeWork : null;
                if (work == null)
                    continue;
                float left = work.IsDone ? 0f : work.TimeLeftToScavenge;
                if (_sent.TryGetValue(b.ID, out var last) && Mathf.Abs(last - left) < 0.01f)
                    continue;
                _sent[b.ID] = left;
                changed.Add(b);
            }

            for (int start = 0; start < changed.Count; start += MaxPerMessage)
            {
                int count = Math.Min(MaxPerMessage, changed.Count - start);
                int first = start;
                _session.BroadcastToGuests(Protocol.Build(Msg.ScavengeState, w =>
                {
                    w.Write(count);
                    for (int i = first; i < first + count; i++)
                    {
                        var work = changed[i].ScavengeWork;
                        w.Write(changed[i].ID);
                        w.Write(work.TimeToScavenge);
                        w.Write(work.IsDone ? 0f : work.TimeLeftToScavenge);
                    }
                }));
            }
        }

        public void GuestOnState(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > MaxPerMessage)
                throw new InvalidDataException("zu viele Häuser");
            var controller = GameAccess.Game<BuildingsController>();
            for (int i = 0; i < n; i++)
            {
                long id = r.ReadInt64();
                float total = r.ReadSingle();
                float left = r.ReadSingle();
                var b = controller?.GetBuildingById(id);
                if (b == null)
                    continue;
                try
                {
                    Apply(b, total, left);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Haus {id} durchsucht setzen: {e.Message}");
                }
            }
        }

        private void Apply(Building b, float total, float left)
        {
            if (b.ScavengeWork == null)
                b.InitializeScavengeWork(null);
            var work = b.ScavengeWork;
            if (work.IsDone || Mathf.Abs(work.TimeLeftToScavenge - left) < 0.01f)
                return;
            if (Mathf.Abs(work.TimeToScavenge - total) > 0.01f)
                work.SetSpaceToScavenge(total);
            if (left <= 0f)
                work.EndImminently(clearResources: true); // Funde des Gastes verwerfen, die zählt der Host
            else
                work.SetLeftSpaceToScavenge(left);
            Applied++;
        }

        /// <summary>Für die Statusdatei: durchsuchte Häuser und angefangene.</summary>
        public static void Count(out int done, out int started)
        {
            done = 0;
            started = 0;
            var buildings = GameAccess.Game<BuildingsController>()?.Buildings;
            if (buildings == null)
                return;
            for (int i = 0; i < buildings.Count; i++)
            {
                var work = buildings[i] != null ? buildings[i].ScavengeWork : null;
                if (work == null)
                    continue;
                if (work.IsDone) done++;
                else started++;
            }
        }
    }
}
