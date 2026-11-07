using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Controllers;
using Controllers.Time;
using Convoy;
using Convoy.Locations;
using Core;
using Data.Building;
using Gameplay.Buildings;
using Gameplay.Immigrants;
using Gameplay.ObjectOnMap;
using Gameplay.Rebuilding;
using Gameplay.Rebuilding.StateMachine;
using Gameplay.Units;
using Gameplay.Units.Characters;
using Gameplay.Units.Enemy.Hideouts;
using Gameplay.Units.Virtual;
using Gameplay.Units.Workers.WorkSystem.AreaWorkSystem;
using Gameplay.Vehicles;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using Newtonsoft.Json;
using UnityEngine;
using ZenjectExtensions.Installers;
using GameJson = Utilities.JsonSerializer;

namespace IfzCoop.Sync
{
    /// <summary>
    /// NPC-Siedlungen, Umzug in eine andere Stadt und die Statistik am Spielende.
    ///
    /// 1. Siedlungen (NpcZoneController: Farmer, Neutrale, Banditen, Reiniger, verlassene Zone).
    ///    Gast: würfelt beim Entdecken nichts mehr selbst (OnNpcZoneStructureDetected, TryMeasurePlaceForFarms, Übernahme,
    ///    „feindlich werden“, Zurück zur Kartenauswahl sind gesperrt). Host: 1×/s eine Unterschrift über die Zone
    ///    (Art, aufgedeckt, übernommen, die Gebäude der Zone mit Bauplan und Zustand, Verstecke als HideoutSerializedInfo-JSON,
    ///    dieselbe Klasse wie im Spielstand). Bei Änderung und alle 10 s an die Gäste. Gast stellt nach, was das Spiel beim
    ///    Laden auch macht: Art, Flags, Liste der Zonen-Gebäude, MarkedAsPartOfZoneType, Umbau zum Siedlungsgebäude
    ///    (NpcZoneSpawner.AdaptNpcBuilding), Versteck aus dem JSON (HideoutsController.CreateHideout).
    ///    Gruppen kommen über UnitSync/StorySync, Felder über StructureSync, Gebäude-Zustände danach über BuildingSync.
    ///    Bleibt der Gast 30 s lang abweichend, holt er sich einmal den Spielstand neu. Die Übernahme der Siedlung
    ///    (Bürger, Rohstoffe, neues HQ) schickt beim Host nach 4 s einen frischen Spielstand an alle Gäste (wie HqSync beim Lager).
    ///
    /// 2. Umzug: Der Gast kann nicht umziehen (Expeditionszentrum, Umzugsfenster, Speichern vor dem Umzug, Konvoi-Start
    ///    gesperrt, Meldung „Nur der Host kann umziehen“). Verlässt der Host die Karte (Umzug, Hauptmenü, anderer Spielstand),
    ///    gehen die Gäste mit einer Meldung ins Hauptmenü und warten dort (Session.GuestAwaitHost). Ist der Host wieder im Spiel,
    ///    bekommen sie nach 4 s automatisch seinen neuen Spielstand (Session.ResyncAll) und sind wieder dabei.
    ///
    /// 3. Statistik: Tote (Infizierte, Banditen, eigene), gesammelte und gefundene Rohstoffe, aufgenommene Einwanderer
    ///    vom Host (bei Änderung höchstens alle 2 s, sonst alle 15 s, sofort beim Spielende). Der Gast setzt die Werte
    ///    vor jeder Anzeige des Statistik-Fensters und nach jedem eigenen Zählen eines Toten zurück auf die des Hosts.
    /// </summary>
    internal sealed class ZoneSync
    {
        private const float ZoneInterval = 1f;
        private const float ZoneForceInterval = 10f;
        private const float StatsInterval = 2f;
        private const float StatsForceInterval = 15f;
        private const float TakeoverResyncDelay = 4f;
        private const float ArriveDelay = 4f;
        private const float FallbackDelay = 30f;

        public const string MoveText = "Der Host zieht in eine andere Stadt um. Du wartest im Hauptmenü; nach der Ankunft kommt sein neuer Spielstand automatisch.";
        public const string LeftText = "Der Host hat die Karte verlassen (Hauptmenü oder anderer Spielstand). Du wartest im Hauptmenü, bis er wieder im Spiel ist.";
        public const string AnnounceText = "Der Host startet den Umzug in eine andere Stadt. Gleich geht es für dich ins Hauptmenü; nach der Ankunft bist du automatisch wieder dabei.";
        public const string BackText = "Der Host ist wieder im Spiel. Sein Spielstand kommt gleich.";
        public const string GuestNoMoveText = "Nur der Host kann umziehen. Bitte den Host, den Umzug im Expeditionszentrum zu starten.";

        private sealed class Item
        {
            public string Id = "";
            public string Type = "";
            public string Draft = "";
            public int State;
            public bool Found;
            public bool Hideout;
            public string Json = "";

            public string Sig => Id + ":" + Type + ":" + Draft + ":" + State + ":" + (Found ? 1 : 0) + (Hideout ? 1 : 0);
        }

        private sealed class Snap
        {
            public int Loc;
            public bool Revealed;
            public bool Taken;
            public readonly List<Item> Items = new List<Item>();

            public string Sig()
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(Loc).Append('|').Append(Revealed ? 1 : 0).Append(Taken ? 1 : 0);
                foreach (var it in Items) sb.Append('|').Append(it.Sig);
                return sb.ToString();
            }
        }

