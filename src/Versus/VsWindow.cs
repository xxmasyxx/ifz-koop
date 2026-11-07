using System.Collections.Generic;
using System.Globalization;
using IfzCoop.Game;
using Steamworks;
using UnityEngine;

namespace IfzCoop.Versus
{
    /// <summary>
    /// Versus-Teil des F6-Fensters (CoopWindow): Knopf „Versus hosten“ neben „Koop hosten“, Lobby mit Spielerliste,
    /// Start-Einstellungen (Host bearbeitet, Gäste sehen sie), Start und HQ-Wartefenster. Während Versus läuft, zeigt das
    /// F6-Fenster nur diesen Teil (die Koop-Knöpfe sind dann aus, und umgekehrt).
    /// </summary>
    internal static class VsWindow
    {
        private const uint GameAppId = 1465460;
        private static Vector2 _scroll;
        private static bool _showSettings = true;
        private static bool _showFriends;
        private static Vector2 _friendScroll;
        private static readonly List<(CSteamID id, string name, bool inGame, bool invited)> Friends = new List<(CSteamID, string, bool, bool)>();
        private static readonly Dictionary<Gameplay.GameResources.ResourceID, string> ResInputs = new Dictionary<Gameplay.GameResources.ResourceID, string>();
        private static string _workersInput;
        private static GUIStyle _hudStyle;

        private static readonly string[] ModeLabels = { "Jeder gegen jeden", "Teams", "Alle verbündet" };
        private static readonly string[] MissionLabels = { "Wettlauf", "Eigene Story", "Beides" };

        private static VersusSession Vs => VersusSession.Instance;

        // ================================================================ Knöpfe im Koop-Teil

        /// <summary>Im Koop-Leerlauf neben „Koop hosten“.</summary>
        public static void DrawHostButton()
        {
            var vs = Vs;
            if (vs == null)
                return;
            bool can = vs.CanHost(out _);
            GUI.enabled = can;
            if (GUILayout.Button("Versus hosten"))
                vs.StartHost();
            GUI.enabled = true;
        }

        /// <summary>Im Koop-Leerlauf unter der Direktverbindung: Versus über IP/Port (Standard-Port 27121).</summary>
        public static void DrawDirectButtons(string input)
        {
            var vs = Vs;
            if (vs == null)
                return;
            GUILayout.BeginHorizontal();
            GUI.enabled = vs.CanHost(out _);
            if (GUILayout.Button("Versus direkt hosten"))
                vs.StartHostDirect(ParsePort(input, true));
            GUI.enabled = true;
            if (GUILayout.Button("Versus direkt beitreten"))
            {
                var parts = (input ?? string.Empty).Trim().Split(':');
                vs.JoinDirect(parts[0], parts.Length > 1 ? ParsePort(parts[1], false) : VsProtocol.DefaultPort);
            }
            GUILayout.EndHorizontal();
            if (!vs.CanHost(out var why) && GameAccess.IsGameStarted)
                GUILayout.Label("Versus: " + why);
            else if (!GameAccess.IsGameStarted)
                GUILayout.Label("Versus hosten: neues Spiel in frei gewählter Stadt starten, dann in der HQ-Wahl hier hosten.");
            GUILayout.Label("Versus beitreten: Steam-Einladung annehmen oder Lobby-ID oben eingeben.");
        }

        private static int ParsePort(string text, bool hosting)
        {
            var last = (text ?? string.Empty).Trim().Split(':');
            if (int.TryParse(last[last.Length - 1], out var port) && port > 0 && port < 65536)
                return hosting && port == 27120 ? VsProtocol.DefaultPort : port; // Koop-Standard -> Versus-Standard
            return VsProtocol.DefaultPort;
        }

        // ================================================================ Anzeige ohne Fenster

