using System;
using System.Collections.Generic;
using System.IO;
using Controllers.Time;
using IfzCoop.Game;
using IfzCoop.Net;
using IfzCoop.Sync;
using Steamworks;
using UnityEngine;

namespace IfzCoop
{
    internal enum Role { None, Host, Guest }

    internal enum GuestState { Idle, JoiningLobby, WaitingWelcome, ReceivingSave, Loading, InGame }

    internal sealed class Peer
    {
        public CSteamID Id;
        public string Name = "?";
        public float LastHeard;
        public bool Welcomed;
        public bool AwaitingLoad;  // hat einen Spielstand bekommen, Ready steht noch aus
        public int LastTransfer;
        public double RttMs = -1;
    }

    /// <summary>
    /// Meilenstein 1: Verbindung über eine Steam-Lobby, Übergabe des Host-Spielstands an den Gast,
    /// gemeinsame Spielzeit (Tempo und Pause vom Host). Die Simulation läuft danach auf beiden
    /// Rechnern getrennt weiter; Befehle des Gastes werden noch nicht übertragen (Meilenstein 2).
    /// </summary>
    internal sealed class Session
    {
        private const string HostSaveDir = "ifzcoop-host";
        private const string GuestSaveDir = "ifzcoop-guest";
        private const string LobbyKey = "ifzcoop";
        private const float PingInterval = 1f;
        private const float TimeStateInterval = 1f;
        private const float PeerTimeout = 20f;
        private const float LoadingTimeout = 180f;

        private readonly SteamTransport _steam = new SteamTransport();
        private TcpTransport _tcp;
        private ITransport _transport;
        private readonly Dictionary<ulong, Peer> _peers = new Dictionary<ulong, Peer>();

        /// <summary>Direktverbindung (TCP) statt Steam.</summary>
        public bool Direct { get; private set; }

        /// <summary>Eigene Kennung: Steam-ID, bei Direktverbindung die vom Host vergebene.</summary>
        public CSteamID MyId { get; private set; } = CSteamID.Nil;

        /// <summary>Anzeigename (Steam-Name oder -ifzcoop-name).</summary>
        public string MyName
        {
            get
            {
                if (Plugin.NameOverride != null) return Plugin.NameOverride;
                string own = Plugin.PlayerName?.Value;
                if ((Direct || !SteamReady) && !string.IsNullOrWhiteSpace(own)) return own.Trim();
                return SteamReady ? SteamFriends.GetPersonaName() : (string.IsNullOrWhiteSpace(own) ? "Spieler" : own.Trim());
            }
        }

        /// <summary>Name kommt von Steam (Eingabe im Menü gesperrt).</summary>
        public bool NameFromSteam => SteamReady && !Direct;

        private bool _steamHooked;
        private Callback<GameLobbyJoinRequested_t> _cbJoinRequested;
        private Callback<GameRichPresenceJoinRequested_t> _cbRichJoin;
        private Callback<LobbyEnter_t> _cbLobbyEnter;
        private Callback<LobbyChatUpdate_t> _cbLobbyChat;
        private CallResult<LobbyCreated_t> _crLobbyCreated;

        private float _nextPing;
        private float _nextTimeState;

        // Host
        private bool _saving;
        private readonly List<CSteamID> _pendingSnapshot = new List<CSteamID>();
        private readonly List<CSteamID> _waitingForHq = new List<CSteamID>();  // Gäste, die auf die HQ-Wahl des Hosts warten
        private readonly List<CSteamID> _waitingForGame = new List<CSteamID>(); // Gäste aus der Lobby im Hauptmenü: warten, bis der Host im Spiel ist

        // Versus aus der Hauptmenü-Lobby (KONZEPT-MENUE M7): Host eröffnet bei der HQ-Wahl die Versus-Runde, schickt den
        // wartenden Gästen VsSwitch und schließt danach die Koop-Lobby still.
        private List<CSteamID> _vsHandoverTargets;
        private float _vsHandoverSentAt = -1f;
        private string _directHost;                 // Gast: Adresse des Hosts bei der Direktverbindung (für VsSwitch)

        /// <summary>Koop-Host darf neben sich eine Versus-Runde eröffnen (nur während der Übergabe).</summary>
        public bool VersusHandoverActive => _vsHandoverTargets != null;
        private float _hqChosenAt = -1f;
        private int _transferCounter;
        private bool _holdingTime;
        private float _heldSpeed = 1f;
        private float _lastBroadcastSpeed = -1f;

        // Gast
        private float _hostLastHeard;
        private int _transferId;
        private byte[] _transferBuffer;
        private byte[] _transferHash;
        private int _transferReceived;
        private bool _sawGameStopped;
        private float _loadingSince;
        private float _hostSpeed = -1f;
        private double _hostRttMs = -1;
        private string _pendingCommandLineLobby;

        public RadioSync Radio { get; }
        public OrderSync Orders { get; }
        public PresenceSync Presence { get; }
        public UnitSync Units { get; }
        public CombatSync Combat { get; }
        public SquadSync Squads { get; }
        public HqSync Hq { get; }
        public ScavengeSync Scavenge { get; }
        public HideoutSync Hideouts { get; }
        public StockSync Stock { get; }
        public ContainerSync Containers { get; }
        public GroundSync Ground { get; }
        public VehicleSync Vehicles { get; }
        public EventSync Events { get; }
        public WorldSync World { get; }
        public BuildingSync Buildings { get; }
        public PopulationSync Population { get; }
        public VirtualSync Virtuals { get; }
        public ChecksumSync Checksums { get; }
        public ExchangeSync Exchange { get; }
        public ExpeditionSync Expeditions { get; }
        public TraderSync Traders { get; }
        public WorkerPosSync WorkerPos { get; }
        public AreaWorkSync AreaWorks { get; }
        public ProductionSync Production { get; }
        public WarningSync Warnings { get; }
        public TowerWeaponSync TowerWeapons { get; }
        public StorySync Story { get; }
        public StructureSync Structures { get; }
        public SquadActionSync SquadActions { get; }
        public ZoneSync Zones { get; }

        /// <summary>Gast: Nummer der laufenden Spielstand-Übertragung (-1 = wartet auf den Host, ZoneSync).</summary>
        public int TransferId => _transferId;

        /// <summary>
        /// ZoneSync: Gast wartet ohne eigenes Spiel auf den Host (Umzug in eine andere Stadt, Hauptmenü, anderer Spielstand).
        /// Zustand „lädt“ (Zeitlimit fürs Laden, kein Spielgeschehen) mit ungültiger Übertragung: ein späteres „Ready“ zählt
        /// beim Host nicht. Der nächste Spielstand des Hosts (SaveBegin) beendet das Warten.
        /// </summary>
        public void GuestAwaitHost(string status)
        {
            if (Role != Role.Guest)
                return;
            GuestState = GuestState.Loading;
            _transferId = -1;
            _transferBuffer = null;
            _transferReceived = 0;
            _sawGameStopped = false;
            _loadingSince = float.PositiveInfinity;
            Status = status;
        }

        public BuildResSync BuildRes { get; }
        public SquadViewSync SquadViews { get; }
        public SquadQueueSync SquadQueue { get; }
        public DemolishSync Demolish { get; }
        public StatsSync Stats { get; }
        public ResStatsSync ResStats { get; }
        public PlayerColorSync PlayerColors { get; }
        public SquadStateSync SquadStates { get; }
        public WorkSync Works { get; }

        /// <summary>Gast: true, solange das Spielende des Hosts nachgespielt wird.</summary>
        public bool ApplyingGameOver { get; private set; }

        /// <summary>Wie oft die Spielzeit des Gastes auf die des Hosts gesetzt wurde.</summary>
        public int TimeCorrections { get; private set; }

        /// <summary>Kurze Meldung oben am Bildschirm (z. B. wer einen Funkspruch beantwortet hat).</summary>
        public string Notice { get; private set; }
        public float NoticeUntil { get; private set; }

        public Role Role { get; private set; } = Role.None;
        public GuestState GuestState { get; private set; } = GuestState.Idle;
        public CSteamID Lobby { get; private set; } = CSteamID.Nil;
        public CSteamID HostId { get; private set; } = CSteamID.Nil;
        public string Status { get; private set; } = "Bereit";
        public float TimeDriftSeconds { get; private set; }
        public float TransferProgress => _transferBuffer == null || _transferBuffer.Length == 0 ? 0f : (float)_transferReceived / _transferBuffer.Length;
        public IEnumerable<Peer> Peers => _peers.Values;

        private void BeginVersusHandover()
        {
            var vs = Versus.VersusSession.Instance;
            if (vs == null)
                return;
            _vsHandoverTargets = new List<CSteamID>(_waitingForGame);
            _waitingForGame.Clear();
            _vsHandoverSentAt = -1f;
            vs.AutoStartWhen = _vsHandoverTargets.Count;
            if (Direct) vs.StartHostDirect(Versus.VsProtocol.DefaultPort);
            else vs.StartHost();
            Plugin.Log.LogInfo($"IFZCOOP Lobby -> Versus: {_vsHandoverTargets.Count} Gast/Gäste wechseln");
            ShowNotice("Versus: die Runde wird eröffnet, deine Gäste wechseln hinein", 5f);
        }

