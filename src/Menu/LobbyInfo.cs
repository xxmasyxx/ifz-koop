using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GameCustomization;
using IfzCoop.Game;
using IfzCoop.Net;
using UnityEngine;
using ZenjectExtensions.Installers;

namespace IfzCoop.Menu
{
    /// <summary>
    /// KONZEPT-MENUE M4: Was der Host im Hauptmenü gerade auswählt, live für die Gäste in der Lobby
    /// (Wunsch 6.11.: „wenn der Host ein neues Spiel erstellt, sehen die Gäste die Spieleinstellungen“).
    /// Host: liest Kartenauswahl (TileSelector, TilerMapSizeHandler), Anpassungsfenster (GameCustomize) und Ladezustand,
    /// schickt bei Änderung Msg.LobbyInfo (höchstens 2×/s). Gast: zeigt die Zeilen im Kasten „Der Host wählt“ (MenuLobbyUi).
    /// </summary>
    internal static class LobbyInfo
    {
        /// <summary>Gast: Zeilen vom Host. Host: die zuletzt gesendeten.</summary>
        public static List<string> Lines { get; private set; } = new List<string>();
        public static int Sent { get; private set; }
        public static int Received { get; private set; }

        private static string _lastSig;

        // Zuletzt vom Spiel gewählter Kartenausschnitt (Haken auf TilesGridController.Init): Kachel, Größe.
        private static int _pickX, _pickY, _pickPadding;
        private static float _pickAt = -1f;
        public static int Picks { get; private set; }

        // Zuletzt vom Host geladener Spielstand (Haken auf SaveHandler.PrepareToLoadMap)
        private static string _loadName, _loadCity;
        private static DateTime _loadDate;
        private static float _loadAt = -1f;

        private static void PrepareToLoadMapPrefix(SaveInfo saveInfo)
        {
            try
            {
                if (saveInfo == null) return;
                _loadName = saveInfo.Name;
                _loadCity = saveInfo.CityName;
                _loadDate = saveInfo.DateTime;
                _loadAt = Time.realtimeSinceStartup;
                _lastSig = null;
            }
            catch { }
        }

