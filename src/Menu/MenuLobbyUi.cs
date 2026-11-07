using System;
using System.Collections.Generic;
using IfzCoop.Game;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using ZenjectExtensions.Installers;

namespace IfzCoop.Menu
{
    /// <summary>
    /// Mehrspieler im Hauptmenü (KONZEPT-MENUE.md, Stufen M1/M2). Baut in das Menü des Spiels, im Stil des Spiels
    /// (kopierte Knöpfe, Schrift „Brother1816“):
    /// - Versionsnummer des Mods unter der Spielversion oben rechts,
    /// - Knopf „Mehrspieler“ in der linken Knopfleiste,
    /// - 8 Spieler-Kacheln unten: man selbst, die Mitspieler der Steam-Lobby, „+“ = Freunde einladen (legt die Lobby an).
    /// Die Kacheln passen sich der Breite an (Canvas skaliert nach der Höhe, Bezug 1920×1080).
    /// </summary>
    internal static class MenuLobbyUi
    {
        private const int Slots = 8;
        private const float LeftMenuWidth = 300f;
        private const float SideMargin = 40f;
        private const float Gap = 10f;
        private const float TileHeight = 112f;

        private static GameObject _bar, _version, _mpButton, _statsButton;
        private static RectTransform _barRect, _canvasRect;
        private static TextMeshProUGUI _info;
        private static GameObject _hostBox;
        private static TextMeshProUGUI _hostBoxText;
        private static readonly List<UI.EscMenu.MenuButton> _lockedButtons = new List<UI.EscMenu.MenuButton>();
        private static Transform _buttonList;
        private static readonly List<Tile> _tiles = new List<Tile>();
        private static TMP_FontAsset _font;
        private static Material _fontMaterial;
        private static float _nextRefresh, _nextTry;
        private static float _lastCanvasWidth;
        private static float _nextEscCheck;
        private static bool _escOpen;
        private static bool _inGame;   // Kacheln gehören zum Esc-Menü im Spiel
        private static bool _panelOpen;
        private static float _nextPanelCheck;

        /// <summary>Ein Fenster rechts neben der Knopfleiste ist offen (Mehrspieler-Einstellungen oder eins des Spiels).</summary>
        private static bool AnyPanelOpen()
        {
            if (MenuSettingsWindow.Visible) return true;
            foreach (var p in UnityEngine.Object.FindObjectsOfType<UI.EscMenu.MenuPanel>())
                if (p != null && p.gameObject.activeInHierarchy) return true;
            return false;
        }

        public static int Builds { get; private set; }
        public static string LastLayout { get; private set; } = "-";

        private sealed class Tile
        {
            public GameObject Go;
            public Image Background, Strip;
            public RawImage Avatar;
            public ulong AvatarFor;
            public TextMeshProUGUI Name, Sub, Plus;
            public Button Button;
            public ulong SteamId;
            public bool Empty;
            public int VsSlot = -1;
        }

        private static Session S => Plugin.Instance?.Session;

        private static bool InMenu
        {
            get
            {
                try { return ScenesManager.IsMainSceneActive && !ScenesManager.IsSceneSwitching && !GameAccess.IsGameStarted; }
                catch { return false; }
            }
        }

