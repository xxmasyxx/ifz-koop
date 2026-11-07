using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Controllers.Time;
using Controllers.Weather;
using Gameplay.Laws;
using Gameplay.Mood;
using Gameplay.Research;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using Newtonsoft.Json;
using UnityEngine;
using GameJson = Utilities.JsonSerializer;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Forschung, Gesetze, Stimmung, Wetter (Analyse P10, P12, P13, W8; Paket G).
    ///
    /// Host: je Bereich 1×/s eine Unterschrift; bei Änderung das ganze gespeicherte Objekt des Spiels
    /// (ResearchControllerSerialized, LawsControllerSerialized, MoodControllerSerialized, WeatherControllerSerialized).
    /// Gast: übernimmt den Stand. Eigene Fortschritte sind gesperrt (UnitPatches): Forschungsfortschritt und Wissenschaftsmaterial,
    /// Versammlung, Stimmungs-Takt, Wetter-Vorhersage (Würfel) und Vorhersage-Fortschritt.
    /// Klicks des Gastes (Forschung starten/abbrechen, Gesetz einführen) gehen als Wunsch an den Host, der sie ausführt.
    /// </summary>
    internal sealed class WorldSync
    {
        private const float SendInterval = 1f;
        private const float WeatherResendInterval = 30f;

        private readonly Session _session;
        private readonly Dictionary<Msg, string> _sentSig = new Dictionary<Msg, string>();
        private float _nextSend;
        private float _nextWeatherResend;

        public int ResearchApplied { get; private set; }
        public int LawsApplied { get; private set; }
        public int MoodApplied { get; private set; }
        public int WeatherApplied { get; private set; }
        public int RequestsSent { get; private set; }
        public int RequestsDone { get; private set; }

        public WorldSync(Session session)
        {
            _session = session;
        }

        public void ResendAll() => _sentSig.Clear();

        public void Reset() => ResendAll();

        private static string R(float f, float step) => Mathf.RoundToInt(f / step).ToString(CultureInfo.InvariantCulture);

        // ================================================================ Reflexion

        private static readonly MethodInfo ResearchButtonClick = AccessTools.Method(typeof(ResearchController), "HandleResearchButtonClick");
        private static readonly MethodInfo ResearchCancel = AccessTools.Method(typeof(ResearchController), "CancelCurrentResearch");
        private static readonly MethodInfo ResearchCurrentTechSetter = AccessTools.PropertySetter(typeof(ResearchController), "CurrentTechnology");
        private static readonly MethodInfo ResearchCurrentLawSetter = AccessTools.PropertySetter(typeof(ResearchController), "CurrentLaw");

        private static readonly MethodInfo GetLawMethod = AccessTools.Method(typeof(LawsController), "GetLaw");
        private static readonly MethodInfo GetLawsGroupMethod = AccessTools.Method(typeof(LawsController), "GetLawsGroup");

        private static readonly MethodInfo MoodLoadModifiers = AccessTools.Method(typeof(MoodController), "LoadModifiers");
        private static readonly AccessTools.FieldRef<MoodController, List<MoodModifier>> MoodModifiersField =
            AccessTools.FieldRefAccess<MoodController, List<MoodModifier>>("_moodModifiers");

        private static readonly MethodInfo WeatherLoadStates = AccessTools.Method(typeof(WeatherController), "LoadWeatherStatesFromSave");
        private static readonly MethodInfo WeatherLoadState = AccessTools.Method(typeof(WeatherController), "LoadWeatherState");
        private static readonly MethodInfo WeatherSetCurrent = AccessTools.Method(typeof(WeatherController), "SetCurrentWeather");
        private static readonly MethodInfo WeatherSetImmediately = AccessTools.Method(typeof(WeatherController), "SetWeatherImmediately");
        private static readonly MethodInfo WeatherSetDeveloped = AccessTools.Method(typeof(WeatherController), "SetDevelopedForecastEntries");
        private static readonly AccessTools.FieldRef<WeatherController, int> WeatherDayField =
            AccessTools.FieldRefAccess<WeatherController, int>("_currentDay");
        private static readonly AccessTools.FieldRef<WeatherController, float> WeatherPointsField =
            AccessTools.FieldRefAccess<WeatherController, float>("_forecastPoints");
        private static readonly AccessTools.FieldRef<WeatherController, float> WeatherCostField =
            AccessTools.FieldRefAccess<WeatherController, float>("_currentForecastCost");

        /// <summary>Ein Ereignis (event-Feld) eines Spielobjekts von außen auslösen.</summary>
        private static void Fire(object target, string eventName, params object[] args)
        {
            try
            {
                var f = AccessTools.Field(target.GetType(), eventName);
                if (f?.GetValue(target) is Delegate d)
                    d.DynamicInvoke(args);
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"Ereignis {eventName}: {e.Message}");
            }
        }

        // ================================================================ Host

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend || _session.ActiveGuestCount == 0 || !GameAccess.IsGameStarted)
                return;
            _nextSend = now + SendInterval;
            if (now >= _nextWeatherResend)
            {
                _nextWeatherResend = now + WeatherResendInterval;
                _sentSig.Remove(Msg.WeatherState);
            }

            Channel(Msg.ResearchState, ResearchSig, () =>
            {
                var s = new ResearchControllerSerialized();
                s.Serialize(GameAccess.Game<ResearchController>());
                return s;
            });
            Channel(Msg.LawState, LawsSig, () =>
            {
                var s = new LawsControllerSerialized();
                s.Serialize(GameAccess.Game<LawsController>());
                return s;
            });
            Channel(Msg.MoodState, MoodSig, () =>
            {
                var s = new MoodControllerSerialized();
                s.Serialize(GameAccess.Game<MoodController>());
                return s;
            });
            Channel(Msg.WeatherState, WeatherSig, () =>
            {
                var s = new WeatherControllerSerialized();
                s.Serialize(GameAccess.Game<WeatherController>());
                return s;
            }, extra: w => w.Write(WeatherDayField(GameAccess.Game<WeatherController>())));
        }

        private void Channel(Msg msg, Func<string> sigFunc, Func<object> build, Action<BinaryWriter> extra = null)
        {
            try
            {
                string sig = sigFunc();
                if (sig == null || (_sentSig.TryGetValue(msg, out var last) && last == sig))
                    return;
                string json = JsonConvert.SerializeObject(build(), GameJson.DefaultSettings);
                _sentSig[msg] = sig;
                _session.BroadcastToGuests(Protocol.Build(msg, w =>
                {
                    w.Write(json);
                    extra?.Invoke(w);
                }));
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"{msg} nicht gesendet: {e.Message}");
            }
        }

        private static string ResearchSig()
        {
            var c = GameAccess.Game<ResearchController>();
            if (c == null)
                return null;
            var sb = new StringBuilder();
            sb.Append(R(c.GatheredScientificMaterials, 0.5f)).Append('|')
              .Append(c.CurrentTechnology != null ? c.CurrentTechnology.GetID().ToString() : "-").Append('|')
              .Append(c.CurrentLaw != null ? c.CurrentLaw.GetID().ToString() : "-").Append('|');
            var s = new ResearchControllerSerialized();
            s.Serialize(c);
            foreach (var t in s.ResearchTypes) sb.Append(t.IsUnlocked ? '1' : '0');
            sb.Append('|');
            foreach (var r in s.Researches)
            {
                float total = c.GetResearch(r.ResearchID)?.GetTotalResearchTime() ?? 1f;
                sb.Append((int)r.State).Append(r.WasPayed ? 'p' : 'n').Append(r.CompletedCount)
                  .Append(':').Append(R(r.ResearchTime, Mathf.Max(0.001f, total / 1000f))).Append(',');
            }
            return sb.ToString();
        }

        private static string LawsSig()
        {
            var c = GameAccess.Game<LawsController>();
            if (c == null)
                return null;
            var s = new LawsControllerSerialized();
            s.Serialize(c);
            var sb = new StringBuilder();
            foreach (var l in s.Laws) sb.Append(l.IsEnabled ? '1' : '0');
            sb.Append('|');
            foreach (var g in s.LawsGroups) sb.Append(g.IsUnlocked ? '1' : '0');
            sb.Append('|').Append(s.LawChangedToday).Append(s.GatheringStarted).Append(s.GatheringFinished)
              .Append(R(s.GatheringElapsedTime, Mathf.Max(1f, TimeController.HourLengthInGts / 10f)));
            return sb.ToString();
        }

        private static string MoodSig()
        {
            var c = GameAccess.Game<MoodController>();
            if (c == null)
                return null;
            var s = new MoodControllerSerialized();
            s.Serialize(c);
            var sb = new StringBuilder();
            sb.Append(R(s.CurrentMood, 0.1f)).Append('|');
            if (s.MoodModifiers != null)
                foreach (var m in s.MoodModifiers)
                    sb.Append(m.moodModifierID).Append(':').Append(R(m.ModifierValue, 0.1f)).Append(',');
            return sb.ToString();
        }

        private static string WeatherSig()
        {
            var c = GameAccess.Game<WeatherController>();
            if (c == null)
                return null;
            var s = new WeatherControllerSerialized();
            s.Serialize(c);
            var sb = new StringBuilder();
            sb.Append(WeatherDayField(c)).Append('|').Append(s.DevelopedForecastEntries).Append('|').Append(R(s.ForecastPoints, 1f))
              .Append('|').Append(s.WeatherHistory?.Count ?? 0).Append('|');
            if (s.WeatherForecast != null)
                foreach (var w in s.WeatherForecast)
                    sb.Append(R(w.Temperature, 0.1f)).Append(R(w.Clouds, 0.01f)).Append(R(w.Precipitation, 0.01f)).Append(',');
            return sb.ToString();
        }

        // Host: Wünsche des Gastes

        public void HostOnResearchRequest(Peer from, BinaryReader r)
        {
            var id = (ResearchID)r.ReadInt32();
            bool cancel = r.ReadBoolean();
            var c = GameAccess.Game<ResearchController>();
            if (c == null || c.GetResearch(id) == null)
                return;
            try
            {
                (cancel ? ResearchCancel : ResearchButtonClick)?.Invoke(c, new object[] { id });
                RequestsDone++;
                Plugin.Log.LogInfo($"IFZCOOP research {(cancel ? "cancel" : "start")} {id} (Wunsch von {from.Name})");
                _session.ShowNotice($"{from.Name}: Forschung {(cancel ? "abgebrochen" : "gestartet")}");
                _session.BroadcastToGuests(Protocol.Build(Msg.Notice, w => w.Write($"{from.Name}: Forschung {(cancel ? "abgebrochen" : "gestartet")}")));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Forschung {id}: {e.InnerException?.Message ?? e.Message}");
            }
        }

        public void HostOnLawRequest(Peer from, BinaryReader r)
        {
            var id = (LawID)r.ReadInt32();
            var c = GameAccess.Game<LawsController>();
            if (c == null)
                return;
            c.SetLaw(id, true);
            RequestsDone++;
            Plugin.Log.LogInfo($"IFZCOOP law {id} (Wunsch von {from.Name})");
            string text = $"{from.Name}: Gesetz „{c.GetLawName(id)}“";
            _session.ShowNotice(text);
            _session.BroadcastToGuests(Protocol.Build(Msg.Notice, w => w.Write(text)));
        }

        // ================================================================ Gast: Wünsche

        /// <summary>Prefix ResearchController.HandleResearchButtonClick / CancelCurrentResearch beim Gast.</summary>
        public bool GuestResearchClick(ResearchID id, bool cancel)
        {
            if (_session.Role != Role.Guest || !_session.GuestControlsLocked || UnitSync.NetApplying)
                return true;
            _session.SendToHost(Protocol.Build(Msg.ResearchRequest, w =>
            {
                w.Write((int)id);
                w.Write(cancel);
            }));
            RequestsSent++;
            _session.ShowNotice(cancel ? "Abbruch an den Host geschickt" : "Forschung an den Host geschickt");
            return false;
        }

        /// <summary>Prefix Law.SetEnabled beim Gast.</summary>
        public bool GuestLawSet(Law law, bool isEnabled)
        {
            if (_session.Role != Role.Guest || !_session.GuestControlsLocked || UnitSync.NetApplying)
                return true;
            if (isEnabled && law?.Draft != null)
            {
                _session.SendToHost(Protocol.Build(Msg.LawRequest, w => w.Write((int)law.Draft.Law)));
                RequestsSent++;
                _session.ShowNotice("Gesetz an den Host geschickt");
            }
            return false;
        }

        // ================================================================ Gast: Stand übernehmen

        public void GuestOnState(Msg msg, BinaryReader r)
        {
            string json = r.ReadString();
            UnitSync.NetApplying = true;
            try
            {
                switch (msg)
                {
                    case Msg.ResearchState:
                        ApplyResearch(JsonConvert.DeserializeObject<ResearchControllerSerialized>(json, GameJson.DefaultSettings));
                        ResearchApplied++;
                        break;
                    case Msg.LawState:
                        ApplyLaws(JsonConvert.DeserializeObject<LawsControllerSerialized>(json, GameJson.DefaultSettings));
                        LawsApplied++;
                        break;
                    case Msg.MoodState:
                        ApplyMood(JsonConvert.DeserializeObject<MoodControllerSerialized>(json, GameJson.DefaultSettings));
                        MoodApplied++;
                        break;
                    case Msg.WeatherState:
                        int day = r.ReadInt32();
                        ApplyWeather(JsonConvert.DeserializeObject<WeatherControllerSerialized>(json, GameJson.DefaultSettings), day);
                        WeatherApplied++;
                        break;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{msg} übernehmen: {e}");
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        private static void ApplyResearch(ResearchControllerSerialized s)
        {
            var c = GameAccess.Game<ResearchController>();
            if (c == null || s == null)
                return;
            if (Mathf.Abs(c.GatheredScientificMaterials - s.GatheredScientificMaterials) > 0.001f)
            {
                // Missionsziele hören mit; deren Abmeldung kann beim Gast scheitern (Ziel kommt ohnehin vom Host)
                try { c.SetScientificMaterialsCount(s.GatheredScientificMaterials); }
                catch (Exception e) { Plugin.Log.LogDebug("Material-Zähler: " + e.Message); }
            }
            foreach (var t in s.ResearchTypes)
            {
                var type = c.GetResearchType(t.ResearchTypeID);
                if (type != null && t.IsUnlocked && !type.IsUnlocked)
                    type.Unlock();
            }

            foreach (var rs in s.Researches)
            {
                var research = c.GetResearch(rs.ResearchID);
                if (research == null)
                    continue;
                if (research.ResearchState != rs.State)
                {
                    if (rs.State == ResearchStates.completed)
                        c.SetResearchState(rs.ResearchID, ResearchStates.completed); // Kinder freischalten, Meldungen
                    else
                        research.SetState(rs.State);
                }
                if (research.WasPayed != rs.WasPayed)
                    research.SetPayedState(rs.WasPayed);
                if (research.CompletedCount != rs.CompletedCount)
                    research.SetCompletedCount(rs.CompletedCount);
                if (Mathf.Abs(research.CurrentResearchTime - rs.ResearchTime) > 0.0001f)
                {
                    research.SetResearchTime(rs.ResearchTime);
                    float total = research.GetTotalResearchTime();
                    Fire(c, "OnResearchUpdated", research.Group, rs.ResearchID, total > 0f ? rs.ResearchTime / total : 0f);
                }
            }

            SetCurrent(c, ResearchGroup.technology, c.CurrentTechnology, s.CurrentTechnology, ResearchCurrentTechSetter);
            SetCurrent(c, ResearchGroup.law, c.CurrentLaw, s.CurrentLaw, ResearchCurrentLawSetter);
        }

        private static void SetCurrent(ResearchController c, ResearchGroup group, Research have, ResearchSerialized want, MethodInfo setter)
        {
            ResearchID? haveId = have?.GetID();
            ResearchID? wantId = want?.ResearchID;
            if (haveId == wantId || setter == null)
                return;
            var next = wantId.HasValue ? c.GetResearch(wantId.Value) : null;
            setter.Invoke(c, new object[] { next });
            if (have != null && have.ResearchState != ResearchStates.completed)
                Fire(c, "OnResearchCanceled", group, haveId.Value);
            if (next != null)
            {
                float total = next.GetTotalResearchTime();
                Fire(c, "OnResearchStarted", group, wantId.Value, total > 0f ? next.CurrentResearchTime / total : 0f);
            }
            Plugin.Log.LogInfo($"IFZCOOP research current {group} {(wantId.HasValue ? wantId.Value.ToString() : "-")}");
        }

        private static void ApplyLaws(LawsControllerSerialized s)
        {
            var c = GameAccess.Game<LawsController>();
            if (c == null || s == null || GetLawMethod == null)
                return;
            // erst ausschalten, dann einschalten (je Gruppe ist eines an)
            foreach (var pass in new[] { false, true })
                foreach (var ls in s.Laws.Where(l => l.IsEnabled == pass))
                {
                    var law = GetLawMethod.Invoke(c, new object[] { ls.LawId }) as Law;
                    if (law != null && law.IsEnabled != ls.IsEnabled)
                    {
                        law.SetEnabled(ls.IsEnabled);
                        Plugin.Log.LogInfo($"IFZCOOP law {ls.LawId}={ls.IsEnabled}");
                    }
                }
            foreach (var gs in s.LawsGroups)
            {
                var group = GetLawsGroupMethod?.Invoke(c, new object[] { gs.LawsGroupId }) as LawsGroup;
                if (group != null && group.IsUnlocked != gs.IsUnlocked)
                    group.SetUnlock(gs.IsUnlocked);
            }
            var tr = Traverse.Create(c);
            bool wasStarted = tr.Field<bool>("_gatheringStarted").Value;
            tr.Field<bool>("_lawChangedToday").Value = s.LawChangedToday;
            tr.Field<bool>("_gatheringStarted").Value = s.GatheringStarted;
            tr.Field<bool>("_gatheringFinished").Value = s.GatheringFinished;
            tr.Field<float>("_gatheringElapsedTime").Value = s.GatheringElapsedTime;
            if (wasStarted != s.GatheringStarted)
                Fire(c, "OnGatherngStateChanged", s.GatheringStarted);
        }

        private static void ApplyMood(MoodControllerSerialized s)
        {
            var c = GameAccess.Game<MoodController>();
            if (c == null || s == null || s.MoodModifiers == null || MoodLoadModifiers == null)
                return;
            var list = MoodModifiersField(c);
            if (list == null)
                return;
            foreach (var m in list) m?.Dispose();
            list.Clear();
            MoodLoadModifiers.Invoke(c, new object[] { s.MoodModifiers });
            // Der Wert des Hosts gilt (er rechnet nicht bei jeder Änderung neu, die Summe kann kurz abweichen)
            var moodField = AccessTools.Field(typeof(MoodController), "_currentMood");
            if (moodField != null)
            {
                moodField.SetValue(moodField.IsStatic ? null : c, s.CurrentMood);
                AccessTools.Method(typeof(MoodController), "InvokeRecalculateActions")?.Invoke(c, null);
            }
            Fire(c, "OnAddedRemovedModifier");
        }

        private static void ApplyWeather(WeatherControllerSerialized s, int day)
        {
            var c = GameAccess.Game<WeatherController>();
            if (c == null || s == null || WeatherLoadStates == null)
                return;
            if (s.WeatherForecast != null)
                WeatherLoadStates.Invoke(c, new object[] { s.WeatherForecast, c.Forecast });
            if (s.WeatherHistory != null)
                WeatherLoadStates.Invoke(c, new object[] { s.WeatherHistory, c.History });
            WeatherPointsField(c) = s.ForecastPoints;
            WeatherCostField(c) = s.CurrentForecastCost;
            if (c.DevelopedForecastEntries != s.DevelopedForecastEntries)
                WeatherSetDeveloped?.Invoke(c, new object[] { s.DevelopedForecastEntries });
            if (WeatherDayField(c) != day && s.CurrentWeather != null)
            {
                // neuer Tag beim Host: Wetter wie beim Laden setzen (der eigene Tageswechsel des Gastes ist gesperrt)
                WeatherDayField(c) = day;
                var state = WeatherLoadState.Invoke(c, new object[] { s.CurrentWeather });
                WeatherSetCurrent?.Invoke(c, new[] { state });
                WeatherSetImmediately?.Invoke(c, new[] { state });
                Plugin.Log.LogInfo($"IFZCOOP weather day {day}");
            }
        }

        /// <summary>Für die Statusdatei.</summary>
        public static IEnumerable<string> StatusLines()
        {
            var inv = CultureInfo.InvariantCulture;
            var rc = GameAccess.Game<ResearchController>();
            if (rc != null)
            {
                var s = new ResearchControllerSerialized();
                s.Serialize(rc);
                int done = s.Researches.Count(x => x.State == ResearchStates.completed);
                int unlocked = s.Researches.Count(x => x.State == ResearchStates.unlocked);
                string cur = rc.CurrentTechnology != null
                    ? rc.CurrentTechnology.GetID() + "@" + (rc.CurrentTechnology.CurrentResearchTime / Mathf.Max(0.001f, rc.CurrentTechnology.GetTotalResearchTime())).ToString("0.00", inv)
                    : "-";
                yield return $"research sci={rc.GatheredScientificMaterials.ToString("0.#", inv)} current={cur} law={(rc.CurrentLaw != null ? rc.CurrentLaw.GetID().ToString() : "-")} completed={done} unlocked={unlocked} types={s.ResearchTypes.Count(x => x.IsUnlocked)}";
            }
            var lc = GameAccess.Game<LawsController>();
            if (lc != null)
            {
                var s = new LawsControllerSerialized();
                s.Serialize(lc);
                yield return $"laws on={string.Join(",", s.Laws.Where(x => x.IsEnabled).Select(x => x.LawId.ToString()))} groups={s.LawsGroups.Count(x => x.IsUnlocked)} changedToday={s.LawChangedToday} gathering={s.GatheringStarted}";
            }
            var mc = GameAccess.Game<MoodController>();
            if (mc != null)
            {
                var s = new MoodControllerSerialized();
                s.Serialize(mc);
                yield return $"mood={MoodController.CurrentMood.ToString("0.0", inv)} level={mc.CurrentMoodLevel} modifiers={s.MoodModifiers?.Count ?? 0}";
            }
            var wc = GameAccess.Game<WeatherController>();
            if (wc != null)
            {
                var s = new WeatherControllerSerialized();
                s.Serialize(wc);
                string next = s.WeatherForecast != null ? string.Join(",", s.WeatherForecast.Take(4).Select(x => x.Temperature.ToString("0", inv))) : "";
                yield return $"weather day={WeatherDayField(wc)} temp={s.CurrentWeather?.Temperature.ToString("0.0", inv)} forecast={s.WeatherForecast?.Count} next={next} history={s.WeatherHistory?.Count} entries={s.DevelopedForecastEntries} points={s.ForecastPoints.ToString("0.0", inv)}";
            }
        }
    }
}