        private void PumpVersusHandover(float now)
        {
            var vs = Versus.VersusSession.Instance;
            if (vs == null || vs.Role != Versus.VsRole.Host)
            {
                if (now - _vsHandoverSentAt > 30f && _vsHandoverSentAt > 0f || vs == null)
                {
                    _vsHandoverTargets = null;
                    Status = "Versus konnte nicht eröffnet werden: " + (vs?.Status ?? "-");
                }
                return;
            }
            if (_vsHandoverSentAt < 0f)
            {
                if (!vs.Direct && vs.Lobby == CSteamID.Nil)
                    return; // Steam-Lobby noch nicht fertig
                bool direct = vs.Direct;
                int port = Versus.VsProtocol.DefaultPort;
                ulong lobby = vs.Lobby.m_SteamID;
                var msg = Protocol.Build(Msg.VsSwitch, w => { w.Write(direct); w.Write(port); w.Write(lobby); });
                foreach (var t in _vsHandoverTargets) _transport.Send(t, msg);
                _transport.Flush();
                _vsHandoverSentAt = now;
                return;
            }
            if (now - _vsHandoverSentAt < 1.5f)
                return;
            // Koop-Lobby still schließen; Versus übernimmt (Rich Presence wieder auf die Versus-Lobby)
            _vsHandoverTargets = null;
            Leave("in die Versus-Runde gewechselt", sendBye: false, backToMenu: false);
            if (!vs.Direct && vs.Lobby != CSteamID.Nil && SteamReady)
                SteamFriends.SetRichPresence("connect", "+connect_lobby " + vs.Lobby.m_SteamID);
        }

        private bool _vsSwitchPending, _vsSwitchDirect;
        private int _vsSwitchPort;
        private ulong _vsSwitchLobby;

        /// <summary>Gast: Host wechselt in eine Versus-Runde. Ausgeführt im nächsten Update (nicht mitten im Empfang).</summary>
        private void GuestOnVsSwitch(BinaryReader r)
        {
            _vsSwitchDirect = r.ReadBoolean();
            _vsSwitchPort = r.ReadInt32();
            _vsSwitchLobby = r.ReadUInt64();
            _vsSwitchPending = true;
        }

        private void DoVsSwitch()
        {
            _vsSwitchPending = false;
            bool direct = _vsSwitchDirect;
            int port = _vsSwitchPort;
            ulong lobby = _vsSwitchLobby;
            string host = _directHost;
            Plugin.Log.LogInfo($"IFZCOOP Wechsel in die Versus-Runde (direkt={direct}, Port {port}, Lobby {lobby})");
            Leave("Wechsel in die Versus-Runde", sendBye: false, backToMenu: false);
            var vs = Versus.VersusSession.Instance;
            if (vs == null)
                return;
            if (direct && !string.IsNullOrEmpty(host)) vs.JoinDirect(host, port);
            else if (lobby != 0) vs.JoinLobbyById(lobby);
            ShowNotice("Versus: du trittst der Runde des Hosts bei", 5f);
        }

        /// <summary>Anzeige für die Kacheln im Hauptmenü/Esc-Menü: Zustand eines Gastes beim Host.</summary>
        public string PeerState(ulong id)
        {
            if (!_peers.TryGetValue(id, out var p) || !p.Welcomed)
                return "tritt bei";
            if (_waitingForGame.Contains(p.Id)) return "wartet";
            if (_waitingForHq.Contains(p.Id)) return "wartet auf HQ";
            if (p.AwaitingLoad) return "lädt";
            return "im Spiel";
        }
        public double HostRttMs => _hostRttMs;
        public bool SteamReady { get; private set; }

        /// <summary>Gast im laufenden Koop-Spiel: Tempo kommt nur noch vom Host.</summary>
        public bool GuestControlsLocked => Role == Role.Guest && GuestState == GuestState.InGame;

        public Session()
        {
            _steam.Received += OnReceived;
            _steam.SessionFailed += OnSessionFailed;
            _steam.AcceptPeer = AcceptPeer;
            _transport = _steam;
            _pendingCommandLineLobby = ReadConnectLobbyArgument();
            Radio = new RadioSync(this);
            Orders = new OrderSync(this);
            Presence = new PresenceSync(this);
            Units = new UnitSync(this);
            Combat = new CombatSync(this, Units);
            Squads = new SquadSync(this, Units);
            Units.Squads = Squads;
            Hq = new HqSync(this);
            Scavenge = new ScavengeSync(this);
            Hideouts = new HideoutSync(this);
            Stock = new StockSync(this);
            Containers = new ContainerSync(this);
            Ground = new GroundSync(this);
            Vehicles = new VehicleSync(this);
            Events = new EventSync(this);
            World = new WorldSync(this);
            Buildings = new BuildingSync(this);
            Population = new PopulationSync(this);
            Virtuals = new VirtualSync(this);
            Checksums = new ChecksumSync(this);
            Exchange = new ExchangeSync(this);
            Expeditions = new ExpeditionSync(this);
            Traders = new TraderSync(this);
            WorkerPos = new WorkerPosSync(this);
            AreaWorks = new AreaWorkSync(this);
            Production = new ProductionSync(this);
            Warnings = new WarningSync(this);
            TowerWeapons = new TowerWeaponSync(this);
            Story = new StorySync(this);
            Structures = new StructureSync(this);
            SquadActions = new SquadActionSync(this);
            Zones = new ZoneSync(this);
            BuildRes = new BuildResSync(this);
            SquadViews = new SquadViewSync(this);
            SquadQueue = new SquadQueueSync(this);
            Demolish = new DemolishSync(this);
            Stats = new StatsSync(this);
            ResStats = new ResStatsSync(this);
            PlayerColors = new PlayerColorSync(this);
            SquadStates = new SquadStateSync(this);
            Works = new WorkSync(this);
        }

        // ------------------------------------------------------------------ Spielende

        /// <summary>Postfix von GameOverController.PlayEndgameSequence beim Host.</summary>
        public void OnGameOverPlayed(int failReason, int fraction)
        {
            if (Role != Role.Host || ActiveGuestCount == 0)
                return;
            BroadcastToGuests(Protocol.Build(Msg.GameOver, w => { w.Write(failReason); w.Write(fraction); }));
            Plugin.Log.LogInfo($"IFZCOOP game over gesendet: {(Controllers.GameOverController.FailReason)failReason}");
        }

        private void ApplyGameOver(int failReason, int fraction)
        {
            var controller = GameAccess.Game<Controllers.GameOverController>();
            if (controller == null)
            {
                Plugin.Log.LogWarning("GameOverController nicht gefunden, Spielende des Hosts entfällt");
                return;
            }
            ApplyingGameOver = true;
            try
            {
                controller.PlayEndgameSequence((Controllers.GameOverController.FailReason)failReason, (Gameplay.Units.Fraction)fraction);
            }
            finally
            {
                ApplyingGameOver = false;
            }
        }

        // ------------------------------------------------------------------ Spielzeit

        /// <summary>Gast: Uhr-Tempo des Hosts (F7 „Uhrzeit einfrieren“ / „Uhr-Tempo“), 1 = normal, 0 = steht.</summary>
        public static float HostClockRate = 1f;
        private float _lastHostTotal = -1f, _lastHostAt;

        /// <summary>Uhr-Tempo des Hosts aus zwei Zeit-Meldungen: Uhr-Fortschritt / (Echtzeit · Spieltempo).</summary>
        private void EstimateHostClockRate(float hostTotal, float speed)
        {
            float now = Time.realtimeSinceStartup;
            float prev = _lastHostTotal, prevAt = _lastHostAt;
            _lastHostTotal = hostTotal;
            _lastHostAt = now;
            float dt = now - prevAt;
            if (prev < 0f || speed < 0.01f || dt < 0.3f || dt > 5f)
                return;
            float d = hostTotal - prev;
            if (d < 0f) return; // Uhr zurückgesetzt (Uhrzeit setzen)
            float r = d / (dt * speed);
            if (r > 21f) return; // Sprung (Uhrzeit gesetzt), kein Tempo
            if (r < 0.03f) r = 0f;
            else if (Mathf.Abs(r - 1f) < 0.08f) r = 1f;
            else r = Mathf.Round(r * 20f) / 20f;
            HostClockRate = r;
        }

        private static readonly System.Reflection.MethodInfo SetCurrentDayGtsMethod =
            HarmonyLib.AccessTools.Method(typeof(TimeController), "SetCurrentDayGts", new[] { typeof(float) });

        /// <summary>
        /// Setzt die Uhr des Gastes auf die des Hosts, sobald sie mehr als 1,5 Spielsekunden abweicht.
        /// Die Uhr driftet trotz gleichem Tempo, weil das Spiel pro Frame höchstens 0,1 s zählt (Ruckler).
        /// Zurück wird nur innerhalb derselben Stunde gesprungen, damit Stunden-Ereignisse (Essen,
        /// Autospeichern, Wetter) nicht doppelt auslösen.
        /// </summary>
        private void CorrectTime(int hostDay, float hostGts, int dayLength)
        {
            if (Mathf.Abs(TimeDriftSeconds) < 1.5f || SetCurrentDayGtsMethod == null || dayLength <= 0)
                return;
            var clock = GameAccess.Game<TimeController>();
            if (clock == null)
                return;

            if (TimeDriftSeconds < 0f)
            {
                if (hostDay != TimeController.Day)
                    return;
                int hostHour = (int)TimeController.GameplaySecondsToHour(hostGts, dayLength);
                if (hostHour != TimeController.HourInt)
                    return;
            }

            if (hostDay != TimeController.Day)
                clock.SetDay(hostDay);
            SetCurrentDayGtsMethod.Invoke(null, new object[] { hostGts });
            TimeCorrections++;
        }

        // ------------------------------------------------------------------ für die Sync-Module

        /// <summary>Gäste, die im Spiel sind (nicht mehr laden).</summary>
        public int ActiveGuestCount
        {
            get
            {
                int n = 0;
                foreach (var p in _peers.Values)
                    if (p.Welcomed && !p.AwaitingLoad) n++;
                return n;
            }
        }

        public void BroadcastToGuests(byte[] message, CSteamID except = default)
        {
            if (Role != Role.Host)
                return;
            foreach (var p in _peers.Values)
                if (p.Welcomed && !p.AwaitingLoad && p.Id != except) _transport.Send(p.Id, message);
        }

