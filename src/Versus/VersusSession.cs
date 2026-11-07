using System;
using System.Collections.Generic;
using System.IO;
using Controllers;
using Controllers.Time;
using IfzCoop.Game;
using IfzCoop.Net;
using IfzCoop.Sync;
using Steamworks;
using UnityEngine;

namespace IfzCoop.Versus
{
    internal enum VsRole { None, Host, Guest }

    /// <summary>Startphase (Msg.VsStartPhase): 0 Lobby, 1 Spielstand kommt, 2 HQ wählen, 3 los.</summary>
    internal enum VsPhase : byte { Lobby = 0, Transfer = 1, ChooseHq = 2, Running = 3 }

    internal enum VsGuestState { Idle, WaitingWelcome, InLobby, ReceivingSave, Loading, InGame }

    internal sealed class VsPeer
    {
        public CSteamID Id;
        public string Name = "?";
        public byte Slot;
        public float LastHeard;
        public bool Welcomed;
        public bool Loading;        // hat den Start-Spielstand bekommen, Ready fehlt noch
        public bool Loaded;         // Ready für den letzten Spielstand
        public int LastTransfer;
        public double RttMs = -1;

        // HQ-Wahl (nur beim Host bekannt, geheim)
        public bool HqChosen;
        public long HqId;
        public Vector3 HqPos;

        // gemeldete Bevölkerung (VsPopulation)
        public bool PopKnown;
        public bool PopApplied;
        public int PopWorkers, PopChildren, PopSquads, PopSoldiers;
        public string PopResources = "";
    }

    /// <summary>
    /// Versus-Modus, Schritte V0 (Verbindung) und V1 (gemeinsamer Start), VERSUS-TECHNIK Abschnitte 1.3, 2, 8.4, 9.1, 10.
    ///
    /// Eigene Sitzung neben der Koop-Session: eigener Steam-Kanal (1) bzw. TCP-Port (27121), eigene Lobby (Schlüssel
    /// „ifzversus“), eigene Plätze (0 = Host, 1–7 = Gäste). Die Koop-Session bleibt dabei auf Role.None, damit kein
    /// Koop-Haken greift. Sternform: Gäste reden nur mit dem Host.
    ///
    /// Ablauf: Der Host startet ein neues Spiel (freie Stadt), öffnet in der HQ-Wahl die Versus-Lobby, die Gäste treten
    /// bei (Steam-Einladung oder direkt). „Start“ speichert den Stand vor der HQ-Wahl und schickt ihn an alle (VsStart).
    /// Jeder wählt heimlich sein HQ, der Host zuletzt. Uhr, Tempo, Pause und Wetter kommen danach vom Host (VsClock).
    /// </summary>
    internal sealed class VersusSession
    {
        private const string HostSaveDir = "ifzvs-start";
        private const string GuestSaveDir = "ifzvs-guest";
        private const float PingInterval = 1f;
        private const float TimeStateInterval = 1f;
        private const float PeerTimeout = 20f;
        private const float LoadingTimeout = 240f;
        private const float PopulationInterval = 5f;

        public static VersusSession Instance { get; private set; }

        /// <summary>Versus läuft (Host oder Gast, auch in der Lobby).</summary>
        public static bool Active => Instance != null && Instance.Role != VsRole.None;
        public static bool IsHostActive => Instance != null && Instance.Role == VsRole.Host;
        public static bool IsGuestActive => Instance != null && Instance.Role == VsRole.Guest;

        /// <summary>Entwickler-Menü (F7) ist im Versus für alle aus (sonst Schummeln, Risiko R16).</summary>
        public static bool BlocksCheats => Active;

        /// <summary>Gast hat den Versus-Spielstand geladen: Tempo und Pause kommen nur noch vom Host.</summary>
        public static bool GuestTimeLocked => IsGuestActive && Instance.GuestState == VsGuestState.InGame;

        /// <summary>Automatische Pausen (Funk, Handel, Hinweisfenster) sind aus, solange das Versus-Spiel läuft (8.4).</summary>
        public static bool SuppressAutoPause => Active && Instance.Phase == VsPhase.Running && Instance.Start.MyHqConfirmed
                                                && GameAccess.IsGameStarted
                                                && (Instance.Role == VsRole.Host || Instance.GuestState == VsGuestState.InGame);

        private readonly SteamTransport _steam = new SteamTransport(VsProtocol.SteamChannel);
        private TcpTransport _tcp;
        private ITransport _transport;
        private readonly Dictionary<ulong, VsPeer> _peers = new Dictionary<ulong, VsPeer>();

        private bool _steamHooked;
        private CallResult<LobbyCreated_t> _crLobbyCreated;
        private Callback<LobbyChatUpdate_t> _cbLobbyChat;
        private int _lobbyTries;
        private const int MaxLobbyTries = 4;

        private float _nextPing;
        private float _nextTimeState;
        private float _nextPopulation;
        private float _nextSettingsSend;
        private bool _settingsDirty;
        private string _rosterSig;
        private float _lastBroadcastSpeed = -1f;

        // Host
        private bool _saving;
        private int _transferCounter;

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
        private bool _pendingTileLoad;
        private int _tileX, _tileY, _tileZoom = 15, _tilePadding = 1;

        public VsRole Role { get; private set; } = VsRole.None;
        public VsGuestState GuestState { get; private set; } = VsGuestState.Idle;
        public VsPhase Phase { get; private set; } = VsPhase.Lobby;
        public bool Direct { get; private set; }
        public CSteamID Lobby { get; private set; } = CSteamID.Nil;
        public CSteamID HostId { get; private set; } = CSteamID.Nil;
        public CSteamID MyId { get; private set; } = CSteamID.Nil;
        public byte MySlot { get; private set; }
        public string Status { get; private set; } = "Bereit";

        /// <summary>Wie der Start lief: 1 = gemeinsamer Spielstand (S1), 2 = jeder startet die Kachel selbst (S2).</summary>
        public byte StartMode { get; private set; }

        /// <summary>Einstellungen. Beim Host editierbar (danach MarkSettingsChanged), beim Gast vom Host.</summary>
        public VsSettings Settings { get; private set; } = new VsSettings();
        public VsRoster Roster { get; } = new VsRoster();
        public VsStart Start { get; }
        public VsClock Clock { get; }
        public VsVote Vote { get; }
        public VsDiplomacy Diplomacy { get; }
        public VsUnits Units { get; }