        private struct Stats
        {
            public int Inf, Raid, Cit, Imm;
            public float Gath, Found;

            public string Sig => Inf + "|" + Raid + "|" + Cit + "|" + Imm + "|" + Mathf.RoundToInt(Gath) + "|" + Mathf.RoundToInt(Found);
        }

        private readonly Session _session;

        // Host: Siedlung
        private string _zoneSentSig;
        private float _zoneNextSend;
        private float _zoneNextForce;
        private int _lastTaken = -1;
        private float _resyncAt = -1f;

        // Host: Statistik
        private string _statsSentSig;
        private float _statsNextSend;
        private float _statsNextForce;

        // Host: Karte verlassen / wieder da
        private bool _wasStarted;
        private float _startedAt = -1f;
        private bool _hostAway;

        // Gast
        private bool _haveStats;
        private Stats _hostStats;
        private bool _awaitHost;
        private float _nextAwaitCheck;
        private float _mismatchSince = -1f;
        private string _mismatchSig;
        private string _resyncAskedFor;

        public int ZoneSent { get; private set; }
        public int ZoneApplied { get; private set; }
        public int Adapted { get; private set; }
        public int HideoutsCreated { get; private set; }
        public int Mismatch { get; private set; }
        public int ResyncAsked { get; private set; }
        public int TakeoverResyncs { get; private set; }
        public int StatsSent { get; private set; }
        public int StatsApplied { get; private set; }
        public int MovesSent { get; private set; }
        public int Arrivals { get; private set; }
        public int MoveWaits { get; private set; }
        public static int RevealBlocked { get; private set; }
        public static int MoveBlocked { get; private set; }

        public ZoneSync(Session session)
        {
            _session = session;
        }

        public void ResendAll()
        {
            _zoneSentSig = null;
            _zoneNextSend = 0f;
            _statsSentSig = null;
            _statsNextSend = 0f;
        }

        public void Reset()
        {
            ResendAll();
            _lastTaken = -1;
            _resyncAt = -1f;
            _hostAway = false;
            _wasStarted = false;
            _startedAt = -1f;
            _haveStats = false;
            _awaitHost = false;
            _mismatchSince = -1f;
            _mismatchSig = null;
            _resyncAskedFor = null;
        }

        private static ZoneSync Me => Plugin.Instance?.Session?.Zones;
        private static bool IsGuest => Plugin.Instance?.Session?.Role == Role.Guest;
        private static bool IsHostWithGuests
        {
            get
            {
                var s = Plugin.Instance?.Session;
                return s != null && s.Role == Role.Host;
            }
        }

        // ================================================================ Spielobjekte

        private static NpcZoneController Zone()
        {
            var bc = GameAccess.Game<BuildingsController>();
            return bc?.NpcZoneController ?? GameAccess.Game<NpcZoneController>();
        }

        private static StructureStateType StateOf(Structure s) => s?.StateMachine?.CurrentState?.StateType ?? StructureStateType.None;

        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>();

        private static Type TypeOf(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            if (!TypeCache.TryGetValue(name, out var t))
            {
                t = AccessTools.TypeByName(name);
                TypeCache[name] = t;
            }
            return t;
        }