        /// <summary>Statuszeile unten und Warte-Kasten während der HQ-Wahl (auch bei geschlossenem Fenster).</summary>
        public static void DrawHud(bool windowVisible)
        {
            var vs = Vs;
            if (vs == null || !VersusSession.Active)
                return;

            var start = vs.Start;
            string text = null;
            bool hostWait = vs.Role == VsRole.Host && start.HostPending;
            if (hostWait)
                text = $"Warte auf die Gäste: {start.ChosenCount} von {start.GuestTotal} haben ihr Hauptquartier gewählt (noch {start.HostWaitLeft:0} s)";
            else if (vs.Role == VsRole.Guest && start.AwaitingAnswer)
                text = "HQ-Wahl an den Host gemeldet, warte auf Bestätigung ...";
            else if (vs.Role == VsRole.Guest && vs.GuestState == VsGuestState.InGame && start.MyHqConfirmed && vs.Phase != VsPhase.Running)
                text = "Dein Hauptquartier steht. Die Uhr läuft, sobald der Host gewählt hat.";
            else if (vs.Role == VsRole.Host && vs.Phase == VsPhase.ChooseHq && !start.MyHqConfirmed)
                text = $"Versus: Wähle dein Hauptquartier als Letzter ({start.ChosenCount} von {start.GuestTotal} Gästen fertig)";
            if (text == null && VsVote.Enabled && !string.IsNullOrEmpty(vs.Vote.Text))
                text = vs.Vote.Text;
            if (text == null)
                return;
            if (_hudStyle == null)
                _hudStyle = new GUIStyle(GUI.skin.box) { fontSize = 15, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            float width = Mathf.Min(640f, Screen.width - 40f);
            float x = (Screen.width - width) / 2f;
            GUI.Box(new Rect(x, 112f, width, hostWait ? 64f : 36f), text, _hudStyle);
            if (hostWait)
            {
                if (GUI.Button(new Rect(x + width / 2f - 210f, 148f, 200f, 24f), "Ohne die übrigen starten"))
                    start.ForceStart = true;
                if (GUI.Button(new Rect(x + width / 2f + 10f, 148f, 200f, 24f), "HQ-Wahl abbrechen"))
                {
                    var setter = GameAccess.Game<Controllers.HqSetterController>();
                    if (setter != null)
                        HarmonyLib.Traverse.Create(setter).Method("OnSelectionDenied").GetValue();
                }
            }
        }

        // ================================================================ Fenster

        /// <summary>true = Versus läuft, der Koop-Teil des Fensters wird nicht gezeichnet.</summary>
        public static bool DrawPanel(Session koop)
        {
            var vs = Vs;
            if (vs == null || !VersusSession.Active)
                return false;

            GUILayout.Label($"VERSUS ({(vs.Role == VsRole.Host ? "Host" : "Gast")}, Platz {vs.MySlot + 1}): {vs.Status}");
            if (vs.Role == VsRole.Host)
                DrawHostLobby(vs);
            else
                DrawGuestInfo(vs);

            DrawPlayers(vs);

            _showSettings = GUILayout.Toggle(_showSettings, "Start-Einstellungen anzeigen");
            if (_showSettings)
            {
                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(300f));
                DrawSettings(vs, vs.Role == VsRole.Host && vs.SettingsEditable);
                GUILayout.EndScrollView();
            }

            if (vs.Role == VsRole.Host)
            {
                if (vs.Phase == VsPhase.Lobby)
                {
                    GUI.enabled = GameAccess.IsGameStarted && Controllers.HqController.MainHeadquarter == null;
                    if (GUILayout.Button($"Start ({vs.WelcomedGuestCount} Gäste): Spielstand an alle, dann HQ-Wahl"))
                        vs.StartGame();
                    GUI.enabled = true;
                }
                else if (vs.Phase == VsPhase.ChooseHq && !vs.Start.MyHqConfirmed)
                {
                    GUILayout.Label($"HQ-Wahl: {vs.Start.ChosenCount} von {vs.Start.GuestTotal} Gästen fertig. Du wählst als Letzter.");
                    if (!vs.Start.ForceStart && GUILayout.Button("Nicht auf die übrigen warten"))
                        vs.Start.ForceStart = true;
                }
            }

            if (GUILayout.Button(vs.Role == VsRole.Host ? "Versus beenden" : "Versus verlassen"))
                vs.Leave(vs.Role == VsRole.Host ? "Host hat beendet" : "Gast hat verlassen");
            return true;
        }