        /// <summary>Jedes Bild aus Plugin.Update.</summary>
        public static void Update()
        {
            try { LobbyInfo.HostUpdate(); }
            catch (Exception e) { Plugin.Log.LogDebug("Lobby-Info: " + e.Message); }
            float now = Time.realtimeSinceStartup;
            bool menu = InMenu;
            bool game = !menu && GameAccess.IsGameStarted;
            if (!menu && !game)
                return;
            if (game)
            {
                // KONZEPT-MENUE M6: im Spiel nur bei offenem Esc-Menü (dieselbe Knopfleiste wie im Hauptmenü)
                if (now >= _nextEscCheck)
                {
                    _nextEscCheck = now + 0.25f;
                    var list = GameObject.Find("MenuButtonsList");
                    _escOpen = list != null && list.activeInHierarchy;
                }
                if (!_escOpen)
                {
                    if (_bar != null && _bar.activeSelf) _bar.SetActive(false);
                    MenuSettingsWindow.Hide();
                    return;
                }
                if (_bar != null && !_bar.activeSelf) { _bar.SetActive(true); _nextRefresh = 0f; }
            }
            if (_bar == null)
            {
                if (now < _nextTry) return;
                _nextTry = now + 1f;
                try { Build(); }
                catch (Exception e) { Plugin.Log.LogWarning("IFZCOOP Menü-Kacheln: " + e); }
                return;
            }
            if (now >= _nextPanelCheck)
            {
                _nextPanelCheck = now + 0.2f;
                bool open = AnyPanelOpen();
                if (open != _panelOpen) { _panelOpen = open; Layout(); }
            }
            if (_canvasRect != null && Mathf.Abs(_canvasRect.rect.width - _lastCanvasWidth) > 0.5f)
                Layout();
            MenuSettingsWindow.Update();
            if (now >= _nextRefresh)
            {
                _nextRefresh = now + 0.5f;
                try { Refresh(); }
                catch (Exception e) { Plugin.Log.LogDebug("Menü-Kacheln: " + e.Message); }
            }
        }

        // ================================================================ Aufbau

