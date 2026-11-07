using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Controllers;
using Gameplay.Buildings;
using Gameplay.GameResources;
using Gameplay.Rebuilding;
using Gameplay.Units.Workers;
using Gameplay.Units.Workers.WorkSystem.Works;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Abbau (Dekonstruktion) und Rückbau (Deadaptation) beim Gast anzeigen. Bisher stand im Fenster beim Gast immer
    /// „Fortschritt 0.0 %“ und „Gesammelte Ressourcen 0/84“ (Fehlerbericht 2026-11-06): beim Gast arbeitet niemand
    /// (Marionetten), also zählt dort weder die Zeit noch werden Kisten abgeholt.
    ///
    /// Host: 1×/s alle laufenden DisassembleWork an Häusern und Anlagen: Fortschritt, Zeit, Start- und Restmenge.
    ///       Nur geänderte (Fortschritt auf 0,1 %, Mengen auf 1 gerundet), alle 10 s alle.
    /// Gast: setzt die Werte in die eigene Arbeit (SetProgress, Profit, StartingProfit) und meldet OnProfitChanged,
    ///       damit offene Fenster (DemolishWorkPanel, DeadaptWorkPanel) neu zeichnen.
    /// </summary>
    internal sealed class DemolishSync
    {
        private const float SendInterval = 1f;
        private const float ForceInterval = 10f;

        private static readonly AccessTools.FieldRef<Work, Dictionary<ResourceID, float>> ProfitRef =
            AccessTools.FieldRefAccess<Work, Dictionary<ResourceID, float>>("Profit");
        private static readonly AccessTools.FieldRef<DisassembleWork, Dictionary<ResourceID, float>> StartRef =
            AccessTools.FieldRefAccess<DisassembleWork, Dictionary<ResourceID, float>>("_startingProfit");

        private readonly Session _session;
        private readonly Dictionary<string, string> _sent = new Dictionary<string, string>();
        private float _nextSend;
        private float _nextForce;

        public int Sent { get; private set; }
        public int Applied { get; private set; }
        public int Missing { get; private set; }

        private sealed class Entry
        {
            public string Key;
            public bool Deadapt;
            public float Progress;
            public float Time;
            public List<KeyValuePair<int, float>> Start = new List<KeyValuePair<int, float>>();
            public List<KeyValuePair<int, float>> Left = new List<KeyValuePair<int, float>>();

            public string Sig()
            {
                var sb = new StringBuilder();
                sb.Append(Deadapt ? 'D' : 'A').Append(Mathf.RoundToInt(Progress * 1000f)).Append('|');
                foreach (var kv in Start) sb.Append(kv.Key).Append(':').Append(Mathf.RoundToInt(kv.Value)).Append(',');
                sb.Append('|');
                foreach (var kv in Left) sb.Append(kv.Key).Append(':').Append(Mathf.RoundToInt(kv.Value)).Append(',');
                return sb.ToString();
            }
        }

        public DemolishSync(Session session)
        {
            _session = session;
        }

        public void ResendAll()
        {
            _sent.Clear();
            _nextSend = 0f;
        }

        public void Reset() => ResendAll();

        // ================================================================ gemeinsam

        private static IEnumerable<KeyValuePair<string, DisassembleWork>> Works()
        {
            var bc = GameAccess.Game<BuildingsController>();
            if (bc?.Buildings != null)
                foreach (var b in bc.Buildings)
                {
                    if (b == null) continue;
                    string key = "b" + b.ID.ToString(CultureInfo.InvariantCulture);
                    if (b.DeconstructWork != null && !b.DeconstructWork.IsDone)
                        yield return new KeyValuePair<string, DisassembleWork>(key + "A", b.DeconstructWork);
                    if (b.DeadaptationWork != null && !b.DeadaptationWork.IsDone)
                        yield return new KeyValuePair<string, DisassembleWork>(key + "D", b.DeadaptationWork);
                }
            foreach (var s in StructureSync.All())
            {
                if (s == null || s.DeconstructWork == null || s.DeconstructWork.IsDone) continue;
                string id = s.GetId();
                if (!string.IsNullOrEmpty(id))
                    yield return new KeyValuePair<string, DisassembleWork>("s" + id + "A", s.DeconstructWork);
            }
        }

        private static List<KeyValuePair<int, float>> ToList(IReadOnlyDictionary<ResourceID, float> d)
        {
            var list = new List<KeyValuePair<int, float>>();
            if (d != null)
                foreach (var kv in d.OrderBy(k => (int)k.Key))
                    list.Add(new KeyValuePair<int, float>((int)kv.Key, kv.Value));
            return list;
        }

        // ================================================================ Host

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend || _session.ActiveGuestCount == 0 || !GameAccess.IsGameStarted)
                return;
            _nextSend = now + SendInterval;
            if (now >= _nextForce)
            {
                _nextForce = now + ForceInterval;
                _sent.Clear();
            }

            var changed = new List<Entry>();
            try
            {
                foreach (var kv in Works())
                {
                    var w = kv.Value;
                    var e = new Entry
                    {
                        Key = kv.Key,
                        Deadapt = w is DeadaptationWork,
                        Progress = w.Progress,
                        Time = w.TimeDeconstructing,
                        Start = ToList(w.StartingProfit),
                        Left = ToList(w.GetProfit()),
                    };
                    string sig = e.Sig();
                    if (_sent.TryGetValue(e.Key, out var last) && last == sig)
                        continue;
                    _sent[e.Key] = sig;
                    changed.Add(e);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug("Abbau lesen: " + ex.Message);
                return;
            }
            if (changed.Count == 0)
                return;
            _session.BroadcastToGuests(Protocol.Build(Msg.DemolishState, w =>
            {
                w.Write(changed.Count);
                foreach (var e in changed)
                {
                    w.Write(e.Key);
                    w.Write(e.Progress);
                    w.Write(e.Time);
                    w.Write(e.Start.Count);
                    foreach (var kv in e.Start) { w.Write(kv.Key); w.Write(kv.Value); }
                    w.Write(e.Left.Count);
                    foreach (var kv in e.Left) { w.Write(kv.Key); w.Write(kv.Value); }
                }
            }));
            Sent += changed.Count;
        }

        // ================================================================ Gast

        public void GuestOnState(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 5000)
                throw new InvalidDataException("zu viele Abbau-Arbeiten");
            var list = new List<Entry>(n);
            for (int i = 0; i < n; i++)
            {
                var e = new Entry { Key = r.ReadString(), Progress = r.ReadSingle(), Time = r.ReadSingle() };
                int ns = r.ReadInt32();
                if (ns < 0 || ns > 100) throw new InvalidDataException("zu viele Rohstoffe");
                for (int j = 0; j < ns; j++) e.Start.Add(new KeyValuePair<int, float>(r.ReadInt32(), r.ReadSingle()));
                int nl = r.ReadInt32();
                if (nl < 0 || nl > 100) throw new InvalidDataException("zu viele Rohstoffe");
                for (int j = 0; j < nl; j++) e.Left.Add(new KeyValuePair<int, float>(r.ReadInt32(), r.ReadSingle()));
                list.Add(e);
            }

            Dictionary<string, DisassembleWork> mine;
            try { mine = Works().GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.First().Value); }
            catch (Exception ex) { Plugin.Log.LogDebug("Abbau finden: " + ex.Message); return; }

            foreach (var e in list)
            {
                if (!mine.TryGetValue(e.Key, out var w))
                {
                    Missing++;
                    continue;
                }
                try
                {
                    Apply(w, e);
                    Applied++;
                }
                catch (Exception ex) { Plugin.Log.LogDebug($"Abbau {e.Key}: {ex.Message}"); }
            }
        }

        private static void Apply(DisassembleWork w, Entry e)
        {
            var start = StartRef(w);
            if (start != null)
            {
                start.Clear();
                foreach (var kv in e.Start) start[(ResourceID)kv.Key] = kv.Value;
            }
            var profit = ProfitRef(w);
            if (profit != null)
            {
                profit.Clear();
                foreach (var kv in e.Left)
                    if (kv.Value > 0f) profit[(ResourceID)kv.Key] = kv.Value;
            }
            w.SetTimeDeconstructing(e.Time);
            if (Mathf.Abs(w.Progress - e.Progress) > 0.0005f)
                w.SetProgress(e.Progress);
            // offene Fenster neu zeichnen (Work.OnProfitChanged)
            if (Traverse.Create(w).Field("OnProfitChanged").GetValue() is Action<Work> ev)
                ev(w);
        }

        public string StatusLine() => $"demolish sent={Sent} applied={Applied} missing={Missing}";

        public static IEnumerable<string> StatusLines()
        {
            List<KeyValuePair<string, DisassembleWork>> works;
            try { works = Works().ToList(); } catch { yield break; }
            foreach (var kv in works.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                float start = kv.Value.StartingProfit?.Values.Sum() ?? 0f;
                float left = kv.Value.GetProfit()?.Values.Sum() ?? 0f;
                yield return $"demo {kv.Key} p={(kv.Value.Progress * 100f).ToString("0.0", CultureInfo.InvariantCulture)} got={Mathf.RoundToInt(start - left)}/{Mathf.RoundToInt(start)}";
            }
        }
    }
}
