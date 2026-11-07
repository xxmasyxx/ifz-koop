using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using CallSystem.Runtime.DialogueSystem.UI;
using Cameras;
using DialogueSystem.Runtime.Core.UI;
using HarmonyLib;
using IfzCoop.Game;
using UnityEngine;

namespace IfzCoop
{
    /// <summary>
    /// Sperren, die nach Story-Szenen hängen bleiben konnten (Kamera, Funk, Fenster): Ursache beheben und Zustand anzeigen.
    ///
    /// (1) Kamera-Sperren. GameCameraInput sperrt mit „|= Grund“, löst die Bewegungs-, Zoom- und Randscroll-Sperren aber mit
    /// „^= Grund“ (XOR). GameCamera.Unblock*(Grund) prüft vorher nur, ob die Achse überhaupt gesperrt ist. Wird ein Grund
    /// gelöst, der gar nicht gesetzt ist, während ein anderer die Achse sperrt, wird er dadurch GESETZT – und nie wieder
    /// gelöst. Die Gründe überlappen außerdem bitweise (Pause 0x50 / Umgebungskarte 0xC0 / Platzieren 0x480, Ladebild 0xC
    /// enthält PointerOverUi 4). Das ist ein Spielfehler, den der Mod auslöste (StorySync gab 2,5 s nach jeder Story-Kamera
    /// CenterCamera frei, auch wenn es nicht gesetzt war) und den das Spiel selbst bei verschachtelten Fenstern
    /// (CameraMovementBlockerUI) erreicht. Hier: dieselben Methoden, aber „&= ~Grund“. Ein Grund, der nicht gesetzt ist,
    /// wird damit nie mehr gesetzt; ist ein anderer gesetzt, bleibt die Achse gesperrt (jeder Grund hat ein eigenes Bit).
    /// Hängt die Tastatur-Achse, friert GameCameraInput.GetMousePosition die Mausposition ein: Ziehen bewegt nichts,
    /// Doppelklick fährt an die alte Mausstelle, Zoom geht noch – genau das Bild beim Gast.
    ///
    /// (2) Antwortknöpfe. DialogueOption.Show vergisst laufende Ausblend-Tweens (Liste wird ohne Kill geleert),
    /// DialoguePanel.ClearDialogOptions zerstört die Knöpfe ohne Kill → DOTween meldet NullReference an
    /// „DialogueOptionButton(Clone)/Frame“ (Graphic.set_color). Vor dem Zerstören werden alle Tweens der Knöpfe beendet.
    ///
    /// (3) Statuszeilen (Kamera, Pausen, Funk, Fenster) für Tests und für F8.
    /// </summary>
    internal static class UiLocks
    {
        private static readonly AccessTools.FieldRef<GameCameraInput, GameCameraInput.BlockReason> MouseRef =
            AccessTools.FieldRefAccess<GameCameraInput, GameCameraInput.BlockReason>("_movementMouseBlockReason");
        private static readonly AccessTools.FieldRef<GameCameraInput, GameCameraInput.BlockReason> KeysRef =
            AccessTools.FieldRefAccess<GameCameraInput, GameCameraInput.BlockReason>("_movementKeyboardBlockReason");
        private static readonly AccessTools.FieldRef<GameCameraInput, GameCameraInput.BlockReason> ZoomRef =
            AccessTools.FieldRefAccess<GameCameraInput, GameCameraInput.BlockReason>("_zoomBlockReason");
        private static readonly AccessTools.FieldRef<GameCameraInput, GameCameraInput.BlockReason> EdgeRef =
            AccessTools.FieldRefAccess<GameCameraInput, GameCameraInput.BlockReason>("_edgeScrollingReason");
        private static readonly AccessTools.FieldRef<GameCameraInput, GameCameraInput.BlockReason> RotateRef =
            AccessTools.FieldRefAccess<GameCameraInput, GameCameraInput.BlockReason>("_rotateKeyboardBlockReason");

        public static int UnblockCalls { get; private set; }
        public static int XorPrevented { get; private set; }
        public static string LastXor { get; private set; } = "-";
        public static int TweensKilled { get; private set; }
        public static int PatchesOk { get; private set; }

        private static MethodInfo _doTweenKill;

