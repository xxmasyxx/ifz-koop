using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Controllers;
using Gameplay.Buildings;
using Gameplay.Units.Characters;
using Gameplay.Units.Player.Workers;
using Gameplay.Units.Player.Workers.WorkSystem;
using Gameplay.Units.Player.Workers.WorkSystem.Signals;
using Gameplay.Units.Workers;
using Gameplay.Units.Workers.WorkSystem;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using ScriptableObjectScripts.UI;
using UnityEngine;
using Zenject;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Arbeitszuteilung (Analyse W4 Stufe 2, Teil „Zuteilung“): wer an welcher Arbeit sitzt, wie viele höchstens,
    /// Wichtigkeit je Arbeit, Wichtigkeit je Berufsart (Übersicht unten rechts) und Alarm.
    ///
    /// Host: 1×/s eine Unterschrift über alles; bei Änderung (und alle 10 s) der ganze Stand.
    /// Gast: die eigene Verteilung des Spiels ist gesperrt (UnitPatches: WorkController.AssignWorkers/FindWork/…,
    /// WorksPriorityManager.FindWork/RealignWorkers). Er übernimmt Wichtigkeit, Höchstzahl und die zugeteilten Arbeiter.
    /// Damit stimmen auch „arbeitslos“ und die Zähler. Klicks des Gastes in den beiden Fenstern
    /// (Auftrag: WorkersCountChanger; Übersicht: WorkersLabel) und der Alarm gehen als Wunsch an den Host.
    /// </summary>
    internal sealed class WorkSync
    {
        private const float SendInterval = 1f;
        private const float ForceInterval = 10f;

        /// <summary>Was der Gast angeklickt hat.</summary>
        public enum Op : byte
        {
            // Fenster eines Auftrags
            Add = 1, Remove = 2, AddMax = 3, RemoveAll = 4, IncMax = 5, DecMax = 6, Priority = 7,
            // Übersicht je Berufsart
            LZero = 11, LMax = 12, LAdd = 13, LRemove = 14, LAddMax = 15, LRemoveAll = 16, LPriority = 17,
            Alarm = 20,
        }

        private readonly Session _session;
        private string _sentSig;
        private float _nextSend;
        private float _nextForce;

        public int Applied { get; private set; }
        public int Assigned { get; private set; }
        public int Unassigned { get; private set; }
        public int AssignFailed { get; private set; }
        public int MissingWorks { get; private set; }
        public int RequestsSent { get; private set; }
        public int RequestsDone { get; private set; }

        public WorkSync(Session session)
        {
            _session = session;
        }

        public void ResendAll()
        {
            _sentSig = null;
            _nextSend = 0f;
        }

        public void Reset()
        {
            ResendAll();
            _last = null;
            _staleSince.Clear();
        }

        // Letzter Stand vom Host (Gast): wird jede Sekunde erneut angewandt, falls das Spiel beim Gast etwas verstellt hat
        private (bool alarm, List<KeyValuePair<int, int>> emp, Dictionary<string, (int prio, int max, bool susp, List<string> ids)> works)? _last;
        private float _nextReapply;
        public int Reapplied { get; private set; }

        // ================================================================ Zugriff

        private static readonly AccessTools.FieldRef<WorkController, WorksPriorityManager> PriorityManagerField =
            AccessTools.FieldRefAccess<WorkController, WorksPriorityManager>("_worksPriorityManager");

        private static readonly AccessTools.FieldRef<WorkController, float> AlarmElapsedField =
            AccessTools.FieldRefAccess<WorkController, float>("_alarmElapsedTime");

        private static readonly AccessTools.FieldRef<WorkersLabel, Workplace> LabelWorkplaceField =
            AccessTools.FieldRefAccess<WorkersLabel, Workplace>("_workplace");

        private static WorkController Wc => GameAccess.Game<WorkController>();

        private static SignalBus Bus => GameAccess.Game<SignalBus>();

        private static string F(float f) => Mathf.RoundToInt(f * 2f).ToString(CultureInfo.InvariantCulture);

        /// <summary>Gleicher Schlüssel bei Host und Gast: Art + Gebäude/Fahrzeug/Gebiet + laufende Nummer bei Gleichen.</summary>
        private static string BaseKey(WorkBase w)
        {
            string type = w.GetType().Name;
            Gameplay.Rebuilding.Structure s = null;
            try { s = w.GetRelatedStructure(); } catch { }
            if (s != null)
            {
                if (s is Building b)
                    return type + ":b" + b.ID;
                var p = s.transform.position;
                return type + ":s" + F(p.x) + "," + F(p.z);
            }
            if (w is VehicleRepairWork vr)
                return type + ":v" + (vr.VehicleBeingRepaired != null ? vr.VehicleBeingRepaired.Id : "?");
            if (w is AreaWork aw)
                return type + ":a" + (aw.AreaWorkType != null ? aw.AreaWorkType.Name : "?")
                    + (AreaWorkSync.StableId(aw) is string sid ? "@" + sid : "");
            return type;
        }

        /// <summary>Alle Arbeiten mit Schlüssel, in der Reihenfolge des Spiels.</summary>
        private static List<KeyValuePair<string, WorkBase>> Keyed()
        {
            var list = new List<KeyValuePair<string, WorkBase>>();
            var works = Wc?.Works;
            if (works == null)
                return list;
            var count = new Dictionary<string, int>();
            foreach (var w in works)
            {
                if (w == null)
                    continue;
                string k = BaseKey(w);
                count.TryGetValue(k, out int n);
                count[k] = n + 1;
                list.Add(new KeyValuePair<string, WorkBase>(n == 0 ? k : k + "#" + n, w));
            }
            return list;
        }

        /// <summary>Für den Testtreiber: Arbeit Nummer i in der Reihenfolge der Statuszeilen.</summary>
        public static KeyValuePair<string, WorkBase> ByIndex(int i)
        {
            var l = Keyed().OrderBy(k => k.Key, StringComparer.Ordinal).ToList();
            return i >= 0 && i < l.Count ? l[i] : default;
        }

        private static WorkBase Find(string key) => Keyed().FirstOrDefault(kv => kv.Key == key).Value;

        private static string KeyOf(WorkBase w) => Keyed().FirstOrDefault(kv => ReferenceEquals(kv.Value, w)).Key;

        private static IEnumerable<string> WorkerIds(WorkBase w)
        {
            foreach (var c in w.Workers)
                if (c != null && !string.IsNullOrEmpty(c.Id))
                    yield return c.Id;
        }

        // ================================================================ Host

        private sealed class Snapshot
        {
            public int Mode;
            public bool Alarm;
            public List<KeyValuePair<int, int>> Emp = new List<KeyValuePair<int, int>>();
            public List<(string key, int prio, int max, bool susp, List<string> ids)> Works = new List<(string, int, int, bool, List<string>)>();

            public string Sig()
            {
                var sb = new StringBuilder();
                sb.Append(Mode).Append(Alarm ? 'A' : '-').Append('|');
                foreach (var e in Emp) sb.Append(e.Key).Append(':').Append(e.Value).Append(',');
                foreach (var w in Works)
                {
                    sb.Append('|').Append(w.key).Append(' ').Append(w.prio).Append(' ').Append(w.max).Append(w.susp ? 'S' : '-');
                    foreach (var id in w.ids) sb.Append(' ').Append(id);
                }
                return sb.ToString();
            }
        }

        private static Snapshot Take()
        {
            var wc = Wc;
            var s = new Snapshot();
            if (wc == null)
                return s;
            try { s.Mode = GameAccess.Game<GameCustomization.GameCustomize>()?.WorkManagement ?? 0; } catch { }
            s.Alarm = wc.IsAlarm;
            if (wc.EmploymentPriorities != null)
                foreach (var kv in wc.EmploymentPriorities.OrderBy(k => (int)k.Key))
                    s.Emp.Add(new KeyValuePair<int, int>((int)kv.Key, kv.Value));
            foreach (var kv in Keyed())
                s.Works.Add((kv.Key, kv.Value.Priority, kv.Value.MaxWorkers, kv.Value.IsSuspended, WorkerIds(kv.Value).ToList()));
            return s;
        }

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend || _session.ActiveGuestCount == 0 || !GameAccess.IsGameStarted)
                return;
            _nextSend = now + SendInterval;
            bool force = now >= _nextForce;
            if (force) _nextForce = now + ForceInterval;

            Snapshot s;
            try { s = Take(); }
            catch (Exception e) { Plugin.Log.LogDebug($"Arbeit-Stand: {e.Message}"); return; }
            string sig = s.Sig();
            if (!force && sig == _sentSig)
                return;
            _sentSig = sig;
            _session.BroadcastToGuests(Protocol.Build(Msg.WorkState, w =>
            {
                w.Write(s.Mode);
                w.Write(s.Alarm);
                w.Write(s.Emp.Count);
                foreach (var e in s.Emp)
                {
                    w.Write(e.Key);
                    w.Write(e.Value);
                }
                w.Write(s.Works.Count);
                foreach (var x in s.Works)
                {
                    w.Write(x.key);
                    w.Write(x.prio);
                    w.Write(x.max);
                    w.Write(x.susp);
                    w.Write(x.ids.Count);
                    foreach (var id in x.ids) w.Write(id);
                }
            }));
        }

        public void HostOnRequest(Peer from, BinaryReader r)
        {
            var op = (Op)r.ReadByte();
            string key = r.ReadString();
            int emp = r.ReadInt32();
            int arg = r.ReadInt32();
            bool flag = r.ReadBoolean();
            var wc = Wc;
            var bus = Bus;
            if (wc == null || bus == null)
                return;
            try
            {
                if (op == Op.Alarm)
                {
                    if (flag && !wc.IsAlarm) wc.StartAlarm();
                    else if (!flag && wc.IsAlarm) wc.StopAlarm();
                }
                else if (op < Op.LZero)
                {
                    var work = Find(key);
                    if (work == null)
                    {
                        Plugin.Log.LogWarning($"IFZCOOP work-Wunsch {op}: Arbeit {key} gibt es beim Host nicht");
                        return;
                    }
                    HostWorkOp(work, op, arg, flag, bus);
                }
                else
                {
                    HostLabelOp((Employment)emp, op, arg, flag, wc, bus);
                }
                RequestsDone++;
                _nextSend = 0f; // gleich zurückmelden
                Plugin.Log.LogInfo($"IFZCOOP work {op} {key}{(op >= Op.LZero ? ((Employment)emp).ToString() : "")} {arg} {flag} (von {from.Name})");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"IFZCOOP work-Wunsch {op}: {e.Message}"); }
        }

        private static void HostWorkOp(WorkBase work, Op op, int arg, bool flag, SignalBus bus)
        {
            switch (op)
            {
                case Op.Add:
                    bus.Fire(new AssignWorkersSignal(work, Math.Max(1, arg)));
                    break;
                case Op.Remove:
                    for (int i = 0; i < Math.Max(1, arg); i++)
                    {
                        work.RemoveWorker();
                        if (work.Workers.Count == 0) break;
                    }
                    break;
                case Op.AddMax:
                    bus.Fire(new AssignWorkersSignal(work, work.MaxWorkers - work.Workers.Count));
                    break;
                case Op.RemoveAll:
                    work.RemoveAllWorkers();
                    break;
                case Op.IncMax:
                    if (work.MaxWorkers < work.InitialMaxWorkers)
                        work.MaxWorkers = Math.Min(work.InitialMaxWorkers, work.MaxWorkers + Math.Max(1, arg));
                    break;
                case Op.DecMax:
                    if (work.MaxWorkers > 0)
                        work.MaxWorkers = Math.Max(0, work.MaxWorkers - Math.Max(1, arg));
                    break;
                case Op.Priority:
                    bus.Fire(new WorkPriorityChangedSignal(work, arg, flag));
                    break;
            }
        }

        /// <summary>Die Übersicht unten rechts: gleiche Rechnung wie WorkersLabel, auf dessen Arbeitsplatz beim Host.</summary>
        private static void HostLabelOp(Employment emp, Op op, int priority, bool leftButton, WorkController wc, SignalBus bus)
        {
            Workplace wp = null;
            try
            {
                var panel = System.Linq.Enumerable.FirstOrDefault(Resources.FindObjectsOfTypeAll<WorkersPanelController>(), p => p._labels != null);
                if (panel?._labels != null && panel._labels.TryGetValue(emp, out var label) && label != null)
                    wp = LabelWorkplaceField(label);
            }
            catch { }
            if (wp == null)
            {
                wp = new Workplace(bus);
                foreach (var w in wc.Works)
                    if (w != null && w.EmploymentType() == emp)
                        wp.AddWork(w);
            }
            switch (op)
            {
                case Op.LZero:
                    wp.ZeroMaxWorkers();
                    break;
                case Op.LMax:
                    wp.MaxMaxWorkers();
                    break;
                case Op.LAdd:
                    if (wp.WorksCount != 0)
                    {
                        var w = wp.GetMostUrgentWork();
                        if (w != null) bus.Fire(new AssignWorkersSignal(w, 1));
                    }
                    break;
                case Op.LRemove:
                    wp.GetLeastUrgentWork()?.RemoveWorker();
                    break;
                case Op.LAddMax:
                {
                    int expected = WorkController.AvailableWorkersCount;
                    int guard = 0;
                    while (WorkController.AvailableWorkersCount > 0 && wp.HasEmptyWorkPositions() && expected == WorkController.AvailableWorkersCount && guard++ < 500)
                    {
                        var w = wp.GetMostUrgentWork();
                        if (w == null) break;
                        bus.Fire(new AssignWorkersSignal(w, 1));
                        expected--;
                    }
                    break;
                }
                case Op.LRemoveAll:
                    wp.RemoveAllWorkers();
                    break;
                case Op.LPriority:
                    if (wp.HasSpecialPriority())
                        priority += 5;
                    if (leftButton)
                        wp.SetPriority(priority);
                    wc.SetPriority(emp, priority);
                    break;
            }
        }

        // ================================================================ Gast

        private bool GuestCanAsk => _session.Role == Role.Guest && _session.GuestControlsLocked && !UnitSync.NetApplying;

        private void Send(Op op, string key, int emp, int arg, bool flag)
        {
            _session.SendToHost(Protocol.Build(Msg.WorkRequest, w =>
            {
                w.Write((byte)op);
                w.Write(key ?? "");
                w.Write(emp);
                w.Write(arg);
                w.Write(flag);
            }));
            RequestsSent++;
        }

        private static bool Shift(object ui)
        {
            try
            {
                var keys = Traverse.Create(ui).Field("_gameKeysController").GetValue();
                return keys != null && Traverse.Create(keys).Property("IsShifPressed").GetValue<bool>();
            }
            catch { return false; }
        }

        /// <summary>Prefix der Knöpfe im Fenster eines Auftrags. false = beim Gast nicht selbst ausführen.</summary>
        public bool GuestWorkUi(UI.InfoPanels.WorkersCountChanger ui, Op op, int priority = 0, bool leftButton = true)
        {
            if (!GuestCanAsk || ui == null)
                return true;
            WorkBase work = null;
            try { work = Traverse.Create(ui).Field("_work").GetValue<WorkBase>(); } catch { }
            if (work == null)
                return false;
            string key = KeyOf(work);
            if (key == null)
                return false;
            bool shift = Shift(ui);
            switch (op)
            {
                case Op.Priority:
                    if (!leftButton)
                        return false;
                    // Wie das Spiel: im Alarm liegt die Wichtigkeit 5 höher
                    bool alarm;
                    try { alarm = Traverse.Create(ui).Field("_isAlarm").GetValue<bool>(); } catch { alarm = work.Priority > 5; }
                    if (alarm && priority > 0) priority += 5;
                    Send(op, key, 0, priority, shift);
                    break;
                case Op.Add:
                case Op.Remove:
                case Op.IncMax:
                case Op.DecMax:
                    Send(op, key, 0, shift ? 10 : 1, false);
                    break;
                default:
                    Send(op, key, 0, 0, false);
                    break;
            }
            return false;
        }

        /// <summary>Prefix der Knöpfe der Übersicht unten rechts.</summary>
        public bool GuestLabelUi(WorkersLabel label, Op op, int priority = 0, bool leftButton = true)
        {
            if (!GuestCanAsk || label == null)
                return true;
            Send(op, "", (int)label.EmploymentType, priority, leftButton);
            return false;
        }

        /// <summary>Prefix WorkController.StartAlarm/StopAlarm beim Gast.</summary>
        public bool GuestAlarm(bool on)
        {
            if (!GuestCanAsk)
                return true;
            Send(Op.Alarm, "", 0, 0, on);
            return false;
        }

        public void GuestOnState(BinaryReader r)
        {
            int mode = r.ReadInt32();
            bool alarm = r.ReadBoolean();
            int ne = r.ReadInt32();
            if (ne < 0 || ne > 100)
                throw new InvalidDataException("zu viele Berufsarten");
            var emp = new List<KeyValuePair<int, int>>();
            for (int i = 0; i < ne; i++) emp.Add(new KeyValuePair<int, int>(r.ReadInt32(), r.ReadInt32()));
            int nw = r.ReadInt32();
            if (nw < 0 || nw > 20000)
                throw new InvalidDataException("zu viele Arbeiten");
            var host = new Dictionary<string, (int prio, int max, bool susp, List<string> ids)>();
            for (int i = 0; i < nw; i++)
            {
                string key = r.ReadString();
                int prio = r.ReadInt32();
                int max = r.ReadInt32();
                bool susp = r.ReadBoolean();
                int n = r.ReadInt32();
                if (n < 0 || n > 5000)
                    throw new InvalidDataException("zu viele Arbeiter");
                var ids = new List<string>(n);
                for (int j = 0; j < n; j++) ids.Add(r.ReadString());
                host[key] = (prio, max, susp, ids);
            }

            _last = (alarm, emp, host);
            var wc = Wc;
            if (wc == null)
                return;
            UnitSync.NetApplying = true;
            try
            {
                Apply(wc, alarm, emp, host);
                Applied++;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Arbeit-Stand: {e.Message}"); }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        public void GuestFrame()
        {
            if (_last == null || _session.Role != Role.Guest || !_session.GuestControlsLocked)
                return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextReapply)
                return;
            _nextReapply = now + 1f;
            var wc = Wc;
            if (wc == null)
                return;
            var l = _last.Value;
            int before = Assigned + Unassigned;
            UnitSync.NetApplying = true;
            try { Apply(wc, l.alarm, l.emp, l.works); }
            catch (Exception e) { Plugin.Log.LogDebug($"Arbeit erneut: {e.Message}"); }
            finally { UnitSync.NetApplying = false; }
            if (Assigned + Unassigned != before) Reapplied++;
        }

        private void Apply(WorkController wc, bool alarm, List<KeyValuePair<int, int>> emp,
            Dictionary<string, (int prio, int max, bool susp, List<string> ids)> host)
        {
            if (wc.IsAlarm != alarm)
                wc.LoadIsAlarm(alarm);
            // Die Alarm-Uhr zählt nur der Host. Beim Gast lief sie sonst mit einem alten Wert, war sofort „abgelaufen“ und
            // WorkController.CustomUpdate rief StopAlarm – das ging als Wunsch an den Host und schaltete den Alarm dort aus.
            if (alarm)
                AlarmElapsedField(wc) = 0f;
            foreach (var e in emp)
            {
                var em = (Employment)e.Key;
                if (wc.EmploymentPriorities != null && (!wc.EmploymentPriorities.TryGetValue(em, out int cur) || cur != e.Value))
                    wc.SetPriority(em, e.Value);
            }

            var pm = PriorityManagerField(wc);
            var keyed = Keyed();
            var mine = new Dictionary<string, WorkBase>();
            foreach (var kv in keyed) mine[kv.Key] = kv.Value;

            // 1. Einstellungen; Arbeiter heraus, die beim Host nicht dort sind (auch aus Arbeiten, die der Host nicht hat)
            int missing = 0;
            foreach (var kv in keyed)
            {
                var w = kv.Value;
                if (!host.TryGetValue(kv.Key, out var h))
                {
                    for (int i = w.Workers.Count - 1; i >= 0; i--)
                        Unassign(w.Workers[i]);
                    TryRemoveStale(w);
                    continue;
                }
                _staleSince.Remove(w);
                if (w.Priority != h.prio)
                {
                    if (pm != null) pm.ChangeWorkPriority(w, h.prio);
                    else w.Priority = h.prio;
                }
                if (w.IsSuspended != h.susp)
                {
                    if (h.susp) wc.SuspendWork(w);
                    else w.IsSuspended = false;
                }
                if (w.MaxWorkers != h.max)
                    w.MaxWorkers = h.max;
                var want = new HashSet<string>(h.ids);
                for (int i = w.Workers.Count - 1; i >= 0; i--)
                {
                    var c = w.Workers[i];
                    if (c != null && !want.Contains(c.Id))
                        Unassign(c);
                }
            }
            foreach (var k in host.Keys)
                if (!mine.ContainsKey(k)) missing++;
            MissingWorks = missing;

            // 2. Fehlende Arbeiter hinein
            Dictionary<string, Character> workers = null;
            foreach (var kv in host)
            {
                if (kv.Value.ids.Count == 0 || !mine.TryGetValue(kv.Key, out var w))
                    continue;
                foreach (var id in kv.Value.ids)
                {
                    if (w.Workers.Any(c => c != null && c.Id == id))
                        continue;
                    if (workers == null)
                    {
                        workers = new Dictionary<string, Character>();
                        var list = GameAccess.Game<WorkersController>()?.Workers;
                        if (list != null)
                            foreach (var c in list)
                                if (c != null && !string.IsNullOrEmpty(c.Id)) workers[c.Id] = c;
                    }
                    if (!workers.TryGetValue(id, out var ch) || ch.Health == null || ch.Health.IsDead())
                    {
                        AssignFailed++;
                        continue;
                    }
                    if (ch.WorkModule != null && (ch.WorkModule.HasWork || ch.WorkModule.HasParentWork))
                        Unassign(ch);
                    bool ok = false;
                    try { ok = w.AddWorker(ch); } catch (Exception e) { Plugin.Log.LogDebug($"Arbeiter {id} → {kv.Key}: {e.Message}"); }
                    if (ok) Assigned++;
                    else AssignFailed++;
                }
            }
        }

        // Bauaufträge, die es nur beim Gast gibt (z. B. nach einem Abbruch beim Host) und die nicht mehr der laufende
        // Auftrag ihres Gebäudes sind, räumt der Gast nach 10 s weg. Dauerhafte Arbeiten (Produktion, Verteidigung …) nie.
        private readonly Dictionary<WorkBase, float> _staleSince = new Dictionary<WorkBase, float>();
        public int StaleRemoved { get; private set; }

        private static readonly HashSet<string> TransientWorks = new HashSet<string>
        {
            "BuildStructureWork", "DeconstructWork", "DeadaptationWork", "RepairStructureWork", "CreateBuildingWork",
            "SplitWork", "CustomizationWork", "PlantExplosivesWork", "ExtraAdaptationWork",
        };

        private void TryRemoveStale(WorkBase w)
        {
            if (!TransientWorks.Contains(w.GetType().Name))
                return;
            Gameplay.Rebuilding.Structure s = null;
            try { s = w.GetRelatedStructure(); } catch { }
            if (s != null && ReferenceEquals(s.CurrentWork, w))
                return;
            float now = Time.realtimeSinceStartup;
            if (!_staleSince.TryGetValue(w, out float since))
            {
                _staleSince[w] = now;
                return;
            }
            if (now - since < 10f)
                return;
            _staleSince.Remove(w);
            try
            {
                w.DeleteWork();
                StaleRemoved++;
                Plugin.Log.LogInfo($"IFZCOOP work alter Auftrag beim Gast entfernt: {BaseKey(w)}");
            }
            catch (Exception e) { Plugin.Log.LogDebug($"Alter Auftrag {BaseKey(w)}: {e.Message}"); }
        }

        private void Unassign(Character c)
        {
            if (c?.WorkModule == null)
                return;
            try
            {
                c.WorkModule.UnassignWork(areaWork: true);
                Unassigned++;
            }
            catch (Exception e) { Plugin.Log.LogDebug($"Arbeiter {c.Id} heraus: {e.Message}"); }
        }

        // ================================================================ Status

        /// <summary>Für Statusdatei und Prüfsumme (nur die Einstellungen, ohne Arbeiten, die es beim Gast nicht gibt).</summary>
        public static string GlobalLine()
        {
            var wc = Wc;
            if (wc == null)
                return "work none";
            string prios = wc.EmploymentPriorities == null ? "" :
                string.Join(",", wc.EmploymentPriorities.OrderBy(k => (int)k.Key).Select(k => (int)k.Key + ":" + k.Value));
            return $"work alarm={(wc.IsAlarm ? 1 : 0)} prios={prios}";
        }

        public static IEnumerable<string> StatusLines()
        {
            yield return GlobalLine();
            var keyed = Keyed();
            yield return $"workcount works={keyed.Count} assigned={keyed.Sum(k => k.Value.Workers.Count)} available={WorkController.AvailableWorkersCount}";
            foreach (var kv in keyed.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var w = kv.Value;
                yield return $"workitem {kv.Key} emp={w.EmploymentType()} p={w.Priority} m={w.MaxWorkers}/{w.InitialMaxWorkers} n={w.Workers.Count} susp={(w.IsSuspended ? 1 : 0)} ids={string.Join(",", WorkerIds(w).OrderBy(x => x, StringComparer.Ordinal))}";
            }
        }

        public string StatusLine() =>
            $"workApplied={Applied} reapplied={Reapplied} staleRemoved={StaleRemoved} assigned={Assigned} unassigned={Unassigned} assignFailed={AssignFailed} missingWorks={MissingWorks} requestsSent={RequestsSent} requestsDone={RequestsDone}";
    }
}
