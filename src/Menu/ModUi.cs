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