        public IEnumerable<VsPeer> Peers => _peers.Values;
        public double HostRttMs => _hostRttMs;
        public float TransferProgress => _transferBuffer == null || _transferBuffer.Length == 0 ? 0f : (float)_transferReceived / _transferBuffer.Length;
        public float HostSpeed => _hostSpeed;

        public string MyName => Plugin.NameOverride ?? (Koop != null && Koop.SteamReady ? SteamFriends.GetPersonaName() : "Spieler");

        private static Session Koop => Plugin.Instance?.Session;

        public VersusSession()
        {
            _steam.Received += OnReceived;
            _steam.SessionFailed += OnSessionFailed;
            _steam.AcceptPeer = AcceptPeer;
            _transport = _steam;
            Start = new VsStart(this);
            Clock = new VsClock(this);
            Vote = new VsVote(this);
            Diplomacy = new VsDiplomacy(this);
            Units = new VsUnits(this);
            Settings.MaxPlayers = Mathf.Clamp(Plugin.MaxPlayers?.Value ?? 4, 2, VsProtocol.MaxPlayers);
            Instance = this;
        }

        public void Notice(string text, float seconds = 6f)
        {
            Koop?.ShowNotice(text, seconds);
        }

        // ================================================================== Frame

        public void Update()
        {
            var koop = Koop;
            if (koop == null)
                return;
            if (koop.SteamReady)
                EnsureSteamHooks();
            if (Role == VsRole.None)
                return;
            if (!Direct && !koop.SteamReady)
                return;

            _transport.Pump();
            if (Role == VsRole.Host && VsVote.Enabled)
                Vote.Tick();
            AutoStartUpdate(Time.realtimeSinceStartup);
            if (Role == VsRole.None)
                return; // beim Empfangen beendet

            float now = Time.realtimeSinceStartup;
            if (now >= _nextPing)
            {
                _nextPing = now + PingInterval;
                var ping = Protocol.Build(Msg.Ping, w => w.Write(Time.realtimeSinceStartupAsDouble));
                if (Role == VsRole.Host)
                {
                    foreach (var p in _peers.Values) _transport.Send(p.Id, ping);
                }
                else if (HostId != CSteamID.Nil)
                {
                    _transport.Send(HostId, ping);
                }
                CheckTimeouts(now);
                if (Role == VsRole.None)
                    return;
            }

            if (Role == VsRole.Host)
                UpdateHost(now);
            else
                UpdateGuest(now);

            if (Role != VsRole.None)
                Start.Update(now);
            if (Role != VsRole.None)
            {
                try { Diplomacy.Update(now); }
                catch (Exception e) { Plugin.Log.LogDebug("IFZVS Bündnisse: " + e.Message); }
                try { Units.Update(now); }
                catch (Exception e) { Plugin.Log.LogDebug("IFZVS fremde Trupps: " + e.Message); }
            }
        }

        private void UpdateHost(float now)
        {
            if (GameAccess.IsGameStarted && now >= _nextTimeState)
            {
                _nextTimeState = now + TimeStateInterval;
                var msg = Clock.BuildTimeState();
                BroadcastLoaded(msg);
            }

            if (_settingsDirty && now >= _nextSettingsSend)
            {
                _settingsDirty = false;
                _nextSettingsSend = now + 0.3f;
                BroadcastWelcomed(BuildSettings());
                if (!Direct && Lobby != CSteamID.Nil)
                    SteamMatchmaking.SetLobbyMemberLimit(Lobby, Settings.MaxPlayers);
            }

            UpdateHostRosterStates();
            string sig = Roster.Signature();
            if (sig != _rosterSig)
            {
                _rosterSig = sig;
                BroadcastWelcomed(BuildRoster());
            }

            if (Phase == VsPhase.Transfer && !_saving)
            {
                bool anyLoading = false;
                foreach (var p in _peers.Values)
                    if (p.Welcomed && !p.Loaded) anyLoading = true;
                if (!anyLoading)
                    SetHostPhase(VsPhase.ChooseHq, "Alle haben den Spielstand. Jeder wählt jetzt heimlich sein HQ, der Host zuletzt.");
            }

            Clock.HostUpdate(now);
        }

        private void UpdateGuest(float now)
        {
            if (_pendingTileLoad && ZenjectExtensions.Installers.ScenesManager.IsMainSceneActive
                && !ZenjectExtensions.Installers.ScenesManager.IsSceneSwitching)
            {
                _pendingTileLoad = false;
                LoadTileNow();
            }

            if (GuestState == VsGuestState.Loading)
            {
                if (!GameAccess.IsGameStarted)
                {
                    _sawGameStopped = true;
                }
                else if (_sawGameStopped)
                {
                    GuestState = VsGuestState.InGame;
                    Start.OnGuestLoaded();
                    Status = "Versus-Spielstand geladen. Wähle dein Hauptquartier.";
                    _transport.Send(HostId, VsNet.Build(Msg.Ready, w => w.Write(_transferId)));
                    Plugin.Log.LogInfo("IFZVS guest ready, transfer " + _transferId);
                }
                else if (now - _loadingSince > 40f)
                {
                    Status = "Lade Spielstand ... (dauert lange, Plugin-Log prüfen)";
                }
            }

            if (GuestState == VsGuestState.InGame)
            {
                // Tempo vom Host. Ohne eigenes HQ steht die eigene Uhr (die eigene HQ-Wahl pausiert ohnehin).
                if (_hostSpeed >= 0f && GameAccess.IsGameStarted)
                {
                    float want = HqController.MainHeadquarter == null ? 0f : _hostSpeed;
                    if (!Mathf.Approximately(TimeController.SpeedMultiplier, want))
                        Clock.ApplySpeed(want);
                }
                if (now >= _nextPopulation && Start.Applied)
                {
                    _nextPopulation = now + PopulationInterval;
                    SendToHost(Start.BuildPopulation());
                }
            }
        }