        private static void Build()
        {
            var list = GameObject.Find("MenuButtonsList");
            if (list == null)
                return;
            _inGame = GameAccess.IsGameStarted;
            _lockedButtons.Clear();
            var settingsBtn = list.transform.Find("SettingsBtn");
            _buttonList = list.transform;
            var canvas = list.GetComponentInParent<Canvas>()?.rootCanvas;
            if (settingsBtn == null || canvas == null)
                return;
            _canvasRect = canvas.GetComponent<RectTransform>();
            var refText = settingsBtn.GetComponentInChildren<TextMeshProUGUI>(true);
            _font = refText.font;
            _fontMaterial = refText.fontSharedMaterial;

            // Knopf „Mehrspieler“ über „Einstellungen“
            _mpButton = UnityEngine.Object.Instantiate(settingsBtn.gameObject, list.transform);
            _mpButton.name = "IfzCoopMultiplayerBtn";
            _mpButton.transform.SetSiblingIndex(settingsBtn.GetSiblingIndex());
            StripTranslation(_mpButton);
            _mpButton.GetComponentInChildren<TextMeshProUGUI>(true).text = "Mehrspieler";
            var mb = _mpButton.GetComponent<UI.EscMenu.MenuButton>();
            if (mb != null)
                mb.AddClickListener(_ =>
                {
                    MenuSettingsWindow.Toggle();
                    mb.ForceUnhighlight();
                });

            // Knopf „Statistik“ unter „Mehrspieler“ (nur im Spiel, also im Esc-Menü)
            _statsButton = null;
            if (_inGame)
            {
                _statsButton = UnityEngine.Object.Instantiate(settingsBtn.gameObject, list.transform);
                _statsButton.name = "IfzCoopStatsBtn";
                _statsButton.transform.SetSiblingIndex(_mpButton.transform.GetSiblingIndex() + 1);
                StripTranslation(_statsButton);
                _statsButton.GetComponentInChildren<TextMeshProUGUI>(true).text = "Statistik";
                var sbtn = _statsButton.GetComponent<UI.EscMenu.MenuButton>();
                if (sbtn != null)
                    sbtn.AddClickListener(_ =>
                    {
                        StatsWindow.Toggle();
                        sbtn.ForceUnhighlight();
                    });
            }

            // Versionsnummer unter der Spielversion
            foreach (var t in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
            {
                if (t == null || t.name != "VersionText" || !t.gameObject.scene.IsValid() || !t.gameObject.activeInHierarchy)
                    continue;
                _version = UnityEngine.Object.Instantiate(t.gameObject, t.transform.parent);
                _version.name = "IfzCoopVersionText";
                foreach (var c in _version.GetComponents<MonoBehaviour>())
                    if (!(c is TextMeshProUGUI)) UnityEngine.Object.DestroyImmediate(c);
                var vr = (RectTransform)_version.transform;
                vr.anchoredPosition += new Vector2(0f, -20f);
                var vt = _version.GetComponent<TextMeshProUGUI>();
                vt.text = "IFZ Koop " + Plugin.Version;
                vt.color = new Color(0.70f, 0.70f, 0.70f, 1f);
                break;
            }

            // Leiste mit 8 Kacheln
            _bar = new GameObject("IfzCoopLobbyBar", typeof(RectTransform));
            _barRect = (RectTransform)_bar.transform;
            _barRect.SetParent(canvas.transform, false);
            _barRect.anchorMin = new Vector2(0f, 0f);
            _barRect.anchorMax = new Vector2(0f, 0f);
            _barRect.pivot = new Vector2(0f, 0f);

            _info = NewText("Info", _barRect, 15f, TextAlignmentOptions.BottomLeft, new Color(0.70f, 0.70f, 0.70f, 1f));
            var ir = _info.rectTransform;
            ir.anchorMin = new Vector2(0f, 1f);
            ir.anchorMax = new Vector2(1f, 1f);
            ir.pivot = new Vector2(0f, 0f);
            ir.anchoredPosition = new Vector2(0f, 8f);
            ir.sizeDelta = new Vector2(0f, 24f);

            _tiles.Clear();
            for (int i = 0; i < Slots; i++)
                _tiles.Add(NewTile(i));
            BuildHostBox(canvas.transform);
            Layout();
            Refresh();
            Builds++;
            Plugin.Log.LogInfo($"IFZCOOP Menü: Mehrspieler-Knopf, Version und {Slots} Kacheln eingebaut ({LastLayout})");
        }

        /// <summary>Gast: Kasten oben rechts „Der Host wählt“ mit den Zeilen aus LobbyInfo.</summary>
        private static void BuildHostBox(Transform canvas)
        {
            _hostBox = new GameObject("IfzCoopHostChoice", typeof(RectTransform));
            var r = (RectTransform)_hostBox.transform;
            r.SetParent(canvas, false);
            r.anchorMin = r.anchorMax = new Vector2(1f, 1f);
            r.pivot = new Vector2(1f, 1f);
            r.anchoredPosition = new Vector2(-40f, -110f);
            r.sizeDelta = new Vector2(470f, 260f);
            var bg = _hostBox.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.90f);
            bg.raycastTarget = false;
            var title = NewText("Title", r, 16f, TextAlignmentOptions.TopLeft, new Color(1f, 0f, 0f, 1f));
            var tr = title.rectTransform;
            tr.anchorMin = new Vector2(0f, 1f); tr.anchorMax = new Vector2(1f, 1f); tr.pivot = new Vector2(0.5f, 1f);
            tr.anchoredPosition = new Vector2(0f, -12f); tr.sizeDelta = new Vector2(-30f, 24f);
            title.text = "Der Host wählt";
            var line = new GameObject("Line", typeof(RectTransform));
            line.transform.SetParent(r, false);
            var lr = (RectTransform)line.transform;
            lr.anchorMin = new Vector2(0f, 1f); lr.anchorMax = new Vector2(1f, 1f); lr.pivot = new Vector2(0.5f, 1f);
            lr.anchoredPosition = new Vector2(0f, -40f); lr.sizeDelta = new Vector2(-30f, 2f);
            var li = line.AddComponent<Image>();
            li.color = new Color(0.333f, 0.416f, 0.475f, 1f);
            li.raycastTarget = false;
            _hostBoxText = NewText("Body", r, 14f, TextAlignmentOptions.TopLeft, new Color(0.67f, 0.80f, 0.87f, 1f));
            _hostBoxText.fontStyle = FontStyles.Normal;
            _hostBoxText.enableWordWrapping = true;
            _hostBoxText.overflowMode = TextOverflowModes.Truncate;
            var br = _hostBoxText.rectTransform;
            br.anchorMin = new Vector2(0f, 0f); br.anchorMax = new Vector2(1f, 1f);
            br.offsetMin = new Vector2(15f, 10f); br.offsetMax = new Vector2(-15f, -50f);
            _hostBox.SetActive(false);
        }

