using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace IfzCoop.Menu
{
    /// <summary>
    /// Farbrad für die eigene Spielerfarbe (Wunsch 2026-11-06: „ein RGB-Rad, damit man die Farbe komplett selbst wählen
    /// kann“). Geöffnet aus dem Mehrspieler-Fenster, Zeile „Meine Farbe“ → „Farbrad …“. Zeichnung mit IMGUI wie das
    /// frühere F6-Fenster: Farbton = Winkel, Sättigung = Abstand zur Mitte, Helligkeit mit Regler, dazu Hex-Eingabe.
    /// Gesperrt bleiben nur Grün (= freier Trupp) und sehr blasse/dunkle Farben (= untätiger, grauer Trupp); diese
    /// Bereiche sind im Rad dunkel gestreift. Solange das Fenster offen ist, nimmt die Spiel-Oberfläche keine Klicks an
    /// (EventSystem aus), sonst gingen Klicks durch das Fenster auf die Menü-Knöpfe darunter.
    /// </summary>
    internal static class ColorPickerWindow
    {
        private const int WheelSize = 220;
        private const int WindowId = 0x1F2E3F;

        public static bool Visible { get; private set; }
        public static event Action Applied;
        public static event Action Closed;

        private static Rect _rect = new Rect(0f, 0f, 430f, 330f);
        private static Texture2D _wheel;
        private static float _wheelValue = -1f;
        private static float _value = 1f;
        private static Color _pending = Color.white;
        private static string _hex = "";
        private static string _msg;
        private static EventSystem _disabledEs;

        public static void Open()
        {
            var cur = Sync.PlayerColorSync.ConfiguredColor();
            Color start = cur.HasValue ? (Color)cur.Value : (Plugin.Instance?.Session != null ? Plugin.Instance.Session.PlayerColors.MyColor : Sync.PlayerColorSync.Defaults[0]);
            _pending = start;
            Color.RGBToHSV(_pending, out _, out _, out _value);
            _value = Mathf.Clamp(_value, 0.45f, 1f);
            _hex = ColorUtility.ToHtmlStringRGB(_pending);
            _msg = null;
            _rect.x = (Screen.width - _rect.width) / 2f;
            _rect.y = (Screen.height - _rect.height) / 2f;
            Visible = true;
            BlockUi(true);
        }

        public static void Close()
        {
            Visible = false;
            BlockUi(false);
            try { Closed?.Invoke(); } catch (Exception e) { Plugin.Log.LogDebug("Farbrad zu: " + e.Message); }
        }

        private static void BlockUi(bool block)
        {
            try
            {
                if (block)
                {
                    var es = EventSystem.current;
                    if (es != null && es.enabled)
                    {
                        es.enabled = false;
                        _disabledEs = es;
                    }
                }
                else if (_disabledEs != null)
                {
                    _disabledEs.enabled = true;
                    _disabledEs = null;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("Farbrad Oberfläche: " + e.Message);
            }
        }

        public static void Draw()
        {
            if (!Visible) return;
            _rect = GUI.Window(WindowId, _rect, DrawWindow, "Meine Farbe – Farbrad");
            ModUi.Report(WindowId, _rect);
        }

        /// <summary>Für den Testtreiber: Farbe direkt übernehmen wie mit dem Knopf.</summary>
        public static bool QaApply(string hex, out string why)
        {
            why = null;
            if (!ColorUtility.TryParseHtmlString("#" + hex.TrimStart('#'), out var c)) { why = "keine Farbe"; return false; }
            return Apply(c, out why);
        }

        private static bool Apply(Color c, out string why)
        {
            var s = Plugin.Instance?.Session;
            if (s != null)
            {
                if (!s.PlayerColors.SetMyColor(c, out why)) return false;
            }
            else
            {
                if (!Sync.PlayerColorSync.IsAllowed(c, out why)) return false;
                Plugin.MyColorSetting.Value = ColorUtility.ToHtmlStringRGB(c);
            }
            try { Applied?.Invoke(); } catch (Exception e) { Plugin.Log.LogDebug("Farbrad: " + e.Message); }
            return true;
        }

        private static void EnsureWheel()
        {
            if (_wheel != null && Mathf.Abs(_wheelValue - _value) < 0.01f)
                return;
            if (_wheel == null)
                _wheel = new Texture2D(WheelSize, WheelSize, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            _wheelValue = _value;
            float r = WheelSize / 2f;
            for (int y = 0; y < WheelSize; y++)
                for (int x = 0; x < WheelSize; x++)
                {
                    float dx = (x + 0.5f - r) / r, dy = (y + 0.5f - r) / r;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d > 1f)
                    {
                        _wheel.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                        continue;
                    }
                    float h = Mathf.Repeat(Mathf.Atan2(dy, dx) / (2f * Mathf.PI), 1f);
                    var c = Color.HSVToRGB(h, d, _value);
                    if (!Sync.PlayerColorSync.IsAllowed(c, out _))
                    {
                        bool stripe = ((x + y) / 4) % 2 == 0;
                        c = Color.Lerp(c, Color.black, stripe ? 0.75f : 0.55f);
                    }
                    c.a = Mathf.Clamp01((1f - d) * r);
                    _wheel.SetPixel(x, y, c);
                }
            _wheel.Apply();
        }

        private static void Swatch(Color c, float w, float h)
        {
            var r = GUILayoutUtility.GetRect(w, h, GUILayout.Width(w), GUILayout.Height(h));
            var old = GUI.color;
            GUI.color = Color.black;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = c;
            GUI.DrawTexture(new Rect(r.x + 2f, r.y + 2f, r.width - 4f, r.height - 4f), Texture2D.whiteTexture);
            GUI.color = old;
        }

        private static void Pick(Color c)
        {
            if (Sync.PlayerColorSync.IsAllowed(c, out var why))
            {
                _pending = c;
                _hex = ColorUtility.ToHtmlStringRGB(c);
                _msg = null;
            }
            else
            {
                _msg = "Nicht wählbar: " + why;
            }
        }

        private static void DrawWindow(int id)
        {
            EnsureWheel();
            GUILayout.BeginHorizontal();
            var rect = GUILayoutUtility.GetRect(WheelSize, WheelSize, GUILayout.Width(WheelSize), GUILayout.Height(WheelSize));
            GUI.DrawTexture(rect, _wheel);
            Color.RGBToHSV(_pending, out float ph, out float ps, out _);
            float ang = ph * 2f * Mathf.PI;
            var mark = new Vector2(rect.center.x + Mathf.Cos(ang) * ps * rect.width / 2f, rect.center.y - Mathf.Sin(ang) * ps * rect.height / 2f);
            var oldColor = GUI.color;
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(mark.x - 5f, mark.y - 5f, 10f, 10f), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(mark.x - 3f, mark.y - 3f, 6f, 6f), Texture2D.whiteTexture);
            GUI.color = oldColor;

            GUILayout.BeginVertical();
            GUILayout.Label("Vorschau:");
            Swatch(_pending, 150f, 40f);
            GUILayout.Label("Helligkeit:");
            float nv = GUILayout.HorizontalSlider(_value, 0.45f, 1f, GUILayout.Width(170f));
            if (Mathf.Abs(nv - _value) > 0.001f)
            {
                _value = nv;
                Color.RGBToHSV(_pending, out float h, out float s, out _);
                Pick(Color.HSVToRGB(h, s, _value));
            }
            GUILayout.Space(6f);
            GUILayout.Label("Hex (z. B. 8040FF):");
            string nh = GUILayout.TextField(_hex, 7, GUILayout.Width(100f));
            if (nh != _hex)
            {
                _hex = nh;
                if (nh.TrimStart('#').Length == 6 && ColorUtility.TryParseHtmlString("#" + nh.TrimStart('#'), out var hc))
                {
                    Pick(hc);
                    Color.RGBToHSV(hc, out _, out _, out float hv);
                    _value = Mathf.Clamp(hv, 0.45f, 1f);
                }
            }
            GUILayout.Space(6f);
            GUILayout.Label("Gestreift = nicht wählbar\n(Grün = freier Trupp,\nblass/dunkel = untätig)");
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            var e = Event.current;
            if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && rect.Contains(e.mousePosition))
            {
                float dx = (e.mousePosition.x - rect.center.x) / (rect.width / 2f);
                float dy = -(e.mousePosition.y - rect.center.y) / (rect.height / 2f);
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d <= 1f)
                {
                    float h = Mathf.Repeat(Mathf.Atan2(dy, dx) / (2f * Mathf.PI), 1f);
                    Pick(Color.HSVToRGB(h, d, _value));
                }
                e.Use();
            }

            GUILayout.Label(string.IsNullOrEmpty(_msg) ? " " : _msg);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Übernehmen", GUILayout.Height(28f)))
            {
                if (Apply(_pending, out var why)) Close();
                else _msg = "Nicht wählbar: " + why;
            }
            if (GUILayout.Button("Abbrechen", GUILayout.Height(28f)))
                Close();
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
        }
    }
}
