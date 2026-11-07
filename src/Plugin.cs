using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace IfzCoop
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "de.ifzcoop";
        public const string Name = "IFZ Koop";
        public const string Version = "0.23.27";

        internal static Plugin Instance;
        internal static ManualLogSource Log;
        internal static ConfigEntry<KeyCode> ToggleKey;
        internal static ConfigEntry<KeyCode> CheatKey;
        internal static ConfigEntry<KeyCode> CameraRescueKey;
        internal static ConfigEntry<KeyCode> BugReportKey;
        internal static ConfigEntry<bool> CheatEnabled;
        internal static ConfigEntry<bool> GuestCheats;
        internal static ConfigEntry<string> LobbyMode;
        internal static ConfigEntry<string> PlayerName;
        internal static ConfigEntry<string> DirectAddress;
        internal static ConfigEntry<bool> InstantBuildSetting;
        internal static ConfigEntry<int> MaxPlayers;
        internal static ConfigEntry<bool> GuestMayChangeSpeed;
        internal static ConfigEntry<Sync.AnswerMode> AnswerModeSetting;
        internal static ConfigEntry<string> MyColorSetting;

        internal static string NameOverride;

        internal Session Session;

        private Harmony _harmony;
        private QaDriver _qa;
        private CoopWindow _window;
        private CheatWindow _cheat;
        private bool _inputBroken;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            // Ab 0.22.0 sitzt alles im Spielmenü (Kacheln, „Mehrspieler“). Das alte Fenster gibt es nur noch als Notausgang.
            ToggleKey = Config.Bind("Allgemein", "NotfallFensterTaste", KeyCode.None, "Altes Koop-Fenster (nur für Notfälle; Standard: aus). Alles Übliche ist im Hauptmenü/Esc-Menü unter „Mehrspieler“.");
            DirectAddress = Config.Bind("Allgemein", "DirektAdresse", "127.0.0.1:27120", "Adresse für „Direkt beitreten“ (ohne Steam).");
            BugReportKey = Config.Bind("Allgemein", "FehlerberichtTaste", KeyCode.F8, "Öffnet das Fenster „Fehler melden“: Log, Zustand und Bildschirmfoto werden gespeichert, beim Gast auch an den Host geschickt.");
            CameraRescueKey = Config.Bind("Allgemein", "KameraRettenTaste", KeyCode.None, "Setzt eine hängende Kamera zurück (auch als Knopf im Fehlerbericht-Fenster). Gleiche Taste wie der Fehlerbericht = nur Fehlerbericht.");
            CheatKey = Config.Bind("Entwickler", "MenueTaste", KeyCode.F7, "Öffnet das Entwickler-Menü (nur Host und Einzelspiel).");
            PlayerName = Config.Bind("Allgemein", "Name", "", "Eigener Name für die Direktverbindung ohne Steam (mit Steam gilt der Steam-Name).");
            LobbyMode = Config.Bind("Host", "Spielmodus", "koop", "Mehrspieler-Modus im Hauptmenü: koop oder versus.");
            GuestCheats = Config.Bind("Entwickler", "GaesteDuerfenMenue", false, "Gäste dürfen das Entwickler-Menü benutzen (ihre Klicks führt der Host aus). Im F6-Fenster umschaltbar.");
            CheatEnabled = Config.Bind("Entwickler", "MenueAn", true, "Entwickler-Menü erlauben (Rohstoffe, Forschung, Sofort-Bauen …).");
            // „Sofort bauen“ gilt ab 0.23.12 nur für das laufende Spiel (Wunsch: nicht ins nächste Spiel mitnehmen).
            // Alte gespeicherte Einstellung zurücksetzen.
            InstantBuildSetting = Config.Bind("Entwickler", "SofortBauen", false, "Nicht mehr benutzt: „Sofort bauen“ gilt nur im laufenden Spiel (Entwickler-Menü).");
            if (InstantBuildSetting.Value) InstantBuildSetting.Value = false;
            CheatWindow.InstantBuild = false;
            MaxPlayers = Config.Bind("Host", "MaxSpieler", 4, "Spieler pro Lobby, Host eingeschlossen (2 bis 8).");
            GuestMayChangeSpeed = Config.Bind("Host", "GaesteDuerfenTempoAendern", true, "Gäste dürfen Pause und Spieltempo beim Host anfragen.");
            MyColorSetting = Config.Bind("Allgemein", "MeineFarbe", "", "Eigene Spielerfarbe als Hex (z. B. 4099FF), leer = automatisch. "
                + "Grün ist nicht erlaubt (Grün = freier Trupp). Auch im Fenster „Mehrspieler“ (Hauptmenü/Esc-Menü) unter „Meine Farbe“ wählbar.");
            AnswerModeSetting = Config.Bind("Host", "FunkAntworten", Sync.AnswerMode.Alle, "Wer darf Funksprüche beantworten: Alle, NurHost oder NurGaeste. Auch im F6-Fenster umschaltbar.");

            _harmony = new Harmony(Guid);
            try
            {
                Patches.Apply(_harmony);
            }
            catch (Exception e)
            {
                // Laut scheitern: ohne Patches würde der Gast die Zeit des Hosts nicht übernehmen.
                Log.LogError("IFZCOOP setup FAIL: " + e);
                enabled = false;
                return;
            }

            // Einheiten-Abgleich: einzeln abgesichert, ein fehlender Haken schaltet nicht den ganzen Mod ab.
            // Kamera-Sperren richtig loesen (XOR-Fehler des Spiels), Tweens der Antwortknoepfe beenden (radiofix)
            UiLocks.Apply(_harmony);
            Menu.LobbyInfo.Patch(_harmony);
            Sync.HqSuggest.Patch(_harmony);
            UnitPatches.Apply(_harmony);
            SaveExtras.Patch(_harmony);
            ClockFreeze.Patch(_harmony);

            // Versus: eigene Haken, einzeln abgesichert (VERSUS-TECHNIK 1.3)
            Versus.VsPatches.Apply(_harmony);

            NameOverride = QaDriver.Arg(Environment.GetCommandLineArgs(), "-ifzcoop-name");
            Session = new Session();
            try
            {
                new Versus.VersusSession();
            }
            catch (Exception e)
            {
                Log.LogError("IFZVS Versus nicht verfügbar: " + e);
            }
            _window = new CoopWindow(Session);
            _cheat = new CheatWindow(Session);
            _qa = new QaDriver(Session);
            Log.LogInfo($"{Name} {Version} geladen (Spielversion {Application.version})");
            _updateNote = TakeUpdateNote();
        }

        // Der Updater (BepInEx\patchers\IfzCoop) legt nach einem Update eine Notiz ab; im Hauptmenü einmal zeigen.
        private string _updateNote;
        private float _updateNoteCheck;

        private static string TakeUpdateNote()
        {
            try
            {
                string f = System.IO.Path.Combine(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "cache"), "ifzcoop-updated.txt");
                if (!System.IO.File.Exists(f)) return null;
                var parts = System.IO.File.ReadAllText(f).Split('|');
                System.IO.File.Delete(f);
                return "IFZ Koop wurde automatisch auf " + Version + " aktualisiert" + (parts.Length > 0 && parts[0].Length > 0 ? " (vorher " + parts[0] + ")" : "");
            }
            catch (Exception e)
            {
                Log.LogWarning("Update-Notiz: " + e.Message);
                return null;
            }
        }

        // Einmal je geladenem Spiel: Rohstoffe ohne Spieldaten aus dem Lager (alte F7-Fehler, sonst hängt Tauschen)
        private bool _stockChecked;
        private float _stockCheckAt;

        private void CheckStockOnce()
        {
            if (!IfzCoop.Game.GameAccess.IsGameStarted) { _stockChecked = false; _stockCheckAt = 0f; return; }
            if (_stockChecked || Session == null || Session.Role == Role.Guest) return;
            if (_stockCheckAt == 0f) { _stockCheckAt = Time.realtimeSinceStartup + 5f; return; }
            if (Time.realtimeSinceStartup < _stockCheckAt) return;
            _stockChecked = true;
            try
            {
                var r = CheatWindow.CleanStock();
                if (r.Count > 0) Session.ShowNotice("Lager repariert: unbekannte Rohstoffe entfernt (" + string.Join(", ", r) + ")", 8f);
            }
            catch (Exception e) { Log.LogWarning("Lager prüfen: " + e.Message); }
        }

        private void ShowUpdateNoteInMenu()
        {
            if (_updateNote == null || Time.realtimeSinceStartup < _updateNoteCheck) return;
            _updateNoteCheck = Time.realtimeSinceStartup + 1f;
            var menu = GameObject.Find("MenuButtonsList");
            if (menu == null || !menu.activeInHierarchy) return;
            Session?.ShowNotice(_updateNote, 8f);
            Log.LogInfo(_updateNote);
            _updateNote = null;
        }

        private void Update()
        {
            if (!_inputBroken)
            {
                try
                {
                    if (ToggleKey.Value != KeyCode.None && Input.GetKeyDown(ToggleKey.Value))
                        _window.Visible = !_window.Visible;
                    if (CheatEnabled.Value && Input.GetKeyDown(CheatKey.Value))
                        _cheat.Toggle();
                    if (BugReportKey.Value != KeyCode.None && Input.GetKeyDown(BugReportKey.Value))
                        BugReport.Toggle(this);
                    else if (CameraRescueKey.Value != KeyCode.None && Input.GetKeyDown(CameraRescueKey.Value) && IfzCoop.Game.GameAccess.IsGameStarted)
                        Session?.ShowNotice(CameraRescue.Run() + " (" + CameraRescueKey.Value + ")", 4f);
                }
                catch (InvalidOperationException)
                {
                    // Spiel nutzt nur das neue Input-System: dann nur über den Knopf im Fenster.
                    _inputBroken = true;
                    _window.Visible = true;
                    Log.LogWarning("Alte Unity-Eingabe nicht verfügbar, Koop-Fenster bleibt offen.");
                }
            }

            try
            {
                Session.Update();
            }
            catch (Exception e)
            {
                Log.LogError("Session.Update: " + e);
            }

            try
            {
                ShowUpdateNoteInMenu();
                Menu.ModUi.UpdateCameraBlock();
                SaveExtras.Update();
                if (!IfzCoop.Game.GameAccess.IsGameStarted)
                {
                    // gilt nur für das laufende Spiel
                    CheatWindow.ResetGameOnly();
                }
                CheckStockOnce();
                Menu.NoticeUi.Update();
            }
            catch (Exception e)
            {
                Log.LogError("Meldungen: " + e);
            }

            try
            {
                Sync.HqSuggest.Update();
            }
            catch (Exception e)
            {
                Log.LogError("HQ-Vorschläge: " + e);
            }

            try
            {
                Menu.MenuLobbyUi.Update();
            }
            catch (Exception e)
            {
                Log.LogError("Menü: " + e);
            }

            try
            {
                CameraRescue.Tick();
            }
            catch (Exception e)
            {
                Log.LogError("Funk-Wächter: " + e);
            }

            try
            {
                Versus.VersusSession.Instance?.Update();
                Versus.VsQa.Update();
            }
            catch (Exception e)
            {
                Log.LogError("Versus.Update: " + e);
            }

            try
            {
                _cheat?.Update();
            }
            catch (Exception e)
            {
                Log.LogError("Entwickler: " + e);
            }

            try
            {
                _qa?.Update();
            }
            catch (Exception e)
            {
                Log.LogError("QA: " + e);
            }
        }

        /// <summary>Altes Koop-Fenster (F6) öffnen/schließen, z. B. vom Knopf „Mehrspieler“ im Hauptmenü.</summary>
        internal void ToggleCoopWindow() => _window.Visible = !_window.Visible;

        private void OnGUI()
        {
            _window?.Draw();
            _cheat?.Draw();
            BugReport.Draw();
            Menu.ColorPickerWindow.Draw();
            Menu.StatsWindow.Draw();
        }

        private void OnApplicationQuit()
        {
            Versus.VersusSession.Instance?.Shutdown();
            Session?.Shutdown();
        }

        private void OnDestroy()
        {
            Versus.VersusSession.Instance?.Shutdown();
            Session?.Shutdown();
            _harmony?.UnpatchSelf();
        }
    }
}
