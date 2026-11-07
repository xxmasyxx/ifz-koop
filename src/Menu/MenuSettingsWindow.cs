using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace IfzCoop.Menu
{
    /// <summary>
    /// Fenster „Mehrspieler-Einstellungen“ im Hauptmenü (KONZEPT-MENUE.md, Stufe M3). Gebaut aus Kopien des
    /// Einstellungsfensters des Spiels (Hintergrund, rote Abschnittstitel, Linien, Zeilen mit Auswahlliste), an derselben
    /// Stelle rechts neben der Knopfleiste. Der Host stellt ein, Gäste sehen dieselben Werte (kommen mit Msg.Settings,
    /// siehe LobbySettings). Spielmodus Koop/Versus; je nach Modus die passenden Zeilen.
    /// </summary>
    internal static class MenuSettingsWindow
    {
        private static GameObject _panel;
        private static RectTransform _column;
        private static GameObject _rowTemplate, _labelTemplate, _lineTemplate, _inputTemplate;
        private static TMP_InputField _nameField;
        private static TextMeshProUGUI _nameTitle;
        private static readonly List<Row> _rows = new List<Row>();
        private static TextMeshProUGUI _hint;
        private static float _nextRefresh;
        private static bool _applying;

        public static bool Visible => _panel != null && _panel.activeSelf;

        private sealed class Row
        {
            public GameObject Go;
            public TMP_Dropdown Dropdown;
            public string[] Values;            // interne Werte je Eintrag
            public Func<string> Get;           // aktueller Wert
            public Action<string> Set;         // nur Host
            public string Section;             // "all", "koop", "versus"
            public bool Personal;              // eigene Einstellung (z. B. Farbe): jeder darf sie ändern, geht nicht an die Lobby
        }

        private static Session S => Plugin.Instance?.Session;

        private static bool CanEdit => S == null || S.Role != Role.Guest;

        // ================================================================ Öffnen/Schließen

        public static void Toggle()
        {
            try
            {
                if (_panel == null && !Build())
                {
                    Plugin.Instance?.ToggleCoopWindow(); // Ersatz, falls das Spielmenü anders gebaut ist
                    return;
                }
                bool show = !_panel.activeSelf;
                if (show)
                {
                    foreach (var p in UnityEngine.Object.FindObjectsOfType<UI.EscMenu.MenuPanel>())
                        if (p != null && p.gameObject.activeSelf) p.HidePanel();
                    Refresh(true);
                }
                _panel.SetActive(show);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("IFZCOOP Mehrspieler-Einstellungen: " + e);
            }
        }

        public static void Hide()
        {
            if (_panel != null && _panel.activeSelf) _panel.SetActive(false);
        }

        /// <summary>Jedes Bild aus MenuLobbyUi.Update (nur im Hauptmenü).</summary>
        public static void Update()
        {
            if (!Visible) return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextRefresh) return;
            _nextRefresh = now + 1f;
            Refresh(false);
        }

        // ================================================================ Aufbau

        private static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            foreach (Transform c in root)
            {
                var f = FindDeep(c, name);
                if (f != null) return f;
            }
            return null;
        }

        private static bool Build()
        {
            Transform settings = null;
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                if (t != null && t.name == "GameSettings" && t.gameObject.scene.IsValid() && t.GetComponent("SettingsWindow") != null)
                { settings = t; break; }
            if (settings == null)
                return false;
            var bg = FindDeep(settings, "BackgroundMenuPanel");
            var label = FindDeep(settings, "GameplayAndControlsLabel");
            var line = FindDeep(settings, "Line (1)");
            var units = FindDeep(settings, "Units");
            if (bg == null || label == null || line == null || units == null)
                return false;
            _rowTemplate = units.gameObject;
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                if (t != null && t.name == "ZoneName" && t.gameObject.scene.IsValid() && t.GetComponentInChildren<TMP_InputField>(true) != null)
                { _inputTemplate = t.gameObject; break; }
            _labelTemplate = label.gameObject;
            _lineTemplate = line.gameObject;

            _panel = new GameObject("IfzCoopSettingsPanel", typeof(RectTransform));
            var pr = (RectTransform)_panel.transform;
            pr.SetParent(settings.parent, false);
            var sr = (RectTransform)settings;
            pr.anchorMin = sr.anchorMin;
            pr.anchorMax = sr.anchorMax;
            pr.pivot = sr.pivot;
            pr.anchoredPosition = sr.anchoredPosition;
            pr.sizeDelta = sr.sizeDelta;
            pr.SetSiblingIndex(settings.GetSiblingIndex());

            var bgc = UnityEngine.Object.Instantiate(bg.gameObject, pr);
            bgc.name = "BackgroundMenuPanel";
            bgc.SetActive(true);

            // scrollbar (Mausrad), falls der Bildschirm für alle Zeilen zu niedrig ist
            var vp = new GameObject("Viewport", typeof(RectTransform));
            var vpr = (RectTransform)vp.transform;
            vpr.SetParent(pr, false);
            vpr.anchorMin = new Vector2(0f, 0f);
            vpr.anchorMax = new Vector2(1f, 1f);
            vpr.offsetMin = new Vector2(0f, 20f);
            vpr.offsetMax = new Vector2(0f, -40f);
            vp.AddComponent<RectMask2D>();
            var vpImg = vp.AddComponent<Image>();
            vpImg.color = new Color(0f, 0f, 0f, 0f); // fängt das Mausrad
            var col = new GameObject("Column", typeof(RectTransform));
            _column = (RectTransform)col.transform;
            _column.SetParent(vpr, false);
            _column.anchorMin = new Vector2(0f, 1f);
            _column.anchorMax = new Vector2(1f, 1f);
            _column.pivot = new Vector2(0.5f, 1f);
            _column.anchoredPosition = Vector2.zero;
            _column.sizeDelta = Vector2.zero;
            var fit = col.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var sc = _panel.AddComponent<ScrollRect>();
            sc.viewport = vpr;
            sc.content = _column;
            sc.horizontal = false;
            sc.vertical = true;
            sc.movementType = ScrollRect.MovementType.Clamped;
            sc.scrollSensitivity = 30f;
            var vl = col.AddComponent<VerticalLayoutGroup>();
            vl.childAlignment = TextAnchor.UpperCenter;
            vl.childControlWidth = false;
            vl.childControlHeight = false;
            vl.childForceExpandWidth = false;
            vl.childForceExpandHeight = false;
            vl.spacing = 4f;

            BuildRows();
            _panel.SetActive(false);

            // Andere Menü-Knöpfe schließen dieses Fenster (wie bei den Fenstern des Spiels)
            var list = GameObject.Find("MenuButtonsList");
            if (list != null)
                foreach (var mb in list.GetComponentsInChildren<UI.EscMenu.MenuButton>(true))
                    if (mb != null && mb.gameObject.name != "IfzCoopMultiplayerBtn")
                        mb.ButtonClicked += _ => Hide();
            Plugin.Log.LogInfo($"IFZCOOP Menü: Mehrspieler-Einstellungen gebaut ({_rows.Count} Zeilen)");
            return true;
        }

        private static void Section(string title)
        {
            var l = UnityEngine.Object.Instantiate(_labelTemplate, _column);
            l.SetActive(true);
            StripTranslation(l);
            l.GetComponent<TextMeshProUGUI>().text = title;
            var ln = UnityEngine.Object.Instantiate(_lineTemplate, _column);
            ln.SetActive(true);
        }

        private static GameObject SectionGroupStart(string title, string section)
        {
            Section(title);
            // Titel und Linie gehören zum Abschnitt: merken über eine Pseudo-Zeile ohne Auswahlliste
            var lastTwo = new[] { _column.GetChild(_column.childCount - 2).gameObject, _column.GetChild(_column.childCount - 1).gameObject };
            foreach (var go in lastTwo)
                _rows.Add(new Row { Go = go, Section = section });
            return lastTwo[0];
        }

        private static void StripTranslation(GameObject go)
        {
            foreach (var c in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (c != null && c.GetType().Name == "StaticTranslateText")
                    UnityEngine.Object.DestroyImmediate(c);
        }

        private static void Choice(string section, string title, string[] labels, string[] values, Func<string> get, Action<string> set)
        {
            var go = UnityEngine.Object.Instantiate(_rowTemplate, _column);
            go.name = "IfzRow_" + title;
            go.SetActive(true);
            StripTranslation(go);
            var texts = go.GetComponentsInChildren<TextMeshProUGUI>(true);
            var head = texts.FirstOrDefault(t => t.gameObject.name == "Text (TMP)");
            if (head != null) head.text = title;
            var dd = go.GetComponentInChildren<TMP_Dropdown>(true);
            dd.onValueChanged = new TMP_Dropdown.DropdownEvent(); // gespeicherte Verbindungen zu den Spiel-Einstellungen weg
            dd.ClearOptions();
            dd.AddOptions(labels.ToList());
            var row = new Row { Go = go, Dropdown = dd, Values = values, Get = get, Set = set, Section = section };
            dd.onValueChanged.AddListener(i =>
            {
                if (_applying || i < 0 || i >= row.Values.Length) return;
                if (row.Personal)
                {
                    row.Set(row.Values[i]);
                    Refresh(true);
                    return;
                }
                if (!CanEdit) return;
                row.Set(row.Values[i]);
                LobbySettings.HostChanged();
                Refresh(true);
            });
            _rows.Add(row);
        }

        // ================================================================ eigene Farbe

        private static readonly string[] ColorNames = { "Blau", "Orange", "Lila", "Gelb", "Pink", "Rot", "Hellblau", "Braun" };

        /// <summary>
        /// „Meine Farbe“: jeder Spieler (auch Gäste) wählt selbst; Automatisch oder eine der acht Standardfarben. Eine andere
        /// Farbe aus der Konfiguration (MeineFarbe) erscheint als „Eigene“. Grün gibt es nicht (Grün = freier Trupp).
        /// </summary>
        private static void ColorRow()
        {
            var defaults = Sync.PlayerColorSync.Defaults;
            var labels = new List<string> { "Automatisch" };
            var values = new List<string> { "auto" };
            for (int i = 0; i < defaults.Length; i++)
            {
                string hex = Sync.PlayerColorSync.Hex(defaults[i]);
                labels.Add("<color=#" + hex + ">" + (i < ColorNames.Length ? ColorNames[i] : hex) + "</color>");
                values.Add(hex);
            }
            var own = Sync.PlayerColorSync.ConfiguredColor();
            string ownHex = own.HasValue ? Sync.PlayerColorSync.Hex(own.Value) : null;
            labels.Add(ownHex != null ? "<color=#" + ownHex + ">Eigene Farbe</color>" : "Eigene Farbe");
            values.Add("custom");
            // „Farbrad …“ ist nie der aktuelle Wert: jede Wahl löst aus und öffnet das Rad, danach springt die Zeile zurück
            labels.Add("Farbrad …");
            values.Add("wheel");
            Choice("all", "Meine Farbe", labels.ToArray(), values.ToArray(), CurrentColorValue, SetColorValue);
            _rows[_rows.Count - 1].Personal = true;
            if (!_wheelHooked)
            {
                _wheelHooked = true;
                ColorPickerWindow.Applied += () => { try { Refresh(true); } catch (Exception e) { Plugin.Log.LogDebug("Farbe anzeigen: " + e.Message); } };
                ColorPickerWindow.Closed += () => { try { Refresh(true); } catch { } };
            }
        }

        private static bool _wheelHooked;

        private static string CurrentColorValue()
        {
            var c = Sync.PlayerColorSync.ConfiguredColor();
            if (!c.HasValue) return "auto";
            string hex = Sync.PlayerColorSync.Hex(c.Value);
            foreach (var d in Sync.PlayerColorSync.Defaults)
                if (Sync.PlayerColorSync.Hex(d) == hex) return hex;
            return "custom";
        }

        private static void SetColorValue(string v)
        {
            if (v == "custom") return;
            if (v == "wheel")
            {
                ColorPickerWindow.Open();
                return;
            }
            Color? c = null;
            if (v != "auto" && ColorUtility.TryParseHtmlString("#" + v, out var parsed)) c = parsed;
            if (S != null)
                S.PlayerColors.SetMyColor(c, out _);
            else
                Plugin.MyColorSetting.Value = c.HasValue ? ColorUtility.ToHtmlStringRGB(c.Value) : string.Empty;
        }

        private static string B(bool b) => b ? "1" : "0";

        private static TMP_InputField _addressField;

        /// <summary>
        /// Direktverbindung ohne Steam (früher im F6-Fenster): Adresse (IP:Port) und zwei Knöpfe im Stil der Menü-Knöpfe.
        /// </summary>
        private static void DirectRows()
        {
            if (_inputTemplate == null) return;
            var head = SectionGroupStart("Ohne Steam", "all");
            var go = UnityEngine.Object.Instantiate(_inputTemplate, _column);
            go.name = "IfzRow_Adresse";
            go.SetActive(true);
            StripTranslation(go);
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && (mb.GetType().Name == "TooltipTriggerUI" || mb.GetType().Name == "NotAvailableBtnTooltipSetter"))
                    UnityEngine.Object.DestroyImmediate(mb);
            ((RectTransform)go.transform).sizeDelta = new Vector2(260f, 62f);
            var title = go.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(t => t.gameObject.name == "Text (TMP)");
            if (title != null) title.text = "Adresse des Hosts (IP:Port)";
            _addressField = go.GetComponentInChildren<TMP_InputField>(true);
            ((RectTransform)_addressField.transform).sizeDelta = new Vector2(240f, 30f);
            _addressField.onValueChanged = new TMP_InputField.OnChangeEvent();
            _addressField.onEndEdit = new TMP_InputField.SubmitEvent();
            _addressField.onSubmit = new TMP_InputField.SubmitEvent();
            _addressField.characterLimit = 60;
            _addressField.SetTextWithoutNotify(Plugin.DirectAddress.Value);
            _addressField.onEndEdit.AddListener(v => Plugin.DirectAddress.Value = (v ?? "").Trim());
            _rows.Add(new Row { Go = go, Section = "all" });

            var list = GameObject.Find("MenuButtonsList");
            var template = list != null ? list.transform.Find("SettingsBtn") : null;
            if (template == null) return;
            AddButton(template.gameObject, "Direkt hosten", () =>
            {
                var s = S;
                if (s == null) return;
                if (s.Role != Role.None) { s.ShowNotice("Erst die laufende Verbindung beenden", 4f); return; }
                s.StartHostDirect(27120);
                s.ShowNotice(s.Status, 5f);
            });
            AddButton(template.gameObject, "Direkt beitreten", () =>
            {
                var s = S;
                if (s == null) return;
                if (s.Role != Role.None) { s.ShowNotice("Erst die laufende Verbindung beenden", 4f); return; }
                string addr = (_addressField != null ? _addressField.text : Plugin.DirectAddress.Value).Trim();
                Plugin.DirectAddress.Value = addr;
                string host = addr;
                int port = 27120;
                int colon = addr.LastIndexOf(':');
                if (colon > 0 && int.TryParse(addr.Substring(colon + 1), out int p)) { host = addr.Substring(0, colon); port = p; }
                s.JoinDirect(host, port);
                s.ShowNotice(s.Status, 5f);
            });
            AddButton(template.gameObject, "Verbindung beenden", () =>
            {
                var s = S;
                if (s == null || s.Role == Role.None) return;
                s.Leave("beendet", sendBye: true, backToMenu: false);
                s.ShowNotice("Mehrspieler beendet", 4f);
            });
        }

        private static void AddButton(GameObject template, string text, Action onClick)
        {
            var go = UnityEngine.Object.Instantiate(template, _column);
            go.name = "IfzBtn_" + text;
            go.SetActive(true);
            StripTranslation(go);
            ((RectTransform)go.transform).sizeDelta = new Vector2(260f, 44f);
            go.GetComponentInChildren<TextMeshProUGUI>(true).text = text;
            var mb = go.GetComponent<UI.EscMenu.MenuButton>();
            if (mb != null)
                mb.AddClickListener(_ => { onClick(); mb.ForceUnhighlight(); });
            _rows.Add(new Row { Go = go, Section = "all" });
        }

        /// <summary>Name: mit Steam der Steam-Name (fest), bei der Direktverbindung frei eingebbar.</summary>
        private static void NameRow()
        {
            if (_inputTemplate == null) return;
            var go = UnityEngine.Object.Instantiate(_inputTemplate, _column);
            go.name = "IfzRow_Name";
            go.SetActive(true);
            StripTranslation(go);
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && (mb.GetType().Name == "TooltipTriggerUI" || mb.GetType().Name == "NotAvailableBtnTooltipSetter"))
                    UnityEngine.Object.DestroyImmediate(mb);
            ((RectTransform)go.transform).sizeDelta = new Vector2(260f, 62f);
            _nameTitle = go.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(t => t.gameObject.name == "Text (TMP)");
            _nameField = go.GetComponentInChildren<TMP_InputField>(true);
            ((RectTransform)_nameField.transform).sizeDelta = new Vector2(240f, 30f);
            _nameField.onValueChanged = new TMP_InputField.OnChangeEvent();
            _nameField.onEndEdit = new TMP_InputField.SubmitEvent();
            _nameField.onSubmit = new TMP_InputField.SubmitEvent();
            _nameField.characterLimit = 24;
            _nameField.onEndEdit.AddListener(v =>
            {
                if (_applying || S == null || S.NameFromSteam) return;
                Plugin.PlayerName.Value = (v ?? "").Trim();
            });
            _rows.Add(new Row { Go = go, Section = "all" });
        }

        private static void BuildRows()
        {
            _rows.Clear();
            Section("Mehrspieler");
            NameRow();
            ColorRow();
            Choice("all", "Spielmodus", new[] { "Koop – zusammen eine Basis", "Versus – jeder seine Basis" }, new[] { "koop", "versus" },
                () => LobbySettings.Mode, v => LobbySettings.SetMode(v));
            Choice("all", "Spieler höchstens", new[] { "2", "3", "4", "5", "6", "7", "8" }, new[] { "2", "3", "4", "5", "6", "7", "8" },
                () => LobbySettings.MaxPlayers.ToString(), v => LobbySettings.SetMaxPlayers(int.Parse(v)));

            SectionGroupStart("Koop", "koop");
            Choice("koop", "Funksprüche beantworten", new[] { "Alle", "Nur Host", "Nur Gäste" }, new[] { "0", "1", "2" },
                () => ((int)(S?.Radio.Mode ?? Sync.AnswerMode.Alle)).ToString(),
                v => Plugin.AnswerModeSetting.Value = (Sync.AnswerMode)int.Parse(v));
            Choice("koop", "Gäste: Entwickler-Menü (F7)", new[] { "Aus", "An" }, new[] { "0", "1" },
                () => B(S != null && S.Role == Role.Guest ? CheatWindow.GuestAllowedByHost : Plugin.GuestCheats.Value),
                v => Plugin.GuestCheats.Value = v == "1");
            Choice("koop", "Gäste: Pause und Tempo", new[] { "Erlaubt", "Nur der Host" }, new[] { "1", "0" },
                () => B(S != null && S.Role == Role.Guest ? LobbySettings.GuestSpeedFromHost : Plugin.GuestMayChangeSpeed.Value),
                v => Plugin.GuestMayChangeSpeed.Value = v == "1");

            SectionGroupStart("Versus", "versus");
            Choice("versus", "Spielart", new[] { "Jeder gegen jeden", "Teams", "Alle verbündet" }, new[] { "ffa", "teams", "allied" },
                () => VsS.Mode == Versus.VsMode.Teams ? "teams" : VsS.Mode == Versus.VsMode.AllAllied ? "allied" : "ffa", v => VsSet("mode", v));
            Choice("versus", "Einwohner am Anfang", new[] { "Wie im Spiel", "10", "20", "40" }, new[] { "0", "10", "20", "40" },
                () => VsS.Workers.ToString(), v => VsSet("workers", v));
            Choice("versus", "Trupps am Anfang", new[] { "Wie im Spiel", "1", "2", "3" }, new[] { "0", "1", "2", "3" },
                () => VsS.Squads.ToString(), v => VsSet("squads", v));
            Choice("versus", "Truppgröße", new[] { "2", "3", "4", "6", "8" }, new[] { "2", "3", "4", "6", "8" },
                () => VsS.SquadSize.ToString(), v => VsSet("squadsize", v));
            Choice("versus", "Startausstattung", new[] { "Eigene (Werte hier)", "Wie auf der Karte" }, new[] { "1", "0" },
                () => B(VsS.CustomStart), v => VsSet("start", v == "1" ? "custom" : "map"));
            Choice("versus", "Start-Rohstoffe", new[] { "Keine extra", "Wenig", "Normal", "Viel" }, new[] { "0", "50", "150", "500" },
                () => ResourcePreset().ToString(), v => SetResourcePreset(int.Parse(v)));
            Choice("versus", "Fahrzeug am HQ", VehLabels(true), VehValues(true), () => VehSlotType(0), v => SetVehSlot(0, v, null));
            Choice("versus", "Anzahl", new[] { "1", "2", "3", "4", "5" }, new[] { "1", "2", "3", "4", "5" },
                () => VehSlotCount(0), v => SetVehSlot(0, null, v));
            Choice("versus", "2. Fahrzeug am HQ", VehLabels(true), VehValues(true), () => VehSlotType(1), v => SetVehSlot(1, v, null));
            Choice("versus", "Anzahl ", new[] { "1", "2", "3", "4", "5" }, new[] { "1", "2", "3", "4", "5" },
                () => VehSlotCount(1), v => SetVehSlot(1, null, v));
            Choice("versus", "HQ", new[] { "Wie im Spiel (je nach Gebäude)", "Bei allen gleich (Werte unten)" }, new[] { "0", "1" },
                () => B(VsS.HqEqual), v => VsSet("hq", v));
            Choice("versus", "HQ Leben", new[] { "2000", "5000", "10000", "20000", "50000" }, new[] { "2000", "5000", "10000", "20000", "50000" },
                () => VsS.HqHp.ToString(), v => VsSet("hqhp", v));
            Choice("versus", "HQ Verstärkung (Mauern)", Versus.VsSettings.EnforcementLabels, new[] { "0", "1", "2", "3" },
                () => VsS.HqEnforcement.ToString(), v => VsSet("hqenf", v));
            Choice("versus", "HQ Lagerplatz (Kisten)", new[] { "200", "400", "800", "1500", "3000" }, new[] { "200", "400", "800", "1500", "3000" },
                () => VsS.HqStorage.ToString(), v => VsSet("hqstorage", v));
            Choice("versus", "Sieg", new[] { "Letzter mit Bevölkerung", "Ohne Sieg" }, new[] { "1", "0" },
                () => B(VsS.Victory), v => VsSet("victory", v));
            Choice("versus", "Durchsuchte Häuser", new[] { "Für alle sichtbar", "Nur eigene" }, new[] { "1", "0" },
                () => B(VsS.ScavengedVisible), v => VsSet("scav", v));
            Choice("versus", "Missionen", new[] { "Wettlauf und eigene Story", "Nur Wettlauf", "Nur eigene Story" }, new[] { "both", "race", "story" },
                () => VsS.Missions == Versus.VsMissions.Race ? "race" : VsS.Missions == Versus.VsMissions.OwnStory ? "story" : "both", v => VsSet("missions", v));

            DirectRows();

            var hintGo = UnityEngine.Object.Instantiate(_labelTemplate, _column);
            hintGo.SetActive(true);
            StripTranslation(hintGo);
            _hint = hintGo.GetComponent<TextMeshProUGUI>();
            _hint.fontSize = 13f;
            _hint.color = new Color(0.67f, 0.80f, 0.87f, 1f);
            _hint.enableWordWrapping = true;
            ((RectTransform)hintGo.transform).sizeDelta = new Vector2(260f, 60f);
        }

        private static Versus.VsSettings VsS => Versus.VersusSession.Instance?.Settings ?? _fallback;
        private static readonly Versus.VsSettings _fallback = new Versus.VsSettings();

        private static void VsSet(string key, string value)
        {
            if (VsS.TrySet(key, value, out _)) VsS.Revision++;
        }

        // Fahrzeuge am HQ: zwei Plätze (Art + Anzahl), gespeichert in VsSettings.Vehicles
        private static string[] VehLabels(bool withNone)
        {
            var l = new List<string>();
            if (withNone) l.Add("Keins");
            foreach (var t in CheatWindow.VehicleTypes) l.Add(t.label);
            return l.ToArray();
        }

        private static string[] VehValues(bool withNone)
        {
            var l = new List<string>();
            if (withNone) l.Add("none");
            foreach (var t in CheatWindow.VehicleTypes) l.Add(t.type.ToString());
            return l.ToArray();
        }

        private static List<KeyValuePair<Gameplay.Vehicles.VehicleType, int>> VehSlots()
        {
            var order = CheatWindow.VehicleTypes.Select(t => t.type).ToList();
            return VsS.Vehicles.Where(kv => kv.Value > 0).OrderBy(kv => order.IndexOf(kv.Key)).ToList();
        }

        private static string VehSlotType(int i)
        {
            var sl = VehSlots();
            return i < sl.Count ? sl[i].Key.ToString() : "none";
        }

        private static string VehSlotCount(int i)
        {
            var sl = VehSlots();
            return i < sl.Count ? Mathf.Clamp(sl[i].Value, 1, 5).ToString() : "1";
        }

        private static void SetVehSlot(int i, string type, string count)
        {
            var sl = VehSlots();
            var slots = new List<KeyValuePair<Gameplay.Vehicles.VehicleType, int>>();
            for (int k = 0; k < 2; k++)
                slots.Add(k < sl.Count ? sl[k] : new KeyValuePair<Gameplay.Vehicles.VehicleType, int>((Gameplay.Vehicles.VehicleType)(-1), 1));
            var cur = slots[i];
            var t = cur.Key;
            int n = cur.Value;
            if (type != null) t = type == "none" ? (Gameplay.Vehicles.VehicleType)(-1) : (Gameplay.Vehicles.VehicleType)Enum.Parse(typeof(Gameplay.Vehicles.VehicleType), type);
            if (count != null) n = int.Parse(count);
            slots[i] = new KeyValuePair<Gameplay.Vehicles.VehicleType, int>(t, Mathf.Max(1, n));
            VsS.Vehicles.Clear();
            foreach (var kv in slots)
                if ((int)kv.Key >= 0)
                    VsS.Vehicles[kv.Key] = (VsS.Vehicles.TryGetValue(kv.Key, out var have) ? have : 0) + kv.Value;
            VsS.Revision++;
        }

        private static int ResourcePreset()
        {
            int v = VsS.ResourceAmount(Gameplay.GameResources.ResourceID.res_wood);
            return v >= 500 ? 500 : v >= 150 ? 150 : v >= 50 ? 50 : 0;
        }

        private static void SetResourcePreset(int amount)
        {
            // Rohstoffe voll, Waffen/Schutz ein Zehntel
            foreach (var r in Versus.VsSettings.ResourceChoices)
            {
                bool gear = r.id.ToString().StartsWith("eq_");
                int n = gear ? amount / 10 : amount;
                if (n <= 0) VsS.Resources.Remove(r.id); else VsS.Resources[r.id] = n;
            }
            VsS.Revision++;
        }

        // ================================================================ Anzeigen

        private static void Refresh(bool force)
        {
            if (_panel == null) return;
            _applying = true;
            try
            {
                string mode = LobbySettings.Mode;
                bool edit = CanEdit;
                bool inGame = IfzCoop.Game.GameAccess.IsGameStarted;
                foreach (var r in _rows)
                {
                    bool show = r.Section == "all" || r.Section == mode;
                    if (r.Go.activeSelf != show) r.Go.SetActive(show);
                    if (r.Dropdown == null) continue;
                    // im laufenden Spiel lässt sich der Modus nicht mehr wechseln
                    r.Dropdown.interactable = r.Personal || (edit && !(inGame && r.Go.name == "IfzRow_Spielmodus"));
                    int idx = Array.IndexOf(r.Values, r.Get());
                    if (idx >= 0 && r.Dropdown.value != idx) r.Dropdown.SetValueWithoutNotify(idx);
                }
                var s = S;
                if (_nameField != null && s != null)
                {
                    bool steam = s.NameFromSteam;
                    if (_nameTitle != null) _nameTitle.text = steam ? "Name (von Steam)" : "Dein Name";
                    _nameField.interactable = !steam;
                    if (!_nameField.isFocused) _nameField.SetTextWithoutNotify(s.MyName);
                }
                _hint.text = s == null || s.Role != Role.Guest
                    ? (s != null && s.Role == Role.Host ? "Deine Gäste sehen diese Einstellungen." : "Lade Freunde über die Kacheln unten ein.")
                    : "Die Einstellungen legt der Host fest.";
            }
            finally
            {
                _applying = false;
            }
        }

        public static string StatusLine()
        {
            if (_panel == null) return "msettings built=0";
            var vals = _rows.Where(r => r.Dropdown != null && r.Go.activeSelf)
                .Select(r => r.Go.name.Replace("IfzRow_", "").Replace(' ', '_') + "=" + (r.Dropdown.value >= 0 && r.Dropdown.value < r.Values.Length ? r.Values[r.Dropdown.value] : "?")
                             + (r.Dropdown.interactable ? "" : "(fest)"));
            return $"msettings built=1 visible={(Visible ? 1 : 0)} mode={LobbySettings.Mode} {string.Join(" ", vals)}";
        }

        /// <summary>QA: „msettings open“ oder „msettings set &lt;Zeilentitel-Anfang&gt; &lt;Eintrag-Nr&gt;“ (wie ein Klick).</summary>
        public static string Qa(string[] a)
        {
            if (a.Length < 2) return StatusLine();
            if (a[1] == "open") { if (!Visible) Toggle(); return StatusLine(); }
            if (a[1] == "close") { Hide(); return StatusLine(); }
            if (a[1] == "set" && a.Length >= 4)
            {
                if (_panel == null && !Build()) return "kein Fenster";
                Refresh(true); // aktuelle Werte setzen, sonst greift eine Änderung auf den schon gezeigten Wert nicht
                var row = _rows.FirstOrDefault(r => r.Dropdown != null && r.Go.name.Replace("IfzRow_", "").StartsWith(a[2].Replace('_', ' '), StringComparison.OrdinalIgnoreCase));
                if (row == null) return "keine Zeile " + a[2];
                if (!row.Dropdown.interactable) return "fest (Gast)";
                if (row.Dropdown.value == int.Parse(a[3])) { row.Dropdown.onValueChanged.Invoke(row.Dropdown.value); return StatusLine(); }
                row.Dropdown.value = int.Parse(a[3]);
                return StatusLine();
            }
            return "?";
        }
    }

    /// <summary>
    /// Einstellungen der Lobby, die der Host festlegt und mit Msg.Settings an Gäste schickt (an die Funk-Einstellung und den
    /// Entwickler-Haken angehängt): Spielmodus, Spieler höchstens, Gäste-Tempo, Versus-Einstellungen.
    /// </summary>
    internal static class LobbySettings
    {
        public static string HostMode = "koop";      // Gast: vom Host
        public static int HostMaxPlayers = 4;
        public static bool GuestSpeedFromHost = true;

        private static Session S => Plugin.Instance?.Session;
        private static bool IsGuest => S != null && S.Role == Role.Guest;

        public static string Mode => IsGuest ? HostMode : (Plugin.LobbyMode.Value == "versus" ? "versus" : "koop");
        public static int MaxPlayers => IsGuest ? HostMaxPlayers : Mathf.Clamp(Plugin.MaxPlayers.Value, 2, 8);

        public static void SetMode(string v) => Plugin.LobbyMode.Value = v == "versus" ? "versus" : "koop";

        public static void SetMaxPlayers(int n)
        {
            Plugin.MaxPlayers.Value = Mathf.Clamp(n, 2, 8);
            var vs = Versus.VersusSession.Instance?.Settings;
            if (vs != null) { vs.MaxPlayers = Plugin.MaxPlayers.Value; vs.Revision++; }
        }

        /// <summary>Host hat etwas geändert: an alle Gäste.</summary>
        public static void HostChanged()
        {
            var s = S;
            if (s != null && s.Role == Role.Host)
                s.BroadcastSettings();
        }

        /// <summary>An Msg.Settings anhängen (Host).</summary>
        public static void Write(BinaryWriter w)
        {
            w.Write(Mode);
            w.Write((byte)MaxPlayers);
            w.Write(Plugin.GuestMayChangeSpeed.Value);
            var vs = Versus.VersusSession.Instance?.Settings;
            w.Write(vs != null);
            if (vs != null) vs.Write(w);
        }

        /// <summary>Aus Msg.Settings lesen (Gast); ältere Hosts schicken das nicht.</summary>
        public static void Read(BinaryReader r)
        {
            if (r.BaseStream.Position >= r.BaseStream.Length) return;
            HostMode = r.ReadString();
            HostMaxPlayers = r.ReadByte();
            GuestSpeedFromHost = r.ReadBoolean();
            if (r.ReadBoolean())
            {
                var vs = Versus.VsSettings.Read(r);
                Versus.VersusSession.Instance?.Settings.CopyFrom(vs);
            }
        }
    }
}