        private void CheckTimeouts(float now)
        {
            if (Role == VsRole.Host)
            {
                List<VsPeer> lost = null;
                foreach (var p in _peers.Values)
                {
                    float limit = p.Loading ? LoadingTimeout : PeerTimeout;
                    if (now - p.LastHeard > limit)
                        (lost ??= new List<VsPeer>()).Add(p);
                }
                if (lost != null)
                    foreach (var p in lost) DropPeer(p.Id, "keine Antwort");
            }
            else if (Role == VsRole.Guest && HostId != CSteamID.Nil)
            {
                float limit = GuestState == VsGuestState.Loading || GuestState == VsGuestState.ReceivingSave ? LoadingTimeout : PeerTimeout;
                if (now - _hostLastHeard > limit)
                    Leave("Host antwortet nicht mehr", sendBye: false);
            }
        }

        // ================================================================== Senden

        public void SendToHost(byte[] message)
        {
            if (Role == VsRole.Guest && HostId != CSteamID.Nil)
                _transport.Send(HostId, message);
        }

        public void SendTo(CSteamID peer, byte[] message) => _transport.Send(peer, message);

        /// <summary>Host: an alle angenommenen Gäste (auch in der Lobby).</summary>
        public void BroadcastWelcomed(byte[] message)
        {
            if (Role != VsRole.Host)
                return;
            foreach (var p in _peers.Values)
                if (p.Welcomed) _transport.Send(p.Id, message);
        }

        /// <summary>Host: an alle Gäste, die den Versus-Spielstand geladen haben.</summary>
        public void BroadcastLoaded(byte[] message)
        {
            if (Role != VsRole.Host)
                return;
            foreach (var p in _peers.Values)
                if (p.Welcomed && p.Loaded) _transport.Send(p.Id, message);
        }

        public byte[] BuildSettings() => VsNet.Build(Msg.VsSettings, w => Settings.Write(w));

        public byte[] BuildRoster() => VsNet.Build(Msg.VsRoster, w => Roster.Write(w));

        /// <summary>Host: Einstellungen geändert (Fenster oder Testbefehl), an alle schicken.</summary>
        public void MarkSettingsChanged()
        {
            if (Role != VsRole.None && Role != VsRole.Host)
                return;
            Settings.Clamp();
            Settings.Revision++;
            _settingsDirty = true;
        }

        public bool SettingsEditable => (Role == VsRole.None || Role == VsRole.Host) && Phase == VsPhase.Lobby;

        // ================================================================== Steam

        private void EnsureSteamHooks()
        {
            if (_steamHooked)
                return;
            _steamHooked = true;
            try
            {
                _steam.Init();
                _crLobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
                _cbLobbyChat = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
                Plugin.Log.LogInfo("IFZVS Steam bereit (Kanal " + VsProtocol.SteamChannel + ")");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("IFZVS Steam-Haken: " + e.Message);
            }
        }

        private bool AcceptPeer(CSteamID peer)
        {
            if (Role == VsRole.Host)
                return Lobby != CSteamID.Nil && !Direct;
            if (Role == VsRole.Guest)
                return peer == HostId;
            return false;
        }

        private bool IsLobbyMember(CSteamID who)
        {
            if (Direct)
                return true;
            int n = SteamMatchmaking.GetNumLobbyMembers(Lobby);
            for (int i = 0; i < n; i++)
                if (SteamMatchmaking.GetLobbyMemberByIndex(Lobby, i) == who)
                    return true;
            return false;
        }

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
            if (Koop != null && Koop.SteamReady)
                MyId = SteamUser.GetSteamID();
        }

        // ================================================================== Host

        /// <summary>Ob jetzt eine Versus-Lobby geöffnet werden darf (Grund für das Fenster).</summary>
        public bool CanHost(out string reason)
        {
            reason = null;
            var koop = Koop;
            if (Role != VsRole.None)
                reason = "Versus läuft schon";
            else if (koop == null || (koop.Role != IfzCoop.Role.None && !koop.VersusHandoverActive))
                reason = "Erst das Koop-Spiel beenden";
            else if (!GameAccess.IsGameStarted)
                reason = "Erst ein neues Spiel in einer frei gewählten Stadt starten";
            else if (MapEssentials.Map.IsConvoy)
                reason = "Versus geht nicht mit einem Konvoi-Start";
            else if (HqController.MainHeadquarter != null)
                reason = "Versus startet vor der HQ-Wahl: neues Spiel in einer frei gewählten Stadt starten (nicht aus der Vorschlagsliste)";
            return reason == null;
        }

        public void StartHost()
        {
            if (!CanHost(out var reason))
            {
                Status = reason;
                Notice(reason);
                return;
            }
            if (Koop == null || !Koop.SteamReady)
            {
                Status = "Steam ist nicht bereit (Direkt hosten geht trotzdem)";
                return;
            }
            UseSteam();
            BecomeHost();
            _lobbyTries = 0;
            CreateLobbyNow();
        }

        public void StartHostDirect(int port)
        {
            if (!CanHost(out var reason))
            {
                Status = reason;
                Notice(reason);
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
            BecomeHost();
            MyId = TcpTransport.HostId;
            Lobby = new CSteamID((ulong)port); // nur Markierung „Lobby offen“
            Roster.BySlot(0).Id = MyId.m_SteamID;
            _rosterSig = null;
            Status = $"Versus-Direkt-Host auf Port {port}. Gäste treten bei, dann „Start“.";
            Plugin.Log.LogInfo("IFZVS host direct " + port);
        }

        private void BecomeHost()
        {
            Role = VsRole.Host;
            Phase = VsPhase.Lobby;
            MySlot = 0;
            VsNet.ResetCounters();
            Roster.Clear();
            Roster.Add(0, MyId.m_SteamID, MyName, PlayerColorSync.ConfiguredColor());
            Settings.Clamp();
            _rosterSig = null;
            _settingsDirty = false;
        }

        private void CreateLobbyNow()
        {
            _lobbyTries++;
            Status = _lobbyTries == 1 ? "Versus-Lobby wird erstellt ..." : $"Steam antwortet nicht, neuer Versuch {_lobbyTries}/{MaxLobbyTries} ...";
            var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, Settings.MaxPlayers);
            _crLobbyCreated.Set(call);
        }

