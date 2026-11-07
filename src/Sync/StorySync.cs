using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Audio;
using BepInEx.Configuration;
using Cameras;
using Core.States;
using DialogueSystem.Runtime.Core;
using EventsSystem;
using EventsSystem.Actions;
using Gameplay.Immigrants;
using Gameplay.Signals;
using Gameplay.Units;
using Gameplay.Units.Enemy;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using Newtonsoft.Json;
using TransmissionSystem;
using UnityEngine;
using Zenject;
using GameJson = Utilities.JsonSerializer;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Zwei Lücken der Story beim Gast.
    ///
    /// (A) Seiten der Gruppen (Msg.GroupSide). UnitSync schickt eine Gruppe nur beim Erscheinen ganz (GroupSerializedInfo), danach
    /// nur Ort, Figuren und Lebenspunkte. Wechselt beim Host später die Fraktion (EventsSystemController.ChangeFraction →
    /// FractionAssigner.SetFraction, Group.ChangeFraction) oder die Zugehörigkeit (ChangeAffiliation, AffiliationProvider.Set,
    /// beschossene Gruppen mit ChangeAffiliationToAttacker, Einwanderer annehmen/ablehnen/feindlich, Lösegeld-Banditen,
    /// NpcZone …), blieb beim Gast die alte Farbe.
    /// Host: 1×/s je Nicht-Spieler-Gruppe (GroupsController) eine Unterschrift über Vorlage (= Fraktion), alle Zugehörigkeiten,
    /// Einwanderer-Zustand und Gruppen-Zustand (Idle/Inactive – grau). Geänderte Gruppen gehen sofort raus, alle 10 s alle.
    /// Gast: setzt mit denselben Spiel-Methoden (FractionAssigner.SetFraction, AffiliationProvider.Deserialize wie beim Laden,
    /// StateMachine.SetState wie GroupBuilder.RestoreState) unter UnitSync.NetApplying; noch fehlende Gruppen jede Sekunde erneut.
    /// Einwanderer: der Gast nimmt nichts selbst an (UnitPatches sperrt ImmigrantsController beim Gast), Antworten im Funk gehen
    /// wie bisher über RadioSync an den Host, das eigene Einladen per Funk (outtrans_welcome → StartBroadcastInviteSystem) geht
    /// als Wunsch an den Host (Msg.GroupSide in Gegenrichtung). Bürger werden die Einwanderer beim Host; beim Gast kommen sie
    /// über PopulationSync, die leere Gruppe verschwindet über die Gruppenliste (UnitSync).
    ///
    /// (B) Story-Kamera und Töne (Msg.StoryCue). Ereignis-Aktionen (RunAction: CenterCameraOnObject, FollowMarkerByCamera,
    /// PlayAudio) und Funk-Funktionen (CenterCameraOnMarker, CenterCameraOnGroup, CenterAtImmigrants) laufen nur beim Host.
    /// Host: solange eine davon läuft, wird jedes GameCamera.CenterOnPosition mitgeschickt; Folgen und Ton nach RunAction.
    /// Gast: fährt die Kamera einmal dorthin, ohne Sperre und ohne Folgen (abschaltbar: Einstellung [Gast] StoryKamera)
    /// und spielt den Ton.
    /// </summary>
    internal sealed class StorySync
    {
        private const float SendInterval = 1f;
        private const float ForceInterval = 10f;

        public enum CueKind : byte { Center = 1, Follow = 2, Audio = 3, Radio = 4 }

        public enum Op : byte { Invite = 1 }

        private sealed class Side
        {
            public string Id;
            public string Draft;          // nur Hinweis zum Anwenden; Vorlagen aus Verstecken (hideout_bandits) gibt es beim Gast als „bandits“
            public Fraction Frac;
            public FractionAffiliation[] Affs;
            public ImmigrantsState Imm;
            public bool Played;
            public StateType State;

            public string Sig()
            {
                var sb = new StringBuilder();
                sb.Append((int)Frac).Append('|');
                foreach (var a in Affs) sb.Append((int)a.Fraction).Append(':').Append((int)a.Affiliation).Append(',');
                sb.Append('|').Append((int)Imm).Append(Played ? 'P' : '-').Append('|').Append((int)State);
                return sb.ToString();
            }
        }

        private static readonly AccessTools.FieldRef<Group, Group.FractionAssigner> AssignerField =
            AccessTools.FieldRefAccess<Group, Group.FractionAssigner>("_fractionAssigner");

        private readonly Session _session;
        private readonly ConfigEntry<bool> _cameraSetting;

        // Host
        private readonly Dictionary<string, string> _sentSigs = new Dictionary<string, string>();
        private float _nextSend;
        private float _nextForce;

        // Gast
        private readonly Dictionary<string, Side> _host = new Dictionary<string, Side>();
        private float _nextReapply;

        public int SidesSent { get; private set; }
        public int SidesReceived { get; private set; }
        public int FractionChanged { get; private set; }
        public int AffChanged { get; private set; }
        public int ImmChanged { get; private set; }
        public int StateChanged { get; private set; }
        public int ApplyFailed { get; private set; }
        public int Pending { get; private set; }
        public int InvitesSent { get; private set; }
        public int InvitesDone { get; private set; }
        public int CuesSent { get; private set; }
        public int CuesReceived { get; private set; }
        public int CuesApplied { get; private set; }
        public int CamSkipped { get; private set; }
        public string LastCue { get; private set; } = "-";

        public StorySync(Session session)
        {
            _session = session;
            try
            {
                _cameraSetting = Plugin.Instance?.Config.Bind("Gast", "StoryKamera", true,
                    "Gast: Die Kamera fährt mit, wenn beim Host die Story die Kamera bewegt (Marker, Gruppen, Einwanderer). Aus = nur Ton.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Einstellung StoryKamera: " + e.Message); }
        }

        public bool CameraEnabled
        {
            get => _cameraSetting == null || _cameraSetting.Value;
            set { if (_cameraSetting != null) _cameraSetting.Value = value; }
        }

        public void ResendAll()
        {
            _sentSigs.Clear();
            _nextSend = 0f;
            _nextForce = 0f;
        }

        public void Reset()
        {
            ResendAll();
            _host.Clear();
            _tries.Clear();
            _storyDepth = 0;
            _ctx = null;
        }

        private bool HostWithGuests => _session.Role == Role.Host && _session.ActiveGuestCount > 0;

        private bool GuestCanAsk => _session.Role == Role.Guest && _session.GuestControlsLocked && !UnitSync.NetApplying;

        // ================================================================ Seiten: Host

        private static IEnumerable<Group> NpcGroups()
        {
            var groups = GameAccess.Game<GroupsController>()?.Groups;
            if (groups == null)
                yield break;
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                if (g != null && !string.IsNullOrEmpty(g.Id) && !g.IsEmpty() && g.Draft != null && g.AffiliationProvider != null)
                    yield return g;
            }
        }

        private static Side Take(Group g)
        {
            var affs = g.AffiliationProvider.Serialize();
            Array.Sort(affs, (x, y) => ((int)x.Fraction).CompareTo((int)y.Fraction));
            StateType st = StateType.Idle;
            try { if (g.StateMachine?.CurrentState != null) st = g.StateMachine.CurrentState.Type; } catch { }
            return new Side { Id = g.Id, Draft = g.Draft.name, Frac = g.Fraction, Affs = affs, Imm = g.ImmigrantsState, Played = g.ImmigrantsTransmissionPlayed, State = st };
        }

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend || !HostWithGuests || !GameAccess.IsGameStarted)
                return;
            _nextSend = now + SendInterval;
            bool full = now >= _nextForce;
            if (full) _nextForce = now + ForceInterval;

            var all = new List<Side>();
            try
            {
                foreach (var g in NpcGroups())
                    all.Add(Take(g));
            }
            catch (Exception e) { Plugin.Log.LogDebug("Gruppen-Seiten: " + e.Message); return; }

            var send = new List<Side>();
            foreach (var s in all)
            {
                string sig = s.Sig();
                if (full || !_sentSigs.TryGetValue(s.Id, out var old) || old != sig)
                    send.Add(s);
            }
            if (!full && send.Count == 0)
                return;
            if (full) _sentSigs.Clear();
            foreach (var s in send) _sentSigs[s.Id] = s.Sig();

            _session.BroadcastToGuests(Protocol.Build(Msg.GroupSide, w =>
            {
                w.Write(full);
                w.Write(send.Count);
                foreach (var s in send)
                {
                    w.Write(s.Id);
                    w.Write(s.Draft);
                    w.Write((byte)s.Frac);
                    w.Write((byte)s.Affs.Length);
                    foreach (var a in s.Affs)
                    {
                        w.Write((byte)a.Fraction);
                        w.Write((byte)a.Affiliation);
                    }
                    w.Write((byte)s.Imm);
                    w.Write(s.Played);
                    w.Write((byte)s.State);
                }
            }));
            SidesSent += send.Count;
        }

        /// <summary>Wunsch des Gastes (Msg.GroupSide Gast → Host).</summary>
        public void HostOnRequest(Peer from, BinaryReader r)
        {
            var op = (Op)r.ReadByte();
            try
            {
                if (op == Op.Invite)
                {
                    var ic = GameAccess.Game<ImmigrantsController>();
                    if (ic == null)
                        return;
                    ic.StartBroadcastInviteSystem();
                    InvitesDone++;
                    _nextSend = 0f;
                    Plugin.Log.LogInfo($"IFZCOOP story Einladen (Funk) von {from.Name}");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"IFZCOOP story-Wunsch {op}: {e.Message}"); }
        }

        // ================================================================ Seiten: Gast

        public void GuestOnSides(BinaryReader r)
        {
            bool full = r.ReadBoolean();
            int n = r.ReadInt32();
            if (n < 0 || n > 100000)
                throw new InvalidDataException("zu viele Gruppen");
            var got = new List<Side>(n);
            for (int i = 0; i < n; i++)
            {
                var s = new Side { Id = r.ReadString(), Draft = r.ReadString() };
                s.Frac = (Fraction)r.ReadByte();
                int na = r.ReadByte();
                s.Affs = new FractionAffiliation[na];
                for (int j = 0; j < na; j++)
                    s.Affs[j] = new FractionAffiliation { Fraction = (Fraction)r.ReadByte(), Affiliation = (Affiliation)r.ReadByte() };
                s.Imm = (ImmigrantsState)r.ReadByte();
                s.Played = r.ReadBoolean();
                s.State = (StateType)r.ReadByte();
                got.Add(s);
            }
            SidesReceived += n;
            if (full)
                _host.Clear();
            foreach (var s in got)
                _host[s.Id] = s;
            ApplyAll();
        }

        // Früher standen hier eine „Sicherheits-Freigabe“ (2,5 s nach jedem Kamera-Signal UnblockAllCameraMovement(CenterCamera))
        // und ein Kamera-Wächter. Die Freigabe war selbst die Ursache der hängenden Kamera beim Gast: GameCameraInput löst
        // Sperren mit XOR (^=). Wurde CenterCamera freigegeben, obwohl es gar nicht gesetzt war, während ein anderer Grund
        // (Maus über einem Fenster = PointerOverUi, Pause eines Popups) die Achse sperrte, wurde CenterCamera dadurch GESETZT und
        // blieb für immer. Steht die Tastatur-Bewegung gesperrt, liefert GameCameraInput.GetMousePosition nur noch die alte
        // Mausposition: Ziehen bewegt nichts, Doppelklick fährt an die alte Stelle, Zoom geht. Der Wächter griff nie
        // (Traverse.Property("Input"), Input ist aber ein Feld). Beides entfernt; Diagnose jetzt in UiLocks/CameraRescue.

        public void GuestFrame()
        {
            if (_host.Count == 0 || !_session.GuestControlsLocked)
                return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextReapply)
                return;
            _nextReapply = now + 1f;
            ApplyAll();
        }

        private void ApplyAll()
        {
            var mine = new Dictionary<string, Group>();
            foreach (var g in NpcGroups())
                mine[g.Id] = g;
            int pending = 0;
            UnitSync.NetApplying = true;
            try
            {
                foreach (var s in _host.Values)
                {
                    if (!mine.TryGetValue(s.Id, out var g))
                    {
                        pending++;
                        continue;
                    }
                    try { Apply(g, s); }
                    catch (Exception e)
                    {
                        ApplyFailed++;
                        Plugin.Log.LogDebug($"Gruppen-Seite {s.Id}: {e.Message}");
                    }
                }
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
            Pending = pending;
        }

        /// <summary>Wie oft eine Gruppe für denselben Host-Stand schon angewandt wurde (danach nicht mehr, kein Dauer-Anwenden).</summary>
        private readonly Dictionary<string, KeyValuePair<string, int>> _tries = new Dictionary<string, KeyValuePair<string, int>>();
        private const int MaxTries = 3;
        public int Stuck { get; private set; }

        private static bool AffDiffers(Group g, Side s)
        {
            foreach (var a in s.Affs)
            {
                try { if (g.AffiliationProvider.Get(a.Fraction) != a.Affiliation) return true; }
                catch (KeyNotFoundException) { return true; }
            }
            return false;
        }

        private static bool Differs(Group g, Side s)
        {
            var cur = g.StateMachine?.CurrentState;
            return g.Fraction != s.Frac || AffDiffers(g, s) || g.ImmigrantsState != s.Imm || g.ImmigrantsTransmissionPlayed != s.Played
                || (cur != null && cur.Type != s.State && IsRestorable(s.State) && IsRestorable(cur.Type));
        }

        private void Apply(Group g, Side s)
        {
            if (!Differs(g, s))
            {
                _tries.Remove(s.Id);
                return;
            }
            string sig = s.Sig();
            if (_tries.TryGetValue(s.Id, out var t) && t.Key == sig)
            {
                if (t.Value >= MaxTries)
                    return; // geht hier nicht (z. B. Vorlage fehlt): nicht jede Sekunde neu
                _tries[s.Id] = new KeyValuePair<string, int>(sig, t.Value + 1);
                if (t.Value + 1 == MaxTries)
                {
                    Stuck++;
                    Plugin.Log.LogWarning($"IFZCOOP side {g.Id}: Stand des Hosts lässt sich nicht übernehmen ({s.Frac}, {AffText(s.Affs)})");
                }
            }
            else
                _tries[s.Id] = new KeyValuePair<string, int>(sig, 1);

            // 1. Fraktion wie EventsSystemController.ChangeFraction. Verglichen wird nur die Fraktion: Vorlagen aus Verstecken
            // (hideout_bandits) stehen nicht in GroupsConfig und heißen beim Gast nach dem Laden „bandits“.
            if (g.Fraction != s.Frac)
            {
                var cfg = JutsuGamesConfig.Runtime.Configs.GetConfig<GroupsConfig>();
                var draft = cfg?.GetDraftByName(s.Draft);
                if (draft == null || draft.Fraction != s.Frac)
                    draft = cfg?.GetDraftByFraction(s.Frac);
                var assigner = AssignerField(g) ?? GameAccess.Game<Group.FractionAssigner>();
                if (draft != null && assigner != null)
                {
                    var before = g.Fraction;
                    try { assigner.SetFraction(g, draft); }
                    finally
                    {
                        FractionChanged++;
                        Plugin.Log.LogInfo($"IFZCOOP side {g.Id} Fraktion {before} -> {g.Fraction} ({draft.name})");
                    }
                }
            }

            // 2. Zugehörigkeiten wie beim Laden (GroupBuilder: AffiliationProvider.Deserialize)
            if (AffDiffers(g, s))
            {
                g.AffiliationProvider.Deserialize(s.Affs);
                AffChanged++;
                Plugin.Log.LogInfo($"IFZCOOP side {g.Id} Zugehörigkeit -> {AffText(s.Affs)}");
            }

            // 3. Einwanderer-Zustand (nur Felder und die Anzeige; Annehmen selbst macht der Host)
            if (g.ImmigrantsState != s.Imm || g.ImmigrantsTransmissionPlayed != s.Played)
            {
                bool accepted = s.Imm == ImmigrantsState.Accepted && g.ImmigrantsState != ImmigrantsState.Accepted;
                g.ImmigrantsState = s.Imm;
                g.ImmigrantsTransmissionPlayed = s.Played;
                if (accepted)
                {
                    // wie ImmigrantsController.AcceptSpecificImmigrants, ohne Verlauf, Erfolge und Funk-Folgen
                    g.FogOfWarDetectable?.DisableHidingInFog();
                    if (g.PathDrawer != null) g.PathDrawer.Draw = true;
                }
                ImmChanged++;
            }

            // 4. Zustand grau/normal (wie GroupBuilder.RestoreState, nur Idle und Inactive)
            var cur = g.StateMachine?.CurrentState;
            if (cur != null && cur.Type != s.State && IsRestorable(s.State) && IsRestorable(cur.Type))
            {
                var state = g.StateMachine.GetState(s.State);
                if (state != null)
                {
                    g.StateMachine.SetState(state.GetType());
                    try { g.Pointer?.UpdateState(g); } catch { }
                    StateChanged++;
                }
            }
        }

        private static bool IsRestorable(StateType t) => t == StateType.Idle || t == StateType.Inactive;

        private static string AffText(FractionAffiliation[] affs) =>
            string.Join(",", affs.Select(a => a.Fraction + ":" + a.Affiliation));

        /// <summary>Prefix ImmigrantsController.StartBroadcastInviteSystem: beim Gast an den Host. false = hier nicht ausführen.</summary>
        public bool GuestInvite()
        {
            if (!GuestCanAsk)
                return true;
            _session.SendToHost(Protocol.Build(Msg.GroupSide, w => w.Write((byte)Op.Invite)));
            InvitesSent++;
            _session.ShowNotice("Einladung an den Host geschickt ...");
            return false;
        }

        // ================================================================ Story-Kamera: Host

        private static int _storyDepth;
        private static string _ctx;

        private sealed class Frame
        {
            public bool Counted;
            public string PrevCtx;
        }

        private static StorySync Current => Plugin.Instance?.Session?.Story;

        private static Frame Enter(string ctx)
        {
            var me = Current;
            var f = new Frame { PrevCtx = _ctx };
            if (me != null && me._session.Role == Role.Host)
            {
                f.Counted = true;
                _storyDepth++;
                _ctx = ctx;
            }
            return f;
        }

        private static void Leave(Frame f)
        {
            if (f == null || !f.Counted)
                return;
            if (_storyDepth > 0) _storyDepth--;
            _ctx = f.PrevCtx;
        }

        private void SendCue(CueKind kind, string id, Vector3 pos)
        {
            if (!HostWithGuests || !GameAccess.IsGameStarted)
                return;
            _session.BroadcastToGuests(Protocol.Build(Msg.StoryCue, w =>
            {
                w.Write((byte)kind);
                w.Write(id ?? "");
                w.Write(pos.x);
                w.Write(pos.y);
                w.Write(pos.z);
            }));
            CuesSent++;
            LastCue = CueText(kind, id, pos);
            Plugin.Log.LogInfo("IFZCOOP story cue gesendet " + LastCue);
        }

        private static string CueText(CueKind kind, string id, Vector3 pos) =>
            $"{kind}:{(string.IsNullOrEmpty(id) ? "-" : id)}@{pos.x.ToString("0", CultureInfo.InvariantCulture)},{pos.z.ToString("0", CultureInfo.InvariantCulture)}";

        // EventsSystemController.RunAction(ActionDraft actionDraft)
        private static void RunActionPrefix(ActionDraft actionDraft, out Frame __state)
            => __state = Enter("action:" + (actionDraft != null ? actionDraft.actionType.ToString() : "?"));

        private static void RunActionFinalizer(ActionDraft actionDraft, Frame __state, Exception __exception)
        {
            Leave(__state);
            if (__state == null || !__state.Counted || __exception != null || actionDraft == null)
                return;
            var me = Current;
            if (me == null)
                return;
            try
            {
                switch (actionDraft.actionType)
                {
                    case ActionType.PlayAudio:
                        if (!string.IsNullOrEmpty(actionDraft.audioFileName))
                            me.SendCue(CueKind.Audio, actionDraft.audioFileName, Vector3.zero);
                        break;
                    case ActionType.FollowMarkerByCamera:
                        var marker = GameAccess.Game<EventsSystemController>()?.MarkersController?.GetMarkerById(actionDraft.markerId);
                        if (marker != null)
                            me.SendCue(CueKind.Follow, actionDraft.markerId, marker.transform.position);
                        break;
                }
            }
            catch (Exception e) { Plugin.Log.LogDebug("Story-Aktion melden: " + e.Message); }
        }

        // EventsSystemController.CenterCameraOnMarker(string markerId) – Funk-Funktion
        private static void MarkerPrefix(string markerId, out Frame __state) => __state = Enter("marker:" + markerId);

        // EventsSystemController.CenterCameraOnGroup(string groupId) – Funk-Funktion (groupId ist eine Hilfs-ID)
        private static void GroupPrefix(string groupId, out Frame __state)
        {
            string id = groupId;
            try
            {
                var groups = GameAccess.Game<GroupsController>()?.Groups;
                if (groups != null)
                    foreach (var g in groups)
                        if (g != null && g.HasId(groupId)) { id = g.Id; break; }
            }
            catch { }
            __state = Enter("group:" + id);
        }

        // GameConsoleCommandHandler.CenterAtImmigrants() / CenterCameraOnObject(ISearchableObject)
        private static void ImmigrantsCamPrefix(out Frame __state) => __state = Enter("immigrants");

        private static void ObjectCamPrefix(out Frame __state) => __state = Enter(_ctx ?? "object");

        private static void StoryFinalizer(Frame __state) => Leave(__state);

        // GameCamera.CenterOnPosition(Vector3 targetPos, …): während einer Story-Methode mitschicken
        private static void CenterPostfix(Vector3 targetPos)
        {
            if (_storyDepth <= 0)
                return;
            try { Current?.SendCue(CueKind.Center, _ctx, targetPos); }
            catch (Exception e) { Plugin.Log.LogDebug("Story-Kamera melden: " + e.Message); }
        }

        // ImmigrantsController.StartBroadcastInviteSystem()
        private static bool InvitePrefix(ref bool __result)
        {
            var me = Current;
            if (me == null || me.GuestInvite())
                return true;
            __result = true;
            return false;
        }

        // ================================================================ Story-Kamera: Gast

        public void GuestOnCue(BinaryReader r)
        {
            var kind = (CueKind)r.ReadByte();
            string id = r.ReadString();
            var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (kind == CueKind.Radio)
            {
                GuestOnRadio(id);
                return;
            }
            CuesReceived++;
            LastCue = CueText(kind, id, pos);
            if (!GameAccess.IsGameStarted)
                return;
            UnitSync.NetApplying = true;
            try
            {
                switch (kind)
                {
                    case CueKind.Audio:
                    {
                        var h = GameAccess.Game<SoundEffectsHandler>() ?? UnityEngine.Object.FindObjectOfType<SoundEffectsHandler>();
                        if (h == null) return;
                        h.PlaySfxAudioByName(id);
                        CuesApplied++;
                        break;
                    }
                    case CueKind.Center:
                    case CueKind.Follow:
                    {
                        if (!CameraEnabled) { CamSkipped++; return; }
                        var cam = GameCamera.Active;
                        if (cam == null) return;
                        // Folgen (Follow) wird beim Gast NICHT nachgebaut: FollowMarkerId hängt die Kamera an den Marker, bis
                        // irgendeine Kamerabewegung es beendet (TmsCamera.TranslateXZ/CenterOnPosition → StopFollowingObject);
                        // bei einem bleibenden Marker (marker_firsthq) zieht sie den Gast jedes Bild zurück. Der Gast fährt einmal
                        // an die Stelle des Hosts. Ohne Sperre: die Sperre CenterCamera würde nur über die XOR-Freigabe des Spiels
                        // wieder gelöst (siehe UiLocks).
                        cam.CenterOnObjectWithCamZoom(new Vector3(pos.x, 0f, pos.z), blockCamMovement: false);
                        CuesApplied++;
                        break;
                    }
                }
                Plugin.Log.LogInfo("IFZCOOP story cue " + LastCue + (CameraEnabled ? "" : " (Kamera aus)"));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Story-Signal: " + e.Message); }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        // ================================================================ Funk: Einwanderer-Merkmale und Startzeile
        //
        // Einwanderer-Funksprüche (dilemma_immigrants_spotted_*, Einladen-Ereignisse mit DrawPosAndNegCharacteristics) haben
        // eine Startzeile ohne Antworten. Erst TransmissionManager.GetNextDialogueOptionIdsToCombine (Merkmale der Gruppe aus
        // ImmigrantsController.CurrentGroupTransmissionStartedFor bzw. ausgewürfelt) und HandleNextDialogueOptionsAnswersCombine
        // hängen die Merkmal-Zeilen und damit die Antworten an die Startzeile. Beim Gast ist CurrentGroupTransmissionStartedFor
        // leer (ImmigrantsController gesperrt), die Startzeile (RandomStartOption) außerdem eigens ausgewürfelt. Dann hat die
        // gespielte Startzeile keine Antwort, DialogueManager.LinePlayed ruft End() und der Funkspruch ist beim Gast sofort weg.
        // Host: vor HandleNextDialogueOptionsAnswersCombine Startzeile, Merkmale (JSON) und Einwanderer-ID an die Gäste
        // (StoryCue Art 4; geht vor der ersten Zeile raus, die RadioSync schickt). Gast: übernimmt Startzeile und Merkmale statt
        // selbst zu würfeln und lässt das Spiel die Kette bauen wie beim Host. Kommt die Angabe erst danach, baut er sie nach.

        private const char RadioSep = '\u001f';
        private const float RadioInfoMaxAge = 30f;

        private sealed class RadioInfo
        {
            public string Dialog, Start, Imm;
            public GroupCharacteristics Chars;
            public float Received;
        }

        private RadioInfo _radio;
        private Dialogue _radioWaiting;
        private static bool _radioBypass;

        private static readonly AccessTools.FieldRef<TransmissionManager, DialogueManager> TmDialogueField =
            AccessTools.FieldRefAccess<TransmissionManager, DialogueManager>("_dialogueManager");
        private static readonly AccessTools.FieldRef<TransmissionManager, List<string>> TmCombineField =
            AccessTools.FieldRefAccess<TransmissionManager, List<string>>("_nextDialogueOptionIdsToCombine");
        private static readonly System.Reflection.MethodInfo TmGetNextMethod =
            AccessTools.Method(typeof(TransmissionManager), "GetNextDialogueOptionIdsToCombine", new[] { typeof(Dialogue) });
        private static readonly System.Reflection.MethodInfo TmCombineMethod =
            AccessTools.Method(typeof(TransmissionManager), "HandleNextDialogueOptionsAnswersCombine", new[] { typeof(Dialogue) });

        public int RadioSent { get; private set; }
        public int RadioReceived { get; private set; }
        public int RadioApplied { get; private set; }
        public int RadioLate { get; private set; }
        public int RadioWaited { get; private set; }

        private static bool IsOutgoingDialogue(string id) => id != null && id.StartsWith("outtrans", StringComparison.Ordinal);

        // TransmissionManager.HandleNextDialogueOptionsAnswersCombine(Dialogue dialogue) – Host: Angaben an die Gäste
        private static void CombinePrefix(TransmissionManager __instance, Dialogue dialogue)
        {
            var me = Current;
            if (me == null || dialogue == null || !me.HostWithGuests || IsOutgoingDialogue(dialogue.ID))
                return;
            try
            {
                var gc = __instance.CurrentGroupCharacteristics;
                string chars = gc != null && gc.CharacteristicIds != null && gc.CharacteristicIds.Count > 0
                    ? JsonConvert.SerializeObject(gc, GameJson.DefaultSettings) : "";
                string imm = GameAccess.Game<ImmigrantsController>()?.CurrentImmigrantsId ?? "";
                string packed = string.Join(RadioSep.ToString(), new[] { dialogue.ID, dialogue.StartingLine?.ID ?? "", imm, chars });
                me._session.BroadcastToGuests(Protocol.Build(Msg.StoryCue, w =>
                {
                    w.Write((byte)CueKind.Radio);
                    w.Write(packed);
                    w.Write(0f);
                    w.Write(0f);
                    w.Write(0f);
                }));
                me.RadioSent++;
                Plugin.Log.LogInfo($"IFZCOOP story radio {dialogue.ID} start={dialogue.StartingLine?.ID} merkmale={(gc?.CharacteristicIds != null ? string.Join(",", gc.CharacteristicIds) : "-")}");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Funk-Merkmale senden: " + e.Message); }
        }

        // TransmissionManager.GetNextDialogueOptionIdsToCombine(Dialogue dialogue) – Gast: nicht selbst würfeln, Host-Angaben nehmen
        private static bool OptionsPrefix(TransmissionManager __instance, Dialogue dialogue)
        {
            var me = Current;
            if (_radioBypass || me == null || dialogue == null || !me._session.GuestControlsLocked || IsOutgoingDialogue(dialogue.ID))
                return true;
            try
            {
                var info = me._radio;
                if (info != null && info.Dialog == dialogue.ID && Time.realtimeSinceStartup - info.Received <= RadioInfoMaxAge)
                {
                    me._radio = null; // einmal verbrauchen: derselbe Funkspruch kann später mit anderer Gruppe wiederkommen
                    me._radioWaiting = null;
                    me.Prepare(__instance, dialogue, info);
                    me.RadioApplied++;
                    return info.Chars != null; // ohne Merkmale gibt es nichts zu verketten
                }
                me._radioWaiting = dialogue; // Angaben des Hosts kommen gleich (vor seiner ersten Zeile)
                me.RadioWaited++;
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Funk-Merkmale anwenden: " + e.Message);
                return true;
            }
        }

        private void Prepare(TransmissionManager tm, Dialogue dialogue, RadioInfo info)
        {
            if (!string.IsNullOrEmpty(info.Start) && dialogue.ContainsLine(info.Start))
                dialogue.SetStartingLine(dialogue.GetLine(info.Start));
            tm.CurrentGroupCharacteristics = info.Chars;
            if (info.Chars != null)
                dialogue.SetRelatedGroupCharacteristics(info.Chars);
            try { GameAccess.Game<ImmigrantsController>()?.SetCurrentId(info.Imm); } catch { }
        }

        private void GuestOnRadio(string packed)
        {
            RadioReceived++;
            var parts = (packed ?? "").Split(RadioSep);
            if (parts.Length < 4 || !_session.GuestControlsLocked)
                return;
            var info = new RadioInfo { Dialog = parts[0], Start = parts[1], Imm = parts[2], Received = Time.realtimeSinceStartup };
            if (parts[3].Length > 0)
            {
                try { info.Chars = JsonConvert.DeserializeObject<GroupCharacteristics>(parts[3], GameJson.DefaultSettings); }
                catch (Exception e) { Plugin.Log.LogWarning("Funk-Merkmale nicht lesbar: " + e.Message); }
            }
            Plugin.Log.LogInfo($"IFZCOOP story radio (vom Host) {info.Dialog} start={info.Start} merkmale={(info.Chars?.CharacteristicIds != null ? string.Join(",", info.Chars.CharacteristicIds) : "-")}");

            var tm = UnityEngine.Object.FindFirstObjectByType<TransmissionManager>();
            var waiting = _radioWaiting;
            var current = tm != null ? TmDialogueField(tm)?.CurrentDialogue : null;
            if (tm == null || waiting == null || !ReferenceEquals(current, waiting) || waiting.ID != info.Dialog)
            {
                _radio = info; // Gast zeigt den Funkspruch erst noch an: OptionsPrefix übernimmt
                return;
            }

            // Gast war schneller (z. B. Wolke angeklickt): Kette jetzt so bauen, wie es das Spiel beim Host getan hat.
            // Die Startzeile des Hosts ist noch unverändert (OptionsPrefix hat beim Gast nichts verkettet).
            _radioWaiting = null;
            try
            {
                Prepare(tm, waiting, info);
                if (info.Chars != null && TmGetNextMethod != null && TmCombineMethod != null)
                {
                    TmCombineField(tm)?.Clear();
                    _radioBypass = true;
                    try
                    {
                        TmGetNextMethod.Invoke(tm, new object[] { waiting });
                        TmCombineMethod.Invoke(tm, new object[] { waiting });
                    }
                    finally { _radioBypass = false; }
                }
                RadioApplied++;
                RadioLate++;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Funk-Merkmale nachträglich: " + e); }
        }

        // ================================================================ Haken

        public static void ApplyPatches(Harmony harmony)
        {
            int ok = 0, missing = 0;
            void P(Type type, string method, Type[] args, string prefix, string postfix = null, string finalizer = null)
            {
                try
                {
                    var target = args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args);
                    if (target == null) { missing++; Plugin.Log.LogWarning($"IFZCOOP story hook fehlt: {type.Name}.{method}"); return; }
                    harmony.Patch(target,
                        prefix: prefix == null ? null : new HarmonyMethod(typeof(StorySync), prefix),
                        postfix: postfix == null ? null : new HarmonyMethod(typeof(StorySync), postfix),
                        finalizer: finalizer == null ? null : new HarmonyMethod(typeof(StorySync), finalizer));
                    ok++;
                }
                catch (Exception e) { missing++; Plugin.Log.LogWarning($"IFZCOOP story hook {type.Name}.{method} nicht gesetzt: {e.Message}"); }
            }
            var esc = typeof(EventsSystemController);
            P(esc, "RunAction", new[] { typeof(ActionDraft) }, nameof(RunActionPrefix), null, nameof(RunActionFinalizer));
            P(esc, "CenterCameraOnMarker", new[] { typeof(string) }, nameof(MarkerPrefix), null, nameof(StoryFinalizer));
            P(esc, "CenterCameraOnGroup", new[] { typeof(string) }, nameof(GroupPrefix), null, nameof(StoryFinalizer));
            P(typeof(Commands.GameConsoleCommandHandler), "CenterAtImmigrants", Type.EmptyTypes, nameof(ImmigrantsCamPrefix), null, nameof(StoryFinalizer));
            P(typeof(Commands.GameConsoleCommandHandler), "CenterCameraOnObject", null, nameof(ObjectCamPrefix), null, nameof(StoryFinalizer));
            P(typeof(GameCamera), "CenterOnPosition", null, null, nameof(CenterPostfix));
            P(typeof(ImmigrantsController), "StartBroadcastInviteSystem", Type.EmptyTypes, nameof(InvitePrefix));
            P(typeof(TransmissionManager), "HandleNextDialogueOptionsAnswersCombine", new[] { typeof(Dialogue) }, nameof(CombinePrefix));
            P(typeof(TransmissionManager), "GetNextDialogueOptionIdsToCombine", new[] { typeof(Dialogue) }, nameof(OptionsPrefix));
            Plugin.Log.LogInfo($"Story-Abgleich: {ok} Haken gesetzt, {missing} fehlen");
        }

        // ================================================================ Status

        public static IEnumerable<string> StatusLines()
        {
            var list = NpcGroups().Select(Take).OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
            uint hash = 2166136261;
            foreach (var s in list)
                foreach (char ch in s.Id + s.Sig())
                    hash = (hash ^ ch) * 16777619;
            yield return $"groupsidecount n={list.Count} hash={hash:x8}";
            foreach (var s in list)
                yield return $"groupside {s.Id} f={s.Frac} a={string.Join(",", s.Affs.Select(a => a.Fraction + ":" + a.Affiliation))} imm={s.Imm}{(s.Played ? "+" : "")} st={s.State}";
        }

        public string StatusLine()
        {
            var inv = CultureInfo.InvariantCulture;
            string cam = "-", pivot = "-";
            try
            {
                var c = GameCamera.Active;
                if (c != null)
                {
                    var p = c.GetPositionOnYEqualsAndVieport(0f, new Vector3(0.5f, 0.5f, 0f));
                    cam = p.x.ToString("0", inv) + "," + p.z.ToString("0", inv);
                    var t = c.transform.position;
                    pivot = t.x.ToString("0", inv) + "," + t.z.ToString("0", inv);
                }
            }
            catch { }
            return $"story sidesSent={SidesSent} sidesReceived={SidesReceived} fractionChanged={FractionChanged} affChanged={AffChanged} immChanged={ImmChanged} stateChanged={StateChanged} applyFailed={ApplyFailed} stuck={Stuck} pending={Pending}"
                + $" invitesSent={InvitesSent} invitesDone={InvitesDone} cuesSent={CuesSent} cuesReceived={CuesReceived} cuesApplied={CuesApplied} camSkipped={CamSkipped} camOn={(CameraEnabled ? 1 : 0)}"
                + $" radioSent={RadioSent} radioReceived={RadioReceived} radioApplied={RadioApplied} radioLate={RadioLate} radioWaited={RadioWaited}"
                + $" lastCue={LastCue} camCenter={cam} camPivot={pivot}";
        }

        // ================================================================ Testbefehle (QaDriver)

        private static Group QaGroup(string key)
        {
            var groups = GameAccess.Game<GroupsController>()?.Groups;
            if (groups == null || groups.Count == 0)
                return null;
            if (key == "last")
                return groups[groups.Count - 1];
            if (key == "imm")
                return groups.LastOrDefault(g => g != null && g.Fraction == Fraction.Immigrants);
            return UnitSync.FindGroupPublic(key);
        }

        private static ActionDraft QaAction(ActionType t)
        {
            var d = ScriptableObject.CreateInstance<ActionDraft>();
            d.actionType = t;
            return d;
        }

        /// <summary>
        /// side &lt;id|last|imm&gt; fraction &lt;Fraction&gt; | aff &lt;Fraction&gt; &lt;Affiliation&gt; | state Idle|Inactive   (Host)
        /// immig spawn [n] | accept|decline|hostile|arrive|spotted &lt;id|imm&gt; | invite      (invite auch beim Gast; spotted beim Gast = Sperrprobe)
        /// cue marker [id] | group &lt;id&gt; | follow [id] | audio [name] | pos x z (pos = keine Story, darf nicht gesendet werden)   (Host)
        /// storycam 0|1   (Gast)
        /// </summary>
        public string Qa(string[] a)
        {
            var inv = CultureInfo.InvariantCulture;
            var ic = GameAccess.Game<ImmigrantsController>();
            var esc = GameAccess.Game<EventsSystemController>();
            var hq = Controllers.HqController.MainHeadquarter;
            var hqPos = hq != null ? hq.transform.position : Vector3.zero;
            switch (a[0].ToLowerInvariant())
            {
                case "side":
                {
                    var g = QaGroup(a.Length > 1 ? a[1] : "last");
                    if (g == null) return "keine Gruppe";
                    switch (a.Length > 2 ? a[2] : "")
                    {
                        case "fraction":
                        {
                            // wie EventsSystemController.ChangeFraction; ohne Vorlage dieser Fraktion (z. B. Survivors) nicht
                            var f = (Fraction)Enum.Parse(typeof(Fraction), a[3], true);
                            if (JutsuGamesConfig.Runtime.Configs.GetConfig<GroupsConfig>()?.GetDraftByFraction(f) == null)
                                return "keine Vorlage für Fraktion " + f;
                            g.ChangeFraction(f);
                            break;
                        }
                        case "aff": g.ChangeAffiliation((Fraction)Enum.Parse(typeof(Fraction), a[3], true), (Affiliation)Enum.Parse(typeof(Affiliation), a[4], true)); break;
                        case "state":
                        {
                            var st = g.StateMachine.GetState((StateType)Enum.Parse(typeof(StateType), a[3], true));
                            if (st != null) g.StateMachine.SetState(st.GetType());
                            break;
                        }
                    }
                    _nextSend = 0f;
                    var s = Take(g);
                    return $"Gruppe {g.Id} {g.Fraction} d={s.Draft} a={AffText(s.Affs)} st={s.State}";
                }
                case "immig":
                {
                    if (ic == null) return "kein ImmigrantsController";
                    string sub = a.Length > 1 ? a[1] : "";
                    if (sub == "spawn")
                    {
                        // "immig spawn [n] [Abstand]": weit genug weg, sonst sind Angenommene sofort am HQ und schon Bürger
                        int n = a.Length > 2 ? int.Parse(a[2], inv) : 4;
                        float dist = a.Length > 3 ? float.Parse(a[3], inv) : 250f;
                        var ng = ic.ImmigrantsSpawner.SpawnImmigrantsAt(hqPos + new Vector3(dist, 0f, 0f), n);
                        return ng != null ? "Gruppe " + ng.Id + " " + ng.Fraction : "keine Gruppe";
                    }
                    if (sub == "invite")
                        return "Einladen -> " + ic.StartBroadcastInviteSystem() + " (Gast: an Host " + InvitesSent + ")";
                    var g = QaGroup(a.Length > 2 ? a[2] : "imm");
                    if (g == null) return "keine Gruppe";
                    switch (sub)
                    {
                        case "accept": ic.AcceptSpecificImmigrants(g); break;
                        case "decline": ic.DeclineSpecificImmigrant(g); break;
                        case "hostile": ic.ChangeSpecificGroupAffiliation(g, Fraction.Player, Affiliation.Hostile); break;
                        case "arrive": GameAccess.Game<SignalBus>()?.Fire(new ImmigrantsReachedHqSignal(g)); break;
                        case "spotted": GameAccess.Game<SignalBus>()?.Fire(new ImmigrantsSpottedSignal(g)); break;
                        default: return "unbekannt: " + sub;
                    }
                    _nextSend = 0f;
                    return $"Gruppe {g.Id} {g.Fraction} imm={g.ImmigrantsState} a={AffText(Take(g).Affs)}";
                }
                case "cue":
                {
                    if (esc == null) return "kein EventsSystemController";
                    string sub = a.Length > 1 ? a[1] : "marker";
                    switch (sub)
                    {
                        case "marker":
                        case "follow":
                        {
                            string id = a.Length > 2 ? a[2] : null;
                            if (id == null)
                                foreach (var o in esc.MarkersController.Objects)
                                    if (o is EventMarker em && em != null) { id = em.Id; break; }
                            if (id == null) return "kein Marker";
                            if (sub == "marker")
                                return "Kamera auf Marker " + id + " -> " + esc.CenterCameraOnMarker(id);
                            var d = QaAction(ActionType.FollowMarkerByCamera);
                            d.markerId = id;
                            esc.RunAction(d);
                            return "Kamera folgt Marker " + id;
                        }
                        case "group":
                            return "Kamera auf Gruppe " + a[2] + " -> " + esc.CenterCameraOnGroup(a[2]);
                        case "audio":
                        {
                            var d = QaAction(ActionType.PlayAudio);
                            d.audioFileName = a.Length > 2 ? a[2] : "UI_ObjectiveComplete";
                            esc.RunAction(d);
                            return "Ton " + d.audioFileName;
                        }
                        case "pos":
                            GameCamera.Active.CenterOnObjectWithCamZoom(new Vector3(float.Parse(a[2], inv), 0f, float.Parse(a[3], inv)));
                            return "Kamera (keine Story) " + a[2] + "," + a[3];
                    }
                    return "unbekannt: " + sub;
                }
                case "storycam":
                    CameraEnabled = a.Length < 2 || a[1] != "0";
                    return "StoryKamera=" + CameraEnabled;
            }
            return "unbekannt";
        }
    }
}
