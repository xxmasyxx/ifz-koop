using System;
using System.IO;
using System.Reflection;
using System.Text;
using Controllers.Time;
using Controllers.Weather;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using Newtonsoft.Json;
using UnityEngine;
using GameJson = Utilities.JsonSerializer;

namespace IfzCoop.Versus
{
    /// <summary>
    /// Uhr, Tempo und Wetter vom Host (VERSUS-TECHNIK 1.2, 8.4). Gleiches Verfahren wie im Koop (Session.CorrectTime,
    /// WorldSync-Wetterteil), aber auf dem Versus-Kanal und ohne die Koop-Session.
    ///
    /// Wetter: Alle starten aus demselben Spielstand, die Jahres-Vorhersage ist darin enthalten. Abweichen kann sie nur über
    /// Tagessprünge beim Nachstellen der Uhr (WeatherController.UpdateForecast). Darum ist UpdateForecast beim Gast gesperrt
    /// (VsPatches), und der Host schickt Vorhersage, Verlauf und das aktuelle Wetter. Der Vorhersage-Fortschritt
    /// (Meteorologen, Forschung) bleibt beim Spieler, er gehört zu seiner Basis.
    /// </summary>
    internal sealed class VsClock
    {
        private const float WeatherCheckInterval = 1f;
        private const float WeatherResendInterval = 30f;

        private static readonly MethodInfo SetCurrentDayGtsMethod =
            AccessTools.Method(typeof(TimeController), "SetCurrentDayGts", new[] { typeof(float) });
        private static readonly MethodInfo WeatherLoadStates = AccessTools.Method(typeof(WeatherController), "LoadWeatherStatesFromSave");
        private static readonly MethodInfo WeatherLoadState = AccessTools.Method(typeof(WeatherController), "LoadWeatherState");
        private static readonly MethodInfo WeatherSetCurrent = AccessTools.Method(typeof(WeatherController), "SetCurrentWeather");
        private static readonly MethodInfo WeatherSetImmediately = AccessTools.Method(typeof(WeatherController), "SetWeatherImmediately");
        private static readonly AccessTools.FieldRef<WeatherController, int> WeatherDayField =
            AccessTools.FieldRefAccess<WeatherController, int>("_currentDay");

        private readonly VersusSession _vs;
        private float _nextWeatherCheck;
        private float _nextWeatherResend;
        private string _weatherSig;

        public float DriftSeconds { get; private set; }
        public int Corrections { get; private set; }
        public int SpeedApplied { get; private set; }
        public int WeatherSent { get; private set; }
        public int WeatherApplied { get; private set; }

        public VsClock(VersusSession vs)
        {
            _vs = vs;
        }

        public void Reset()
        {
            DriftSeconds = 0f;
            Corrections = 0;
            _weatherSig = null;
            _nextWeatherResend = 0f;
        }

        public void ResendWeather() => _weatherSig = null;

        // ================================================================ Uhr

        public byte[] BuildTimeState()
        {
            return VsNet.Build(Msg.TimeState, w =>
            {
                w.Write(TimeController.Day);
                w.Write(TimeController.CurrentDayGameplaySeconds);
                w.Write(TimeController.SpeedMultiplier);
                w.Write(TimeController.DayLengthInSeconds);
            });
        }

        /// <summary>Gast: Uhr des Hosts. Liefert das Tempo des Hosts.</summary>
        public float GuestOnTimeState(BinaryReader r, double rttMs, bool apply)
        {
            int day = r.ReadInt32();
            float dayGts = r.ReadSingle();
            float speed = r.ReadSingle();
            int dayLength = r.ReadInt32();
            if (!apply || !GameAccess.IsGameStarted)
                return speed;
            float hostTotal = day * (float)dayLength + dayGts;
            float localTotal = TimeController.Day * (float)TimeController.DayLengthInSeconds + TimeController.CurrentDayGameplaySeconds;
            float latency = rttMs > 0 ? (float)(rttMs / 2000.0) * speed : 0f;
            DriftSeconds = hostTotal + latency - localTotal;
            CorrectTime(day, Mathf.Min(dayGts + latency, dayLength - 0.01f), dayLength);
            return speed;
        }

        /// <summary>Wie Session.CorrectTime: ab 1,5 s Abweichung nachstellen, rückwärts nur innerhalb derselben Stunde.</summary>
        private void CorrectTime(int hostDay, float hostGts, int dayLength)
        {
            if (Mathf.Abs(DriftSeconds) < 1.5f || SetCurrentDayGtsMethod == null || dayLength <= 0)
                return;
            var clock = GameAccess.Game<TimeController>();
            if (clock == null)
                return;
            if (DriftSeconds < 0f)
            {
                if (hostDay != TimeController.Day)
                    return;
                int hostHour = (int)TimeController.GameplaySecondsToHour(hostGts, dayLength);
                if (hostHour != TimeController.HourInt)
                    return;
            }
            if (hostDay != TimeController.Day)
                clock.SetDay(hostDay);
            SetCurrentDayGtsMethod.Invoke(null, new object[] { hostGts });
            Corrections++;
        }

