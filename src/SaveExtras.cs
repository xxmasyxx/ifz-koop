using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace IfzCoop
{
    /// <summary>
    /// Zusatzdatei im Spielstand-Ordner (ifzcoop-extra.json): Einstellungen des Entwickler-Menüs, die zum Spiel gehören
    /// (Sofort bauen/herstellen/töten, unsterbliche Trupps, Tempo-Regler, unzerstörbare Fahrzeuge/Gebäude) und die
    /// Statistik je Spieler. Geschrieben beim Speichern (Postfix SaveHandler.SaveGame, nach Ende des Speicherns),
    /// gelesen beim Laden (Prefix SaveHandler.PrepareToLoadMap merkt den Ordner, beim Spielstart wird eingelesen).
    /// Neues Spiel = keine Datei = alles aus. Nur Host/Einzelspiel.
    /// </summary>
    internal static class SaveExtras
    {
        private const string FileName = "ifzcoop-extra.json";

        private sealed class Data
        {
            public int Version = 1;
            public bool InstantBuild, InstantMake, OneHitAll, InstantDemolish, InstantCustomize, FreezeClock;
            public float ClockRate = 1f;
            public List<string> Immortal = new List<string>();
            public List<string> OneHit = new List<string>();
            public Dictionary<string, float> ScavBoost = new Dictionary<string, float>();
            public Dictionary<string, float> WalkBoost = new Dictionary<string, float>();
            public Dictionary<string, float> SpeedBoost = new Dictionary<string, float>();
            public List<string> IndestructibleVehicles = new List<string>();
            public List<long> IndestructibleBuildings = new List<long>();
            public Dictionary<string, float[]> Stats = new Dictionary<string, float[]>();
            public Dictionary<string, string> StatNames = new Dictionary<string, string>();
            public Dictionary<string, ulong> SquadLastBy = new Dictionary<string, ulong>(); // Punkte „zuletzt gesteuert“
        }

        private static string _pendingLoadDir;
        private static bool _wasStarted;
        private static float _loadAt;

        public static int Saved { get; private set; }
        public static int Loaded { get; private set; }

        public static void Patch(Harmony h)
        {
            try
            {
                var m = AccessTools.Method(typeof(SaveHandler), "PrepareToLoadMap");
                h.Patch(m, prefix: new HarmonyMethod(typeof(SaveExtras), nameof(PrepareToLoadPrefix)));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Spielstand-Zusatz (Laden) nicht gesetzt: " + e.Message); }
        }

        // SaveHandler.PrepareToLoadMap(string saveFolderPath, SaveInfo saveInfo)
        private static void PrepareToLoadPrefix(string saveFolderPath)
        {
            _pendingLoadDir = saveFolderPath;
        }

        /// <summary>Postfix SaveHandler.SaveGame (über UnitPatches): Stand jetzt einsammeln, nach dem Speichern schreiben.</summary>
        public static void OnSave(string directoryName, System.Threading.Tasks.Task<bool> task)
        {
            var s = Plugin.Instance?.Session;
            if (s == null || s.Role == Role.Guest || string.IsNullOrEmpty(directoryName) || directoryName.StartsWith("ifzcoop-"))
                return;
            string json;
            try { json = JsonConvert.SerializeObject(Collect(s), Formatting.Indented); }
            catch (Exception e) { Plugin.Log.LogWarning("Spielstand-Zusatz einsammeln: " + e.Message); return; }
            string dir = SaveHandler.GetSaveFolderPath(directoryName);
            void Write()
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, FileName), json);
                    Saved++;
                }
                catch (Exception e) { Plugin.Log.LogWarning("Spielstand-Zusatz schreiben: " + e.Message); }
            }
            if (task == null) Write();
            else task.ContinueWith(_ => Write());
        }

        private static Data Collect(Session s)
        {
            var d = new Data
            {
                InstantBuild = CheatWindow.InstantBuild,
                InstantMake = CheatWindow.InstantMake,
                OneHitAll = CheatWindow.OneHitAll,
                InstantDemolish = CheatWindow.InstantDemolish,
                InstantCustomize = CheatWindow.InstantCustomize,
                FreezeClock = CheatWindow.FreezeClock,
                ClockRate = CheatWindow.ClockRate,
                Immortal = CheatWindow.Immortal.ToList(),
                OneHit = CheatWindow.OneHit.ToList(),
                ScavBoost = new Dictionary<string, float>(CheatWindow.ScavBoost),
                WalkBoost = new Dictionary<string, float>(CheatWindow.WalkBoost),
                SpeedBoost = new Dictionary<string, float>(CheatWindow.SpeedBoost),
                IndestructibleVehicles = CheatWindow.Indestructible.ToList(),
                IndestructibleBuildings = CheatWindow.IndestructibleBuildings.Keys.ToList(),
                SquadLastBy = Sync.SquadViewSync.ExportLast(),
            };
            if (s.Stats != null)
            {
                d.Stats = s.Stats.ExportValues();
                d.StatNames = s.Stats.ExportNames();
            }
            return d;
        }

        /// <summary>Jedes Bild (Plugin.Update): nach dem Spielstart die Datei des geladenen Spielstands einlesen.</summary>
        public static void Update()
        {
            bool started = Game.GameAccess.IsGameStarted;
            if (!started)
            {
                _wasStarted = false;
                return;
            }
            if (!_wasStarted)
            {
                _wasStarted = true;
                _loadAt = Time.realtimeSinceStartup + 3f; // erst wenn Trupps, Fahrzeuge, Gebäude da sind
                Sync.SquadViewSync.ImportLast(null); // neues/anderes Spiel: alte Punkte weg (Spielstand füllt sie gleich wieder)
                return;
            }
            if (_pendingLoadDir == null || Time.realtimeSinceStartup < _loadAt)
                return;
            string dir = _pendingLoadDir;
            _pendingLoadDir = null;
            var s = Plugin.Instance?.Session;
            if (s == null || s.Role == Role.Guest)
                return;
            try { Apply(s, dir); }
            catch (Exception e) { Plugin.Log.LogWarning("Spielstand-Zusatz lesen: " + e.Message); }
        }

        private static void Apply(Session s, string dir)
        {
            string path = Path.Combine(dir, FileName);
            if (!File.Exists(path))
                return;
            var d = JsonConvert.DeserializeObject<Data>(File.ReadAllText(path));
            if (d == null)
                return;
            CheatWindow.InstantBuild = d.InstantBuild;
            CheatWindow.InstantMake = d.InstantMake;
            CheatWindow.OneHitAll = d.OneHitAll;
            CheatWindow.InstantDemolish = d.InstantDemolish;
            CheatWindow.InstantCustomize = d.InstantCustomize;
            CheatWindow.FreezeClock = d.FreezeClock;
            CheatWindow.SetClockRate(d.ClockRate);
            Fill(CheatWindow.Immortal, d.Immortal);
            Fill(CheatWindow.OneHit, d.OneHit);
            Fill(CheatWindow.Indestructible, d.IndestructibleVehicles);
            Fill(CheatWindow.ScavBoost, d.ScavBoost);
            Fill(CheatWindow.WalkBoost, d.WalkBoost);
            Fill(CheatWindow.SpeedBoost, d.SpeedBoost);
            CheatWindow.RebuildWalk();
            CheatWindow.IndestructibleBuildings.Clear();
            var bc = Game.GameAccess.Game<Controllers.BuildingsController>();
            if (bc?.Buildings != null && d.IndestructibleBuildings != null)
                foreach (var b in bc.Buildings)
                    if (b != null && b.Health != null && d.IndestructibleBuildings.Contains(b.ID))
                        CheatWindow.IndestructibleBuildings[b.ID] = b.Health;
            // Fahrzeug-Tempo neu anwenden
            var vc = Game.GameAccess.Game<Controllers.VehiclesController>();
            if (vc?.Vehicles != null)
                foreach (var v in vc.Vehicles)
                    if (v != null && CheatWindow.SpeedBoost.ContainsKey(v.Id ?? ""))
                        try { v.UpdateMaxSpeed(); } catch { }
            if (s.Stats != null && d.Stats != null && d.Stats.Count > 0)
                s.Stats.Import(d.Stats, d.StatNames);
            if (d.SquadLastBy != null && d.SquadLastBy.Count > 0)
                Sync.SquadViewSync.ImportLast(d.SquadLastBy);
            Loaded++;
            Plugin.Log.LogInfo("IFZCOOP Spielstand-Zusatz geladen: " + path);
        }

        private static void Fill(HashSet<string> set, List<string> from)
        {
            set.Clear();
            if (from != null) foreach (var x in from) if (!string.IsNullOrEmpty(x)) set.Add(x);
        }

        private static void Fill(Dictionary<string, float> dict, Dictionary<string, float> from)
        {
            dict.Clear();
            if (from != null) foreach (var kv in from) dict[kv.Key] = kv.Value;
        }
    }
}
