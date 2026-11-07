using System.Collections.Generic;
using IfzCoop.Game;
using Steamworks;
using UnityEngine;

namespace IfzCoop
{
    /// <summary>Kleines IMGUI-Fenster: hosten, einladen, beitreten, Zustand ansehen.</summary>
    internal sealed class CoopWindow
    {
        private const int WindowId = 0x1F2C00;

        private readonly Session _session;
        private Rect _rect = new Rect(20f, 140f, 360f, 10f);
        private string _lobbyInput = string.Empty;

        private const int DefaultPort = 27120;
        private string _directInput = "27120";

        private static int ParsePort(string text)
        {
            var last = (text ?? string.Empty).Trim().Split(':');
            return int.TryParse(last[last.Length - 1], out var port) && port > 0 && port < 65536 ? port : DefaultPort;
        }

        private const uint GameAppId = 1465460;
        private readonly List<FriendEntry> _friends = new List<FriendEntry>();
        private Vector2 _friendScroll;
        private bool _showFriends;

        private sealed class FriendEntry
        {
            public CSteamID Id;
            public string Name;
            public bool InThisGame;
            public bool Invited;
        }

        public bool Visible;

        public CoopWindow(Session session)
        {
            _session = session;
        }

        public void Draw()
        {
            if (_session.Role != Role.None)
                _session.Presence.Draw();
            // Meldungen zeichnet Menu.NoticeUi im Stil des Spiels; der Stand steht in den Kacheln (Hauptmenü/Esc-Menü).
            Versus.VsWindow.DrawHud(Visible);
            if (!Visible)
                return;
            _rect = GUILayout.Window(WindowId, _rect, DrawContents, $"{Plugin.Name} {Plugin.Version}");
            Menu.ModUi.Report(WindowId, _rect);
        }

        private GUIStyle _noticeStyle;

        // Meldung oben in der Mitte, auch bei geschlossenem Fenster (z. B. „Max hat geantwortet: …“).
        private void DrawNotice()
        {
            if (string.IsNullOrEmpty(_session.Notice) || Time.realtimeSinceStartup > _session.NoticeUntil)
                return;
            if (_noticeStyle == null)
            {
                _noticeStyle = new GUIStyle(GUI.skin.box)
                {
                    fontSize = 16,
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true,
                };
            }
            float width = Mathf.Min(700f, Screen.width - 40f);
            GUI.Box(new Rect((Screen.width - width) / 2f, 60f, width, 44f), _session.Notice, _noticeStyle);
        }

        private static readonly string[] AnswerModeLabels = { "Alle", "Nur Host", "Nur Gäste" };

        private void DrawAnswerMode(bool editable)
        {
            GUILayout.Label("Funksprüche beantworten dürfen:");
            int current = (int)_session.Radio.Mode;
            if (!editable)
            {
                GUILayout.Label("  " + AnswerModeLabels[Mathf.Clamp(current, 0, AnswerModeLabels.Length - 1)] + " (legt der Host fest)");
                GUILayout.Label("Entwickler-Menü (" + Plugin.CheatKey.Value + ") für Gäste: " + (CheatWindow.GuestAllowedByHost ? "erlaubt" : "aus") + " (legt der Host fest)");
                return;
            }
            int chosen = GUILayout.Toolbar(current, AnswerModeLabels);
            if (chosen != current)
            {
                Plugin.AnswerModeSetting.Value = (Sync.AnswerMode)chosen;
                _session.BroadcastSettings();
            }
            bool cheats = GUILayout.Toggle(Plugin.GuestCheats.Value, " Gäste dürfen das Entwickler-Menü (" + Plugin.CheatKey.Value + ") benutzen");
            if (cheats != Plugin.GuestCheats.Value)
            {
                Plugin.GuestCheats.Value = cheats;
                _session.BroadcastSettings();
            }
        }

        // ------------------------------------------------------------- eigene Spielerfarbe (Farbrad)

        private const int WheelSize = 150;
        private Texture2D _wheel;
        private float _wheelValue = -1f;
        private bool _showWheel;
        private float _value = 1f;
        private Color _pending = Color.white;
        private string _colorMsg;

        private static void Swatch(Color c, float w, float h)
        {
            var r = GUILayoutUtility.GetRect(w, h, GUILayout.Width(w), GUILayout.Height(h));
            var old = GUI.color;
            GUI.color = Color.black;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = c;
            GUI.DrawTexture(new Rect(r.x + 2f, r.y + 2f, r.width - 4f, r.height - 4f), Texture2D.whiteTexture);
            GUI.color = old;
        }