        private static void DrawHostLobby(VersusSession vs)
        {
            if (vs.Direct)
            {
                GUILayout.Label("Direktverbindung, Port " + vs.Lobby.m_SteamID);
                return;
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Lobby-ID: " + vs.Lobby.m_SteamID);
            if (GUILayout.Button("Kopieren", GUILayout.Width(80f)))
                GUIUtility.systemCopyBuffer = vs.Lobby.m_SteamID.ToString();
            GUILayout.EndHorizontal();
            if (vs.Phase != VsPhase.Lobby)
                return;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Steam-Einladung (Overlay)"))
                vs.OpenInviteDialog();
            if (GUILayout.Button(_showFriends ? "Freundesliste zu" : "Freunde einladen"))
            {
                _showFriends = !_showFriends;
                if (_showFriends) RefreshFriends();
            }
            GUILayout.EndHorizontal();
            if (_showFriends)
                DrawFriends(vs);
        }

        private static void DrawGuestInfo(VersusSession vs)
        {
            if (vs.GuestState == VsGuestState.ReceivingSave)
                GUILayout.Label($"Übertragung: {vs.TransferProgress * 100f:0} %");
            if (vs.HostRttMs >= 0)
                GUILayout.Label($"Ping zum Host: {vs.HostRttMs:0} ms, Zeitabweichung {vs.Clock.DriftSeconds:0.0} s (nachgestellt {vs.Clock.Corrections}x)");
            GUILayout.Label("Pause und Tempo bestimmt nur der Host.");
        }

        private static void DrawPlayers(VersusSession vs)
        {
            GUILayout.Label($"Spieler ({vs.Roster.Count} von höchstens {vs.Settings.MaxPlayers}):");
            foreach (var p in vs.Roster.Players)
            {
                GUILayout.BeginHorizontal();
                Swatch(p.Color);
                string ping = "";
                if (vs.Role == VsRole.Host)
                {
                    var peer = vs.PeerBySlot(p.Slot);
                    if (peer != null && peer.RttMs >= 0) ping = $", {peer.RttMs:0} ms";
                }
                string me = p.Slot == vs.MySlot ? " (du)" : "";
                GUILayout.Label($"{p.Slot + 1}. {p.Name}{me}: {VsRoster.StateLabel(p.State)}{ping}", GUILayout.Width(250f));
                if (vs.Settings.Mode == VsMode.Teams)
                {
                    if (vs.Role == VsRole.Host && vs.Phase == VsPhase.Lobby)
                    {
                        if (GUILayout.Button("Team " + (p.Team + 1), GUILayout.Width(70f)))
                            vs.SetTeam(p.Slot, (p.Team + 1) % 4);
                    }
                    else
                    {
                        GUILayout.Label("Team " + (p.Team + 1), GUILayout.Width(70f));
                    }
                }
                GUILayout.EndHorizontal();
            }
        }

        private static void Swatch(Color c)
        {
            var r = GUILayoutUtility.GetRect(14f, 14f, GUILayout.Width(14f), GUILayout.Height(14f));
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        private static void DrawSettings(VersusSession vs, bool edit)
        {
            var s = vs.Settings;
            bool changed = false;
            var inv = CultureInfo.InvariantCulture;
            if (!edit)
                GUILayout.Label(vs.Role == VsRole.Host ? "(nach dem Start nicht mehr änderbar)" : "(legt der Host fest)");

            GUILayout.Label("Spielart:");
            int mode = Choice((int)s.Mode, ModeLabels, edit);
            if (mode != (int)s.Mode) { s.Mode = (VsMode)mode; changed = true; }

            GUILayout.Label("Einwohner am Anfang (Arbeiter):");
            var presets = VsSettings.WorkerPresets;
            int presetIdx = System.Array.IndexOf(presets, s.Workers);
            var labels = new[] { presets[0].ToString(), presets[1].ToString(), presets[2].ToString(), "frei" };
            int chosen = Choice(presetIdx < 0 ? 3 : presetIdx, labels, edit);
            if (chosen < 3 && chosen != presetIdx) { s.Workers = presets[chosen]; _workersInput = null; changed = true; }
            if (chosen == 3)
            {
                if (_workersInput == null) _workersInput = s.Workers.ToString(inv);
                if (edit)
                {
                    GUILayout.BeginHorizontal();
                    _workersInput = GUILayout.TextField(_workersInput, 4, GUILayout.Width(60f));
                    GUILayout.Label("(0 = wie im Spiel)");
                    GUILayout.EndHorizontal();
                    if (int.TryParse(_workersInput, NumberStyles.Integer, inv, out var w) && w != s.Workers && w >= 0) { s.Workers = w; changed = true; }
                }
                else
                {
                    GUILayout.Label("  " + s.Workers);
                }
            }

            GUILayout.Label("Trupps am Anfang:");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Anzahl", GUILayout.Width(60f));
            changed |= Stepper(ref s.Squads, 0, 6, edit);
            GUILayout.Label("Größe", GUILayout.Width(50f));
            changed |= Stepper(ref s.SquadSize, 1, 10, edit);
            GUILayout.EndHorizontal();

            GUILayout.Label("Start-Rohstoffe (zusätzlich zur Startausstattung im HQ):");
            foreach (var (label, id) in VsSettings.ResourceChoices)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(label, GUILayout.Width(130f));
                int cur = s.ResourceAmount(id);
                if (edit)
                {
                    if (!ResInputs.TryGetValue(id, out var text) || (int.TryParse(text, NumberStyles.Integer, inv, out var tv) && tv != cur && GUIUtility.keyboardControl == 0))
                        text = cur.ToString(inv);
                    text = GUILayout.TextField(text, 6, GUILayout.Width(70f));
                    ResInputs[id] = text;
                    if (int.TryParse(text, NumberStyles.Integer, inv, out var v) && v >= 0 && v != cur)
                    {
                        s.Resources[id] = v;
                        changed = true;
                    }
                }
                else
                {
                    GUILayout.Label(cur.ToString(inv));
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Label("Durchsuchte Häuser sichtbar:");
            int scav = Choice(s.ScavengedVisible ? 0 : 1, new[] { "ja (alle sehen es)", "nein (nur die eigenen)" }, edit);
            if ((scav == 0) != s.ScavengedVisible) { s.ScavengedVisible = scav == 0; changed = true; }

            GUILayout.Label("Missionen:");
            int mis = Choice((int)s.Missions, MissionLabels, edit);
            if (mis != (int)s.Missions) { s.Missions = (VsMissions)mis; changed = true; }

            GUILayout.Label("Sicht im Bündnis:");
            int sight = Choice(s.SharedAllySight ? 0 : 1, new[] { "geteilt", "getrennt" }, edit);
            if ((sight == 0) != s.SharedAllySight) { s.SharedAllySight = sight == 0; changed = true; }

            GUILayout.Label("Sieg:");
            int vic = Choice(s.Victory ? 0 : 1, new[] { "Letzter mit Bevölkerung gewinnt", "ohne Sieg" }, edit);
            if ((vic == 0) != s.Victory) { s.Victory = vic == 0; changed = true; }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Spieler höchstens", GUILayout.Width(120f));
            changed |= Stepper(ref s.MaxPlayers, 2, VsProtocol.MaxPlayers, edit);
            GUILayout.EndHorizontal();

            GUILayout.Label("Zombies, Horden und Banditen: gemeinsam (rechnet der Host), sichtbar nur in der eigenen Sichtweite.");
            GUILayout.Label("Pause und Spieltempo: nur der Host.");

            if (changed && edit)
                vs.MarkSettingsChanged();
        }

        private static int Choice(int current, string[] labels, bool edit)
        {
            if (!edit)
            {
                GUILayout.Label("  " + labels[Mathf.Clamp(current, 0, labels.Length - 1)]);
                return current;
            }
            return GUILayout.Toolbar(current, labels);
        }

        private static bool Stepper(ref int value, int min, int max, bool edit)
        {
            if (!edit)
            {
                GUILayout.Label(value.ToString(CultureInfo.InvariantCulture), GUILayout.Width(30f));
                return false;
            }
            int old = value;
            if (GUILayout.Button("-", GUILayout.Width(24f))) value--;
            GUILayout.Label(value.ToString(CultureInfo.InvariantCulture), GUILayout.Width(26f));
            if (GUILayout.Button("+", GUILayout.Width(24f))) value++;
            value = Mathf.Clamp(value, min, max);
            return value != old;
        }

        // ================================================================ Freunde

        private static void RefreshFriends()
        {
            Friends.Clear();
            int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < count; i++)
            {
                var id = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (SteamFriends.GetFriendPersonaState(id) == EPersonaState.k_EPersonaStateOffline)
                    continue;
                bool inGame = SteamFriends.GetFriendGamePlayed(id, out var game) && game.m_gameID.AppID().m_AppId == GameAppId;
                Friends.Add((id, SteamFriends.GetFriendPersonaName(id), inGame, false));
            }
            Friends.Sort((a, b) => a.inGame != b.inGame ? (a.inGame ? -1 : 1) : string.Compare(a.name, b.name, System.StringComparison.OrdinalIgnoreCase));
        }

        private static void DrawFriends(VersusSession vs)
        {
            if (Friends.Count == 0)
            {
                GUILayout.Label("Keine Freunde online.");
                return;
            }
            _friendScroll = GUILayout.BeginScrollView(_friendScroll, GUILayout.Height(120f));
            for (int i = 0; i < Friends.Count; i++)
            {
                var f = Friends[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label((f.inGame ? "[IFZ] " : "") + f.name, GUILayout.Width(220f));
                if (f.invited)
                    GUILayout.Label("eingeladen");
                else if (GUILayout.Button("Einladen"))
                    Friends[i] = (f.id, f.name, f.inGame, vs.InviteFriend(f.id));
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }
    }
}