        /// <summary>Gast in einer Lobby: „Neues Spiel“, „Spiel laden“, „Fortsetzen“ aus (der Host wählt die Karte).</summary>
        private static void LockGuestButtons(bool guest)
        {
            if (_buttonList == null) return;
            if (guest && _lockedButtons.Count == 0)
            {
                foreach (var name in new[] { "NewGameBtn", "LoadGameBtn", "ContinueGameButton" })
                {
                    var b = _buttonList.Find(name)?.GetComponent<UI.EscMenu.MenuButton>();
                    if (b != null && b.gameObject.activeSelf && b.Enabled)
                    {
                        b.Enabled = false;
                        _lockedButtons.Add(b);
                    }
                }
            }
            else if (!guest && _lockedButtons.Count > 0)
            {
                foreach (var b in _lockedButtons)
                    if (b != null) b.Enabled = true;
                _lockedButtons.Clear();
            }
        }

        private static void StripTranslation(GameObject go)
        {
            foreach (var c in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (c != null && c.GetType().Name == "StaticTranslateText")
                    UnityEngine.Object.DestroyImmediate(c);
        }

        private static TextMeshProUGUI NewText(string name, Transform parent, float size, TextAlignmentOptions align, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.font = _font;
            if (_fontMaterial != null) t.fontSharedMaterial = _fontMaterial;
            t.fontSize = size;
            t.fontStyle = FontStyles.UpperCase;
            t.alignment = align;
            t.color = color;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.raycastTarget = false;
            return t;
        }

        private static Tile NewTile(int index)
        {
            var tile = new Tile();
            tile.Go = new GameObject("Tile" + index, typeof(RectTransform));
            var r = (RectTransform)tile.Go.transform;
            r.SetParent(_barRect, false);
            r.anchorMin = r.anchorMax = new Vector2(0f, 0f);
            r.pivot = new Vector2(0f, 0f);
            tile.Background = tile.Go.AddComponent<Image>();
            tile.Background.color = new Color(0f, 0f, 0f, 0.90f);
            tile.Button = tile.Go.AddComponent<Button>();
            tile.Button.targetGraphic = tile.Background;
            var cb = tile.Button.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1f, 1f, 1f, 1f);
            cb.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            cb.colorMultiplier = 1f;
            tile.Button.colors = cb;
            int idx = index;
            tile.Button.onClick.AddListener(() => OnTileClicked(idx));
            var hover = tile.Go.AddComponent<TileHover>();
            hover.Target = tile.Background;

            var strip = new GameObject("Strip", typeof(RectTransform));
            strip.transform.SetParent(r, false);
            var sr = (RectTransform)strip.transform;
            sr.anchorMin = new Vector2(0f, 1f);
            sr.anchorMax = new Vector2(1f, 1f);
            sr.pivot = new Vector2(0.5f, 1f);
            sr.sizeDelta = new Vector2(0f, 5f);
            tile.Strip = strip.AddComponent<Image>();
            tile.Strip.raycastTarget = false;

            var av = new GameObject("Avatar", typeof(RectTransform));
            av.transform.SetParent(r, false);
            var ar = (RectTransform)av.transform;
            ar.anchorMin = ar.anchorMax = new Vector2(0.5f, 1f);
            ar.pivot = new Vector2(0.5f, 1f);
            ar.anchoredPosition = new Vector2(0f, -12f);
            ar.sizeDelta = new Vector2(46f, 46f);
            tile.Avatar = av.AddComponent<RawImage>();
            tile.Avatar.raycastTarget = false;
            av.SetActive(false);

            tile.Name = NewText("Name", r, 15f, TextAlignmentOptions.Center, new Color(0.70f, 0.70f, 0.70f, 1f));
            Stretch(tile.Name.rectTransform, 0.20f, 0.42f);
            tile.Sub = NewText("Sub", r, 12f, TextAlignmentOptions.Center, new Color(0.50f, 0.50f, 0.50f, 1f));
            Stretch(tile.Sub.rectTransform, 0.03f, 0.21f);
            tile.Plus = NewText("Plus", r, 38f, TextAlignmentOptions.Center, new Color(0.45f, 0.45f, 0.45f, 1f));
            Stretch(tile.Plus.rectTransform, 0.30f, 1.0f);
            tile.Plus.text = "+";
            return tile;
        }

        private static void Stretch(RectTransform r, float yMin, float yMax)
        {
            r.anchorMin = new Vector2(0f, yMin);
            r.anchorMax = new Vector2(1f, yMax);
            r.pivot = new Vector2(0.5f, 0.5f);
            r.offsetMin = new Vector2(6f, 0f);
            r.offsetMax = new Vector2(-6f, 0f);
        }

