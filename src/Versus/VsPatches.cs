using System;
using System.Linq;
using Controllers;
using Controllers.Time;
using Controllers.Weather;
using Gameplay.Units.Spawning;
using HarmonyLib;
using UI.Clock;
using UnityEngine;

namespace IfzCoop.Versus
{
    /// <summary>
    /// Alle Versus-Haken (V0/V1). Jeder Prefix beginnt mit „Versus aktiv?“, sonst läuft das Original wie bisher.
    /// Die Koop-Haken an denselben Methoden laufen weiter, liefern aber bei Koop-Rolle None immer true.
    /// Gehakt werden nur Methoden, die über Delegates/Knöpfe/virtuell aufgerufen werden oder lang genug sind (keine Einbettung).
    /// </summary>
    internal static class VsPatches
    {
        public static int Applied { get; private set; }
        public static int Missing { get; private set; }

        /// <summary>Gast: abgewiesene Pause-/Tempo-Klicks.</summary>
        public static int PauseDenied { get; private set; }

        /// <summary>Automatische Pausen (Funk, Handel, Hinweise), die im laufenden Versus-Spiel ausgelassen wurden.</summary>
        public static int PausesSuppressed { get; private set; }

        /// <summary>Host: Pause/Tempo kommt gerade ausdrücklich vom Spieler (Leertaste, Tempo-Knöpfe).</summary>
        private static bool _explicit;
        private static bool _suppressedOpen;

        private static VersusSession Vs => VersusSession.Instance;

        private static void HqStoragePostfix(Gameplay.Buildings.Building __instance) => VsStart.UpdateStoragePostfix(__instance);

        /// <summary>
        /// Versus braucht freie HQ-Wahl. Bei Vorlage-Karten (vorgeschlagene Städte) baut das Spiel das HQ sonst schon beim
        /// Erzeugen der Karte (HqController.ChooseHqForRecommendedPlaces) bzw. schlägt ein festes Gebäude vor
        /// (Map.GetLatLonHq). Gilt, sobald Versus läuft oder der Host in der Mehrspieler-Lobby „Versus“ gewählt hat.
        /// </summary>
        internal static bool FreeHqWanted
        {
            get
            {
                if (VersusSession.Active || VsQa.HasArgs) return true;
                var koop = Plugin.Instance?.Session;
                return koop != null && koop.Role == Role.Host && Menu.LobbySettings.Mode == "versus";
            }
        }

        private static readonly System.Reflection.FieldInfo HqSetterField = AccessTools.Field(typeof(HqController), "_hqSetterController");

        private static bool RecommendedHqPrefix(HqController __instance)
        {
            if (!FreeHqWanted)
                return true;
            Plugin.Log.LogInfo("IFZVS Vorlage-Karte: festes HQ übersprungen, freie HQ-Wahl");
            try { (HqSetterField?.GetValue(__instance) as HqSetterController)?.HandleOnMapGenerated(null); }
            catch (System.Exception e) { Plugin.Log.LogWarning("IFZVS freie HQ-Wahl vorbereiten: " + e.Message); }
            return false;
        }

        /// <summary>
        /// HqController.IsHqPresentInGame meldet bei Vorlage-Städten immer „HQ da“ (das Spiel hat es dort schon beim Erzeugen
        /// der Karte gebaut) → die HQ-Wahl startet nie. Im Versus ohne HQ: „kein HQ“, damit die freie Wahl kommt.
        /// </summary>
        private static void HqPresentPostfix(ref bool __result)
        {
            if (__result && FreeHqWanted && HqController.MainHeadquarter == null)
            {
                __result = false;
                Plugin.Log.LogInfo("IFZVS Vorlage-Karte: HQ-Wahl wird gestartet");
            }
        }

