using System.Collections.Generic;
using UnityEngine;

namespace IfzCoop.Menu
{
    /// <summary>
    /// Wo liegen gerade die IMGUI-Fenster der Mod (F7, Statistik, Farbrad, F8, altes Koop-Fenster)? Das Spiel kennt sie
    /// nicht und gab Klicks/Kartenziehen darunter an die Karte weiter (Bericht 2026-11-07). UnitPatches meldet dem Spiel
    /// über GameCursor „Maus ist über der Oberfläche“, solange die Maus über einem dieser Fenster ist.
    /// Jedes Fenster meldet sich in seinem Draw mit Report(id, rect); gilt für die nächsten paar Bilder.
    /// </summary>
    internal static class ModUi
    {
        private static readonly Dictionary<int, (Rect rect, int frame)> Windows = new Dictionary<int, (Rect, int)>();

        public static void Report(int id, Rect rect)
        {
            Windows[id] = (rect, Time.frameCount);
        }

        // Eigenes Sperr-Bit (nicht PointerOverUi = 4, das schaltet das Spiel selbst per XOR um)
        private const Cameras.GameCameraInput.BlockReason ModBlock = (Cameras.GameCameraInput.BlockReason)0x10000;
        private static Cameras.GameCameraInput _blockedOn;
        public static int ZoomBlocks { get; private set; }

        /// <summary>Jedes Bild (Plugin.Update): Zoom der Kamera sperren, solange die Maus über einem Mod-Fenster ist.</summary>
        public static void UpdateCameraBlock()
        {
            Cameras.GameCameraInput gci = null;
            try { gci = Cameras.GameCamera.Active != null ? Cameras.GameCamera.Active.Input as Cameras.GameCameraInput : null; }
            catch { }
            bool over = gci != null && MouseOverWindow();
            try
            {
                if (_blockedOn != null && (!over || !ReferenceEquals(_blockedOn, gci)))
                {
                    if ((ReasonOf(_blockedOn) & ModBlock) != 0) _blockedOn.UnblockCameraZoom(ModBlock);
                    _blockedOn = null;
                }
                if (over && _blockedOn == null)
                {
                    if ((ReasonOf(gci) & ModBlock) == 0) gci.BlockCameraZoom(ModBlock);
                    _blockedOn = gci;
                    ZoomBlocks++;
                }
            }
            catch { _blockedOn = null; }
        }

        private static Cameras.GameCameraInput.BlockReason ReasonOf(Cameras.GameCameraInput gci) =>
            HarmonyLib.Traverse.Create(gci).Field("_zoomBlockReason").GetValue<Cameras.GameCameraInput.BlockReason>();

        public static bool MouseOverWindow()
        {
            if (Windows.Count == 0) return false;
            var mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            int now = Time.frameCount;
            foreach (var w in Windows.Values)
                if (now - w.frame <= 3 && w.rect.Contains(mouse))
                    return true;
            return false;
        }
    }
}
