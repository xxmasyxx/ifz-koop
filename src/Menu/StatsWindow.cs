using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;

namespace IfzCoop.Menu
{
    /// <summary>
    /// Statistik je Spieler im Esc-Menü (Knopf „Statistik“ unter „Mehrspieler“). Tabelle aus StatsSync: Getötet, Schaden,
    /// Verluste, Gebaut, Abgerissen, Repariert, Trupps ausgebildet, Befehle, dazu eine Summenzeile. Namen in Spielerfarbe.
    /// IMGUI wie das Farbrad. Liegt die Maus über dem Fenster, nimmt die Spiel-Oberfläche darunter keine Klicks an.
    /// Schließt sich mit dem Esc-Menü.
    /// </summary>
    internal static class StatsWindow
    {
        private const int WindowId = 0x1F2E40;

        public static bool Visible { get; private set; }

        private static Rect _rect = new Rect(0f, 0f, 900f, 300f);
        private static float _width = 900f;
        private static GUIStyle _head, _cell, _name, _title, _box;
        private static Texture2D _bg, _rowBg;
        private static EventSystem _disabledEs;
        private static float _nextMenuCheck;
        private static bool _menuOpen;

        private static readonly string[] Short = { "Getötet", "Schaden", "Verluste", "Gebaut", "Abgerissen", "Repariert", "Trupps", "Befehle" };

        public static void Toggle()
        {
            Visible = !Visible;
            if (Visible)
            {
                _width = Mathf.Min(980f, Screen.width - 40f);
                _rect = new Rect((Screen.width - _width) / 2f, 0f, _width, 300f);
                _rect.y = Screen.height * 0.18f;
                _menuOpen = true;
                _nextMenuCheck = Time.realtimeSinceStartup + 0.5f;
            }
            else
            {
                ReleaseUi();
            }
        }

        public static void Close()
        {
            if (Visible) Toggle();
        }

