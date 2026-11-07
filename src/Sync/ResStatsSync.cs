using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Controllers;
using Controllers.CharacterLogic;
using Gameplay.GameResources;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Zahlen der Rohstoffleiste oben (Fehler aus dem echten Spiel: Tooltips beim Gast stimmen nicht).
    ///
    /// Herkunft im Spiel: ResourceUi.GetContentText (Tooltip je Rohstoff: „Vortag: Produziert / Gesammelt / Verwendet /
    /// Tägliche Veränderung“) liest MainResourcesContainer.ResourcesProducedPreviousDay / ResourcesScavengedPreviousDay /
    /// ResourcesUsedPreviousDay von StockroomsController.Container. Fortgeschrieben wird in
    /// MainResourcesContainer.ForceAddResource (nur ResourceAddReason.Scavenge bzw. Production → „…InLast24H“) und
    /// MainResourcesContainer.TryRemoveResource (jede Entnahme → „UsedInLast24H“); MainResourcesContainer.TimeOfDayChanged
    /// (Wechsel auf Tag) schiebt „InLast24H“ nach „PreviousDay“. Gespeichert über StockroomsController.SerializeObject
    /// (SaveData.Resources…InLast24H/PreviousDay).
    /// Beim Gast: TimeOfDayChanged ist gesperrt (StockSync), also rückt der Vortag nie weiter; StockSync gleicht mit
    /// ForceAddResource(…, None) / ForceRemoveResource an, das zählt gar nicht. Ergebnis: eingefrorene Werte vom Laden.
    ///
    /// Host: 1×/s ein Stand (beide Vorräte je 6 Statistiken, Lager-Kapazität, Bevölkerungszähler). Bei Änderung
    /// höchstens alle 2 s, beim Tageswechsel (Vortag ändert sich) sofort, sonst alle 10 s zur Sicherheit.
    /// Gast: übernimmt die 6 Statistiken (Wörterbücher ersetzen). Eigene Fortschreibung ist gesperrt
    /// (UnitPatches: Prefix/Postfix um ForceAddResource/TryRemoveResource/TimeOfDayChanged stellen die Werte wieder her).
    /// Dazu: Kapazität des Lagers und belegte Kisten (Abweichung zur Summe) wie beim Host; CitizensCount/ChildrenCount/
    /// SoldiersCount werden angeglichen, wenn sie länger als 6 s abweichen (Essen-reicht-Tage, Bevölkerungs-Leiste).
    /// </summary>
    internal sealed class ResStatsSync
    {
        private const float CheckInterval = 1f;
        private const float MinSendInterval = 2f;
        private const float ForceInterval = 10f;
        private const float CorrectAfter = 6f;

        private const int StatCount = 6;
        private static readonly string[] StatFieldNames =
        {
            "_resourcesProducedInLast24H", "_resourcesScavengedInLast24H", "_resourcesUsedInLast24H",
            "_resourcesProducedPreviousDay", "_resourcesScavengedPreviousDay", "_resourcesUsedPreviousDay",
        };

        private static readonly FieldInfo[] StatFields = StatFieldNames
            .Select(n => AccessTools.Field(typeof(MainResourcesContainer), n)).ToArray();
        private static readonly FieldInfo UsedCapacityField = AccessTools.Field(typeof(ResourcesContainer), "_usedCapacity");
        private static readonly MethodInfo CitizensCountSetter = AccessTools.PropertySetter(typeof(CitizensController), "CitizensCount");
        private static readonly MethodInfo ChildrenCountSetter = AccessTools.PropertySetter(typeof(CitizensController), "ChildrenCount");
        private static readonly MethodInfo SoldiersCountSetter = AccessTools.PropertySetter(typeof(Gameplay.Units.SquadsController), "SoldiersCount");
        private static readonly FieldInfo TooltipTextField = AccessTools.Field(typeof(UI.Tooltips.TooltipTriggerUI), "_uiElementID");
        private static readonly FieldInfo ResUiTooltipField = AccessTools.Field(typeof(ResourceUi), "tooltipTrigger");
        private static readonly FieldInfo RepTooltipField = AccessTools.Field(typeof(ResourceGroupRepresentative), "representativeTooltipTrigger");
        private static readonly MethodInfo ResUiUpdateTooltip = AccessTools.Method(typeof(ResourceUi), "UpdateTooltip");

        /// <summary>Bevölkerungszähler des Hosts (für Leiste, Essen-Tage, Status).</summary>
        private struct Counters
        {
            public int Citizens, Children, Soldiers, Homeless, Quarters, Workers, Available;
        }

        private readonly Session _session;
        private byte[] _lastSent;
        private string _lastPrevSig;
        private float _nextCheck;
        private float _nextAllowed;
        private float _nextForce;

        // Gast: zuletzt vom Host
        private bool _have;
        private int _hostCapacity;
        private int _hostUsedOffset;
        private Counters _host;
        private int _lastHomelessShown = -1;
        private float _mismatchSince = -1f;
        private float _nextGuestCheck;

        public int Sent { get; private set; }
        public int Applied { get; private set; }
        public int CapacityFixed { get; private set; }
        public int CountersFixed { get; private set; }

        /// <summary>Wie oft die eigene Fortschreibung des Gastes zurückgenommen wurde.</summary>
        public static int Locked;

        public ResStatsSync(Session session)
        {
            _session = session;
        }

        public void ResendAll()
        {
            _lastSent = null;
            _nextAllowed = 0f;
        }

        public void Reset()
        {
            _lastSent = null;
            _lastPrevSig = null;
            _nextAllowed = 0f;
            _nextForce = 0f;
            _have = false;
            _lastHomelessShown = -1;
            _mismatchSince = -1f;
        }

        private static Dictionary<ResourceID, float> Stat(MainResourcesContainer c, int i)
        {
            var f = StatFields[i];
            return f == null || c == null ? null : f.GetValue(c) as Dictionary<ResourceID, float>;
        }

        private static int CrateSum(ResourcesContainer c)
        {
            int sum = 0;
            foreach (var kv in c.Resources)
                sum += Mathf.CeilToInt(kv.Value);
            return sum;
        }

        private static Counters ReadCounters()
        {
            var c = new Counters
            {
                Citizens = CitizensController.CitizensCount,
                Children = CitizensController.ChildrenCount,
                Soldiers = Gameplay.Units.SquadsController.SoldiersCount,
                Workers = Gameplay.Units.Player.Workers.WorkersController.WorkersCount,
                Available = Gameplay.Units.Workers.WorkSystem.WorkController.AvailableWorkersCount,
                Homeless = -1,
                Quarters = -1,
            };
            try
            {
                var ha = GameAccess.Game<Gameplay.Buildings.HouseAssigner>();
                if (ha != null) c.Homeless = ha.GetHomelessCount();
                var bc = GameAccess.Game<BuildingsController>();
                var cc = GameAccess.Game<Gameplay.Rebuilding.Towers.ConvoyStructureController>();
                if (bc != null) c.Quarters = bc.GetLivingQuartersCapacity() + (cc != null ? cc.GetLivingQuartersCapacity() : 0);
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("Unterkunft zählen: " + e.Message);
            }
            return c;
        }

        // ================================================================ Host

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextCheck || _session.ActiveGuestCount == 0 || !GameAccess.IsGameStarted)
                return;
            _nextCheck = now + CheckInterval;
            var stock = GameAccess.Game<StockroomsController>();
            if (stock == null)
                return;
            byte[] msg;
            string prevSig;
            try
            {
                var sb = new StringBuilder();
                for (int k = 3; k < StatCount; k++)
                    AppendSig(sb, Stat(stock.Container, k));
                prevSig = sb.ToString();
                var counters = ReadCounters();
                msg = Protocol.Build(Msg.ResStats, w =>
                {
                    WriteStats(w, stock.Container);
                    WriteStats(w, stock.VirtualResourcesContainer);
                    w.Write(stock.Container.Capacity);
                    w.Write(stock.Container.GetCratesCount() - CrateSum(stock.Container));
                    w.Write(counters.Citizens);
                    w.Write(counters.Children);
                    w.Write(counters.Soldiers);
                    w.Write(counters.Homeless);
                    w.Write(counters.Quarters);
                    w.Write(counters.Workers);
                    w.Write(counters.Available);
                });
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("Rohstoff-Statistik nicht gebaut: " + e.Message);
                return;
            }
            bool changed = _lastSent == null || !_lastSent.SequenceEqual(msg);
            bool dayChanged = _lastPrevSig == null || prevSig != _lastPrevSig;
            if (!changed && now < _nextForce)
                return;
            if (changed && !dayChanged && now < _nextAllowed && now < _nextForce)
                return;
            _lastSent = msg;
            _lastPrevSig = prevSig;
            _nextAllowed = now + MinSendInterval;
            _nextForce = now + ForceInterval;
            _session.BroadcastToGuests(msg);
            Sent++;
        }

        private static void AppendSig(StringBuilder sb, Dictionary<ResourceID, float> d)
        {
            if (d != null)
                foreach (var kv in d.OrderBy(x => (int)x.Key))
                    sb.Append((int)kv.Key).Append(':').Append(kv.Value.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append('|');
        }

        private static void WriteStats(BinaryWriter w, MainResourcesContainer c)
        {
            for (int k = 0; k < StatCount; k++)
            {
                var d = Stat(c, k);
                if (d == null)
                {
                    w.Write(0);
                    continue;
                }
                var items = d.OrderBy(x => (int)x.Key).ToList();
                w.Write(items.Count);
                foreach (var kv in items)
                {
                    w.Write((int)kv.Key);
                    w.Write(kv.Value);
                }
            }
        }

        private static List<KeyValuePair<ResourceID, float>>[] ReadStats(BinaryReader r)
        {
            var res = new List<KeyValuePair<ResourceID, float>>[StatCount];
            for (int k = 0; k < StatCount; k++)
            {
                int n = r.ReadInt32();
                if (n < 0 || n > 10000)
                    throw new InvalidDataException("Statistik zu groß");
                var list = new List<KeyValuePair<ResourceID, float>>(n);
                for (int i = 0; i < n; i++)
                    list.Add(new KeyValuePair<ResourceID, float>((ResourceID)r.ReadInt32(), r.ReadSingle()));
                res[k] = list;
            }
            return res;
        }

        // ================================================================ Gast

        public void GuestOnState(BinaryReader r)
        {
            var main = ReadStats(r);
            var virt = ReadStats(r);
            int cap = r.ReadInt32();
            int usedOffset = r.ReadInt32();
            var c = new Counters
            {
                Citizens = r.ReadInt32(),
                Children = r.ReadInt32(),
                Soldiers = r.ReadInt32(),
                Homeless = r.ReadInt32(),
                Quarters = r.ReadInt32(),
                Workers = r.ReadInt32(),
                Available = r.ReadInt32(),
            };
            var stock = GameAccess.Game<StockroomsController>();
            if (stock == null)
                return;
            ApplyStats(stock.Container, main);
            ApplyStats(stock.VirtualResourcesContainer, virt);
            _hostCapacity = cap;
            _hostUsedOffset = usedOffset;
            _host = c;
            _have = true;
            Applied++;
            EnforceCapacity(stock);
            if (c.Homeless >= 0 && c.Homeless != _lastHomelessShown)
                RefreshPeoplePanel(c.Homeless);
        }

        private static void ApplyStats(MainResourcesContainer c, List<KeyValuePair<ResourceID, float>>[] stats)
        {
            for (int k = 0; k < StatCount; k++)
            {
                var d = Stat(c, k);
                if (d == null)
                    continue;
                d.Clear();
                foreach (var kv in stats[k])
                    d[kv.Key] = kv.Value;
            }
        }

        /// <summary>Lager-Kapazität und belegte Kisten wie beim Host (Leiste: Füllbalken, „Lager voll“).</summary>
        private void EnforceCapacity(StockroomsController stock)
        {
            if (!_have || stock == null)
                return;
            try
            {
                var c = stock.Container;
                bool changed = false;
                if (c.Capacity != _hostCapacity)
                {
                    c.SetCapacity(_hostCapacity, force: true);
                    changed = true;
                }
                if (UsedCapacityField != null)
                {
                    int want = CrateSum(c) + _hostUsedOffset;
                    if (c.GetCratesCount() != want)
                    {
                        UsedCapacityField.SetValue(c, want);
                        changed = true;
                    }
                }
                if (changed)
                {
                    CapacityFixed++;
                    c.InvokeResourcesUpdated();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("Lager-Kapazität angleichen: " + e.Message);
            }
        }

        /// <summary>Gast-Takt: Kapazität nachziehen (eigene Lager-Ereignisse setzen sie neu), Zähler bei Dauerabweichung angleichen.</summary>
        public void GuestFrame()
        {
            float now = Time.realtimeSinceStartup;
            if (!_have || now < _nextGuestCheck)
                return;
            _nextGuestCheck = now + 1f;
            var stock = GameAccess.Game<StockroomsController>();
            if (stock == null)
                return;
            EnforceCapacity(stock);

            bool diff = CitizensController.CitizensCount != _host.Citizens
                        || CitizensController.ChildrenCount != _host.Children
                        || Gameplay.Units.SquadsController.SoldiersCount != _host.Soldiers;
            if (!diff)
            {
                _mismatchSince = -1f;
                return;
            }
            if (_mismatchSince < 0f)
            {
                _mismatchSince = now;
                return;
            }
            if (now - _mismatchSince < CorrectAfter)
                return;
            _mismatchSince = -1f;
            try
            {
                Plugin.Log.LogInfo($"Zähler angleichen: Bürger {CitizensController.CitizensCount}->{_host.Citizens}, Kinder {CitizensController.ChildrenCount}->{_host.Children}, Soldaten {Gameplay.Units.SquadsController.SoldiersCount}->{_host.Soldiers}");
                CitizensCountSetter?.Invoke(null, new object[] { _host.Citizens });
                ChildrenCountSetter?.Invoke(null, new object[] { _host.Children });
                SoldiersCountSetter?.Invoke(null, new object[] { _host.Soldiers });
                CountersFixed++;
                stock.Container.InvokeResourcesUpdated(); // Essen-Gruppe rechnet „reicht für x Tage“ neu
                RefreshPeoplePanel(_host.Homeless);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Zähler angleichen: " + e.Message);
            }
        }

        /// <summary>Bevölkerungs-Leiste (CitizensPanelCanvas/WorkersPanelController) neu zeichnen.</summary>
        private void RefreshPeoplePanel(int homeless)
        {
            try
            {
                var canvas = Resources.FindObjectsOfTypeAll<UITools.CitizensPanelCanvas>()
                    .FirstOrDefault(x => x != null && x.gameObject.scene.IsValid());
                if (canvas == null)
                    return;
                AccessTools.Method(typeof(UITools.CitizensPanelCanvas), "UpdateChildren")?.Invoke(canvas, null);
                var wp = AccessTools.Field(typeof(UITools.CitizensPanelCanvas), "workersPanel")?.GetValue(canvas) as WorkersPanelController;
                if (wp != null)
                {
                    wp.SetSquadMembers();
                    if (homeless >= 0)
                    {
                        wp.SetHomelessCount(homeless);
                        _lastHomelessShown = homeless;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("Bevölkerungs-Leiste: " + e.Message);
            }
        }

        // ================================================================ Sperre (UnitPatches)

        /// <summary>Prefix ForceAddResource/TryRemoveResource: Tageswerte dieses Rohstoffs merken (nur Gast).</summary>
        public static float[] LockBefore(MainResourcesContainer c, ResourceID id, bool puppet)
        {
            if (!puppet || c == null)
                return null;
            var s = new float[3];
            for (int k = 0; k < 3; k++)
            {
                var d = Stat(c, k);
                s[k] = d != null && d.TryGetValue(id, out float v) ? v : float.NaN;
            }
            return s;
        }

        /// <summary>Postfix: eigene Fortschreibung des Gastes zurücknehmen.</summary>
        public static void LockAfter(MainResourcesContainer c, ResourceID id, float[] s)
        {
            if (s == null || c == null)
                return;
            for (int k = 0; k < 3; k++)
            {
                var d = Stat(c, k);
                if (d == null)
                    continue;
                bool had = d.TryGetValue(id, out float now);
                if (float.IsNaN(s[k]))
                {
                    if (had) { d.Remove(id); Locked++; }
                }
                else if (!had || now != s[k])
                {
                    d[id] = s[k];
                    Locked++;
                }
            }
        }

        /// <summary>Prefix TimeOfDayChanged: alle 6 Statistiken merken (nur Gast; der Vortag kommt vom Host).</summary>
        public static Dictionary<ResourceID, float>[] DayBefore(MainResourcesContainer c, bool puppet)
        {
            if (!puppet || c == null)
                return null;
            var s = new Dictionary<ResourceID, float>[StatCount];
            for (int k = 0; k < StatCount; k++)
            {
                var d = Stat(c, k);
                s[k] = d == null ? null : new Dictionary<ResourceID, float>(d);
            }
            return s;
        }

        public static void DayAfter(MainResourcesContainer c, Dictionary<ResourceID, float>[] s)
        {
            if (s == null || c == null)
                return;
            for (int k = 0; k < StatCount; k++)
            {
                var d = Stat(c, k);
                if (d == null || s[k] == null)
                    continue;
                if (d.Count == s[k].Count && d.All(kv => s[k].TryGetValue(kv.Key, out float v) && v == kv.Value))
                    continue;
                d.Clear();
                foreach (var kv in s[k]) d[kv.Key] = kv.Value;
                Locked++;
            }
        }

        // ================================================================ Status und Tests

        private static string F(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        /// <summary>Zeilen für die Statusdatei: Vortag (genau), heute (läuft), Kapazität, Zähler.</summary>
        public static IEnumerable<string> StatusLines()
        {
            var stock = GameAccess.Game<StockroomsController>();
            if (stock == null)
            {
                yield return "rstat -";
                yield break;
            }
            var c = stock.Container;
            foreach (var line in Lines("prev", c, 3))
                yield return line;
            foreach (var line in Lines("cur", c, 0))
                yield return line;
            yield return $"rstat cap cap={c.Capacity} used={c.GetCratesCount()} free={F(stock.GetSpaceLeft())} oldestFood={F(c.GetOldestFoodCount())} oldestMeat={F(c.GetOldestMeatCount())}";
            var n = ReadCounters();
            yield return $"rstat pop citizens={n.Citizens} children={n.Children} soldiers={n.Soldiers} homeless={n.Homeless} quarters={n.Quarters} workers={n.Workers} available={n.Available}";
        }

        private static IEnumerable<string> Lines(string tag, MainResourcesContainer c, int first)
        {
            var p = Stat(c, first) ?? new Dictionary<ResourceID, float>();
            var s = Stat(c, first + 1) ?? new Dictionary<ResourceID, float>();
            var u = Stat(c, first + 2) ?? new Dictionary<ResourceID, float>();
            var ids = new SortedSet<int>(p.Keys.Concat(s.Keys).Concat(u.Keys).Select(x => (int)x));
            foreach (int i in ids)
            {
                var id = (ResourceID)i;
                p.TryGetValue(id, out float pv);
                s.TryGetValue(id, out float sv);
                u.TryGetValue(id, out float uv);
                if (Mathf.Abs(pv) < 0.05f && Mathf.Abs(sv) < 0.05f && Mathf.Abs(uv) < 0.05f)
                    continue;
                yield return $"rstat {tag} {id} p={F(pv)} s={F(sv)} u={F(uv)} d={F(pv + sv - uv)}";
            }
        }

        public string StatusLine() =>
            $"rstatSent={Sent} rstatApplied={Applied} rstatLocked={Locked} capFixed={CapacityFixed} countersFixed={CountersFixed}";

        /// <summary>
        /// Testbefehle: "rstat status" | "rstat tip" (Zahlen aller Rohstoff-Tooltips der Leiste, wie sie gezeigt würden)
        /// | "rstat prod|scav &lt;Rohstoff&gt; &lt;Menge&gt;" (ins Lager mit Grund Production/Scavenge)
        /// | "rstat use &lt;Rohstoff&gt; &lt;Menge&gt;" (aus dem Lager nehmen). Beim Gast prüfen prod/scav/use die Sperre.
        /// </summary>
        public string Qa(string[] a)
        {
            string op = a.Length > 1 ? a[1] : "status";
            var stock = GameAccess.Game<StockroomsController>();
            if (stock == null)
                return "kein Lager";
            switch (op)
            {
                case "prod":
                case "scav":
                case "use":
                {
                    var id = (ResourceID)Enum.Parse(typeof(ResourceID), a[2]);
                    float qty = a.Length > 3 ? float.Parse(a[3], CultureInfo.InvariantCulture) : 5f;
                    if (op == "use")
                        return id + " -" + qty + " " + stock.Container.TryRemoveResource(id, qty);
                    stock.Container.ForceAddResource(id, qty, op == "prod" ? ResourceAddReason.Production : ResourceAddReason.Scavenge);
                    return id + " +" + qty + " " + op;
                }
                case "tip":
                    return Tooltips();
                default:
                    return string.Join(" / ", StatusLines()) + " / " + StatusLine();
            }
        }

        /// <summary>Text aller Tooltips der Rohstoffleiste neu bauen (ResourceUi.UpdateTooltip) und die Zahlen darin ausgeben.</summary>
        private static string Tooltips()
        {
            var parts = new List<string>();
            try
            {
                foreach (var ui in Resources.FindObjectsOfTypeAll<ResourceUi>())
                {
                    if (ui == null || !ui.gameObject.scene.IsValid())
                        continue;
                    UI.Tooltips.TooltipTriggerUI trig;
                    string name;
                    if (ui is ResourceGroupRepresentative)
                    {
                        trig = RepTooltipField?.GetValue(ui) as UI.Tooltips.TooltipTriggerUI;
                        name = "group:" + (ui.transform.parent != null ? ui.transform.parent.name : ui.name);
                    }
                    else
                    {
                        ResUiUpdateTooltip?.Invoke(ui, null);
                        trig = ResUiTooltipField?.GetValue(ui) as UI.Tooltips.TooltipTriggerUI;
                        name = ui.ResourceType.ToString();
                    }
                    string text = trig != null ? TooltipTextField?.GetValue(trig) as string : null;
                    if (text == null)
                        continue;
                    text = Regex.Replace(text, "<[^>]*>", "");
                    var nums = Regex.Matches(text, @"-?\d+(?:[.,]\d+)?").Cast<Match>().Select(m => m.Value);
                    parts.Add(name + "=" + string.Join(",", nums));
                }
            }
            catch (Exception e)
            {
                return "Fehler: " + e.Message;
            }
            parts.Sort(StringComparer.Ordinal);
            return parts.Count == 0 ? "keine Leiste" : string.Join(" ", parts);
        }
    }
}