        private static bool ProxyUpdatePrefix(Gameplay.Units.Group __instance) => VsUnits.GroupUpdatePrefix(__instance);
        private static bool ProxyFixedPrefix(Gameplay.Units.Group __instance) => VsUnits.GroupFixedPrefix(__instance);
        /// <summary>Gemeinsamer Sperr-Prefix: Aufruf mit einem fremden Trupp oder fremden Auto als Argument → nicht ausführen.</summary>
        private static bool ProxyImmigrantPrefix(object[] __args)
        {
            if (VsUnits.Applying || VsUnits.Instance == null || __args == null)
                return true;
            foreach (var a in __args)
            {
                if (a is Gameplay.Units.Group g && VsUnits.IsProxy(g)) return false;
                if (a is Gameplay.Vehicles.Vehicle v && VsUnits.IsProxyVehicle(v)) return false;
            }
            return true;
        }

        private static bool ProxyReceiveDamagePrefix(Gameplay.Units.Characters.Character __instance, Gameplay.Units.Equipment.Weapon attackedWith, float dmg,
            Gameplay.Units.Interfaces.IDamageApplier damageApplier, ref bool __result)
            => VsUnits.ReceiveDamagePrefix(__instance, attackedWith, dmg, damageApplier, ref __result);

        private static bool ProxyVehicleDamagePrefix(Gameplay.Vehicles.Vehicle __instance) => VsUnits.VehicleDamagePrefix(__instance);

        private static bool ProxyDamagePrefix(Gameplay.Units.Characters.Character __instance) => VsUnits.DamagePrefix(__instance);

        private static void LatLonHqPostfix(ref Gameplay.Buildings.Building __result)
        {
            if (__result != null && FreeHqWanted)
                __result = null;
        }

