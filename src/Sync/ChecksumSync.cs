using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using IfzCoop.Game;
using IfzCoop.Net;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Abweichungsprüfung (Analyse W17; Paket L). Der Gast schickt alle 10 s je Bereich eine Prüfsumme (aus denselben Werten wie
    /// die Statusdatei, ohne Werte, die nur gerundet oder gleitend abgeglichen werden). Der Host rechnet dasselbe; weicht ein Bereich
    /// zweimal hintereinander ab, schickt er dessen Stand neu (ResendAll des Bausteins) und schreibt es ins Log.
    /// </summary>
    internal sealed class ChecksumSync
    {
        private const float Interval = 10f;

        private readonly Session _session;
        private readonly Dictionary<string, int> _strikes = new Dictionary<string, int>();
        private float _next;

        public int Sent { get; private set; }
        public int Mismatches { get; private set; }
        public int Repairs { get; private set; }
        public string LastMismatch { get; private set; } = "-";

        public ChecksumSync(Session session)
        {
            _session = session;
        }

        public void Reset() => _strikes.Clear();

        private static readonly Regex Volatile = new Regex(@"\b(pos|fuel|hp|ammo|t|sci|current|points|temp|time)=[^ ]*", RegexOptions.Compiled);

        private static string Clean(IEnumerable<string> lines) => string.Join("\n", lines.Select(l => Volatile.Replace(l ?? "", "")));

        private static uint Hash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s) h = (h ^ c) * 16777619;
            return h;
        }

        /// <summary>Bereich → Prüfsumme. Gleiche Rechnung bei Host und Gast.</summary>
        private static Dictionary<string, uint> Compute()
        {
            var d = new Dictionary<string, uint>();
            void Add(string name, Func<IEnumerable<string>> f)
            {
                try { d[name] = Hash(Clean(f())); }
                catch (Exception e) { Plugin.Log.LogDebug($"Prüfsumme {name}: {e.Message}"); }
            }
            Add("stock", () => new[] { StockSync.StatusLine() });
            Add("ground", () => new[] { GroundSync.StatusLine() });
            Add("containers", () => new[] { ContainerSync.BuildingsStatus() });
            Add("vehicles", VehicleSync.StatusLines);
            Add("events", EventSync.ChecksumLines);
            Add("world", () => WorldSync.StatusLines().Where(l => !l.StartsWith("mood=")));
            Add("buildings", () => new[] { BuildingSync.StatusLine() });
            Add("population", () => new[] { PopulationSync.StatusLine() });
            Add("work", () => new[] { WorkSync.GlobalLine() });
            Add("expedition", () => new[] { ExpeditionSync.ChecksumLine() });
            Add("trader", TraderSync.ChecksumLines);
            Add("area", () => AreaWorkSync.ChecksumLines());
            Add("structures", () => StructureSync.StatusLines().Where(l => l.StartsWith("structs ")));
            Add("virtual", () => VirtualSync.StatusLines().Where(l => l.StartsWith("virtual ")).Select(l => Regex.Replace(l, @" vis=\d", "")));
            return d;
        }

        // ================================================================ Gast

        public void GuestUpdate()
        {
            if (_session.Role != Role.Guest || !_session.GuestControlsLocked)
                return;
            float now = Time.realtimeSinceStartup;
            if (now < _next)
                return;
            _next = now + Interval;
            var d = Compute();
            _session.SendToHost(Protocol.Build(Msg.Checksum, w =>
            {
                w.Write(d.Count);
                foreach (var kv in d)
                {
                    w.Write(kv.Key);
                    w.Write(kv.Value);
                }
            }));
            Sent++;
        }

        // ================================================================ Host

        public void HostOnChecksum(Peer from, BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 100)
                return;
            var guest = new Dictionary<string, uint>();
            for (int i = 0; i < n; i++) guest[r.ReadString()] = r.ReadUInt32();
            var mine = Compute();
            foreach (var kv in guest)
            {
                if (!mine.TryGetValue(kv.Key, out var h))
                    continue;
                string key = from.Id + "/" + kv.Key;
                if (h == kv.Value)
                {
                    _strikes.Remove(key);
                    continue;
                }
                Mismatches++;
                _strikes.TryGetValue(key, out int s);
                _strikes[key] = ++s;
                LastMismatch = kv.Key;
                if (s < 2)
                    continue;
                _strikes.Remove(key);
                Repairs++;
                Plugin.Log.LogInfo($"IFZCOOP checksum Abweichung bei {from.Name}: {kv.Key} – Stand wird neu geschickt");
                Repair(kv.Key);
            }
        }

        private void Repair(string area)
        {
            switch (area)
            {
                case "stock": _session.Stock.ResendAll(); break;
                case "ground": _session.Ground.ResendAll(); break;
                case "containers": _session.Containers.ResendAll(); break;
                case "vehicles": _session.Vehicles.ResendAll(); break;
                case "events": _session.Events.ResendAll(); break;
                case "world": _session.World.ResendAll(); break;
                case "buildings": _session.Buildings.ResendAll(); break;
                case "population": _session.Population.ResendAll(); break;
                case "expedition": _session.Expeditions.ResendAll(); break;
                case "trader": _session.Traders.ResendAll(); break;
                case "area": _session.AreaWorks.ResendAll(); break;
                case "structures": _session.Structures.ResendAll(); break;
                case "virtual": _session.Virtuals.ResendAll(); break;
                case "work": _session.Works.ResendAll(); break;
            }
        }

        public string StatusLine() => $"checksum sent={Sent} mismatches={Mismatches} repairs={Repairs} last={LastMismatch}";
    }
}