        /// <summary>Kachelgröße aus der verfügbaren Breite (rechts neben der Knopfleiste). Über den Social-Knöpfen unten rechts.</summary>
        private static void Layout()
        {
            float w = _canvasRect.rect.width;
            _lastCanvasWidth = w;
            // Fenster des Menüs (300 breit) liegen rechts neben der Knopfleiste: dann daneben anfangen
            float left = LeftMenuWidth + (_panelOpen ? 300f : 0f) + SideMargin;
            float avail = Mathf.Max(400f, w - left - SideMargin);
            float tileW = Mathf.Clamp((avail - Gap * (Slots - 1)) / Slots, 80f, 165f);
            float total = tileW * Slots + Gap * (Slots - 1);
            float x = left + Mathf.Max(0f, (avail - total) / 2f);
            // Unten rechts liegen die Knöpfe Facebook/Discord (~330 breit, ~200 hoch): reicht der Platz daneben nicht, höher setzen.
            bool fitsBesideSocial = x + total <= w - 340f;
            float y = fitsBesideSocial ? 36f : 215f;
            _barRect.anchoredPosition = new Vector2(x, y);
            _barRect.sizeDelta = new Vector2(total, TileHeight);
            for (int i = 0; i < _tiles.Count; i++)
            {
                var r = (RectTransform)_tiles[i].Go.transform;
                r.anchoredPosition = new Vector2(i * (tileW + Gap), 0f);
                r.sizeDelta = new Vector2(tileW, TileHeight);
            }
            LastLayout = $"canvas={w:0}x{_canvasRect.rect.height:0} tile={tileW:0} x={x:0} y={y:0} panel={(_panelOpen ? 1 : 0)}";
        }

        // ================================================================ Inhalt

        private struct Member
        {
            public ulong Id;
            public string Name;
            public string State;
            public bool IsHost;
            public Color? Color;
            public int VsSlotPlus1; // Versus-Platz + 1, 0 = keiner (Struktur ohne Feld-Startwerte)
        }

        private static List<Member> Members()
        {
            var list = new List<Member>();
            var s = S;
            if (s == null || !s.SteamReady)
            {
                list.Add(new Member { Name = "Du", State = "kein Steam" });
                return list;
            }
            var vs = Versus.VersusSession.Instance;
            if (Versus.VersusSession.Active && vs.Roster.Count > 0)
            {
                // Versus: Spielerliste der Runde, im Spiel mit Bündnis-Stand (Klick = Bündnis-Fenster)
                bool running = _inGame && vs.Phase != Versus.VsPhase.Lobby;
                foreach (var p in vs.Roster.Players)
                {
                    string state = p.Slot == vs.MySlot ? "Du" : running ? vs.Diplomacy.RelationLabel(p.Slot) : Versus.VsRoster.StateLabel(p.State);
                    list.Add(new Member { Id = p.Id, Name = p.Name, State = state, IsHost = p.Slot == 0, Color = (Color)p.Color, VsSlotPlus1 = p.Slot + 1 });
                }
                return list;
            }
            ulong me = SteamUser.GetSteamID().m_SteamID;
            if (s.Direct && s.Role == Role.Host)
            {
                list.Add(new Member { Id = me, Name = s.MyName, State = "Host", IsHost = true });
                foreach (var p in s.Peers)
                    list.Add(new Member { Id = p.Id.m_SteamID, Name = p.Name, State = s.PeerState(p.Id.m_SteamID) });
                return list;
            }
            if (s.Direct && s.Role == Role.Guest)
            {
                list.Add(new Member { Id = s.HostId.m_SteamID, Name = "Host", State = "Host", IsHost = true });
                list.Add(new Member { Id = s.MyId.m_SteamID, Name = s.MyName, State = "Du" });
                return list;
            }
            bool inLobby = s.Role != Role.None && s.Lobby != CSteamID.Nil && !s.Direct;
            if (!inLobby)
            {
                list.Add(new Member { Id = me, Name = SteamFriends.GetPersonaName(), State = "Du" });
                return list;
            }
            var owner = SteamMatchmaking.GetLobbyOwner(s.Lobby);
            int n = SteamMatchmaking.GetNumLobbyMembers(s.Lobby);
            var others = new List<Member>();
            for (int i = 0; i < n; i++)
            {
                var id = SteamMatchmaking.GetLobbyMemberByIndex(s.Lobby, i);
                string name = id.m_SteamID == me ? SteamFriends.GetPersonaName() : SteamFriends.GetFriendPersonaName(id);
                var m = new Member { Id = id.m_SteamID, Name = name, IsHost = id == owner };
                m.State = m.IsHost ? "Host" : s.Role == Role.Host ? s.PeerState(id.m_SteamID) : (id.m_SteamID == me ? "Du" : "dabei");
                if (m.IsHost) list.Add(m); else others.Add(m);
            }
            list.AddRange(others);
            return list;
        }

