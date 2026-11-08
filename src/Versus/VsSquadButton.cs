using System;
using Gameplay.Units;
using HarmonyLib;
using IfzCoop.Game;
using UI.Buttons;
using UnityEngine;

namespace IfzCoop.Versus
{
    /// <summary>
    /// Versus: Knopf „Spieler angreifen: AN/AUS“ direkt über den Haltungs-Knöpfen des ausgewählten Trupps (unten rechts).
    /// AUS = der Trupp greift fremde Spieler nicht an (weder von selbst noch auf Befehl), Zombies und Banditen schon.
    /// </summary>
    internal static class VsSquadButton
    {
        private static StancesPresenter _presenter;
        private static float _nextFind;
        private static GUIStyle _on, _off;
        private static Rect _last;
        private static readonly AccessTools.FieldRef<StancesPresenter, Group> _current =
            AccessTools.FieldRefAccess<StancesPresenter, Group>("_currentGroup");
        private static readonly Vector3[] _corners = new Vector3[4];

        private static bool InGame => VersusSession.Active && GameAccess.IsGameStarted && VersusSession.Instance.Phase == VsPhase.Running;

        private static Group SelectedSquad()
        {
            if (_presenter == null && Time.realtimeSinceStartup >= _nextFind)
            {
                _nextFind = Time.realtimeSinceStartup + 2f;
                _presenter = UnityEngine.Object.FindObjectOfType<StancesPresenter>();
            }
            if (_presenter == null || !_presenter.isActiveAndEnabled)
                return null;
            var g = _current(_presenter);
            if (g == null || g.IsEmpty() || g.Fraction != Fraction.Player || VsUnits.IsProxy(g))
                return null;
            return g;
        }

        /// <summary>Bildschirm-Rechteck der Haltungs-Knöpfe (GUI-Koordinaten, oben = 0).</summary>
        private static bool PresenterRect(out Rect r)
        {
            r = default;
            var rt = _presenter.transform as RectTransform;
            if (rt == null)
                return false;
            var canvas = _presenter.GetComponentInParent<Canvas>();
            Camera cam = null;
            if (canvas != null)
            {
                canvas = canvas.rootCanvas;
                if (canvas.renderMode != RenderMode.ScreenSpaceOverlay) cam = canvas.worldCamera;
            }
            rt.GetWorldCorners(_corners);
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var c in _corners)
            {
                var p = RectTransformUtility.WorldToScreenPoint(cam, c);
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
            }
            if (maxX - minX < 4f || maxY - minY < 4f)
                return false;
            r = new Rect(minX, Screen.height - maxY, maxX - minX, maxY - minY);
            return true;
        }

        public static void Draw()
        {
            if (!InGame)
                return;
            Group g;
            try { g = SelectedSquad(); }
            catch { g = null; }
            if (g == null || !PresenterRect(out var pr))
            {
                _last = default;
                return;
            }
            if (_on == null)
            {
                _on = MakeStyle(new Color(0.55f, 0.12f, 0.10f, 0.95f), new Color(1f, 0.85f, 0.8f));
                _off = MakeStyle(new Color(0.15f, 0.17f, 0.18f, 0.95f), new Color(0.75f, 0.78f, 0.8f));
            }
            float scale = Mathf.Clamp(Screen.height / 1080f, 0.8f, 2f);
            float h = 28f * scale;
            float w = Mathf.Max(pr.width, 230f * scale);
            var r = new Rect(pr.xMax - w, pr.y - h - 6f * scale, w, h);
            _last = r;
            bool on = VsUnits.AttacksPlayers(g);
            _on.fontSize = _off.fontSize = Mathf.RoundToInt(14f * scale);
            var label = new GUIContent(on ? "Spieler angreifen: AN" : "Spieler angreifen: AUS",
                on ? "Dieser Trupp greift fremde Spieler an (wenn nicht verbündet). Klick = aus."
                   : "Dieser Trupp lässt fremde Spieler in Ruhe. Zombies und Banditen greift er weiter an. Klick = an.");
            if (GUI.Button(r, label, on ? _on : _off))
                VsUnits.SetAttacksPlayers(g, !on);
            Menu.ModUi.Report(0x1F2E52, r);
        }

        private static GUIStyle MakeStyle(Color bg, Color text)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, bg);
            tex.Apply();
            var hover = new Texture2D(1, 1);
            hover.SetPixel(0, 0, Color.Lerp(bg, Color.white, 0.15f));
            hover.Apply();
            var s = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            s.normal.background = tex;
            s.hover.background = hover;
            s.active.background = hover;
            s.normal.textColor = s.hover.textColor = s.active.textColor = text;
            return s;
        }

        public static string Compact()
        {
            Group g = null;
            try { g = SelectedSquad(); } catch { }
            return $"presenter={(_presenter != null)} active={(_presenter != null && _presenter.isActiveAndEnabled)} squad={(g != null ? g.Id : "-")} " +
                   $"attack={(g != null ? VsUnits.AttacksPlayers(g).ToString() : "-")} rect={_last.x:0},{_last.y:0},{_last.width:0},{_last.height:0}";
        }
    }
}
