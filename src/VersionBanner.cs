using System.Collections.Generic;
using UnityEngine;

namespace IfzCoop
{
    /// <summary>
    /// Großes rotes Banner oben in der Mitte, wenn Host und Gast verschiedene Mod-Versionen haben (beide Seiten).
    /// Bleibt stehen, bis man es wegklickt oder sich alle mit derselben Version verbinden.
    /// </summary>
    internal static class VersionBanner
    {
        private static readonly Dictionary<string, string> _lines = new Dictionary<string, string>();
        private static GUIStyle _box, _title, _text, _close;
        private static bool _hidden;

        public static bool Visible => _lines.Count > 0 && !_hidden;

        /// <summary>Abweichung melden. key = wer (z. B. Name des Gastes oder "host").</summary>
        public static void Mismatch(string key, string host, string guest, string who)
        {
            string line = $"Host: {host}   ·   {who}: {guest}";
            if (_lines.TryGetValue(key, out var old) && old == line)
                return;
            _lines[key] = line;
            _hidden = false;
            Plugin.Log.LogWarning($"IFZCOOP Versionen stimmen nicht: {line}");
        }

        /// <summary>Verbindung mit derselben Version: Eintrag entfernen.</summary>
        public static void Match(string key) => _lines.Remove(key);

        public static void Draw()
        {
            if (!Visible)
                return;
            if (_box == null)
            {
                var bg = new Texture2D(1, 1);
                bg.SetPixel(0, 0, new Color(0.62f, 0.05f, 0.05f, 0.97f));
                bg.Apply();
                _box = new GUIStyle(GUI.skin.box) { padding = new RectOffset(20, 20, 12, 14) };
                _box.normal.background = bg;
                _title = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
                _title.normal.textColor = Color.white;
                _text = new GUIStyle(_title) { fontStyle = FontStyle.Normal };
                _close = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold };
            }
            float scale = Mathf.Clamp(Screen.height / 1080f, 0.8f, 2f);
            _title.fontSize = Mathf.RoundToInt(26f * scale);
            _text.fontSize = Mathf.RoundToInt(17f * scale);
            _close.fontSize = Mathf.RoundToInt(15f * scale);
            float w = Mathf.Min(Screen.width - 40f, 900f * scale);
            float lineH = 26f * scale;
            float h = (54f + 30f + 34f) * scale + _lines.Count * lineH;
            var r = new Rect((Screen.width - w) / 2f, 60f * scale, w, h);
            GUI.Box(r, GUIContent.none, _box);
            float y = r.y + 10f * scale;
            GUI.Label(new Rect(r.x + 10f, y, r.width - 20f, 40f * scale), "!!  VERSIONEN STIMMEN NICHT ÜBEREIN  !!", _title);
            y += 44f * scale;
            foreach (var line in _lines.Values)
            {
                GUI.Label(new Rect(r.x + 10f, y, r.width - 20f, lineH), line, _text);
                y += lineH;
            }
            GUI.Label(new Rect(r.x + 10f, y + 4f * scale, r.width - 20f, 28f * scale),
                "Alle Spieler brauchen dieselbe IFZ-Koop-Version. Im Loader dieselbe Version wählen.", _text);
            if (GUI.Button(new Rect(r.xMax - 40f * scale, r.y + 8f * scale, 30f * scale, 30f * scale), "X", _close))
                _hidden = true;
            Menu.ModUi.Report(0x1F2E60, r);
        }
    }
}
