using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace IfzCoop.Menu
{
    /// <summary>
    /// QA „uidump [Filter] [Tiefe]“: schreibt alle Canvases der aktiven Szenen als Baum nach BepInEx\ifzcoop-uidump.txt
    /// (Name, aktiv, Bauteile, RectTransform, Texte mit Schrift/Größe/Farbe, Bilder mit Sprite/Farbe). Zum Vermessen des
    /// Spielmenüs für den Menü-Umbau (KONZEPT-MENUE.md, Stufe M1).
    /// </summary>
    internal static class UiDump
    {
        public static string Run(string filter, int maxDepth)
        {
            var sb = new StringBuilder();
            var inv = CultureInfo.InvariantCulture;
            var canvases = Resources.FindObjectsOfTypeAll<Canvas>()
                .Where(c => c != null && c.isRootCanvas && c.gameObject.scene.IsValid())
                .OrderBy(c => c.sortingOrder).ToList();
            sb.AppendLine($"Screen {Screen.width}x{Screen.height}, {canvases.Count} Wurzel-Canvases");
            int lines = 0;
            foreach (var c in canvases)
            {
                if (!string.IsNullOrEmpty(filter) && filter != "*" && c.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                sb.AppendLine($"=== Canvas {c.name} aktiv={c.gameObject.activeInHierarchy} mode={c.renderMode} order={c.sortingOrder} scene={c.gameObject.scene.name}");
                var scaler = c.GetComponent<CanvasScaler>();
                if (scaler != null)
                    sb.AppendLine($"    Scaler {scaler.uiScaleMode} ref={scaler.referenceResolution} match={scaler.matchWidthOrHeight.ToString("0.##", inv)} scale={c.scaleFactor.ToString("0.###", inv)}");
                Walk(c.transform, 1, maxDepth, sb, ref lines);
                if (lines > 20000) { sb.AppendLine("… abgeschnitten"); break; }
            }
            string path = Path.Combine(Paths.BepInExRootPath, "ifzcoop-uidump.txt");
            File.WriteAllText(path, sb.ToString());
            return $"{canvases.Count} Canvases, {lines} Zeilen -> {path}";
        }

        private static void Walk(Transform t, int depth, int maxDepth, StringBuilder sb, ref int lines)
        {
            var inv = CultureInfo.InvariantCulture;
            foreach (Transform child in t)
            {
                lines++;
                var go = child.gameObject;
                string ind = new string(' ', depth * 2);
                var comps = go.GetComponents<Component>().Where(x => x != null && !(x is Transform) && !(x is CanvasRenderer))
                    .Select(x => x.GetType().Name).ToArray();
                string rect = "";
                if (child is RectTransform rt)
                    rect = $" anc={V(rt.anchorMin)}-{V(rt.anchorMax)} piv={V(rt.pivot)} pos={V(rt.anchoredPosition)} size={V(rt.sizeDelta)}";
                string extra = "";
                var tmp = go.GetComponent<TMP_Text>();
                if (tmp != null)
                    extra += $" TEXT=\"{Short(tmp.text)}\" font={tmp.font?.name} size={tmp.fontSize.ToString("0.#", inv)} col={C(tmp.color)} style={tmp.fontStyle} align={tmp.alignment}";
                var img = go.GetComponent<Image>();
                if (img != null)
                    extra += $" IMG sprite={img.sprite?.name ?? "-"} col={C(img.color)} type={img.type}";
                sb.AppendLine($"{ind}{(go.activeSelf ? "+" : "-")} {go.name} [{string.Join(",", comps)}]{rect}{extra}");
                if (depth < maxDepth)
                    Walk(child, depth + 1, maxDepth, sb, ref lines);
            }
        }

        private static string V(Vector2 v) => v.x.ToString("0.##", CultureInfo.InvariantCulture) + "," + v.y.ToString("0.##", CultureInfo.InvariantCulture);
        private static string C(Color c) => "#" + ColorUtility.ToHtmlStringRGBA(c);
        private static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\n", "\\n");
            return s.Length > 60 ? s.Substring(0, 60) + "…" : s;
        }
    }
}