        private static void Refresh()
        {
            var members = Members();
            var s = S;
            for (int i = 0; i < _tiles.Count; i++)
            {
                var t = _tiles[i];
                if (i < members.Count)
                {
                    var m = members[i];
                    t.Empty = false;
                    t.SteamId = m.Id;
                    t.Name.text = m.Name;
                    t.Sub.text = m.State;
                    t.Plus.gameObject.SetActive(false);
                    t.Name.gameObject.SetActive(true);
                    t.Sub.gameObject.SetActive(true);
                    Color c = Color.gray;
                    try { c = m.Color ?? (s != null ? s.PlayerColors.ColorOf(m.Id) : Color.gray); } catch { }
                    t.VsSlot = m.VsSlotPlus1 - 1;
                    t.Strip.color = c;
                    t.Strip.gameObject.SetActive(true);
                    UpdateAvatar(t, m.Id);
                }
                else
                {
                    t.Empty = true;
                    t.VsSlot = -1;
                    t.SteamId = 0;
                    t.Plus.gameObject.SetActive(true);
                    t.Name.gameObject.SetActive(false);
                    t.Sub.gameObject.SetActive(true);
                    t.Sub.text = "Einladen";
                    t.Strip.gameObject.SetActive(false);
                    t.Avatar.gameObject.SetActive(false);
                    t.AvatarFor = 0;
                }
            }
            string role = s == null ? "" : s.Role == Role.Host ? "Du bist Host" : s.Role == Role.Guest ? "Du bist Gast" : "Allein";
            role = (LobbySettings.Mode == "versus" ? "Versus" : "Koop") + " · " + role;
            string status = s?.Status;
            _info.text = $"Mehrspieler · {members.Count} von {Slots} · {role}" + (string.IsNullOrEmpty(status) || s.Role == Role.None ? "" : " · " + status);
            bool guest = s != null && s.Role == Role.Guest;
            LockGuestButtons(guest);
            bool showBox = guest && !_inGame && LobbyInfo.Lines.Count > 0;
            if (_hostBox != null)
            {
                if (showBox)
                {
                    _hostBoxText.text = string.Join("\n", LobbyInfo.Lines);
                    var hr = (RectTransform)_hostBox.transform;
                    hr.sizeDelta = new Vector2(470f, 62f + 21f * LobbyInfo.Lines.Count);
                }
                if (_hostBox.activeSelf != showBox) _hostBox.SetActive(showBox);
            }
        }

        private static readonly Dictionary<ulong, Texture2D> _avatars = new Dictionary<ulong, Texture2D>();
        public static int AvatarsLoaded => _avatars.Count;

        /// <summary>Steam-Profilbild (mittel, 64×64). Steam lädt es evtl. erst nach: dann beim nächsten Durchlauf.</summary>
        private static void UpdateAvatar(Tile t, ulong id)
        {
            if (t.AvatarFor == id && t.Avatar.texture != null)
                return;
            var tex = Avatar(id);
            t.Avatar.texture = tex;
            t.Avatar.gameObject.SetActive(tex != null);
            t.AvatarFor = tex != null ? id : 0;
        }