        /// <summary>HSV-Kreis (Farbton = Winkel, Sättigung = Abstand zur Mitte) in der gewählten Helligkeit.
        /// Nicht erlaubte Bereiche (Grün = frei, Blass/Grau = untätig) sind dunkel gestreift.</summary>
        private void EnsureWheel()
        {
            if (_wheel != null && Mathf.Abs(_wheelValue - _value) < 0.01f)
                return;
            if (_wheel == null)
                _wheel = new Texture2D(WheelSize, WheelSize, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            _wheelValue = _value;
            float r = WheelSize / 2f;
            for (int y = 0; y < WheelSize; y++)
                for (int x = 0; x < WheelSize; x++)
                {
                    float dx = (x + 0.5f - r) / r, dy = (y + 0.5f - r) / r;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d > 1f)
                    {
                        _wheel.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                        continue;
                    }
                    float h = Mathf.Repeat(Mathf.Atan2(dy, dx) / (2f * Mathf.PI), 1f);
                    var c = Color.HSVToRGB(h, d, _value);
                    if (!Sync.PlayerColorSync.IsAllowed(c, out _))
                    {
                        bool stripe = ((x + y) / 4) % 2 == 0;
                        c = Color.Lerp(c, Color.black, stripe ? 0.75f : 0.55f);
                    }
                    c.a = Mathf.Clamp01((1f - d) * r);
                    _wheel.SetPixel(x, y, c);
                }
            _wheel.Apply();
        }

        private void DrawMyColor()
        {
            var colors = _session.PlayerColors;
            var chosen = Sync.PlayerColorSync.ConfiguredColor();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Meine Farbe:", GUILayout.Width(90f));
            Swatch(colors.MyColor, 40f, 20f);
            GUILayout.Label(chosen.HasValue ? "#" + Sync.PlayerColorSync.Hex(chosen.Value) : "automatisch", GUILayout.Width(100f));
            if (GUILayout.Button(_showWheel ? "Schließen" : "Ändern", GUILayout.Width(80f)))
            {
                _showWheel = !_showWheel;
                _pending = colors.MyColor;
                Color.RGBToHSV(_pending, out _, out _, out _value);
                _value = Mathf.Clamp(_value, 0.45f, 1f);
                _colorMsg = null;
            }
            GUILayout.EndHorizontal();
            if (!_showWheel)
                return;

            EnsureWheel();
            GUILayout.BeginHorizontal();
            var rect = GUILayoutUtility.GetRect(WheelSize, WheelSize, GUILayout.Width(WheelSize), GUILayout.Height(WheelSize));
            GUI.DrawTexture(rect, _wheel);
            // Markierung der gewählten Farbe
            Color.RGBToHSV(_pending, out float ph, out float ps, out _);
            float ang = ph * 2f * Mathf.PI;
            var mark = new Vector2(rect.center.x + Mathf.Cos(ang) * ps * rect.width / 2f, rect.center.y - Mathf.Sin(ang) * ps * rect.height / 2f);
            var oldColor = GUI.color;
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(mark.x - 4f, mark.y - 4f, 8f, 8f), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(mark.x - 2f, mark.y - 2f, 4f, 4f), Texture2D.whiteTexture);
            GUI.color = oldColor;