        public static void Apply(Harmony harmony)
        {
            var reason = new[] { typeof(GameCameraInput.BlockReason) };
            P(harmony, typeof(GameCamera), "UnblockCameraInputMovementKeyboard", reason, nameof(UnblockKeysPrefix));
            P(harmony, typeof(GameCamera), "UnblockCameraInputMovementMouse", reason, nameof(UnblockMousePrefix));
            P(harmony, typeof(GameCamera), "UnblockCameraZoomMovement", reason, nameof(UnblockZoomPrefix));
            P(harmony, typeof(GameCamera), "UnblockEdgeScrolling", reason, nameof(UnblockEdgePrefix));
            P(harmony, typeof(DialoguePanel), "ClearDialogOptions", Type.EmptyTypes, nameof(ClearOptionsPrefix));
            try
            {
                var t = AccessTools.TypeByName("DG.Tweening.DOTween");
                _doTweenKill = t == null ? null : AccessTools.Method(t, "Kill", new[] { typeof(object), typeof(bool) });
            }
            catch (Exception e) { Plugin.Log.LogWarning("DOTween.Kill nicht gefunden: " + e.Message); }
            Plugin.Log.LogInfo($"UI-Sperren: {PatchesOk} Haken gesetzt, DOTween.Kill {(_doTweenKill != null ? "da" : "fehlt")}");
        }