        private static Texture2D Avatar(ulong id)
        {
            if (id == 0) return null;
            if (_avatars.TryGetValue(id, out var cached) && cached != null) return cached;
            try
            {
                int handle = SteamFriends.GetMediumFriendAvatar(new CSteamID(id));
                if (handle <= 0) return null; // 0 = keins, -1 = wird geladen
                if (!SteamUtils.GetImageSize(handle, out uint w, out uint h) || w == 0 || h == 0) return null;
                var buf = new byte[w * h * 4];
                if (!SteamUtils.GetImageRGBA(handle, buf, buf.Length)) return null;
                // Steam liefert von oben nach unten, Unity erwartet von unten nach oben
                var flipped = new byte[buf.Length];
                int row = (int)w * 4;
                for (int y = 0; y < h; y++)
                    Buffer.BlockCopy(buf, y * row, flipped, ((int)h - 1 - y) * row, row);
                var tex = new Texture2D((int)w, (int)h, TextureFormat.RGBA32, false);
                tex.LoadRawTextureData(flipped);
                tex.Apply();
                _avatars[id] = tex;
                return tex;
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("Steam-Bild: " + e.Message);
                return null;
            }
        }

        private static void OnTileClicked(int index)
        {
            var s = S;
            if (s == null)
                return;
            var t = _tiles[index];
            var vsi = Versus.VersusSession.Instance;
            if (!t.Empty && Versus.VersusSession.Active && t.VsSlot >= 0)
            {
                // Versus im Spiel: Klick auf einen anderen Spieler = Bündnis-Fenster
                if (_inGame && t.VsSlot != vsi.MySlot)
                    Versus.VsDiplomacyUi.OpenFor(t.VsSlot);
                return;
            }
            if (t.Empty && Versus.VersusSession.Active)
            {
                if (vsi.Role == Versus.VsRole.Host && vsi.Phase == Versus.VsPhase.Lobby) vsi.OpenInviteDialog();
                return;
            }
            if (!t.Empty)
            {
                // eigene Kachel: Mehrspieler-Fenster öffnen (dort „Meine Farbe“)
                if (t.SteamId == s.MyId.m_SteamID && !MenuSettingsWindow.Visible)
                    MenuSettingsWindow.Toggle();
                return;
            }
            if (!s.SteamReady)
            {
                s.ShowNotice("Steam ist nicht bereit", 4f);
                return;
            }
            if (s.Role == Role.None)
                s.StartHost(); // legt die Lobby an und öffnet danach die Freundesliste
            else
                s.OpenInviteDialog();
        }

        public static string StatusLine()
        {
            int occupied = 0;
            foreach (var t in _tiles) if (t != null && !t.Empty) occupied++;
            return $"menu ingame={(_inGame ? 1 : 0)} visible={(_bar != null && _bar.activeInHierarchy ? 1 : 0)} locked={_lockedButtons.Count} hostBox={(_hostBox != null && _hostBox.activeSelf ? 1 : 0)} avatars={AvatarsLoaded} bar={(_bar != null ? 1 : 0)} mpBtn={(_mpButton != null ? 1 : 0)} statsBtn={(_statsButton != null ? 1 : 0)} statsOpen={(StatsWindow.Visible ? 1 : 0)} version={(_version != null ? 1 : 0)} tiles={_tiles.Count} occupied={occupied} builds={Builds} layout={LastLayout.Replace(' ', '_')} info={(_info != null ? _info.text.Replace(' ', '_') : "-")}";
        }

        /// <summary>QA: Kachel anklicken („menutile &lt;Nr&gt;“).</summary>
        public static string QaClick(int index)
        {
            if (_bar == null) return "keine Kacheln";
            if (index < 0 || index >= _tiles.Count) return "Nr?";
            OnTileClicked(index);
            return "Kachel " + index + " geklickt";
        }
    }

    /// <summary>Kachel heller, solange die Maus darüber ist (wie die Menü-Knöpfe).</summary>
    internal sealed class TileHover : MonoBehaviour, UnityEngine.EventSystems.IPointerEnterHandler, UnityEngine.EventSystems.IPointerExitHandler
    {
        public Image Target;
        public void OnPointerEnter(UnityEngine.EventSystems.PointerEventData e) { if (Target != null) Target.color = new Color(0.18f, 0.18f, 0.18f, 0.95f); }
        public void OnPointerExit(UnityEngine.EventSystems.PointerEventData e) { if (Target != null) Target.color = new Color(0f, 0f, 0f, 0.90f); }
    }
}
