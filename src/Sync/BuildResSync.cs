using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Controllers;
using Gameplay.Buildings;
using Gameplay.GameResources;
using Gameplay.InGameResources;
using Gameplay.Rebuilding;
using Gameplay.Units.Player.Workers.WorkSystem;
using Gameplay.Units.Workers.WorkSystem;
using Gameplay.Units.Workers.WorkSystem.Works;
using Gameplay.Vehicles;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Baustellen-Rohstoffe und Baufortschritt (Fehler aus dem Spiel zu zweit: Bau-Fenster beim Gast 15/65 Holz, 0.0 %).
    ///
    /// Ursache: Das Bau-Fenster (StructureInProgressPanelInfo, StructureInRepairPanelInfo, CustomizationProcessPanel,
    /// BuildingCreationPanelInfo, SplitWorkPanel, VehicleInRepairPanel) zeigt je Rohstoff
    /// UsedResources[r] + ResourcesContainer.GetCratesCount(r) von TotalCost[r] (ConstructableWork.ForceResourcesUpdate /
    /// ResourceUsed) und den Fortschritt aus WorkBase.Progress (OnProgressUpdated). Beim Gast laufen die Arbeiter als
    /// Marionetten (WorkModule.ExecuteWork gesperrt), also verbraucht dort niemand Kisten: UsedResources bleibt leer, Cost
    /// voll, TimeConstructing/Progress 0. Die Kisten im Haus kamen schon über ContainerSync, darum „15/65“.
    ///
    /// Host: 1×/s alle laufenden Bau-Arbeiten mit Kosten (ConstructableWork: Umbau/Bau, Erweitern, Reparatur, Anpassung,
    /// Anpassungs-Reparatur, eigenes Gebäude, Teilung; dazu VehicleRepairWork), Schlüssel = Ort (b Gebäude-ID | s Anlagen-ID |
    /// v Fahrzeug-ID) + Platz der Arbeit am Ort. Gesendet werden Restkosten, Gesamtkosten, verbrauchte Rohstoffe, Zeiten und
    /// Fortschritt, bei Anlagen und Fahrzeugen auch die gelieferten Kisten (bei Häusern macht das ContainerSync).
    /// Nur geänderte Einträge (Fortschritt auf 0,1 % gerundet), alle 10 s alle.
    /// Gast: setzt die Werte unter NetApplying in die vorhandene Arbeit (in place), setzt den Fortschritt mit SetProgress
    /// (Fenster, Haus wächst sichtbar) und ruft ForceResourcesUpdate (Fenster-Leiste). Lebenspunkte bleiben dabei unberührt
    /// (UnitPatches sperren UpdateStructureHealthDuringBuilding und RepairStructureWork.SetHealthToStructure, solange
    /// Applying gilt) – die kommen aus BuildingSync/StructureSync. Fortschritt von Anlagen-Bau (StructureSync) und
    /// Fahrzeug-Reparatur (SquadActionSync) setzt das jeweilige Modul selbst.
    /// Wird eine Arbeit beim Gast gelöscht, verwirft er die übernommenen verbrauchten Rohstoffe vorher (sonst ließe
    /// ConstructableWork.DeleteWork sie als Kisten fallen); was zurückkommt, rechnet der Host.
    /// </summary>
    internal sealed class BuildResSync
    {
        private const float SendInterval = 1f;
        private const float ForceInterval = 10f;
        private const float ReapplyInterval = 1f;
        private const float ForgetAfter = 30f;
        private const int MaxPerMessage = 200;

        private enum Loc : byte { Building = 0, Structure = 1, Vehicle = 2 }

        private enum Slot : byte { None = 0, Build = 1, Repair = 2, CustomRepair = 3, Custom = 4, Extra = 5, Current = 6, Vehicle = 7 }

        /// <summary>Gast setzt gerade Host-Werte: Lebenspunkte-Nebenwirkungen des Fortschritts sperren (UnitPatches).</summary>
        public static bool Applying;

        private static readonly MethodInfo TimeConstructingSetter = AccessTools.PropertySetter(typeof(ConstructableWork), "TimeConstructing");
        private static readonly MethodInfo UseResourceMethod = AccessTools.Method(typeof(ConstructableWork), "UseResourceForConstruction",
            new[] { typeof(ResourceID), typeof(int) });

        private readonly Session _session;
        private readonly Dictionary<string, string> _sent = new Dictionary<string, string>();
        private float _nextSend;
        private float _nextForce;

        // Gast: letzter Stand je Schlüssel (wird jede Sekunde erneut angewandt, falls die Arbeit erst später entsteht)
        private readonly Dictionary<string, Entry> _last = new Dictionary<string, Entry>();
        private float _nextReapply;

        public int Sent { get; private set; }
        public int Received { get; private set; }
        public int Applied { get; private set; }
        public int Missing { get; private set; }
        public string LastKey { get; private set; } = "";

        public BuildResSync(Session session)
        {
            _session = session;
        }

        public void ResendAll()
        {
            _sent.Clear();
            _nextSend = 0f;
        }

        public void Reset()
        {
            _sent.Clear();
            _last.Clear();
            _nextSend = 0f;
            _nextForce = 0f;
            Applying = false;
        }

        // ================================================================ Eintrag

        private sealed class Entry
        {
            public Loc Loc;
            public string Id = "";
            public Slot Slot;
            public string Type = "";
            public float Progress, TimeCons, TimeTo, CurRes, ConsumeRes;
            public List<KeyValuePair<int, int>> Cost = new List<KeyValuePair<int, int>>();
            public List<KeyValuePair<int, int>> Total = new List<KeyValuePair<int, int>>();
            public List<KeyValuePair<int, int>> Used = new List<KeyValuePair<int, int>>();
            public bool HasBox;
            public int Capacity;
            public List<KeyValuePair<int, float>> Box = new List<KeyValuePair<int, float>>();
            public float Received;

            public string Key => LocKey(Loc, Id) + ":" + (int)Slot;

            public string Sig(bool withProgress)
            {
                var sb = new StringBuilder();
                sb.Append(Type).Append('|');
                if (withProgress)
                    sb.Append(Mathf.RoundToInt(Progress * 1000f)).Append('|');
                sb.Append(Mathf.RoundToInt(TimeTo)).Append('|');
                foreach (var kv in Cost) sb.Append(kv.Key).Append(':').Append(kv.Value).Append(',');
                sb.Append('|');
                foreach (var kv in Total) sb.Append(kv.Key).Append(':').Append(kv.Value).Append(',');
                sb.Append('|');
                foreach (var kv in Used) sb.Append(kv.Key).Append(':').Append(kv.Value).Append(',');
                sb.Append('|');
                if (HasBox)
                    foreach (var kv in Box) sb.Append(kv.Key).Append(':').Append(kv.Value.ToString("0.##", CultureInfo.InvariantCulture)).Append(',');
                return sb.ToString();
            }

            public void Write(BinaryWriter w)
            {
                w.Write((byte)Loc);
                w.Write(Id ?? "");
                w.Write((byte)Slot);
                w.Write(Type ?? "");
                w.Write(Progress); w.Write(TimeCons); w.Write(TimeTo); w.Write(CurRes); w.Write(ConsumeRes);
                WriteList(w, Cost);
                WriteList(w, Total);
                WriteList(w, Used);
                w.Write(HasBox);
                if (HasBox)
                {
                    w.Write(Capacity);
                    w.Write(Box.Count);
                    foreach (var kv in Box) { w.Write(kv.Key); w.Write(kv.Value); }
                }
            }

            public static Entry Read(BinaryReader r)
            {
                var e = new Entry
                {
                    Loc = (Loc)r.ReadByte(),
                    Id = r.ReadString(),
                    Slot = (Slot)r.ReadByte(),
                    Type = r.ReadString(),
                    Progress = r.ReadSingle(), TimeCons = r.ReadSingle(), TimeTo = r.ReadSingle(),
                    CurRes = r.ReadSingle(), ConsumeRes = r.ReadSingle(),
                };
                e.Cost = ReadList(r);
                e.Total = ReadList(r);
                e.Used = ReadList(r);
                e.HasBox = r.ReadBoolean();
                if (e.HasBox)
                {
                    e.Capacity = r.ReadInt32();
                    int n = r.ReadInt32();
                    if (n < 0 || n > 1000)
                        throw new InvalidDataException("zu viele Kisten-Arten");
                    for (int i = 0; i < n; i++)
                        e.Box.Add(new KeyValuePair<int, float>(r.ReadInt32(), r.ReadSingle()));
                }
                return e;
            }
        }

        private static void WriteList(BinaryWriter w, List<KeyValuePair<int, int>> list)
        {
            w.Write(list.Count);
            foreach (var kv in list) { w.Write(kv.Key); w.Write(kv.Value); }
        }

        private static List<KeyValuePair<int, int>> ReadList(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 1000)
                throw new InvalidDataException("zu viele Rohstoffe");
            var list = new List<KeyValuePair<int, int>>(n);
            for (int i = 0; i < n; i++)
                list.Add(new KeyValuePair<int, int>(r.ReadInt32(), r.ReadInt32()));
            return list;
        }

        private static List<KeyValuePair<int, int>> ToList(Dictionary<ResourceID, int> d)
        {
            var list = new List<KeyValuePair<int, int>>();
            if (d != null)
                foreach (var kv in d.OrderBy(k => (int)k.Key))
                    list.Add(new KeyValuePair<int, int>((int)kv.Key, kv.Value));
            return list;
        }

        private static void Fill(Dictionary<ResourceID, int> d, List<KeyValuePair<int, int>> list)
        {
            if (d == null)
                return;
            // in place: Fenster und Arbeit halten dasselbe Wörterbuch (ConstructionCost => Cost)
            d.Clear();
            foreach (var kv in list)
                d[(ResourceID)kv.Key] = kv.Value;
        }

        private static string LocKey(Loc loc, string id) => (loc == Loc.Building ? "b" : loc == Loc.Structure ? "s" : "v") + id;

        // ================================================================ Arbeiten finden

        private static Slot SlotOf(Structure s, WorkBase w)
        {
            if (s == null || w == null) return Slot.None;
            if (ReferenceEquals(w, s.BuildStructureWork)) return Slot.Build;
            if (ReferenceEquals(w, s.RepairWork)) return Slot.Repair;
            if (ReferenceEquals(w, s.CustomizationRepairWork)) return Slot.CustomRepair;
            if (ReferenceEquals(w, s.CustomizationWork)) return Slot.Custom;
            if (s is Building b && ReferenceEquals(w, b.ExtraAdaptationWork)) return Slot.Extra;
            if (ReferenceEquals(w, s.CurrentWork)) return Slot.Current;
            return Slot.None;
        }

        private static WorkBase WorkAt(Structure s, Slot slot)
        {
            switch (slot)
            {
                case Slot.Build: return s.BuildStructureWork;
                case Slot.Repair: return s.RepairWork;
                case Slot.CustomRepair: return s.CustomizationRepairWork;
                case Slot.Custom: return s.CustomizationWork;
                case Slot.Extra: return (s as Building)?.ExtraAdaptationWork;
                case Slot.Current: return s.CurrentWork;
                default: return null;
            }
        }

        private static bool Locate(Structure s, out Loc loc, out string id)
        {
            loc = Loc.Building;
            id = null;
            if (s is Building b)
            {
                id = b.ID.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            if (s is PlaceableStructure ps)
            {
                loc = Loc.Structure;
                id = ps.GetId();
                return !string.IsNullOrEmpty(id);
            }
            return false;
        }

        /// <summary>Laufende Bau-Arbeiten mit Kosten (Host: zum Senden; Gast: für Status und Testbefehle).</summary>
        private static List<KeyValuePair<Entry, WorkBase>> Collect()
        {
            var result = new List<KeyValuePair<Entry, WorkBase>>();
            var seen = new HashSet<WorkBase>();
            var keys = new HashSet<string>();
            void Take(WorkBase w)
            {
                if (w == null || w.IsDone || !seen.Add(w))
                    return;
                Entry e = null;
                try { e = Make(w); }
                catch (Exception ex) { Plugin.Log.LogDebug("Baustelle lesen: " + ex.Message); }
                if (e != null && keys.Add(e.Key))
                    result.Add(new KeyValuePair<Entry, WorkBase>(e, w));
            }
            var works = GameAccess.Game<WorkController>()?.Works;
            if (works != null)
                foreach (var w in works)
                    if (w is ConstructableWork || w is VehicleRepairWork)
                        Take(w);
            var bc = GameAccess.Game<BuildingsController>();
            if (bc?.BuildingsWithInteraction != null)
                foreach (var b in bc.BuildingsWithInteraction)
                    if (b != null && b.CurrentWork is ConstructableWork cw)
                        Take(cw);
            foreach (var s in StructureSync.All())
                if (s != null && s.CurrentWork is ConstructableWork cw)
                    Take(cw);
            return result;
        }

        private static Entry Make(WorkBase w)
        {
            if (w is ConstructableWork cw)
            {
                var s = cw.Structure;
                if (s == null || !Locate(s, out var loc, out var id))
                    return null;
                var slot = SlotOf(s, cw);
                if (slot == Slot.None)
                    return null;
                var e = new Entry
                {
                    Loc = loc, Id = id, Slot = slot, Type = cw.GetType().Name,
                    Progress = cw.Progress, TimeCons = cw.TimeConstructing, TimeTo = cw.TimeToConstruct,
                    CurRes = cw.CurrentResourceTime, ConsumeRes = cw.ConsumeResourceTime,
                    Cost = ToList(cw.Cost), Total = ToList(cw.TotalCost), Used = ToList(cw.UsedResources),
                };
                // Kisten im Haus kommen über ContainerSync; bei Anlagen schickt sie niemand sonst
                if (loc == Loc.Structure)
                    FillBox(e, s.ResourcesContainer);
                return e;
            }
            if (w is VehicleRepairWork vr)
            {
                var v = vr.VehicleBeingRepaired;
                if (v == null || string.IsNullOrEmpty(v.Id))
                    return null;
                var e = new Entry
                {
                    Loc = Loc.Vehicle, Id = v.Id, Slot = Slot.Vehicle, Type = nameof(VehicleRepairWork),
                    Progress = vr.Progress, TimeCons = vr.TimeRepairing, TimeTo = vr.TimeToRepair,
                    CurRes = vr.CurrentResourceTime, ConsumeRes = vr.ConsumeResourceTime,
                    Cost = ToList(vr.Cost), Total = ToList(vr.StartingCost), Used = ToList(vr.UsedResources),
                };
                FillBox(e, vr.ResourcesContainer);
                return e;
            }
            return null;
        }

        private static void FillBox(Entry e, ResourcesContainer c)
        {
            if (c == null || ReferenceEquals(c, GameAccess.Game<StockroomsController>()?.Container))
                return;
            e.HasBox = true;
            e.Capacity = c.Capacity;
            foreach (var kv in c.Resources.OrderBy(k => (int)k.Key))
                if (Mathf.Abs(kv.Value) > 0.0001f)
                    e.Box.Add(new KeyValuePair<int, float>((int)kv.Key, kv.Value));
        }

        // ================================================================ Host

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend || _session.ActiveGuestCount == 0 || !GameAccess.IsGameStarted)
                return;
            _nextSend = now + SendInterval;
            bool full = now >= _nextForce;
            if (full)
                _nextForce = now + ForceInterval;

            var list = Collect();
            var changed = new List<Entry>();
            var present = new HashSet<string>();
            foreach (var kv in list)
            {
                var e = kv.Key;
                present.Add(e.Key);
                string sig = e.Sig(true);
                if (!full && _sent.TryGetValue(e.Key, out var last) && last == sig)
                    continue;
                _sent[e.Key] = sig;
                changed.Add(e);
            }
            foreach (var k in _sent.Keys.Where(k => !present.Contains(k)).ToList())
                _sent.Remove(k);
            if (changed.Count == 0)
                return;

            for (int start = 0; start < changed.Count; start += MaxPerMessage)
            {
                int count = Math.Min(MaxPerMessage, changed.Count - start);
                int first = start;
                _session.BroadcastToGuests(Protocol.Build(Msg.BuildResState, w =>
                {
                    w.Write(count);
                    for (int i = first; i < first + count; i++)
                        changed[i].Write(w);
                }));
            }
            Sent += changed.Count;
        }

        // ================================================================ Gast

        public void GuestOnState(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > MaxPerMessage)
                throw new InvalidDataException("zu viele Baustellen");
            float now = Time.realtimeSinceStartup;
            var fresh = new List<Entry>(n);
            for (int i = 0; i < n; i++)
            {
                var e = Entry.Read(r);
                e.Received = now;
                _last[e.Key] = e;
                fresh.Add(e);
            }
            Received += n;
            foreach (var e in fresh)
                TryApply(e);
        }

        public void GuestFrame()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextReapply || _last.Count == 0)
                return;
            _nextReapply = now + ReapplyInterval;
            foreach (var k in _last.Where(kv => now - kv.Value.Received > ForgetAfter).Select(kv => kv.Key).ToList())
                _last.Remove(k);
            foreach (var e in _last.Values.ToList())
                TryApply(e);
        }

        private void TryApply(Entry e)
        {
            try
            {
                if (e.Loc == Loc.Vehicle)
                {
                    var v = FindVehicle(e.Id);
                    var vr = v != null ? v.RepairWork : null;
                    if (vr == null) { Missing++; return; }
                    var have = Make(vr);
                    if (have != null && have.Sig(false) == e.Sig(false) && Mathf.Abs(vr.TimeRepairing - e.TimeCons) < 0.5f)
                        return;
                    ApplyVehicle(vr, e);
                }
                else
                {
                    var s = FindStructure(e.Loc, e.Id);
                    var w = s != null ? WorkAt(s, e.Slot) as ConstructableWork : null;
                    if (w == null || w.IsDone || w.GetType().Name != e.Type) { Missing++; return; }
                    // Anlagen-Bau: Fortschritt setzt StructureSync
                    bool setProgress = !(e.Loc == Loc.Structure && e.Slot == Slot.Build);
                    var have = Make(w);
                    if (have != null && have.Sig(setProgress) == e.Sig(setProgress))
                        return;
                    ApplyWork(s, w, e, setProgress);
                }
                Applied++;
                LastKey = e.Key;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Baustelle {e.Key}: {ex.Message}");
            }
        }

        private static Structure FindStructure(Loc loc, string id)
        {
            if (loc == Loc.Building)
            {
                if (!long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bid))
                    return null;
                return GameAccess.Game<BuildingsController>()?.GetBuildingById(bid);
            }
            foreach (var s in StructureSync.All())
                if (s != null && s.GetId() == id)
                    return s;
            return null;
        }

        private static Vehicle FindVehicle(string id)
        {
            var vehicles = GameAccess.Game<VehiclesController>()?.Vehicles;
            if (vehicles != null)
                foreach (var v in vehicles)
                    if (v != null && v.Id == id)
                        return v;
            return null;
        }

        private static void ApplyWork(Structure s, ConstructableWork w, Entry e, bool setProgress)
        {
            bool wasApplying = Applying;
            UnitSync.NetApplying = true;
            Applying = true;
            try
            {
                if (e.TimeTo > 0f && Mathf.Abs(w.TimeToConstruct - e.TimeTo) > 0.01f)
                    w.SetTimeToConstruct(e.TimeTo);
                // nicht SetTimeConstructing: das rechnet den Forschungs-Bonus noch einmal ein
                if (TimeConstructingSetter != null && Mathf.Abs(w.TimeConstructing - e.TimeCons) > 0.001f)
                    TimeConstructingSetter.Invoke(w, new object[] { e.TimeCons });
                w.SetCurrentResourceTime(e.CurRes);
                w.SetConsumeResourceTime(e.ConsumeRes);
                Fill(w.TotalCost, e.Total);
                Fill(w.Cost, e.Cost);
                Fill(w.UsedResources, e.Used);
                if (e.HasBox)
                    ApplyBox(w.ResourcesContainer, e);
                if (setProgress && Mathf.Abs(w.Progress - e.Progress) > 0.0005f)
                {
                    // Fenster (OnProgressUpdated), Haus-Bild (SetConstructionProgress, PartialBuildingHandler) wie beim Host
                    w.SetProgress(e.Progress);
                    w.SetPreviousProgress(e.Progress);
                }
                w.ForceResourcesUpdate();
            }
            finally
            {
                Applying = wasApplying;
                UnitSync.NetApplying = false;
            }
        }

        private static void ApplyVehicle(VehicleRepairWork vr, Entry e)
        {
            bool wasApplying = Applying;
            UnitSync.NetApplying = true;
            Applying = true;
            try
            {
                if (e.TimeTo > 0f)
                    vr.SetTimeToRepair(e.TimeTo);
                vr.SetTimeRepairing(e.TimeCons);
                vr.SetCurrentResourceTime(e.CurRes);
                vr.SetConsumeResourceTime(e.ConsumeRes);
                Fill(vr.StartingCost, e.Total);
                Fill(vr.Cost, e.Cost);
                Fill(vr.UsedResources, e.Used);
                if (e.HasBox)
                    ApplyBox(vr.ResourcesContainer, e);
                vr.ForceResourcesUpdate();
            }
            finally
            {
                Applying = wasApplying;
                UnitSync.NetApplying = false;
            }
        }

        private static void ApplyBox(ResourcesContainer c, Entry e)
        {
            if (c == null || ReferenceEquals(c, GameAccess.Game<StockroomsController>()?.Container))
                return;
            var want = new List<ResourceQuantity>(e.Box.Count);
            foreach (var kv in e.Box)
                want.Add(new ResourceQuantity((ResourceID)kv.Key, kv.Value));
            if (c.Capacity < e.Capacity)
                c.SetCapacity(e.Capacity, force: true);
            c.Clear();
            c.LoadResources(want);
            c.InvokeResourcesUpdated();
        }

        // ================================================================ Status / Test

        private static string ResText(Entry e, WorkBase w)
        {
            // so wie das Fenster: verbraucht + Kisten vor Ort / gesamt, dazu Rest
            ResourcesContainer box = null;
            if (w is ConstructableWork cw) box = cw.ResourcesContainer;
            else if (w is VehicleRepairWork vr) box = vr.ResourcesContainer;
            var sb = new StringBuilder();
            foreach (var t in e.Total)
            {
                var id = (ResourceID)t.Key;
                int used = e.Used.Where(u => u.Key == t.Key).Select(u => u.Value).FirstOrDefault();
                int crates = box != null ? box.GetCratesCount(id) : 0;
                int rest = e.Cost.Where(c => c.Key == t.Key).Select(c => c.Value).FirstOrDefault();
                if (sb.Length > 0) sb.Append(',');
                sb.Append(id.ToString().Replace("res_", "")).Append(' ').Append(used + crates).Append('/').Append(t.Value)
                  .Append(" rest").Append(rest).Append(" used").Append(used);
            }
            return sb.ToString();
        }

        /// <summary>Für die Statusdatei: je Baustelle das, was das Bau-Fenster zeigt.</summary>
        public static IEnumerable<string> StatusLines()
        {
            List<KeyValuePair<Entry, WorkBase>> list;
            try { list = Collect(); }
            catch (Exception e) { list = new List<KeyValuePair<Entry, WorkBase>>(); Plugin.Log.LogDebug("bres: " + e.Message); }
            list = list.OrderBy(kv => kv.Key.Key, StringComparer.Ordinal).ToList();
            uint hash = 2166136261;
            foreach (var kv in list)
                foreach (char ch in kv.Key.Key + kv.Key.Type + kv.Key.Sig(false))
                    hash = (hash ^ ch) * 16777619;
            yield return $"bres n={list.Count} hash={hash:x8}";
            int shown = 0;
            foreach (var kv in list)
            {
                if (++shown > 15) break;
                var e = kv.Key;
                yield return $"bres {e.Key} {e.Type} p={(e.Progress * 100f).ToString("0.0", CultureInfo.InvariantCulture)} {ResText(e, kv.Value)}";
            }
        }

        public string StatusLine() =>
            $"bresSent={Sent} received={Received} applied={Applied} missing={Missing} cached={_last.Count} last={LastKey}";

        private static KeyValuePair<Entry, WorkBase> Pick(string[] a, int idx, Vector3 hqPos)
        {
            var list = Collect();
            if (list.Count == 0)
                return default;
            string want = a.Length > idx ? a[idx] : null;
            if (!string.IsNullOrEmpty(want) && want != "near")
            {
                var hit = list.FirstOrDefault(kv => kv.Key.Key == want || kv.Key.Key.StartsWith(want, StringComparison.Ordinal));
                if (hit.Key != null)
                    return hit;
            }
            // nächste Baustelle zum HQ, Häuser zuerst
            return list.OrderBy(kv => kv.Key.Loc == Loc.Building ? 0 : 1)
                       .ThenBy(kv => kv.Value is ConstructableWork cw && cw.Structure != null ? (cw.Structure.transform.position - hqPos).sqrMagnitude : float.MaxValue)
                       .First();
        }

        /// <summary>
        /// Testbefehle: "bres status" | "bres step [n] [Schlüssel|near]" (Host: n Kisten liefern und verbauen, Zeit und Fortschritt
        /// wie ConstructableWork.Execute weiterzählen) | "bres deliver [n] [Schlüssel]" (Host: n Kisten an die Baustelle legen)
        /// | "bres panel [Schlüssel]" (Fenster der Baustelle öffnen, auch beim Gast) | "bres ui" (was das offene Fenster zeigt).
        /// </summary>
        public string Qa(string[] a, Vector3 hqPos)
        {
            string what = a.Length > 1 ? a[1] : "status";
            bool guest = _session.Role == Role.Guest;
            switch (what)
            {
                case "status":
                    return string.Join(" ; ", StatusLines()) + " | " + StatusLine();

                case "step":
                case "deliver":
                {
                    if (guest) return "nur beim Host";
                    int n = a.Length > 2 && int.TryParse(a[2], out int nn) ? Mathf.Max(1, nn) : 5;
                    var t = Pick(a, 3, hqPos);
                    if (!(t.Value is ConstructableWork w)) return "keine Baustelle (Haus/Anlage) mit Kosten";
                    var box = w.ResourcesContainer;
                    int done = 0;
                    for (int i = 0; i < n; i++)
                    {
                        var res = w.Cost.Where(c => c.Value - box.GetCratesCount(c.Key) > 0).Select(c => c.Key).FirstOrDefault();
                        if (what == "step")
                            res = w.Cost.Where(c => c.Value > 0).Select(c => c.Key).FirstOrDefault();
                        if (res == ResourceID.None)
                            break;
                        if (box.GetCratesCount(res) == 0 && !box.AddCrates(res, 1, ResourceAddReason.None))
                        {
                            box.SetCapacity(box.Capacity + 1, force: true);
                            box.AddCrates(res, 1, ResourceAddReason.None);
                        }
                        if (what == "step")
                        {
                            if (UseResourceMethod == null || !(bool)UseResourceMethod.Invoke(w, new object[] { res, 1 }))
                                break;
                            float sum = Mathf.Max(1, w.TotalCost.Values.Sum());
                            float tc = Mathf.Min(w.TimeConstructing + w.TimeToConstruct / sum, w.TimeToConstruct * 0.98f);
                            TimeConstructingSetter?.Invoke(w, new object[] { tc });
                            w.SetProgress(Mathf.Clamp01(w.TimeConstructing / Mathf.Max(0.001f, w.TimeToConstruct)));
                        }
                        done++;
                    }
                    if (what == "deliver")
                        box.InvokeResourcesUpdated();
                    _nextSend = 0f;
                    return $"{what} {done} -> {t.Key.Key} p={(w.Progress * 100f).ToString("0.0", CultureInfo.InvariantCulture)} {ResText(Make(w) ?? t.Key, w)}";
                }

                case "panel":
                {
                    var t = Pick(a, 2, hqPos);
                    if (t.Key == null) return "keine Baustelle";
                    if (t.Value is ConstructableWork w && w.Structure != null)
                    {
                        w.Structure.ShowProperInfoPanel();
                        return "Fenster " + t.Key.Key + " " + InfoPanelController.Instance?.CurrentDisplayedPanel?.GetType().Name;
                    }
                    return "Fahrzeug-Fenster nicht unterstützt: " + t.Key.Key;
                }

                case "ui":
                    return PanelText();

                default:
                    return "bres status|step [n] [key]|deliver [n] [key]|panel [key]|ui";
            }
        }

        /// <summary>Liest das offene Info-Fenster: Fortschrittsbalken (Text) und Rohstoff-Leiste (Text je Rohstoff).</summary>
        private static string PanelText()
        {
            var panel = InfoPanelController.Instance?.CurrentDisplayedPanel;
            if (panel == null || !panel.gameObject.activeInHierarchy)
                return "kein Fenster offen";
            var sb = new StringBuilder(panel.GetType().Name);
            foreach (var f in AccessTools.GetDeclaredFields(panel.GetType()))
            {
                object v;
                try { v = f.GetValue(panel); } catch { continue; }
                if (v == null) continue;
                if (f.FieldType.Name == "ProgressBarTextValue")
                {
                    sb.Append(" | ").Append(f.Name).Append('=')
                      .Append(Text(v, "currentValueLabel")).Append(Text(v, "signLabel")).Append(Text(v, "maxValueLabel"));
                }
                else if (f.FieldType.Name == "ConstructionPanel")
                {
                    var cycles = AccessTools.Field(f.FieldType, "_productionCycles")?.GetValue(v) as System.Collections.IEnumerable;
                    if (cycles == null) continue;
                    foreach (var c in cycles)
                    {
                        if (c == null) continue;
                        var rid = AccessTools.Property(c.GetType(), "ResourceId")?.GetValue(c, null);
                        sb.Append(" | ").Append(rid).Append('=').Append(Text(c, "resourceLabel"));
                    }
                }
            }
            return sb.ToString();
        }

        private static string Text(object owner, string field)
        {
            try
            {
                var label = AccessTools.Field(owner.GetType(), field)?.GetValue(owner);
                return label == null ? "" : AccessTools.Property(label.GetType(), "text")?.GetValue(label, null) as string ?? "";
            }
            catch { return ""; }
        }
    }
}