        public static void Patch(HarmonyLib.Harmony h)
        {
            try
            {
                var lm = HarmonyLib.AccessTools.Method(typeof(SaveHandler), "PrepareToLoadMap");
                h.Patch(lm, prefix: new HarmonyLib.HarmonyMethod(typeof(LobbyInfo), nameof(PrepareToLoadMapPrefix)));
                Plugin.Log.LogInfo("Gepatcht: SaveHandler.PrepareToLoadMap (Lobby-Info)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Lobby-Info Laden-Haken: " + e.Message);
            }
            try
            {
                var m = HarmonyLib.AccessTools.Method(typeof(MapTiler.TilesGridController), "Init");
                h.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(LobbyInfo), nameof(InitPostfix)));
                Plugin.Log.LogInfo("Gepatcht: TilesGridController.Init (Lobby-Info)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Lobby-Info Haken: " + e.Message);
            }
        }

        private static void InitPostfix(object[] __args)
        {
            try
            {
                var c = __args[0];
                var t = HarmonyLib.Traverse.Create(c);
                _pickX = t.Property("Tx").GetValue<int>();
                _pickY = t.Property("Ty").GetValue<int>();
                _pickPadding = __args.Length > 5 && __args[5] is int p ? p : 1;
                _pickAt = Time.realtimeSinceStartup;
                Picks++;
                _lastSig = null;
                Plugin.Log.LogInfo($"IFZCOOP Lobby: Host wählt Kachel {_pickX},{_pickY} Größe {_pickPadding}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("Lobby-Info Init: " + e.Message);
            }
        }
        private static float _next;

        private static Session S => Plugin.Instance?.Session;

        // ================================================================ Host

        public static void HostUpdate()
        {
            var s = S;
            if (s == null || s.Role != Role.Host)
                return;
            bool menu;
            try { menu = ScenesManager.IsMainSceneActive || ScenesManager.IsSceneSwitching; } catch { menu = false; }
            if (!menu || GameAccess.IsGameStarted)
                return;
            float now = Time.realtimeSinceStartup;
            if (now < _next) return;
            _next = now + 0.5f;
            List<string> lines;
            try { lines = Build(); }
            catch (Exception e) { lines = new List<string> { "?" }; Plugin.Log.LogDebug("Lobby-Info: " + e.Message); }
            string sig = string.Join("\n", lines);
            if (sig == _lastSig) return;
            _lastSig = sig;
            Lines = lines;
            s.BroadcastToLobby(BuildMessage());
            Sent++;
        }

        public static byte[] BuildMessage()
        {
            var lines = Lines;
            return Protocol.Build(Msg.LobbyInfo, w =>
            {
                w.Write(lines.Count);
                foreach (var l in lines) w.Write(l);
            });
        }

        /// <summary>Neuer Gast in der Lobby: sofort den aktuellen Stand.</summary>
        public static void ForceResend() => _lastSig = null;

        private static T Find<T>() where T : UnityEngine.Object
        {
            try { return UnityEngine.Object.FindObjectOfType<T>(); } catch { return null; }
        }

        private static List<string> Build()
        {
            var lines = new List<string>();
            var inv = CultureInfo.InvariantCulture;
            bool loading = false;
            try { loading = ScenesManager.IsSceneSwitching; } catch { }
            var customize = Find<GameCustomizeController>();
            var grid = Find<MapTiler.TilesGridController>();
            var selector = grid != null && grid.isActive ? grid.tileSelector : null;
            var finder = Find<UITools.CityFinderView>();
            var load = Find<UI.EscMenu.LoadWindow>();
            bool tileChosen = selector != null && selector.CurrentTileCoord != Vector3Int.zero;
            // Globus/Kartenwahl offen: das Spiel schaltet dafür seine Kartenwahl-Canvases ein (MenuCanvas.ShowCitySearch)
            bool finderOpen = false;
            try
            {
                var mc = Find<UI.EscMenu.MenuCanvas>();
                var list = mc != null ? HarmonyLib.Traverse.Create(mc).Field("_mapSelectCanvases").GetValue<List<Canvas>>() : null;
                finderOpen = list != null && list.Any(c => c != null && c.enabled);
            }
            catch { finderOpen = finder != null && finder.gameObject.activeInHierarchy; }
            // zurück im Hauptmenü (kein Globus, keine Karte, keine Anpassung): alte Auswahl vergessen
            if (!loading && !finderOpen && !(grid != null && grid.isActive) && !(customize != null && customize.IsActive))
            {
                _pickAt = -1f;
                _loadAt = -1f;
            }
            bool picked = _pickAt > 0f;

            string phase;
            if (loading) phase = "Lädt die Karte – gleich geht es los";
            else if (customize != null && customize.IsActive) phase = "Spiel-Einstellungen";
            else if (tileChosen || picked) phase = "Wählt den Kartenausschnitt";
            else if (finderOpen) phase = "Sucht eine Stadt auf dem Globus";
            else if (load != null && load.gameObject.activeInHierarchy) phase = "Wählt einen Spielstand";
            else phase = "Im Hauptmenü";
            lines.Add("Gerade: " + phase);

            if (tileChosen)
            {
                string city = selector.CurrentCityName, country = selector.CurrentCityCountry;
                if (!string.IsNullOrWhiteSpace(city))
                    lines.Add("Stadt: " + city.Trim() + (string.IsNullOrWhiteSpace(country) ? "" : ", " + country.Trim()));
                int n = grid.MapPadding * 2 + 1;
                lines.Add($"Kartengröße: {n} × {n}");
            }
            else if (picked)
            {
                string city = MapTiler.TileSelector.SelectedCityName;
                lines.Add("Stadt: " + (string.IsNullOrWhiteSpace(city) ? $"Kachel {_pickX}, {_pickY}" : city.Trim()));
                int n = _pickPadding * 2 + 1;
                lines.Add($"Kartengröße: {n} × {n}");
            }
            else if (_loadAt > 0f)
            {
                // Host lädt einen Spielstand
                lines.Add("Spielstand: " + (string.IsNullOrWhiteSpace(_loadName) ? "?" : _loadName.Trim()));
                if (!string.IsNullOrWhiteSpace(_loadCity)) lines.Add("Stadt: " + _loadCity.Trim());
                if (_loadDate != default(DateTime)) lines.Add("Gespeichert: " + _loadDate.ToString("dd.MM.yyyy HH:mm", inv));
            }
            else if (loading && !string.IsNullOrEmpty(SaveHandler.SelectedTilesCoordinates))
            {
                lines.Add("Karte: " + SaveHandler.SelectedTilesCoordinates);
            }

            if (customize != null && customize.IsActive)
            {
                var gc = GameAccess.Project<GameCustomize>();
                if (gc != null)
                {
                    if (!string.IsNullOrWhiteSpace(gc.ZoneName)) lines.Add("Zone: " + gc.ZoneName.Trim());
                    lines.Add("Schwierigkeit: " + Difficulty(gc.DifficultyLevel));
                    // Bezeichnung so, wie das Spiel sie in seiner Auswahlliste zeigt (übersetzt)
                    try
                    {
                        var wm = Find<WorkersManagement>();
                        var dd = wm != null ? HarmonyLib.Traverse.Create(wm).Field("workManagementDropdown").GetValue<TMPro.TMP_Dropdown>() : null;
                        if (dd != null && dd.value >= 0 && dd.value < dd.options.Count)
                            lines.Add("Zuweisung von Arbeitern: " + dd.options[dd.value].text);
                    }
                    catch { }
                    lines.Add($"Bewohner ×{gc.PeopleCountMultiplier.ToString("0.##", inv)} · Funde ×{gc.ResourcesFoundQuantityMultiplier.ToString("0.##", inv)} · Horden ×{gc.HordeSizeMultiplier.ToString("0.##", inv)}");
                    lines.Add($"Start: {Month(gc.InitialDayOfYear)} · Tag {gc.DayLengthInSeconds / 60} min · Max. {gc.MaxPopulation} Bewohner");
                    lines.Add($"Story {OnOff(gc.StoryEvents)} · Einführung {OnOff(gc.TutorialEvents)} · Krankheit {OnOff(gc.Sickness)}");
                    lines.Add($"Verstecke {OnOff(gc.Hideouts)} · Nester {OnOff(gc.Lairs)} · Tiere {OnOff(gc.InfectedAnimals)} · Schwimmer {OnOff(gc.SwimmingInfected)}");
                    // „Konvoi starten“ so, wie der Haken im Anpassungsfenster gerade steht
                    try
                    {
                        var cs = Find<ConvoyStartSetting>();
                        var tg = cs != null ? HarmonyLib.Traverse.Create(cs).Field("toggle").GetValue<UnityEngine.UI.Toggle>() : null;
                        bool convoy = tg != null ? tg.isOn : gc.ConvoyStart;
                        lines.Add("Konvoi starten: " + OnOff(convoy));
                    }
                    catch { if (gc.ConvoyStart) lines.Add("Konvoi starten: an"); }
                }
            }
            lines.Add("Modus: " + (LobbySettings.Mode == "versus" ? "Versus" : "Koop"));
            return lines;
        }

        private static string OnOff(bool b) => b ? "an" : "aus";

        private static string Difficulty(GameDifficulty d)
        {
            switch (d)
            {
                case GameDifficulty.VeryEasy: return "Sehr leicht";
                case GameDifficulty.Easy: return "Leicht";
                case GameDifficulty.Medium: return "Mittel";
                case GameDifficulty.Hard: return "Schwer";
                case GameDifficulty.VeryHard: return "Sehr schwer";
            }
            return d.ToString();
        }

        private static readonly string[] Months = { "Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember" };

        private static string Month(int dayOfYear)
        {
            try
            {
                var date = new DateTime(2025, 1, 1).AddDays(Math.Max(0, dayOfYear - 1));
                return Months[date.Month - 1];
            }
            catch { return "Tag " + dayOfYear; }
        }

        // ================================================================ Gast

        public static void GuestOnInfo(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 40) throw new InvalidDataException("Lobby-Info");
            var list = new List<string>(n);
            for (int i = 0; i < n; i++) list.Add(r.ReadString());
            Lines = list;
            Received++;
        }

        public static void Reset()
        {
            Lines = new List<string>();
            _lastSig = null;
        }

        public static string StatusLine() =>
            $"lobbyinfo picks={Picks} sent={Sent} received={Received} lines={Lines.Count} text={string.Join("|", Lines).Replace(' ', '_')}";
    }
}