        private static void ReleaseUi()
        {
            if (_disabledEs != null)
            {
                try { _disabledEs.enabled = true; } catch { }
                _disabledEs = null;
            }
        }

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private static void EnsureStyles()
        {
            if (_cell != null) return;
            _bg = Solid(new Color(0.07f, 0.09f, 0.10f, 0.96f));
            _rowBg = Solid(new Color(1f, 1f, 1f, 0.05f));
            _box = new GUIStyle(GUI.skin.window) { padding = new RectOffset(16, 16, 34, 14) };
            _box.normal.background = _bg;
            _box.onNormal.background = _bg;
            _box.normal.textColor = new Color(0.29f, 0.99f, 0.49f);
            _box.onNormal.textColor = _box.normal.textColor;
            _box.fontSize = 18;
            _box.fontStyle = FontStyle.Bold;
            _head = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
            _head.normal.textColor = new Color(0.70f, 0.70f, 0.70f);
            _cell = new GUIStyle(GUI.skin.label) { fontSize = 17, alignment = TextAnchor.MiddleRight };
            _cell.normal.textColor = Color.white;
            _name = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, richText = true };
            _title = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleLeft };
            _title.normal.textColor = new Color(0.60f, 0.60f, 0.60f);
        }

        public static void Draw()
        {
            if (!Visible) return;
            if (!GameAccess_IsGameStarted())
            {
                Close();
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (now >= _nextMenuCheck)
            {
                _nextMenuCheck = now + 0.3f;
                var list = GameObject.Find("MenuButtonsList");
                _menuOpen = list != null && list.activeInHierarchy;
            }
            if (!_menuOpen)
            {
                Close(); // Esc-Menü zu → Statistik zu
                return;
            }
            EnsureStyles();
            // Breite fest halten: GUILayout würde das Fenster sonst jedes Bild um die Label-Ränder verbreitern
            // (Spaltenbreite hängt an der Fensterbreite → Rückkopplung, Fenster wuchs endlos).
            var r = GUILayout.Window(WindowId, _rect, DrawWindow, "STATISTIK", _box, GUILayout.Width(_width), GUILayout.MaxWidth(_width));
            _rect = new Rect(r.x, r.y, _width, r.height);
            ModUi.Report(WindowId, _rect);

            // Klicks über dem Fenster nicht an die Spiel-Oberfläche durchreichen
            var mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            bool over = _rect.Contains(mouse);
            try
            {
                if (over && _disabledEs == null && EventSystem.current != null && EventSystem.current.enabled)
                {
                    _disabledEs = EventSystem.current;
                    _disabledEs.enabled = false;
                }
                else if (!over)
                {
                    ReleaseUi();
                }
            }
            catch { }
        }

        private static bool GameAccess_IsGameStarted() => Game.GameAccess.IsGameStarted;

        private static string Num(float v, int i)
        {
            if (i == (int)Sync.StatsSync.Stat.Damage)
            {
                if (v >= 1000000f) return (v / 1000000f).ToString("0.0", CultureInfo.InvariantCulture) + " Mio.";
                if (v >= 10000f) return (v / 1000f).ToString("0", CultureInfo.InvariantCulture) + " Tsd.";
            }
            return Mathf.RoundToInt(v).ToString("N0", CultureInfo.GetCultureInfo("de-DE"));
        }

        private static void DrawWindow(int id)
        {
            var s = Plugin.Instance?.Session;
            if (s == null || s.Stats == null)
            {
                GUILayout.Label("Keine Daten.");
                return;
            }
            List<Sync.StatsSync.Row> rows;
            try { rows = s.Stats.Rows(); }
            catch (Exception e) { GUILayout.Label("Fehler: " + e.Message); return; }

            float nameW = 220f;
            // Innenbreite = Fensterbreite minus Polster (16+16); je Label kommen noch 4+4 Rand dazu
            float colW = Mathf.Floor((_width - 32f - nameW - 8f * (Short.Length + 1)) / Short.Length);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Spieler", _head, GUILayout.Width(nameW - 8f));
            _head.alignment = TextAnchor.MiddleRight;
            foreach (var h in Short) GUILayout.Label(h, _head, GUILayout.Width(colW));
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);

            var sum = new float[Sync.StatsSync.StatCount];
            int idx = 0;
            foreach (var r in rows)
            {
                var line = GUILayoutUtility.GetRect(10f, 30f, GUILayout.ExpandWidth(true));
                if (idx++ % 2 == 0) GUI.DrawTexture(line, _rowBg);
                float x = line.x;
                string hex = ColorUtility.ToHtmlStringRGB(r.Color);
                GUI.Label(new Rect(x + 6f, line.y, nameW - 6f, line.height), $"<color=#{hex}>■</color>  <color=#{hex}>{Escape(r.Name)}</color>", _name);
                x += nameW;
                for (int i = 0; i < Sync.StatsSync.StatCount && i < r.Values.Length; i++)
                {
                    GUI.Label(new Rect(x, line.y, colW, line.height), Num(r.Values[i], i), _cell);
                    sum[i] += r.Values[i];
                    x += colW;
                }
            }
            if (rows.Count > 1)
            {
                GUILayout.Space(4f);
                var line = GUILayoutUtility.GetRect(10f, 30f, GUILayout.ExpandWidth(true));
                GUI.DrawTexture(new Rect(line.x, line.y, line.width, 1f), Texture2D.whiteTexture);
                float x = line.x;
                GUI.Label(new Rect(x + 6f, line.y, nameW - 6f, line.height), "Zusammen", _name);
                x += nameW;
                for (int i = 0; i < sum.Length; i++)
                {
                    GUI.Label(new Rect(x, line.y, colW, line.height), Num(sum[i], i), _cell);
                    x += colW;
                }
            }

            GUILayout.Space(10f);
            GUILayout.Label("Kämpfe zählen für den, der den Trupp zuletzt gesteuert hat. Gilt für diese Sitzung (seit dem Laden).", _title);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Schließen", GUILayout.Width(140f), GUILayout.Height(30f)))
                Close();
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 30f));
        }

        private static string Escape(string s) => (s ?? "").Replace("<", "‹").Replace(">", "›");
    }
}