        private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
        {
            if (Role != VsRole.Host || Direct)
                return;
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                Plugin.Log.LogWarning($"IFZVS Lobby-Erstellung Versuch {_lobbyTries}: {(ioFailure ? "IO-Fehler" : result.m_eResult.ToString())}");
                if (_lobbyTries < MaxLobbyTries)
                {
                    CreateLobbyNow();
                    return;
                }
                Reset($"Versus-Lobby konnte nicht erstellt werden ({result.m_eResult}). Ist Steam online?");
                return;
            }
            if (Lobby != CSteamID.Nil)
                return;
            Lobby = new CSteamID(result.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(Lobby, VsProtocol.LobbyKey, VsProtocol.Version.ToString());
            SteamMatchmaking.SetLobbyData(Lobby, "gamever", GameAccess.GameVersion);
            SteamMatchmaking.SetLobbyData(Lobby, "modver", Plugin.Version);
            SteamMatchmaking.SetLobbyData(Lobby, "host", SteamFriends.GetPersonaName());
            SteamFriends.SetRichPresence("connect", "+connect_lobby " + Lobby.m_SteamID);
            Status = "Versus-Lobby " + Lobby.m_SteamID + ". Freunde einladen, dann „Start“.";
            Plugin.Log.LogInfo("IFZVS host lobby " + Lobby.m_SteamID);
            OpenInviteDialog();
        }

        public void OpenInviteDialog()
        {
            if (!Direct && Lobby != CSteamID.Nil)
                SteamFriends.ActivateGameOverlayInviteDialog(Lobby);
        }

        public bool InviteFriend(CSteamID friend)
        {
            return !Direct && Lobby != CSteamID.Nil && SteamMatchmaking.InviteUserToLobby(Lobby, friend);
        }

        /// <summary>Host: Spieler im Teams-Modus einem anderen Team zuordnen.</summary>
        public void SetTeam(int slot, int team)
        {
            if (Role != VsRole.Host || Phase != VsPhase.Lobby)
                return;
            var p = Roster.BySlot(slot);
            if (p != null)
                p.Team = (byte)Mathf.Clamp(team, 0, VsProtocol.MaxPlayers - 1);
        }

        private void UpdateHostRosterStates()
        {
            var me = Roster.BySlot(0);
            if (me != null)
                me.State = Start.MyHqConfirmed ? VsPlayerState.InGame : VsPlayerState.ChoosingHq;
            foreach (var p in _peers.Values)
            {
                var rp = Roster.BySlot(p.Slot);
                if (rp == null)
                    continue;
                if (!p.Loaded && !p.Loading) rp.State = VsPlayerState.Lobby;
                else if (p.Loading) rp.State = VsPlayerState.Loading;
                else if (!p.HqChosen) rp.State = VsPlayerState.ChoosingHq;
                else rp.State = Phase == VsPhase.Running ? VsPlayerState.InGame : VsPlayerState.HqChosen;
            }
        }

        public int LoadedGuestCount
        {
            get
            {
                int n = 0;
                foreach (var p in _peers.Values) if (p.Welcomed && p.Loaded) n++;
                return n;
            }
        }

        /// <summary>Plätze aller Gäste mit geladenem Spielstand (für die Abstimmung).</summary>
        public List<byte> LoadedGuestSlots()
        {
            var list = new List<byte>();
            foreach (var p in _peers.Values) if (p.Welcomed && p.Loaded) list.Add(p.Slot);
            return list;
        }

        public int WelcomedGuestCount
        {
            get
            {
                int n = 0;
                foreach (var p in _peers.Values) if (p.Welcomed) n++;
                return n;
            }
        }

        public VsPeer PeerBySlot(int slot)
        {
            foreach (var p in _peers.Values)
                if (p.Slot == slot) return p;
            return null;
        }

        public void SetHostPhase(VsPhase phase, string text)
        {
            if (Role != VsRole.Host || Phase == phase)
                return;
            Phase = phase;
            Status = text;
            BroadcastWelcomed(BuildPhase(phase, text));
            Plugin.Log.LogInfo("IFZVS phase " + (int)phase + " " + phase);
        }

        private byte[] BuildPhase(VsPhase phase, string text, Action<BinaryWriter> extra = null)
        {
            return VsNet.Build(Msg.VsStartPhase, w =>
            {
                w.Write((byte)phase);
                w.Write(StartMode);
                w.Write(text ?? string.Empty);
                extra?.Invoke(w);
            });
        }

        /// <summary>
        /// Host: „Start“. Speichert den Stand vor der HQ-Wahl und schickt ihn an alle Gäste (S1). Scheitert das Speichern,
        /// oder ist tile = true, startet jeder Rechner dieselbe Kachel selbst (S2, Ersatzweg aus VERSUS-TECHNIK 2.1).
        /// </summary>
        /// <summary>Lobby aus dem Hauptmenü: so viele Gäste werden erwartet; sind sie da, startet die Runde von selbst.</summary>
        public int AutoStartWhen;
        private float _autoStartSince = -1f;

        private void AutoStartUpdate(float now)
        {
            if (AutoStartWhen <= 0 || Role != VsRole.Host || Phase != VsPhase.Lobby)
                return;
            if (_autoStartSince < 0f) _autoStartSince = now;
            int welcomed = WelcomedGuestCount;
            if (welcomed >= AutoStartWhen || (now - _autoStartSince > 60f && welcomed > 0) || now - _autoStartSince > 120f)
            {
                Plugin.Log.LogInfo($"IFZVS Start aus der Hauptmenü-Lobby ({welcomed} von {AutoStartWhen} Gästen)");
                AutoStartWhen = 0;
                _autoStartSince = -1f;
                StartGame();
            }
        }

        public void StartGame(bool tile = false)
        {
            if (Role != VsRole.Host || Phase != VsPhase.Lobby)
                return;
            if (!GameAccess.IsGameStarted || HqController.MainHeadquarter != null)
            {
                Status = "Start geht nur in der HQ-Wahl (Hauptquartier noch nicht gewählt)";
                Notice(Status);
                return;
            }
            var targets = new List<CSteamID>();
            foreach (var p in _peers.Values)
                if (p.Welcomed) targets.Add(p.Id);

            StartMode = tile ? (byte)2 : (byte)1;
            Phase = VsPhase.Transfer;
            BroadcastWelcomed(BuildSettings());
            Diplomacy.HostInit();
            if (targets.Count == 0)
            {
                Phase = VsPhase.Lobby;
                SetHostPhase(VsPhase.ChooseHq, "Versus ohne Gäste gestartet. Wähle dein Hauptquartier.");
                return;
            }
            if (tile)
                StartViaTile(targets);
            else
                SendStartSave(targets);
        }