        /// <summary>An alle angenommenen Gäste, auch die in der Lobby im Hauptmenü.</summary>
        public void BroadcastToLobby(byte[] message)
        {
            if (Role != Role.Host)
                return;
            foreach (var p in _peers.Values)
                if (p.Welcomed) _transport.Send(p.Id, message);
        }

        public void SendTo(CSteamID peer, byte[] message)
        {
            _transport.Send(peer, message);
        }

        public void SendToHost(byte[] message)
        {
            if (Role == Role.Guest && HostId != CSteamID.Nil)
                _transport.Send(HostId, message);
        }

        // ================================================================ Speichern des Hosts

        private System.Threading.Tasks.Task<bool> _hostSaveTask;

        /// <summary>
        /// Postfix SaveHandler.SaveGame beim Host: Das Spiel steht beim Speichern kurz, der Gast wusste nicht warum
        /// (Fehlerbericht 2026-11-06). Jetzt bekommt er einen Hinweis und einen zweiten, wenn es weitergeht.
        /// </summary>
        public void HostOnSaveStarted(string directoryName, System.Threading.Tasks.Task<bool> task)
        {
            if (Role != Role.Host || ActiveGuestCount == 0 || directoryName == HostSaveDir)
                return;
            BroadcastToGuests(Protocol.Build(Msg.Notice, w => w.Write("Der Host speichert das Spiel – kurze Pause")));
            _hostSaveTask = task;
        }

        private void HostCheckSaveDone()
        {
            var t = _hostSaveTask;
            if (t == null || !t.IsCompleted)
                return;
            _hostSaveTask = null;
            bool ok = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && t.Result;
            BroadcastToGuests(Protocol.Build(Msg.Notice, w => w.Write(ok ? "Gespeichert – es geht weiter" : "Speichern beim Host fehlgeschlagen – es geht weiter")));
        }

        public void ShowNotice(string text, float seconds = 6f)
        {
            Notice = text;
            NoticeUntil = Time.realtimeSinceStartup + seconds;
            Plugin.Log.LogInfo("Hinweis: " + text);
        }

        /// <summary>Host: Einstellung „Wer darf antworten“ an alle Gäste.</summary>
        public void BroadcastSettings()
        {
            // an alle angenommenen Gäste, auch die in der Lobby im Hauptmenü (sie sehen die Mehrspieler-Einstellungen)
            if (Role != Role.Host)
                return;
            var msg = Radio.BuildSettings();
            foreach (var p in _peers.Values)
                if (p.Welcomed) _transport.Send(p.Id, msg);
        }

        // ------------------------------------------------------------------ Frame

        public void Update()
        {
            // Die Direktverbindung braucht Steam nicht.
            bool steam = EnsureSteam();
            if (!steam && !Direct)
                return;

            if (steam && _pendingCommandLineLobby != null && Role == Role.None)
            {
                var id = _pendingCommandLineLobby;
                _pendingCommandLineLobby = null;
                if (ulong.TryParse(id, out var lobby))
                    JoinLobby(new CSteamID(lobby));
            }

            _transport.Pump();
            if (_vsSwitchPending)
                DoVsSwitch();

            float now = Time.realtimeSinceStartup;
            if (Role != Role.None && now >= _nextPing)
            {
                _nextPing = now + PingInterval;
                var ping = Protocol.Build(Msg.Ping, w => w.Write(Now()));
                if (Role == Role.Host)
                    foreach (var p in _peers.Values) _transport.Send(p.Id, ping);
                else if (HostId != CSteamID.Nil)
                    _transport.Send(HostId, ping);
                CheckTimeouts(now);
            }

            if (Role == Role.Host)
                UpdateHost(now);
            else if (Role == Role.Guest)
                UpdateGuest(now);

            if (Role != Role.None)
            {
                Presence.Update();
                Sync.ExpeditionRange.Tick();
            }
            if (Role == Role.Host)
            {
                Units.HostUpdate();
                Combat.HostUpdate();
            }
            else if (Role == Role.Guest)
            {
                Units.GuestFrame();
                Vehicles.GuestFrame();
                Virtuals.GuestFrame();
                Traders.GuestFrame();
                WorkerPos.GuestFrame();
                Production.GuestFrame();
                Warnings.GuestFrame();
                TowerWeapons.GuestFrame();
                Story.GuestFrame();
                SquadActions.GuestFrame();
                Zones.GuestFrame();
                Buildings.GuestFrame();
                BuildRes.GuestFrame();
                SquadViews.GuestFrame();
                Events.GuestFrame();
                ResStats.GuestFrame();
                PlayerColors.GuestUpdate();
                Works.GuestFrame();
                Checksums.GuestUpdate();
            }
        }

        private void UpdateHost(float now)
        {
            if (_peers.Count > 0 && GameAccess.IsGameStarted && now >= _nextTimeState)
            {
                _nextTimeState = now + TimeStateInterval;
                var msg = Protocol.Build(Msg.TimeState, w =>
                {
                    w.Write(TimeController.Day);
                    w.Write(TimeController.CurrentDayGameplaySeconds);
                    w.Write(TimeController.SpeedMultiplier);
                    w.Write(TimeController.DayLengthInSeconds);
                });
                foreach (var p in _peers.Values)
                    if (p.Welcomed && !p.AwaitingLoad) _transport.Send(p.Id, msg);
            }

            // Lobby im Modus Versus: bei der HQ-Wahl in eine Versus-Runde wechseln statt Koop-Spielstand schicken
            if (_waitingForGame.Count > 0 && GameAccess.IsGameStarted && _vsHandoverTargets == null
                && Menu.LobbySettings.Mode == "versus" && Hq.HostChoosingHq)
                BeginVersusHandover();
            if (_vsHandoverTargets != null)
            {
                PumpVersusHandover(now);
                return;
            }

            // Lobby aus dem Hauptmenü: sobald der Host im Spiel ist (bei der HQ-Wahl übernimmt SendSnapshot das Warten).
            // Versus, neues Spiel ohne HQ (z. B. Vorlage-Stadt: Intro läuft noch, HQ-Wahl kommt gleich): warten statt Koop
            bool vsWaitForHqChoice = Menu.LobbySettings.Mode == "versus" && Controllers.HqController.MainHeadquarter == null;
            if (_waitingForGame.Count > 0 && GameAccess.IsGameStarted && !vsWaitForHqChoice)
            {
                if (Menu.LobbySettings.Mode == "versus" && !Hq.HostChoosingHq)
                {
                    const string text = "Versus geht nur mit einem neuen Spiel – dieser Spielstand wird zusammen im Koop gespielt";
                    ShowNotice(text, 8f);
                    BroadcastToLobby(Protocol.Build(Msg.Notice, w => w.Write(text)));
                }
                var targets = new List<CSteamID>(_waitingForGame);
                _waitingForGame.Clear();
                Plugin.Log.LogInfo($"IFZCOOP Host ist im Spiel, Spielstand an {targets.Count} Gast/Gäste aus der Lobby");
                SendSnapshot(targets);
            }

            // Spielstände, die auf die HQ-Wahl gewartet haben: kurz nach der Wahl schicken, damit die Folgen
            // der Wahl (HQ als Gebäude, Startkisten) schon im Spielstand stehen.
            if (_waitingForHq.Count > 0 && !Hq.HostChoosingHq)
            {
                if (_hqChosenAt < 0f)
                    _hqChosenAt = now;
                else if (now - _hqChosenAt > 2f)
                {
                    var targets = new List<CSteamID>(_waitingForHq);
                    _waitingForHq.Clear();
                    _hqChosenAt = -1f;
                    Plugin.Log.LogInfo($"IFZCOOP HQ gewählt, Spielstand an {targets.Count} wartende(n) Gast/Gäste");
                    SendSnapshot(targets);
                }
            }
            Hq.HostUpdate();
            Scavenge.HostUpdate();
            Hideouts.HostUpdate();
            Stock.HostUpdate();
            Containers.HostUpdate();
            Ground.HostUpdate();
            Vehicles.HostUpdate();
            Events.HostUpdate();
            World.HostUpdate();
            Buildings.HostUpdate();
            Population.HostUpdate();
            Virtuals.HostUpdate();
            Expeditions.HostUpdate();
            Traders.HostUpdate();
            WorkerPos.HostUpdate();
            AreaWorks.HostUpdate();
            Production.HostUpdate();
            Warnings.HostUpdate();
            TowerWeapons.HostUpdate();
            Story.HostUpdate();
            Structures.HostUpdate();
            SquadActions.HostUpdate();
            Zones.HostUpdate();
            BuildRes.HostUpdate();
            SquadViews.HostUpdate();
            SquadQueue.HostUpdate();
            Demolish.HostUpdate();
            Stats.HostUpdate();
            HostCheckSaveDone();
            ResStats.HostUpdate();
            PlayerColors.HostUpdate();
            SquadStates.HostUpdate();
            Works.HostUpdate();

            if (_holdingTime && !_saving && _pendingSnapshot.Count == 0)
            {
                bool anyLoading = false;
                foreach (var p in _peers.Values) anyLoading |= p.AwaitingLoad;
                if (!anyLoading)
                    ReleaseTime();
            }
        }

        private void UpdateGuest(float now)
        {
            if (GuestState == GuestState.InGame)
                Radio.Update();

            if (GuestState == GuestState.Loading)
            {
                if (!GameAccess.IsGameStarted)
                    _sawGameStopped = true;
                else if (_sawGameStopped)
                {
                    GuestState = GuestState.InGame;
                    OffsetGuestIds();
                    Status = "Im Koop-Spiel";
                    _transport.Send(HostId, Protocol.Build(Msg.Ready, w => w.Write(_transferId)));
                    Plugin.Log.LogInfo("IFZCOOP guest ready, transfer " + _transferId);
                    if (_hostSpeed >= 0f)
                        ApplyHostSpeed(_hostSpeed);
                }
                else if (now - _loadingSince > 30f)
                {
                    Status = "Lade Spielstand ... (dauert lange, Plugin-Log prüfen)";
                }
            }
        }

