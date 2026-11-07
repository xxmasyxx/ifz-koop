using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Controllers;
using Data.Building;
using GameInput;
using Gameplay.Buildings;
using Gameplay.Rebuilding;
using Gameplay.Rebuilding.CustomBuildings;
using Gameplay.Rebuilding.Split;
using Gameplay.Rebuilding.StateMachine;
using Gameplay.Rebuilding.StateMachine.States;
using Gameplay.Units.Workers.WorkSystem.Works;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using MapOperator.Runtime.Data;
using MapOperator.Runtime.MeshConstructors.Buildings.Roofs;
using Newtonsoft.Json;
using UnityEngine;
using Utilities;
using GameJson = Utilities.JsonSerializer;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Gebäude (Analyse W3, W16; Paket H, Teil 2). Umbau, Erweitern, Rückbau, Abriss, Abriss-Markierung, Reparatur,
    /// dazu Abbrechen/Anhalten/Fortsetzen, Sprengstoff legen, Beanspruchen, Abbau (Rückbau zu Schutt), eigene Gebäude,
    /// Teilen, Lebenspunkte.
    ///
    /// Host: 1×/s Liste der Häuser mit Zustand ≠ verlassen (BuildingsController.BuildingsWithInteraction) und der zum Abriss
    /// markierten: (ID, Zustand, Umbau-Art, Zielhöhe, markiert). Bei Änderung die geänderten Einträge, alle 10 s alle;
    /// verschwundene Häuser (GameObject zerstört, z. B. nach dem Teilen) als „weg“ (Zustand -1).
    /// Alle 2 s die Lebenspunkte (gedrosselt: nur Häuser mit Schaden, beansprucht, angehaltenem Rückbau/Abriss oder
    /// Sprengladungen; nur bei Änderung, alle 30 s alle) – Msg.BuildingHealth.
    /// Neue Häuser (Msg.BuildingEvent): eigenes Gebäude (CreationBuildingController) und Teilung (BuildingSplitController)
    /// laufen beim Gast mit denselben Eingaben über dieselben Spiel-Methoden. Die neuen Häuser bekommen dieselben IDs wie
    /// beim Host, weil der Gast vorher den Gebäude-Zähler der Kartendaten (MapData.IdAssigner) auf den Stand des Hosts setzt.
    /// Gast: stellt den Zustand nach (StartAdapt, StartDeconstruction, ChangeTo …) und setzt HP, Beanspruchung, Pause.
    /// Klicks des Gastes gehen als Wunsch an den Host (Msg.BuildingRequest).
    /// Gebaute Anlagen außerhalb von Häusern (Türme, Mauern, Felder) macht StructureSync.
    /// </summary>
    internal sealed class BuildingSync
    {
        private const float SendInterval = 1f;
        private const float FullInterval = 10f;
        private const float HealthInterval = 2f;
        private const float HealthFullInterval = 30f;
        private const float PendingTime = 60f;
        private const int Gone = -1;

        internal enum Action : byte
        {
            Adapt = 0, ExtraAdapt = 1, Deadapt = 2, Demolish = 3, Ready = 4, Repair = 5,
            Cancel = 6, Explosives = 7, Claim = 8, Deconstruct = 9, Create = 10, Split = 11,
        }

        /// <summary>Welcher Abbrechen-/Fortsetzen-Knopf (Wert bei Action.Cancel).</summary>
        internal enum CancelKind
        {
            None = 0, Build = 1, Repair = 2, Ruin = 3, DemolishDelete = 4, DemolishResume = 5, DeadaptDelete = 6,
            DeadaptResume = 7, Customize = 8, Split = 9, Create = 10, Explosives = 11,
        }

        private enum Ev : byte { Create = 1, SplitStart = 2, SplitDone = 3 }

        [Flags]
        private enum HF : byte { None = 0, Claimed = 1, DeconstructPaused = 2, DeadaptPaused = 4 }

        private struct Entry
        {
            public long Id;
            public int State;
            public string Draft;
            public float Target;
            public float Cur;
            public bool Ready;

            // Cur nur für fertige Häuser in der Unterschrift (beim Bau ändert es sich laufend, das macht BuildResSync)
            public string Sig => State + "|" + Draft + "|" + Mathf.RoundToInt(Target * 100f) + "|" + Ready
                + (State == (int)StructureStateType.Completed ? "|" + Mathf.RoundToInt(Cur * 100f) : "");
        }

        private struct Hp
        {
            public float Value;
            public float Max;
            public HF Flags;
            public int Explosives;
        }

        /// <summary>Eingaben eines eigenen Gebäudes (wie CustomBuildingData nach dem Bestätigen).</summary>
        private sealed class CustomData
        {
            public List<Vector3> Points = new List<Vector3>();
            public float Height;
            public int RoofAngle;
            public int RoofType;
            public int Facade;
            public Color Wall;
            public Color Roof;

            public static CustomData From(CustomBuildingData d) => new CustomData
            {
                Points = new List<Vector3>(d.Points),
                Height = d.Height,
                RoofAngle = d.RoofAngle,
                RoofType = (int)d.RoofType,
                Facade = (int)d.FacadeType,
                Wall = d.WallsColor,
                Roof = d.RoofColor,
            };

            public CustomBuildingData Build(CustomBuildingConfig config)
            {
                var d = new CustomBuildingData(config);
                d.SetPoints(Points);
                d.SetHeight(Height);
                d.SetRoofAngle(RoofAngle);
                d.SetRoofType((RoofType)RoofType);
                d.SetFacadeType((FacadeType)Facade);
                d.SetRoofColor(Roof);
                d.SetWallsColor(Wall);
                d.SetIsClosed(true);
                return d;
            }

            public void Write(BinaryWriter w)
            {
                w.Write(Points.Count);
                foreach (var p in Points) { w.Write(p.x); w.Write(p.y); w.Write(p.z); }
                w.Write(Height);
                w.Write(RoofAngle);
                w.Write(RoofType);
                w.Write(Facade);
                WriteColor(w, Wall);
                WriteColor(w, Roof);
            }

            public static CustomData Read(BinaryReader r)
            {
                var c = new CustomData();
                int n = r.ReadInt32();
                if (n < 0 || n > 1000) throw new InvalidDataException("zu viele Eckpunkte");
                for (int i = 0; i < n; i++) c.Points.Add(new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
                c.Height = r.ReadSingle();
                c.RoofAngle = r.ReadInt32();
                c.RoofType = r.ReadInt32();
                c.Facade = r.ReadInt32();
                c.Wall = ReadColor(r);
                c.Roof = ReadColor(r);
                return c;
            }

            private static void WriteColor(BinaryWriter w, Color c) { w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); }
            private static Color ReadColor(BinaryReader r) => new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }

        // Spiel-Interna (privat), per Reflection
        private static readonly FieldInfo SplitRequestF = AccessTools.Field(typeof(SplitWork), "_splitRequest");
        private static readonly FieldInfo SplitHandlerF = AccessTools.Field(typeof(BuildingSplitController), "_buildingSplitHandler");
        private static readonly MethodInfo ExecuteRequestM = AccessTools.Method(typeof(BuildingSplitController), "ExecuteRequest");
        private static readonly MethodInfo SplitEntrancesM = AccessTools.Method(typeof(BuildingSplitController), "GetSplitEntrances");
        private static readonly ConstructorInfo SplitInfoCtor = AccessTools.Constructor(typeof(SplitRequest.SerializedInfo), new[] { typeof(SplitRequest) });
        private static readonly FieldInfo DraftF = AccessTools.Field(typeof(CreationBuildingController), "_draft");
        private static readonly FieldInfo FactoryF = AccessTools.Field(typeof(CreationBuildingController), "_customBuildingFactory");
        private static readonly FieldInfo ConfigF = AccessTools.Field(typeof(CreationBuildingController), "_customBuildingConfig");
        private static readonly MethodInfo StartDeconstructionM = AccessTools.Method(typeof(Building), "StartDeconstruction");

        /// <summary>
        /// Gast: alte Abbau-Arbeit eines Hauses wegräumen. Beim Hin und Her Abbau -> verlassen -> Abbau (Fehlerbericht
        /// 2026-11-06) blieb die erste Arbeit in der Arbeitsliste: zwei DeconstructWork am selben Haus, die Arbeiter an der
        /// alten, das Fenster an der neuen („Keine Arbeiter 0/52“, Wichtigkeit nicht änderbar).
        /// </summary>
        private void DropStaleDeconstruct(Building b)
        {
            var old = b.DeconstructWork;
            if (old == null)
                return;
            try { Quietly(b, () => old.ForceDelete()); }
            catch (Exception e) { Plugin.Log.LogDebug($"Abbau {b.ID} wegräumen: {e.Message}"); }
        }
        private static readonly PropertyInfo IdAssignerP = AccessTools.Property(typeof(MapData), "IdAssigner");
        private static readonly FieldInfo BuildingsIdF = AccessTools.Field(typeof(IdAssigner), "_buildingsId");
        private static readonly FieldInfo DisplayedStructureF = AccessTools.Field(typeof(UI.InfoPanels.NewStructurePanelInfo), "_displayedStructure");

        private readonly Session _session;
        private readonly Dictionary<long, string> _sent = new Dictionary<long, string>();
        private readonly Dictionary<long, Hp> _hpSent = new Dictionary<long, Hp>();
        private readonly HashSet<long> _splitAnnounced = new HashSet<long>();
        private readonly Dictionary<long, string> _splitInfo = new Dictionary<long, string>();
        private readonly HashSet<long> _splitBusy = new HashSet<long>();
        private readonly Dictionary<long, KeyValuePair<Entry, float>> _pending = new Dictionary<long, KeyValuePair<Entry, float>>();
        private bool _full = true;
        private bool _hpFull = true;
        private float _nextSend;
        private float _nextFull;
        private float _nextHealth;
        private float _nextHealthFull;
        private float _nextPending;
        private float _localUntil;
        private long _localIdFrom = long.MaxValue;

        public int Applied { get; private set; }
        public int RequestsSent { get; private set; }
        public int RequestsDone { get; private set; }
        public int HpApplied { get; private set; }
        public int HpSent { get; private set; }
        public int Created { get; private set; }
        public int IdMismatch { get; private set; }
        public int SplitsStarted { get; private set; }
        public int SplitsDone { get; private set; }
        public int EventsSent { get; private set; }

        public BuildingSync(Session session)
        {
            _session = session;
        }

        public void ResendAll()
        {
            _sent.Clear();
            _hpSent.Clear();
            _splitAnnounced.Clear();
            _full = true;
            _hpFull = true;
        }

        public void Reset()
        {
            ResendAll();
            _splitInfo.Clear();
            _splitBusy.Clear();
            _pending.Clear();
            _localUntil = 0f;
        }

        private static StructureStateType StateOf(Structure s) => s?.StateMachine?.CurrentState?.StateType ?? StructureStateType.None;

        private static bool IsIdle(StructureStateType t) =>
            t == StructureStateType.None || t == StructureStateType.Empty || t == StructureStateType.Abandoned;

        /// <summary>Fertiges (auch teilweise umgebautes) Haus: Höhe wie beim Host. Sonst sprang sie beim Gast auf den Startwert.</summary>
        private static void ApplyHeight(Building b, Entry e)
        {
            var h = b.PartialBuildingHandler;
            if (h == null)
                return;
            try
            {
                bool changed = false;
                if (Mathf.Abs(h.TargetValue - e.Target) > 0.005f) { h.SetTargetValue(e.Target); changed = true; }
                if (Mathf.Abs(h.CurrentPartialValue - e.Cur) > 0.005f) { h.SetCurrentPartialValue(e.Cur); changed = true; }
                if (changed) h.RefreshPartialValue();
            }
            catch (Exception ex) { Plugin.Log.LogDebug($"Höhe {b.ID}: {ex.Message}"); }
        }

        private static Entry Make(Building b) => new Entry
        {
            Id = b.ID,
            State = (int)StateOf(b),
            Draft = b.Draft != null ? b.Draft.Id ?? "" : "",
            Target = b.PartialBuildingHandler != null ? b.PartialBuildingHandler.TargetValue : 1f,
            Cur = b.PartialBuildingHandler != null ? b.PartialBuildingHandler.CurrentPartialValue : 1f,
            Ready = b.IsReadyToDemolish,
        };

        private bool GuestCanAsk => _session.Role == Role.Guest && _session.GuestControlsLocked && !UnitSync.NetApplying;

        // ================================================================ Gebäude-Zähler der Kartendaten

        private static IdAssigner Assigner()
        {
            var data = GameAccess.Map?.Data;
            return data == null || IdAssignerP == null ? null : IdAssignerP.GetValue(data) as IdAssigner;
        }

        /// <summary>Letzte vergebene Gebäude-ID (die nächste ist +1).</summary>
        private static long GetBuildingCounter()
        {
            var a = Assigner();
            return a == null || BuildingsIdF == null ? long.MinValue : (long)BuildingsIdF.GetValue(a);
        }

        private static bool SetBuildingCounter(long value)
        {
            var a = Assigner();
            if (a == null || BuildingsIdF == null || value == long.MinValue)
                return false;
            lock (a)
                BuildingsIdF.SetValue(a, value);
            return true;
        }

        // ================================================================ Host

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (_session.ActiveGuestCount == 0 || !GameAccess.IsGameStarted)
                return;
            if (now >= _nextHealth)
            {
                _nextHealth = now + HealthInterval;
                try { HostHealth(now); }
                catch (Exception e) { Plugin.Log.LogWarning("Gebäude-HP: " + e.Message); }
            }
            if (now < _nextSend)
                return;
            _nextSend = now + SendInterval;
            if (now >= _nextFull)
            {
                _nextFull = now + FullInterval;
                _full = true;
            }
            var bc = GameAccess.Game<BuildingsController>();
            if (bc == null)
                return;

            var current = new Dictionary<long, Entry>();
            foreach (var b in bc.BuildingsWithInteraction)
                if (b != null && !(b.IsHq && IsIdle(StateOf(b))))
                    current[b.ID] = Make(b);
            foreach (var b in bc.Buildings)
                if (b != null && b.IsReadyToDemolish && !current.ContainsKey(b.ID))
                    current[b.ID] = Make(b);

            // Teilungen zuerst ankündigen (der Gast braucht die Teil-Flächen, bevor er den Zustand „Teilen“ nachstellt)
            foreach (var e in current.Values)
            {
                if ((StructureStateType)e.State != StructureStateType.Split || _splitAnnounced.Contains(e.Id))
                    continue;
                var b = bc.GetBuildingById(e.Id);
                if (b != null && b.CurrentWork is SplitWork sw)
                {
                    try
                    {
                        string json = JsonConvert.SerializeObject(sw.GetRequestSerializedInfo(), GameJson.DefaultSettings);
                        BroadcastEvent(w => { w.Write((byte)Ev.SplitStart); w.Write(e.Id); w.Write(json); });
                        _splitAnnounced.Add(e.Id);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"Teilung {e.Id} ankündigen: {ex.Message}");
                        _splitAnnounced.Add(e.Id);
                    }
                }
            }
            _splitAnnounced.RemoveWhere(id => !current.TryGetValue(id, out var x) || (StructureStateType)x.State != StructureStateType.Split);

            var changed = new List<Entry>();
            foreach (var e in current.Values)
                if (_full || !_sent.TryGetValue(e.Id, out var last) || last != e.Sig)
                    changed.Add(e);
            foreach (var id in _sent.Keys.ToList())
                if (!current.ContainsKey(id))
                {
                    var b = bc.GetBuildingById(id);
                    changed.Add(b != null ? Make(b) : new Entry { Id = id, State = Gone, Draft = "" });
                    _sent.Remove(id);
                }
            _full = false;
            foreach (var e in current.Values) _sent[e.Id] = e.Sig;
            if (changed.Count == 0)
                return;

            _session.BroadcastToGuests(Protocol.Build(Msg.BuildingState, w =>
            {
                w.Write(changed.Count);
                foreach (var e in changed)
                {
                    w.Write(e.Id);
                    w.Write(e.State);
                    w.Write(e.Draft ?? "");
                    w.Write(e.Target);
                    w.Write(e.Cur);
                    w.Write(e.Ready);
                }
            }));
        }

        private static Hp MakeHp(Building b)
        {
            var h = b.Health;
            var f = HF.None;
            if (b.IsClaimedByPlayer) f |= HF.Claimed;
            if (b.DeconstructWork != null && b.DeconstructWork.IsPaused) f |= HF.DeconstructPaused;
            if (b.DeadaptationWork != null && b.DeadaptationWork.IsPaused) f |= HF.DeadaptPaused;
            return new Hp
            {
                Value = h != null ? h.Hp : 0f,
                Max = h != null ? h.MaxHp : 0f,
                Flags = f,
                Explosives = b.PlantExplosivesWork != null ? b.PlantExplosivesWork.PlantedExplosives : 0,
            };
        }

        private static bool Interesting(Building b, Hp v) =>
            (b.Health != null && !b.Health.IsMax) || v.Flags != HF.None || v.Explosives > 0;

        private void HostHealth(float now)
        {
            var bc = GameAccess.Game<BuildingsController>();
            if (bc == null)
                return;
            if (now >= _nextHealthFull)
            {
                _nextHealthFull = now + HealthFullInterval;
                _hpFull = true;
            }
            var changed = new List<KeyValuePair<long, Hp>>();
            var seen = new HashSet<long>();
            foreach (var b in bc.Buildings)
            {
                if (b == null || b.Health == null)
                    continue;
                var v = MakeHp(b);
                bool interesting = Interesting(b, v);
                bool had = _hpSent.TryGetValue(b.ID, out var last);
                if (!interesting && !had)
                    continue;
                seen.Add(b.ID);
                bool diff = !had || _hpFull || last.Flags != v.Flags || last.Explosives != v.Explosives
                    || Mathf.Abs(last.Value - v.Value) >= Mathf.Max(1f, v.Max * 0.005f) || Mathf.Abs(last.Max - v.Max) >= 1f;
                if (!diff)
                    continue;
                changed.Add(new KeyValuePair<long, Hp>(b.ID, v));
                if (interesting) _hpSent[b.ID] = v;
                else _hpSent.Remove(b.ID);
            }
            foreach (var id in _hpSent.Keys.Where(id => !seen.Contains(id)).ToList())
                _hpSent.Remove(id);
            _hpFull = false;
            if (changed.Count == 0)
                return;
            HpSent += changed.Count;
            _session.BroadcastToGuests(Protocol.Build(Msg.BuildingHealth, w =>
            {
                w.Write(changed.Count);
                foreach (var kv in changed)
                {
                    w.Write(kv.Key);
                    w.Write(kv.Value.Value);
                    w.Write(kv.Value.Max);
                    w.Write((byte)kv.Value.Flags);
                    w.Write(kv.Value.Explosives);
                }
            }));
        }

        private void BroadcastEvent(System.Action<BinaryWriter> body)
        {
            EventsSent++;
            _session.BroadcastToGuests(Protocol.Build(Msg.BuildingEvent, body));
        }

        public void HostOnRequest(Peer from, BinaryReader r)
        {
            var action = (Action)r.ReadByte();
            long id = r.ReadInt64();
            string draftId = r.ReadString();
            float value = r.ReadSingle();
            try
            {
                if (action == Action.Create)
                {
                    var cd = CustomData.Read(r);
                    HostCreate(cd);
                    _session.Stats?.Add(from.Id.m_SteamID, StatsSync.Stat.Built);
                }
                else
                {
                    var b = GameAccess.Game<BuildingsController>()?.GetBuildingById(id);
                    if (b == null)
                        return;
                    StatsSync.As(from.Id.m_SteamID, () => Do(b, action, draftId, value));
                }
                RequestsDone++;
                Plugin.Log.LogInfo($"IFZCOOP building {action} {id} {(action == Action.Split ? "" : draftId)} {value.ToString(CultureInfo.InvariantCulture)} (Wunsch von {from.Name})");
                string text = $"{from.Name}: {Describe(action, (CancelKind)Mathf.RoundToInt(value))}";
                _session.ShowNotice(text);
                _session.BroadcastToGuests(Protocol.Build(Msg.Notice, w => w.Write(text)));
                _nextSend = 0f;
                _nextHealth = 0f;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Gebäude {id} {action}: {e}");
            }
        }

        private static string Describe(Action a, CancelKind k = CancelKind.None)
        {
            switch (a)
            {
                case Action.Adapt: return "Umbau gestartet";
                case Action.ExtraAdapt: return "Erweiterung gestartet";
                case Action.Deadapt: return "Rückbau gestartet";
                case Action.Demolish: return "Abriss gestartet";
                case Action.Ready: return "Abriss-Markierung geändert";
                case Action.Repair: return "Reparatur gestartet";
                case Action.Explosives: return "Sprengstoff legen";
                case Action.Claim: return "Haus beansprucht";
                case Action.Deconstruct: return "Abbau gestartet";
                case Action.Create: return "eigenes Gebäude gebaut";
                case Action.Split: return "Haus teilen";
                case Action.Cancel:
                    switch (k)
                    {
                        case CancelKind.DemolishResume:
                        case CancelKind.DeadaptResume: return "Arbeit fortgesetzt";
                        case CancelKind.DemolishDelete:
                        case CancelKind.DeadaptDelete: return "Arbeit angehalten/abgebrochen";
                        default: return "Arbeit abgebrochen";
                    }
                default: return a.ToString();
            }
        }

        /// <summary>Aktion wie die Oberfläche des Spiels ausführen (beim Host als Wunsch des Gastes).</summary>
        private void Do(Building b, Action action, string draftId, float value)
        {
            switch (action)
            {
                case Action.Adapt:
                    StartAdapt(b, draftId, value);
                    break;
                case Action.ExtraAdapt:
                    b.PartialBuildingHandler?.SetTargetValue(value);
                    b.StartExtraAdapt();
                    break;
                case Action.Deadapt:
                    b.StartDeadaptation();
                    break;
                case Action.Demolish:
                    b.StartDemolish();
                    break;
                case Action.Ready:
                    b.SetReadyToDemolish(value > 0.5f);
                    break;
                case Action.Repair:
                    b.StartRepairing();
                    break;
                case Action.Cancel:
                    DoCancel(b, (CancelKind)Mathf.RoundToInt(value));
                    break;
                case Action.Explosives:
                    // wie NewStructurePanelInfo.OnPlantExplosivesCallback
                    if (!b.IsReadyToDemolish && !b.IsPlantingExplosives && b.StateMachine.HasState(StructureStateType.PlantingExplosives))
                        b.StateMachine.ChangeTo(StructureStateType.PlantingExplosives);
                    break;
                case Action.Claim:
                    if (!b.IsClaimedByPlayer)
                        b.MakePartOfZone();
                    break;
                case Action.Deconstruct:
                    // wie das Abbau-Ereignis des Hausfensters -> Building.StartDeconstruction
                    if (IsIdle(StateOf(b)) && b.Draft != null && b.Draft.IsDeconstructable)
                        StartDeconstructionM?.Invoke(b, null);
                    break;
                case Action.Split:
                    HostSplit(b, draftId);
                    break;
            }
        }

        /// <summary>Wie die Abbrechen-/Fortsetzen-Knöpfe der Fenster (StructureInProgressPanelInfo, DemolishWorkPanel …).</summary>
        private static void DoCancel(Building b, CancelKind k)
        {
            var cur = b.CurrentWork;
            switch (k)
            {
                case CancelKind.Build:
                    if (cur != null && (cur == b.BuildWork || cur == b.ExtraAdaptationWork))
                        ((ConstructableWork)cur).CancelWork();
                    break;
                case CancelKind.Repair:
                    b.RepairWork?.DeleteWork();
                    break;
                case CancelKind.Ruin:
                    if (!b.IsRuined)
                        break;
                    b.StateMachine.ChangeTo(b.StateMachine.HasState(StructureStateType.Abandoned) ? StructureStateType.Abandoned : StructureStateType.Empty);
                    break;
                case CancelKind.DemolishDelete:
                    b.DeconstructWork?.DeleteWork();
                    break;
                case CancelKind.DemolishResume:
                    if (b.DeconstructWork != null && b.DeconstructWork.IsPaused)
                        b.DeconstructWork.ResumeWork();
                    break;
                case CancelKind.DeadaptDelete:
                    b.DeadaptationWork?.DeleteWork();
                    break;
                case CancelKind.DeadaptResume:
                    if (b.DeadaptationWork != null && b.DeadaptationWork.IsPaused)
                        b.DeadaptationWork.ResumeWork();
                    break;
                case CancelKind.Customize:
                    if (cur != null && (cur == b.CustomizationWork || cur == b.CustomizationRepairWork))
                        cur.DeleteWork();
                    break;
                case CancelKind.Split:
                    (cur as SplitWork)?.CancelWork();
                    break;
                case CancelKind.Create:
                    (cur as CreateBuildingWork)?.CancelWork();
                    break;
                case CancelKind.Explosives:
                    if (b.IsPlantingExplosives)
                        b.PlantExplosivesWork?.CancelWork();
                    break;
            }
        }

        private static void StartAdapt(Building b, string draftId, float target)
        {
            var draft = BuildingsDatas.Instance.GetPlaceableObjectDraftById(draftId);
            if (draft == null)
                throw new InvalidOperationException("unbekannte Umbau-Art " + draftId);
            if (b.ScavengeNotStarted())
                b.InitializeAndEndScavengeWork(generateResources: false);
            var ac = GameAccess.Game<AdaptController>();
            if (target >= 0.999f && ac != null)
                ac.StartFullAdapt(b, draft);
            else
            {
                b.PartialBuildingHandler?.SetTargetValue(target);
                b.StartAdapt(draft);
            }
        }

        // ---------------------------------------------------------------- Host: eigenes Gebäude

        private static bool CreationParts(out CreationBuildingController ctrl, out CustomBuilding.Factory factory, out CustomBuildingConfig config)
        {
            ctrl = GameAccess.Game<CreationBuildingController>();
            factory = ctrl != null && FactoryF != null ? FactoryF.GetValue(ctrl) as CustomBuilding.Factory : null;
            config = ctrl != null && ConfigF != null ? ConfigF.GetValue(ctrl) as CustomBuildingConfig : null;
            return factory != null && config != null;
        }

        /// <summary>Wunsch des Gastes: eigenes Gebäude wie CreationBuildingController.OnConstructionConfirmed bauen.</summary>
        private void HostCreate(CustomData cd)
        {
            if (!CreationParts(out _, out var factory, out var config))
                throw new InvalidOperationException("Bau eigener Gebäude nicht erreichbar");
            var data = cd.Build(config);
            if (!data.IsValid)
                Plugin.Log.LogWarning($"IFZCOOP eigenes Gebäude: Prüfung beim Host {data.InValidReason}, wird trotzdem gebaut");
            var draft = factory.Create();
            draft.Initialize(data);
            AnnounceCreate(cd);
            var task = draft.CreateBuilding(CancellationToken.None);
            Created++;
        }

        private void AnnounceCreate(CustomData cd)
        {
            long counter = GetBuildingCounter();
            BroadcastEvent(w => { w.Write((byte)Ev.Create); w.Write(counter); cd.Write(w); });
            Plugin.Log.LogInfo($"IFZCOOP building create id={counter + 1} n={cd.Points.Count} h={cd.Height.ToString("0.0", CultureInfo.InvariantCulture)}");
        }

        // ---------------------------------------------------------------- Host: Teilen

        /// <summary>Wunsch des Gastes: Teilung wie BuildingSplitController.SendSplitRequest starten.</summary>
        private static void HostSplit(Building b, string json)
        {
            var have = StateOf(b);
            if (!IsIdle(have))
                throw new InvalidOperationException("Haus nicht frei zum Teilen: " + have);
            var info = JsonConvert.DeserializeObject<SplitRequest.SerializedInfo>(json, GameJson.DefaultSettings);
            var req = info.Deserialize(b);
            b.StateMachine.ChangeTo(StructureStateType.Split);
            if (b.StateMachine.CurrentState is BuildingSplitState st)
                st.CreateWork(req, assignWorkers: true);
            else
                throw new InvalidOperationException("kein Teilen-Zustand");
        }

        // ================================================================ Gast: Klicks

        /// <summary>Prefix der Bau-Aktionen beim Gast: an den Host schicken, selbst nicht ausführen.</summary>
        public bool GuestAction(Structure structure, Action action, string draftId, float value)
        {
            if (_session.Role != Role.Guest && !UnitSync.NetApplying)
                CountStat(action);
            if (!GuestCanAsk)
                return true;
            if (!(structure is Building b))
                return true; // Türme, Mauern … macht StructureSync
            // Nach dem Nachbauen einer Teilung setzt das Spiel (asynchron, ohne NetApplying) die Abriss-Markierung der neuen Teile
            if (action == Action.Ready && Time.realtimeSinceStartup < _localUntil && b.ID > _localIdFrom)
                return true;
            SendRequest(action, b.ID, draftId, value, null);
            _session.ShowNotice("An den Host geschickt: " + Describe(action, (CancelKind)Mathf.RoundToInt(value)));
            return false;
        }

        private void CountStat(Action action)
        {
            var st = _session.Stats;
            if (st == null) return;
            switch (action)
            {
                case Action.Adapt:
                case Action.ExtraAdapt:
                case Action.Create:
                    st.AddForActor(StatsSync.Stat.Built);
                    break;
                case Action.Demolish:
                case Action.Deconstruct:
                case Action.Deadapt:
                    st.AddForActor(StatsSync.Stat.Demolished);
                    break;
                case Action.Repair:
                    st.AddForActor(StatsSync.Stat.Repaired);
                    break;
            }
        }

        private void SendRequest(Action action, long id, string draftId, float value, System.Action<BinaryWriter> extra)
        {
            _session.SendToHost(Protocol.Build(Msg.BuildingRequest, w =>
            {
                w.Write((byte)action);
                w.Write(id);
                w.Write(draftId ?? "");
                w.Write(value);
                extra?.Invoke(w);
            }));
            RequestsSent++;
        }

        private static Structure PanelStructure(UI.InfoPanels.SpecificPanelInfo panel)
        {
            var s = panel.Structure;
            if (s == null && panel is UI.InfoPanels.NewStructurePanelInfo && DisplayedStructureF != null)
                s = DisplayedStructureF.GetValue(panel) as Structure;
            return s;
        }

        private static void ClosePanel(UI.InfoPanels.SpecificPanelInfo panel)
        {
            try
            {
                var ipc = InfoPanelController.Instance;
                if (ipc != null && (ipc.CurrentDisplayedPanel == panel || ipc.CurrentSecondDisplayedPanel == panel))
                    ipc.CloseCurrentPanel(panel);
                else
                    panel.ClosePanel();
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("Fenster schließen: " + e.Message);
            }
        }

        /// <summary>Prefix der Abbrechen-/Fortsetzen-Knöpfe in den Fenstern eines Hauses. false = beim Gast nicht selbst ausführen.</summary>
        public bool GuestPanel(UI.InfoPanels.SpecificPanelInfo panel, bool resume)
        {
            if (!GuestCanAsk || panel == null || !(PanelStructure(panel) is Building b))
                return true;
            CancelKind k;
            bool close = true;
            switch (panel)
            {
                case UI.InfoPanels.StructureInProgressPanelInfo _: k = CancelKind.Build; break;
                case UI.InfoPanels.StructureInRepairPanelInfo _: k = CancelKind.Repair; close = false; break;
                case UI.InfoPanels.RuinedStructurePanelInfo _: k = CancelKind.Ruin; break;
                case UI.InfoPanels.WorksPanels.DemolishWorkPanel _: k = resume ? CancelKind.DemolishResume : CancelKind.DemolishDelete; break;
                case UI.InfoPanels.WorksPanels.DeadaptWorkPanel _: k = resume ? CancelKind.DeadaptResume : CancelKind.DeadaptDelete; break;
                case UI.InfoPanels.CustomizationProcessPanel _: k = CancelKind.Customize; close = false; break;
                case UI.InfoPanels.WorksPanels.SplitWorkPanel _: k = CancelKind.Split; break;
                case UI.InfoPanels.BuildingCreationPanelInfo _: k = CancelKind.Create; break;
                case UI.InfoPanels.StructurePlantingExplosivesPanelInfo _: k = CancelKind.Explosives; break;
                default: return true;
            }
            SendRequest(Action.Cancel, b.ID, "", (float)k, null);
            _session.ShowNotice("An den Host geschickt: " + Describe(Action.Cancel, k));
            if (close)
                ClosePanel(panel);
            return false;
        }

        /// <summary>Prefix NewStructurePanelInfo.OnPlantExplosivesCallback.</summary>
        public bool GuestPlantExplosives(UI.InfoPanels.SpecificPanelInfo panel)
        {
            if (!GuestCanAsk || panel == null || !(PanelStructure(panel) is Building b))
                return true;
            SendRequest(Action.Explosives, b.ID, "", 0f, null);
            _session.ShowNotice("An den Host geschickt: " + Describe(Action.Explosives));
            ClosePanel(panel);
            return false;
        }

        /// <summary>Prefix CreationBuildingController.OnConstructionConfirmed. Host: Bau ankündigen; Gast: als Wunsch an den Host.</summary>
        public bool OnCreateConfirmed(CreationBuildingController c)
        {
            var draft = c != null && DraftF != null ? DraftF.GetValue(c) as CustomBuilding : null;
            if (draft == null || draft.CustomBuildingData == null)
                return true;
            if (_session.Role == Role.Host)
            {
                if (_session.ActiveGuestCount > 0)
                {
                    try
                    {
                        AnnounceCreate(CustomData.From(draft.CustomBuildingData));
                        Created++;
                    }
                    catch (Exception e) { Plugin.Log.LogWarning("Eigenes Gebäude ankündigen: " + e.Message); }
                }
                return true;
            }
            if (!GuestCanAsk)
                return true;
            var cd = CustomData.From(draft.CustomBuildingData);
            SendRequest(Action.Create, 0, "", 0f, cd.Write);
            _session.ShowNotice("An den Host geschickt: " + Describe(Action.Create));
            try { CursorsManager.SelectDefaultCursor(); } catch (Exception e) { Plugin.Log.LogDebug("Zeiger: " + e.Message); }
            return false;
        }

        /// <summary>Prefix BuildingSplitController.SendSplitRequest: beim Gast die Teilung als Wunsch an den Host.</summary>
        public bool GuestSplitConfirm(BuildingSplitController c)
        {
            if (!GuestCanAsk || c == null || SplitHandlerF == null)
                return true;
            var h = SplitHandlerF.GetValue(c) as BuildingSplitHandler;
            if (h == null || !h.IsValid || h.Building == null || h.SplitResult == null || h.SplitResult.Length < 2)
                return true; // das Spiel tut dann ohnehin nichts
            try
            {
                var b = h.Building;
                var polygons = new[] { h.SplitResult[0], h.SplitResult[1] };
                var entrances = (List<Vector3>)SplitEntrancesM.Invoke(c, new object[] { b, h.SplitLines });
                var req = new SplitRequest(b, polygons, h.BuildingParts, entrances, h.SplitLength);
                string json = JsonConvert.SerializeObject(SplitInfoCtor.Invoke(new object[] { req }), GameJson.DefaultSettings);
                SendRequest(Action.Split, b.ID, json, 0f, null);
                _session.ShowNotice("An den Host geschickt: " + Describe(Action.Split));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Teilen-Wunsch: " + e);
            }
            try { CursorsManager.SelectDefaultCursor(); } catch (Exception e) { Plugin.Log.LogDebug("Zeiger: " + e.Message); }
            return false;
        }

        /// <summary>Prefix BuildingSplitController.ExecuteRequest (Teilarbeit fertig, jetzt entstehen die neuen Häuser).
        /// Host: Zählerstand ankündigen; Gast: nur beim Nachspielen.</summary>
        public bool OnSplitExecute(SplitRequest req)
        {
            if (_session.Role == Role.Host)
            {
                if (_session.ActiveGuestCount > 0 && req?.Building != null)
                {
                    long id = req.Building.ID;
                    long counter = GetBuildingCounter();
                    BroadcastEvent(w => { w.Write((byte)Ev.SplitDone); w.Write(id); w.Write(counter); });
                    SplitsDone++;
                    Plugin.Log.LogInfo($"IFZCOOP building split {id} -> ab id {counter + 1}");
                }
                return true;
            }
            if (_session.Role == Role.Guest && _session.GuestControlsLocked && !UnitSync.NetApplying)
                return false; // der Gast teilt nur auf Ansage des Hosts
            return true;
        }

        // ================================================================ Gast: Stand

        public void GuestOnState(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 100000)
                throw new InvalidDataException("zu viele Gebäude");
            var list = new List<Entry>(n);
            for (int i = 0; i < n; i++)
                list.Add(new Entry { Id = r.ReadInt64(), State = r.ReadInt32(), Draft = r.ReadString(), Target = r.ReadSingle(), Cur = r.ReadSingle(), Ready = r.ReadBoolean() });
            var bc = GameAccess.Game<BuildingsController>();
            if (bc == null)
                return;
            float now = Time.realtimeSinceStartup;
            UnitSync.NetApplying = true;
            try
            {
                foreach (var e in list)
                {
                    var b = bc.GetBuildingById(e.Id);
                    if (b == null)
                    {
                        // neues Haus, das beim Gast noch entsteht (eigenes Gebäude, Teilung): später noch einmal
                        if (e.State != Gone)
                            _pending[e.Id] = new KeyValuePair<Entry, float>(e, now + PendingTime);
                        else
                            _pending.Remove(e.Id);
                        continue;
                    }
                    _pending.Remove(e.Id);
                    try { Apply(b, e); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"Gebäude {e.Id} -> {(StructureStateType)e.State}: {ex.Message}"); }
                }
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        /// <summary>Gast-Takt: noch nicht vorhandene Häuser nachholen.</summary>
        public void GuestFrame()
        {
            float now = Time.realtimeSinceStartup;
            if (_pending.Count == 0 || now < _nextPending)
                return;
            _nextPending = now + 1f;
            var bc = GameAccess.Game<BuildingsController>();
            if (bc == null)
                return;
            UnitSync.NetApplying = true;
            try
            {
                foreach (var id in _pending.Keys.ToList())
                {
                    var kv = _pending[id];
                    if (now > kv.Value)
                    {
                        _pending.Remove(id);
                        Plugin.Log.LogWarning($"IFZCOOP building {id} fehlt beim Gast (Zustand {(StructureStateType)kv.Key.State})");
                        continue;
                    }
                    var b = bc.GetBuildingById(id);
                    if (b == null)
                        continue;
                    _pending.Remove(id);
                    try { Apply(b, kv.Key); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"Gebäude {id} (nachgeholt): {ex.Message}"); }
                }
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        private void Apply(Building b, Entry e)
        {
            if (e.State == Gone)
            {
                ApplyGone(b);
                return;
            }
            if (b.IsReadyToDemolish != e.Ready)
                b.SetReadyToDemolish(e.Ready);

            var want = (StructureStateType)e.State;
            var have = StateOf(b);

            // Eigenes Gebäude beim Host fertig: wie CreateBuildingWork.OnWorkDone (Material, verlassen oder Umbau)
            if (have == StructureStateType.Creating && want != StructureStateType.Creating && want != StructureStateType.Rubble
                && b.CurrentWork is CreateBuildingWork cw)
            {
                Plugin.Log.LogInfo($"IFZCOOP building {b.ID} Bau fertig -> {want}");
                cw.BuildImmediately();
                have = StateOf(b);
                Applied++;
            }

            if (want == have)
            {
                if (want == StructureStateType.Constructing && b.PartialBuildingHandler != null
                    && Mathf.Abs(b.PartialBuildingHandler.TargetValue - e.Target) > 0.005f)
                    b.PartialBuildingHandler.SetTargetValue(e.Target);
                if (want == StructureStateType.Completed)
                    ApplyHeight(b, e);
                return;
            }
            if (b.IsHq && IsIdle(want))
                return; // HQ kommt über HqSync

            Plugin.Log.LogInfo($"IFZCOOP building {b.ID} {have} -> {want} {e.Draft}");
            switch (want)
            {
                case StructureStateType.Constructing:
                    if (IsIdle(have) && !string.IsNullOrEmpty(e.Draft))
                        StartAdapt(b, e.Draft, e.Target);
                    else if (b.StateMachine.HasState(want))
                        b.StateMachine.ChangeTo(want);
                    break;
                case StructureStateType.Completed:
                    if (IsIdle(have) && !string.IsNullOrEmpty(e.Draft))
                        StartAdapt(b, e.Draft, e.Target);
                    if (StateOf(b) != StructureStateType.Completed && b.StateMachine.HasState(want))
                        b.StateMachine.ChangeTo(want);
                    ApplyHeight(b, e);
                    break;
                case StructureStateType.Abandoned:
                case StructureStateType.Empty:
                case StructureStateType.None:
                    // Arbeit beim Host beendet/abgebrochen: die passenden Spiel-Wege, sonst zurücksetzen
                    if (have == StructureStateType.Split && b.CurrentWork is SplitWork sw)
                        sw.DeleteWork();
                    else if (have == StructureStateType.PlantingExplosives && b.PlantExplosivesWork != null)
                        b.PlantExplosivesWork.CancelWork();
                    else if (have == StructureStateType.Deconstructing)
                        DropStaleDeconstruct(b);
                    if (StateOf(b) != StructureStateType.Abandoned && b.StateMachine.HasState(StructureStateType.Abandoned))
                        b.StateMachine.ChangeTo(StructureStateType.Abandoned);
                    break;
                case StructureStateType.Demolishing:
                    b.StartDemolish();
                    break;
                case StructureStateType.Rubble:
                    if (have == StructureStateType.Demolishing)
                        break; // Abriss läuft beim Gast selbst zu Ende (Animation)
                    if (have == StructureStateType.Split || _splitBusy.Contains(b.ID))
                        return; // die Teilung beim Gast macht das selbst
                    if (have == StructureStateType.Creating && b.CurrentWork is CreateBuildingWork cw2)
                        cw2.DeleteWork(); // Bau abgebrochen (wie CreateBuildingWork.OnWorkDeleted)
                    else if (have == StructureStateType.Deconstructing && b.DeconstructWork != null)
                        b.DeconstructWork.ForceEndWork(); // Abbau fertig (BuildingDeconstructingState.HandleDone)
                    else
                        b.StartDemolish();
                    break;
                case StructureStateType.Deconstructing:
                    if (IsIdle(have) && StartDeconstructionM != null)
                    {
                        DropStaleDeconstruct(b); // sonst zwei Abbau-Arbeiten, das Fenster zeigt die leere (0/52)
                        Quietly(b, () => StartDeconstructionM.Invoke(b, null));
                    }
                    else if (b.StateMachine.HasState(want))
                        b.StateMachine.ChangeTo(want);
                    break;
                case StructureStateType.PlantingExplosives:
                    if (b.StateMachine.HasState(want))
                        Quietly(b, () => b.StateMachine.ChangeTo(StructureStateType.PlantingExplosives));
                    break;
                case StructureStateType.Split:
                    if (IsIdle(have) && _splitInfo.TryGetValue(b.ID, out var json))
                        StartSplitLocal(b, json);
                    else
                        return; // ohne Teil-Flächen nicht nachstellbar
                    break;
                case StructureStateType.Creating:
                    return; // entsteht beim Gast über BuildingEvent
                case StructureStateType.Hideout:
                case StructureStateType.NpcZoneDefault:
                case StructureStateType.NpcZoneHostile:
                    return; // eigene Wege (Verstecke, Siedlungen) – nicht erzwingen
                default:
                    if (b.StateMachine.HasState(want))
                        b.StateMachine.ChangeTo(want);
                    break;
            }
            Applied++;
        }

        /// <summary>Spiel-Zustände öffnen beim Betreten oft das Fenster des Hauses (ShowProperInfoPanel). Beim Gast nur dann
        /// offen lassen, wenn er das Haus ohnehin gerade ansah.</summary>
        private static void Quietly(Building b, System.Action act)
        {
            var ipc = InfoPanelController.Instance;
            var before = ipc != null && ipc.CurrentDisplayedPanel != null ? ipc.CurrentDisplayedPanel.Structure : null;
            act();
            if (ipc == null || before == b)
                return;
            var after = ipc.CurrentDisplayedPanel;
            if (after != null && after.Structure == b)
            {
                try { ipc.CloseCurrentPanel(after); }
                catch (Exception e) { Plugin.Log.LogDebug("Fenster schließen: " + e.Message); }
            }
        }

        /// <summary>Haus ist beim Host verschwunden (z. B. nach dem Teilen oder abgebrochenem Bau).</summary>
        private void ApplyGone(Building b)
        {
            var have = StateOf(b);
            if (have == StructureStateType.Split || _splitBusy.Contains(b.ID))
                return; // die Teilung beim Gast macht das selbst
            Plugin.Log.LogInfo($"IFZCOOP building {b.ID} {have} -> weg");
            if (have == StructureStateType.Creating && b.CurrentWork is CreateBuildingWork cw)
            {
                cw.DeleteWork();
            }
            else if (have != StructureStateType.Rubble && b.StateMachine.HasState(StructureStateType.Rubble))
            {
                b.DestroyReason = DestroyReason.Disassembled;
                b.StateMachine.ChangeTo(StructureStateType.Rubble);
            }
            Applied++;
        }

        // ---------------------------------------------------------------- Gast: HP, Beanspruchung, Pause

        public void GuestOnHealth(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 200000)
                throw new InvalidDataException("zu viele Gebäude-HP");
            var list = new List<KeyValuePair<long, Hp>>(n);
            for (int i = 0; i < n; i++)
            {
                long id = r.ReadInt64();
                list.Add(new KeyValuePair<long, Hp>(id, new Hp { Value = r.ReadSingle(), Max = r.ReadSingle(), Flags = (HF)r.ReadByte(), Explosives = r.ReadInt32() }));
            }
            var bc = GameAccess.Game<BuildingsController>();
            if (bc == null)
                return;
            UnitSync.NetApplying = true;
            try
            {
                foreach (var kv in list)
                {
                    var b = bc.GetBuildingById(kv.Key);
                    if (b == null)
                        continue;
                    try { ApplyHp(b, kv.Value); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"Gebäude-HP {kv.Key}: {ex.Message}"); }
                }
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        private void ApplyHp(Building b, Hp v)
        {
            var h = b.Health;
            if (h != null)
            {
                float target = v.Max > 0.01f && h.MaxHp > 0.01f ? v.Value / v.Max * h.MaxHp : v.Value;
                target = Mathf.Clamp(target, 0f, Mathf.Max(h.MaxHp, 0f));
                if (Mathf.Abs(h.Hp - target) > 0.5f)
                {
                    // Schaden wie Structure.SubtractHp: Reparaturkosten mitführen (für Knopf und Tooltip), aber ohne OnDeath
                    if (h.Hp > target)
                        b.UpdateRepairCost(h.Hp - target);
                    h.SetHp(target);
                    if (h.IsMax)
                        b.ResetRepairCost();
                    HpApplied++;
                }
            }
            bool claimed = (v.Flags & HF.Claimed) != 0;
            if (claimed && !b.IsClaimedByPlayer)
                b.MarkAsClaimedByPlayer();
            else if (!claimed && b.IsClaimedByPlayer)
                b.MarkAsUnclaimedByPlayer();
            bool dp = (v.Flags & HF.DeconstructPaused) != 0;
            if (b.DeconstructWork != null && b.DeconstructWork.IsPaused != dp)
                b.DeconstructWork.SetPauseState(dp);
            bool ap = (v.Flags & HF.DeadaptPaused) != 0;
            if (b.DeadaptationWork != null && b.DeadaptationWork.IsPaused != ap)
                b.DeadaptationWork.SetPauseState(ap);
            if (v.Explosives > 0 && b.PlantExplosivesWork == null)
            {
                var stock = GameAccess.Game<StockroomsController>();
                if (stock != null)
                    b.StartPlantingExplosives(stock);
            }
            if (b.PlantExplosivesWork != null && b.PlantExplosivesWork.PlantedExplosives != v.Explosives)
                b.PlantExplosivesWork.SetPlantedExplosivesCount(v.Explosives);
        }

        // ---------------------------------------------------------------- Gast: neue Häuser

        public void GuestOnEvent(BinaryReader r)
        {
            var op = (Ev)r.ReadByte();
            switch (op)
            {
                case Ev.Create:
                {
                    long counter = r.ReadInt64();
                    var cd = CustomData.Read(r);
                    GuestCreate(counter, cd);
                    break;
                }
                case Ev.SplitStart:
                {
                    long id = r.ReadInt64();
                    string json = r.ReadString();
                    _splitInfo[id] = json;
                    var b = GameAccess.Game<BuildingsController>()?.GetBuildingById(id);
                    if (b != null && IsIdle(StateOf(b)))
                    {
                        UnitSync.NetApplying = true;
                        try { StartSplitLocal(b, json); }
                        catch (Exception e) { Plugin.Log.LogWarning($"Teilung {id} nachstellen: {e.Message}"); }
                        finally { UnitSync.NetApplying = false; }
                    }
                    break;
                }
                case Ev.SplitDone:
                {
                    long id = r.ReadInt64();
                    long counter = r.ReadInt64();
                    GuestSplitDone(id, counter);
                    break;
                }
                default:
                    throw new InvalidDataException("unbekanntes Gebäude-Ereignis " + op);
            }
        }

        private void GuestCreate(long counter, CustomData cd)
        {
            if (!CreationParts(out _, out var factory, out var config))
            {
                Plugin.Log.LogWarning("IFZCOOP eigenes Gebäude: Bau beim Gast nicht erreichbar");
                return;
            }
            UnitSync.NetApplying = true;
            try
            {
                var data = cd.Build(config);
                var draft = factory.Create();
                draft.Initialize(data);
                if (!SetBuildingCounter(counter))
                    Plugin.Log.LogWarning("IFZCOOP eigenes Gebäude: Gebäude-Zähler nicht gesetzt, IDs können abweichen");
                _localUntil = Time.realtimeSinceStartup + 15f;
                _localIdFrom = counter;
                var task = draft.CreateBuilding(CancellationToken.None);
                long now = GetBuildingCounter();
                if (now != counter + 1)
                {
                    IdMismatch++;
                    Plugin.Log.LogWarning($"IFZCOOP eigenes Gebäude: ID {now} statt {counter + 1}");
                }
                Created++;
                Plugin.Log.LogInfo($"IFZCOOP building create id={counter + 1} (vom Host)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Eigenes Gebäude nachbauen: " + e);
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        private SplitRequest GuestSplitRequest(Building b)
        {
            if (b.CurrentWork is SplitWork sw && SplitRequestF != null && SplitRequestF.GetValue(sw) is SplitRequest req)
                return req;
            if (_splitInfo.TryGetValue(b.ID, out var json))
                return JsonConvert.DeserializeObject<SplitRequest.SerializedInfo>(json, GameJson.DefaultSettings).Deserialize(b);
            return null;
        }

        /// <summary>Wie BuildingSplitController.SendSplitRequest (ohne Arbeiter zuteilen).</summary>
        private void StartSplitLocal(Building b, string json)
        {
            var req = JsonConvert.DeserializeObject<SplitRequest.SerializedInfo>(json, GameJson.DefaultSettings).Deserialize(b);
            b.StateMachine.ChangeTo(StructureStateType.Split);
            if (b.StateMachine.CurrentState is BuildingSplitState st)
                st.CreateWork(req, assignWorkers: false);
            SplitsStarted++;
            Plugin.Log.LogInfo($"IFZCOOP building {b.ID} Teilung nachgestellt");
        }

        /// <summary>Teilung beim Host fertig: die neuen Häuser über BuildingSplitController.ExecuteRequest mit denselben IDs bauen.</summary>
        private void GuestSplitDone(long id, long counter)
        {
            var bc = GameAccess.Game<BuildingsController>();
            var b = bc?.GetBuildingById(id);
            var ctrl = GameAccess.Game<BuildingSplitController>();
            if (b == null || ctrl == null || ExecuteRequestM == null)
            {
                Plugin.Log.LogWarning($"IFZCOOP building split {id}: Haus oder Teilen beim Gast nicht da");
                return;
            }
            UnitSync.NetApplying = true;
            try
            {
                var req = GuestSplitRequest(b);
                if (req == null)
                {
                    Plugin.Log.LogWarning($"IFZCOOP building split {id}: Teil-Flächen fehlen");
                    return;
                }
                if (b.IsReadyToDemolish && b.PlantExplosivesWork == null)
                {
                    var stock = GameAccess.Game<StockroomsController>();
                    if (stock != null) b.StartPlantingExplosives(stock);
                }
                if (!SetBuildingCounter(counter))
                    Plugin.Log.LogWarning("IFZCOOP Teilung: Gebäude-Zähler nicht gesetzt, IDs können abweichen");
                _splitBusy.Add(id);
                _localUntil = Time.realtimeSinceStartup + 15f;
                _localIdFrom = counter;
                ExecuteRequestM.Invoke(ctrl, new object[] { req });
                long now = GetBuildingCounter();
                if (now <= counter)
                {
                    IdMismatch++;
                    Plugin.Log.LogWarning($"IFZCOOP Teilung {id}: Zähler {now} nach dem Start (erwartet > {counter})");
                }
                SplitsDone++;
                _splitInfo.Remove(id);
                Plugin.Log.LogInfo($"IFZCOOP building split {id} -> ab id {counter + 1} (vom Host)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Teilung {id} nachbauen: {e}");
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        // ================================================================ Status

        /// <summary>Für die Statusdatei: Zahl der Häuser je Zustand und Prüfsumme.</summary>
        public static string StatusLine()
        {
            var bc = GameAccess.Game<BuildingsController>();
            if (bc == null)
                return "buildings=?";
            var entries = new List<Entry>();
            foreach (var b in bc.BuildingsWithInteraction)
                if (b != null && !IsIdle(StateOf(b))) entries.Add(Make(b));
            foreach (var b in bc.Buildings)
                if (b != null && b.IsReadyToDemolish && IsIdle(StateOf(b))) entries.Add(Make(b));
            uint hash = 2166136261;
            foreach (var e in entries.OrderBy(x => x.Id))
                foreach (char ch in e.Id + ":" + e.State + e.Draft + e.Ready)
                    hash = (hash ^ ch) * 16777619;
            var byState = entries.GroupBy(x => (StructureStateType)x.State).OrderBy(g => g.Key)
                .Select(g => g.Key + "=" + g.Count());
            return $"buildings {string.Join(" ", byState)} ready={entries.Count(x => x.Ready)} hash={hash:x8}";
        }

        /// <summary>Für die Statusdatei: HP/Beanspruchung (5-%-Stufen) und Zahl/höchste ID der Häuser.</summary>
        public static IEnumerable<string> ExtraStatusLines()
        {
            var bc = GameAccess.Game<BuildingsController>();
            if (bc == null)
                yield break;
            int damaged = 0, claimed = 0, total = 0;
            long maxId = long.MinValue;
            uint hash = 2166136261;
            foreach (var b in bc.Buildings.Where(x => x != null).OrderBy(x => x.ID))
            {
                total++;
                if (b.ID > maxId) maxId = b.ID;
                var h = b.Health;
                bool dmg = h != null && !h.IsMax;
                if (dmg) damaged++;
                if (b.IsClaimedByPlayer) claimed++;
                if (!dmg && !b.IsClaimedByPlayer)
                    continue;
                int pct = h != null && h.MaxHp > 0.01f ? Mathf.RoundToInt(h.Hp / h.MaxHp * 20f) : 0;
                foreach (char ch in b.ID + ":" + pct + (b.IsClaimedByPlayer ? "c" : ""))
                    hash = (hash ^ ch) * 16777619;
            }
            yield return $"bldhp damaged={damaged} claimed={claimed} hash={hash:x8}";
            yield return $"bldcount total={total} maxId={maxId}";
            var hq = HqController.MainHeadquarter;
            if (hq != null && hq.Health != null)
                yield return $"bldhq hp={Mathf.RoundToInt(hq.Health.Hp / Mathf.Max(1f, hq.Health.MaxHp) * 100f)}%";
        }

        public string StatusLine2() =>
            $"bld2 hpApplied={HpApplied} hpSent={HpSent} created={Created} splitsStarted={SplitsStarted} splitsDone={SplitsDone} " +
            $"events={EventsSent} idMismatch={IdMismatch} pending={_pending.Count} counter={GetBuildingCounter()}";

        /// <summary>Für die Statusdatei: die Häuser mit Zustand einzeln (höchstens 12).</summary>
        public static IEnumerable<string> DetailLines()
        {
            var bc = GameAccess.Game<BuildingsController>();
            if (bc == null)
                yield break;
            int n = 0;
            foreach (var b in bc.BuildingsWithInteraction.OrderBy(x => x.ID))
            {
                if (b == null || IsIdle(StateOf(b))) continue;
                if (++n > 12) yield break;
                yield return $"bld {b.ID} {StateOf(b)} {b.Draft?.Id} target={b.PartialBuildingHandler?.TargetValue.ToString("0.00", CultureInfo.InvariantCulture)} cur={b.PartialBuildingHandler?.CurrentPartialValue.ToString("0.00", CultureInfo.InvariantCulture)}";
            }
        }

        // ================================================================ Testbefehle

        private static Building Near(Vector3 hqPos, Func<Building, bool> ok)
        {
            Building best = null;
            float bestD = float.MaxValue;
            foreach (var b in GameAccess.Game<BuildingsController>().Buildings)
            {
                if (b == null || !ok(b)) continue;
                float d = (b.transform.position - hqPos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = b; }
            }
            return best;
        }

        private static Building Pick(string[] a, int idx, Vector3 hqPos, Func<Building, bool> ok)
        {
            if (a.Length > idx)
            {
                if (a[idx] == "hq") return HqController.MainHeadquarter as Building;
                if (long.TryParse(a[idx], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
                    return GameAccess.Game<BuildingsController>().GetBuildingById(id);
            }
            return Near(hqPos, ok);
        }

        private static bool Abandoned(Building b) =>
            !b.IsHq && StateOf(b) == StructureStateType.Abandoned && !b.IsReadyToDemolish && b.Draft != null && b.Draft.IsInteractable;

        /// <summary>
        /// "bld2 damage <HP> [hq|id]" (Host: Schaden), "bld2 heal [hq|id]" (Host: voll), "bld2 panel [resume] [id]" (Abbrechen-
        /// bzw. Fortsetzen-Knopf im Fenster des Hauses drücken; ohne id das nächste Haus mit laufender Arbeit),
        /// "bld2 explosives [id]", "bld2 claim [id]", "bld2 deconstruct [id]", "bld2 create [Größe]", "bld2 split [id]",
        /// "bld2 repair [id]", "bld2 finish [id]" (Host: Bau-/Teil-/Erstell-Arbeit sofort fertig), "bld2 state <id>", "bld2 status". Beim Gast laufen alle über die abgefangenen Spiel-Methoden, gehen also als Wunsch an den Host.
        /// </summary>
        public string Qa(string[] a, Vector3 hqPos)
        {
            string what = a.Length > 1 ? a[1] : "status";
            switch (what)
            {
                case "damage":
                {
                    float amount = a.Length > 2 ? float.Parse(a[2], CultureInfo.InvariantCulture) : 50f;
                    var b = Pick(a, 3, hqPos, x => !x.IsHq && x.IsDone);
                    if (b == null || b.Health == null) return "kein Haus";
                    b.SubtractHp(amount, Gameplay.Units.SubtractionHpReason.None);
                    return $"Haus {b.ID} hp={b.Health.Hp.ToString("0", CultureInfo.InvariantCulture)}/{b.Health.MaxHp.ToString("0", CultureInfo.InvariantCulture)}";
                }
                case "repair":
                {
                    var b = Pick(a, 2, hqPos, x => x.IsDone && x.Health != null && !x.Health.IsMax && x.IsClaimedByPlayer);
                    if (b == null) return "kein beschädigtes Haus";
                    b.StartRepairing();
                    return $"Haus {b.ID} reparieren -> {StateOf(b)}";
                }
                case "finish":
                {
                    // Host: laufende Bau-, Teil- oder Erstell-Arbeit sofort fertig (wie BuildImmediately)
                    var b = Pick(a, 2, hqPos, x => x.CurrentWork is ConstructableWork);
                    if (b == null || !(b.CurrentWork is ConstructableWork cw)) return "kein Haus mit Bauarbeit";
                    cw.BuildImmediately();
                    return $"Haus {b.ID} fertig -> {StateOf(b)}";
                }
                case "state":
                {
                    var b = Pick(a, 2, hqPos, x => false);
                    if (b == null) return "kein Haus";
                    return $"Haus {b.ID} {StateOf(b)} hp={(b.Health != null ? b.Health.Hp : 0f).ToString("0", CultureInfo.InvariantCulture)} beansprucht={b.IsClaimedByPlayer} Arbeit={b.CurrentWork?.GetType().Name}";
                }
                case "heal":
                {
                    var b = Pick(a, 2, hqPos, x => x.Health != null && !x.Health.IsMax);
                    if (b == null || b.Health == null) return "kein Haus";
                    b.Health.SetMaxHealth();
                    b.ResetRepairCost();
                    return $"Haus {b.ID} voll";
                }
                case "panel":
                {
                    bool resume = a.Length > 2 && a[2] == "resume";
                    int idx = resume ? 3 : 2;
                    var b = Pick(a, idx, hqPos, x => !x.IsHq && !IsIdle(StateOf(x)) && StateOf(x) != StructureStateType.Completed && StateOf(x) != StructureStateType.Rubble);
                    if (b == null) return "kein Haus mit Arbeit";
                    b.ShowProperInfoPanel();
                    var panel = InfoPanelController.Instance?.CurrentDisplayedPanel;
                    if (panel == null) return "kein Fenster";
                    string m;
                    if (panel is UI.Panels.WorksPanels.WorkPanel) m = resume ? "OnResumed" : "OnDeleted";
                    else m = "OnCancelButtonClicked";
                    var mi = AccessTools.Method(panel.GetType(), m);
                    if (mi == null) return $"Fenster {panel.GetType().Name} ohne {m}";
                    mi.Invoke(panel, null);
                    return $"Haus {b.ID} {StateOf(b)} Fenster {panel.GetType().Name}.{m}";
                }
                case "explosives":
                {
                    var b = Pick(a, 2, hqPos, Abandoned);
                    if (b == null) return "kein Haus";
                    b.ShowProperInfoPanel();
                    var panel = InfoPanelController.Instance?.CurrentDisplayedPanel;
                    if (!(panel is UI.InfoPanels.NewStructurePanelInfo))
                        return "Fenster " + (panel != null ? panel.GetType().Name : "-");
                    AccessTools.Method(panel.GetType(), "OnPlantExplosivesCallback").Invoke(panel, null);
                    return $"Haus {b.ID} -> {StateOf(b)}";
                }
                case "claim":
                {
                    var b = Pick(a, 2, hqPos, x => !x.IsHq && !x.IsClaimedByPlayer);
                    if (b == null) return "kein Haus";
                    b.MakePartOfZone();
                    return $"Haus {b.ID} beansprucht={b.IsClaimedByPlayer}";
                }
                case "deconstruct":
                {
                    var b = Pick(a, 2, hqPos, x => Abandoned(x) && x.Draft.IsDeconstructable);
                    if (b == null) return "kein Haus";
                    StartDeconstructionM.Invoke(b, null);
                    return $"Haus {b.ID} -> {StateOf(b)}";
                }
                case "create":
                {
                    float size = a.Length > 2 ? float.Parse(a[2], CultureInfo.InvariantCulture) : 8f;
                    if (!CreationParts(out var ctrl, out var factory, out var config)) return "Bau eigener Gebäude nicht erreichbar";
                    var bc = GameAccess.Game<BuildingsController>();
                    Vector3 spot = Vector3.zero;
                    bool found = false;
                    for (int ring = 3; ring < 12 && !found; ring++)
                        for (int k = 0; k < 16 && !found; k++)
                        {
                            float ang = k * Mathf.PI * 2f / 16f;
                            var p = hqPos + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * (ring * 10f);
                            if (bc.GetBuildingsByPosition(p, size * 1.5f + 6f).Count == 0) { spot = p; found = true; }
                        }
                    if (!found) return "kein freier Platz";
                    float hs = size * 0.5f;
                    var pts = new List<Vector3>
                    {
                        new Vector3(spot.x - hs, 0f, spot.z - hs).UnitySampleHeight(),
                        new Vector3(spot.x - hs, 0f, spot.z + hs).UnitySampleHeight(),
                        new Vector3(spot.x + hs, 0f, spot.z + hs).UnitySampleHeight(),
                        new Vector3(spot.x + hs, 0f, spot.z - hs).UnitySampleHeight(),
                    };
                    var cd = new CustomData
                    {
                        Points = pts, Height = Mathf.Max(config.MinBuildingHeight, 6f), RoofAngle = 30, RoofType = 0,
                        Facade = (int)FacadeType.wood, Wall = Color.gray, Roof = Color.red,
                    };
                    var data = cd.Build(config);
                    var draft = factory.Create();
                    draft.Initialize(data);
                    DraftF.SetValue(ctrl, draft);
                    long before = GetBuildingCounter();
                    AccessTools.Method(typeof(CreationBuildingController), "OnConstructionConfirmed").Invoke(ctrl, null);
                    if (draft != null)
                        UnityEngine.Object.Destroy(draft.gameObject); // beim Gast bleibt die Vorschau sonst stehen
                    return $"eigenes Gebäude bei {spot.x.ToString("0", CultureInfo.InvariantCulture)},{spot.z.ToString("0", CultureInfo.InvariantCulture)} gültig={data.IsValid} ({data.InValidReason}) id={before + 1}";
                }
                case "split":
                {
                    var b = Pick(a, 2, hqPos, x => Abandoned(x) && x.AABB.width > 14f && x.AABB.height > 14f);
                    if (b == null) return "kein Haus";
                    var ctrl = GameAccess.Game<BuildingSplitController>();
                    var h = ctrl != null ? SplitHandlerF.GetValue(ctrl) as BuildingSplitHandler : null;
                    if (h == null) return "Teilen nicht erreichbar";
                    var r = b.AABB;
                    var c = r.center;
                    var path = r.width >= r.height
                        ? new List<Vector2> { new Vector2(c.x, r.yMin - 3f), new Vector2(c.x, r.yMax + 3f) }
                        : new List<Vector2> { new Vector2(r.xMin - 3f, c.y), new Vector2(r.xMax + 3f, c.y) };
                    h.SetBuilding(b);
                    if (!h.TrySplitBuilding(new List<List<Vector2>> { path }))
                        return $"Haus {b.ID} nicht teilbar: {h.SplitFailReason}";
                    ctrl.SendSplitRequest();
                    return $"Haus {b.ID} teilen -> {StateOf(b)}";
                }
                default:
                    return StatusLine2();
            }
        }
    }
}
