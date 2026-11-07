using System;
using System.Linq;
using System.Reflection;
using System.Text;
using Cameras;
using HarmonyLib;
using IfzCoop.Game;
using UnityEngine;

namespace IfzCoop
{
    /// <summary>
    /// Kamera-Rettung (Standard F8, für Host, Gast und Einzelspiel): Wenn sich die Kamera nach einer Story-Szene nicht mehr
    /// verschieben lässt oder Doppelklicks woanders landen. Schreibt zuerst den Zustand der Kamera ins Log (für die Fehlersuche),
    /// dann: Umgebungskarte schließen, Verfolgen beenden, alle Kamera-Sperren lösen und auf das HQ fahren.
    /// Nur Notausgang: die Ursache (XOR-Freigabe der Kamera-Sperren, ausgelöst durch die frühere StorySync-Freigabe) behebt
    /// UiLocks. Bis 0.20.5 las F8 die Sperren über Traverse.Property("Input") – Input ist ein Feld, die Sperren wurden nie gelöst.
    /// </summary>
    internal static class CameraRescue
    {
        public static int Count { get; private set; }
        public static int StuckRadioFixed { get; private set; }
        public static int StuckPopupFixed { get; private set; }

        private static TransmissionSystem.TransmissionManager Tm()
        {
            try { return GameAccess.Game<TransmissionSystem.TransmissionManager>() ?? UnityEngine.Object.FindObjectOfType<TransmissionSystem.TransmissionManager>(); }
            catch { return null; }
        }

        private static DecisionPopup.DecisionPopupController Popups() => UiLocks.Popups();

        /// <summary>Funk „belegt“, aber kein Dialog mehr da: die Sprechblase am Ende wurde abgebrochen und hat den Funk nie freigegeben.</summary>
        private static bool RadioStuck(TransmissionSystem.TransmissionManager tm)
            => tm != null && tm.ActiveDialogueId != null && !tm.IsDialogueActive && !tm.IsTransmissionStarting;

        private static void FreeRadio(TransmissionSystem.TransmissionManager tm, string why)
        {
            string id = tm.ActiveDialogueId;
            tm.ResetActiveTransmission();
            StuckRadioFixed++;
            Plugin.Log.LogWarning($"IFZCOOP Funk hing ({id}) – freigegeben ({why})");
        }

        /// <summary>„Wirklich beenden?“-Fenster usw. kommen nicht, weil das Spiel ein unsichtbares Fenster für offen hält.</summary>
        private static bool FreePopups(string why)
        {
            var pc = Popups();
            if (pc == null) return false;
            var t = Traverse.Create(pc);
            bool active = t.Field("_isRecentPopupActive").GetValue<bool>();
            var win = t.Field("decisionPopupWindow").GetValue<Component>();
            if (!active || (win != null && win.gameObject.activeInHierarchy)) return false;
            t.Field("_isRecentPopupActive").SetValue(false);
            StuckPopupFixed++;
            Plugin.Log.LogWarning("IFZCOOP Fenster-Sperre hing (unsichtbares Fenster) – freigegeben (" + why + ")");
            return true;
        }

        private static PropertyInfo _esCurrent;
        private static MethodInfo _esOver;

        /// <summary>EventSystem.current.IsPointerOverGameObject() per Reflection (kein Verweis auf UnityEngine.UI).</summary>
        private static bool PointerOverUi()
        {
            try
            {
                if (_esCurrent == null)
                {
                    var t = AccessTools.TypeByName("UnityEngine.EventSystems.EventSystem");
                    if (t == null) return false;
                    _esCurrent = AccessTools.Property(t, "current");
                    _esOver = AccessTools.Method(t, "IsPointerOverGameObject", Type.EmptyTypes);
                }
                var es = _esCurrent?.GetValue(null, null);
                return es != null && _esOver != null && (bool)_esOver.Invoke(es, null);
            }
            catch { return false; }
        }

        private static float _nextTick, _radioStuckSince, _camStuckSince;
        private static bool _camStuckLogged;

        /// <summary>Wie oft die Kamera-Diagnose eine hängende Sperre ins Log geschrieben hat (nur Log, keine Freigabe).</summary>
        public static int CamStuckLogged { get; private set; }

        /// <summary>
        /// Diagnose (Host und Gast, keine Freigabe): Ist die Kamera-Bewegung länger als 4 s gesperrt, ohne dass die Maus über
        /// einem Fenster liegt, ein Mausknopf gedrückt ist oder ein Ladebild läuft, steht einmal der ganze Zustand im Log.
        /// </summary>
        private static void CameraDiagnosis(float now)
        {
            var input = GameCamera.Active?.Input as GameCameraInput;
            bool blocked = input != null && (input.IsMovementMouseBlocked || input.IsMovementKeyboardBlocked);
            bool busy = Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2) || SaveHandler.LoadingData
                        || PointerOverUi();
            if (!blocked || busy)
            {
                _camStuckSince = 0f;
                if (!blocked) _camStuckLogged = false;
                return;
            }
            if (_camStuckSince <= 0f) { _camStuckSince = now; return; }
            if (_camStuckLogged || now - _camStuckSince < 4f) return;
            _camStuckLogged = true;
            CamStuckLogged++;
            Plugin.Log.LogWarning("IFZCOOP Kamera-Bewegung gesperrt seit 4 s (nicht über Fenster): " + string.Join(" | ", UiLocks.StatusLines()));
        }