        private void CheckTimeouts(float now)
        {
            if (Role == Role.Host)
            {
                List<Peer> lost = null;
                foreach (var p in _peers.Values)
                {
                    float limit = p.AwaitingLoad ? LoadingTimeout : PeerTimeout;
                    if (now - p.LastHeard > limit)
                        (lost ??= new List<Peer>()).Add(p);
                }
                if (lost != null)
                    foreach (var p in lost) DropPeer(p.Id, "keine Antwort");
            }
            else if (Role == Role.Guest && HostId != CSteamID.Nil && GuestState != GuestState.JoiningLobby)
            {
                float limit = GuestState == GuestState.Loading || GuestState == GuestState.ReceivingSave ? LoadingTimeout : PeerTimeout;
                if (now - _hostLastHeard > limit)
                    Leave("Host antwortet nicht mehr");
            }
        }

        // ------------------------------------------------------------------ Steam

        private bool EnsureSteam()
        {
            if (!SteamReady)
            {
                try
                {
                    InteropHelp.TestIfAvailableClient();
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
                SteamReady = true;
            }

            if (!_steamHooked)
            {
                _steamHooked = true;
                _steam.Init();
                if (!Direct && Role == Role.None)
                    MyId = SteamUser.GetSteamID();
                _cbJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
                _cbRichJoin = Callback<GameRichPresenceJoinRequested_t>.Create(OnRichPresenceJoin);
                _cbLobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
                _cbLobbyChat = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
                _crLobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
                Plugin.Log.LogInfo("Steam bereit, Callbacks registriert. Ich bin " + SteamUser.GetSteamID().m_SteamID);
            }
            return true;
        }

        private bool AcceptPeer(CSteamID peer)
        {
            // Der Host nimmt jede Sitzung an (die Lobby-Mitgliederliste kann der ersten Nachricht
            // hinterherhinken); geprüft wird erst beim Hello.
            if (Role == Role.Host)
                return Lobby != CSteamID.Nil;
            if (Role == Role.Guest)
                return peer == HostId;
            return false;
        }

        private bool IsLobbyMember(CSteamID who)
        {
            if (Direct)
                return true; // wer die TCP-Verbindung hat, ist dabei
            int n = SteamMatchmaking.GetNumLobbyMembers(Lobby);
            for (int i = 0; i < n; i++)
                if (SteamMatchmaking.GetLobbyMemberByIndex(Lobby, i) == who)
                    return true;
            return false;
        }

        // ------------------------------------------------------------------ Direktverbindung

        private TcpTransport UseTcp()
        {
            if (_tcp == null)
            {
                _tcp = new TcpTransport();
                _tcp.Received += OnReceived;
                _tcp.SessionFailed += OnSessionFailed;
            }
            _transport = _tcp;
            Direct = true;
            return _tcp;
        }

        private void UseSteam()
        {
            _transport = _steam;
            Direct = false;
            if (SteamReady)
                MyId = SteamUser.GetSteamID();
        }

        /// <summary>Host ohne Steam-Lobby: Gäste verbinden sich per IP und Port.</summary>
        public void StartHostDirect(int port)
        {
            if (Role != Role.None)
                return;
            // ab 0.22.0 auch im Hauptmenü (Gäste warten dort, bis der Host im Spiel ist)
            if (Versus.VersusSession.Active)
            {
                Status = "Erst das Versus-Spiel beenden";
                return;
            }
            try
            {
                UseTcp().Listen(port);
            }
            catch (Exception e)
            {
                UseSteam();
                Status = $"Port {port} geht nicht: {e.Message}";
                return;
            }
            Role = Role.Host;
            MyId = TcpTransport.HostId;
            Lobby = new CSteamID((ulong)port); // nur als „Lobby offen“-Markierung
            Status = $"Direkt-Host auf Port {port}";
            Plugin.Log.LogInfo("IFZCOOP host direct " + port);
        }

        /// <summary>Gast ohne Steam: direkt zu IP:Port verbinden.</summary>
        public void JoinDirect(string host, int port)
        {
            if (Role != Role.None)
                return;
            if (Versus.VersusSession.Active)
            {
                Status = "Erst das Versus-Spiel beenden";
                return;
            }
            _directHost = host;
            try
            {
                UseTcp().Connect(host, port);
            }
            catch (Exception e)
            {
                UseSteam();
                Status = $"Verbindung zu {host}:{port} fehlgeschlagen: {e.Message}";
                Plugin.Log.LogWarning(Status);
                return;
            }
            Role = Role.Guest;
            HostId = TcpTransport.HostId;
            Lobby = new CSteamID((ulong)port);
            _hostLastHeard = Time.realtimeSinceStartup;
            GuestState = GuestState.WaitingWelcome;
            Status = $"Verbunden mit {host}:{port}, warte auf Antwort ...";
            SendHello();
            Plugin.Log.LogInfo($"IFZCOOP guest direct {host}:{port}");
        }

        private void SendHello()
        {
            _transport.Send(HostId, Protocol.Build(Msg.Hello, w =>
            {
                w.Write(Protocol.Version);
                w.Write(Plugin.Version);
                w.Write(GameAccess.GameVersion);
                w.Write(MyName);
            }));
        }

        // ------------------------------------------------------------------ Host

        public void StartHost()
        {
            if (!SteamReady || Role != Role.None)
                return;
            // Ab 0.22.0 auch im Hauptmenü: Gäste warten dort, bis der Host eine Partie startet oder lädt.
            if (Versus.VersusSession.Active)
            {
                Status = "Erst das Versus-Spiel beenden";
                return;
            }
            UseSteam();
            Role = Role.Host;
            _lobbyTries = 0;
            CreateLobbyNow();
        }

        private const int MaxLobbyTries = 4;
        private int _lobbyTries;

        private void CreateLobbyNow()
        {
            _lobbyTries++;
            Status = _lobbyTries == 1 ? "Lobby wird erstellt ..." : $"Steam antwortet nicht, neuer Versuch {_lobbyTries}/{MaxLobbyTries} ...";
            var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, Mathf.Clamp(Plugin.MaxPlayers.Value, 2, 8));
            _crLobbyCreated.Set(call);
        }

        private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
        {
            if (Role != Role.Host)
                return;
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                bool transient = ioFailure || result.m_eResult == EResult.k_EResultTimeout || result.m_eResult == EResult.k_EResultBusy
                    || result.m_eResult == EResult.k_EResultServiceUnavailable || result.m_eResult == EResult.k_EResultNoConnection
                    || result.m_eResult == EResult.k_EResultFail;
                Plugin.Log.LogWarning($"IFZCOOP Lobby-Erstellung Versuch {_lobbyTries}: {(ioFailure ? "IO-Fehler" : result.m_eResult.ToString())}");
                if (transient && _lobbyTries < MaxLobbyTries)
                {
                    CreateLobbyNow();
                    return;
                }
                Status = $"Lobby konnte nicht erstellt werden ({result.m_eResult}, {_lobbyTries} Versuche). Ist Steam online? Später noch einmal \"Koop hosten\".";
                Role = Role.None;
                return;
            }
            if (Lobby != CSteamID.Nil)
                return; // Steam meldet die Erstellung manchmal doppelt
            Lobby = new CSteamID(result.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(Lobby, LobbyKey, Protocol.Version.ToString());
            SteamMatchmaking.SetLobbyData(Lobby, "gamever", GameAccess.GameVersion);
            SteamMatchmaking.SetLobbyData(Lobby, "modver", Plugin.Version);
            SteamMatchmaking.SetLobbyData(Lobby, "host", SteamFriends.GetPersonaName());
            SteamFriends.SetRichPresence("connect", "+connect_lobby " + Lobby.m_SteamID);
            Status = "Hoste Lobby " + Lobby.m_SteamID;
            Plugin.Log.LogInfo("IFZCOOP host lobby " + Lobby.m_SteamID);
            OpenInviteDialog();
        }

        public void OpenInviteDialog()
        {
            if (Lobby != CSteamID.Nil)
                SteamFriends.ActivateGameOverlayInviteDialog(Lobby);
        }

        public void ResyncAll()
        {
            if (Role != Role.Host)
                return;
            var targets = new List<CSteamID>();
            foreach (var p in _peers.Values)
                if (p.Welcomed) targets.Add(p.Id);
            if (targets.Count > 0)
                SendSnapshot(targets);
        }

