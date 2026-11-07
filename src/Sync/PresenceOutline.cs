using System;
using System.Collections.Generic;
using System.Linq;
using Controllers;
using Gameplay.Buildings;
using HarmonyLib;
using IfzCoop.Game;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Haus des Mitspielers mit dem Original-Umriss des Spiels zeigen (statt eines eigenen Rahmens).
    ///
    /// Das Spiel zeichnet Umrisse mit cakeslice.Outline / OutlineEffect: Jedes Gebäude hat eine BuildingOutline
    /// (Structure.Outline), die sich bei OutlineEffect anmeldet, solange sie eingeschaltet ist (OnEnable/OnDisable).
    /// OutlineEffect.LateUpdate baut daraus einmal pro Bild den Befehlspuffer; die Farbe ist ein Platz 0–4
    /// (StructureOutline: 0 blau, 1 weiß = Überfahren/Ausgewählt, 2 grün = eigenes Haus, 3 gelb, 4 rot).
    ///
    /// Hier: Prefix auf OutlineEffect.LateUpdate schaltet den Umriss des vom Mitspieler gewählten Hauses kurz ein und setzt
    /// den Farbplatz (Standard 0 = blau), der Finalizer stellt Farbe und Ein/Aus sofort wieder her. Die Spiel-Logik sieht
    /// den Umriss also nie verändert; gezeichnet wird er trotzdem, weil der Befehlspuffer im LateUpdate entsteht.
    /// Überfährt oder wählt der Spieler das Haus selbst (Platz 1, weiß), bleibt seine eigene Markierung.
    ///
    /// Farbe des Mitspielers (Wunsch 2026-11-06, bisher immer blau): Das Spiel kennt nur 5 Umrissfarben, alle belegt.
    /// Jedes Bild wird geschaut, welche Plätze gerade kein anderer Umriss braucht (bevorzugt 3 gelb, 4 rot, 0 blau);
    /// dort wird die Spielerfarbe eingetragen (OutlineEffect.LineColorN + UpdateMaterialsPublicProperties). Ist keiner
    /// frei, bleibt es blau. Weiß (eigene Auswahl) und grün (eigene Häuser) werden nie verändert. Ohne Mitspieler-Haus
    /// gelten wieder die Originalfarben.
    /// </summary>
    internal static class PresenceOutline
    {
        private const float StaleAfter = 5f;
        private const int OwnHighlight = 1;   // StructureOutline.OUTLINE_WHITE

        /// <summary>Farbplatz für Mitspieler-Häuser (0 blau, 1 weiß, 2 grün, 3 gelb, 4 rot). QA: "outline &lt;0-4&gt;".</summary>
        public static int Slot = 0;

        /// <summary>true, wenn der Haken sitzt und OutlineEffect da ist – sonst zeichnet PresenceSync den alten Rahmen.</summary>
        public static bool Active => _patched && global::OutlineEffect.OutlineEffect.Instance != null;

        public static int FramesDrawn { get; private set; }
        public static long LastBuilding { get; private set; }

        private static bool _patched;

        private struct Saved
        {
            public cakeslice.Outline Outline;
            public bool Enabled;
            public int Color;
        }

        private static readonly List<Saved> _saved = new List<Saved>();
        private static readonly HashSet<long> _targets = new HashSet<long>();

        private static readonly AccessTools.FieldRef<cakeslice.Outline, bool> BlockEnablingField =
            AccessTools.FieldRefAccess<cakeslice.Outline, bool>("_blockEnabling");
        private static readonly AccessTools.FieldRef<global::OutlineEffect.OutlineEffect, HashSet<cakeslice.Outline>> OutlinesField =
            AccessTools.FieldRefAccess<global::OutlineEffect.OutlineEffect, HashSet<cakeslice.Outline>>("_outlines");

        private static readonly int[] BorrowOrder = { 3, 4, 0 };
        private static Color[] _orig;                       // Originalfarben der 5 Plätze
        private static readonly Color?[] _lent = new Color?[5];
        private static readonly Dictionary<long, Color> _targetColors = new Dictionary<long, Color>();
        public static string LastLent { get; private set; } = "-";

        private static Color GetLine(global::OutlineEffect.OutlineEffect fx, int i)
        {
            switch (i)
            {
                case 0: return fx.LineColor0;
                case 1: return fx.LineColor1;
                case 2: return fx.LineColor2;
                case 3: return fx.LineColor3;
                default: return fx.LineColor4;
            }
        }

        private static void SetLine(global::OutlineEffect.OutlineEffect fx, int i, Color c)
        {
            switch (i)
            {
                case 0: fx.LineColor0 = c; break;
                case 1: fx.LineColor1 = c; break;
                case 2: fx.LineColor2 = c; break;
                case 3: fx.LineColor3 = c; break;
                default: fx.LineColor4 = c; break;
            }
        }

        /// <summary>Plätze für die Spielerfarben wählen und eintragen; liefert Gebäude -> Platz.</summary>
        private static Dictionary<long, int> LendSlots(global::OutlineEffect.OutlineEffect fx, Dictionary<long, cakeslice.Outline> mine)
        {
            var result = new Dictionary<long, int>();
            if (_orig == null)
            {
                _orig = new Color[5];
                for (int i = 0; i < 5; i++) _orig[i] = GetLine(fx, i);
            }
            var want = new Color?[5];
            if (mine.Count > 0)
            {
                var used = new bool[5];
                var set = OutlinesField(fx);
                if (set != null)
                {
                    var ours = new HashSet<cakeslice.Outline>(mine.Values);
                    foreach (var o in set)
                        if (o != null && o.enabled && !ours.Contains(o) && o.color >= 0 && o.color < 5)
                            used[o.color] = true;
                }
                var byColor = new Dictionary<string, int>();
                foreach (var kv in mine)
                {
                    if (!_targetColors.TryGetValue(kv.Key, out var col))
                        continue;
                    string hex = ColorUtility.ToHtmlStringRGB(col);
                    if (!byColor.TryGetValue(hex, out int slot))
                    {
                        slot = -1;
                        foreach (int cand in BorrowOrder)
                            if (!used[cand] && !want[cand].HasValue) { slot = cand; break; }
                        if (slot < 0)
                            continue; // kein Platz frei: bleibt beim Standard (blau)
                        want[slot] = col;
                        byColor[hex] = slot;
                    }
                    result[kv.Key] = slot;
                }
            }
            bool changed = false;
            var lent = new List<string>();
            for (int i = 0; i < 5; i++)
            {
                Color target = want[i] ?? _orig[i];
                if (want[i].HasValue) lent.Add(i + ":" + ColorUtility.ToHtmlStringRGB(want[i].Value));
                if (_lent[i] != want[i] || GetLine(fx, i) != target)
                {
                    SetLine(fx, i, target);
                    _lent[i] = want[i];
                    changed = true;
                }
            }
            if (changed)
                fx.UpdateMaterialsPublicProperties();
            LastLent = lent.Count > 0 ? string.Join(",", lent) : "-";
            return result;
        }

        public static void ApplyPatches(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(global::OutlineEffect.OutlineEffect), "LateUpdate");
                if (target == null)
                {
                    Plugin.Log.LogWarning("IFZCOOP outline hook fehlt: OutlineEffect.LateUpdate – Mitspieler-Haus bekommt den alten Rahmen");
                    return;
                }
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(PresenceOutline), nameof(LatePrefix)),
                    finalizer: new HarmonyMethod(typeof(PresenceOutline), nameof(LateFinalizer)));
                _patched = true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("IFZCOOP outline hook nicht gesetzt: " + e.Message);
            }
        }

        private static void LatePrefix()
        {
            _saved.Clear();
            try
            {
                var presence = Plugin.Instance?.Session?.Presence;
                if (presence == null || !GameAccess.IsGameStarted)
                {
                    var fx0 = global::OutlineEffect.OutlineEffect.Instance;
                    if (fx0 != null && _orig != null)
                        LendSlots(fx0, new Dictionary<long, cakeslice.Outline>()); // geliehene Farben zurück
                    return;
                }
                _targets.Clear();
                _targetColors.Clear();
                float now = Time.realtimeSinceStartup;
                foreach (var remote in presence.Remotes)
                    if (remote != null && remote.Building != 0 && now - remote.Updated <= StaleAfter)
                    {
                        _targets.Add(remote.Building);
                        _targetColors[remote.Building] = remote.Color;
                    }
                var fx = global::OutlineEffect.OutlineEffect.Instance;
                var buildings = GameAccess.Game<BuildingsController>();
                var mine = new Dictionary<long, cakeslice.Outline>();
                if (buildings != null)
                    foreach (long id in _targets)
                    {
                        var b = buildings.GetBuildingById(id);
                        if (b == null)
                            continue;
                        cakeslice.Outline o = b.Outline;
                        if (o == null || !o.gameObject.activeInHierarchy || BlockEnablingField(o))
                            continue;
                        // eigene Markierung des Spielers (Überfahren/Ausgewählt) hat Vorrang
                        if (o.enabled && o.color == OwnHighlight)
                            continue;
                        mine[id] = o;
                    }
                // auch ohne Mitspieler-Haus aufrufen: setzt geliehene Farben zurück
                var slots = fx != null ? LendSlots(fx, mine) : new Dictionary<long, int>();
                int fallback = Mathf.Clamp(Slot, 0, 4);
                foreach (var kv in mine)
                {
                    long id = kv.Key;
                    var o = kv.Value;
                    _saved.Add(new Saved { Outline = o, Enabled = o.enabled, Color = o.color });
                    o.color = slots.TryGetValue(id, out int slot) ? slot : fallback;
                    if (!o.enabled)
                        o.enabled = true;   // OnEnable meldet den Umriss bei OutlineEffect an
                    FramesDrawn++;
                    LastBuilding = id;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("Mitspieler-Umriss: " + e.Message);
            }
        }

        private static void LateFinalizer()
        {
            for (int i = 0; i < _saved.Count; i++)
            {
                var s = _saved[i];
                try
                {
                    if (s.Outline == null)
                        continue;
                    s.Outline.color = s.Color;
                    if (!s.Enabled)
                        s.Outline.enabled = false;   // OnDisable meldet ihn wieder ab
                }
                catch
                {
                    // Gebäude im Abriss
                }
            }
            _saved.Clear();
        }

        public static string StatusLine()
        {
            string colors = "-";
            try
            {
                var fx = global::OutlineEffect.OutlineEffect.Instance;
                if (fx != null)
                    colors = string.Join(",", new[] { fx.LineColor0, fx.LineColor1, fx.LineColor2, fx.LineColor3, fx.LineColor4 }
                        .Select(ColorUtility.ToHtmlStringRGB));
            }
            catch { }
            return $"presenceOutline active={(Active ? 1 : 0)} slot={Slot} lastBuilding={LastBuilding} frames={FramesDrawn} colors={colors} lent={LastLent}";
        }
    }
}