        private async void SendStartSave(List<CSteamID> targets)
        {
            if (_saving)
                return;
            _saving = true;
            try
            {
                BroadcastWelcomed(BuildPhase(VsPhase.Transfer, "Der Host schickt den Spielstand ..."));
                var map = GameAccess.Map;
                if (map == null)
                    throw new InvalidOperationException("Map nicht gefunden");
                Status = "Speichere den Stand vor der HQ-Wahl ...";
                bool ok = await SaveHandler.SaveGame(map, HostSaveDir, "Versus-Start", autoSave: false);
                if (!ok)
                    throw new IOException("SaveGame meldet Fehler");
                if (Role != VsRole.Host)
                    return;

                string dir = SaveHandler.GetSaveFolderPath(HostSaveDir);
                byte[] blob = SaveBlob.Pack(dir);
                byte[] hash = SaveBlob.Sha256(blob);
                int id = ++_transferCounter;
                foreach (var target in targets)
                {
                    if (!_peers.TryGetValue(target.m_SteamID, out var peer))
                        continue;
                    peer.Loading = true;
                    peer.Loaded = false;
                    peer.LastTransfer = id;
                    peer.LastHeard = Time.realtimeSinceStartup;
                    _transport.Send(target, VsNet.Build(Msg.SaveBegin, w => { w.Write(id); w.Write(blob.Length); w.Write(hash); }));
                    for (int offset = 0; offset < blob.Length; offset += Protocol.ChunkSize)
                    {
                        int len = Math.Min(Protocol.ChunkSize, blob.Length - offset);
                        int o = offset;
                        _transport.Send(target, Protocol.Build(Msg.SaveChunk, w => { w.Write(id); w.Write(o); w.Write(len); w.Write(blob, o, len); }));
                    }
                    _transport.Send(target, VsNet.Build(Msg.SaveEnd, w => w.Write(id)));
                }
                Status = $"Spielstand gesendet ({blob.Length / 1024} KB), Gäste laden ...";
                Plugin.Log.LogInfo($"IFZVS start save {id}: {blob.Length} bytes to {targets.Count} guest(s)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("IFZVS start save FAIL, Ersatzweg Kachel (S2): " + e);
                if (Role == VsRole.Host)
                    StartViaTile(targets);
            }
            finally
            {
                _saving = false;
            }
        }

        private void StartViaTile(List<CSteamID> targets)
        {
            StartMode = 2;
            var c = SaveHandler.Coordinates;
            ParseZoomPadding(SaveHandler.SelectedTilesCoordinates, out int zoom, out int padding);
            int id = ++_transferCounter;
            foreach (var target in targets)
            {
                if (!_peers.TryGetValue(target.m_SteamID, out var peer))
                    continue;
                peer.Loading = true;
                peer.Loaded = false;
                peer.LastTransfer = id;
                peer.LastHeard = Time.realtimeSinceStartup;
                _transport.Send(target, BuildPhase(VsPhase.Transfer, "Jeder startet dieselbe Stadt selbst ...", w =>
                {
                    w.Write(id);
                    w.Write(c.x);
                    w.Write(c.y);
                    w.Write(zoom);
                    w.Write(padding);
                }));
            }
            Status = $"Ersatzweg: Gäste starten die Kachel {c.x},{c.y} selbst ...";
            Plugin.Log.LogInfo($"IFZVS start tile {c.x},{c.y} zoom {zoom} padding {padding}");
        }

        private static void ParseZoomPadding(string coords, out int zoom, out int padding)
        {
            zoom = 15;
            padding = 1;
            if (string.IsNullOrEmpty(coords))
                return;
            var m = System.Text.RegularExpressions.Regex.Match(coords, @"zoom_(\d+)-p_(\d+)");
            if (m.Success)
            {
                int.TryParse(m.Groups[1].Value, out zoom);
                int.TryParse(m.Groups[2].Value, out padding);
            }
        }

        private void HostHandle(CSteamID from, Msg type, BinaryReader r)
        {
            _peers.TryGetValue(from.m_SteamID, out var peer);
            if (type == Msg.VsHello)
            {
                HostOnHello(from, peer, r);
                return;
            }
            if (peer == null || !peer.Welcomed)
                return;

            switch (type)
            {
                case Msg.Ready:
                {
                    int transfer = r.ReadInt32();
                    if (transfer == peer.LastTransfer && peer.Loading)
                    {
                        peer.Loading = false;
                        peer.Loaded = true;
                        Plugin.Log.LogInfo($"IFZVS guest loaded: {peer.Name} (Platz {peer.Slot})");
                        _transport.Send(peer.Id, VsNet.Build(Msg.Speed, w => w.Write(TimeController.SpeedMultiplier)));
                        _transport.Send(peer.Id, Clock.BuildTimeState());
                        Clock.ResendWeather();
                        if (VsVote.Enabled) Vote.OnPlayersChanged();
                        Diplomacy.HostOnPeerLoaded(peer);
                    }
                    break;
                }
                case Msg.VsAllyRequest:
                {
                    byte target = r.ReadByte();
                    byte kind = r.ReadByte();
                    Diplomacy.HostOnRequest(peer.Slot, target, kind);
                    break;
                }
                case Msg.VsAllyAnswer:
                {
                    byte asker = r.ReadByte();
                    bool yes = r.ReadBoolean();
                    Diplomacy.HostOnAnswer(peer.Slot, asker, yes);
                    break;
                }
                case Msg.VsScavenged:
                    Diplomacy.HostOnScavengedMsg(peer, r);
                    break;
                case Msg.VsUnitSnap:
                    Units.HostOnSnap(peer, r);
                    break;
                case Msg.VsUnitRoster:
                    Units.HostOnRoster(peer, r);
                    break;
                case Msg.VsHqChosen:
                    Start.HostOnHqChosen(peer, r);
                    break;
                case Msg.VsPopulation:
                    Start.HostOnPopulation(peer, r);
                    break;
                case Msg.BugReportPart:
                    BugReport.HostOnPart(peer.Name, r);
                    break;
                case Msg.SpeedRequest:
                {
                    // Abstimmung (VsVote): byte 0 = Tempo + int TimeSpeedType, 1 = Leertaste
                    byte kind = r.ReadByte();
                    int speedType = kind == 0 ? r.ReadInt32() : 0;
                    if (!VsVote.Enabled)
                        _transport.Send(peer.Id, VsNet.Build(Msg.Notice, w => w.Write("Pause und Tempo erst, wenn alle ihr HQ gewählt haben")));
                    else if (kind == 1)
                        Vote.Toggle(peer.Slot);
                    else
                        Vote.Cast(peer.Slot, (global::Controllers.Time.TimeSpeedType)speedType);
                    break;
                }
                case Msg.Bye:
                    DropPeer(from, "hat das Versus-Spiel verlassen");
                    break;
            }
        }

        private void HostOnHello(CSteamID from, VsPeer peer, BinaryReader r)
        {
            int proto = r.ReadInt32();
            string modVersion = r.ReadString();
            string gameVersion = r.ReadString();
            string name = r.ReadString();
            bool hasColor = r.ReadBoolean();
            Color32? wish = null;
            if (hasColor)
                wish = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), 255);
            r.ReadString(); // Spiel-ID beim Fortsetzen (V12)