        private void HostHandle(CSteamID from, Msg type, BinaryReader r)
        {
            _peers.TryGetValue(from.m_SteamID, out var peer);
            if (type == Msg.Hello)
            {
                int proto = r.ReadInt32();
                string modVersion = r.ReadString();
                string gameVersion = r.ReadString();
                string name = r.ReadString();
                if (modVersion != Plugin.Version) VersionBanner.Mismatch("g:" + name, Plugin.Version, modVersion, "Gast " + name);
                else VersionBanner.Match("g:" + name);
                string reason = null;
                if (!IsLobbyMember(from))
                    reason = "nicht in der Lobby";
                else if (proto != Protocol.Version)
                    reason = $"Mod-Version passt nicht (Host {Plugin.Version}, Gast {modVersion})";
                else if (gameVersion != GameAccess.GameVersion)
                    reason = $"Spielversion passt nicht (Host {GameAccess.GameVersion}, Gast {gameVersion})";

                if (reason != null)
                {
                    _transport.Send(from, Protocol.Build(Msg.Welcome, w => { w.Write(false); w.Write(reason); w.Write(0UL); w.Write(Plugin.Version); }));
                    Plugin.Log.LogWarning($"Gast {name} abgelehnt: {reason}");
                    return;
                }

                if (peer == null)
                {
                    peer = new Peer { Id = from };
                    _peers[from.m_SteamID] = peer;
                }
                peer.Name = name;
                peer.Welcomed = true;
                // Bis „Ready“ gilt der Gast als ladend: sonst bekäme er während des (asynchronen) Speicherns
                // schon Spielgeschehen geschickt, und Tempo-Änderungen in dieser Zeit gingen verloren.
                peer.AwaitingLoad = true;
                peer.LastHeard = Time.realtimeSinceStartup;
                _transport.Send(from, Protocol.Build(Msg.Welcome, w => { w.Write(true); w.Write(string.Empty); w.Write(from.m_SteamID); w.Write(Plugin.Version); }));
                Plugin.Log.LogInfo($"IFZCOOP guest joined: {name} ({from.m_SteamID})" + (GameAccess.IsGameStarted ? "" : " – wartet im Hauptmenü"));
                if (!GameAccess.IsGameStarted)
                {
                    if (!_waitingForGame.Contains(from))
                        _waitingForGame.Add(from);
                    _transport.Send(from, Protocol.Build(Msg.Notice, w => w.Write("Du bist in der Lobby. Der Host wählt die Karte, dann geht es los.")));
                    _transport.Send(from, Radio.BuildSettings());
                    _transport.Send(from, Menu.LobbyInfo.BuildMessage());
                    ShowNotice($"{name} ist in der Lobby", 4f);
                    return;
                }
                SendSnapshot(new List<CSteamID> { from });
                return;
            }

            if (peer == null || !peer.Welcomed)
                return;

            switch (type)
            {
                case Msg.Ready:
                    int transfer = r.ReadInt32();
                    if (transfer == peer.LastTransfer)
                    {
                        peer.AwaitingLoad = false;
                        Scavenge.ResendAll();
                        Hideouts.ResendAll();
                        Stock.ResendAll();
                        Containers.ResendAll();
                        Ground.ResendAll();
                        Warnings.ResendAll();
                        TowerWeapons.ResendAll();
                        Vehicles.ResendAll();
                        Events.ResendAll();
                        World.ResendAll();
                        Buildings.ResendAll();
                        Population.ResendAll();
                        Virtuals.ResendAll();
                        Expeditions.ResendAll();
                        Traders.ResendAll();
                        WorkerPos.ResendAll();
                        AreaWorks.ResendAll();
                        Production.ResendAll();
                        Story.ResendAll();
                        Structures.ResendAll();
                        SquadActions.ResendAll();
                        Zones.ResendAll();
                        BuildRes.ResendAll();
                        SquadViews.ResendAll();
                        SquadQueue.ResendAll();
                        Demolish.ResendAll();
                        Stats.ResendAll();
                        ResStats.ResendAll();
                        PlayerColors.ResendAll();
                        SquadStates.ResendAll();
                        Works.ResendAll();
                        Plugin.Log.LogInfo($"IFZCOOP guest ready: {peer.Name}");
                        // aktuelles Tempo sofort schicken (während des Haltens ist es 0)
                        _transport.Send(peer.Id, Protocol.Build(Msg.Speed, w => w.Write(TimeController.SpeedMultiplier)));
                        _transport.Send(peer.Id, Radio.BuildSettings());
                        Radio.SendActiveTo(peer);
                    }
                    break;

                case Msg.SpeedRequest:
                    byte kind = r.ReadByte();
                    int speedType = r.ReadInt32();
                    HandleSpeedRequest(peer, kind, speedType);
                    break;

                case Msg.ResyncRequest:
                    if (!peer.AwaitingLoad)
                        SendSnapshot(new List<CSteamID> { peer.Id });
                    break;

                case Msg.AnswerRequest:
                    if (!peer.AwaitingLoad)
                        Radio.HostOnAnswerRequest(peer, r);
                    break;

                case Msg.SquadOrder:
                    if (!peer.AwaitingLoad)
                        Orders.HostOnOrder(peer, r);
                    break;

                case Msg.Presence:
                    if (!peer.AwaitingLoad)
                        Presence.HostOnPresence(peer, r);
                    break;

                case Msg.GroupSpawnRequest:
                    if (!peer.AwaitingLoad)
                        Units.HostOnSpawnRequest(peer, r);
                    break;

                case Msg.SquadRequest:
                    if (!peer.AwaitingLoad)
                        Squads.HostOnRequest(peer, r);
                    break;

                case Msg.SquadQueueCancel:
                    if (!peer.AwaitingLoad)
                        SquadQueue.HostOnCancel(peer, r);
                    break;

                case Msg.ResearchRequest:
                    if (!peer.AwaitingLoad)
                        World.HostOnResearchRequest(peer, r);
                    break;

                case Msg.WorkRequest:
                    if (!peer.AwaitingLoad)
                        Works.HostOnRequest(peer, r);
                    break;

                case Msg.TowerWeapon:
                    if (!peer.AwaitingLoad)
                        TowerWeapons.HostOnRequest(peer, r);
                    break;

                case Msg.TraderRequest:
                    if (!peer.AwaitingLoad)
                        Traders.HostOnRequest(peer, r);
                    break;

                case Msg.AreaWorkRequest:
                    if (!peer.AwaitingLoad)
                        AreaWorks.HostOnRequest(peer, r);
                    break;

                case Msg.ProductionRequest:
                    if (!peer.AwaitingLoad)
                        Production.HostOnRequest(peer, r);
                    break;

                case Msg.GroupSide:
                    if (!peer.AwaitingLoad)
                        Story.HostOnRequest(peer, r);
                    break;

                case Msg.StructureRequest:
                    if (!peer.AwaitingLoad)
                        Structures.HostOnRequest(peer, r);
                    break;

                case Msg.SquadActionRequest:
                    if (!peer.AwaitingLoad)
                        SquadActions.HostOnRequest(peer, r);
                    break;

                case Msg.PlayerRequest:
                    PlayerColors.HostOnRequest(peer, r); // Farbe auch aus der Lobby; Op 1 (Trupps) nur nach dem Laden
                    break;

                case Msg.SquadStateRequest:
                    if (!peer.AwaitingLoad)
                        SquadStates.HostOnRequest(peer, r);
                    break;

                case Msg.DragRequest:
                    if (!peer.AwaitingLoad)
                        Exchange.HostOnRequest(peer, r);
                    break;

                case Msg.Checksum:
                    if (!peer.AwaitingLoad)
                        Checksums.HostOnChecksum(peer, r);
                    break;

                case Msg.HideoutRequest:
                    if (!peer.AwaitingLoad)
                        Hideouts.HostOnRequest(peer, r);
                    break;

                case Msg.BugReportPart:
                    BugReport.HostOnPart(peer.Name, r);
                    break;

                case Msg.HqSuggest:
                    Sync.HqSuggest.HostOnSuggest(peer, r);
                    break;

                case Msg.CheatRequest:
                {
                    string cmd = r.ReadString();
                    string res = CheatWindow.Instance?.HostOnGuestRequest(peer.Name, cmd) ?? "kein Menü";
                    _transport.Send(peer.Id, Protocol.Build(Msg.Notice, w => w.Write("Entwickler-Menü: " + res)));
                    break;
                }

                case Msg.BuildingRequest:
                    if (!peer.AwaitingLoad)
                        Buildings.HostOnRequest(peer, r);
                    break;

                case Msg.LawRequest:
                    if (!peer.AwaitingLoad)
                        World.HostOnLawRequest(peer, r);
                    break;

                case Msg.HqSetupRequest:
                    if (!peer.AwaitingLoad)
                        Hq.HostOnSetupRequest(peer, r);
                    break;

                case Msg.Bye:
                    DropPeer(from, "hat das Koop-Spiel verlassen");
                    break;
            }
        }

        private void HandleSpeedRequest(Peer peer, byte kind, int speedType)
        {
            if (!Plugin.GuestMayChangeSpeed.Value || _holdingTime)
                return;
            var setter = GameAccess.SpeedSetter;
            if (setter == null)
                return;
            if (kind == 1)
                setter.ChangeGameplayPauseState();
            else if (Enum.IsDefined(typeof(TimeSpeedType), speedType))
                setter.TrySetTimeSpeed((TimeSpeedType)speedType);
            Plugin.Log.LogInfo($"Tempo-Anfrage von {peer.Name}: {(kind == 1 ? "Pause umschalten" : ((TimeSpeedType)speedType).ToString())}");
        }

