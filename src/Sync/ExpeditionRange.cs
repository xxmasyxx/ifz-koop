using System;
using System.Collections.Generic;
using System.Reflection;
using Expedition;
using Gameplay.Rebuilding.Towers;
using HarmonyLib;
using IfzCoop.Game;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Felder der Umgebungskarte sind nur in Reichweite einer Funkantenne aktiv (ExpeditionTilesInteraction.UpdateExpeditionRange:
    /// TowersController.Antennas, Abstand ≤ Antenna.Range). Nur aktive Felder bekommen ihr Plünder-Symbol (ExpeditionPoiIconsManager).
    /// Beim Host waren am 2026-11-09 fast alle Symbole weg, beim Gast nicht, obwohl 59 von 72 Feldern unberührt waren.
    /// Solange die Umgebungskarte offen ist, alle 3 s: Antennen-Liste mit den Türmen abgleichen (fehlende Antennen eintragen),
    /// Reichweite jeder Antenne auf die globale setzen und die Feld-Freigabe neu rechnen lassen. Zahlen für den F8-Bericht.
    /// </summary>
    internal static class ExpeditionRange
    {
        private const float Interval = 3f;
        private static float _next;

        private static readonly FieldInfo AntennasField = AccessTools.Field(typeof(TowersController), "_antennas");
        private static readonly MethodInfo UpdateAntennaMethod = AccessTools.Method(typeof(Antenna), "UpdateAntennaRange");
        private static readonly MethodInfo UpdateRangeMethod = AccessTools.Method(typeof(ExpeditionTilesInteraction), "UpdateExpeditionRange");
        private static readonly FieldInfo PoiField = AccessTools.Field(typeof(ExpeditionPoiIconsManager), "_poiOutZone");

        public static int AntennasAdded { get; private set; }
        public static int RangesFixed { get; private set; }
        public static int Refreshes { get; private set; }
        public static int TilesWoken { get; private set; }

        public static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _next)
                return;
            _next = now + Interval;
            try
            {
                if (!GameAccess.IsGameStarted || !ExpeditionView.Active)
                    return;
                Repair();
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("Antennen-Reichweite: " + e.Message);
            }
        }

        private static void Repair()
        {
            var tc = GameAccess.Game<Gameplay.ObjectOnMap.PlaceableObjectsController>()?.TowersController;
            if (tc == null || AntennasField == null)
                return;
            var list = AntennasField.GetValue(tc) as List<Antenna>;
            if (list == null)
                return;
            list.RemoveAll(a => a == null);
            foreach (var t in tc.Towers)
            {
                if (t == null || !t.TryGetComponent<Antenna>(out var a) || list.Contains(a))
                    continue;
                list.Add(a);
                AntennasAdded++;
                Plugin.Log.LogWarning($"IFZCOOP Antenne {t.name} fehlte in der Antennen-Liste – nachgetragen");
            }
            float global = Antenna.GlobalAntennaRange;
            foreach (var a in list)
            {
                if (global > 0f && Mathf.Abs(a.Range - global) > 0.01f && UpdateAntennaMethod != null)
                {
                    float was = a.Range;
                    UpdateAntennaMethod.Invoke(a, null);
                    RangesFixed++;
                    Plugin.Log.LogWarning($"IFZCOOP Antennen-Reichweite {was:0} -> {a.Range:0} (global {global:0})");
                }
            }
            var ti = UnityEngine.Object.FindObjectOfType<ExpeditionTilesInteraction>();
            if (ti == null || UpdateRangeMethod == null)
                return;
            int before = CountEnabled(ti);
            UpdateRangeMethod.Invoke(ti, null);
            Refreshes++;
            int after = CountEnabled(ti);
            if (after > before)
            {
                TilesWoken += after - before;
                Plugin.Log.LogWarning($"IFZCOOP Umgebungsfelder freigeschaltet: {before} -> {after}");
            }
        }

        private static int CountEnabled(ExpeditionTilesInteraction ti)
        {
            int n = 0;
            var tiles = ti.Tiles;
            if (tiles == null)
                return 0;
            foreach (var t in tiles.Values)
                if (t != null && t.IsInteractionEnable) n++;
            return n;
        }

        public static string StatusLine()
        {
            try
            {
                var tc = GameAccess.Game<Gameplay.ObjectOnMap.PlaceableObjectsController>()?.TowersController;
                var sb = new System.Text.StringBuilder();
                sb.Append("expRange global=").Append(Antenna.GlobalAntennaRange.ToString("0"));
                int towersWithAntenna = 0;
                if (tc != null)
                {
                    foreach (var t in tc.Towers)
                        if (t != null && t.TryGetComponent<Antenna>(out _)) towersWithAntenna++;
                    sb.Append(" antennas=").Append(tc.Antennas.Count).Append(" towersWithAntenna=").Append(towersWithAntenna).Append(" [");
                    foreach (var a in tc.Antennas)
                        if (a != null)
                            sb.Append(a.name).Append(" r=").Append(a.Range.ToString("0"))
                              .Append(" done=").Append(a.Tower != null && a.Tower.StateMachine.IsCompleted ? 1 : 0)
                              .Append(" pos=").Append(a.transform.position.x.ToString("0")).Append(',').Append(a.transform.position.z.ToString("0")).Append("; ");
                    sb.Append(']');
                }
                var ti = UnityEngine.Object.FindObjectOfType<ExpeditionTilesInteraction>();
                if (ti?.Tiles != null)
                {
                    int enabled = 0, open = 0, openEnabled = 0;
                    foreach (var t in ti.Tiles.Values)
                    {
                        if (t == null) continue;
                        bool en = t.IsInteractionEnable;
                        if (en) enabled++;
                        var tt = t.TilerTile;
                        if (tt != null && tt.Scavengable && !tt.IsScavenged())
                        {
                            open++;
                            if (en) openEnabled++;
                        }
                    }
                    sb.Append(" tiles=").Append(ti.Tiles.Count).Append(" enabled=").Append(enabled)
                      .Append(" scavOpen=").Append(open).Append(" scavOpenEnabled=").Append(openEnabled);
                }
                var pm = UnityEngine.Object.FindObjectOfType<ExpeditionPoiIconsManager>();
                if (pm != null && PoiField?.GetValue(pm) is System.Collections.ICollection pois)
                    sb.Append(" poiIcons=").Append(pois.Count);
                bool layer = false;
                try { layer = Gameplay.MapLayers.ViewLayersController.ViewLayersData.IsLayerGroupActive(ScriptableObjectScripts.UI.ViewLayerGroupId.ScavengeGroup); } catch { }
                sb.Append(" scavLayer=").Append(layer ? 1 : 0)
                  .Append(" added=").Append(AntennasAdded).Append(" rangesFixed=").Append(RangesFixed)
                  .Append(" refreshes=").Append(Refreshes).Append(" woken=").Append(TilesWoken);
                return sb.ToString();
            }
            catch (Exception e)
            {
                return "expRange ? " + e.Message;
            }
        }
    }
}