        /// <summary>Jedes Bild aus Plugin.Update: hängender Funk wird nach 20 s von selbst freigegeben.</summary>
        public static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextTick || !GameAccess.IsGameStarted) return;
            _nextTick = now + 1f;
            try { UiLocks.FreeStaleMenuLock(); }
            catch (Exception e) { Plugin.Log.LogDebug("Menü-Sperre: " + e.Message); }
            try { CameraDiagnosis(now); }
            catch (Exception e) { Plugin.Log.LogDebug("Kamera-Diagnose: " + e.Message); }
            try
            {
                var tm = Tm();
                if (!RadioStuck(tm)) { _radioStuckSince = 0f; return; }
                if (_radioStuckSince <= 0f) { _radioStuckSince = now; return; }
                // Nicht automatisch freigeben: ein eingehender Anruf, der noch auf Annehmen wartet, sieht genauso aus.
                // Nur ins Log schreiben (einmal je Minute), freigeben macht F8.
                if (now - _radioStuckSince >= 60f) { _radioStuckSince = 0f; Plugin.Log.LogInfo($"IFZCOOP Funk belegt ohne Dialog seit 60 s: {tm.ActiveDialogueId} (F8 gibt frei)"); }
            }
            catch (Exception e) { Plugin.Log.LogDebug("Funk-Wächter: " + e.Message); }
        }

        public static string Run()
        {
            var sb = new StringBuilder("IFZCOOP Kamera-Rettung: ");
            var cam = GameCamera.Active;
            if (cam == null)
                return "keine Kamera";
            try
            {
                // ---- Zustand fürs Log
                sb.Append("expedition=").Append(SafeBool(() => global::Expedition.ExpeditionView.Active));
                sb.Append(" pos=").Append(cam.transform.position.ToString("F1"));
                object input = cam.Input; // öffentliches Feld von TmsCamera (keine Eigenschaft)
                if (input != null)
                    foreach (var f in new[] { "_movementMouseBlockReason", "_movementKeyboardBlockReason", "_zoomBlockReason", "_rotateKeyboardBlockReason", "_edgeScrollingReason" })
                        try { sb.Append(' ').Append(f.TrimStart('_')).Append('=').Append((int)Traverse.Create(input).Field(f).GetValue<GameCameraInput.BlockReason>()); } catch { }
                for (var t = cam.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    {
                        string n = f.Name.ToLowerInvariant();
                        if (!(n.Contains("follow") || n.Contains("target") || n.Contains("block") || n.Contains("center") || n.Contains("moving")))
                            continue;
                        object v = null;
                        try { v = f.GetValue(cam); } catch { }
                        sb.Append(' ').Append(f.Name).Append('=').Append(v is UnityEngine.Object o ? (o ? o.name : "null(zerstört)") : (v?.ToString() ?? "null"));
                    }
                var tm0 = Tm();
                if (tm0 != null) sb.Append(" funk=").Append(tm0.ActiveDialogueId ?? "-").Append(" dialog=").Append(tm0.IsDialogueActive).Append(" startet=").Append(tm0.IsTransmissionStarting);
                Plugin.Log.LogWarning(sb.ToString());
                Plugin.Log.LogWarning("IFZCOOP Kamera-Rettung Zustand: " + string.Join(" | ", UiLocks.StatusLines()));
                if (RadioStuck(tm0)) FreeRadio(tm0, "F8");
                FreePopups("F8");

                // ---- Retten
                if (SafeBool(() => global::Expedition.ExpeditionView.Active))
                {
                    var view = UnityEngine.Object.FindObjectOfType<global::Expedition.ExpeditionView>();
                    try { view?.CloseExpeditionView(); } catch (Exception e) { Plugin.Log.LogDebug("Umgebungskarte schließen: " + e.Message); }
                }
                try { Traverse.Create(cam).Method("StopFollowingObject").GetValue(); } catch { }
                if (input is GameCameraInput gi && !SaveHandler.LoadingData)
                {
                    gi.SetMovementMouseBlockReason(GameCameraInput.BlockReason.None);
                    gi.SetMovementKeyboardBlockReason(GameCameraInput.BlockReason.None);
                    gi.SetRotateKeyboardBlockReason(GameCameraInput.BlockReason.None);
                    gi.SetZoomBlockReason(GameCameraInput.BlockReason.None);
                    try { Traverse.Create(gi).Field("_edgeScrollingReason").SetValue(GameCameraInput.BlockReason.None); } catch { }
                }
                var hq = Controllers.HqController.MainHeadquarter;
                if (hq != null)
                    cam.CenterOnObjectWithCamZoom(hq.transform.position, blockCamMovement: false);
                Count++;
                return "Kamera zurückgesetzt";
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("IFZCOOP Kamera-Rettung: " + e);
                return "Fehler: " + e.Message;
            }
        }

        private static bool SafeBool(Func<bool> f)
        {
            try { return f(); } catch { return false; }
        }
    }
}