        /// <summary>Speichert das laufende Spiel in einen eigenen Ordner und schickt es an die Ziele.</summary>
        private async void SendSnapshot(List<CSteamID> targets)
        {
            if (_saving)
            {
                foreach (var t in targets)
                    if (!_pendingSnapshot.Contains(t)) _pendingSnapshot.Add(t);
                return;
            }

            // HQ-Wahl des Hosts läuft (KONZEPT-MENUE M5): Gäste bekommen schon jetzt den Stand vor der Wahl, schauen zu und
            // schlagen per Klick Häuser vor (HqSuggest; bestätigen kann nur der Host, HqSync). Nach der Wahl kommt der Stand
            // mit HQ noch einmal (_waitingForHq), damit die Folgen der Wahl (HQ-Gebäude, Startkisten) bei allen gleich sind.
            if (Hq.HostChoosingHq)
            {
                foreach (var t in targets)
                {
                    if (!_peers.TryGetValue(t.m_SteamID, out var waiting))
                        continue;
                    waiting.AwaitingLoad = true;
                    if (!_waitingForHq.Contains(t))
                        _waitingForHq.Add(t);
                    _transport.Send(t, Protocol.Build(Msg.Notice, w => w.Write("Der Host wählt das Hauptquartier. Klick auf ein Haus = Vorschlag an alle.")));
                }
                _hqChosenAt = -1f;
                Status = "Wähle ein Hauptquartier – deine Gäste schauen zu und können Häuser vorschlagen";
            }

            _saving = true;
            HoldTime();
            try
            {
                var map = GameAccess.Map;
                if (map == null)
                    throw new InvalidOperationException("Map nicht gefunden (nicht im Spiel?)");

                Status = "Speichere für die Übertragung ...";
                bool ok = await SaveHandler.SaveGame(map, HostSaveDir, "Koop-Sitzung", autoSave: false);
                if (!ok)
                    throw new IOException("SaveGame meldet Fehler");

                string dir = SaveHandler.GetSaveFolderPath(HostSaveDir);
                byte[] blob = SaveBlob.Pack(dir);
                byte[] hash = SaveBlob.Sha256(blob);
                int id = ++_transferCounter;

                foreach (var target in targets)
                {
                    if (!_peers.TryGetValue(target.m_SteamID, out var peer))
                        continue;
                    peer.AwaitingLoad = true;
                    peer.LastTransfer = id;
                    peer.LastHeard = Time.realtimeSinceStartup;
                    _transport.Send(target, Protocol.Build(Msg.SaveBegin, w => { w.Write(id); w.Write(blob.Length); w.Write(hash); }));
                    for (int offset = 0; offset < blob.Length; offset += Protocol.ChunkSize)
                    {
                        int len = Math.Min(Protocol.ChunkSize, blob.Length - offset);
                        int o = offset;
                        _transport.Send(target, Protocol.Build(Msg.SaveChunk, w => { w.Write(id); w.Write(o); w.Write(len); w.Write(blob, o, len); }));
                    }
                    _transport.Send(target, Protocol.Build(Msg.SaveEnd, w => w.Write(id)));
                }

                Status = $"Spielstand gesendet ({blob.Length / 1024} KB), warte auf Gäste ...";
                Plugin.Log.LogInfo($"IFZCOOP snapshot {id}: {blob.Length} bytes to {targets.Count} guest(s)");
            }
            catch (Exception e)
            {
                Status = "Übertragung fehlgeschlagen: " + e.Message;
                Plugin.Log.LogError("IFZCOOP snapshot FAIL: " + e);
                foreach (var t in targets)
                    if (_peers.TryGetValue(t.m_SteamID, out var p)) p.AwaitingLoad = false;
            }
            finally
            {
                _saving = false;
                if (_pendingSnapshot.Count > 0)
                {
                    var next = new List<CSteamID>(_pendingSnapshot);
                    _pendingSnapshot.Clear();
                    SendSnapshot(next);
                }
            }
        }

        private void HoldTime()
        {
            if (_holdingTime)
                return;
            _holdingTime = true;
            _heldSpeed = TimeController.SpeedMultiplier;
            GameAccess.TimeChanger?.SetSpeedMultiplier(0f, shouldPlaySound: false);
        }

        private void ReleaseTime()
        {
            _holdingTime = false;
            if (TimeController.SpeedMultiplier == 0f && _heldSpeed > 0f)
                GameAccess.TimeChanger?.SetSpeedMultiplier(_heldSpeed, shouldPlaySound: false);
            Status = "Koop läuft";
        }

        private void DropPeer(CSteamID id, string reason)
        {
            if (!_peers.TryGetValue(id.m_SteamID, out var peer))
                return;
            _peers.Remove(id.m_SteamID);
            Presence.Forget(id.m_SteamID);
            _pendingSnapshot.Remove(id);
            _waitingForHq.Remove(id);
            _waitingForGame.Remove(id);
            _transport.Close(id);
            Status = $"{peer.Name} {reason}";
            Plugin.Log.LogInfo($"IFZCOOP guest left: {peer.Name} ({reason})");
            // Meldung oben für den Host und alle übrigen Gäste (Wunsch 2026-11-09)
            string text = $"{peer.Name} {reason}";
            ShowNotice(text, 10f);
            try { BroadcastToGuests(Protocol.Build(Msg.Notice, w => w.Write(text))); }
            catch (Exception e) { Plugin.Log.LogWarning("Abgangsmeldung senden: " + e.Message); }
        }

        /// <summary>Vom Postfix auf SetSpeedMultiplier: Host verteilt jede Tempoänderung.</summary>
        public void OnLocalSpeedApplied(float speed)
        {
            if (Role != Role.Host || _peers.Count == 0)
                return;
            if (Mathf.Approximately(speed, _lastBroadcastSpeed))
                return;
            _lastBroadcastSpeed = speed;
            var msg = Protocol.Build(Msg.Speed, w => w.Write(speed));
            foreach (var p in _peers.Values)
                if (p.Welcomed && !p.AwaitingLoad) _transport.Send(p.Id, msg);
        }

        // ------------------------------------------------------------------ Gast

        private void OnJoinRequested(GameLobbyJoinRequested_t request)
        {
            Plugin.Log.LogInfo("Einladung angenommen, Lobby " + request.m_steamIDLobby.m_SteamID);
            JoinLobby(request.m_steamIDLobby);
        }

        private void OnRichPresenceJoin(GameRichPresenceJoinRequested_t request)
        {
            var lobby = ParseConnectString(request.m_rgchConnect);
            if (lobby != null && ulong.TryParse(lobby, out var id))
                JoinLobby(new CSteamID(id));
        }

        public void JoinLobby(CSteamID lobby)
        {
            if (Role == Role.Host)
            {
                Status = "Du hostest gerade, erst beenden";
                return;
            }
            if (Role == Role.Guest)
                Leave("wechsle die Lobby", sendBye: true, backToMenu: false);
            UseSteam();
            Role = Role.Guest;
            GuestState = GuestState.JoiningLobby;
            Status = "Trete Lobby bei ...";
            SteamMatchmaking.JoinLobby(lobby);
        }

        private void OnLobbyEnter(LobbyEnter_t entered)
        {
            if (Role != Role.Guest || GuestState != GuestState.JoiningLobby)
                return;
            const uint success = 1; // k_EChatRoomEnterResponseSuccess
            var lobby = new CSteamID(entered.m_ulSteamIDLobby);
            if (entered.m_EChatRoomEnterResponse != success)
            {
                Reset("Beitritt fehlgeschlagen (Antwort " + entered.m_EChatRoomEnterResponse + ")");
                return;
            }
            // Versus-Lobby (Einladung landet hier): die Versus-Sitzung übernimmt, Koop bleibt auf Role.None
            if (Versus.VersusSession.Instance != null && Versus.VersusSession.Instance.AdoptLobby(lobby))
            {
                Reset("Versus-Lobby beigetreten (Koop aus)");
                return;
            }
            if (SteamMatchmaking.GetLobbyData(lobby, LobbyKey) != Protocol.Version.ToString())
            {
                SteamMatchmaking.LeaveLobby(lobby);
                string modver = SteamMatchmaking.GetLobbyData(lobby, "modver");
                if (!string.IsNullOrEmpty(modver)) VersionBanner.Mismatch("host", modver, Plugin.Version, "Du");
                Reset(string.IsNullOrEmpty(modver) ? "Das ist keine IFZ-Koop-Lobby" : $"Mod-Version passt nicht (Host {modver}, du {Plugin.Version})");
                return;
            }

            Lobby = lobby;
            HostId = SteamMatchmaking.GetLobbyOwner(lobby);
            _hostLastHeard = Time.realtimeSinceStartup;
            GuestState = GuestState.WaitingWelcome;
            Status = "Verbunden mit " + SteamMatchmaking.GetLobbyData(lobby, "host") + ", warte auf Antwort ...";
            SendHello();
            Plugin.Log.LogInfo($"IFZCOOP guest entered lobby {lobby.m_SteamID}, host {HostId.m_SteamID}");
        }

