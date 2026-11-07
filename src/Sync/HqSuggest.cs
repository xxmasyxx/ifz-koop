using System;
using System.Collections.Generic;
using System.IO;
using Controllers;
using Gameplay.Buildings;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace IfzCoop.Sync
{
    /// <summary>
    /// KONZEPT-MENUE M5: Koop, neues Spiel. Die Gäste sind schon während der HQ-Wahl im Spiel (Spielstand vor der Wahl),
    /// nur der Host bestätigt das HQ. Klickt ein Gast auf ein Haus, kommt statt des Bestätigungsfensters ein Vorschlag:
    /// über dem Haus steht bei allen „Max schlägt vor“ in seiner Spielerfarbe. Nach der Wahl des Hosts verschwinden die
    /// Vorschläge, und die Gäste bekommen den Spielstand mit HQ (Session._waitingForHq).
    /// </summary>
    internal static class HqSuggest
    {
        private sealed class Suggestion
        {
            public long BuildingId;
            public string Name;
            public Color Color;
            public float At;
            public GameObject Label;
            public RectTransform Rect;
            public Vector3 Anchor;
        }

        private static readonly Dictionary<ulong, Suggestion> _bySender = new Dictionary<ulong, Suggestion>();
        private static Canvas _canvas;
        private static TMP_FontAsset _font;

        public static int Sent { get; private set; }
        public static int Shown { get; private set; }

        private static Session S => Plugin.Instance?.Session;

        public static void Patch(Harmony h)
        {
            try
            {
                var m = AccessTools.Method(typeof(HqSetterController), "ShowSelectionPopup");
                h.Patch(m, prefix: new HarmonyMethod(typeof(HqSuggest), nameof(ShowSelectionPopupPrefix)));
                Plugin.Log.LogInfo("Gepatcht: HqSetterController.ShowSelectionPopup (HQ-Vorschläge)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("HQ-Vorschläge Haken: " + e.Message);
            }
        }

        // HqSetterController.ShowSelectionPopup(Building building, float adaptValue): beim Koop-Gast = Vorschlag
        private static bool ShowSelectionPopupPrefix(Building building)
        {
            var s = S;
            if (s == null || s.Role != Role.Guest || building == null)
                return true;
            try
            {
                long id = building.ID;
                s.SendToHost(Protocol.Build(Msg.HqSuggest, w => w.Write(id)));
                Sent++;
                building.PartialBuildingHandler?.UpdatePartialValue(0f);
                s.ShowNotice("Vorschlag an alle geschickt. Das Hauptquartier wählt der Host.", 4f);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("HQ-Vorschlag: " + e.Message);
            }
            return false;
        }

        // ================================================================ Host

        public static void HostOnSuggest(Peer from, BinaryReader r)
        {
            long id = r.ReadInt64();
            var s = S;
            if (s == null || !s.Hq.HostChoosingHq)
                return;
            if (GameAccess.Game<BuildingsController>()?.GetBuildingById(id) == null)
                return;
            Color c = s.PlayerColors.ColorOf(from.Id.m_SteamID);
            ulong sender = from.Id.m_SteamID;
            string name = from.Name;
            s.BroadcastToLobby(BuildShow(sender, id, name, c));
            Show(sender, id, name, c);
            s.ShowNotice($"{name} schlägt ein Haus als Hauptquartier vor", 5f);
            Plugin.Log.LogInfo($"IFZCOOP HQ-Vorschlag von {name}: Haus {id}");
        }

        private static byte[] BuildShow(ulong sender, long id, string name, Color c) =>
            Protocol.Build(Msg.HqSuggestShow, w =>
            {
                w.Write(sender);
                w.Write(id);
                w.Write(name);
                w.Write(c.r); w.Write(c.g); w.Write(c.b);
            });

        // ================================================================ Gast

        public static void GuestOnShow(BinaryReader r)
        {
            ulong sender = r.ReadUInt64();
            long id = r.ReadInt64();
            string name = r.ReadString();
            var c = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), 1f);
            Show(sender, id, name, c);
        }

        // ================================================================ Anzeige

        private static void Show(ulong sender, long buildingId, string name, Color color)
        {
            var b = GameAccess.Game<BuildingsController>()?.GetBuildingById(buildingId);
            if (b == null)
                return;
            if (_bySender.TryGetValue(sender, out var old) && old.Label != null)
                UnityEngine.Object.Destroy(old.Label);
            float top = b.transform.position.y + 10f;
            foreach (var rend in b.GetComponentsInChildren<Renderer>())
                if (rend != null) top = Mathf.Max(top, rend.bounds.max.y + 3f);
            var sug = new Suggestion { BuildingId = buildingId, Name = name, Color = color, At = Time.realtimeSinceStartup, Anchor = new Vector3(b.transform.position.x, top, b.transform.position.z) };
            sug.Label = BuildLabel(name, color, out sug.Rect);
            _bySender[sender] = sug;
            Shown++;
        }

        private static GameObject BuildLabel(string name, Color color, out RectTransform rect)
        {
            if (_canvas == null)
            {
                var go = new GameObject("IfzCoopHqSuggestCanvas");
                _canvas = go.AddComponent<Canvas>();
                _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _canvas.sortingOrder = 4;
                var scaler = go.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 1f;
            }
            if (_font == null)
                foreach (var t in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
                    if (t != null && t.font != null && t.font.name.StartsWith("07-Brother1816")) { _font = t.font; break; }

            var label = new GameObject("Suggestion", typeof(RectTransform));
            rect = (RectTransform)label.transform;
            rect.SetParent(_canvas.transform, false);
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(230f, 34f);
            var bg = label.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.85f);
            bg.raycastTarget = false;
            var strip = new GameObject("Strip", typeof(RectTransform));
            strip.transform.SetParent(rect, false);
            var sr = (RectTransform)strip.transform;
            sr.anchorMin = new Vector2(0f, 0f); sr.anchorMax = new Vector2(0f, 1f); sr.pivot = new Vector2(0f, 0.5f);
            sr.sizeDelta = new Vector2(6f, 0f);
            var si = strip.AddComponent<Image>();
            si.color = color;
            si.raycastTarget = false;
            var tgo = new GameObject("Text", typeof(RectTransform));
            tgo.transform.SetParent(rect, false);
            var tr = (RectTransform)tgo.transform;
            tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.offsetMin = new Vector2(12f, 0f); tr.offsetMax = new Vector2(-6f, 0f);
            var text = tgo.AddComponent<TextMeshProUGUI>();
            if (_font != null) text.font = _font;
            text.fontSize = 15f;
            text.fontStyle = FontStyles.UpperCase;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.color = Color.white;
            text.raycastTarget = false;
            text.text = $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>▼</color> {name} schlägt vor";
            return label;
        }

        /// <summary>Jedes Bild (Plugin.Update): Schilder über den Häusern halten, nach der HQ-Wahl wegräumen.</summary>
        public static void Update()
        {
            if (_bySender.Count == 0)
                return;
            var s = S;
            bool choosing = false;
            try
            {
                var setter = GameAccess.Game<HqSetterController>();
                choosing = GameAccess.IsGameStarted && setter != null && setter.IsSelecting && HqController.MainHeadquarter == null;
            }
            catch { }
            if (!choosing || s == null || s.Role == Role.None)
            {
                Clear();
                return;
            }
            var cam = Camera.main;
            if (cam == null || _canvas == null)
                return;
            float scale = _canvas.scaleFactor > 0f ? _canvas.scaleFactor : 1f;
            foreach (var sug in _bySender.Values)
            {
                if (sug.Label == null) continue;
                var p = cam.WorldToScreenPoint(sug.Anchor);
                bool visible = p.z > 0f;
                if (sug.Label.activeSelf != visible) sug.Label.SetActive(visible);
                if (visible) sug.Rect.anchoredPosition = new Vector2(p.x / scale, p.y / scale);
            }
        }

        public static void Clear()
        {
            foreach (var sug in _bySender.Values)
                if (sug.Label != null) UnityEngine.Object.Destroy(sug.Label);
            _bySender.Clear();
        }

        public static string StatusLine() =>
            $"hqsuggest sent={Sent} shown={Shown} active={_bySender.Count}";

        /// <summary>QA (Gast): „hqsuggest“ – einen Vorschlag für das vom Spiel vorgeschlagene/erste geeignete Haus schicken.</summary>
        public static string QaSuggest(long buildingId)
        {
            var s = S;
            if (s == null || s.Role != Role.Guest) return "nur Gast";
            var b = GameAccess.Game<BuildingsController>()?.GetBuildingById(buildingId);
            if (b == null) return "kein Haus " + buildingId;
            ShowSelectionPopupPrefix(b);
            return "Vorschlag " + buildingId;
        }
    }
}