            GUILayout.BeginVertical();
            GUILayout.Label("Vorschau:");
            Swatch(_pending, 80f, 34f);
            GUILayout.Label("Helligkeit:");
            float nv = GUILayout.HorizontalSlider(_value, 0.45f, 1f, GUILayout.Width(150f));
            if (Mathf.Abs(nv - _value) > 0.001f)
            {
                _value = nv;
                Color.RGBToHSV(_pending, out float h, out float s, out _);
                var c = Color.HSVToRGB(h, s, _value);
                if (Sync.PlayerColorSync.IsAllowed(c, out var why)) { _pending = c; _colorMsg = null; }
                else _colorMsg = why;
            }
            GUILayout.Label("Grün = freier Trupp,\nnicht wählbar.");
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            var e = Event.current;
            if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && rect.Contains(e.mousePosition))
            {
                float dx = (e.mousePosition.x - rect.center.x) / (rect.width / 2f);
                float dy = -(e.mousePosition.y - rect.center.y) / (rect.height / 2f);
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d <= 1f)
                {
                    float h = Mathf.Repeat(Mathf.Atan2(dy, dx) / (2f * Mathf.PI), 1f);
                    var c = Color.HSVToRGB(h, d, _value);
                    if (Sync.PlayerColorSync.IsAllowed(c, out var why)) { _pending = c; _colorMsg = null; }
                    else _colorMsg = "Nicht wählbar: " + why;
                }
                e.Use();
            }
            if (!string.IsNullOrEmpty(_colorMsg))
                GUILayout.Label(_colorMsg);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Übernehmen"))
            {
                if (colors.SetMyColor(_pending, out var why))
                {
                    _showWheel = false;
                    _colorMsg = null;
                }
                else
                {
                    _colorMsg = "Nicht wählbar: " + why;
                }
            }
            if (GUILayout.Button("Automatisch"))
            {
                colors.SetMyColor(null, out _);
                _showWheel = false;
                _colorMsg = null;
            }
            GUILayout.EndHorizontal();
        }

        // Mitspieler mit Farbe und „Hinspringen“ (Kamera auf seinen Blickpunkt).
        private void DrawPlayers()
        {
            bool any = false;
            foreach (var remote in _session.Presence.Remotes)
            {
                if (!any)
                {
                    GUILayout.Label("Mitspieler (Trupp in seiner Farbe = er hat ihn ausgewählt):");
                    any = true;
                }
                GUILayout.BeginHorizontal();
                var color = _session.PlayerColors.ColorOf(remote.SteamId, remote.Color);
                Swatch(color, 16f, 16f);
                var old = GUI.color;
                GUI.color = color;
                GUILayout.Label(remote.Name, GUILayout.Width(200f));
                GUI.color = old;
                if (GUILayout.Button("Hinspringen", GUILayout.Width(100f)))
                    _session.Presence.JumpTo(remote);
                GUILayout.EndHorizontal();
            }
        }

        private void DrawContents(int id)
        {
            GUILayout.Label("Status: " + _session.Status);
            DrawMyColor();

            if (!_session.SteamReady)
            {
                GUILayout.Label("Warte auf Steam ...");
                GUI.DragWindow();
                return;
            }

            // Versus läuft: nur der Versus-Teil, die Koop-Knöpfe sind aus
            if (Versus.VsWindow.DrawPanel(_session))
            {
                if (GUILayout.Button($"Fenster schließen ({Plugin.ToggleKey.Value})"))
                    Visible = false;
                GUI.DragWindow();
                return;
            }

            switch (_session.Role)
            {
                case Role.None:
                    DrawIdle();
                    break;
                case Role.Host:
                    DrawHost();
                    break;
                case Role.Guest:
                    DrawGuest();
                    break;
            }

            if (GUILayout.Button($"Fenster schließen ({Plugin.ToggleKey.Value})"))
                Visible = false;
            GUI.DragWindow();
        }

        private void DrawIdle()
        {
            GUI.enabled = GameAccess.IsGameStarted;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Koop hosten (aktuelles Spiel)"))
                _session.StartHost();
            Versus.VsWindow.DrawHostButton();
            GUILayout.EndHorizontal();
            GUI.enabled = true;
            if (!GameAccess.IsGameStarted)
                GUILayout.Label("Zum Hosten erst ein Spiel starten oder laden.");

            GUILayout.Space(6f);
            GUILayout.Label("Beitreten: Steam-Einladung annehmen oder Lobby-ID eingeben.");
            GUILayout.BeginHorizontal();
            _lobbyInput = GUILayout.TextField(_lobbyInput, 24, GUILayout.Width(200f));
            if (GUILayout.Button("Beitreten") && ulong.TryParse(_lobbyInput.Trim(), out var lobby))
                _session.JoinLobby(new CSteamID(lobby));
            GUILayout.EndHorizontal();

            // Direktverbindung ohne Steam (LAN, VPN, Portfreigabe)
            GUILayout.Space(6f);
            GUILayout.Label("Direkt (LAN/VPN): Port bzw. IP:Port");
            GUILayout.BeginHorizontal();
            _directInput = GUILayout.TextField(_directInput, 40, GUILayout.Width(200f));
            GUI.enabled = GameAccess.IsGameStarted;
            if (GUILayout.Button("Direkt hosten"))
                _session.StartHostDirect(ParsePort(_directInput));
            GUI.enabled = true;
            if (GUILayout.Button("Direkt beitreten"))
            {
                var parts = _directInput.Trim().Split(':');
                _session.JoinDirect(parts[0], parts.Length > 1 ? ParsePort(parts[1]) : DefaultPort);
            }
            GUILayout.EndHorizontal();
            Versus.VsWindow.DrawDirectButtons(_directInput);
        }

        private void DrawHost()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Lobby-ID: " + _session.Lobby.m_SteamID);
            if (GUILayout.Button("Kopieren", GUILayout.Width(80f)))
                GUIUtility.systemCopyBuffer = _session.Lobby.m_SteamID.ToString();
            GUILayout.EndHorizontal();

            int count = 0;
            foreach (var peer in _session.Peers)
            {
                count++;
                string state = peer.AwaitingLoad ? "lädt" : "im Spiel";
                string ping = peer.RttMs >= 0 ? $"{peer.RttMs:0} ms" : "-";
                GUILayout.Label($"• {peer.Name}: {state}, Ping {ping}");
            }
            if (count == 0)
                GUILayout.Label("Noch keine Gäste.");

            DrawPlayers();
            DrawAnswerMode(editable: true);

            if (GUILayout.Button(_showFriends ? "Freundesliste schließen" : "Freunde einladen"))
            {
                _showFriends = !_showFriends;
                if (_showFriends)
                    RefreshFriends();
            }
            if (_showFriends)
                DrawFriends();
            if (GUILayout.Button("Spielstand neu an alle senden"))
                _session.ResyncAll();
            if (GUILayout.Button("Koop beenden"))
                _session.Leave("Host hat beendet");
        }

        // Das Steam-Overlay öffnet sich nicht, wenn die Spielkopie direkt (nicht über Steam)
        // gestartet wird. Deshalb eine eigene Freundesliste; der Freund bekommt eine normale
        // Steam-Einladung (ISteamMatchmaking::InviteUserToLobby).
        private void RefreshFriends()
        {
            _friends.Clear();
            int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < count; i++)
            {
                var id = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (SteamFriends.GetFriendPersonaState(id) == EPersonaState.k_EPersonaStateOffline)
                    continue;
                bool inGame = SteamFriends.GetFriendGamePlayed(id, out var game)
                              && game.m_gameID.AppID().m_AppId == GameAppId;
                _friends.Add(new FriendEntry { Id = id, Name = SteamFriends.GetFriendPersonaName(id), InThisGame = inGame });
            }
            _friends.Sort((a, b) => a.InThisGame != b.InThisGame
                ? (a.InThisGame ? -1 : 1)
                : string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase));
        }

        private void DrawFriends()
        {
            if (_friends.Count == 0)
            {
                GUILayout.Label("Keine Freunde online.");
            }
            else
            {
                _friendScroll = GUILayout.BeginScrollView(_friendScroll, GUILayout.Height(160f));
                foreach (var friend in _friends)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label((friend.InThisGame ? "[IFZ] " : "") + friend.Name, GUILayout.Width(230f));
                    if (friend.Invited)
                        GUILayout.Label("eingeladen");
                    else if (GUILayout.Button("Einladen"))
                        friend.Invited = SteamMatchmaking.InviteUserToLobby(_session.Lobby, friend.Id);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
                GUILayout.Label("[IFZ] = spielt gerade Infection Free Zone");
            }
            if (GUILayout.Button("Liste aktualisieren"))
                RefreshFriends();
        }

        private void DrawGuest()
        {
            GUILayout.Label("Zustand: " + _session.GuestState);
            if (_session.GuestState == GuestState.ReceivingSave)
                GUILayout.Label($"Übertragung: {_session.TransferProgress * 100f:0} %");
            if (_session.GuestState == GuestState.InGame)
            {
                GUILayout.Label($"Zeitabweichung zum Host: {_session.TimeDriftSeconds:0.0} s Spielzeit (nachgestellt: {_session.TimeCorrections}x)");
                DrawPlayers();
                if (_session.HostRttMs >= 0)
                    GUILayout.Label($"Ping zum Host: {_session.HostRttMs:0} ms");
                DrawAnswerMode(editable: false);
                if (GUILayout.Button("Spielstand neu vom Host holen"))
                    _session.RequestResync();
            }
            if (GUILayout.Button("Koop verlassen"))
                _session.Leave("Gast hat verlassen");
        }
    }
}
