using System;
using System.Reflection;
using Controllers;
using Controllers.Time;
using Gameplay.Units;
using DialogueSystem.Runtime.Core;
using Gameplay.Units.Orders;
using Gameplay.Units.Player;
using HarmonyLib;
using IfzCoop.Sync;
using TransmissionSystem;
using UI.Clock;
using UnityEngine;

namespace IfzCoop
{
    /// <summary>
    /// Harmony-Patches für die Zeitsteuerung.
    ///
    /// Host:  jede Tempoänderung (auch Pause durch Menüs) geht an alle Gäste.
    /// Gast:  das Tempo bestimmt nur der Host. Klicks auf die Tempoknöpfe und die Leertaste
    ///        werden als Anfrage an den Host geschickt; lokale Pausen (Menüs, Popups) ändern
    ///        das Tempo nicht, damit die Spielzeit gleich weiterläuft.
    /// </summary>
    internal static class Patches
    {
        /// <summary>true, solange ein vom Host empfangenes Tempo angewendet wird.</summary>
        internal static bool ApplyingRemote;

        private static float _nextGarageTry, _nextGarageNotice;
        private static readonly AccessTools.FieldRef<Gameplay.Buildings.Building, Gameplay.Buildings.GarageHandler> GarageRef =
            AccessTools.FieldRefAccess<Gameplay.Buildings.Building, Gameplay.Buildings.GarageHandler>("GarageHandler");

        /// <summary>
        /// Prefix CarWorkshopController.HandleRequestCompleted: hat die Werkstatt keine Garage, eine würfeln (wie das Spiel bei
        /// Schnellbau); klappt das nicht, Auftrag liegen lassen (er bleibt fertig und wird ausgeliefert, sobald es eine Garage
        /// gibt – die schickt beim Koop der Gast, der die Werkstatt gebaut hat).
        /// </summary>
        private static bool CarDoneWithoutGaragePrefix(Gameplay.Research.CarWorkshopWork work)
        {
            try
            {
                var b = work?.Building;
                var req = work?.CurrentRequest;
                if (b == null || req == null || req.Type == Gameplay.Buildings.CarWorkshopRequestType.Deconstruction)
                    return true;
                var g = GarageRef(b);
                if (g == null || g.HasGarage)
                    return true;
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now >= _nextGarageTry)
                {
                    _nextGarageTry = now + 5f;
                    try { b.TryGenerateRandomGarage(); } catch { }
                    if (g.HasGarage)
                    {
                        Plugin.Log.LogInfo($"IFZCOOP Werkstatt {b.ID}: Garage fehlte, neu angelegt");
                        return true;
                    }
                }
                if (now >= _nextGarageNotice)
                {
                    _nextGarageNotice = now + 60f;
                    Plugin.Log.LogWarning($"IFZCOOP Werkstatt {b.ID}: Auto fertig, aber keine Garage – wartet");
                    Plugin.Instance?.Session?.ShowNotice("Fahrzeugwerkstatt hat keine Garage: das fertige Auto wartet", 8f);
                }
                return false;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogDebug("Werkstatt ohne Garage: " + e.Message);
                return true;
            }
        }

