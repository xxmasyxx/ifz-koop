using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Data.Building;
using Gameplay.ObjectOnMap;
using Gameplay.Rebuilding;
using Gameplay.Rebuilding.Farmlands;
using Gameplay.Rebuilding.Gates;
using Gameplay.Rebuilding.StateMachine;
using Gameplay.Rebuilding.Towers;
using Gameplay.Rebuilding.Walls;
using Gameplay.Units.Workers.WorkSystem.Works;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using Newtonsoft.Json;
using Steamworks;
using UnityEngine;
using Utilities;
using GameJson = Utilities.JsonSerializer;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Gebaute Anlagen außerhalb von Häusern: Türme, Mauern, Tore, Felder, Minenfelder, Fischer, Lehmgruben/Friedhöfe.
    ///
    /// Host: 1×/s je Anlage (Kennung = Structure.GetId(), Art, Bauplan, Zustand, HP, Baufortschritt, Ort/Drehung/Größe).
    /// Geänderte Einträge gehen an die Gäste; neue zusätzlich als Spielstand-JSON (SerializedPlaceableStructure &amp; Co.,
    /// dieselben Klassen, mit denen das Spiel speichert). Alle 10 s alles (ohne JSON) und eine Liste aller Kennungen.
    /// Gast: legt fehlende Anlagen so an, wie das Spiel sie aus dem Spielstand lädt (der jeweilige Controller.Load()
    /// mit einem Spielstand-Ausschnitt, der nur diese Anlage enthält; LoadPlaceable übernimmt die Host-Kennung),
    /// stellt Zustand, HP und Baufortschritt nach, entfernt Anlagen, die der Host nicht (mehr) hat. Fehlt ihm das JSON
    /// einer Anlage, fragt er danach (Op.NeedJson).
    /// Klicks des Gastes (Platzieren, Bau abbrechen, Abriss, Abriss abbrechen, Reparatur, Ruine entfernen) gehen als Wunsch
    /// an den Host; beim Gast entsteht nichts selbst. Eigenes Fertigwerden der Baustelle ist beim Gast gesperrt.
    /// </summary>
    internal sealed class StructureSync
    {
        private const float SendInterval = 1f;
        private const float ForceInterval = 10f;
        private const float NeedInterval = 2f;

        public enum Kind : byte { None = 0, Tower = 1, Wall = 2, Gate = 3, Farm = 4, Minefield = 5, Fisherman = 6, ClayPit = 7 }

        public enum Op : byte
        {
            Place = 1, CancelBuild = 2, Deconstruct = 3, CancelDeconstruct = 4, Repair = 5, CancelRepair = 6, RemoveRuin = 7,
            NeedJson = 20,
        }

        private sealed class Entry
        {
            public string Id = "";
            public Kind Kind;
            public string Draft = "";
            public int State;
            public float Hp, MaxHp, Progress, TimeCons, TimeTo;
            public Vector3 Pos, Rot, Scale;
            public bool Claimed;
            public string Json = "";

            public string Sig =>
                (int)Kind + "|" + Draft + "|" + State + "|" + Mathf.RoundToInt(Hp) + "|" + Mathf.RoundToInt(Progress * 100f) + "|"
                + Mathf.RoundToInt(Pos.x * 10f) + "," + Mathf.RoundToInt(Pos.z * 10f) + "|" + Mathf.RoundToInt(Rot.y) + "|"
                + Mathf.RoundToInt(Scale.x * 100f) + "|" + (Claimed ? 1 : 0);

            public void Write(BinaryWriter w)
            {
                w.Write(Id ?? "");
                w.Write((byte)Kind);
                w.Write(Draft ?? "");
                w.Write(State);
                w.Write(Hp); w.Write(MaxHp); w.Write(Progress); w.Write(TimeCons); w.Write(TimeTo);
                WriteV(w, Pos); WriteV(w, Rot); WriteV(w, Scale);
                w.Write(Claimed);
                w.Write(Json ?? "");
            }

            public static Entry Read(BinaryReader r)
            {
                var e = new Entry
                {
                    Id = r.ReadString(),
                    Kind = (Kind)r.ReadByte(),
                    Draft = r.ReadString(),
                    State = r.ReadInt32(),
                    Hp = r.ReadSingle(), MaxHp = r.ReadSingle(), Progress = r.ReadSingle(), TimeCons = r.ReadSingle(), TimeTo = r.ReadSingle(),
                };
                e.Pos = ReadV(r); e.Rot = ReadV(r); e.Scale = ReadV(r);
                e.Claimed = r.ReadBoolean();
                e.Json = r.ReadString();
                return e;
            }
        }

        /// <summary>Ein Stück Anlage für den Platzier-Wunsch (Mauern: mehrere Stücke).</summary>
        private struct Seg
        {
            public Vector3 Pos, Rot, Scale;
        }

        private readonly Session _session;
        private readonly Dictionary<string, string> _sent = new Dictionary<string, string>();
        private HashSet<string> _known;
        private float _nextSend;
        private float _nextForce;

        // Gast
        private readonly HashSet<string> _need = new HashSet<string>();
        private float _nextNeed;

        /// <summary>Zuletzt platzierte bzw. beim Gast zuletzt angelegte Anlage (für Testbefehle "last").</summary>
        public static string LastId { get; private set; } = "";

        public int Applied { get; private set; }
        public int Created { get; private set; }
        public int CreateFailed { get; private set; }
        public int Removed { get; private set; }
        public int Finished { get; private set; }
        public int BlockedDone { get; private set; }
        public int NeedSent { get; private set; }
        public int RequestsSent { get; private set; }
        public int RequestsDone { get; private set; }
        public int Rejected { get; private set; }

        public StructureSync(Session session)
        {
            _session = session;
        }

        public void ResendAll()
        {
            _sent.Clear();
            _nextSend = 0f;
            _nextForce = 0f;
        }

        public void Reset()
        {
            ResendAll();
            _known = null;
            _need.Clear();
        }

        // ================================================================ Zugriff

        private static PlaceableObjectsController Poc => GameAccess.Game<PlaceableObjectsController>();

        private static readonly MethodInfo CreateTowerMethod = AccessTools.Method(typeof(TowersController), "CreateTower", new[] { typeof(PlaceableObjectDraft) });
        private static readonly MethodInfo CreateGateMethod = AccessTools.Method(typeof(GatesController), "CreateGate", new[] { typeof(PlaceableObjectDraft) });
        private static readonly FieldInfo WallFactoryField = AccessTools.Field(typeof(WallsController), "_wallFactory");
        private static readonly FieldInfo CursorConstructorField = AccessTools.Field(typeof(WallCursor), "_wallConstructor");
        private static readonly FieldInfo CtorWallsField = AccessTools.Field(typeof(WallConstructor), "_walls");
        private static readonly FieldInfo CtorDraftField = AccessTools.Field(typeof(WallConstructor), "_draft");
        private static readonly FieldInfo CtorGeneratedField = AccessTools.Field(typeof(WallConstructor), "_isGenerated");
        private static readonly MethodInfo CtorSetOrigin = AccessTools.Method(typeof(WallConstructor), "SetOriginInfo", new[] { typeof(Wall.SnapInfo) });
        private static readonly MethodInfo CtorUpdate = AccessTools.Method(typeof(WallConstructor), "Update", new[] { typeof(Wall.SnapInfo) });
        private static readonly MethodInfo OnConstructionDoneMethod = AccessTools.Method(typeof(Structure), "OnConstructionDone");
        private static readonly MethodInfo TimeConstructingSetter = AccessTools.PropertySetter(typeof(ConstructableWork), "TimeConstructing");
        private static readonly MethodInfo StateTypeSetter = AccessTools.PropertySetter(typeof(SerializedStructureWithInteraction), "StructureStateType");

        // Über Reflection: das Projekt verweist nicht auf UnityEngine.PhysicsModule (Collider im Methodenkopf)
        private static readonly MethodInfo IsInValidPositionMethod = AccessTools.Method(typeof(ValidatableStructure), "IsInValidPosition");
        private static readonly MethodInfo SyncTransformsMethod = AccessTools.Method(AccessTools.TypeByName("UnityEngine.Physics"), "SyncTransforms");

        private static void SyncPhysics()
        {
            try { SyncTransformsMethod?.Invoke(null, null); }
            catch (Exception e) { Plugin.Log.LogDebug("Physics.SyncTransforms: " + e.Message); }
        }

        /// <summary>Wie der Bau-Cursor: IsInValidPosition(out BuildBlockReason, exclude: null).</summary>
        private static bool IsValidSpot(PlaceableObject o, out string reason)
        {
            reason = "";
            if (IsInValidPositionMethod == null)
                return true;
            var args = new object[] { null, null };
            bool ok = (bool)IsInValidPositionMethod.Invoke(o, args);
            reason = args[0]?.ToString() ?? "";
            return ok;
        }

        private static StructureStateType StateOf(Structure s) => s?.StateMachine?.CurrentState?.StateType ?? StructureStateType.None;

        private static Kind KindOf(Structure s)
        {
            switch (s)
            {
                case Tower _: return Kind.Tower;
                case Wall _: return Kind.Wall;
                case Gate _: return Kind.Gate;
                case Minefield _: return Kind.Minefield;
                case Farmland _: return Kind.Farm;
                case Fisherman _: return Kind.Fisherman;
                case ClayPit _: return Kind.ClayPit;
                default: return Kind.None;
            }
        }

        private static Kind KindOf(PlaceableObjectType t)
        {
            switch (t)
            {
                case PlaceableObjectType.Tower: return Kind.Tower;
                case PlaceableObjectType.Wall: return Kind.Wall;
                case PlaceableObjectType.Gate: return Kind.Gate;
                case PlaceableObjectType.Farm: return Kind.Farm;
                case PlaceableObjectType.Minefield: return Kind.Minefield;
                case PlaceableObjectType.FishermanStructure: return Kind.Fisherman;
                case PlaceableObjectType.ClayPitStructure: return Kind.ClayPit;
                default: return Kind.None;
            }
        }

        private static string KindName(Kind k)
        {
            switch (k)
            {
                case Kind.Tower: return "Turm";
                case Kind.Wall: return "Mauer";
                case Kind.Gate: return "Tor";
                case Kind.Farm: return "Feld";
                case Kind.Minefield: return "Minenfeld";
                case Kind.Fisherman: return "Fischer";
                case Kind.ClayPit: return "Grube";
                default: return "Anlage";
            }
        }

        /// <summary>Alle gebauten Anlagen in fester Reihenfolge (ohne Konvoi/HQ-Bus).</summary>
        internal static IEnumerable<PlaceableStructure> All()
        {
            var p = Poc;
            if (p == null)
                yield break;
            foreach (var x in p.TowersController.Towers) if (x != null) yield return x;
            foreach (var x in p.WallsController.Walls) if (x != null) yield return x;
            foreach (var x in p.GatesController.Gates) if (x != null) yield return x;
            foreach (var x in p.FarmlandsController.Farmlands) if (x != null) yield return x;
            foreach (var x in p.MinefieldController.Minefields) if (x != null) yield return x;
            foreach (var x in p.FishermanStructureController.Fishermans) if (x != null) yield return x;
            foreach (var x in p.ClayPitStructureController.Structures) if (x != null) yield return x;
        }

        private static Dictionary<string, PlaceableStructure> Index()
        {
            var d = new Dictionary<string, PlaceableStructure>();
            foreach (var s in All())
            {
                if (s.IsGameObjectDestroyed) continue;
                string id = s.GetId();
                if (!d.ContainsKey(id)) d[id] = s;
            }
            return d;
        }

        private static PlaceableObjectDraft DraftById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            try { return BuildingsDatas.Instance.GetPlaceableObjectDraftById(id); }
            catch { return null; }
        }

        private static void WriteV(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        private static Vector3 ReadV(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        // ================================================================ Host

        private static Entry Make(PlaceableStructure s)
        {
            var t = s.transform;
            var st = StateOf(s);
            var e = new Entry
            {
                Id = s.GetId(),
                Kind = KindOf(s),
                Draft = s.Draft != null ? s.Draft.Id ?? "" : "",
                State = (int)st,
                Hp = s.Health?.Hp ?? 0f,
                MaxHp = s.Health?.MaxHp ?? 0f,
                Pos = t.position,
                Rot = t.eulerAngles,
                Scale = t.localScale,
                Claimed = s.IsClaimedByPlayer,
            };
            if (st == StructureStateType.Constructing && s.BuildStructureWork != null)
            {
                e.Progress = s.BuildStructureWork.Progress;
                e.TimeCons = s.BuildStructureWork.TimeConstructing;
                e.TimeTo = s.BuildStructureWork.TimeToConstruct;
            }
            else if (st == StructureStateType.Deconstructing && s.DeconstructWork != null)
            {
                e.Progress = s.DeconstructWork.Progress;
                e.TimeCons = s.DeconstructWork.TimeDeconstructing;
                e.TimeTo = s.DeconstructWork.TimeToDeconstruct;
            }
            return e;
        }

        /// <summary>Spielstand-Eintrag der Anlage, wie das Spiel ihn speichert.</summary>
        private static string MakeJson(PlaceableStructure s, Kind k)
        {
            try
            {
                object ser;
                if (k == Kind.Minefield) ser = new SerializedMinefield((Minefield)s);
                else if (k == Kind.Tower || k == Kind.Wall || k == Kind.Gate) ser = new SerializedDefensibleStructure(s);
                else ser = new SerializedPlaceableStructure(s);
                return JsonConvert.SerializeObject(ser, GameJson.DefaultSettings);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Anlage {s.GetId()} als JSON: {ex.Message}");
                return "";
            }
        }

        private Dictionary<string, KeyValuePair<Entry, PlaceableStructure>> Take()
        {
            var d = new Dictionary<string, KeyValuePair<Entry, PlaceableStructure>>();
            foreach (var s in All())
            {
                if (s.IsGameObjectDestroyed) continue;
                var k = KindOf(s);
                if (k == Kind.None) continue;
                var st = StateOf(s);
                if (st == StructureStateType.Placing || st == StructureStateType.None) continue;
                // Feste Kennung: neu gebaute Anlagen haben noch keine (GetId liefert dann die Instanz-Nummer, die auch gespeichert würde)
                if (string.IsNullOrEmpty(s.Id)) s.SetId(s.GetId());
                var e = Make(s);
                if (!d.ContainsKey(e.Id)) d[e.Id] = new KeyValuePair<Entry, PlaceableStructure>(e, s);
            }
            return d;
        }

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend || _session.ActiveGuestCount == 0 || !GameAccess.IsGameStarted)
                return;
            _nextSend = now + SendInterval;
            bool force = now >= _nextForce;
            if (force) _nextForce = now + ForceInterval;

            Dictionary<string, KeyValuePair<Entry, PlaceableStructure>> cur;
            try { cur = Take(); }
            catch (Exception e) { Plugin.Log.LogDebug($"Anlagen-Stand: {e.Message}"); return; }

            // Was es schon gab, als der Gast den Spielstand bekam, hat er; JSON nur für später Gebautes
            if (_known == null)
                _known = new HashSet<string>(cur.Keys);

            var changed = new List<Entry>();
            foreach (var kv in cur)
            {
                var e = kv.Value.Key;
                bool isNew = !_known.Contains(e.Id);
                if (isNew)
                {
                    e.Json = MakeJson(kv.Value.Value, e.Kind);
                    _known.Add(e.Id);
                    LastId = e.Id;
                }
                if (force || isNew || !_sent.TryGetValue(e.Id, out var last) || last != e.Sig)
                    changed.Add(e);
            }
            var removed = _sent.Keys.Where(id => !cur.ContainsKey(id)).ToList();
            _sent.Clear();
            foreach (var kv in cur) _sent[kv.Key] = kv.Value.Key.Sig;

            if (changed.Count > 0 || removed.Count > 0)
                SendEntries(changed, removed, CSteamID.Nil);
            if (force)
            {
                var ids = cur.Keys.ToList();
                _session.BroadcastToGuests(Protocol.Build(Msg.StructureState, w =>
                {
                    w.Write((byte)1);
                    w.Write(ids.Count);
                    foreach (var id in ids) w.Write(id);
                }));
            }
        }

        /// <summary>Einträge in Stücken senden (Steam: höchstens 512 KB je Nachricht).</summary>
        private void SendEntries(List<Entry> list, List<string> removed, CSteamID to)
        {
            int i = 0;
            bool first = true;
            do
            {
                var chunk = new List<Entry>();
                int bytes = 0;
                while (i < list.Count && chunk.Count < 400 && bytes < 150000)
                {
                    chunk.Add(list[i]);
                    bytes += 120 + (list[i].Json?.Length ?? 0) * 2;
                    i++;
                }
                var rem = first ? removed : new List<string>();
                first = false;
                var msg = Protocol.Build(Msg.StructureState, w =>
                {
                    w.Write((byte)0);
                    w.Write(chunk.Count);
                    foreach (var e in chunk) e.Write(w);
                    w.Write(rem.Count);
                    foreach (var id in rem) w.Write(id);
                });
                if (to == CSteamID.Nil) _session.BroadcastToGuests(msg);
                else _session.SendTo(to, msg);
            } while (i < list.Count);
        }

        public void HostOnRequest(Peer from, BinaryReader r)
        {
            var op = (Op)r.ReadByte();
            string id = r.ReadString();
            var kind = (Kind)r.ReadByte();
            string draftId = r.ReadString();
            int n = r.ReadInt32();
            if (n < 0 || n > 5000)
                throw new InvalidDataException("zu viele Stücke");
            var segs = new List<Seg>(n);
            for (int i = 0; i < n; i++) segs.Add(new Seg { Pos = ReadV(r), Rot = ReadV(r), Scale = ReadV(r) });
            try
            {
                if (op == Op.NeedJson)
                {
                    HostSendJson(from, id.Split('\n'));
                    return;
                }
                string text;
                if (op == Op.Place)
                {
                    var draft = DraftById(draftId);
                    if (draft == null)
                    {
                        Plugin.Log.LogWarning($"IFZCOOP struct-Wunsch Place: Bauplan {draftId} unbekannt");
                        return;
                    }
                    string placed = HostPlace(draft, segs, out string why);
                    if (placed != null)
                        _session.Stats?.Add(from.Id.m_SteamID, StatsSync.Stat.Built);
                    if (placed == null)
                    {
                        text = $"{from.Name}: {KindName(KindOf(draft.PlaceableObjectType))} hier nicht möglich ({why})";
                        Plugin.Log.LogInfo($"IFZCOOP struct Place {draftId} abgelehnt: {why} (von {from.Name})");
                    }
                    else
                    {
                        text = $"{from.Name}: {KindName(KindOf(draft.PlaceableObjectType))} geplant";
                        Plugin.Log.LogInfo($"IFZCOOP struct Place {draftId} -> {placed} (von {from.Name})");
                    }
                }
                else
                {
                    var s = Index().TryGetValue(id, out var x) ? x : null;
                    if (s == null)
                    {
                        Plugin.Log.LogWarning($"IFZCOOP struct-Wunsch {op}: Anlage {id} gibt es beim Host nicht");
                        return;
                    }
                    StatsSync.As(from.Id.m_SteamID, () => HostDo(s, op));
                    text = $"{from.Name}: {KindName(KindOf(s))} – {Describe(op)}";
                    Plugin.Log.LogInfo($"IFZCOOP struct {op} {id} -> {StateOf(s)} (von {from.Name})");
                }
                RequestsDone++;
                _nextSend = 0f;
                _session.ShowNotice(text);
                _session.BroadcastToGuests(Protocol.Build(Msg.Notice, w => w.Write(text)));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"IFZCOOP struct-Wunsch {op} {id}: {e}");
            }
        }

        private void HostSendJson(Peer from, IEnumerable<string> ids)
        {
            var idx = Index();
            var list = new List<Entry>();
            foreach (var id in ids.Distinct())
            {
                if (!idx.TryGetValue(id, out var s)) continue;
                var e = Make(s);
                e.Json = MakeJson(s, e.Kind);
                list.Add(e);
            }
            if (list.Count > 0)
                SendEntries(list, new List<string>(), from.Id);
        }

        private static string Describe(Op op)
        {
            switch (op)
            {
                case Op.CancelBuild: return "Bau abgebrochen";
                case Op.Deconstruct: return "Abriss gestartet";
                case Op.CancelDeconstruct: return "Abriss abgebrochen";
                case Op.Repair: return "Reparatur gestartet";
                case Op.CancelRepair: return "Reparatur abgebrochen";
                case Op.RemoveRuin: return "Ruine entfernt";
                default: return op.ToString();
            }
        }

        /// <summary>Aktion wie die Oberfläche des Spiels (Fenster der Anlage) ausführen.</summary>
        private static void HostDo(PlaceableStructure s, Op op)
        {
            switch (op)
            {
                case Op.CancelBuild:
                    if (StateOf(s) == StructureStateType.Constructing && s.BuildStructureWork != null)
                        s.BuildStructureWork.CancelWork();
                    break;
                case Op.Deconstruct:
                    StartDeconstructionQuiet(s);
                    s.PrintLogWithStructureAction("Deconstruction started");
                    break;
                case Op.CancelDeconstruct:
                    s.DeconstructWork?.DeleteWork();
                    break;
                case Op.Repair:
                    s.StartRepairing();
                    break;
                case Op.CancelRepair:
                    s.RepairWork?.DeleteWork();
                    break;
                case Op.RemoveRuin:
                    if (s.StateMachine.HasState(StructureStateType.Abandoned)) s.StateMachine.ChangeTo(StructureStateType.Abandoned);
                    else s.StateMachine.ChangeTo(StructureStateType.Empty);
                    break;
            }
        }

        /// <summary>Wie Structure.StartDeconstruction, aber ohne das Fenster zu öffnen.</summary>
        private static void StartDeconstructionQuiet(Structure s)
        {
            if (s.StateMachine.IsDeconstructing)
                return;
            s.StateMachine.ChangeTo(StructureStateType.Deconstructing);
            s.DeconstructWork?.StartWork();
        }

        /// <summary>Vorschau-Objekt anlegen wie der Bau-Cursor (Controller-Methode des Spiels).</summary>
        private static PlaceableObject CreatePreview(Kind k, PlaceableObjectDraft draft)
        {
            var p = Poc;
            switch (k)
            {
                case Kind.Tower: return (PlaceableObject)CreateTowerMethod.Invoke(p.TowersController, new object[] { draft });
                case Kind.Gate: return (PlaceableObject)CreateGateMethod.Invoke(p.GatesController, new object[] { draft });
                case Kind.Farm: return p.FarmlandsController.CreateStructure(draft);
                case Kind.Minefield: return p.MinefieldController.CreateStructure(draft);
                case Kind.Fisherman: return p.FishermanStructureController.CreateStructure(draft);
                case Kind.ClayPit: return p.ClayPitStructureController.CreateStructure(draft);
                default: return null;
            }
        }

        /// <summary>Platzieren beim Host wie beim Loslassen des Bau-Cursors. Liefert die Kennung oder null.</summary>
        private string HostPlace(PlaceableObjectDraft draft, List<Seg> segs, out string why)
        {
            why = "";
            var k = KindOf(draft.PlaceableObjectType);
            if (segs.Count == 0) { why = "keine Lage"; return null; }
            if (k == Kind.Wall)
                return HostPlaceWalls(draft, segs);
            var obj = CreatePreview(k, draft);
            if (obj == null) { why = "Art " + draft.PlaceableObjectType; Rejected++; return null; }
            var seg = segs[0];
            obj.SetPlacingState();
            if (seg.Scale.sqrMagnitude > 0.0001f) obj.transform.localScale = seg.Scale;
            obj.SetRotation(seg.Rot.y);
            obj.SetPosition(seg.Pos);
            SyncPhysics();
            if (!IsValidSpot(obj, out var reason))
            {
                why = reason;
                obj.OnCursorReleased(placeObject: false);
                Rejected++;
                return null;
            }
            obj.OnCursorReleased(placeObject: true);
            if (obj == null || obj.IsGameObjectDestroyed || StateOf(obj) == StructureStateType.Placing)
            {
                why = "nicht gesetzt";
                Rejected++;
                return null;
            }
            if (string.IsNullOrEmpty(((PlaceableStructure)obj).Id)) ((PlaceableStructure)obj).SetId(obj.GetId());
            LastId = obj.GetId();
            _nextSend = 0f;
            return LastId;
        }

        /// <summary>Mauerstücke wie WallConstructor.End(build: true): Fabrik, Größe, StartBuilding, Übersichts-Marker.</summary>
        private string HostPlaceWalls(PlaceableObjectDraft draft, List<Seg> segs)
        {
            var factory = (WallFactory)WallFactoryField.GetValue(Poc.WallsController);
            string last = null;
            foreach (var seg in segs)
            {
                var wall = factory.Create(draft.Prefab, seg.Pos, Quaternion.Euler(seg.Rot), false);
                if (seg.Scale.sqrMagnitude > 0.0001f) wall.transform.localScale = seg.Scale;
                wall.RefreshIconHolderScale();
                wall.UpdateSegment();
                wall.StartBuilding(draft);
                wall.CreateOverviewMarker();
                if (string.IsNullOrEmpty(wall.Id)) wall.SetId(wall.GetId());
                last = wall.GetId();
            }
            if (last != null) LastId = last;
            _nextSend = 0f;
            return last;
        }

        // ================================================================ Gast: Wünsche

        private bool GuestCanAsk => _session.Role == Role.Guest && _session.GuestControlsLocked && !UnitSync.NetApplying;

        private void Send(Op op, string id, Kind kind, string draftId, List<Seg> segs)
        {
            _session.SendToHost(Protocol.Build(Msg.StructureRequest, w =>
            {
                w.Write((byte)op);
                w.Write(id ?? "");
                w.Write((byte)kind);
                w.Write(draftId ?? "");
                w.Write(segs?.Count ?? 0);
                if (segs != null)
                    foreach (var s in segs) { WriteV(w, s.Pos); WriteV(w, s.Rot); WriteV(w, s.Scale); }
            }));
            if (op != Op.NeedJson) RequestsSent++;
        }

        private static Seg SegOf(Transform t) => new Seg { Pos = t.position, Rot = t.eulerAngles, Scale = t.localScale };

        /// <summary>Prefix X.OnCursorReleased(placeObject): true = beim Gast abgefangen (Aufrufer setzt placeObject = false,
        /// das Spiel verwirft dann die Vorschau wie beim Abbrechen).</summary>
        public bool GuestPlace(PlaceableObject obj)
        {
            if (_session.Role != Role.Guest && !UnitSync.NetApplying && obj != null && obj.Draft != null && KindOf(obj.Draft.PlaceableObjectType) != Kind.None)
                _session.Stats?.AddForActor(StatsSync.Stat.Built);
            if (!GuestCanAsk || obj == null || obj.Draft == null)
                return false;
            var k = KindOf(obj.Draft.PlaceableObjectType);
            if (k == Kind.None || k == Kind.Wall)
                return false;
            // Felder/Minen prüfen selbst beim Loslassen; ungültige verwirft das Spiel ohnehin
            if (obj is AreaStructure && !IsValidSpot(obj, out _))
                return true;
            Send(Op.Place, "", k, obj.Draft.Id, new List<Seg> { SegOf(obj.transform) });
            _session.ShowNotice($"An den Host geschickt: {KindName(k)} bauen");
            return true;
        }

        /// <summary>Prefix WallConstructor.End(build, immediately): true = abgefangen (Aufrufer setzt build = false).</summary>
        public bool GuestWallEnd(WallConstructor c, bool immediately)
        {
            if (_session.Role != Role.Guest && !UnitSync.NetApplying && c != null && !immediately)
                _session.Stats?.AddForActor(StatsSync.Stat.Built);
            if (!GuestCanAsk || c == null || immediately)
                return false;
            try
            {
                if ((bool)CtorGeneratedField.GetValue(c))
                    return false;
                var walls = (List<Wall>)CtorWallsField.GetValue(c);
                var draft = (PlaceableObjectDraft)CtorDraftField.GetValue(c);
                if (walls == null || walls.Count == 0 || draft == null)
                    return false;
                var segs = walls.Where(x => x != null).Select(x => SegOf(x.transform)).ToList();
                Send(Op.Place, "", Kind.Wall, draft.Id, segs);
                _session.ShowNotice($"An den Host geschickt: Mauer bauen ({segs.Count} Stücke)");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Mauer-Wunsch: " + e.Message);
                return false;
            }
        }

        /// <summary>Prefix Abriss/Reparatur an einer Anlage. false = beim Gast nicht selbst ausführen.</summary>
        public bool GuestStructAction(Structure s, Op op)
        {
            if (_session.Role != Role.Guest && !UnitSync.NetApplying && s is PlaceableStructure)
                _session.Stats?.AddForActor(op == Op.Repair ? StatsSync.Stat.Repaired : StatsSync.Stat.Demolished);
            if (!GuestCanAsk || !(s is PlaceableStructure ps) || KindOf(ps) == Kind.None)
                return true;
            Send(op, ps.GetId(), KindOf(ps), ps.Draft?.Id, null);
            _session.ShowNotice($"An den Host geschickt: {KindName(KindOf(ps))} – {Describe(op)}");
            return false;
        }

        /// <summary>Prefix der Abbrechen-Knöpfe in den Fenstern einer Anlage (Bau, Reparatur, Abriss, Ruine).</summary>
        public bool GuestPanelCancel(UI.InfoPanels.SpecificPanelInfo panel)
        {
            if (!GuestCanAsk || panel == null)
                return true;
            if (!(panel.Structure is PlaceableStructure ps) || KindOf(ps) == Kind.None)
                return true;
            Op op;
            if (panel is UI.InfoPanels.StructureInProgressPanelInfo) op = Op.CancelBuild;
            else if (panel is UI.InfoPanels.StructureInRepairPanelInfo) op = Op.CancelRepair;
            else if (panel is UI.InfoPanels.RuinedStructurePanelInfo) op = Op.RemoveRuin;
            else if (panel is UI.InfoPanels.WorksPanels.DemolishWorkPanel) op = Op.CancelDeconstruct;
            else return true;
            Send(op, ps.GetId(), KindOf(ps), ps.Draft?.Id, null);
            _session.ShowNotice($"An den Host geschickt: {KindName(KindOf(ps))} – {Describe(op)}");
            if (op != Op.CancelRepair)
            {
                try { panel.ClosePanel(); } catch { }
            }
            return false;
        }

        /// <summary>Prefix Structure.OnConstructionDone: beim Gast meldet der Host, wann eine Anlage fertig ist.</summary>
        public bool AllowConstructionDone(Structure s)
        {
            if (!(s is PlaceableStructure) || s.IsGenerated || UnitSync.NetApplying || _session.Role != Role.Guest || !_session.GuestControlsLocked)
                return true;
            BlockedDone++;
            return false;
        }

        // ================================================================ Gast: Stand

        public void GuestOnState(BinaryReader r)
        {
            byte mode = r.ReadByte();
            if (mode == 1)
            {
                int n = r.ReadInt32();
                if (n < 0 || n > 200000) throw new InvalidDataException("zu viele Anlagen");
                var ids = new HashSet<string>();
                for (int i = 0; i < n; i++) ids.Add(r.ReadString());
                GuestRoster(ids);
                return;
            }
            int ne = r.ReadInt32();
            if (ne < 0 || ne > 100000) throw new InvalidDataException("zu viele Anlagen");
            var list = new List<Entry>(ne);
            for (int i = 0; i < ne; i++) list.Add(Entry.Read(r));
            int nr = r.ReadInt32();
            if (nr < 0 || nr > 100000) throw new InvalidDataException("zu viele entfernte Anlagen");
            var removed = new List<string>(nr);
            for (int i = 0; i < nr; i++) removed.Add(r.ReadString());

            if (Poc == null)
                return;
            UnitSync.NetApplying = true;
            try
            {
                var idx = Index();
                foreach (var e in list)
                {
                    try { ApplyOne(idx, e); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"Anlage {e.Id} ({e.Kind} {(StructureStateType)e.State}): {ex.Message}"); }
                }
                foreach (var id in removed)
                {
                    _need.Remove(id);
                    if (idx.TryGetValue(id, out var s))
                    {
                        try { RemoveLocal(s); }
                        catch (Exception ex) { Plugin.Log.LogWarning($"Anlage {id} entfernen: {ex.Message}"); }
                    }
                }
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
            AskForMissing();
        }

        private void GuestRoster(HashSet<string> ids)
        {
            if (Poc == null)
                return;
            UnitSync.NetApplying = true;
            try
            {
                var idx = Index();
                foreach (var kv in idx)
                {
                    if (ids.Contains(kv.Key)) continue;
                    try { RemoveLocal(kv.Value); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"Anlage {kv.Key} entfernen: {ex.Message}"); }
                }
                _need.RemoveWhere(id => !ids.Contains(id));
                foreach (var id in ids)
                    if (!idx.ContainsKey(id)) _need.Add(id);
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
            AskForMissing();
        }

        private void AskForMissing()
        {
            if (_need.Count == 0)
                return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextNeed)
                return;
            _nextNeed = now + NeedInterval;
            var ids = _need.Take(64).ToList();
            Send(Op.NeedJson, string.Join("\n", ids), Kind.None, "", null);
            NeedSent++;
        }

        private void ApplyOne(Dictionary<string, PlaceableStructure> idx, Entry e)
        {
            if (!idx.TryGetValue(e.Id, out var s) || s == null || s.IsGameObjectDestroyed)
            {
                var want = (StructureStateType)e.State;
                if (want == StructureStateType.Empty || want == StructureStateType.None || want == StructureStateType.Placing)
                    return;
                if (string.IsNullOrEmpty(e.Json))
                {
                    _need.Add(e.Id);
                    return;
                }
                s = Create(e);
                if (s == null)
                {
                    CreateFailed++;
                    return;
                }
                idx[e.Id] = s;
                _need.Remove(e.Id);
                Created++;
                LastId = e.Id;
                Plugin.Log.LogInfo($"IFZCOOP struct angelegt {e.Id} {e.Kind} {e.Draft} {(StructureStateType)e.State}");
            }
            Apply(s, e);
        }

        /// <summary>Anlage so anlegen, wie das Spiel sie aus dem Spielstand lädt (Controller.Load mit einem Ausschnitt).</summary>
        private static PlaceableStructure Create(Entry e)
        {
            bool defensible = e.Kind == Kind.Tower || e.Kind == Kind.Wall || e.Kind == Kind.Gate;
            Type serType = e.Kind == Kind.Minefield ? typeof(SerializedMinefield)
                : defensible ? typeof(SerializedDefensibleStructure) : typeof(SerializedPlaceableStructure);
            var ser = (SerializedPlaceableStructure)JsonConvert.DeserializeObject(e.Json, serType, GameJson.DefaultSettings);
            if (ser == null || ser.SerializedMapObject == null)
                throw new InvalidDataException("leerer Spielstand-Eintrag");

            // Laden kann nur Bau, fertig, Ruine (und Gruben: erschöpft); den Rest stellt Apply danach ein
            var st = ser.StructureStateType;
            bool generic = e.Kind == Kind.Farm || e.Kind == Kind.Minefield || e.Kind == Kind.Fisherman || e.Kind == Kind.ClayPit;
            StructureStateType load;
            switch (st)
            {
                case StructureStateType.Constructing:
                case StructureStateType.Completed:
                case StructureStateType.Ruined:
                    load = st; break;
                case StructureStateType.Depleted:
                    load = generic ? st : StructureStateType.Completed; break;
                case StructureStateType.Repairing:
                    load = ser.PreviousStructureStateType == StructureStateType.Ruined ? StructureStateType.Ruined : StructureStateType.Completed; break;
                case StructureStateType.None:
                case StructureStateType.Empty:
                case StructureStateType.Placing:
                case StructureStateType.Abandoned:
                case StructureStateType.Demolishing:
                case StructureStateType.Rubble:
                    return null;
                default:
                    load = StructureStateType.Completed; break;
            }
            if (load != st)
                StateTypeSetter.Invoke(ser, new object[] { load });

            var save = global::SaveHandler.SaveData;
            var p = Poc;
            Task t;
            switch (e.Kind)
            {
                case Kind.Tower:
                {
                    var c = new global::TowersControllerSerialized();
                    SetList(c, "Towers", new List<SerializedDefensibleStructure> { (SerializedDefensibleStructure)ser });
                    var old = save.TowersControllerSerialized;
                    save.TowersControllerSerialized = c;
                    try { t = p.TowersController.Load(); } finally { save.TowersControllerSerialized = old; }
                    break;
                }
                case Kind.Wall:
                {
                    var c = new global::WallsControllerSerialized();
                    SetList(c, "Walls", new List<SerializedDefensibleStructure> { (SerializedDefensibleStructure)ser });
                    var old = save.WallsControllerSerialized;
                    save.WallsControllerSerialized = c;
                    try { t = p.WallsController.Load(); } finally { save.WallsControllerSerialized = old; }
                    break;
                }
                case Kind.Gate:
                {
                    var c = new global::GatesControllerSerialized();
                    SetList(c, "Gates", new List<SerializedDefensibleStructure> { (SerializedDefensibleStructure)ser });
                    var old = save.GatesControllerSerialized;
                    save.GatesControllerSerialized = c;
                    try { t = p.GatesController.Load(); } finally { save.GatesControllerSerialized = old; }
                    break;
                }
                case Kind.Farm:
                {
                    var c = new global::FarmlandsControllerSerialized();
                    SetList(c, "Farmlands", new List<SerializedPlaceableStructure> { ser });
                    var old = save.FarmlandsControllerSerialized;
                    save.FarmlandsControllerSerialized = c;
                    try { t = p.FarmlandsController.Load(); } finally { save.FarmlandsControllerSerialized = old; }
                    break;
                }
                case Kind.Minefield:
                {
                    var c = new global::MinefieldControllerSerialized();
                    SetList(c, "Minefields", new List<SerializedMinefield> { (SerializedMinefield)ser });
                    var old = save.MinefieldControllerSerialized;
                    save.MinefieldControllerSerialized = c;
                    try { t = p.MinefieldController.Load(); } finally { save.MinefieldControllerSerialized = old; }
                    break;
                }
                case Kind.Fisherman:
                {
                    var c = new global::FishermanStructureControllerSerialized();
                    SetList(c, "Fishermans", new List<SerializedPlaceableStructure> { ser });
                    var old = save.FishermanStructureControllerSerialized;
                    save.FishermanStructureControllerSerialized = c;
                    try { t = p.FishermanStructureController.Load(); } finally { save.FishermanStructureControllerSerialized = old; }
                    break;
                }
                case Kind.ClayPit:
                {
                    var c = new global::StructuresControllerSerialized<ClayPit>();
                    SetList(c, "Structures", new List<SerializedPlaceableStructure> { ser });
                    var old = save.ClayPitStructureControllerSerialized;
                    save.ClayPitStructureControllerSerialized = c;
                    try { t = p.ClayPitStructureController.Load(); } finally { save.ClayPitStructureControllerSerialized = old; }
                    break;
                }
                default:
                    return null;
            }
            if (t != null && t.IsFaulted)
                throw t.Exception?.GetBaseException() ?? new InvalidOperationException("Laden gescheitert");

            var s = Index().TryGetValue(e.Id, out var x) ? x : null;
            if (s == null)
                return null;
            // Genau die Host-Lage (Schlüssel der Arbeiten in WorkSync hängen an der Position)
            var tr = s.transform;
            if ((tr.position - e.Pos).sqrMagnitude > 1e-6f)
                tr.position = e.Pos;
            if (s is Wall w)
            {
                if (e.Scale.sqrMagnitude > 0.0001f) tr.localScale = e.Scale;
                w.UpdateSegment();
                w.RefreshIconHolderScale();
            }
            return s;
        }

        private static void SetList(object target, string property, object list)
        {
            var setter = AccessTools.PropertySetter(target.GetType(), property);
            if (setter == null)
                throw new MissingMemberException(target.GetType().Name, property);
            setter.Invoke(target, new[] { list });
        }

        private void Apply(PlaceableStructure s, Entry e)
        {
            var want = (StructureStateType)e.State;
            var have = StateOf(s);
            if (want != have)
            {
                Plugin.Log.LogInfo($"IFZCOOP struct {e.Id} {e.Kind} {have} -> {want}");
                if (!Transition(s, have, want))
                    return; // entfernt
                have = StateOf(s);
            }
            if (have == StructureStateType.Constructing && want == StructureStateType.Constructing)
                ApplyBuildProgress(s, e);
            else if (have == StructureStateType.Deconstructing && want == StructureStateType.Deconstructing && s.DeconstructWork != null)
            {
                var dw = s.DeconstructWork;
                if (Mathf.Abs(dw.TimeDeconstructing - e.TimeCons) > 0.01f) dw.SetTimeDeconstructing(e.TimeCons);
                if (Mathf.Abs(dw.Progress - e.Progress) > 0.001f) dw.SetProgress(e.Progress);
            }
            if (e.Claimed && !s.IsClaimedByPlayer)
                s.MarkAsClaimedByPlayer();
            if (s.Health != null && Mathf.Abs(s.Health.Hp - e.Hp) > 0.5f)
                s.Health.SetHp(e.Hp);
            if (s is Wall w)
            {
                var tr = w.transform;
                if ((tr.position - e.Pos).sqrMagnitude > 0.0001f || Mathf.Abs(tr.localScale.x - e.Scale.x) > 0.001f)
                {
                    // Ein Tor hat die Mauer gekürzt (WallsResizer) – beim Gast gleich
                    tr.position = e.Pos;
                    if (e.Scale.sqrMagnitude > 0.0001f) tr.localScale = e.Scale;
                    w.UpdateSegment();
                    w.RefreshIconHolderScale();
                }
            }
            Applied++;
        }

        /// <summary>Zustand wechseln wie das Spiel. false, wenn die Anlage dabei entfernt wurde.</summary>
        private bool Transition(PlaceableStructure s, StructureStateType have, StructureStateType want)
        {
            var sm = s.StateMachine;
            switch (want)
            {
                case StructureStateType.Completed:
                    if (have == StructureStateType.Constructing)
                        FinishBuild(s);
                    else if (have == StructureStateType.Deconstructing)
                        s.DeconstructWork?.DeleteWork();
                    if (StateOf(s) != StructureStateType.Completed && sm.HasState(want))
                        sm.ChangeTo(want);
                    break;
                case StructureStateType.Ruined:
                    if (have == StructureStateType.Deconstructing)
                        s.DeconstructWork?.DeleteWork();
                    if (StateOf(s) != StructureStateType.Ruined)
                        s.SetRuinedState();
                    break;
                case StructureStateType.Repairing:
                    if (have == StructureStateType.Ruined || have == StructureStateType.Completed)
                        s.StartRepairing();
                    else if (sm.HasState(want))
                        sm.ChangeTo(want);
                    break;
                case StructureStateType.Deconstructing:
                    StartDeconstructionQuiet(s);
                    break;
                case StructureStateType.Constructing:
                    if (have == StructureStateType.Deconstructing)
                        s.DeconstructWork?.DeleteWork();
                    if (StateOf(s) != StructureStateType.Constructing && sm.HasState(want))
                        sm.ChangeTo(want);
                    break;
                case StructureStateType.None:
                case StructureStateType.Empty:
                case StructureStateType.Placing:
                case StructureStateType.Abandoned:
                    RemoveLocal(s);
                    return false;
                default:
                    if (sm.HasState(want))
                        sm.ChangeTo(want);
                    break;
            }
            return true;
        }

        /// <summary>Baustelle fertig wie BuildStructureWork.OnDone → Structure.OnConstructionDone.</summary>
        private void FinishBuild(PlaceableStructure s)
        {
            var w = s.BuildStructureWork;
            if (w != null && !w.IsDone)
                s.BuildImmediately();
            if (StateOf(s) == StructureStateType.Constructing && OnConstructionDoneMethod != null)
                OnConstructionDoneMethod.Invoke(s, new object[] { w });
            Finished++;
        }

        private static void ApplyBuildProgress(PlaceableStructure s, Entry e)
        {
            var w = s.BuildStructureWork;
            if (w == null || w.IsDone)
                return;
            if (e.TimeTo > 0f && Mathf.Abs(w.TimeToConstruct - e.TimeTo) > 0.01f)
                w.SetTimeToConstruct(e.TimeTo);
            if (TimeConstructingSetter != null && Mathf.Abs(w.TimeConstructing - e.TimeCons) > 0.001f)
                TimeConstructingSetter.Invoke(w, new object[] { e.TimeCons });
            if (Mathf.Abs(w.Progress - e.Progress) > 0.0005f)
                w.SetProgress(e.Progress);
        }

        /// <summary>Anlage beim Gast entfernen (Host hat sie nicht mehr).</summary>
        private void RemoveLocal(PlaceableStructure s)
        {
            if (s == null || s.IsGameObjectDestroyed)
                return;
            string id = s.GetId();
            var st = StateOf(s);
            // Baustelle: wie Abbrechen (PlaceableStructureConstructingState zerstört das Objekt)
            if (st == StructureStateType.Constructing && s.StateMachine.HasState(StructureStateType.Empty))
            {
                try { s.StateMachine.ChangeTo(StructureStateType.Empty); }
                catch (Exception e) { Plugin.Log.LogDebug($"Anlage {id} leeren: {e.Message}"); }
            }
            if (s != null && !s.IsGameObjectDestroyed)
            {
                var icon = s.StructureIcon;
                if (icon != null && !icon.transform.IsChildOf(s.transform))
                    UnityEngine.Object.Destroy(icon.gameObject);
                UnityEngine.Object.Destroy(s.gameObject);
            }
            Removed++;
            Plugin.Log.LogInfo($"IFZCOOP struct entfernt {id} ({st})");
        }

        // ================================================================ Testbefehle (QaDriver "struct …")

        private static readonly string[] PreferredDrafts =
            { "bld_wooden_tower", "bld_wooden_palisade", "bld_wooden_gate", "bld_farm", "bld_minefield", "bld_fisherman", "bld_clay_pit" };

        private static PlaceableObjectDraft ResolveDraft(string what)
        {
            var all = BuildingsDatas.Instance.GetPlaceableObjectTypes();
            var exact = all.FirstOrDefault(d => d != null && string.Equals(d.Id, what, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;
            PlaceableObjectType type;
            switch ((what ?? "").ToLowerInvariant())
            {
                case "tower": type = PlaceableObjectType.Tower; break;
                case "wall": type = PlaceableObjectType.Wall; break;
                case "gate": type = PlaceableObjectType.Gate; break;
                case "farm": type = PlaceableObjectType.Farm; break;
                case "minefield": type = PlaceableObjectType.Minefield; break;
                case "fisherman": type = PlaceableObjectType.FishermanStructure; break;
                case "claypit": type = PlaceableObjectType.ClayPitStructure; break;
                default: return null;
            }
            var ofType = all.Where(d => d != null && d.PlaceableObjectType == type).ToList();
            return ofType.FirstOrDefault(d => PreferredDrafts.Contains(d.Id)) ?? ofType.FirstOrDefault();
        }

        private static PlaceableStructure Select(string sel, Vector3 hq)
        {
            var idx = Index();
            if (string.IsNullOrEmpty(sel) || sel == "last")
                return idx.TryGetValue(LastId ?? "", out var l) ? l : null;
            Kind only = Kind.None;
            switch (sel)
            {
                case "tower": only = Kind.Tower; break;
                case "wall": only = Kind.Wall; break;
                case "gate": only = Kind.Gate; break;
                case "farm": only = Kind.Farm; break;
                case "minefield": only = Kind.Minefield; break;
                case "fisherman": only = Kind.Fisherman; break;
                case "claypit": only = Kind.ClayPit; break;
            }
            if (sel == "near" || only != Kind.None)
            {
                // nächste selbst gebaute Anlage (der Art) beim HQ
                PlaceableStructure best = null;
                float bd = float.MaxValue;
                foreach (var s in idx.Values)
                {
                    if (s.IsGenerated || (only != Kind.None && KindOf(s) != only)) continue;
                    float d = (s.transform.position - hq).sqrMagnitude;
                    if (d < bd) { bd = d; best = s; }
                }
                return best;
            }
            return idx.TryGetValue(sel, out var x) ? x : null;
        }

        /// <summary>
        /// "struct drafts" – Baupläne auflisten; "struct place &lt;Bauplan|tower|wall|gate|farm|minefield|fisherman|claypit&gt; [Abstand]" –
        /// an freier Stelle beim HQ wie der Bau-Cursor platzieren (Gast: geht als Wunsch an den Host);
        /// "struct done|cancel|deconstruct|canceldeconstruct|repair|cancelrepair|removeruin|ruin &lt;id|last|near|tower|wall|gate|farm|…&gt; (Art = nächste dieser Art beim HQ)".
        /// </summary>
        public string Qa(string[] a, Vector3 hq)
        {
            string sub = a.Length > 1 ? a[1].ToLowerInvariant() : "drafts";
            if (Poc == null)
                return "kein Spiel";
            switch (sub)
            {
                case "drafts":
                    return string.Join(" ", BuildingsDatas.Instance.GetPlaceableObjectTypes()
                        .Where(d => d != null && KindOf(d.PlaceableObjectType) != Kind.None)
                        .Select(d => d.Id + ":" + d.PlaceableObjectType));
                case "place":
                {
                    var draft = ResolveDraft(a.Length > 2 ? a[2] : "tower");
                    if (draft == null) return "Bauplan unbekannt (struct drafts)";
                    float dist = a.Length > 3 ? float.Parse(a[3], CultureInfo.InvariantCulture) : 20f;
                    return KindOf(draft.PlaceableObjectType) == Kind.Wall ? QaWall(draft, hq, dist) : QaPlace(draft, hq, dist);
                }
                default:
                {
                    var s = Select(a.Length > 2 ? a[2] : "last", hq);
                    if (s == null) return "keine Anlage " + (a.Length > 2 ? a[2] : "last (" + LastId + ")");
                    string id = s.GetId();
                    bool guest = _session.Role == Role.Guest;
                    switch (sub)
                    {
                        case "done":
                            if (s.BuildStructureWork != null && !s.BuildStructureWork.IsDone) s.BuildImmediately();
                            break;
                        case "ruin":
                            s.DestroyStructure();
                            break;
                        case "deconstruct":
                            // der Weg des Knopfs (NewStructurePanelInfo.OnDeconstructClicked → Structure.StartDeconstruction)
                            AccessTools.Method(typeof(Structure), "StartDeconstruction").Invoke(s, null);
                            break;
                        case "repair":
                            s.StartRepairing();
                            break;
                        case "cancel":
                        case "canceldeconstruct":
                        case "cancelrepair":
                        case "removeruin":
                        {
                            var op = sub == "cancel" ? Op.CancelBuild : sub == "canceldeconstruct" ? Op.CancelDeconstruct
                                : sub == "cancelrepair" ? Op.CancelRepair : Op.RemoveRuin;
                            if (guest && GuestCanAsk) Send(op, id, KindOf(s), s.Draft?.Id, null);
                            else HostDo(s, op);
                            break;
                        }
                        default:
                            return "unbekannt: " + sub;
                    }
                    return $"{sub} {id} -> {(s == null || s.IsGameObjectDestroyed ? "weg" : StateOf(s).ToString())}";
                }
            }
        }

        private static IEnumerable<Vector3> Candidates(Vector3 hq, float dist)
        {
            for (float r = dist; r < dist + 90f; r += 6f)
                for (int k = 0; k < 12; k++)
                {
                    float ang = k * 30f * Mathf.Deg2Rad;
                    yield return new Vector3(hq.x + Mathf.Cos(ang) * r, hq.y, hq.z + Mathf.Sin(ang) * r).SampleHeight();
                }
        }

        private string QaPlace(PlaceableObjectDraft draft, Vector3 hq, float dist)
        {
            var k = KindOf(draft.PlaceableObjectType);
            var obj = CreatePreview(k, draft);
            if (obj == null) return "kein Vorschau-Objekt für " + draft.Id;
            obj.SetPlacingState();
            bool found = false;
            Vector3 at = Vector3.zero;
            string lastReason = "";
            foreach (var p in Candidates(hq, dist))
            {
                obj.SetPosition(p);
                SyncPhysics();
                if (IsValidSpot(obj, out var reason)) { found = true; at = p; break; }
                lastReason = reason;
            }
            if (!found)
            {
                obj.OnCursorReleased(placeObject: false);
                return "keine freie Stelle für " + draft.Id + " (" + lastReason + ")";
            }
            // derselbe Aufruf wie PlaceObjectsCursor.OnReleasedPrimary; beim Gast fängt der Prefix ihn ab
            obj.OnCursorReleased(placeObject: true);
            string where = at.x.ToString("0", CultureInfo.InvariantCulture) + "," + at.z.ToString("0", CultureInfo.InvariantCulture);
            if (_session.Role == Role.Guest)
                return $"Wunsch {draft.Id} bei {where} (requestsSent={RequestsSent})";
            if (obj == null || obj.IsGameObjectDestroyed || StateOf(obj) == StructureStateType.Placing)
                return "nicht platziert " + draft.Id;
            if (string.IsNullOrEmpty(((PlaceableStructure)obj).Id)) ((PlaceableStructure)obj).SetId(obj.GetId());
            LastId = obj.GetId();
            _nextSend = 0f;
            return $"platziert {draft.Id} {LastId} bei {where} -> {StateOf(obj)}";
        }

        private string QaWall(PlaceableObjectDraft draft, Vector3 hq, float dist)
        {
            var c = (WallConstructor)CursorConstructorField.GetValue(Poc.WallsController.WallCursor);
            if (c == null) return "kein WallConstructor";
            foreach (var p in Candidates(hq, dist))
            {
                var dir = (p - hq);
                dir.y = 0f;
                var side = new Vector3(-dir.z, 0f, dir.x).normalized * 10f;
                c.Start(draft, isGenerated: false);
                CtorSetOrigin.Invoke(c, new object[] { new Wall.SnapInfo(p, null) });
                CtorUpdate.Invoke(c, new object[] { new Wall.SnapInfo(p + side, null) });
                SyncPhysics();
                CtorUpdate.Invoke(c, new object[] { new Wall.SnapInfo(p + side, null) });
                SyncPhysics();
                if (!c.IsValid())
                {
                    c.End(build: false);
                    continue;
                }
                int n = c.Walls.Count;
                c.End(build: true); // beim Gast fängt der Prefix ab und schickt die Stücke an den Host
                string where = p.x.ToString("0", CultureInfo.InvariantCulture) + "," + p.z.ToString("0", CultureInfo.InvariantCulture);
                if (_session.Role == Role.Guest)
                    return $"Wunsch Mauer {draft.Id} {n} Stücke bei {where} (requestsSent={RequestsSent})";
                var last = Poc.WallsController.Walls.LastOrDefault();
                if (last != null)
                {
                    if (string.IsNullOrEmpty(last.Id)) last.SetId(last.GetId());
                    LastId = last.GetId();
                }
                _nextSend = 0f;
                return $"Mauer {draft.Id} {n} Stücke bei {where}, letzte {LastId}";
            }
            return "keine freie Stelle für Mauer " + draft.Id;
        }

        // ================================================================ Status

        /// <summary>Für Statusdatei und Prüfsumme: Zahl je Art/Zustand und Prüfsumme über Kennung, Art, Bauplan, Zustand, Lage.</summary>
        public static IEnumerable<string> StatusLines()
        {
            if (Poc == null)
            {
                yield return "structs none";
                yield break;
            }
            var list = Index().Values.Where(s => KindOf(s) != Kind.None).OrderBy(s => s.GetId(), StringComparer.Ordinal).ToList();
            uint hash = 2166136261;
            foreach (var s in list)
            {
                var p = s.transform.position;
                string line = s.GetId() + ":" + (int)KindOf(s) + ":" + s.Draft?.Id + ":" + (int)StateOf(s) + ":" + Mathf.RoundToInt(p.x) + "," + Mathf.RoundToInt(p.z);
                foreach (char ch in line) hash = (hash ^ ch) * 16777619;
            }
            var kinds = list.GroupBy(KindOf).OrderBy(g => g.Key).Select(g => g.Key + "=" + g.Count());
            var states = list.GroupBy(StateOf).OrderBy(g => g.Key).Select(g => g.Key + "=" + g.Count());
            yield return $"structs n={list.Count} {string.Join(" ", kinds)} | {string.Join(" ", states)} hash={hash:x8}";
            int shown = 0;
            foreach (var s in list.Where(x => !x.IsGenerated))
            {
                if (++shown > 15) break;
                var p = s.transform.position;
                float prog = StateOf(s) == StructureStateType.Constructing && s.BuildStructureWork != null ? s.BuildStructureWork.Progress
                    : StateOf(s) == StructureStateType.Deconstructing && s.DeconstructWork != null ? s.DeconstructWork.Progress : 0f;
                yield return $"struct {s.GetId()} {KindOf(s)} {s.Draft?.Id} {StateOf(s)} prog={prog.ToString("0.00", CultureInfo.InvariantCulture)} hp={(s.Health != null ? s.Health.Hp : 0f).ToString("0", CultureInfo.InvariantCulture)} pos={p.x.ToString("0.0", CultureInfo.InvariantCulture)},{p.z.ToString("0.0", CultureInfo.InvariantCulture)}";
            }
        }

        public string StatusLine() =>
            $"structApplied={Applied} created={Created} createFailed={CreateFailed} removed={Removed} finished={Finished} blockedDone={BlockedDone} need={_need.Count} needSent={NeedSent} requestsSent={RequestsSent} requestsDone={RequestsDone} rejected={Rejected} last={LastId}";
    }
}
