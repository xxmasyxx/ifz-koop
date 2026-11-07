using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace IfzCoop.Menu
{
    /// <summary>
    /// Meldungen oben in der Mitte („Max hat geantwortet …“, „Gast ist in der Lobby“) im Stil des Spiels
    /// (schwarzer Kasten, graue Linie, Schrift Brother1816) statt des alten Unity-Kastens. Eigene Canvas, bleibt über
    /// Szenenwechsel erhalten. Inhalt aus Session.Notice/NoticeUntil (auch Versus-Meldungen laufen darüber).
    /// </summary>
    internal static class NoticeUi
    {
        private static GameObject _root;
        private static TextMeshProUGUI _text;
        private static LayoutElement _textLayout;
        private static CanvasGroup _group;
        private static string _shown;
        private static float _nextFontTry;

        public static void Update()
        {
            var s = Plugin.Instance?.Session;
            if (s == null)
                return;
            bool active = !string.IsNullOrEmpty(s.Notice) && Time.realtimeSinceStartup <= s.NoticeUntil;
            if (_root == null)
            {
                if (!active) return;
                Build();
                if (_root == null) return;
            }
            if (_text.font == null || !_text.font.name.StartsWith("07-Brother1816"))
                TryFont();
            if (active && _shown != s.Notice)
            {
                _shown = s.Notice;
                _text.text = s.Notice;
                // lange Meldungen umbrechen (höchstens 900 breit)
                _text.enableWordWrapping = false;
                var pref = _text.GetPreferredValues(s.Notice);
                if (pref.x > 900f)
                {
                    _text.enableWordWrapping = true;
                    _textLayout.preferredWidth = 900f;
                    _textLayout.preferredHeight = _text.GetPreferredValues(s.Notice, 900f, 0f).y;
                }
                else
                {
                    _textLayout.preferredWidth = -1f;
                    _textLayout.preferredHeight = 26f;
                }
            }
            float left = s.NoticeUntil - Time.realtimeSinceStartup;
            _group.alpha = active ? Mathf.Clamp01(left / 0.4f) : 0f;
            if (_root.activeSelf != active) _root.SetActive(active);
        }

        private static void TryFont()
        {
            if (Time.realtimeSinceStartup < _nextFontTry) return;
            _nextFontTry = Time.realtimeSinceStartup + 2f;
            foreach (var t in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
                if (t != null && t.font != null && t.font.name.StartsWith("07-Brother1816"))
                {
                    _text.font = t.font;
                    break;
                }
        }

        private static void Build()
        {
            var go = new GameObject("IfzCoopNoticeCanvas");
            Object.DontDestroyOnLoad(go);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1500;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;

            _root = new GameObject("Notice", typeof(RectTransform));
            var r = (RectTransform)_root.transform;
            r.SetParent(go.transform, false);
            r.anchorMin = r.anchorMax = new Vector2(0.5f, 1f);
            r.pivot = new Vector2(0.5f, 1f);
            r.anchoredPosition = new Vector2(0f, -70f);
            _group = _root.AddComponent<CanvasGroup>();
            _group.blocksRaycasts = false;
            _group.interactable = false;
            var bg = _root.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.92f);
            bg.raycastTarget = false;
            var fitter = _root.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var layout = _root.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(28, 28, 12, 12);
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            var line = new GameObject("Line", typeof(RectTransform));
            line.transform.SetParent(r, false);
            var le = line.AddComponent<LayoutElement>();
            le.ignoreLayout = true;
            var lr = (RectTransform)line.transform;
            lr.anchorMin = new Vector2(0f, 1f); lr.anchorMax = new Vector2(1f, 1f); lr.pivot = new Vector2(0.5f, 1f);
            lr.sizeDelta = new Vector2(0f, 2f);
            var li = line.AddComponent<Image>();
            li.color = new Color(0.333f, 0.416f, 0.475f, 1f);
            li.raycastTarget = false;

            var tgo = new GameObject("Text", typeof(RectTransform));
            tgo.transform.SetParent(r, false);
            _text = tgo.AddComponent<TextMeshProUGUI>();
            _text.fontSize = 18f;
            _text.fontStyle = FontStyles.UpperCase;
            _text.alignment = TextAlignmentOptions.Center;
            _text.color = new Color(0.85f, 0.85f, 0.85f, 1f);
            _text.raycastTarget = false;
            _text.enableWordWrapping = false;
            _textLayout = tgo.AddComponent<LayoutElement>();
            _textLayout.preferredHeight = 26f;
            TryFont();
            _root.SetActive(false);
        }
    }
}