        public static void Apply(Harmony harmony)
        {
            Patch(harmony,
                Find(typeof(TimeChangerController), nameof(TimeChangerController.SetSpeedMultiplier), typeof(float), typeof(bool)),
                prefix: nameof(SetSpeedPrefix), postfix: nameof(SetSpeedPostfix));

            Patch(harmony,
                Find(typeof(TimeSpeedSetter), nameof(TimeSpeedSetter.TrySetTimeSpeed), typeof(TimeSpeedType)),
                prefix: nameof(TrySetTimeSpeedPrefix));

            Patch(harmony,
                Find(typeof(TimeSpeedSetter), nameof(TimeSpeedSetter.ChangeGameplayPauseState)),
                prefix: nameof(ChangePauseStatePrefix));

            // Gast: Speicherwarnung („zu wenig RAM“) nicht als Popup – sie würde das Laden des Host-Spielstands anhalten
            Patch(harmony,
                Find(typeof(LowMemoryDisplayHandler), nameof(LowMemoryDisplayHandler.HasEnoughRamToNotShowPopup), typeof(Action)),
                prefix: nameof(RamPopupPrefix));

            // Gast: kein eigenes Autospeichern (es pausiert und schreibt Spielstände), Spielende nur vom Host
            Patch(harmony,
                Find(typeof(AutoSaveController), nameof(AutoSaveController.Tick)),
                prefix: nameof(AutoSaveTickPrefix));
            Patch(harmony,
                Find(typeof(GameOverController), nameof(GameOverController.PlayEndgameSequence), typeof(GameOverController.FailReason), typeof(Fraction)),
                prefix: nameof(EndgamePrefix), postfix: nameof(EndgamePostfix));

            // Fahrzeugwerkstatt ohne Garage: fertiges Auto nicht verlieren (sonst Absturz beim Ausliefern)
            Patch(harmony,
                Find(typeof(Gameplay.Units.Workers.CarWorkshopController), "HandleRequestCompleted", typeof(Gameplay.Research.CarWorkshopWork)),
                prefix: nameof(CarDoneWithoutGaragePrefix));

            // Trupp-Befehle
            Patch(harmony,
                Find(typeof(OrdersHandlers), nameof(OrdersHandlers.GiveOrder), typeof(Order), typeof(bool), typeof(bool), typeof(bool), typeof(bool)),
                prefix: nameof(GiveOrderPrefix));

            // Funksprüche
            Patch(harmony,
                Find(typeof(TransmissionManager), nameof(TransmissionManager.StartTransmissionCoroutine), typeof(string), typeof(Transform)),
                prefix: nameof(StartTransmissionPrefix), postfix: nameof(StartTransmissionPostfix));
            Patch(harmony,
                Find(typeof(DialogueManager), nameof(DialogueManager.PlayLine), typeof(DialogueLine), typeof(bool)),
                prefix: nameof(PlayLinePrefix));
            Patch(harmony,
                Find(typeof(DialogueManager), "DialogueLineSelected", typeof(DialogueLine)),
                prefix: nameof(DialogueLineSelectedPrefix), postfix: nameof(DialogueLineSelectedPostfix));
            Patch(harmony,
                Find(typeof(DialogueManager), "AutoCompleteWithAnswer", typeof(DialogueLine)),
                prefix: nameof(AutoCompletePrefix), postfix: nameof(AutoCompletePostfix));
            Patch(harmony,
                Find(typeof(DialogueManager), "InvokeActionOnEnd", typeof(DialogueLine)),
                prefix: nameof(ActionOnEndPrefix), postfix: nameof(ActionOnEndPostfix));
        }

        private static RadioSync Radio => Plugin.Instance?.Session?.Radio;

        // LowMemoryDisplayHandler.HasEnoughRamToNotShowPopup(Action onConfirmWhenNotEnough)
        private static bool RamPopupPrefix(ref bool __result)
        {
            var session = Plugin.Instance?.Session;
            if ((session != null && session.Role == Role.Guest) || QaDriver.IsActive)
            {
                Plugin.Log.LogInfo("Speicherwarnung übersprungen (Koop-Gast/Testmodus)");
                __result = true;
                return false;
            }
            return true;
        }

        // AutoSaveController.Tick()
        private static bool AutoSaveTickPrefix()
        {
            var session = Plugin.Instance?.Session;
            return session == null || !session.GuestControlsLocked;
        }

        // GameOverController.PlayEndgameSequence(FailReason failReason, Fraction fraction)
        private static bool EndgamePrefix(GameOverController.FailReason failReason)
        {
            var session = Plugin.Instance?.Session;
            // Auch während der Gast den Spielstand lädt (GuestState noch nicht InGame) sperren: beim Laden stehen die
            // Bürgerzähler kurz auf 0, das Spiel meldete dann „Die Zone ist gefallen“ (Bericht 2026-11-07 crazydrifter31).
            if (session == null || session.Role != Role.Guest || session.ApplyingGameOver)
                return true;
            Plugin.Log.LogInfo("Spielende beim Gast unterdrückt (kommt vom Host): " + failReason);
            return false;
        }

        private static void EndgamePostfix(GameOverController.FailReason failReason, Fraction fraction, bool __runOriginal)
        {
            if (__runOriginal)
                Plugin.Instance?.Session?.OnGameOverPlayed((int)failReason, (int)fraction);
        }

        // OrdersHandlers.GiveOrder(Order order, bool clearOrders, bool overrideClearByShifting, bool checkLoop, bool clearRelatedData)
        private static void GiveOrderPrefix(OrdersHandlers __instance, Order order, bool clearOrders, bool overrideClearByShifting, bool checkLoop, bool clearRelatedData)
        {
            try
            {
                Plugin.Instance?.Session?.Orders?.OnGiveOrder(__instance, order, clearOrders, overrideClearByShifting, checkLoop, clearRelatedData);
            }
            catch (Exception e)
            {
                // Nie den Befehl des Spielers selbst verhindern.
                Plugin.Log.LogError("Befehl abfangen: " + e);
            }
        }

