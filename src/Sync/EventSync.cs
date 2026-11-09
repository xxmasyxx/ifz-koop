using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Controllers;
using EventsSystem;
using EventsSystem.Missions;
using EventsSystem.Objectives;
using GameInput;
using Gameplay.GameResources;
using Gameplay.ObjectOnMap;
using Gameplay.Rebuilding;
using Gameplay.Units;
using Gameplay.Vehicles;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using Newtonsoft.Json;
using Serialization.SaveLoaders;
using UnityEngine;
using GameJson = Utilities.JsonSerializer;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Ereignissystem (Analyse W2, P5, P8, P9; Paket F). Beim Gast laufen keine eigenen Ereignisse mehr
    /// (EventsSystemController.Update, RunAction, ForceEventRun, StartMission, EndMission und Objective.Complete gesperrt,
    /// UnitPatches). Die Wirkung von Ereignis-Aktionen kommt über die anderen Abgleiche (Gruppen, Fahrzeuge, Kisten, Lager, Funk).
    ///
    /// Host: 1×/s Unterschrift über aktive Missionen mit Zielen (erledigt, Zähler), Ziel-Marker, freigeschaltete Inhalte und
    /// das Kapitel; bei Änderung der ganze Stand. Alle 2 s die Restzeiten der Missionen.
    /// Gast: gleicht ab. Fehlende Missionen lädt er wie beim Spielstand (EventsSavedLoader.LoadActiveMissions, ohne Start-Aktionen),
    /// überzählige beendet er (Ergebnis aus der Liste erledigter/gescheiterter Missionen des Hosts, End-Aktionen laufen nicht, weil
    /// RunAction gesperrt ist), Ziele hakt er ab und setzt die Zähler. Marker legt er wie beim Laden an (SpawnMarker), überzählige weg.
    ///
    /// Marker (Runde 3, „markers“): Beim Gast lief der Auslöser-Teil des Ereignissystems weiter (EventTriggerManager:
    /// RunEventByTrigger → HandleMarkerSpawn legt Marker an, HandleTriggerEventWithMarker fährt die Kamera hin und löscht
    /// Marker mit removeMarkerAfterActivate). Weil ForceEventRun beim Gast gesperrt ist, bleiben dort alle Ereignisse ewig im
    /// Pool und feuern immer wieder. Dazu löschen/verstecken Ziele, Gebäude, Ressourcen und Funk Marker lokal. Folge: Marker
    /// tauchten beim Gast auf und verschwanden mit dem nächsten Host-Stand (bzw. der Prüfsummen-Reparatur), oder ein Host-Marker
    /// war beim Gast weg und kam nie wieder (der Host schickt nur bei Änderung).
    /// Jetzt: Auslöser beim Gast aus; Marker anlegen, löschen, ein-/ausblenden beim Gast nur, wenn es vom Host kommt
    /// (NetApplying) oder beim Laden. Der Gast merkt sich die Host-Liste und stellt sie alle 2 s wieder her (fehlend, zu viel,
    /// Sichtbarkeit, Lage, Ziel-Objekt). Die Prüfsumme vergleicht keine Missions-Restzeiten mehr (liefen immer auseinander).
    /// </summary>
    internal sealed class EventSync
    {
        private const float SendInterval = 1f;
        private const float TimesInterval = 2f;
        private const float HealInterval = 2f;
        private const float MoveTolerance = 3f;

        private sealed class State
        {
            public List<string> Missions = new List<string>();   // MissionSerializedData-JSON
            public List<string> Markers = new List<string>();    // EventMarkerSerializedData-JSON
            public List<bool> MarkerVisible = new List<bool>();
            public List<string> Unlocked = new List<string>();
            public List<string> Completed = new List<string>();
            public List<string> Failed = new List<string>();
            public string Chapter = "";
        }

        private sealed class HostMarker
        {
            public EventMarkerSerializedData Data;
            public bool Visible;
        }

        private readonly Session _session;
        private string _sentSig;
        private float _nextSend;
        private float _nextTimes;

        // Gast: letzte Marker-Liste des Hosts (null = noch keine)
        private Dictionary<string, HostMarker> _hostMarkers;
        private float _nextHeal;
        private int _healLogs;
        private readonly HashSet<string> _spawnFailLogged = new HashSet<string>();

        public int Applied { get; private set; }
        public int MissionsStarted { get; private set; }
        public int MissionsEnded { get; private set; }
        public int ObjectivesDone { get; private set; }
        public int MarkersSpawned { get; private set; }
        public int MarkersRemoved { get; private set; }
        public int ContentChanged { get; private set; }
        public int Healed { get; private set; }
        public int MarkersMoved { get; private set; }
        public int ClickablesFixed { get; private set; }

        // Gast-Sperren (statisch, weil die Harmony-Haken statisch sind)
        public static int SpawnBlocked { get; private set; }
        public static int DestroyBlocked { get; private set; }
        public static int VisibilityBlocked { get; private set; }
        public static int TriggersBlocked { get; private set; }
        private static int _blockLogs;

        public EventSync(Session session)
        {
            _session = session;
        }

        public void ResendAll()
        {
            _sentSig = null;
            _nextTimes = 0f;
        }

        public void Reset()
        {
            ResendAll();
            _hostMarkers = null;
            _spawnFailLogged.Clear();
        }

        private static EventsSystemController Controller => GameAccess.Game<EventsSystemController>();

        private static IEnumerable<EventMarker> Markers(EventsSystemController c)
        {
            var objs = c?.MarkersController?.Objects;
            if (objs == null)
                yield break;
            for (int i = 0; i < objs.Count; i++)
                if (objs[i] is EventMarker m && m != null)
                    yield return m;
        }

        private static string R(float f) => Mathf.RoundToInt(f * 100f).ToString(CultureInfo.InvariantCulture);

        // ================================================================ Host

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend || _session.ActiveGuestCount == 0 || !GameAccess.IsGameStarted)
                return;
            _nextSend = now + SendInterval;
            var c = Controller;
            if (c == null)
                return;

            string sig;
            try { sig = Signature(c); }
            catch (Exception e) { Plugin.Log.LogDebug("Ereignis-Unterschrift: " + e.Message); return; }
            if (sig != _sentSig)
            {
                _sentSig = sig;
                SendState(c);
            }
            if (now >= _nextTimes && c.ActiveMissions.Count > 0)
            {
                _nextTimes = now + TimesInterval;
                var list = c.ActiveMissions.Where(m => m != null && m.TimeToResolve > 0f).ToList();
                if (list.Count > 0)
                    _session.BroadcastToGuests(Protocol.Build(Msg.MissionTimes, w =>
                    {
                        w.Write(list.Count);
                        foreach (var m in list)
                        {
                            w.Write(m.Id);
                            w.Write(m.TimeToResolve);
                        }
                    }));
            }
        }

        private static string Signature(EventsSystemController c)
        {
            var sb = new StringBuilder();
            foreach (var m in c.ActiveMissions)
            {
                if (m == null) continue;
                sb.Append('M').Append(m.Id).Append(':');
                foreach (var oe in m.ObjectiveExpressions)
                {
                    var data = new ObjectiveSerializedData(oe.objective);
                    sb.Append(data.Completed ? '1' : '0').Append(R(data.CurrentValue)).Append(',');
                }
            }
            foreach (var mk in Markers(c))
                sb.Append('K').Append(mk.Id).Append(mk.Visible ? '1' : '0').Append(mk.Clickable != null ? "c" : "")
                  .Append(Mathf.RoundToInt(mk.Position.x)).Append(',').Append(Mathf.RoundToInt(mk.Position.z));
            var content = SaveHandler.SaveData?.ContentStateController?.GetContentStates();
            if (content != null)
            {
                sb.Append('C');
                foreach (var cs in content) sb.Append(cs.Unlocked ? '1' : '0');
            }
            var loader = GameAccess.Game<EventsSavedLoader>();
            sb.Append('H').Append(loader?.ActiveChapterId ?? "");
            var ev = SaveHandler.SaveData?.EventsSystemSerializedData;
            if (ev != null)
                sb.Append('E').Append(ev._missionsCompleted?.Count ?? 0).Append('/').Append(ev._missionsFailed?.Count ?? 0);
            return sb.ToString();
        }

        private void SendState(EventsSystemController c)
        {
            var st = new State();
            foreach (var m in c.ActiveMissions)
            {
                if (m == null) continue;
                try { st.Missions.Add(JsonConvert.SerializeObject(new MissionSerializedData(m), GameJson.DefaultSettings)); }
                catch (Exception e) { Plugin.Log.LogWarning($"Mission {m.Id} lässt sich nicht übertragen: {e.Message}"); }
            }
            foreach (var mk in Markers(c))
            {
                try
                {
                    st.Markers.Add(JsonConvert.SerializeObject(new EventMarkerSerializedData(mk), GameJson.DefaultSettings));
                    st.MarkerVisible.Add(mk.Visible);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"Marker {mk.Id} lässt sich nicht übertragen: {e.Message}"); }
            }
            var content = SaveHandler.SaveData?.ContentStateController?.GetContentStates();
            if (content != null)
                foreach (var cs in content)
                    if (cs.Unlocked) st.Unlocked.Add(cs.Id);
            var ev = SaveHandler.SaveData?.EventsSystemSerializedData;
            if (ev != null)
            {
                if (ev._missionsCompleted != null) st.Completed.AddRange(ev._missionsCompleted);
                if (ev._missionsFailed != null) st.Failed.AddRange(ev._missionsFailed);
            }
            st.Chapter = GameAccess.Game<EventsSavedLoader>()?.ActiveChapterId ?? "";

            _session.BroadcastToGuests(Protocol.Build(Msg.EventState, w =>
            {
                WriteList(w, st.Missions);
                w.Write(st.Markers.Count);
                for (int i = 0; i < st.Markers.Count; i++)
                {
                    w.Write(st.Markers[i]);
                    w.Write(st.MarkerVisible[i]);
                }
                WriteList(w, st.Unlocked);
                WriteList(w, st.Completed);
                WriteList(w, st.Failed);
                w.Write(st.Chapter);
            }));
        }

        private static void WriteList(BinaryWriter w, List<string> list)
        {
            w.Write(list.Count);
            foreach (var s in list) w.Write(s ?? "");
        }

        private static List<string> ReadList(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 100000)
                throw new InvalidDataException("Liste zu groß");
            var list = new List<string>(n);
            for (int i = 0; i < n; i++) list.Add(r.ReadString());
            return list;
        }

        // ================================================================ Gast

        public void GuestOnTimes(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 10000)
                return;
            var c = Controller;
            for (int i = 0; i < n; i++)
            {
                string id = r.ReadString();
                float t = r.ReadSingle();
                var m = c?.ActiveMissions.FirstOrDefault(x => x != null && x.Id == id);
                if (m != null)
                    m.TimeToResolve = t;
            }
        }

        public void GuestOnState(BinaryReader r)
        {
            var st = new State { Missions = ReadList(r) };
            int k = r.ReadInt32();
            if (k < 0 || k > 100000)
                throw new InvalidDataException("zu viele Marker");
            for (int i = 0; i < k; i++)
            {
                st.Markers.Add(r.ReadString());
                st.MarkerVisible.Add(r.ReadBoolean());
            }
            st.Unlocked = ReadList(r);
            st.Completed = ReadList(r);
            st.Failed = ReadList(r);
            st.Chapter = r.ReadString();

            _hostMarkers = ParseMarkers(st);

            var c = Controller;
            if (c == null)
                return;
            UnitSync.NetApplying = true;
            try
            {
                Step("Inhalte", () => ApplyContent(st));
                // Marker vor den Missionen: neue Ziele (LoadActiveMissions) suchen ihre Marker per ID.
                Step("Marker", () => ApplyMarkers(c, _hostMarkers));
                Step("Missionen", () => ApplyMissions(c, st));
                // Ende/Abschluss einer Mission kann Marker gelöscht haben, die der Host noch hat.
                Step("Marker", () => ApplyMarkers(c, _hostMarkers));
                Step("Kapitel", () => ApplyChapter(c, st));
                Applied++;
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
            _nextHeal = Time.realtimeSinceStartup + HealInterval;
        }

        /// <summary>Gast, jedes Bild: alle 2 s die eigene Marker-Liste mit der letzten Host-Liste vergleichen und reparieren.</summary>
        public void GuestFrame()
        {
            if (_hostMarkers == null || !_session.GuestControlsLocked || !GameAccess.IsGameStarted)
                return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextHeal)
                return;
            _nextHeal = now + HealInterval;
            var c = Controller;
            if (c?.MarkersController == null)
                return;
            string why;
            try { why = MarkerMismatch(c, _hostMarkers); }
            catch (Exception e) { Plugin.Log.LogDebug("Marker-Vergleich: " + e.Message); return; }
            if (why == null)
                return;
            UnitSync.NetApplying = true;
            try
            {
                ApplyMarkers(c, _hostMarkers);
                Healed++;
                if (_healLogs++ < 50)
                    Plugin.Log.LogInfo("IFZCOOP marker heal: " + why);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Marker-Reparatur: " + e.Message); }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        private static void Step(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { Plugin.Log.LogWarning($"Ereignis-Abgleich ({what}): {e}"); }
        }

        private void ApplyContent(State st)
        {
            var csc = SaveHandler.SaveData?.ContentStateController;
            var list = csc?.GetContentStates();
            if (list == null)
                return;
            var want = new HashSet<string>(st.Unlocked);
            foreach (var cs in list.ToList())
            {
                bool should = want.Contains(cs.Id);
                if (cs.Unlocked == should)
                    continue;
                if (should) csc.UnlockContentById(cs.Id);
                else csc.LockContentById(cs.Id);
                ContentChanged++;
                Plugin.Log.LogInfo($"IFZCOOP content {(should ? "unlock" : "lock")} {cs.Id}");
            }
        }

        private void ApplyMissions(EventsSystemController c, State st)
        {
            var host = new List<MissionSerializedData>();
            foreach (var json in st.Missions)
            {
                try
                {
                    var d = JsonConvert.DeserializeObject<MissionSerializedData>(json, GameJson.DefaultSettings);
                    if (d != null && !string.IsNullOrEmpty(d.MissionId)) host.Add(d);
                }
                catch (Exception e) { Plugin.Log.LogWarning("Mission nicht lesbar: " + e.Message); }
            }
            var hostIds = new HashSet<string>(host.Select(h => h.MissionId));
            var completed = new HashSet<string>(st.Completed);
            var failed = new HashSet<string>(st.Failed);

            // beim Gast zu viel: beenden
            foreach (var m in c.ActiveMissions.Where(m => m != null && !hostIds.Contains(m.Id)).ToList())
            {
                var result = completed.Contains(m.Id) ? MissionResult.Success : failed.Contains(m.Id) ? MissionResult.Fail : MissionResult.Undecided;
                Plugin.Log.LogInfo($"IFZCOOP mission end {m.Id} {result}");
                // Ziele, die der Gast per Complete abgehakt hat, hängen noch an Signalen/Ereignissen (CollectResource,
                // AdaptBuilding, …). EndMission löst sie nur bei Fail/Undecided, Dispose entsorgt sie -> NullReference.
                if (result == MissionResult.Success)
                {
                    try { m.UnassignNotCompletedEventsInObjective(); }
                    catch (Exception ex) { Plugin.Log.LogDebug($"Ziele von {m.Id} lösen: {ex.Message}"); }
                }
                c.EndMission(m, result);
                MissionsEnded++;
            }

            var loader = GameAccess.Game<EventsSavedLoader>();
            foreach (var d in host)
            {
                var m = c.ActiveMissions.FirstOrDefault(x => x != null && x.Id == d.MissionId);
                if (m == null)
                {
                    if (loader == null)
                        continue;
                    Plugin.Log.LogInfo($"IFZCOOP mission start {d.MissionId}");
                    loader.LoadActiveMissions(new[] { d });
                    MissionsStarted++;
                    continue;
                }
                if (d.TimeToResolve > 0f)
                    m.TimeToResolve = d.TimeToResolve;
                foreach (var oe in m.ObjectiveExpressions.ToList())
                {
                    var o = oe.objective;
                    var hs = d.ObjectivesStates.FirstOrDefault(x => x.Id == oe.objectiveId);
                    if (o == null || hs == null)
                        continue;
                    if (!o.Completed)
                    {
                        SetValue(o, hs.CurrentValue);
                        o.UpdateDescription();
                    }
                    if (hs.Completed && !o.Completed)
                    {
                        Plugin.Log.LogInfo($"IFZCOOP objective done {d.MissionId}/{oe.objectiveId}");
                        o.Complete();
                        ObjectivesDone++;
                        // Complete kann die Mission beenden, dann ist die Liste der Ziele hinfällig
                        if (!c.ActiveMissions.Contains(m))
                            break;
                    }
                }
            }
        }

        private static void SetValue(Objective objective, float value)
        {
            Objective.InvokeActionDependOnType(objective.ObjectiveType,
                () => objective.buildingObjectiveData.buildingCountToFinishState = (int)value,
                () => objective.hordeObjectiveData.killedHordesCount = (int)value,
                () => objective.resourceObjectiveData.requiredResource.state = value,
                () => objective.buildingObjectiveData.peopleToAccommodateState = (int)value,
                () => objective.killedCharacterCount = (int)value);
        }

        private static Dictionary<string, HostMarker> ParseMarkers(State st)
        {
            var host = new Dictionary<string, HostMarker>();
            for (int i = 0; i < st.Markers.Count; i++)
            {
                try
                {
                    var d = JsonConvert.DeserializeObject<EventMarkerSerializedData>(st.Markers[i], GameJson.DefaultSettings);
                    if (d != null && !string.IsNullOrEmpty(d.Id) && !host.ContainsKey(d.Id))
                        host[d.Id] = new HostMarker { Data = d, Visible = st.MarkerVisible[i] };
                }
                catch (Exception e) { Plugin.Log.LogWarning("Marker nicht lesbar: " + e.Message); }
            }
            return host;
        }

        /// <summary>Marker, die an einer Gruppe hängen (Group.AttachMarker), wandern mit ihr – deren Lage nicht vergleichen.</summary>
        private static bool FollowsGroup(EventMarker m)
        {
            var parent = m.ParentTransform != null ? m.ParentTransform.parent : null;
            return parent != null && parent.GetComponentInParent<Group>() != null;
        }

        private static bool NeedsMove(EventMarker m, EventMarkerSerializedData d)
        {
            if (d.PlaceTypeToSpawn == SpawnSystem.SpawnSettings.PlaceTypeToSpawn.SpecificGroup || FollowsGroup(m))
                return false;
            float dx = m.Position.x - d.Position.x, dz = m.Position.z - d.Position.z;
            return dx * dx + dz * dz > MoveTolerance * MoveTolerance;
        }

        /// <summary>null = Gast stimmt mit der Host-Liste überein, sonst der erste Unterschied.</summary>
        private static string MarkerMismatch(EventsSystemController c, Dictionary<string, HostMarker> host)
        {
            var local = new Dictionary<string, EventMarker>();
            foreach (var m in Markers(c))
            {
                if (!host.ContainsKey(m.Id))
                    return "zu viel " + m.Id;
                if (local.ContainsKey(m.Id))
                    return "doppelt " + m.Id;
                local[m.Id] = m;
            }
            foreach (var kv in host)
            {
                if (!local.TryGetValue(kv.Key, out var m))
                    return "fehlt " + kv.Key;
                if (m.Visible != kv.Value.Visible)
                    return "Sichtbarkeit " + kv.Key;
                if (NeedsMove(m, kv.Value.Data))
                    return "Lage " + kv.Key;
                if (m.Clickable == null && ResolveClickable(kv.Value.Data) != null)
                    return "Ziel " + kv.Key;
            }
            return null;
        }

        private void ApplyMarkers(EventsSystemController c, Dictionary<string, HostMarker> host)
        {
            var mc = c.MarkersController;
            if (mc == null || host == null)
                return;

            // zu viel oder doppelt: weg (ForceDestroy, weil der Host ihn ja schon losgeworden ist, auch wenn blockDestroyMarker)
            var seen = new HashSet<string>();
            foreach (var m in Markers(c).ToList())
            {
                if (host.ContainsKey(m.Id) && seen.Add(m.Id))
                    continue;
                Plugin.Log.LogInfo($"IFZCOOP marker remove {m.Id}");
                m.ForceDestroy();
                MarkersRemoved++;
            }

            foreach (var kv in host)
            {
                var d = kv.Value.Data;
                var m = mc.GetMarkerById(d.Id);
                if (m == null)
                {
                    var clickable = ResolveClickable(d);
                    try
                    {
                        m = mc.SpawnMarker(d.Position, d.Id, d.Hidden, clickable, d.PlaceTypeToSpawn, d.MarkerAdditionalParameters, d.MissionId, d.SpawnSettings);
                    }
                    catch (Exception e)
                    {
                        if (_spawnFailLogged.Add(d.Id))
                            Plugin.Log.LogWarning($"Marker {d.Id} lässt sich nicht anlegen: {e.Message}");
                        continue;
                    }
                    if (m == null)
                        continue;
                    c.SetMarkerParentExactPlaceId(m, d.PlaceTypeToSpawn, d.ExactPlaceId);
                    MarkersSpawned++;
                    Plugin.Log.LogInfo($"IFZCOOP marker spawn {d.Id} {d.Position.x:0},{d.Position.z:0}");
                }
                else
                {
                    if (NeedsMove(m, d))
                    {
                        var delta = d.Position - m.Position;
                        m.ParentTransform.position += new Vector3(delta.x, 0f, delta.z);
                        MarkersMoved++;
                        Plugin.Log.LogInfo($"IFZCOOP marker move {d.Id} -> {d.Position.x:0},{d.Position.z:0}");
                    }
                    if (m.Clickable == null)
                    {
                        // Ziel (z. B. ein Fahrzeug von VehicleSync) war beim Anlegen noch nicht da
                        var clickable = ResolveClickable(d);
                        if (clickable != null)
                        {
                            m.SetClickable(clickable);
                            ClickablesFixed++;
                        }
                    }
                }
                if (m.Visible != kv.Value.Visible)
                    m.SetVisible(kv.Value.Visible);
            }
        }

        private static IClickable ResolveClickable(EventMarkerSerializedData d)
        {
            if (d.ClickableType == null || string.IsNullOrEmpty(d.ClickableId))
                return null;
            if (d.ClickableType.IsSubclassOf(typeof(Structure)))
                return GameAccess.Game<PlaceableObjectsController>()?.GetStructureByIdAndType(d.ClickableId, d.ClickableType);
            if (d.ClickableType == typeof(Vehicle))
                return GameAccess.Game<VehiclesController>()?.GetVehicleById(d.ClickableId);
            if (d.ClickableType == typeof(Resource))
            {
                var rc = GameAccess.Game<ResourcesController>();
                if (rc != null && rc.Resources.TryGetValue(d.ClickableId, out var res))
                    return res;
            }
            return null;
        }

        private void ApplyChapter(EventsSystemController c, State st)
        {
            if (string.IsNullOrEmpty(st.Chapter))
                return;
            var loader = GameAccess.Game<EventsSavedLoader>();
            if (loader != null && loader.ActiveChapterId == st.Chapter)
                return;
            Plugin.Log.LogInfo($"IFZCOOP chapter {st.Chapter}");
            c.CreateNewChapterPanel(st.Chapter);
        }

        // ================================================================ Gast-Sperren (Harmony)

        /// <summary>Gast im Koop-Spiel, Spiel läuft, nicht beim Laden und nicht gerade ein Host-Stand.</summary>
        private static bool GuestLocal
        {
            get
            {
                var s = Plugin.Instance?.Session;
                return s != null && s.GuestControlsLocked && !UnitSync.NetApplying && GameAccess.IsGameStarted && !SaveHandler.LoadingData;
            }
        }

        private static bool GuestAlways
        {
            get
            {
                var s = Plugin.Instance?.Session;
                return s != null && s.GuestControlsLocked;
            }
        }

        private static void LogBlock(string what)
        {
            if (_blockLogs++ < 40)
                Plugin.Log.LogInfo("IFZCOOP marker beim Gast verhindert (macht der Host): " + what);
        }

        // MarkersController.SpawnMarker(...) – Prefix
        private static bool SpawnMarkerPrefix(string id, ref EventMarker __result)
        {
            if (!GuestLocal)
                return true;
            SpawnBlocked++;
            LogBlock("anlegen " + id);
            __result = null;
            return false;
        }

        // EventMarker.ForceDestroy() – Prefix (Destroy() ruft ForceDestroy)
        private static bool ForceDestroyPrefix(EventMarker __instance)
        {
            if (!GuestLocal || __instance == null)
                return true;
            // hängt an einer Gruppe, die gerade verschwindet: mitgehen lassen, sonst bliebe ein Marker ohne Halt zurück
            try { if (FollowsGroup(__instance)) return true; } catch { }
            DestroyBlocked++;
            LogBlock("löschen " + __instance.Id);
            return false;
        }

        // EventMarker.SetVisible(bool) – Prefix
        private static bool SetVisiblePrefix(EventMarker __instance, bool state)
        {
            if (!GuestLocal || __instance == null || __instance.Visible == state)
                return true;
            VisibilityBlocked++;
            LogBlock((state ? "einblenden " : "ausblenden ") + __instance.Id);
            return false;
        }

        // EventTriggerManager.RunEventByTrigger / HandleTriggerEventWithMarker – beim Gast nie (Pool des Gastes ist veraltet)
        private static bool TriggerPrefix()
        {
            if (!GuestAlways)
                return true;
            TriggersBlocked++;
            return false;
        }

        public static void ApplyPatches(Harmony harmony)
        {
            int ok = 0, missing = 0;
            void P(Type type, string method, string prefix)
            {
                try
                {
                    var target = AccessTools.Method(type, method);
                    if (target == null) { missing++; Plugin.Log.LogWarning($"IFZCOOP marker hook fehlt: {type.Name}.{method}"); return; }
                    harmony.Patch(target, prefix: new HarmonyMethod(typeof(EventSync), prefix));
                    ok++;
                }
                catch (Exception e) { missing++; Plugin.Log.LogWarning($"IFZCOOP marker hook {type.Name}.{method} nicht gesetzt: {e.Message}"); }
            }
            P(typeof(MarkersController), "SpawnMarker", nameof(SpawnMarkerPrefix));
            P(typeof(EventMarker), "ForceDestroy", nameof(ForceDestroyPrefix));
            P(typeof(EventMarker), "SetVisible", nameof(SetVisiblePrefix));
            P(typeof(EventTriggerManager), "RunEventByTrigger", nameof(TriggerPrefix));
            P(typeof(EventTriggerManager), "HandleTriggerEventWithMarker", nameof(TriggerPrefix));
            P(typeof(global::UI.DistancePointer.DistancePointerCanvas), "Update", nameof(PointerCanvasPrefix));
            Plugin.Log.LogInfo($"Marker-Abgleich: {ok} Haken gesetzt, {missing} fehlen");
        }

        private static readonly AccessTools.FieldRef<global::UI.DistancePointer.DistancePointerCanvas, global::Controllers.DistancePointerController> PointerCtrlField =
            AccessTools.FieldRefAccess<global::UI.DistancePointer.DistancePointerCanvas, global::Controllers.DistancePointerController>("_distancePointerController");

        public static int PointersPruned { get; private set; }

        /// <summary>
        /// DistancePointerCanvas.Update – Prefix: Pfeile am Bildschirmrand zu Markern, deren Objekt schon zerstört ist, aus der Liste
        /// nehmen. Verschwindet ein Marker ohne ForceDestroy (z. B. mit seinem Haus oder seiner Gruppe), bleibt sein Pfeil in
        /// DistancePointerController.PointerAims, und das Spiel wirft beim Gast jedes Bild eine NullReferenceException
        /// (Player.log: DistancePointerController.CountAimDirection, Berichte 2026-11-09).
        /// </summary>
        private static void PointerCanvasPrefix(global::UI.DistancePointer.DistancePointerCanvas __instance)
        {
            try
            {
                var list = PointerCtrlField(__instance)?.PointerAims;
                if (list == null)
                    return;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var p = list[i];
                    if (p != null && p.cachedTransform != null && p.cachedGameObject != null)
                        continue;
                    list.RemoveAt(i);
                    if (PointersPruned++ < 5)
                        Plugin.Log.LogInfo("IFZCOOP Randpfeil ohne Marker entfernt");
                }
            }
            catch { }
        }

        // ================================================================ QA

        private static readonly AccessTools.FieldRef<MarkersController, EventTriggerManager> TriggerManagerField =
            AccessTools.FieldRefAccess<MarkersController, EventTriggerManager>("_eventTriggerManager");

        /// <summary>
        /// evmark house (Host: Marker auf das nächste Haus beim HQ wie eine Story-Aktion, Kamera hin) |
        /// evmark local | kill | hide | arrive (Gast: lokal anlegen / löschen / ausblenden / Auslöser „Trupp am Marker“ – muss verhindert werden) |
        /// evmark lose (Gast: ersten Marker wie von außen verlieren, Reparatur muss ihn zurückholen) | evmark off (Host: QA-Hausmarker weg)
        /// </summary>
        public string Qa(string[] a, Vector3 hqPos)
        {
            var esc = Controller;
            var mc = esc?.MarkersController;
            if (mc == null)
                return "kein MarkersController";
            string sub = a.Length > 1 ? a[1] : "house";
            var all = Markers(esc).ToList();
            var first = all.FirstOrDefault(m => m.Id.StartsWith("qa_", StringComparison.Ordinal)) ?? all.FirstOrDefault();
            switch (sub)
            {
                case "house":
                {
                    var bc = GameAccess.Game<BuildingsController>();
                    Gameplay.Buildings.Building best = null;
                    float bestD = float.MaxValue;
                    foreach (var b in bc.Buildings)
                    {
                        if (b == null || b.IsHq || mc.IsBuildingHasMarker(b))
                            continue;
                        float dd = (b.transform.position - hqPos).sqrMagnitude;
                        if (dd < bestD) { bestD = dd; best = b; }
                    }
                    if (best == null)
                        return "kein Haus";
                    string id = "qa_house_" + UnityEngine.Random.Range(1000, 9999);
                    var mk = mc.SpawnMarker(best.RoofCenterPosition, id, false, best, SpawnSystem.SpawnSettings.PlaceTypeToSpawn.AbandonedBuilding, default);
                    if (mk == null)
                        return "kein Marker";
                    bool cam = esc.CenterCameraOnMarker(id);   // Story-Kamera (StorySync schickt das Signal)
                    return $"Marker {id} auf Haus {best.ID} cam={cam}";
                }
                case "off":
                {
                    int n = 0;
                    foreach (var m in all) if (m.Id.StartsWith("qa_house_", StringComparison.Ordinal)) { m.Destroy(); n++; }
                    return n + " Hausmarker weg";
                }
                case "local":
                {
                    var mk = mc.SpawnMarker(hqPos + new Vector3(25f, 0f, -25f), "qa_local_" + UnityEngine.Random.Range(1000, 9999), false, null,
                        SpawnSystem.SpawnSettings.PlaceTypeToSpawn.Object, default);
                    return mk != null ? "angelegt " + mk.Id : "verhindert";
                }
                case "kill":
                    if (first == null) return "kein Marker";
                    first.Destroy();
                    return (mc.GetMarkerById(first.Id) != null ? "verhindert, noch da " : "gelöscht ") + first.Id;
                case "hide":
                    if (first == null) return "kein Marker";
                    first.SetVisible(!first.Visible);
                    return "sichtbar=" + first.Visible + " " + first.Id;
                case "arrive":
                {
                    if (first == null) return "kein Marker";
                    var etm = TriggerManagerField(mc);
                    if (etm == null) return "kein EventTriggerManager";
                    etm.PlayerGroupArrivedToMarkerId(first.Id);
                    return (mc.GetMarkerById(first.Id) != null ? "noch da " : "weg ") + first.Id + " triggersBlocked=" + TriggersBlocked;
                }
                case "lose":
                {
                    if (first == null) return "kein Marker";
                    UnitSync.NetApplying = true;   // an der Sperre vorbei, wie ein Verlust von außen
                    try { first.ForceDestroy(); }
                    finally { UnitSync.NetApplying = false; }
                    return "verloren " + first.Id + " (Reparatur in ≤ 2 s erwartet)";
                }
            }
            return "evmark house|off|local|kill|hide|arrive|lose";
        }

        public string GuardLine() =>
            $"evguard spawnBlocked={SpawnBlocked} destroyBlocked={DestroyBlocked} visBlocked={VisibilityBlocked} triggersBlocked={TriggersBlocked} "
            + $"healed={Healed} moved={MarkersMoved} clickFixed={ClickablesFixed} hostMarkers={(_hostMarkers?.Count ?? -1)}";

        // ================================================================ Status

        /// <summary>Für die Statusdatei: Missionen mit Zielen, Marker, Zahl freigeschalteter Inhalte, Kapitel.</summary>
        public static IEnumerable<string> StatusLines()
        {
            var c = Controller;
            if (c == null)
                yield break;
            var inv = CultureInfo.InvariantCulture;
            foreach (var m in c.ActiveMissions.Where(x => x != null).OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                var parts = m.ObjectiveExpressions.Select(oe =>
                {
                    var d = new ObjectiveSerializedData(oe.objective);
                    return oe.objectiveId + "=" + (d.Completed ? "ok" : d.CurrentValue.ToString("0.##", inv));
                });
                yield return $"mission {m.Id} t={m.TimeToResolve.ToString("0.0", inv)} {string.Join(" ", parts)}";
            }
            var markers = Markers(c).OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
            yield return "markers=" + markers.Count + " " + string.Join(" ", markers.Select(x => x.Id + (x.Visible ? "" : "(v)")
                + "@" + x.Position.x.ToString("0", inv) + "," + x.Position.z.ToString("0", inv)));
            var content = SaveHandler.SaveData?.ContentStateController?.GetContentStates();
            int unlocked = content?.Count(x => x.Unlocked) ?? -1;
            uint hash = 2166136261;
            if (content != null)
                foreach (var cs in content.Where(x => x.Unlocked))
                    foreach (char ch in cs.Id) hash = (hash ^ ch) * 16777619;
            yield return $"content unlocked={unlocked} hash={hash:x8} chapter={GameAccess.Game<EventsSavedLoader>()?.ActiveChapterId}";
        }

        /// <summary>
        /// Für die Prüfsumme: wie StatusLines, aber ohne Missions-Restzeit (kommt beim Gast nur alle 2 s, lief also immer
        /// auseinander und löste ständig „events – Stand wird neu geschickt“ aus) und ohne Marker-Lage (Gruppen-Marker wandern).
        /// </summary>
        public static IEnumerable<string> ChecksumLines()
        {
            var c = Controller;
            if (c == null)
                yield break;
            foreach (var line in StatusLines())
            {
                if (line.StartsWith("mission ", StringComparison.Ordinal))
                {
                    int t = line.IndexOf(" t=", StringComparison.Ordinal);
                    int end = t < 0 ? -1 : line.IndexOf(' ', t + 3);
                    yield return t < 0 ? line : line.Substring(0, t) + (end < 0 ? "" : line.Substring(end));
                }
                else if (line.StartsWith("markers=", StringComparison.Ordinal))
                {
                    var markers = Markers(c).OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
                    yield return "markers=" + markers.Count + " " + string.Join(" ", markers.Select(x => x.Id + (x.Visible ? "" : "(v)")));
                }
                else
                    yield return line;
            }
        }
    }
}
