using System;
using Controllers.Time;
using HarmonyLib;
using UnityEngine;

namespace IfzCoop
{
    /// <summary>
    /// „Uhrzeit einfrieren“ (F7 → Welt &amp; Zeit): die Uhr bleibt stehen, das Spiel läuft weiter (Trupps laufen,
    /// Arbeiten gehen voran, kämpfen). Prefix auf TimeController.CustomUpdate: zählt nur die Spielzeit
    /// (GameplayDeltaTime/GameplayTime) weiter, die Tageszeit nicht. „Uhrzeit setzen“ geht weiterhin.
    /// Gast: friert mit, sobald die Uhr des Hosts stillsteht (Session.HostClockFrozen).
    /// </summary>
    internal static class ClockFreeze
    {
        private static readonly int TimeSpeedId = Shader.PropertyToID("TimeSpeed");
        private static Action<float> _setDelta, _setTime;

        public static bool Active => CheatWindow.FreezeClock || Session.HostClockFrozen;

        public static void Patch(Harmony h)
        {
            try
            {
                _setDelta = AccessTools.MethodDelegate<Action<float>>(AccessTools.PropertySetter(typeof(TimeController), "GameplayDeltaTime"));
                _setTime = AccessTools.MethodDelegate<Action<float>>(AccessTools.PropertySetter(typeof(TimeController), "GameplayTime"));
                h.Patch(AccessTools.Method(typeof(TimeController), "CustomUpdate"),
                    prefix: new HarmonyMethod(typeof(ClockFreeze), nameof(CustomUpdatePrefix)));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Uhrzeit einfrieren nicht gesetzt: " + e.Message); }
        }

        private static bool CustomUpdatePrefix()
        {
            if (!Active || !Game.GameAccess.IsGameStarted || _setDelta == null || _setTime == null)
                return true;
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            float gd = dt * TimeController.SpeedMultiplier;
            _setDelta(gd);
            _setTime(TimeController.GameplayTime + gd);
            Shader.SetGlobalFloat(TimeSpeedId, gd);
            return false;
        }
    }
}