        /// <summary>Gast: Tempo des Hosts setzen (der eigene SetSpeedMultiplier ist sonst gesperrt).</summary>
        public void ApplySpeed(float speed)
        {
            var changer = GameAccess.TimeChanger;
            if (changer == null)
                return;
            VsNet.ApplyingSpeed = true;
            try
            {
                changer.SetSpeedMultiplier(speed, shouldPlaySound: false);
                SpeedApplied++;
            }
            finally
            {
                VsNet.ApplyingSpeed = false;
            }
        }

        // ================================================================ Wetter

        public void HostUpdate(float now)
        {
            if (!GameAccess.IsGameStarted || _vs.LoadedGuestCount == 0 || now < _nextWeatherCheck)
                return;
            _nextWeatherCheck = now + WeatherCheckInterval;
            if (now >= _nextWeatherResend)
            {
                _nextWeatherResend = now + WeatherResendInterval;
                _weatherSig = null;
            }
            var c = GameAccess.Game<WeatherController>();
            if (c == null)
                return;
            try
            {
                var s = new WeatherControllerSerialized();
                s.Serialize(c);
                string sig = Signature(s, WeatherDayField(c));
                if (sig == _weatherSig)
                    return;
                _weatherSig = sig;
                string json = JsonConvert.SerializeObject(s, GameJson.DefaultSettings);
                int day = WeatherDayField(c);
                _vs.BroadcastLoaded(VsNet.Build(Msg.VsWeather, w => { w.Write(json); w.Write(day); }));
                WeatherSent++;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("IFZVS Wetter senden: " + e.Message);
                _nextWeatherCheck = now + 10f;
            }
        }

        private static string Signature(WeatherControllerSerialized s, int day)
        {
            var sb = new StringBuilder();
            sb.Append(day).Append('|').Append(s.WeatherHistory?.Count ?? 0).Append('|');
            if (s.WeatherForecast != null)
                foreach (var w in s.WeatherForecast)
                    sb.Append(Mathf.Round(w.Temperature * 10f)).Append(Mathf.Round(w.Clouds * 100f)).Append(Mathf.Round(w.Precipitation * 100f)).Append(',');
            return sb.ToString();
        }

        public void GuestOnWeather(BinaryReader r)
        {
            string json = r.ReadString();
            int day = r.ReadInt32();
            var c = GameAccess.Game<WeatherController>();
            if (c == null || WeatherLoadStates == null)
                return;
            VsNet.Applying = true;
            try
            {
                var s = JsonConvert.DeserializeObject<WeatherControllerSerialized>(json, GameJson.DefaultSettings);
                if (s == null)
                    return;
                if (s.WeatherForecast != null)
                    WeatherLoadStates.Invoke(c, new object[] { s.WeatherForecast, c.Forecast });
                if (s.WeatherHistory != null)
                    WeatherLoadStates.Invoke(c, new object[] { s.WeatherHistory, c.History });
                if (WeatherDayField(c) != day && s.CurrentWeather != null && WeatherLoadState != null)
                {
                    WeatherDayField(c) = day;
                    var state = WeatherLoadState.Invoke(c, new object[] { s.CurrentWeather });
                    WeatherSetCurrent?.Invoke(c, new[] { state });
                    WeatherSetImmediately?.Invoke(c, new[] { state });
                }
                WeatherApplied++;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("IFZVS Wetter übernehmen: " + e.Message);
            }
            finally
            {
                VsNet.Applying = false;
            }
        }

        /// <summary>Kurzform des Wetters für Statuszeilen (Host und Gast vergleichbar).</summary>
        public static string WeatherLine()
        {
            var c = GameAccess.Game<WeatherController>();
            if (c == null)
                return "-";
            try
            {
                var s = new WeatherControllerSerialized();
                s.Serialize(c);
                var f = s.WeatherForecast != null && s.WeatherForecast.Count > 0 ? s.WeatherForecast[0] : null;
                return "day=" + WeatherDayField(c) + " history=" + (s.WeatherHistory?.Count ?? 0)
                       + (f != null ? " next=" + Mathf.Round(f.Temperature) + "C/" + Mathf.Round(f.Clouds) + "/" + Mathf.Round(f.Precipitation) : "");
            }
            catch (Exception)
            {
                return "?";
            }
        }
    }
}