            string reason = null;
            int slot = peer != null ? peer.Slot : Roster.FreeSlot(Settings.MaxPlayers);
            if (!IsLobbyMember(from))
                reason = "nicht in der Lobby";
            else if (proto != VsProtocol.Version)
                reason = $"Versus-Version passt nicht (Host {Plugin.Version}, Gast {modVersion})";
            else if (gameVersion != GameAccess.GameVersion)
                reason = $"Spielversion passt nicht (Host {GameAccess.GameVersion}, Gast {gameVersion})";
            else if (Phase != VsPhase.Lobby && peer == null)
                reason = "Das Versus-Spiel hat schon begonnen";
            else if (slot < 0)
                reason = $"Lobby voll ({Settings.MaxPlayers} Spieler)";

            if (reason != null)
            {
                _transport.Send(from, VsNet.Build(Msg.VsWelcome, w => { w.Write(false); w.Write(reason); w.Write((byte)0); w.Write(0UL); }));
                Plugin.Log.LogWarning($"IFZVS Gast {name} abgelehnt: {reason}");
                return;
            }

            if (peer == null)
            {
                peer = new VsPeer { Id = from, Slot = (byte)slot };
                _peers[from.m_SteamID] = peer;
            }
            peer.Name = name;
            peer.Welcomed = true;
            peer.LastHeard = Time.realtimeSinceStartup;
            Roster.Add(peer.Slot, from.m_SteamID, name, wish);
            _transport.Send(from, VsNet.Build(Msg.VsWelcome, w => { w.Write(true); w.Write(string.Empty); w.Write(peer.Slot); w.Write(from.m_SteamID); }));
            _transport.Send(from, BuildSettings());
            _rosterSig = null;
            Status = $"{name} ist in der Lobby (Platz {peer.Slot + 1})";
            Plugin.Log.LogInfo($"IFZVS guest joined: {name} ({from.m_SteamID}) slot {peer.Slot}");
        }

        private void DropPeer(CSteamID id, string reason)
        {
            if (!_peers.TryGetValue(id.m_SteamID, out var peer))
                return;
            _peers.Remove(id.m_SteamID);
            Roster.Remove(peer.Slot);
            _transport.Close(id);
            _rosterSig = null;
            Status = $"{peer.Name} {reason}";
            Notice($"Versus: {peer.Name} {reason}");
            Plugin.Log.LogInfo($"IFZVS guest left: {peer.Name} ({reason})");
            if (VsVote.Enabled) Vote.OnPlayersChanged();
        }

        /// <summary>Postfix von TimeChangerController.SetSpeedMultiplier beim Host: jede Tempoänderung an alle.</summary>
        public void OnLocalSpeedApplied(float speed)
        {
            if (Role != VsRole.Host || _peers.Count == 0)
                return;
            if (Mathf.Approximately(speed, _lastBroadcastSpeed))
                return;
            _lastBroadcastSpeed = speed;
            BroadcastLoaded(VsNet.Build(Msg.Speed, w => w.Write(speed)));
        }

        // ================================================================== Gast

        /// <summary>
        /// Von Session.OnLobbyEnter (Koop): Die Steam-Einladung landet immer bei der Koop-Session. Ist die Lobby eine
        /// Versus-Lobby, übernimmt sie diese Sitzung, und die Koop-Session bleibt auf Role.None.
        /// </summary>
        public bool AdoptLobby(CSteamID lobby)
        {
            string version = SteamMatchmaking.GetLobbyData(lobby, VsProtocol.LobbyKey);
            if (string.IsNullOrEmpty(version))
                return false;
            if (Role == VsRole.Host)
            {
                SteamMatchmaking.LeaveLobby(lobby);
                Notice("Du hostest gerade ein Versus-Spiel, erst beenden");
                return true;
            }
            if (Role == VsRole.Guest)
                Leave("wechsle die Versus-Lobby", sendBye: true);
            if (version != VsProtocol.Version.ToString())
            {
                SteamMatchmaking.LeaveLobby(lobby);
                string modver = SteamMatchmaking.GetLobbyData(lobby, "modver");
                Status = $"Versus-Version passt nicht (Host {modver}, du {Plugin.Version})";
                Notice(Status, 10f);
                return true;
            }
            UseSteam();
            Role = VsRole.Guest;
            Lobby = lobby;
            HostId = SteamMatchmaking.GetLobbyOwner(lobby);
            _hostLastHeard = Time.realtimeSinceStartup;
            GuestState = VsGuestState.WaitingWelcome;
            VsNet.ResetCounters();
            Status = "Versus-Lobby von " + SteamMatchmaking.GetLobbyData(lobby, "host") + ", warte auf Antwort ...";
            SendHello();
            Plugin.Log.LogInfo($"IFZVS guest entered lobby {lobby.m_SteamID}, host {HostId.m_SteamID}");
            return true;
        }

        /// <summary>Gast: Versus-Lobby per ID beitreten (die Koop-Session tritt bei und übergibt in OnLobbyEnter).</summary>
        public void JoinLobbyById(ulong lobby)
        {
            if (Role == VsRole.Host)
                return;
            Koop?.JoinLobby(new CSteamID(lobby));
        }

        public void JoinDirect(string host, int port)
        {
            if (Role != VsRole.None)
                return;
            if (Koop != null && Koop.Role != IfzCoop.Role.None)
            {
                Status = "Erst das Koop-Spiel beenden";
                return;
            }
            try
            {
                UseTcp().Connect(host, port);
            }
            catch (Exception e)
            {
                UseSteam();
                Status = $"Versus-Verbindung zu {host}:{port} fehlgeschlagen: {e.Message}";
                Plugin.Log.LogWarning(Status);
                return;
            }
            Role = VsRole.Guest;
            HostId = TcpTransport.HostId;
            Lobby = new CSteamID((ulong)port);
            _hostLastHeard = Time.realtimeSinceStartup;
            GuestState = VsGuestState.WaitingWelcome;
            VsNet.ResetCounters();
            Status = $"Verbunden mit {host}:{port}, warte auf Antwort ...";
            SendHello();
            Plugin.Log.LogInfo($"IFZVS guest direct {host}:{port}");
        }

        private void SendHello()
        {
            var wish = PlayerColorSync.ConfiguredColor();
            _transport.Send(HostId, VsNet.Build(Msg.VsHello, w =>
            {
                w.Write(VsProtocol.Version);
                w.Write(Plugin.Version);
                w.Write(GameAccess.GameVersion);
                w.Write(MyName);
                w.Write(wish.HasValue);
                if (wish.HasValue)
                {
                    w.Write(wish.Value.r);
                    w.Write(wish.Value.g);
                    w.Write(wish.Value.b);
                }
                w.Write(string.Empty);
            }));
        }

        private void GuestHandle(CSteamID from, Msg type, BinaryReader r)
        {
            if (from != HostId)
                return;

            switch (type)
            {
                case Msg.VsWelcome:
                {
                    bool accepted = r.ReadBoolean();
                    string reason = r.ReadString();
                    byte slot = r.ReadByte();
                    ulong assigned = r.ReadUInt64();
                    if (!accepted)
                    {
                        Leave("Host lehnt ab: " + reason, sendBye: false);
                        Notice("Versus: Host lehnt ab: " + reason, 10f);
                        return;
                    }
                    MySlot = slot;
                    if (Direct)
                        MyId = new CSteamID(assigned);
                    GuestState = VsGuestState.InLobby;
                    Status = $"In der Versus-Lobby (Platz {slot + 1}). Warte auf den Start des Hosts.";
                    break;
                }
                case Msg.VsSettings:
                    Settings = VsSettings.Read(r);
                    break;

                case Msg.VsRoster:
                    Roster.Read(r);
                    break;

                case Msg.VsStartPhase:
                    GuestOnPhase(r);
                    break;

                case Msg.SaveBegin:
                {
                    _transferId = r.ReadInt32();
                    int total = r.ReadInt32();
                    _transferHash = r.ReadBytes(32);
                    if (total <= 0 || total > 512 * 1024 * 1024)
                    {
                        Leave("ungültige Spielstandgröße");
                        return;
                    }
                    _transferBuffer = new byte[total];
                    _transferReceived = 0;
                    GuestState = VsGuestState.ReceivingSave;
                    Status = $"Empfange Versus-Spielstand ({total / 1024} KB) ...";
                    break;
                }
                case Msg.SaveChunk:
                {
                    int chunkTransfer = r.ReadInt32();
                    int offset = r.ReadInt32();
                    int length = r.ReadInt32();
                    if (chunkTransfer != _transferId || _transferBuffer == null || offset < 0 || length < 0 || offset + length > _transferBuffer.Length)
                        return;
                    var data = r.ReadBytes(length);
                    Buffer.BlockCopy(data, 0, _transferBuffer, offset, data.Length);
                    _transferReceived += data.Length;
                    break;
                }
                case Msg.SaveEnd:
                    if (r.ReadInt32() == _transferId)
                        FinishTransfer();
                    break;

                case Msg.Speed:
                    _hostSpeed = r.ReadSingle();
                    break;

                case Msg.TimeState:
                    _hostSpeed = Clock.GuestOnTimeState(r, _hostRttMs, GuestState == VsGuestState.InGame && HqController.MainHeadquarter != null);
                    break;

                case Msg.VsWeather:
                    if (GuestState == VsGuestState.InGame)
                        Clock.GuestOnWeather(r);
                    break;

                case Msg.VsHqAnswer:
                    if (GuestState == VsGuestState.InGame)
                        Start.GuestOnHqAnswer(r);
                    break;

                case Msg.VsVoteStatus:
                    Vote.Text = r.ReadString();
                    break;
                case Msg.VsRelations:
                    Diplomacy.GuestOnRelations(r);
                    break;
                case Msg.VsAllyAsk:
                    Diplomacy.LocalOnAsk(r.ReadByte());
                    break;
                case Msg.VsAllyAnswer:
                    Diplomacy.GuestOnAnswer(r);
                    break;
                case Msg.VsScavenged:
                    Diplomacy.GuestOnScavenged(r);
                    break;
                case Msg.VsUnitSnap:
                    Units.GuestOnSnap(r);
                    break;
                case Msg.VsUnitRoster:
                    Units.GuestOnRoster(r);
                    break;
                case Msg.Notice:
                    Notice(r.ReadString());
                    break;

                case Msg.Bye:
                    Leave("Host hat beendet: " + r.ReadString(), sendBye: false);
                    break;
            }
        }

        private void GuestOnPhase(BinaryReader r)
        {
            var phase = (VsPhase)r.ReadByte();
            StartMode = r.ReadByte();
            string text = r.ReadString();
            var old = Phase;
            Phase = phase;
            if (!string.IsNullOrEmpty(text) && phase != old)
            {
                Status = text;
                Notice("Versus: " + text);
            }
            if (phase == VsPhase.Transfer && StartMode == 2 && r.BaseStream.Position < r.BaseStream.Length)
            {
                _transferId = r.ReadInt32();
                _tileX = r.ReadInt32();
                _tileY = r.ReadInt32();
                _tileZoom = r.ReadInt32();
                _tilePadding = r.ReadInt32();
                BeginLoading("Starte dieselbe Stadt wie der Host ...");
                if (ZenjectExtensions.Installers.ScenesManager.IsMainSceneActive && !ZenjectExtensions.Installers.ScenesManager.IsSceneSwitching)
                {
                    LoadTileNow();
                }
                else
                {
                    _pendingTileLoad = true;
                    try { ZenjectExtensions.Installers.ScenesManager.GoToMenu(); }
                    catch (Exception e) { Plugin.Log.LogError("IFZVS ins Hauptmenü: " + e); }
                }
            }
            Plugin.Log.LogInfo("IFZVS guest phase " + phase);
        }

        private void LoadTileNow()
        {
            try
            {
                Plugin.Log.LogInfo($"IFZVS guest starts tile {_tileX},{_tileY}");
                Commands.MenuConsoleCommandsHandler.LoadTileWithConfiguration(_tileX, _tileY, _tileZoom, _tilePadding, 9, Vector2Int.zero, fromSuggestedCitiesList: false);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("IFZVS Kachel starten: " + e);
                Leave("Stadt konnte nicht gestartet werden: " + e.Message);
            }
        }

        private void BeginLoading(string status)
        {
            GuestState = VsGuestState.Loading;
            _sawGameStopped = !GameAccess.IsGameStarted;
            _loadingSince = Time.realtimeSinceStartup;
            Status = status;
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

                BeginLoading("Lade den Versus-Spielstand des Hosts ...");
                Plugin.Log.LogInfo("IFZVS guest loading save " + dir);
                validator.TryRun(dir, info);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("IFZVS load FAIL: " + e);
                Leave("Spielstand konnte nicht geladen werden: " + e.Message);
            }
        }

        // ================================================================== gemeinsam

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
                Plugin.Log.LogWarning("IFZVS kaputte Nachricht von " + from.m_SteamID + ": " + e.Message);
                return;
            }

            using (r)
            {
                VsNet.CountReceived(type, data.Length);
                float now = Time.realtimeSinceStartup;
                if (Role == VsRole.Host && _peers.TryGetValue(from.m_SteamID, out var p))
                    p.LastHeard = now;
                else if (Role == VsRole.Guest && from == HostId)
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
                        double rtt = (Time.realtimeSinceStartupAsDouble - r.ReadDouble()) * 1000.0;
                        if (Role == VsRole.Host && _peers.TryGetValue(from.m_SteamID, out var peer)) peer.RttMs = rtt;
                        else if (Role == VsRole.Guest) _hostRttMs = rtt;
                        return;
                    }
                    if (Role == VsRole.Host)
                        HostHandle(from, type, r);
                    else if (Role == VsRole.Guest)
                        GuestHandle(from, type, r);
                }
                catch (EndOfStreamException)
                {
                    Plugin.Log.LogWarning($"IFZVS zu kurze Nachricht {type} von {from.m_SteamID}");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"IFZVS Nachricht {type}: {e}");
                }
            }
        }

        private void OnSessionFailed(CSteamID peer)
        {
            if (Role == VsRole.Host)
                DropPeer(peer, "Verbindung verloren");
            else if (Role == VsRole.Guest && peer == HostId)
                Leave("Verbindung zum Host verloren", sendBye: false);
        }

        private void OnLobbyChatUpdate(LobbyChatUpdate_t update)
        {
            if (Direct || Lobby == CSteamID.Nil || update.m_ulSteamIDLobby != Lobby.m_SteamID)
                return;
            const uint leftMask = 2 | 4 | 8 | 16;
            if ((update.m_rgfChatMemberStateChange & leftMask) == 0)
                return;
            var who = new CSteamID(update.m_ulSteamIDUserChanged);
            if (Role == VsRole.Host)
                DropPeer(who, "hat die Lobby verlassen");
            else if (Role == VsRole.Guest && who == HostId)
                Leave("Host hat die Lobby verlassen", sendBye: false);
        }

        /// <summary>Versus beenden. Das eigene Spiel läuft weiter (jeder hat seine eigene Basis).</summary>
        public void Leave(string reason = "beendet", bool sendBye = true)
        {
            if (Role == VsRole.None)
                return;
            if (sendBye)
            {
                var bye = Protocol.Build(Msg.Bye, w => w.Write(reason));
                if (Role == VsRole.Host)
                    foreach (var p in _peers.Values) _transport.Send(p.Id, bye);
                else if (HostId != CSteamID.Nil)
                    _transport.Send(HostId, bye);
                _transport.Flush();
            }
            if (!Direct && Lobby != CSteamID.Nil && Koop != null && Koop.SteamReady)
            {
                SteamMatchmaking.LeaveLobby(Lobby);
                SteamFriends.SetRichPresence("connect", string.Empty);
            }
            _transport.CloseAll();
            UseSteam();
            Reset(reason);
            Notice("Versus beendet (" + reason + ")", 10f);
            Plugin.Log.LogInfo("IFZVS session ended: " + reason);
        }

        private void Reset(string status)
        {
            Role = VsRole.None;
            GuestState = VsGuestState.Idle;
            Phase = VsPhase.Lobby;
            Lobby = CSteamID.Nil;
            HostId = CSteamID.Nil;
            MySlot = 0;
            StartMode = 0;
            _peers.Clear();
            Roster.Clear();
            _rosterSig = null;
            _settingsDirty = false;
            _saving = false;
            _transferBuffer = null;
            _pendingTileLoad = false;
            _hostSpeed = -1f;
            _hostRttMs = -1;
            _lastBroadcastSpeed = -1f;
            Start.Reset();
            Clock.Reset();
            Vote.Reset();
            Diplomacy.Reset();
            Units.Reset();
            Status = status;
        }

        public void Shutdown()
        {
            if (Active)
                Leave("Spiel beendet");
        }
    }
}
