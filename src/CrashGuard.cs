using System;
using System.Collections.Generic;
using HarmonyLib;

namespace IfzCoop
{
    /// <summary>
    /// Der Fehlerwächter des Spiels (Utilities.BugDetector.BugDetector) zählt gleiche Ausnahmen hintereinander. Ab 30 öffnet er
    /// im Spiel das Fehlerbericht-Fenster „BLOCKER … (not a crash)“ und schickt beim Schließen ins Hauptmenü
    /// (ScenesManager.BackToMenuCauseException). Im Koop flog so der Gast im Kampf aus dem Spiel, blieb aber in der Sitzung
    /// hängen und musste erst „Sitzung verlassen“ (Meldung 2026-11-09). Auslöser war z. B. ein Randpfeil ohne Marker,
    /// der jedes Bild dieselbe NullReferenceException warf.
    ///
    /// 1. Im Koop: statt rauswerfen die Aufrufkette einmal ins Log schreiben, kurz melden und weiterspielen.
    /// 2. Geht das Spiel trotzdem wegen eines Fehlers ins Hauptmenü: die Koop-Sitzung sauber beenden, damit man direkt neu
    ///    beitreten kann (der Host sagt seinen Gästen Bescheid).
    /// </summary>
    internal static class CrashGuard
    {
        private static AccessTools.FieldRef<object, string> _prevStack;
        private static AccessTools.FieldRef<object, int> _counter;
        private static readonly HashSet<string> _logged = new HashSet<string>();
        public static int LoopsCaught { get; private set; }

        public static void ApplyPatches(Harmony harmony)
        {
            try
            {
                var t = AccessTools.TypeByName("Utilities.BugDetector.BugDetector");
                var m = t != null ? AccessTools.Method(t, "HandleSameExceptionStackTrace") : null;
                if (m != null)
                {
                    _prevStack = AccessTools.FieldRefAccess<string>(t, "_previousExceptionStackTrace");
                    _counter = AccessTools.FieldRefAccess<int>(t, "_sameExceptionCounter");
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(CrashGuard), nameof(SamePrefix)));
                }
                else Plugin.Log.LogWarning("IFZCOOP Fehlerwächter-Haken fehlt (BugDetector)");

                var back = AccessTools.Method(typeof(ZenjectExtensions.Installers.ScenesManager), "BackToMenuCauseException");
                if (back != null)
                    harmony.Patch(back, prefix: new HarmonyMethod(typeof(CrashGuard), nameof(BackToMenuPrefix)));
                else Plugin.Log.LogWarning("IFZCOOP Haken BackToMenuCauseException fehlt");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("IFZCOOP Fehlerwächter-Haken: " + e.Message);
            }
        }

        private static bool InCoop
        {
            get
            {
                var s = Plugin.Instance?.Session;
                return s != null && s.Role != Role.None;
            }
        }

        // BugDetector.HandleSameExceptionStackTrace(string stacktrace) – Prefix
        private static bool SamePrefix(object __instance, string stacktrace)
        {
            try
            {
                if (!InCoop || _prevStack == null || _counter == null)
                    return true;
                if (_prevStack(__instance) != stacktrace || _counter(__instance) < 25)
                    return true;
                // kurz vor der Grenze von 30: Zähler zurück, nicht rauswerfen
                _counter(__instance) = 0;
                LoopsCaught++;
                string first = stacktrace ?? "";
                int nl = first.IndexOf('\n');
                string head = nl > 0 ? first.Substring(0, nl).Trim() : first.Trim();
                if (_logged.Add(first) && _logged.Count <= 20)
                {
                    Plugin.Log.LogWarning("IFZCOOP Fehler wiederholt sich (Spiel hätte ins Hauptmenü geworfen):\n" + first);
                    Plugin.Instance?.Session?.ShowNotice("Spielfehler abgefangen, es geht weiter (F8-Bericht hilft beim Beheben)", 6f);
                }
                else if (LoopsCaught % 50 == 0)
                    Plugin.Log.LogWarning("IFZCOOP Fehler wiederholt sich weiter: " + head);
                return true;
            }
            catch
            {
                return true;
            }
        }

        // ScenesManager.BackToMenuCauseException() – Prefix: Sitzung sauber beenden
        private static void BackToMenuPrefix()
        {
            try
            {
                var s = Plugin.Instance?.Session;
                if (s == null || s.Role == Role.None)
                    return;
                Plugin.Log.LogWarning("IFZCOOP Spiel geht wegen eines Fehlers ins Hauptmenü – Koop-Sitzung wird beendet");
                s.Leave("Spielfehler, zurück im Hauptmenü", sendBye: true, backToMenu: false);
                s.ShowNotice("Spielfehler: Koop beendet. Du kannst gleich wieder beitreten.", 15f);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("IFZCOOP Sitzung beenden nach Spielfehler: " + e.Message);
            }
        }
    }
}