        private static PlaceableObjectDraft DraftById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            try { return BuildingsDatas.Instance.GetPlaceableObjectDraftById(id); }
            catch { return null; }
        }

        private static Structure Find(PlaceableObjectsController poc, string id, Type type)
        {
            if (poc == null || type == null || string.IsNullOrEmpty(id))
                return null;
            try { return poc.GetStructureByIdAndType(id, type); }
            catch { return null; }
        }

        private static readonly MethodInfo RevealedSetter = AccessTools.PropertySetter(typeof(NpcZoneController), "NpcZoneRevealed");
        private static readonly MethodInfo TakenSetter = AccessTools.PropertySetter(typeof(NpcZoneController), "NpcZoneTakenOver");
        private static readonly MethodInfo DetectedMethod = AccessTools.Method(typeof(NpcZoneController), "OnNpcZoneStructureDetected");
        private static readonly MethodInfo SpawnZoneMethod = AccessTools.Method(typeof(NpcZoneController), "TrySpawnZone", new[] { typeof(Vector3) });
        private static readonly MethodInfo SpawnFarmerZoneMethod = AccessTools.Method(typeof(NpcZoneController), "TrySpawnFarmerZone", new[] { typeof(Vector3) });

        // ================================================================ Host

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            HostWatchMap(now);

            if (_resyncAt > 0f && now >= _resyncAt)
            {
                _resyncAt = -1f;
                TakeoverResyncs++;
                Plugin.Log.LogInfo("IFZCOOP Siedlung übernommen, Gäste bekommen neuen Spielstand");
                _session.ResyncAll();
            }

            if (_session.ActiveGuestCount == 0 || !GameAccess.IsGameStarted)
                return;
            HostZone(now);
            HostStats(now, false);
        }

        /// <summary>Host verlässt die Karte bzw. ist wieder im Spiel (läuft jeden Frame, auch im Hauptmenü).</summary>
        private void HostWatchMap(float now)
        {
            bool started = GameAccess.IsGameStarted;
            if (_wasStarted && !started)
            {
                _lastTaken = -1;
                _zoneSentSig = null;
                _statsSentSig = null;
                _resyncAt = -1f;
                if (!_hostAway)
                    HostMapLeaving(LeftText);
            }
            if (started && !_wasStarted)
                _startedAt = now;
            _wasStarted = started;

            if (_hostAway && started && _startedAt > 0f && now - _startedAt > ArriveDelay)
            {
                _hostAway = false;
                Arrivals++;
                var back = Protocol.Build(Msg.ZoneMove, w => { w.Write((byte)3); w.Write(BackText); });
                foreach (var p in _session.Peers.ToList())
                    if (p.Welcomed) _session.SendTo(p.Id, back);
                Plugin.Log.LogInfo("IFZCOOP Host wieder im Spiel, Gäste bekommen den neuen Spielstand");
                _session.ResyncAll();
            }
        }

        /// <summary>
        /// Host: Gäste vorwarnen, dass die Karte gleich weg ist. Sie gelten danach als „ladend“ (kein Spielgeschehen,
        /// Zeitlimit fürs Laden) und gehen ins Hauptmenü. Im neuen Spiel bekommen sie den Spielstand (HostWatchMap).
        /// </summary>
        public void HostMapLeaving(string text)
        {
            if (_session.Role != Role.Host)
                return;
            var msg = Protocol.Build(Msg.ZoneMove, w => { w.Write((byte)2); w.Write(text ?? LeftText); });
            int n = 0;
            foreach (var p in _session.Peers.ToList())
            {
                if (!p.Welcomed)
                    continue;
                _session.SendTo(p.Id, msg);
                p.AwaitingLoad = true;
                p.LastHeard = Time.realtimeSinceStartup;
                n++;
            }
            _hostAway = true;
            _startedAt = GameAccess.IsGameStarted ? Time.realtimeSinceStartup : -1f;
            if (n > 0)
            {
                MovesSent++;
                Plugin.Log.LogInfo($"IFZCOOP Host verlässt die Karte, {n} Gast/Gäste warten im Hauptmenü: {text}");
            }
        }

        private static Snap TakeZone(NpcZoneController zc, bool withJson)
        {
            var s = new Snap();
            if (zc == null)
                return s;
            s.Loc = (int)NpcZoneController.SelectedLocationType;
            s.Revealed = zc.NpcZoneRevealed;
            s.Taken = zc.NpcZoneTakenOver;
            var poc = GameAccess.Game<PlaceableObjectsController>();
            var ids = zc.NpcZoneStructureIds;
            if (ids == null)
                return s;
            foreach (var ss in ids)
            {
                var it = new Item { Id = ss.Id ?? "", Type = ss.Type?.FullName ?? "" };
                var st = Find(poc, ss.Id, ss.Type);
                if (st != null)
                {
                    it.Found = true;
                    it.Draft = st.Draft != null ? st.Draft.Id ?? "" : "";
                    it.State = (int)StateOf(st);
                    if (st is Building b && b.Hideout != null)
                    {
                        it.Hideout = true;
                        if (withJson)
                        {
                            try { it.Json = JsonConvert.SerializeObject(new HideoutSerializedInfo(b.Hideout), GameJson.DefaultSettings); }
                            catch (Exception e) { Plugin.Log.LogDebug("Versteck als JSON: " + e.Message); }
                        }
                    }
                }
                s.Items.Add(it);
            }
            return s;
        }

        private void HostZone(float now)
        {
            if (now < _zoneNextSend)
                return;
            _zoneNextSend = now + ZoneInterval;
            bool force = now >= _zoneNextForce;
            if (force) _zoneNextForce = now + ZoneForceInterval;

            var zc = Zone();
            if (zc == null)
                return;
            Snap s;
            try { s = TakeZone(zc, withJson: true); }
            catch (Exception e) { Plugin.Log.LogDebug("Siedlung-Stand: " + e.Message); return; }

            int taken = s.Taken ? 1 : 0;
            if (_lastTaken == 0 && taken == 1)
            {
                _resyncAt = now + TakeoverResyncDelay;
                Plugin.Log.LogInfo("IFZCOOP Siedlung übernommen, neuer Spielstand für die Gäste in " + TakeoverResyncDelay + " s");
            }
            _lastTaken = taken;

            string sig = s.Sig();
            if (!force && sig == _zoneSentSig)
                return;
            _zoneSentSig = sig;
            ZoneSent++;
            _session.BroadcastToGuests(Protocol.Build(Msg.ZoneState, w =>
            {
                w.Write(s.Loc);
                w.Write(s.Revealed);
                w.Write(s.Taken);
                w.Write(s.Items.Count);
                foreach (var it in s.Items)
                {
                    w.Write(it.Id);
                    w.Write(it.Type);
                    w.Write(it.Draft);
                    w.Write(it.State);
                    w.Write(it.Found);
                    w.Write(it.Hideout);
                    w.Write(it.Json ?? "");
                }
            }));
        }

        private static Stats TakeStats()
        {
            var st = new Stats();
            var dc = GameAccess.Game<DeadCharactersController>();
            if (dc != null)
            {
                st.Inf = dc.DeadInfected;
                st.Raid = dc.DeadRaiders;
                st.Cit = dc.DeadCitizens;
            }
            var awc = GameAccess.Game<AreaWorkController>();
            if (awc != null)
                st.Gath = awc.GatheredResources;
            st.Found = BuildingsStats.FoundResources;
            var ic = GameAccess.Game<ImmigrantsController>();
            if (ic != null)
                st.Imm = ic.ImmigrantsTaken;
            return st;
        }

        private void HostStats(float now, bool immediately)
        {
            if (!immediately && now < _statsNextSend)
                return;
            bool force = immediately || now >= _statsNextForce;
            Stats st;
            try { st = TakeStats(); }
            catch (Exception e) { Plugin.Log.LogDebug("Statistik: " + e.Message); return; }
            string sig = st.Sig;
            if (!force && sig == _statsSentSig)
                return;
            _statsNextSend = now + StatsInterval;
            _statsNextForce = now + StatsForceInterval;
            _statsSentSig = sig;
            StatsSent++;
            _session.BroadcastToGuests(Protocol.Build(Msg.ZoneStats, w =>
            {
                w.Write(st.Inf);
                w.Write(st.Raid);
                w.Write(st.Cit);
                w.Write(st.Imm);
                w.Write(st.Gath);
                w.Write(st.Found);
            }));
        }

        /// <summary>Host: Statistik sofort (Spielende).</summary>
        public void HostSendStatsNow()
        {
            if (_session.Role == Role.Host && _session.ActiveGuestCount > 0 && GameAccess.IsGameStarted)
                HostStats(Time.realtimeSinceStartup, true);
        }

        // ================================================================ Gast

        public void GuestFrame()
        {
            if (!_awaitHost)
                return;
            if (_session.Role != Role.Guest)
            {
                _awaitHost = false;
                return;
            }
            // Neuer Spielstand des Hosts angekommen: Warten vorbei.
            if (_session.TransferId != -1)
            {
                _awaitHost = false;
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (now < _nextAwaitCheck)
                return;
            _nextAwaitCheck = now + 5f;
            // Lief beim Gast noch ein Ladevorgang (alte Karte) zu Ende, wieder ins Hauptmenü.
            if (_session.GuestState == GuestState.InGame || GameAccess.IsGameStarted)
            {
                _session.GuestAwaitHost("Warte auf den Host (Kartenwechsel) ...");
                GoToMenuSafe();
            }
        }

        public void GuestOnMove(BinaryReader r)
        {
            byte phase = r.ReadByte();
            string text = r.ReadString();
            if (_session.Role != Role.Guest)
                return;
            switch (phase)
            {
                case 1:
                    _session.ShowNotice(text, 20f);
                    break;
                case 2:
                    MoveWaits++;
                    _awaitHost = true;
                    _nextAwaitCheck = Time.realtimeSinceStartup + 5f;
                    _session.GuestAwaitHost("Warte auf den Host (Kartenwechsel) ...");
                    _session.ShowNotice(text, 30f);
                    Plugin.Log.LogInfo("IFZCOOP Host verlässt die Karte, Gast wartet im Hauptmenü");
                    GoToMenuSafe();
                    break;
                case 3:
                    _session.ShowNotice(text, 15f);
                    break;
            }
        }

        private static void GoToMenuSafe()
        {
            try
            {
                if (ScenesManager.IsSceneSwitching || ScenesManager.IsMainSceneActive)
                    return;
                ScenesManager.GoToMenu();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ins Hauptmenü: " + e.Message);
            }
        }

        public void GuestOnStats(BinaryReader r)
        {
            var st = new Stats
            {
                Inf = r.ReadInt32(),
                Raid = r.ReadInt32(),
                Cit = r.ReadInt32(),
                Imm = r.ReadInt32(),
                Gath = r.ReadSingle(),
                Found = r.ReadSingle(),
            };
            _hostStats = st;
            _haveStats = true;
            ApplyStats(deadOnly: false);
            StatsApplied++;
        }

        private void ApplyStats(bool deadOnly)
        {
            if (!_haveStats)
                return;
            var st = _hostStats;
            var dc = GameAccess.Game<DeadCharactersController>();
            if (dc != null)
            {
                dc.DeadInfected = st.Inf;
                dc.DeadRaiders = st.Raid;
                dc.DeadCitizens = st.Cit;
            }
            if (deadOnly)
                return;
            var awc = GameAccess.Game<AreaWorkController>();
            if (awc != null)
                awc.GatheredResources = st.Gath;
            BuildingsStats.FoundResources = st.Found;
            var ic = GameAccess.Game<ImmigrantsController>();
            if (ic != null)
                ic.ImmigrantsTaken = st.Imm;
        }

        public void GuestOnState(BinaryReader r)
        {
            var s = new Snap
            {
                Loc = r.ReadInt32(),
                Revealed = r.ReadBoolean(),
                Taken = r.ReadBoolean(),
            };
            int n = r.ReadInt32();
            if (n < 0 || n > 10000)
                throw new InvalidDataException("zu viele Siedlungsgebäude");
            for (int i = 0; i < n; i++)
            {
                s.Items.Add(new Item
                {
                    Id = r.ReadString(),
                    Type = r.ReadString(),
                    Draft = r.ReadString(),
                    State = r.ReadInt32(),
                    Found = r.ReadBoolean(),
                    Hideout = r.ReadBoolean(),
                    Json = r.ReadString(),
                });
            }

            int mismatch;
            UnitSync.NetApplying = true;
            try
            {
                mismatch = ApplyZone(s);
                ZoneApplied++;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Siedlung übernehmen: " + e);
                mismatch = 1;
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
            Mismatch = mismatch;

            // Was sich nicht nachstellen lässt (Versteck ohne Gruppe, Gebäude fehlt …): nach 30 s einmal den Spielstand holen.
            float now = Time.realtimeSinceStartup;
            string sig = s.Sig();
            if (mismatch == 0)
            {
                _mismatchSince = -1f;
                _mismatchSig = null;
            }
            else if (_mismatchSince < 0f || _mismatchSig != sig)
            {
                _mismatchSince = now;
                _mismatchSig = sig;
            }
            else if (now - _mismatchSince > FallbackDelay && _resyncAskedFor != sig)
            {
                _resyncAskedFor = sig;
                ResyncAsked++;
                Plugin.Log.LogWarning($"IFZCOOP Siedlung weicht seit {FallbackDelay} s ab ({mismatch} Gebäude), hole Spielstand neu");
                _session.RequestResync();
            }
        }

        private int ApplyZone(Snap s)
        {
            var zc = Zone();
            if (zc == null)
                return 0;
            var loc = (LocationType)s.Loc;
            if (NpcZoneController.SelectedLocationType != loc)
                NpcZoneController.SetLocationType(loc);
            bool takeFlip = s.Taken && !zc.NpcZoneTakenOver;
            if (zc.NpcZoneRevealed != s.Revealed)
                RevealedSetter?.Invoke(zc, new object[] { s.Revealed });
            if (zc.NpcZoneTakenOver != s.Taken)
                TakenSetter?.Invoke(zc, new object[] { s.Taken });

            if (!SameIds(zc.NpcZoneStructureIds, s.Items))
            {
                var list = new List<StructureSerialized>();
                foreach (var it in s.Items)
                {
                    var t = TypeOf(it.Type);
                    if (t != null)
                        list.Add(new StructureSerialized(it.Id, t));
                }
                zc.NpcZoneStructureIds = list;
            }

            var poc = GameAccess.Game<PlaceableObjectsController>();
            var spawner = zc.NpcZoneSpawner;
            var mark = s.Taken ? LocationType.NormalPlace : loc;
            int mismatch = 0;
            foreach (var it in s.Items)
            {
                if (!it.Found)
                    continue; // auch beim Host nicht (mehr) da
                var st = Find(poc, it.Id, TypeOf(it.Type));
                if (st == null)
                {
                    mismatch++; // z. B. Feld, das StructureSync noch anlegt
                    continue;
                }
                if (st.MarkedAsPartOfZoneType != mark)
                    st.MarkedAsPartOfZoneType = mark;
                try
                {
                    if (takeFlip)
                        NpcZoneSpawner.HandleEmblems(st, LocationType.NormalPlace);
                    if (!ApplyItem(st, it, loc, spawner))
                        mismatch++;
                }
                catch (Exception e)
                {
                    mismatch++;
                    Plugin.Log.LogWarning($"Siedlungsgebäude {it.Id} ({it.Draft}, {(StructureStateType)it.State}): {e.Message}");
                }
            }
            return mismatch;
        }

        private static bool SameIds(List<StructureSerialized> have, List<Item> want)
        {
            if (have == null || have.Count != want.Count)
                return false;
            for (int i = 0; i < have.Count; i++)
                if (have[i].Id != want[i].Id || (have[i].Type?.FullName ?? "") != want[i].Type)
                    return false;
            return true;
        }

        /// <summary>Ein Gebäude/Feld der Zone nachstellen. true = passt jetzt zum Host.</summary>
        private bool ApplyItem(Structure st, Item it, LocationType loc, NpcZoneSpawner spawner)
        {
            var want = (StructureStateType)it.State;
            var have = StateOf(st);
            string draft = st.Draft != null ? st.Draft.Id ?? "" : "";
            bool npcState = want == StructureStateType.NpcZoneDefault || want == StructureStateType.NpcZoneHostile;

            if (st is Building b)
            {
                if (it.Hideout)
                    return b.Hideout != null || CreateHideout(b, it, loc);
                if (npcState)
                {
                    if (have == want && draft == it.Draft)
                        return true;
                    var d = DraftById(it.Draft);
                    if (d == null || spawner == null)
                        return false;
                    spawner.AdaptNpcBuilding(b, d, loc);
                    if (StateOf(b) != want && b.StateMachine.HasState(want))
                        b.StateMachine.ChangeTo(want);
                    Adapted++;
                    Plugin.Log.LogInfo($"IFZCOOP Siedlungsgebäude {b.ID} -> {it.Draft} {want}");
                    return StateOf(b) == want;
                }
                if (want == StructureStateType.Ruined && loc == LocationType.AbandonedZone && draft != it.Draft)
                {
                    var d = DraftById(it.Draft);
                    if (d == null || spawner == null)
                        return false;
                    spawner.AdaptNpcBuildingOnReveal(loc, b, d, new List<Group>());
                    Adapted++;
                    return true;
                }
                return true; // übrige Zustände (fertig nach Übernahme, Ruine, Abriss) gleicht BuildingSync ab
            }

            // Felder entstehen über StructureSync; hier nur der Siedlungs-Zustand
            if (npcState && have != want && st.StateMachine.HasState(want))
            {
                st.StateMachine.ChangeTo(want);
                Adapted++;
            }
            return !npcState || StateOf(st) == want;
        }

        private bool CreateHideout(Building b, Item it, LocationType loc)
        {
            if (string.IsNullOrEmpty(it.Json))
                return false;
            var hc = GameAccess.Game<HideoutsController>();
            if (hc == null)
                return false;
            var info = JsonConvert.DeserializeObject<HideoutSerializedInfo>(it.Json, GameJson.DefaultSettings);
            if (info == null)
                return false;
            if (!string.IsNullOrEmpty(info.MainGroupId) && UnitSync.FindGroupPublic(info.MainGroupId) == null)
                return false; // Hauptgruppe ist noch nicht beim Gast (UnitSync), nächste Runde
            if (!hc.IsBuildingValid(b))
                return false;
            if (!string.IsNullOrEmpty(info.Id) && hc.TryGetHideout(info.Id, out var existing) && existing != null)
                return existing.Building == b;
            var h = hc.CreateHideout(info);
            if (h == null)
                return false;
            b.AddEmblems();
            NpcZoneSpawner.HandleEmblems(b, loc, checkDraft: false);
            HideoutsCreated++;
            Plugin.Log.LogInfo($"IFZCOOP Siedlungs-Versteck {info.Id} in Gebäude {b.ID} angelegt");
            return b.Hideout != null;
        }

        // ================================================================ Haken

        public static void ApplyPatches(Harmony harmony)
        {
            int ok = 0, missing = 0;
            void P(Type type, string method, string prefix, string postfix = null)
            {
                try
                {
                    var target = AccessTools.DeclaredMethod(type, method) ?? AccessTools.Method(type, method);
                    if (target == null) { missing++; Plugin.Log.LogWarning($"IFZCOOP zone hook fehlt: {type.Name}.{method}"); return; }
                    harmony.Patch(target,
                        prefix: prefix == null ? null : new HarmonyMethod(typeof(ZoneSync), prefix),
                        postfix: postfix == null ? null : new HarmonyMethod(typeof(ZoneSync), postfix));
                    ok++;
                }
                catch (Exception e) { missing++; Plugin.Log.LogWarning($"IFZCOOP zone hook {type.Name}.{method} nicht gesetzt: {e.Message}"); }
            }

            // Siedlungen: nichts selbst würfeln oder übernehmen
            var zone = typeof(NpcZoneController);
            P(zone, "OnNpcZoneStructureDetected", nameof(RevealPrefix));
            P(zone, "TryMeasurePlaceForFarms", nameof(CoroutinePrefix));
            P(zone, "HandleTakeOverNpcZone", nameof(GuestBlockPrefix));
            P(zone, "OnGroupEmpty", nameof(GuestBlockPrefix));
            P(zone, "TakeOverBuildings", nameof(GuestTruePrefix));
            P(zone, "ChangeNpcZoneGroupsToHostile", nameof(GuestTruePrefix));
            P(zone, "ComebackToConvoySelector", nameof(GuestFalsePrefix));

            // Umzug nur beim Host
            P(typeof(UI.InfoPanels.NestedUIElements.ExpeditionCenterPanel), "HandleInitMigrationClicked", nameof(MigrationClickPrefix));
            P(typeof(MigrationConvoyController), "ShowStartMigrationPopup", nameof(MigrationClickPrefix));
            P(typeof(MigrationConvoyController), "TrySaveGameBeforeMigration", nameof(GuestBlockPrefix));
            P(typeof(MigrationConvoyController), "SwitchToConvoyTileSelection", nameof(GuestBlockPrefix));
            P(typeof(ConvoyCreator), "StartConvoy", nameof(StartConvoyPrefix));
            P(typeof(GameOverController), "HandleMigrationStarted", nameof(GuestBlockPrefix), nameof(MigrationStartedPostfix));

            // Statistik vom Host
            P(typeof(UI.InfoPanels.StatisticsPanel), "ShowPanel", nameof(StatsShowPrefix));
            P(typeof(DeadCharactersController), "Add", null, nameof(DeadAddPostfix));
            P(typeof(GameOverController), "PlayEndgameSequence", null, nameof(EndgamePostfix));

            Plugin.Log.LogInfo($"Siedlung/Umzug/Statistik: {ok} Haken gesetzt, {missing} fehlen");
        }

        private static bool GuestBlockPrefix() => !IsGuest;

        private static bool RevealPrefix()
        {
            if (!IsGuest)
                return true;
            RevealBlocked++;
            return false;
        }

        private static bool CoroutinePrefix(ref IEnumerator __result)
        {
            if (!IsGuest)
                return true;
            __result = Empty();
            return false;
        }

        private static IEnumerator Empty()
        {
            yield break;
        }

        private static bool GuestTruePrefix(ref bool __result)
        {
            if (!IsGuest)
                return true;
            __result = true;
            return false;
        }

        private static bool GuestFalsePrefix(ref bool __result)
        {
            if (!IsGuest)
                return true;
            __result = false;
            return false;
        }

        private static bool MigrationClickPrefix()
        {
            if (!IsGuest)
                return true;
            MoveBlocked++;
            Plugin.Instance?.Session?.ShowNotice(GuestNoMoveText, 10f);
            return false;
        }

        private static bool StartConvoyPrefix()
        {
            if (IsGuest)
            {
                MoveBlocked++;
                Plugin.Instance?.Session?.ShowNotice(GuestNoMoveText, 10f);
                return false;
            }
            if (IsHostWithGuests)
            {
                try { Me?.HostMapLeaving(MoveText); }
                catch (Exception e) { Plugin.Log.LogWarning("Umzug melden: " + e.Message); }
            }
            return true;
        }

        private static void MigrationStartedPostfix()
        {
            if (!IsHostWithGuests)
                return;
            try
            {
                var s = Plugin.Instance.Session;
                s.BroadcastToGuests(Protocol.Build(Msg.ZoneMove, w => { w.Write((byte)1); w.Write(AnnounceText); }));
                Me?.HostSendStatsNow();
                Plugin.Log.LogInfo("IFZCOOP Umzug angekündigt");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Umzug ankündigen: " + e.Message); }
        }

        private static void StatsShowPrefix()
        {
            if (!IsGuest)
                return;
            try { Me?.ApplyStats(deadOnly: false); }
            catch (Exception e) { Plugin.Log.LogDebug("Statistik anzeigen: " + e.Message); }
        }

        private static void DeadAddPostfix()
        {
            if (!IsGuest)
                return;
            try { Me?.ApplyStats(deadOnly: true); }
            catch (Exception e) { Plugin.Log.LogDebug("Statistik Tote: " + e.Message); }
        }

        private static void EndgamePostfix()
        {
            try { Me?.HostSendStatsNow(); }
            catch (Exception e) { Plugin.Log.LogDebug("Statistik Spielende: " + e.Message); }
        }

        // ================================================================ Status und Tests

        public static IEnumerable<string> StatusLines()
        {
            var lines = new List<string>();
            try
            {
                var zc = GameAccess.IsGameStarted ? Zone() : null;
                if (zc == null)
                {
                    lines.Add("zonestate keine");
                }
                else
                {
                    var s = TakeZone(zc, withJson: false);
                    uint hash = 2166136261;
                    foreach (char ch in s.Sig())
                        hash = (hash ^ ch) * 16777619;
                    lines.Add($"zonestate loc={(LocationType)s.Loc} rev={(s.Revealed ? 1 : 0)} taken={(s.Taken ? 1 : 0)} n={s.Items.Count} hash={hash:x8}");
                    foreach (var it in s.Items.Take(20))
                        lines.Add($"zs {it.Id} {(it.Found ? it.Draft : "fehlt")} {(StructureStateType)it.State} h={(it.Hideout ? 1 : 0)}");
                }
                if (GameAccess.IsGameStarted)
                {
                    var st = TakeStats();
                    lines.Add($"zonestats inf={st.Inf} raid={st.Raid} cit={st.Cit} imm={st.Imm} gath={Mathf.RoundToInt(st.Gath)} found={Mathf.RoundToInt(st.Found)}");
                }
            }
            catch (Exception e)
            {
                lines.Add("zonestate fehler " + e.Message);
            }
            return lines;
        }

        public string StatusLine() =>
            $"zoneSent={ZoneSent} zoneApplied={ZoneApplied} adapted={Adapted} hideoutsCreated={HideoutsCreated} mismatch={Mismatch} resyncAsked={ResyncAsked}"
            + $" takeoverResyncs={TakeoverResyncs} statsSent={StatsSent} statsApplied={StatsApplied} revealBlocked={RevealBlocked} moveBlocked={MoveBlocked}"
            + $" movesSent={MovesSent} arrivals={Arrivals} moveWaits={MoveWaits} hostAway={(_hostAway ? 1 : 0)} awaitHost={(_awaitHost ? 1 : 0)}";

        /// <summary>
        /// Testbefehle "zone ...":
        ///   status | make neutral|farmers|raiders|purifiers|abandoned (Host) | reveal | take (Host) | hostile (Host)
        ///   stat &lt;inf&gt; &lt;raid&gt; &lt;cit&gt; &lt;gath&gt; &lt;found&gt; (Host) | stats
        ///   migrate (Gast: Sperre prüfen) | convoy (Gast: Sperre prüfen) | leave [umzug] (Host: ins Hauptmenü) | load &lt;Ordner&gt; (Host, im Hauptmenü)
        /// </summary>
        public string Qa(string[] a)
        {
            string sub = a.Length > 1 ? a[1].ToLowerInvariant() : "status";
            var inv = CultureInfo.InvariantCulture;
            bool guest = _session.Role == Role.Guest;
            var zc = GameAccess.IsGameStarted ? Zone() : null;
            switch (sub)
            {
                case "status":
                    return StatusLine() + " | " + string.Join(" | ", StatusLines().Where(l => !l.StartsWith("zs ")));

                case "make":
                {
                    if (guest) return "nur beim Host";
                    if (zc == null) return "kein Spiel";
                    var type = ParseLoc(a.Length > 2 ? a[2] : "neutral");
                    if (zc.NpcZoneStructureIds.Count > 0) return $"schon eine Zone ({NpcZoneController.SelectedLocationType}, {zc.NpcZoneStructureIds.Count} Gebäude)";
                    var hq = HqController.MainHeadquarter;
                    if (hq == null) return "kein HQ";
                    NpcZoneController.SetLocationType(type);
                    var pos = hq.transform.position;
                    if (type == LocationType.FarmersZone)
                    {
                        if (SpawnFarmerZoneMethod == null) return "TrySpawnFarmerZone fehlt";
                        CoroutineRunner.StartLocalCoroutine((IEnumerator)SpawnFarmerZoneMethod.Invoke(zc, new object[] { pos }));
                        return "Bauernhof-Suche läuft (zone status in ein paar Sekunden)";
                    }
                    if (SpawnZoneMethod == null) return "TrySpawnZone fehlt";
                    SpawnZoneMethod.Invoke(zc, new object[] { pos });
                    return $"Zone {type}: {zc.NpcZoneStructureIds.Count} Gebäude";
                }

                case "reveal":
                {
                    if (zc == null) return "kein Spiel";
                    if (DetectedMethod == null) return "OnNpcZoneStructureDetected fehlt";
                    Structure first = null;
                    if (zc.NpcZoneStructureIds.Count > 0)
                    {
                        var ss = zc.NpcZoneStructureIds[0];
                        first = Find(GameAccess.Game<PlaceableObjectsController>(), ss.Id, ss.Type);
                    }
                    if (first == null && !guest) return "keine Zone (erst: zone make)";
                    ICharacter ch = null;
                    var squads = GameAccess.Game<SquadsController>()?.Squads;
                    if (squads != null)
                        foreach (var g in squads)
                            if (g != null && g.Characters.Count > 0) { ch = g.Characters[0]; break; }
                    int before = RevealBlocked;
                    try { DetectedMethod.Invoke(zc, new object[] { first, ch }); }
                    catch (TargetInvocationException e) { return "Fehler: " + (e.InnerException?.Message ?? e.Message); }
                    if (guest) return RevealBlocked > before ? "gesperrt" : "NICHT gesperrt";
                    return $"aufgedeckt={zc.NpcZoneRevealed} Gebäude={zc.NpcZoneStructureIds.Count}";
                }

                case "take":
                    if (guest) return "nur beim Host";
                    if (zc == null) return "kein Spiel";
                    zc.TakeOverBuildings();
                    return $"übernommen={zc.NpcZoneTakenOver}";

                case "hostile":
                    if (guest) return "nur beim Host";
                    if (zc == null) return "kein Spiel";
                    zc.ChangeNpcZoneGroupsToHostile();
                    return "Siedlung feindlich";

                case "stat":
                {
                    if (guest) return "nur beim Host";
                    if (!GameAccess.IsGameStarted) return "kein Spiel";
                    int I(int i) => a.Length > i ? int.Parse(a[i], inv) : 0;
                    float F(int i) => a.Length > i ? float.Parse(a[i], inv) : 0f;
                    var dc = GameAccess.Game<DeadCharactersController>();
                    if (dc != null) { dc.DeadInfected = I(2); dc.DeadRaiders = I(3); dc.DeadCitizens = I(4); }
                    var awc = GameAccess.Game<AreaWorkController>();
                    if (awc != null) awc.GatheredResources = F(5);
                    BuildingsStats.FoundResources = F(6);
                    var st = TakeStats();
                    return $"inf={st.Inf} raid={st.Raid} cit={st.Cit} gath={st.Gath:0} found={st.Found:0}";
                }

                case "stats":
                {
                    var st = TakeStats();
                    return $"inf={st.Inf} raid={st.Raid} cit={st.Cit} imm={st.Imm} gath={st.Gath:0} found={st.Found:0} (vom Host: {(_haveStats ? "ja" : "nein")})";
                }

                case "migrate":
                {
                    if (!guest) return "nur beim Gast (beim Host würde das Umzugsfenster aufgehen)";
                    var mcc = GameAccess.Game<MigrationConvoyController>();
                    if (mcc == null) return "MigrationConvoyController fehlt";
                    int before = MoveBlocked;
                    mcc.ShowStartMigrationPopup(HqController.MainHeadquarter);
                    return MoveBlocked > before ? "gesperrt" : "NICHT gesperrt";
                }

                case "convoy":
                {
                    if (!guest) return "nur beim Gast (beim Host würde der Umzug starten)";
                    var cc = GameAccess.Game<ConvoyCreator>();
                    if (cc == null) return "ConvoyCreator fehlt";
                    int before = MoveBlocked;
                    try { cc.StartConvoy(null, new Vehicle[0], new Group[0]); }
                    catch (Exception e) { return "NICHT gesperrt (" + e.GetType().Name + ")"; }
                    return MoveBlocked > before ? "gesperrt" : "NICHT gesperrt";
                }

                case "leave":
                    if (guest) return "nur beim Host";
                    if (!GameAccess.IsGameStarted) return "kein Spiel";
                    if (a.Length > 2 && a[2].ToLowerInvariant() == "umzug")
                        HostMapLeaving(MoveText);
                    GoToMenuSafe();
                    return "Host geht ins Hauptmenü";

                case "load":
                {
                    if (guest) return "nur beim Host";
                    if (GameAccess.IsGameStarted || !ScenesManager.IsMainSceneActive) return "erst ins Hauptmenü (zone leave)";
                    string name = a.Length > 2 ? a[2] : "ifzcoop-host";
                    string dir = SaveHandler.GetSaveFolderPath(name);
                    if (name == "ifzcoop-host")
                    {
                        // Nicht den Übertragungs-Ordner selbst laden: den überschreibt der Mod gleich wieder (Spielstand für die Gäste),
                        // während das Spiel den geladenen Ordner noch packt.
                        string copy = SaveHandler.GetSaveFolderPath("ifzcoop-zonetest");
                        CopyDir(dir, copy);
                        dir = copy;
                    }
                    var info = SaveHandler.LoadSaveInfoByDirPath(dir);
                    var validator = GameAccess.SaveValidator;
                    if (info == null || validator == null) return "Spielstand oder SaveValidator fehlt: " + dir;
                    validator.TryRun(dir, info);
                    return "lade " + dir;
                }
            }
            return "zone status|make <art>|reveal|take|hostile|stat ...|stats|migrate|convoy|leave [umzug]|load <ordner>";
        }

        private static void CopyDir(string from, string to)
        {
            if (Directory.Exists(to))
                Directory.Delete(to, true);
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(from.Length).TrimStart('\\', '/');
                string target = Path.Combine(to, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(f, target, true);
            }
        }

        private static LocationType ParseLoc(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "neutral": return LocationType.NeutralZone;
                case "farmers":
                case "farmer": return LocationType.FarmersZone;
                case "raiders":
                case "bandits": return LocationType.RaidersCamp;
                case "purifiers": return LocationType.PurifiersZone;
                case "abandoned": return LocationType.AbandonedZone;
            }
            return Enum.TryParse<LocationType>(s, true, out var t) ? t : LocationType.NeutralZone;
        }
    }
}