        public static void Apply(Harmony harmony)
        {
            // HQ-Wahl (privat, über PopupButtonSettings-Delegate aufgerufen)
            TryPatch(harmony, typeof(HqSetterController), "OnSelectionConfirmed", nameof(ConfirmPrefix), nameof(ConfirmPostfix));
            // Verstecke nicht an Gast-HQs (protected virtual)
            TryPatch(harmony, typeof(HideoutSpawnPointSpawner), "IsToCloseHq", null, nameof(IsToCloseHqPostfix));
            TryPatch(harmony, typeof(Gameplay.Buildings.Building), "UpdateStorage", null, nameof(HqStoragePostfix));
            // Vorlage-Karten: kein vorgegebenes HQ im Versus
            TryPatch(harmony, typeof(HqController), "ChooseHqForRecommendedPlaces", nameof(RecommendedHqPrefix), null);
            TryPatch(harmony, typeof(MapEssentials.Map), "GetLatLonHq", null, nameof(LatLonHqPostfix));
            TryPatch(harmony, typeof(HqController), "IsHqPresentInGame", null, nameof(HqPresentPostfix));
            // fremde Trupps (VsUnits): keine KI, kein Schaden
            TryPatch(harmony, typeof(Gameplay.Units.Group), "CustomUpdate", nameof(ProxyUpdatePrefix), null);
            TryPatch(harmony, typeof(Gameplay.Units.Group), "FixedUpdate", nameof(ProxyFixedPrefix), null);
            TryPatch(harmony, typeof(Gameplay.Units.Characters.Character), "SubtractHp", nameof(ProxyDamagePrefix), null);
            // Stellvertreter (Einwanderer-Vorlage) nie als Einwanderer behandeln: kein Funkspruch, nicht aufnehmen/ablehnen
            TryPatch(harmony, typeof(Gameplay.Units.Characters.Character), "ReceiveDamage", nameof(ProxyReceiveDamagePrefix), null);
            TryPatch(harmony, typeof(Gameplay.Vehicles.Vehicle), "SubtractHp", nameof(ProxyVehicleDamagePrefix), null);
            // fremde Autos lassen sich nicht übernehmen
            TryPatch(harmony, typeof(Gameplay.Units.Movements.GroupMovement), "MoveTo", nameof(ProxyImmigrantPrefix), null);
            TryPatch(harmony, typeof(Gameplay.Units.VehicleHandler), "AssignVehicle", nameof(ProxyImmigrantPrefix), null);
            TryPatch(harmony, typeof(Gameplay.Immigrants.ImmigrantsController), "ImmigrantsSpotted", nameof(ProxyImmigrantPrefix), null);
            TryPatch(harmony, typeof(Gameplay.Immigrants.ImmigrantsController), "AcceptSpecificImmigrants", nameof(ProxyImmigrantPrefix), null);
            TryPatch(harmony, typeof(Gameplay.Immigrants.ImmigrantsController), "DeclineSpecificImmigrant", nameof(ProxyImmigrantPrefix), null);
            TryPatch(harmony, typeof(Gameplay.Immigrants.ImmigrantsController), "ChangeSpecificGroupAffiliation", nameof(ProxyImmigrantPrefix), null);
            TryPatch(harmony, typeof(Gameplay.Buildings.Building), "UpdateProduction", null, nameof(HqStoragePostfix));

            // Tempo und Pause nur vom Host
            TryPatchExact(harmony, typeof(TimeChangerController), nameof(TimeChangerController.SetSpeedMultiplier), new[] { typeof(float), typeof(bool) },
                nameof(SetSpeedPrefix), nameof(SetSpeedPostfix));
            TryPatchExact(harmony, typeof(TimeSpeedSetter), nameof(TimeSpeedSetter.TrySetTimeSpeed), new[] { typeof(TimeSpeedType) },
                nameof(TrySetTimeSpeedPrefix), nameof(ExplicitPostfix));
            TryPatchExact(harmony, typeof(TimeSpeedSetter), nameof(TimeSpeedSetter.ChangeGameplayPauseState), Type.EmptyTypes,
                nameof(ChangePauseStatePrefix), nameof(ExplicitPostfix));

            // Automatische Pausen (Funk, Handel, Hinweisfenster) im laufenden Versus-Spiel aus (VERSUS-TECHNIK 8.4).
            // GameplayPauseLayer ist intern; ApplyPause/RevertPause sind virtuelle Überschreibungen (keine Einbettung).
            var pauseLayer = AccessTools.TypeByName("Controllers.Pausing.GameplayPauseLayer");
            if (pauseLayer != null)
            {
                TryPatch(harmony, pauseLayer, "ApplyPause", nameof(ApplyPausePrefix));
                TryPatch(harmony, pauseLayer, "RevertPause", nameof(RevertPausePrefix));
            }
            else
            {
                Missing++;
                Plugin.Log.LogWarning("IFZVS hook fehlt: GameplayPauseLayer");
            }
            // Esc-Menü (und Speicher-Hinweis) hält beim Host weiter an: Speichern soll nicht bei laufender Uhr passieren
            TryPatch(harmony, typeof(Controllers.Pausing.PauseController), "PauseUI", nameof(PauseUiPrefix), nameof(ExplicitPostfix));

            // Gast: Wetter-Würfel und Tagessprünge vom Host, kein eigenes Autospeichern, keine Speicherwarnung beim Laden
            TryPatch(harmony, typeof(WeatherController), "UpdateForecast", nameof(BlockOnGuest));
            TryPatch(harmony, typeof(AutoSaveController), "Tick", nameof(AutoSaveTickPrefix), nameof(ExplicitPostfix));
            TryPatch(harmony, typeof(LowMemoryDisplayHandler), "HasEnoughRamToNotShowPopup", nameof(RamPopupPrefix));

            Plugin.Log.LogInfo($"IFZVS Versus-Haken: {Applied} gesetzt, {Missing} fehlen");
        }