        private void GuestHandle(CSteamID from, Msg type, BinaryReader r)
        {
            if (from != HostId)
                return;

            switch (type)
            {
                case Msg.Welcome:
                    bool accepted = r.ReadBoolean();
                    string reason = r.ReadString();
                    ulong assignedId = r.ReadUInt64();
                    // ab 0.26.4 schickt der Host seine Mod-Version mit (ältere Hosts nicht)
                    if (r.BaseStream.Position < r.BaseStream.Length)
                    {
                        string hostVer = r.ReadString();
                        if (hostVer != Plugin.Version) VersionBanner.Mismatch("host", hostVer, Plugin.Version, "Du");
                        else VersionBanner.Match("host");
                    }
                    if (!accepted)
                    {
                        Leave("Host lehnt ab: " + reason, sendBye: false);
                        return;
                    }
                    if (Direct)
                        MyId = new CSteamID(assignedId);
                    GuestState = GuestState.ReceivingSave;
                    Status = "Angenommen, warte auf Spielstand ...";
                    break;

                case Msg.SaveBegin:
                    _transferId = r.ReadInt32();
                    int total = r.ReadInt32();
                    _transferHash = r.ReadBytes(32);
                    if (total <= 0 || total > 512 * 1024 * 1024)
                    {
                        Leave("ungültige Spielstandgröße", sendBye: true);
                        return;
                    }
                    _transferBuffer = new byte[total];
                    _transferReceived = 0;
                    GuestState = GuestState.ReceivingSave;
                    Status = $"Empfange Spielstand ({total / 1024} KB) ...";
                    break;

                case Msg.SaveChunk:
                    int chunkTransfer = r.ReadInt32();
                    int offset = r.ReadInt32();
                    int length = r.ReadInt32();
                    if (chunkTransfer != _transferId || _transferBuffer == null || offset < 0 || length < 0 || offset + length > _transferBuffer.Length)
                        return;
                    var data = r.ReadBytes(length);
                    Buffer.BlockCopy(data, 0, _transferBuffer, offset, data.Length);
                    _transferReceived += data.Length;
                    break;

                case Msg.SaveEnd:
                    int endTransfer = r.ReadInt32();
                    if (endTransfer == _transferId)
                        FinishTransfer();
                    break;

                case Msg.Speed:
                    _hostSpeed = r.ReadSingle();
                    if (GuestState == GuestState.InGame)
                        ApplyHostSpeed(_hostSpeed);
                    break;

                case Msg.TimeState:
                    int day = r.ReadInt32();
                    float dayGts = r.ReadSingle();
                    float speed = r.ReadSingle();
                    int dayLength = r.ReadInt32();
                    if (GuestState == GuestState.InGame)
                    {
                        float hostTotal = day * (float)dayLength + dayGts;
                        float localTotal = TimeController.Day * (float)TimeController.DayLengthInSeconds + TimeController.CurrentDayGameplaySeconds;
                        // Die Nachricht ist eine halbe Pingzeit alt: so viel Spielzeit ist beim Host schon weiter.
                        float latency = _hostRttMs > 0 ? (float)(_hostRttMs / 2000.0) * speed : 0f;
                        TimeDriftSeconds = hostTotal + latency - localTotal;
                        EstimateHostClockRate(hostTotal, speed);
                        if (!Mathf.Approximately(speed, TimeController.SpeedMultiplier))
                            ApplyHostSpeed(speed);
                        latency *= HostClockRate;
                        CorrectTime(day, Mathf.Min(dayGts + latency, dayLength - 0.01f), dayLength);
                    }
                    break;

                case Msg.Presence:
                    if (GuestState == GuestState.InGame)
                        Presence.GuestOnPresence(r);
                    break;

                case Msg.GameOver:
                    if (GuestState == GuestState.InGame)
                        ApplyGameOver(r.ReadInt32(), r.ReadInt32());
                    break;

                case Msg.UnitSnapshot:
                    if (GuestState == GuestState.InGame)
                        Units.GuestOnSnapshot(r);
                    break;

                case Msg.UnitRoster:
                    if (GuestState == GuestState.InGame)
                        Units.GuestOnRoster(r);
                    break;

                case Msg.VehicleSnapshot:
                    if (GuestState == GuestState.InGame)
                        Units.GuestOnVehicles(r);
                    break;

                case Msg.GroupSpawn:
                    if (GuestState == GuestState.InGame)
                        Units.GuestOnSpawn(r);
                    break;

                case Msg.ShotBatch:
                    if (GuestState == GuestState.InGame)
                        Combat.GuestOnShots(r);
                    break;

                case Msg.ProjectileLaunch:
                    if (GuestState == GuestState.InGame)
                        Combat.GuestOnProjectile(r);
                    break;

                case Msg.SquadSpawn:
                    if (GuestState == GuestState.InGame)
                        Squads.GuestOnSquadSpawn(r);
                    break;

                case Msg.SquadQueue:
                    if (GuestState == GuestState.InGame)
                        SquadQueue.GuestOnState(r);
                    break;

                case Msg.CharacterDied:
                    if (GuestState == GuestState.InGame)
                        Squads.GuestOnCharacterDied(r);
                    break;

                case Msg.Bye:
                    Leave("Host hat beendet: " + r.ReadString(), sendBye: false);
                    break;

                case Msg.TransmissionStart:
                    if (GuestState == GuestState.InGame)
                        Radio.GuestOnTransmissionStart(r);
                    break;

                case Msg.DialogueLine:
                    if (GuestState == GuestState.InGame)
                        Radio.GuestOnDialogueLine(r);
                    break;

                case Msg.ScavengeState:
                    if (GuestState == GuestState.InGame)
                        Scavenge.GuestOnState(r);
                    break;

                case Msg.HideoutRevealed:
                    if (GuestState == GuestState.InGame)
                        Hideouts.GuestOnRevealed(r);
                    break;

                case Msg.HideoutCount:
                    if (GuestState == GuestState.InGame)
                        Hideouts.GuestOnCount(r);
                    break;

                case Msg.HideoutInfo:
                    if (GuestState == GuestState.InGame)
                        Hideouts.GuestOnInfo(r);
                    break;

                case Msg.StockState:
                    if (GuestState == GuestState.InGame)
                        Stock.GuestOnState(r);
                    break;

                case Msg.ContainerState:
                    if (GuestState == GuestState.InGame)
                        Containers.GuestOnContainers(r);
                    break;

                case Msg.ItemsState:
                    if (GuestState == GuestState.InGame)
                        Containers.GuestOnItems(r);
                    break;

                case Msg.GroundState:
                    if (GuestState == GuestState.InGame)
                        Ground.GuestOnState(r);
                    break;

                case Msg.ResearchState:
                case Msg.LawState:
                case Msg.MoodState:
                case Msg.WeatherState:
                    if (GuestState == GuestState.InGame)
                        World.GuestOnState(type, r);
                    break;

                case Msg.WorkState:
                    if (GuestState == GuestState.InGame)
                        Works.GuestOnState(r);
                    break;

                case Msg.ExpeditionState:
                    if (GuestState == GuestState.InGame)
                        Expeditions.GuestOnState(r);
                    break;

                case Msg.TraderState:
                    if (GuestState == GuestState.InGame)
                        Traders.GuestOnState(r);
                    break;

                case Msg.WorkerPos:
                    if (GuestState == GuestState.InGame)
                        WorkerPos.GuestOnState(r);
                    break;

                case Msg.AreaWorkState:
                    if (GuestState == GuestState.InGame)
                        AreaWorks.GuestOnState(r);
                    break;

                case Msg.ProductionState:
                    if (GuestState == GuestState.InGame)
                        Production.GuestOnState(r);
                    break;

                case Msg.WarningState:
                    if (GuestState == GuestState.InGame)
                        Warnings.GuestOnState(r);
                    break;

                case Msg.TowerWeapon:
                    if (GuestState == GuestState.InGame)
                        TowerWeapons.GuestOnState(r);
                    break;

                case Msg.GroupSide:
                    if (GuestState == GuestState.InGame)
                        Story.GuestOnSides(r);
                    break;

                case Msg.StoryCue:
                    if (GuestState == GuestState.InGame)
                        Story.GuestOnCue(r);
                    break;

                case Msg.StructureState:
                    if (GuestState == GuestState.InGame)
                        Structures.GuestOnState(r);
                    break;

                case Msg.SquadActionState:
                    if (GuestState == GuestState.InGame)
                        SquadActions.GuestOnState(r);
                    break;

                case Msg.SquadActionEvent:
                    if (GuestState == GuestState.InGame)
                        SquadActions.GuestOnEvent(r);
                    break;

                case Msg.ZoneState:
                    if (GuestState == GuestState.InGame)
                        Zones.GuestOnState(r);
                    break;

                case Msg.ZoneStats:
                    if (GuestState == GuestState.InGame)
                        Zones.GuestOnStats(r);
                    break;

                case Msg.ZoneMove:
                    Zones.GuestOnMove(r);
                    break;

                case Msg.BuildingHealth:
                    if (GuestState == GuestState.InGame)
                        Buildings.GuestOnHealth(r);
                    break;

                case Msg.BuildingEvent:
                    if (GuestState == GuestState.InGame)
                        Buildings.GuestOnEvent(r);
                    break;

                case Msg.BuildResState:
                    if (GuestState == GuestState.InGame)
                        BuildRes.GuestOnState(r);
                    break;

                case Msg.DemolishState:
                    if (GuestState == GuestState.InGame)
                        Demolish.GuestOnState(r);
                    break;

                case Msg.Stats:
                    if (GuestState == GuestState.InGame)
                        Stats.GuestOnState(r);
                    break;

                case Msg.SquadView:
                    if (GuestState == GuestState.InGame)
                        SquadViews.GuestOnState(r);
                    break;

                case Msg.ResStats:
                    if (GuestState == GuestState.InGame)
                        ResStats.GuestOnState(r);
                    break;

                case Msg.PlayerColors:
                    PlayerColors.GuestOnState(r); // auch in der Lobby im Hauptmenü und beim Laden
                    break;

                case Msg.SquadState:
                    if (GuestState == GuestState.InGame)
                        SquadStates.GuestOnState(r);
                    break;

                case Msg.VirtualState:
                    if (GuestState == GuestState.InGame)
                        Virtuals.GuestOnState(r);
                    break;

                case Msg.PopulationState:
                    if (GuestState == GuestState.InGame)
                        Population.GuestOnState(r);
                    break;

                case Msg.BuildingState:
                    if (GuestState == GuestState.InGame)
                        Buildings.GuestOnState(r);
                    break;

                case Msg.EventState:
                    if (GuestState == GuestState.InGame)
                        Events.GuestOnState(r);
                    break;

                case Msg.MissionTimes:
                    if (GuestState == GuestState.InGame)
                        Events.GuestOnTimes(r);
                    break;

                case Msg.VehicleState:
                    if (GuestState == GuestState.InGame)
                        Vehicles.GuestOnState(r);
                    break;

                case Msg.Notice:
                    ShowNotice(r.ReadString());
                    break;

                case Msg.Settings:
                    Radio.GuestOnSettings(r);
                    break;

                case Msg.LobbyInfo:
                    Menu.LobbyInfo.GuestOnInfo(r);
                    break;

                case Msg.HqSuggestShow:
                    Sync.HqSuggest.GuestOnShow(r);
                    break;

                case Msg.VsSwitch:
                    GuestOnVsSwitch(r);
                    return;

                case Msg.SquadOrder:
                    if (GuestState == GuestState.InGame)
                        Orders.GuestOnOrder(r);
                    break;
            }
        }

