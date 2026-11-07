using System;
using Controllers.Time;
using HarmonyLib;
using UnityEngine;

namespace IfzCoop
{
    /// <summary>
    /// F7 → Welt &amp; Zeit: „Uhrzeit einfrieren“ und „Uhr-Tempo“. Nur die Tageszeit läuft anders, das Spiel selbst
    /// (Trupps, Arbeiten, Kämpfe) läuft normal. Prefix auf TimeController.CustomUpdate:
    /// eingefroren → nur Spielzeit (GameplayDeltaTime/GameplayTime) weiterzählen, Uhr nicht;
    /// anderes Tempo → Uhr vor dem Original um gd·(Tempo−1) verschieben, das Original zählt dann gd dazu
    /// (netto gd·Tempo; Tageswechsel und Stunden-Ereignisse macht das Original). Pro Bild höchstens eine halbe
    /// Spielstunde extra, damit keine Stunden-Ereignisse (Essen, Autospeichern) übersprungen werden.
    /// Gast: übernimmt das Uhr-Tempo des Hosts (Session.HostClockRate, aus den Zeit-Meldungen geschätzt).
    /// </summary>
    internal static class ClockFreeze
    {
        private static readonly int TimeSpeedId = Shader.PropertyToID("TimeSpeed");
        private static Action<float> _setDelta, _setTime, _setGts;

        /// <summary>Wirksames Uhr-Tempo (1 = normal, 0 = steht).</summary>
        public static float Rate
        {
            get
            {
                if (CheatWindow.FreezeClock) return 0f;
                var s = Plugin.Instance?.Session;
                if (s != null && s.Role == Role.Guest) return Session.HostClockRate;
                return CheatWindow.ClockRate;
            }
        }

        public static void Patch(Harmony h)
        {
            try
            {
                _setDelta = AccessTools.MethodDelegate<Action<float>>(AccessTools.PropertySetter(typeof(TimeController), "GameplayDeltaTime"));
                _setTime = AccessTools.MethodDelegate<Action<float>>(AccessTools.PropertySetter(typeof(TimeController), "GameplayTime"));
                _setGts = AccessTools.MethodDelegate<Action<float>>(AccessTools.Method(typeof(TimeController), "SetCurrentDayGts", new[] { typeof(float) }));
                h.Patch(AccessTools.Method(typeof(TimeController), "CustomUpdate"),
                    prefix: new HarmonyMethod(typeof(ClockFreeze), nameof(CustomUpdatePrefix)));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Uhr-Tempo nicht gesetzt: " + e.Message); }
        }

        private static bool CustomUpdatePrefix()
        {
            if (!Game.GameAccess.IsGameStarted || _setDelta == null || _setTime == null || _setGts == null)
                return true;
            float rate = Rate;
            if (Mathf.Abs(rate - 1f) < 0.001f)
                return true;
            float gd = Mathf.Min(Time.deltaTime, 0.1f) * TimeController.SpeedMultiplier;
            if (rate <= 0.001f)
            {
                _setDelta(gd);
                _setTime(TimeController.GameplayTime + gd);
                Shader.SetGlobalFloat(TimeSpeedId, gd);
                return false;
            }
            float cur = TimeController.CurrentDayGameplaySeconds;
            float extra = gd * (rate - 1f);
            float maxExtra = Mathf.Max(1f, TimeController.HourLengthInGts * 0.5f);
            if (extra > maxExtra) extra = maxExtra;
            float target = cur + extra;
            if (target < 0f) target = 0f;
            float dayLen = TimeController.DayLengthInSeconds;
            if (dayLen > 0f && target > dayLen) target = dayLen; // Original wechselt dann den Tag
            _setGts(target);
            return true;
        }
    }
}