        private static void TryPatch(Harmony harmony, Type type, string method, string prefix, string postfix = null)
        {
            try
            {
                var targets = AccessTools.GetDeclaredMethods(type)
                    .Where(m => m.Name == method && !m.IsAbstract && !m.ContainsGenericParameters)
                    .ToList();
                if (targets.Count == 0)
                {
                    Missing++;
                    Plugin.Log.LogWarning($"IFZVS hook fehlt: {type.Name}.{method}");
                    return;
                }
                foreach (var target in targets)
                {
                    harmony.Patch(target,
                        prefix: prefix == null ? null : new HarmonyMethod(typeof(VsPatches), prefix),
                        postfix: postfix == null ? null : new HarmonyMethod(typeof(VsPatches), postfix));
                    Applied++;
                }
            }
            catch (Exception e)
            {
                Missing++;
                Plugin.Log.LogWarning($"IFZVS hook {type.Name}.{method} nicht gesetzt: {e.Message}");
            }
        }

        private static void TryPatchExact(Harmony harmony, Type type, string method, Type[] args, string prefix, string postfix)
        {
            try
            {
                var target = AccessTools.Method(type, method, args);
                if (target == null)
                {
                    Missing++;
                    Plugin.Log.LogWarning($"IFZVS hook fehlt: {type.Name}.{method}");
                    return;
                }
                harmony.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(VsPatches), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(VsPatches), postfix));
                Applied++;
            }
            catch (Exception e)
            {
                Missing++;
                Plugin.Log.LogWarning($"IFZVS hook {type.Name}.{method} nicht gesetzt: {e.Message}");
            }
        }

        // ---------------------------------------------------------------- HQ-Wahl

        private static bool ConfirmPrefix(HqSetterController __instance)
        {
            if (!VersusSession.Active)
                return true;
            try
            {
                return Vs.Start.ConfirmPrefix(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("IFZVS HQ-Wahl: " + e);
                return true;
            }
        }

        private static void ConfirmPostfix(HqSetterController __instance, bool __runOriginal)
        {
            if (!VersusSession.Active)
                return;
            try
            {
                Vs.Start.ConfirmPostfix(__instance, __runOriginal);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("IFZVS HQ-Wahl danach: " + e);
            }
        }

        private static void IsToCloseHqPostfix(Gameplay.Buildings.Building building, float distance, ref bool __result)
        {
            if (__result || !VersusSession.IsHostActive || building == null)
                return;
            try
            {
                if (Vs.Start.TooCloseToGuestHq(building.Position, distance))
                    __result = true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("IFZVS Versteck-Abstand: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- Tempo und Pause

        // TimeChangerController.SetSpeedMultiplier(float speed, bool shouldPlaySound)
        private static bool SetSpeedPrefix()
        {
            return !VersusSession.GuestTimeLocked || VsNet.ApplyingSpeed;
        }

        private static void SetSpeedPostfix(float speed, bool __runOriginal)
        {
            if (__runOriginal && VersusSession.IsHostActive)
                Vs.OnLocalSpeedApplied(speed);
        }

        // TimeSpeedSetter.TrySetTimeSpeed(TimeSpeedType)
        private static bool TrySetTimeSpeedPrefix(TimeSpeedSetter __instance, TimeSpeedType timeSpeedType)
        {
            if (VoteInstead(false, timeSpeedType))
                return false;
            return ExplicitPrefix(__instance);
        }

        // TimeSpeedSetter.ChangeGameplayPauseState()
        private static bool ChangePauseStatePrefix(TimeSpeedSetter __instance)
        {
            if (VoteInstead(true, TimeSpeedType.Pause))
                return false;
            return ExplicitPrefix(__instance);
        }

        /// <summary>
        /// Laufendes Versus-Spiel: Pause/Tempo-Klicks sind Stimmen (VsVote). Host zählt selbst, Gast schickt SpeedRequest.
        /// true = Klick wurde zur Stimme, das Original läuft nicht. Das abgestimmte Ergebnis setzt der Host mit VsVote.Applying.
        /// </summary>
        private static bool VoteInstead(bool toggle, TimeSpeedType type)
        {
            if (!VsVote.Enabled || VsVote.Applying)
                return false;
            var vs = Vs;
            if (vs.Role == VsRole.Host)
            {
                if (toggle) vs.Vote.Toggle(0); else vs.Vote.Cast(0, type);
                return true;
            }
            if (VersusSession.GuestTimeLocked)
            {
                vs.SendToHost(VsNet.Build(Net.Msg.SpeedRequest, w =>
                {
                    w.Write((byte)(toggle ? 1 : 0));
                    if (!toggle) w.Write((int)type);
                }));
                return true;
            }
            return false;
        }

        /// <summary>Gast: abweisen. Host: ausdrückliche Pause/Tempo, gilt für alle.</summary>
        private static bool ExplicitPrefix(TimeSpeedSetter setter)
        {
            if (!VersusSession.Active)
                return true;
            if (VersusSession.GuestTimeLocked)
            {
                DenyGuest();
                return false;
            }
            if (!VersusSession.IsHostActive)
                return true;
            // Steht noch eine ausgelassene automatische Pause im Stapel des Spiels (die Uhr läuft aber), zuerst auflösen.
            // Sonst hielte PauseGameplay nicht an (Stapel hat schon eine Spielpause) bzw. die Leertaste beendete nur diese.
            try
            {
                if (_suppressedOpen && setter != null && setter.PauseController.IsGameplayPaused && TimeController.SpeedMultiplier > 0.01f)
                    setter.PauseController.UnpauseGameplay();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("IFZVS Pause auflösen: " + e.Message);
            }
            BeginExplicit();
            return true;
        }

        private static void BeginExplicit() => _explicit = true;

        private static void ExplicitPostfix()
        {
            _explicit = false;
        }

        private static float _nextDenyNotice;

        private static void DenyGuest()
        {
            PauseDenied++;
            if (Time.realtimeSinceStartup >= _nextDenyNotice)
            {
                _nextDenyNotice = Time.realtimeSinceStartup + 3f;
                Vs?.Notice("Im Versus bestimmt nur der Host Pause und Tempo", 3f);
            }
        }

        // GameplayPauseLayer.ApplyPause()
        private static bool ApplyPausePrefix()
        {
            if (!VersusSession.SuppressAutoPause || _explicit)
                return true;
            _suppressedOpen = true;
            PausesSuppressed++;
            return false;
        }

        // GameplayPauseLayer.RevertPause()
        private static bool RevertPausePrefix()
        {
            if (_suppressedOpen && TimeController.SpeedMultiplier > 0.01f && VersusSession.Active)
            {
                // zur ausgelassenen Pause gehörend: Tempo bleibt, wie es ist
                _suppressedOpen = false;
                return false;
            }
            _suppressedOpen = false;
            return true;
        }

        // ---------------------------------------------------------------- Gast-Sperren

        private static bool BlockOnGuest()
        {
            return !VersusSession.GuestTimeLocked || VsNet.Applying;
        }

        // AutoSaveController.Tick(): Gast speichert nicht selbst; beim Host darf das Autospeichern anhalten (PauseGame)
        private static bool AutoSaveTickPrefix()
        {
            if (VersusSession.GuestTimeLocked)
                return false;
            if (VersusSession.IsHostActive)
                BeginExplicit();
            return true;
        }

        // PauseController.PauseUI(): Esc-Menü. Beim Host eine ausdrückliche Pause (gilt für alle).
        private static void PauseUiPrefix(Controllers.Pausing.PauseController __instance)
        {
            if (!VersusSession.IsHostActive)
                return;
            BeginExplicit();
            try
            {
                // Liegt eine ausgelassene automatische Pause im Stapel, legt PauseUI keine Spielpause mehr an: jetzt nachholen.
                if (_suppressedOpen && __instance.IsGameplayPaused && TimeController.SpeedMultiplier > 0.01f)
                {
                    _suppressedOpen = false;
                    Game.GameAccess.TimeChanger?.Pause();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("IFZVS Esc-Pause: " + e.Message);
            }
        }

        // LowMemoryDisplayHandler.HasEnoughRamToNotShowPopup(Action)
        private static bool RamPopupPrefix(ref bool __result)
        {
            if (!VersusSession.IsGuestActive)
                return true;
            __result = true;
            return false;
        }
    }
}