        private static void P(Harmony harmony, Type type, string name, Type[] args, string prefix)
        {
            try
            {
                var m = AccessTools.Method(type, name, args);
                if (m == null) { Plugin.Log.LogWarning($"IFZCOOP UI-Haken fehlt: {type.Name}.{name}"); return; }
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(UiLocks), prefix));
                PatchesOk++;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"IFZCOOP UI-Haken {type.Name}.{name}: {e.Message}"); }
        }

        // ------------------------------------------------------------- (1) Kamera-Sperren richtig lösen

        // GameCamera.UnblockCameraInputMovementKeyboard(BlockReason reason)
        private static bool UnblockKeysPrefix(GameCamera __instance, GameCameraInput.BlockReason reason) => Unblock(__instance, reason, KeysRef, "tasten", false);
        // GameCamera.UnblockCameraInputMovementMouse(BlockReason reason)
        private static bool UnblockMousePrefix(GameCamera __instance, GameCameraInput.BlockReason reason) => Unblock(__instance, reason, MouseRef, "maus", false);
        // GameCamera.UnblockCameraZoomMovement(BlockReason reason)
        private static bool UnblockZoomPrefix(GameCamera __instance, GameCameraInput.BlockReason reason) => Unblock(__instance, reason, ZoomRef, "zoom", false);
        // GameCamera.UnblockEdgeScrolling(BlockReason reason)
        private static bool UnblockEdgePrefix(GameCamera __instance, GameCameraInput.BlockReason reason) => Unblock(__instance, reason, EdgeRef, "rand", true);

        private static bool Unblock(GameCamera cam, GameCameraInput.BlockReason reason,
            AccessTools.FieldRef<GameCameraInput, GameCameraInput.BlockReason> field, string axis, bool edge)
        {
            try
            {
                if (!(cam.Input is GameCameraInput input))
                    return true;
                var current = field(input);
                if (current == GameCameraInput.BlockReason.None)
                    return false; // wie das Spiel: nicht gesperrt → nichts zu tun
                var next = current & ~reason;
                UnblockCalls++;
                if ((current ^ reason) != next)
                {
                    XorPrevented++;
                    LastXor = $"{axis}:{(int)current:x}-{(int)reason:x}";
                    if (XorPrevented <= 30)
                        Plugin.Log.LogInfo($"IFZCOOP Kamera-Sperre {axis}: Spiel hätte {(int)current:x} ^ {(int)reason:x} = {(int)(current ^ reason):x} gesetzt, jetzt {(int)next:x}");
                }
                field(input) = next;
                if (edge && next == GameCameraInput.BlockReason.None)
                    cam.UnblockMoveByEdge();
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("Kamera-Sperre lösen: " + e.Message);
                return true;
            }
        }

        // ------------------------------------------------------------- (2) Tweens der Antwortknöpfe

        // DialoguePanel.ClearDialogOptions() – vor dem Destroy aller Knöpfe
        private static void ClearOptionsPrefix(List<DialogueOption> ____activeDialogueOptions)
        {
            if (_doTweenKill == null || ____activeDialogueOptions == null)
                return;
            try
            {
                foreach (var option in ____activeDialogueOptions)
                {
                    if (option == null)
                        continue;
                    // DOFade setzt die Graphic als Ziel; nach Ziel beenden trifft auch die Tweens, die Show vergessen hat
                    foreach (var c in option.GetComponentsInChildren<Component>(true))
                        if (c != null)
                            TweensKilled += (int)_doTweenKill.Invoke(null, new object[] { c, false });
                }
            }
            catch (Exception e) { Plugin.Log.LogDebug("Knopf-Tweens beenden: " + e.Message); }
        }

        // ------------------------------------------------------------- (3) Zustand

        private static GameCameraInput CamInput() => GameCamera.Active?.Input as GameCameraInput;

        /// <summary>Kamera: Sperrgründe je Achse (hex), Folgen, Fahrt, Ziehen, Grenzen, Umgebungskarte, eingefrorene Maus.</summary>
        public static string CameraLine()
        {
            var inv = CultureInfo.InvariantCulture;
            var cam = GameCamera.Active;
            var input = CamInput();
            if (cam == null || input == null)
                return "uicam none=1";
            var t = Traverse.Create(cam);
            var mouse = MouseRef(input); var keys = KeysRef(input);
            bool movable = mouse == GameCameraInput.BlockReason.None && keys == GameCameraInput.BlockReason.None;
            var followed = t.Field("_followedObject").GetValue<Transform>();
            var recent = Traverse.Create(input).Field("_recentMousePosition").GetValue<Vector2>();
            var b = cam.Bounds;
            bool exp = false;
            try { exp = global::Expedition.ExpeditionView.Active; } catch { }
            return $"uicam movable={(movable ? 1 : 0)} mouse={(int)mouse:x} keys={(int)keys:x} zoom={(int)ZoomRef(input):x} rotate={(int)RotateRef(input):x} edge={(int)EdgeRef(input):x}"
                + $" follow={(followed != null ? followed.name : "-")} moving={(cam.IsMovingToCustomPosition ? 1 : 0)}"
                + $" drag={(t.Field("_isDraggingEnable").GetValue<bool>() ? 1 : 0)}{(t.Field("_isAllDraggingEnable").GetValue<bool>() ? 1 : 0)}"
                + $" expedition={(exp ? 1 : 0)} bounds={b.xMin.ToString("0", inv)},{b.yMin.ToString("0", inv)},{b.xMax.ToString("0", inv)},{b.yMax.ToString("0", inv)}"
                + $" pos={cam.transform.position.x.ToString("0", inv)},{cam.transform.position.z.ToString("0", inv)} zoomY={cam.Zoom.ToString("0", inv)}"
                + $" mouseFrozen={(keys != GameCameraInput.BlockReason.None && (recent - (Vector2)Input.mousePosition).sqrMagnitude > 1f ? 1 : 0)} timeScale={Time.timeScale.ToString("0.##", inv)}";
        }

        /// <summary>Pausen-Stapel, Zeitsperren, aktiver Tastatur-Controller.</summary>
        public static string PauseLine()
        {
            var setter = GameAccess.SpeedSetter;
            var pc = setter?.PauseController;
            int stack = -1;
            try { stack = (Traverse.Create(pc).Field("_appliedPauses").GetValue() as System.Collections.ICollection)?.Count ?? -1; } catch { }
            string kb = "-";
            try { kb = GameAccess.Game<GameInput.KeyboardControllersManager>()?.KeyboardController?.GetType().Name ?? "-"; } catch { }
            return $"uipause gameplay={B(pc?.IsGameplayPaused)} popup={B(pc?.IsPopupPaused)} ui={B(pc?.IsUIPaused)} stack={stack}"
                + $" timeLocked={B(setter?.IsGameplayTimeChangingLocked)} speedLocked={B(setter?.IsSpeedChangingLocked)}"
                + $" speed={Controllers.Time.TimeController.SpeedMultiplier.ToString("0.##", CultureInfo.InvariantCulture)} keyboard={kb}";
        }

        private static TransmissionSystem.TransmissionManager Tm()
        {
            try { return GameAccess.Game<TransmissionSystem.TransmissionManager>() ?? UnityEngine.Object.FindObjectOfType<TransmissionSystem.TransmissionManager>(); }
            catch { return null; }
        }

        internal static DecisionPopup.DecisionPopupController Popups()
        {
            // Nicht vor dem Spielstart: Ein Resolve im Projekt-Container während der Startszene (QA-Statusdatei jede Sekunde)
            // hielt das Spiel bei „Waiting for Emblems…“ fest (schwarzes Bild beim Start mit -ifzcoop-*-Optionen).
            if (!GameAccess.IsGameStarted)
                return null;
            try
            {
                return GameAccess.Game<DecisionPopup.DecisionPopupController>()
                       ?? GameAccess.Project<DecisionPopup.DecisionPopupController>()
                       ?? UnityEngine.Object.FindObjectOfType<DecisionPopup.DecisionPopupController>();
            }
            catch { return null; }
        }

        /// <summary>Funk-Belegung (ActiveDialogueId) und Funk-Pause.</summary>
        public static string RadioLine()
        {
            var tm = Tm();
            if (tm == null)
                return "uiradio none=1";
            bool paused = Traverse.Create(tm).Field("_isPaused").GetValue<bool>();
            return $"uiradio active={tm.ActiveDialogueId ?? "-"} free={(tm.ActiveDialogueId == null ? 1 : 0)} dialog={B(tm.IsDialogueActive)} starting={B(tm.IsTransmissionStarting)}"
                + $" paused={B(paused)} queue={tm._transmissionQueue.Count}";
        }

        /// <summary>Entscheidungs-Fenster (DecisionPopupController) und die Esc-Knöpfe „Hauptmenü“/„Verlassen“.</summary>
        public static string PopupLine()
        {
            var pc = Popups();
            string popup = "none=1";
            if (pc != null)
            {
                var t = Traverse.Create(pc);
                var win = t.Field("decisionPopupWindow").GetValue<Component>();
                popup = $"active={B(t.Field("_isRecentPopupActive").GetValue<bool>())} locked={B(t.Field("_isLocked").GetValue<bool>())} queue={pc.QueueCount}"
                        + $" window={B(win != null && win.gameObject.activeInHierarchy)} ctrl={B(pc.gameObject.activeInHierarchy)} label={(string.IsNullOrEmpty(pc.CurrentContainerLabel) ? "-" : pc.CurrentContainerLabel.Replace(' ', '_'))}";
            }
            string esc = "";
            try
            {
                var canvas = GameAccess.Game<UI.EscMenuCanvas>();
                if (canvas != null)
                {
                    var t = Traverse.Create(canvas);
                    esc = $" escOpen={B(t.Field("panelToShow").GetValue<GameObject>()?.activeSelf)}"
                          + $" menuBtn={B(t.Field("exitToMenuBtn").Property("Enabled").GetValue<bool>())} exitBtn={B(t.Field("exitBtn").Property("Enabled").GetValue<bool>())}";
                }
            }
            catch (Exception e) { esc = " escErr=" + e.GetType().Name; }
            bool saving = false;
            try { saving = UI.EscMenu.SaveWindow.ManualSaveIsRunning || global::AutoSaveController.IsRunnig; } catch { }
            return "uipopup " + popup + esc + " gameSaving=" + B(saving);
        }

        public static string FixLine() =>
            $"uifix patches={PatchesOk} unblockCalls={UnblockCalls} xorPrevented={XorPrevented} lastXor={LastXor} tweensKilled={TweensKilled} menuLockFreed={MenuLockFreed}";

        /// <summary>Wie oft eine aus dem Hauptmenü übrig gebliebene Fenster-Sperre im Spiel gelöst wurde.</summary>
        public static int MenuLockFreed { get; private set; }

        /// <summary>
        /// Ursache „Esc → Hauptmenü/Verlassen tut nichts“: Beim Programmstart sperrt das Willkommensfenster im Hauptmenü
        /// (WelcomePopupController.StartShow → DecisionPopupController.Lock) alle Entscheidungsfenster, bis man dort OK drückt.
        /// Wird vorher ein Spiel geladen (Koop-Einladung, Laden, Testmodus), verschwindet das Willkommensfenster mit der Szene,
        /// ohne Unlock – der DecisionPopupController lebt im Projekt-Kontext weiter und reiht ab da jedes Fenster nur noch ein.
        /// Im laufenden Spiel sperrt sonst niemand diese Fenster, also wird eine solche Sperre hier gelöst (1×/s aus CameraRescue.Tick).
        /// </summary>
        public static void FreeStaleMenuLock()
        {
            if (!GameAccess.IsGameStarted)
                return;
            var pc = Popups();
            if (pc == null || !Traverse.Create(pc).Field("_isLocked").GetValue<bool>())
                return;
            var welcome = UnityEngine.Object.FindObjectOfType<WelcomePopupController>();
            if (welcome != null && welcome.IsActive)
                return;
            // Was während der Sperre eingereiht wurde, stammt aus dem Hauptmenü (z. B. „Karte nach Standort vorschlagen?“),
            // seine Knöpfe zeigen auf Objekte der Menü-Szene: verwerfen statt im Spiel anzeigen.
            int dropped = pc.QueueCount;
            try { Traverse.Create(pc).Field("_decisionPopupsQueue").Method("Clear").GetValue(); } catch { }
            pc.Unlock();
            MenuLockFreed++;
            Plugin.Log.LogWarning($"IFZCOOP Fenster-Sperre aus dem Hauptmenü (Willkommensfenster) gelöst, {dropped} Menü-Fenster verworfen");
        }

        public static IEnumerable<string> StatusLines()
        {
            if (!GameAccess.IsGameStarted)
                yield break; // keine Spiel-Abfragen während Startszene/Menü (siehe Popups)
            yield return Safe(CameraLine, "uicam");
            yield return Safe(PauseLine, "uipause");
            yield return Safe(RadioLine, "uiradio");
            yield return Safe(PopupLine, "uipopup");
            yield return FixLine();
        }

        private static string Safe(Func<string> f, string name)
        {
            try { return f(); } catch (Exception e) { return name + " err=" + e.GetType().Name; }
        }

        private static string B(bool? v) => v == null ? "-" : (v.Value ? "1" : "0");

        // ------------------------------------------------------------- Testbefehle (QaDriver)

        /// <summary>
        /// camxor            – Regressionsprobe: Tastatur-Achse mit PointerOverUi sperren, CenterCamera lösen (wie früher StorySync),
        ///                     PointerOverUi lösen. Mit Fix bleibt nichts hängen; der Ausgangszustand wird wiederhergestellt.
        /// esc open|close    – Esc-Menü öffnen/schließen (UI-Pause, hält den Funk an).
        /// escprobe          – Host: Esc-Menü öffnen, „Hauptmenü“ wie der Knopf (ShowExitToMenuPopup), prüfen ob das Fenster
        ///                     wirklich erscheint (nicht nur eingereiht), dann ohne Bestätigen schließen.
        /// </summary>
        public static string Qa(string[] a)
        {
            switch (a[0])
            {
                case "camxor":
                {
                    var cam = GameCamera.Active;
                    var input = CamInput();
                    if (cam == null || input == null) return "keine Kamera";
                    var before = KeysRef(input);
                    cam.BlockCameraInputMovementKeyboard(GameCameraInput.BlockReason.PointerOverUi);
                    cam.UnblockCameraInputMovementKeyboard(GameCameraInput.BlockReason.CenterCamera);
                    cam.UnblockCameraInputMovementKeyboard(GameCameraInput.BlockReason.PointerOverUi);
                    var after = KeysRef(input);
                    KeysRef(input) = before;
                    return $"camxor vorher={(int)before:x} nachher={(int)after:x} hängt={(after != before ? 1 : 0)}";
                }
                case "esc":
                {
                    // esc open|close – Esc-Menü wie die Taste öffnen/schließen (für den Fall „Funk endet während der Gast pausiert“)
                    var sw = GameAccess.Game<Controllers.EscMenuSwitcher>();
                    if (sw == null) return "kein Esc-Menü";
                    if (a.Length > 1 && a[1] == "close") sw.SwitchToGameplay(); else sw.SwitchToEscMenu();
                    return "esc " + (a.Length > 1 ? a[1] : "open") + " -> " + PauseLine();
                }
                case "escprobe":
                {
                    var switcher = GameAccess.Game<Controllers.EscMenuSwitcher>();
                    var canvas = GameAccess.Game<UI.EscMenuCanvas>();
                    var pc = Popups();
                    if (switcher == null || canvas == null || pc == null) return "kein Esc-Menü/Fenster";
                    var panel = Traverse.Create(canvas).Field("panelToShow").GetValue<GameObject>();
                    bool wasOpen = panel != null && panel.activeSelf;
                    if (!wasOpen)
                        switcher.SwitchToEscMenu();
                    bool opened = panel != null && panel.activeSelf;
                    int queueBefore = pc.QueueCount;
                    bool blockedBefore = pc.IsLockedOrHasActivePopup();
                    string btn = Traverse.Create(canvas).Field("exitToMenuBtn").Property("Enabled").GetValue<bool>() ? "1" : "0";
                    AccessTools.Method(typeof(UI.EscMenuCanvas), "ShowExitToMenuPopup").Invoke(canvas, null);
                    var t = Traverse.Create(pc);
                    var win = t.Field("decisionPopupWindow").GetValue<Component>();
                    bool shown = win != null && win.gameObject.activeInHierarchy && t.Field("_isRecentPopupActive").GetValue<bool>()
                                 && pc.CurrentContainerLabel == "ExitToMainMenu";
                    bool queued = pc.QueueCount > queueBefore;
                    if (shown)
                        pc.ResetPopup(); // „Abbrechen“ ohne Aktion
                    if (opened && !wasOpen)
                        switcher.SwitchToGameplay();
                    return $"escprobe opened={(opened ? 1 : 0)} menuBtn={btn} blockedBefore={(blockedBefore ? 1 : 0)} shown={(shown ? 1 : 0)} queued={(queued ? 1 : 0)}";
                }
            }
            return "unbekannt";
        }
    }
}