        private void FinishTransfer()
        {
            try
            {
                var hash = SaveBlob.Sha256(_transferBuffer);
                if (_transferReceived != _transferBuffer.Length || !SaveBlob.SameHash(hash, _transferHash))
                    throw new InvalidDataException($"Prüfsumme falsch ({_transferReceived}/{_transferBuffer.Length} Bytes)");

                string dir = SaveHandler.GetSaveFolderPath(GuestSaveDir);
                SaveBlob.Unpack(_transferBuffer, dir);
                _transferBuffer = null;

                var info = SaveHandler.LoadSaveInfoByDirPath(dir);
                var validator = GameAccess.SaveValidator;
                if (info == null || validator == null)
                    throw new InvalidOperationException("Spielstand-Info oder SaveValidator fehlt");

                GuestState = GuestState.Loading;
                _sawGameStopped = !GameAccess.IsGameStarted;
                _loadingSince = Time.realtimeSinceStartup;
                Status = "Lade Spielstand des Hosts ...";
                Plugin.Log.LogInfo("IFZCOOP guest loading save " + dir);
                validator.TryRun(dir, info);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("IFZCOOP load FAIL: " + e);
                Leave("Spielstand konnte nicht geladen werden: " + e.Message, sendBye: true);
            }
        }

        private void ApplyHostSpeed(float speed)
        {
            var changer = GameAccess.TimeChanger;
            if (changer == null)
                return;
            Patches.ApplyingRemote = true;
            try
            {
                changer.SetSpeedMultiplier(speed, shouldPlaySound: false);
            }
            finally
            {
                Patches.ApplyingRemote = false;
            }
        }

        public void RequestSpeed(TimeSpeedType type)
        {
            if (Role == Role.Guest && HostId != CSteamID.Nil)
                _transport.Send(HostId, Protocol.Build(Msg.SpeedRequest, w => { w.Write((byte)0); w.Write((int)type); }));
        }

        public void RequestTogglePause()
        {
            if (Role == Role.Guest && HostId != CSteamID.Nil)
                _transport.Send(HostId, Protocol.Build(Msg.SpeedRequest, w => { w.Write((byte)1); w.Write(0); }));
        }

        public void RequestResync()
        {
            if (Role == Role.Guest && GuestState == GuestState.InGame)
                _transport.Send(HostId, Protocol.Build(Msg.ResyncRequest));
        }

        // ------------------------------------------------------------------ gemeinsam

        private void OnReceived(CSteamID from, byte[] data)
        {
            BinaryReader r;
            Msg type;
            try
            {
                r = Protocol.Reader(data, out type);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Kaputte Nachricht von " + from.m_SteamID + ": " + e.Message);
                return;
            }

            using (r)
            {
                float now = Time.realtimeSinceStartup;
                if (Role == Role.Host && _peers.TryGetValue(from.m_SteamID, out var p))
                    p.LastHeard = now;
                else if (Role == Role.Guest && from == HostId)
                    _hostLastHeard = now;

                try
                {
                    if (type == Msg.Ping)
                    {
                        double sent = r.ReadDouble();
                        _transport.Send(from, Protocol.Build(Msg.Pong, w => w.Write(sent)));
                        return;
                    }
                    if (type == Msg.Pong)
                    {
                        double rtt = (Now() - r.ReadDouble()) * 1000.0;
                        if (Role == Role.Host && _peers.TryGetValue(from.m_SteamID, out var peer)) peer.RttMs = rtt;
                        else if (Role == Role.Guest) _hostRttMs = rtt;
                        return;
                    }

                    if (Role == Role.Host)
                        HostHandle(from, type, r);
                    else if (Role == Role.Guest)
                        GuestHandle(from, type, r);
                }
                catch (EndOfStreamException)
                {
                    Plugin.Log.LogWarning($"Zu kurze Nachricht {type} von {from.m_SteamID}");
                }
            }
        }

        private void OnSessionFailed(CSteamID peer)
        {
            if (Role == Role.Host)
                DropPeer(peer, "Verbindung verloren");
            else if (Role == Role.Guest && peer == HostId)
                Leave("Verbindung zum Host verloren", sendBye: false);
        }

        private void OnLobbyChatUpdate(LobbyChatUpdate_t update)
        {
            if (Lobby == CSteamID.Nil || update.m_ulSteamIDLobby != Lobby.m_SteamID)
                return;
            const uint leftMask = 2 | 4 | 8 | 16; // verlassen, getrennt, gekickt, gebannt
            if ((update.m_rgfChatMemberStateChange & leftMask) == 0)
                return;
            var who = new CSteamID(update.m_ulSteamIDUserChanged);
            if (Role == Role.Host)
                DropPeer(who, "hat die Lobby verlassen");
            else if (Role == Role.Guest && who == HostId)
                Leave("Host hat die Lobby verlassen", sendBye: false);
        }

        /// <param name="backToMenu">
        /// Gast im Spiel: danach ins Hauptmenü. Sein Spiel ist eine Kopie des Host-Spielstands mit Marionetten;
        /// allein weiterzuspielen ergibt keinen Sinn (Verbindung verloren, Host beendet, Gast verlässt selbst).
        /// </param>
        public void Leave(string reason = "beendet", bool sendBye = true, bool backToMenu = true)
        {
            bool wasGuest = Role == Role.Guest;
            bool guestToMenu = backToMenu && Role == Role.Guest
                               && (GuestState == GuestState.InGame || GuestState == GuestState.Loading);
            if (sendBye)
            {
                var bye = Protocol.Build(Msg.Bye, w => w.Write(reason));
                if (Role == Role.Host)
                    foreach (var p in _peers.Values) _transport.Send(p.Id, bye);
                else if (Role == Role.Guest && HostId != CSteamID.Nil)
                    _transport.Send(HostId, bye);
                _transport.Flush(); // Bye möglichst noch rausschicken
            }

            if (Role == Role.Host && _holdingTime)
                ReleaseTime();

            if (!Direct && Lobby != CSteamID.Nil)
                SteamMatchmaking.LeaveLobby(Lobby);
            if (!Direct && SteamReady)
                SteamFriends.SetRichPresence("connect", string.Empty);
            _transport.CloseAll();
            UseSteam();
            Reset(reason);
            Plugin.Log.LogInfo("IFZCOOP session ended: " + reason);

            if (!guestToMenu && wasGuest && !sendBye && backToMenu)
                ShowNotice("Koop beendet: " + reason, 10f); // Host weg, Gast war noch nicht im Spiel (z. B. Lobby)

            if (guestToMenu)
            {
                ShowNotice("Koop beendet (" + reason + "), zurück im Hauptmenü", 20f);
                try
                {
                    ZenjectExtensions.Installers.ScenesManager.GoToMenu();
                    Plugin.Log.LogInfo("IFZCOOP Gast zurück ins Hauptmenü");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Zurück ins Hauptmenü: " + e);
                }
            }
        }

        /// <summary>
        /// Gast: was er selbst noch an Objekten anlegt (Kisten in Arbeiterhänden, Funde beim Tod, …), bekommt IDs aus dem
        /// gemeinsamen Zähler (IdProvider._lastID). Ohne Versatz vergibt der Host dieselben Nummern später für andere Objekte
        /// (z. B. Trupp-Mitglieder) und der Gast verwechselt sie. Mit +1 Mrd. liegen lokale Gast-IDs außerhalb.
        /// </summary>
        private static void OffsetGuestIds()
        {
            try
            {
                var ids = GameAccess.Game<Serialization.Data.IdProvider>();
                if (ids == null)
                    return;
                var field = HarmonyLib.Traverse.Create(ids).Field("_lastID");
                uint last = field.GetValue<uint>();
                if (last < GuestIdOffset)
                {
                    field.SetValue(last + GuestIdOffset);
                    Plugin.Log.LogInfo($"IFZCOOP Gast-IDs ab {last + GuestIdOffset} (Host bei {last})");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Gast-IDs versetzen: " + e.Message);
            }
        }

        private const uint GuestIdOffset = 1000000000u;

        private void Reset(string status)
        {
            Role = Role.None;
            GuestState = GuestState.Idle;
            Lobby = CSteamID.Nil;
            HostId = CSteamID.Nil;
            _peers.Clear();
            _pendingSnapshot.Clear();
            _waitingForHq.Clear();
            _waitingForGame.Clear();
            Menu.LobbyInfo.Reset();
            Scavenge.Reset();
            Warnings.Reset();
            TowerWeapons.Reset();
            Hideouts.Reset();
            Stock.Reset();
            Containers.Reset();
            Ground.Reset();
            Vehicles.Reset();
            Events.Reset();
            World.Reset();
            Buildings.Reset();
            Population.Reset();
            Virtuals.Reset();
            Checksums.Reset();
            Expeditions.Reset();
            Traders.Reset();
            WorkerPos.Reset();
            AreaWorks.Reset();
            Production.Reset();
            Story.Reset();
            Structures.Reset();
            SquadActions.Reset();
            Zones.Reset();
            BuildRes.Reset();
            Demolish.Reset();
            Stats.Reset();
            SquadViews.Reset();
            ResStats.Reset();
            PlayerColors.Reset();
            SquadStates.Reset();
            Works.Reset();
            _holdingTime = false;
            _lastBroadcastSpeed = -1f;
            _transferBuffer = null;
            _hostSpeed = -1f;
            _hostRttMs = -1;
            TimeDriftSeconds = 0f;
            Radio.Reset();
            Presence.Reset();
            Units.Reset();
            Combat.Reset();
            ApplyingGameOver = false;
            TimeCorrections = 0;
            Status = status;
        }

        public void Shutdown()
        {
            if (Role != Role.None && SteamReady)
                Leave("Spiel beendet");
        }

        private static double Now() => Time.realtimeSinceStartupAsDouble;

        private static string ReadConnectLobbyArgument()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "+connect_lobby")
                    return args[i + 1];
            return null;
        }

        private static string ParseConnectString(string connect)
        {
            if (string.IsNullOrEmpty(connect))
                return null;
            var parts = connect.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length - 1; i++)
                if (parts[i] == "+connect_lobby")
                    return parts[i + 1];
            return null;
        }
    }
}