        // TransmissionManager.StartTransmissionCoroutine(string id, Transform speakerTransform)
        private static bool StartTransmissionPrefix(string id)
        {
            var radio = Radio;
            return radio == null || radio.AllowTransmissionStart(id);
        }

        // Postfix derselben Methode: Host meldet den Start. Früher hing das an SetActiveDialogueId, aber die
        // Ein-Zeilen-Methode wird vom JIT in den Aufrufer eingebaut, der Haken griff nie (Funk kam beim Gast nicht an).
        private static void StartTransmissionPostfix(TransmissionManager __instance, string id)
        {
            if (__instance != null && id != null && __instance.ActiveDialogueId == id)
                Radio?.OnActiveDialogueSet(id);
        }

        // DialogueManager.PlayLine(DialogueLine dialogueLine, bool clearDialogOptions)
        // Gemeldet wird VOR dem Abspielen: PlayLine kann die nächste Zeile oder das Ende des Funkspruchs
        // noch im selben Aufruf auslösen; danach wäre die Reihenfolge falsch bzw. der Funkspruch schon weg.
        private static bool PlayLinePrefix(DialogueManager __instance, DialogueLine dialogueLine)
        {
            var radio = Radio;
            if (radio == null)
                return true;
            if (!radio.AllowPlayLine(__instance, dialogueLine))
                return false;
            radio.OnLinePlaying(__instance, dialogueLine);
            return true;
        }

        // DialogueManager.DialogueLineSelected(DialogueLine dialogueLine) – privat
        private static bool DialogueLineSelectedPrefix(DialogueManager __instance, DialogueLine dialogueLine)
        {
            var radio = Radio;
            return radio == null || radio.AllowAnswer(__instance, dialogueLine);
        }

        private static void DialogueLineSelectedPostfix(DialogueLine dialogueLine)
        {
            Radio?.AfterAnswer(dialogueLine);
        }

        // DialogueManager.InvokeActionOnEnd(DialogueLine dialogueLine) – privat. Beim Gast nur Anzeige-Aktionen.
        private static void ActionOnEndPrefix(DialogueManager __instance, DialogueLine dialogueLine, out string __state)
        {
            __state = null;
            Radio?.FilterActionOnEnd(__instance, dialogueLine, out __state);
        }

        private static void ActionOnEndPostfix(DialogueLine dialogueLine, string __state)
        {
            if (__state != null && dialogueLine != null)
                dialogueLine.ActionOnEnd = __state;
        }

        // DialogueManager.AutoCompleteWithAnswer(DialogueLine answer) – privat
        private static bool AutoCompletePrefix(DialogueManager __instance)
        {
            var radio = Radio;
            return radio == null || radio.AllowAutoComplete(__instance);
        }

        private static void AutoCompletePostfix()
        {
            Radio?.AfterAutoComplete();
        }

        // Ein fehlender Haken muss laut scheitern, nicht still nichts tun.
        private static MethodInfo Find(Type type, string name, params Type[] args)
        {
            var method = AccessTools.Method(type, name, args);
            if (method == null)
                throw new MissingMethodException(type.FullName, name);
            return method;
        }

        private static void Patch(Harmony harmony, MethodInfo target, string prefix = null, string postfix = null)
        {
            harmony.Patch(target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(Patches), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(Patches), postfix));
            Plugin.Log.LogInfo($"Gepatcht: {target.DeclaringType?.Name}.{target.Name}");
        }

        // TimeChangerController.SetSpeedMultiplier(float speed, bool shouldPlaySound)
        private static bool SetSpeedPrefix(float speed)
        {
            var session = Plugin.Instance?.Session;
            if (session != null && session.GuestControlsLocked && !ApplyingRemote)
                return false;
            return true;
        }

        private static void SetSpeedPostfix(float speed)
        {
            Plugin.Instance?.Session?.OnLocalSpeedApplied(speed);
        }

        // TimeSpeedSetter.TrySetTimeSpeed(TimeSpeedType timeSpeedType)
        private static bool TrySetTimeSpeedPrefix(TimeSpeedType timeSpeedType)
        {
            var session = Plugin.Instance?.Session;
            if (session == null || !session.GuestControlsLocked)
                return true;
            session.RequestSpeed(timeSpeedType);
            return false;
        }

        // TimeSpeedSetter.ChangeGameplayPauseState()
        private static bool ChangePauseStatePrefix()
        {
            var session = Plugin.Instance?.Session;
            if (session == null || !session.GuestControlsLocked)
                return true;
            session.RequestTogglePause();
            return false;
        }
    }
}
