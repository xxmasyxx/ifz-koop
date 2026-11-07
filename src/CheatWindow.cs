using System;
using System.Collections.Generic;
using System.Linq;
using Controllers;
using Controllers.CharacterLogic;
using Gameplay.GameResources;
using Gameplay.Research;
using Gameplay.Units;
using Gameplay.Units.Player.Workers;
using IfzCoop.Game;
using UnityEngine;

namespace IfzCoop
{
    /// <summary>
    /// Entwickler-Menü (Standard F7), ab 0.23.0 neu geordnet: Schalter (Spiel-Flags wie schnell bauen, unendlich Munition),
    /// Lager & Waffen, Basis & Bewohner, Forschung, Trupps & Kampf, Welt & Zeit. Oben: was an ist, Suchfeld; gefährliche Knöpfe rot mit Rückfrage.
    /// Im Einzelspiel und beim Host; beim Gast nur, wenn der Host es im F6-Fenster erlaubt (GuestCheats, ab 0.21.0).
    /// Jeder Knopf ist ein Befehl (Run): beim Host/Einzelspiel sofort ausgeführt (Execute), beim Gast als CheatRequest an den
    /// Host geschickt und dort ausgeführt. Die Koop-Abgleiche bringen das Ergebnis zum Gast.
    /// </summary>
    internal sealed class CheatWindow
    {
        private const int WindowId = 0x1F2C01;
        private const float InstantInterval = 0.5f;

        private readonly Session _session;
        private Rect _rect = new Rect(380f, 120f, 620f, 10f);
        private Vector2 _scroll;
        private int _tab = 1;
        private int _amountIdx = 2;
        private string _targetWorkers = "";
        private string _last = "";
        private float _nextInstant;
        private float _guestNoticeUntil;

        public bool Visible;
        public static bool InstantBuild;

        /// <summary>Gast: der Host erlaubt Gästen das Menü (kommt mit Msg.Settings).</summary>
        public static bool GuestAllowedByHost;

        public int GuestRequests { get; private set; }

        private static readonly string[] Tabs = { "Schalter", "Lager & Waffen", "Basis & Bewohner", "Forschung", "Trupps & Kampf", "Welt & Zeit" };

        // Dauer-Schalter des Spiels (SaveHandler.SaveData, wie die Konsolen-Befehle der Entwickler)
        private static readonly (string key, string label, string hint)[] FlagDefs =
        {
            ("fastbuild", "Schnell bauen", "Baustellen brauchen kaum Zeit"),
            ("fastresearch", "Schnell forschen", "Forschung läuft viel schneller"),
            ("freeresearch", "Forschung kostenlos", "kein Wissenschafts-Material nötig"),
            ("fastscavenge", "Schnell plündern", "Häuser sind sofort durchsucht"),
            ("infiniteammo", "Unendlich Munition", "Trupps und Türme verbrauchen keine"),
            ("nohunger", "Kein Hunger", "niemand braucht Essen"),
            ("perfectmood", "Perfekte Stimmung", "Stimmung immer ganz oben"),
            ("perfectweather", "Perfektes Wetter", "kein Unwetter, beste Ernte"),
        };

        private string _search = "";
        private string _confirm;
        private static readonly int[] Amounts = { 1, 10, 50, 100, 500 };

        private static readonly (string label, ResourceID id)[] Food =
        {
            ("Rationen", ResourceID.res_food_rations), ("Getreide", ResourceID.res_grain), ("Gemüse", ResourceID.res_vegetables),
            ("Fleisch", ResourceID.res_meat), ("Hühner", ResourceID.res_chicken), ("Dosen", ResourceID.res_cans),
            ("Getränke", ResourceID.res_beverages), ("Medizin", ResourceID.res_drugs),
        };

        private static readonly (string label, ResourceID id)[] Materials =
        {
            ("Holz", ResourceID.res_wood), ("Bretter", ResourceID.res_planks), ("Ziegel", ResourceID.res_bricks),
            ("Metall", ResourceID.res_metal), ("Elektronik", ResourceID.res_electronics), ("Werkzeug", ResourceID.res_basic_tool),
            ("Gutes Werkzeug", ResourceID.res_adv_tool), ("Dünger", ResourceID.res_fertiliser), ("Treibstoff", ResourceID.res_fuel),
            ("Wiss.-Material", ResourceID.res_scientificmaterial),
        };

        private static readonly (string label, ResourceID id)[] Combat =
        {
            ("Munition", ResourceID.res_ammo), ("Panzer-Munition", ResourceID.res_ammotank), ("Granaten", ResourceID.res_grenades),
            ("Minen", ResourceID.res_mines), ("Sprengstoff", ResourceID.res_explosives), ("Molotow", ResourceID.eq_molotov),
        };

        private static readonly (string label, ResourceID id)[] Weapons =
        {
            ("Nahkampf", ResourceID.eq_basic_melee_weapon), ("Stangenwaffe", ResourceID.eq_polearm), ("Pistole", ResourceID.eq_pistol),
            ("Schrotflinte", ResourceID.eq_shotgun), ("Sturmgewehr", ResourceID.eq_assault_rifle), ("Scharfschützengewehr", ResourceID.eq_sniper_rifle),
            ("Bogen", ResourceID.eq_bow), ("Armbrust", ResourceID.eq_crossbow), ("Flammenwerfer", ResourceID.eq_flamethrower),
            ("Mörser", ResourceID.eq_mortar),
        };

        private static readonly (string label, ResourceID id)[] Armor =
        {
            ("Schützer", ResourceID.eq_wear_protectors), ("Tarnkleidung", ResourceID.eq_wear_camouflage), ("Schutzausrüstung", ResourceID.eq_wear_riot_gear),
            ("Plattenpanzer", ResourceID.eq_wear_plate), ("Soldaten-Ausrüstung", ResourceID.eq_wear_soldier_gear),
        };

        public static CheatWindow Instance { get; private set; }

        public CheatWindow(Session session)
        {
            _session = session;
            Instance = this;
        }

        /// <summary>Testtreiber: "cheat open" oder ein Befehl wie im Menü (siehe Execute), z. B. "cheat give res_wood 50".</summary>
        public string Qa(string[] a)
        {
            if (a.Length < 2) return "?";
            if (a[1] == "open") { Toggle(); return "sichtbar=" + Visible + " erlaubt=" + Allowed; }
            if (!Allowed) return "nicht erlaubt";
            Run(string.Join(" ", a, 1, a.Length - 1));
            return _last;
        }

        private bool IsGuest => _session.Role == Role.Guest;

        private bool Allowed => GameAccess.IsGameStarted && !Versus.VersusSession.BlocksCheats && (!IsGuest || GuestAllowedByHost);

        /// <summary>Knopf gedrückt: beim Gast an den Host, sonst sofort.</summary>
        private void Run(string cmd)
        {
            if (IsGuest)
            {
                _session.SendToHost(Net.Protocol.Build(Net.Msg.CheatRequest, w => w.Write(cmd)));
                if (cmd.StartsWith("instant ")) InstantBuild = cmd.EndsWith("1");
                _last = "an den Host geschickt ...";
                return;
            }
            try { _last = Execute(cmd.Split(' ')); }
            catch (Exception e) { _last = "Fehler: " + e.Message; Plugin.Log.LogWarning("IFZCOOP Entwickler " + cmd + ": " + e); }
        }

        /// <summary>Host: Befehl eines Gastes. Nur wenn Gästen erlaubt.</summary>
        public string HostOnGuestRequest(string from, string cmd)
        {
            if (!Plugin.GuestCheats.Value)
                return "der Host hat es für Gäste nicht freigegeben";
            if (!GameAccess.IsGameStarted || Versus.VersusSession.BlocksCheats)
                return "gerade nicht möglich";
            string res;
            try { res = Execute(cmd.Split(' ')); }
            catch (Exception e) { res = "Fehler: " + e.Message; }
            GuestRequests++;
            Plugin.Log.LogInfo($"IFZCOOP Entwickler (von {from}): {cmd} -> {res}");
            _session.ShowNotice($"{from} (Entwickler-Menü): {res}", 4f);
            return res;
        }

        /// <summary>Ein Befehl des Menüs ausführen (nur Host/Einzelspiel). Rückgabe: Text für die Anzeige.</summary>
        private string Execute(string[] a)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            int N(int i) => int.Parse(a[i], inv);
            switch (a[0])
            {
                case "give":
                {
                    var id = (ResourceID)Enum.Parse(typeof(ResourceID), a[1]);
                    float qty = float.Parse(a[2], inv);
                    if (qty <= 0f || qty > 100000f) return "Menge?";
                    return Give(id, qty, LabelOf(id));
                }
                case "giveall":
                {
                    float qty = float.Parse(a[1], inv);
                    if (qty <= 0f || qty > 100000f) return "Menge?";
                    foreach (var x in Food.Concat(Materials).Concat(Combat)) Give(x.id, qty, null);
                    return $"Alles je +{qty}";
                }
                case "research":
                {
                    var rc = GameAccess.Game<ResearchController>();
                    if (rc?.CurrentTechnology == null) return "Es läuft keine Forschung";
                    var rid = rc.CurrentTechnology.GetID();
                    rc.SetResearchState(rid, ResearchStates.completed);
                    return "Fertig: " + rid;
                }
                case "scimat":
                    GameAccess.Game<ResearchController>().AddScientificMaterials(N(1));
                    return "Wissenschafts-Material +" + N(1);
                case "researchall":
                {
                    var rc = GameAccess.Game<ResearchController>();
                    int ok = 0;
                    foreach (ResearchID rid in Enum.GetValues(typeof(ResearchID)))
                    {
                        try { rc.SetResearchState(rid, ResearchStates.completed); ok++; }
                        catch { }
                    }
                    return ok + " Forschungen fertig";
                }
                case "instant":
                    InstantBuild = a[1] == "1";
                    Plugin.InstantBuildSetting.Value = InstantBuild;
                    return InstantBuild ? "Sofort bauen AN" : "Sofort bauen AUS";
                case "finish":
                    return FinishAllConstruction() + " Baustellen fertig";
                case "workersadd":
                    return AddWorkers(Mathf.Clamp(N(1), 1, 500));
                case "workersdel":
                    return RemoveWorkers(Mathf.Clamp(N(1), 1, 500));
                case "workers":
                {
                    int t = Mathf.Clamp(N(1), 0, 2000);
                    int w = Adults(c => !c.IsChild && !c.IsSick).Count;
                    if (t > w) return AddWorkers(t - w);
                    if (t < w) return RemoveWorkers(w - t);
                    return "schon " + t;
                }
                case "babies":
                {
                    int n = Mathf.Clamp(N(1), 1, 50);
                    GameAccess.Game<CitizensController>().MakeBabies(n, newborns: true);
                    return $"{n} Kinder";
                }
                case "cure":
                {
                    int k = 0;
                    foreach (var c in Adults(c => c.IsSick)) { c.CureSickness(); k++; }
                    return k + " geheilt";
                }
                case "heal":
                {
                    int k = 0;
                    var squads = GameAccess.Game<SquadsController>()?.Squads;
                    if (squads != null)
                        foreach (var g in squads)
                            foreach (var c in g.Characters)
                                if (c != null && !c.IsDead()) { c.Health.SetHealthAmount01(1f); k++; }
                    return k + " Figuren geheilt";
                }
                case "ammo":
                {
                    int k = 0;
                    var squads = GameAccess.Game<SquadsController>()?.Squads;
                    if (squads != null)
                        foreach (var g in squads)
                            foreach (var c in g.Characters)
                                if (c?.ItemsContainer?.AmmunitionHandler?.Ammunition != null) { c.ItemsContainer.AmmunitionHandler.Ammunition.SetQuantity(100f); k++; }
                    return k + " Figuren mit Munition";
                }
                case "flag":
                {
                    bool on = a[2] == "1";
                    var d = SaveHandler.SaveData;
                    if (d == null) return "kein Spielstand";
                    switch (a[1])
                    {
                        case "fastbuild": d.FastConstruction = on; break;
                        case "fastresearch": d.FastResearch = on; break;
                        case "freeresearch": d.FreeResearch = on; break;
                        case "fastscavenge": d.FastScavenge = on; break;
                        case "infiniteammo": d.InfiniteAmmo = on; break;
                        case "nohunger": d.NoHunger = on; break;
                        case "perfectmood":
                            d.PerfectMood = on;
                            if (on) GameAccess.Game<Gameplay.Mood.MoodController>()?.SetMaxMood();
                            break;
                        case "perfectweather": d.PerfectWeather = on; break;
                        default: return "unbekannter Schalter " + a[1];
                    }
                    return FlagLabel(a[1]) + (on ? " AN" : " AUS");
                }
                case "scavengeall":
                {
                    int k = 0;
                    var bc = GameAccess.Game<BuildingsController>();
                    if (bc?.Buildings != null)
                        foreach (var b in bc.Buildings.ToList())
                        {
                            if (b == null || b.StateMachine?.CurrentState?.StateType != Gameplay.Rebuilding.StateMachine.StructureStateType.Abandoned) continue;
                            if (b.ScavengeWork != null && b.ScavengeWork.IsDone) continue;
                            try { b.InitializeAndEndScavengeWork(); k++; }
                            catch (Exception e) { Plugin.Log.LogDebug($"Plündern {b.ID}: {e.Message}"); }
                        }
                    return k + " Häuser geplündert";
                }
                case "reveal":
                {
                    var hc = GameAccess.Game<Gameplay.Units.Enemy.Hideouts.HideoutsController>();
                    if (hc == null) return "nicht gefunden";
                    foreach (Fraction f in Enum.GetValues(typeof(Fraction)))
                    {
                        try { hc.RevealHideouts(f); } catch { }
                    }
                    return "Verstecke und Höhlen aufgedeckt";
                }
                case "missions":
                    GameAccess.Game<EventsSystem.EventsSystemController>()?.CompleteActiveMissions();
                    return "Laufende Missionen erledigt";
                case "killhordes":
                    GameAccess.Game<Gameplay.Units.Enemy.GroupsController>()?.KillAllHordes();
                    return "Infizierten-Horden getötet";
                case "save":
                    GameAccess.Game<AutoSaveController>()?.TryRunAutoSave(stopGameplay: true, checkIfCanLimitAutoSaves: true);
                    return "Speichern gestartet";
                case "hour":
                {
                    int h = Mathf.Clamp(N(1), 0, 23);
                    GameAccess.Game<Controllers.Time.TimeController>().SetHour(h);
                    return "Uhrzeit " + h;
                }
            }
            return "unbekannter Befehl " + a[0];
        }

        private static string FlagLabel(string key)
        {
            foreach (var f in FlagDefs) if (f.key == key) return f.label;
            return key;
        }

        private static bool FlagOn(string key)
        {
            var d = SaveHandler.SaveData;
            if (d == null) return false;
            switch (key)
            {
                case "fastbuild": return d.FastConstruction;
                case "fastresearch": return d.FastResearch;
                case "freeresearch": return d.FreeResearch;
                case "fastscavenge": return d.FastScavenge;
                case "infiniteammo": return d.InfiniteAmmo;
                case "nohunger": return d.NoHunger;
                case "perfectmood": return d.PerfectMood;
                case "perfectweather": return d.PerfectWeather;
            }
            return false;
        }

        private static string ActiveSummary()
        {
            var on = new List<string>();
            if (InstantBuild) on.Add("Sofort bauen");
            foreach (var f in FlagDefs) if (FlagOn(f.key)) on.Add(f.label);
            return on.Count == 0 ? "keine Schalter an" : string.Join(", ", on);
        }

        /// <summary>Testtreiber: Zustand der Schalter.</summary>
        public static string FlagsLine() => string.Join(" ", FlagDefs.Select(f => f.key + "=" + (FlagOn(f.key) ? 1 : 0))) + " instant=" + (InstantBuild ? 1 : 0);

        private static string LabelOf(ResourceID id)
        {
            foreach (var x in Food.Concat(Materials).Concat(Combat).Concat(Weapons).Concat(Armor))
                if (x.id == id) return x.label;
            return id.ToString();
        }

        /// <summary>Taste gedrückt.</summary>
        public void Toggle()
        {
            if (Versus.VersusSession.BlocksCheats)
            {
                Visible = false;
                _session.ShowNotice("Im Versus-Modus ist das Entwickler-Menü aus", 3f);
                return;
            }
            if (IsGuest && !GuestAllowedByHost)
            {
                Visible = false;
                _guestNoticeUntil = Time.realtimeSinceStartup + 3f;
                return;
            }
            Visible = !Visible;
        }

        /// <summary>Jedes Bild: Sofort-Bauen.</summary>
        public void Update()
        {
            if (!InstantBuild || !Allowed || IsGuest)
                return; // Sofort-Bauen rechnet nur der Host
            float now = Time.realtimeSinceStartup;
            if (now < _nextInstant)
                return;
            _nextInstant = now + InstantInterval;
            int n = FinishAllConstruction();
            if (n > 0)
                Plugin.Log.LogInfo($"IFZCOOP Entwickler: {n} Baustellen sofort fertig");
        }

        public void Draw()
        {
            if (Time.realtimeSinceStartup < _guestNoticeUntil)
                GUI.Box(new Rect((Screen.width - 520f) / 2f, 110f, 520f, 30f), "Das Entwickler-Menü hat der Host für Gäste nicht freigegeben (Mehrspieler-Fenster).");
            if (Versus.VersusSession.BlocksCheats)
                Visible = false;
            if (!Visible)
                return;
            if (!Allowed)
            {
                if (IsGuest) Visible = false;
                else GUI.Box(new Rect((Screen.width - 420f) / 2f, 110f, 420f, 30f), "Entwickler-Menü: erst ein Spiel starten oder laden.");
                return;
            }
            _rect = GUILayout.Window(WindowId, _rect, DrawContents, IsGuest ? "Entwickler-Menü (Gast, der Host führt aus)" : "Entwickler-Menü");
        }

        private GUIStyle _danger, _onStyle, _hint, _sectionStyle;

        private void EnsureStyles()
        {
            if (_danger != null) return;
            _danger = new GUIStyle(GUI.skin.button);
            _danger.normal.textColor = new Color(1f, 0.45f, 0.40f);
            _danger.hover.textColor = new Color(1f, 0.6f, 0.55f);
            _danger.fontStyle = FontStyle.Bold;
            _onStyle = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
            _hint = new GUIStyle(GUI.skin.label) { fontSize = 11 };
            _hint.normal.textColor = new Color(0.65f, 0.65f, 0.65f);
            _sectionStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            _sectionStyle.normal.textColor = new Color(0.29f, 0.99f, 0.49f);
        }

        private bool Searching => !string.IsNullOrWhiteSpace(_search);

        private bool Match(string label) => !Searching || label.IndexOf(_search.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;

        private void Section(string title, params string[] labels)
        {
            if (Searching && !labels.Any(Match)) return;
            GUILayout.Space(4f);
            GUILayout.Label(title, _sectionStyle);
        }

        /// <summary>Knopf mit Suche; gefährliche Knöpfe rot und mit Rückfrage.</summary>
        private void Btn(string label, string cmd, bool danger = false)
        {
            if (!Match(label)) return;
            if (danger && _confirm == cmd)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Wirklich „" + label + "“?", GUILayout.ExpandWidth(true));
                if (GUILayout.Button("Ja", _danger, GUILayout.Width(60f))) { _confirm = null; Run(cmd); }
                if (GUILayout.Button("Nein", GUILayout.Width(60f))) _confirm = null;
                GUILayout.EndHorizontal();
                return;
            }
            if (!GUILayout.Button(label, danger ? _danger : GUI.skin.button)) return;
            if (danger) _confirm = cmd;
            else Run(cmd);
        }

        private void DrawContents(int id)
        {
            EnsureStyles();
            // immer sichtbar: was gerade an ist, Rolle, Suche
            GUILayout.Label("<b>Aktiv:</b> " + ActiveSummary(), _onStyle);
            if (_session.Role == Role.Host)
                GUILayout.Label("Du bist Host: gilt für alle im Spiel.", _hint);
            else if (IsGuest)
                GUILayout.Label("Du bist Gast: jeder Knopf geht als Wunsch an den Host.", _hint);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Suchen:", GUILayout.Width(55f));
            _search = GUILayout.TextField(_search ?? "", 40);
            if (Searching && GUILayout.Button("×", GUILayout.Width(26f))) _search = "";
            GUILayout.EndHorizontal();
            if (!Searching)
                _tab = GUILayout.Toolbar(_tab, Tabs);

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(440f));
            try
            {
                if (Searching)
                {
                    DrawFlags(); DrawStock(); DrawBase(); DrawResearch(); DrawSquads(); DrawWorld();
                }
                else
                {
                    switch (_tab)
                    {
                        case 0: DrawFlags(); break;
                        case 1: DrawStock(); break;
                        case 2: DrawBase(); break;
                        case 3: DrawResearch(); break;
                        case 4: DrawSquads(); break;
                        case 5: DrawWorld(); break;
                    }
                }
            }
            catch (Exception e)
            {
                _last = "Fehler: " + e.Message;
                Plugin.Log.LogWarning("IFZCOOP Entwickler: " + e);
            }
            GUILayout.EndScrollView();
            GUILayout.Label(_last);
            if (GUILayout.Button($"Schließen ({Plugin.CheatKey.Value})"))
                Visible = false;
            GUI.DragWindow();
        }

        // ================================================================ Schalter

        private void DrawFlags()
        {
            Section("Dauer-Schalter (bleiben an, bis du sie ausmachst)", FlagDefs.Select(f => f.label).Concat(new[] { "Sofort bauen" }).ToArray());
            if (Match("Sofort bauen"))
            {
                bool now = GUILayout.Toggle(InstantBuild, " Sofort bauen  (jede Baustelle wird gleich fertig)");
                if (now != InstantBuild)
                    Run("instant " + (now ? "1" : "0"));
            }
            foreach (var f in FlagDefs)
            {
                if (!Match(f.label)) continue;
                bool on = FlagOn(f.key);
                bool want = GUILayout.Toggle(on, " " + f.label + "  (" + f.hint + ")");
                if (want != on) Run("flag " + f.key + " " + (want ? "1" : "0"));
            }
        }

        // ================================================================ Lager & Waffen

        private void DrawAmount()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Menge:", GUILayout.Width(55f));
            _amountIdx = GUILayout.Toolbar(_amountIdx, Amounts.Select(a => a.ToString()).ToArray());
            GUILayout.EndHorizontal();
        }

        private int Amount => Amounts[Mathf.Clamp(_amountIdx, 0, Amounts.Length - 1)];

        private void DrawGrid(string title, (string label, ResourceID id)[] items)
        {
            var shown = items.Where(x => Match(x.label)).ToArray();
            if (shown.Length == 0) return;
            Section(title, shown.Select(x => x.label).ToArray());
            for (int i = 0; i < shown.Length; i += 4)
            {
                GUILayout.BeginHorizontal();
                for (int j = i; j < i + 4 && j < shown.Length; j++)
                    if (GUILayout.Button($"{shown[j].label} +{Amount}", GUILayout.Width(140f)))
                        Run($"give {shown[j].id} {Amount}");
                GUILayout.EndHorizontal();
            }
        }

        private void DrawStock()
        {
            if (!Searching || Food.Concat(Materials).Concat(Combat).Concat(Weapons).Concat(Armor).Any(x => Match(x.label)))
                DrawAmount();
            DrawGrid("Essen und Medizin", Food);
            DrawGrid("Baumaterial", Materials);
            DrawGrid("Kampf", Combat);
            DrawGrid("Waffen (ins Lager)", Weapons);
            DrawGrid("Schutzkleidung (ins Lager)", Armor);
            if (Match("Alles"))
            {
                GUILayout.Space(6f);
                Btn($"Alles (Essen, Material, Kampf) je +{Amount}", $"giveall {Amount}");
            }
        }

        private static string Give(ResourceID id, float qty, string label)
        {
            var stock = GameAccess.Game<StockroomsController>();
            if (stock?.Container == null)
                return "Kein Lager gefunden";
            stock.Container.ForceAddResource(id, qty, ResourceAddReason.None);
            return $"{label ?? id.ToString()} +{qty}";
        }

        // ================================================================ Forschung

        private void DrawResearch()
        {
            var rc = GameAccess.Game<ResearchController>();
            if (rc == null)
            {
                if (!Searching) GUILayout.Label("Keine Forschung gefunden.");
                return;
            }
            Section("Forschung", "Laufende Forschung sofort fertig", "Wissenschafts-Material +100", "ALLE Forschungen fertig");
            if (!Searching)
                GUILayout.Label("Laufende Forschung: " + (rc.CurrentTechnology != null ? rc.CurrentTechnology.GetID().ToString() : "keine"));
            Btn("Laufende Forschung sofort fertig", "research");
            Btn("Wissenschafts-Material +100", "scimat 100");
            if (Match("ALLE Forschungen fertig"))
                GUILayout.Label("Schaltet alles frei, lässt sich nicht rückgängig machen:", _hint);
            Btn("ALLE Forschungen fertig", "researchall", danger: true);
        }

        // ================================================================ Basis & Bewohner

        private void DrawBase()
        {
            Section("Bauen und Plündern", "Jetzt alle Baustellen fertig", "Alle Häuser plündern (Funde bleiben im Haus)");
            Btn("Jetzt alle Baustellen fertig", "finish");
            Btn("Alle Häuser plündern (Funde bleiben im Haus)", "scavengeall", danger: true);

            Section("Bewohner", "Arbeiter +1", "Arbeiter −1", "Kinder +1", "Alle Kranken heilen", "Arbeiter genau auf");
            if (!Searching)
            {
                var workers = Adults(c => !c.IsChild && !c.IsSick).Count;
                var children = Adults(c => c.IsChild).Count;
                var sick = Adults(c => c.IsSick).Count;
                GUILayout.Label($"Arbeiter: {workers}   Kinder: {children}   Kranke: {sick}   Bürger gesamt: {CitizensController.CitizensCount}");
            }
            GUILayout.BeginHorizontal();
            foreach (int n in new[] { 1, 5, 10 }) Btn($"Arbeiter +{n}", "workersadd " + n);
            foreach (int n in new[] { 1, 5, 10 }) Btn($"Arbeiter −{n}", "workersdel " + n);
            GUILayout.EndHorizontal();
            if (Match("Arbeiter genau auf"))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Arbeiter genau auf:", GUILayout.Width(130f));
                _targetWorkers = GUILayout.TextField(_targetWorkers, 4, GUILayout.Width(60f));
                if (GUILayout.Button("Setzen", GUILayout.Width(80f)) && int.TryParse(_targetWorkers, out int target) && target >= 0 && target <= 2000)
                    Run("workers " + target);
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            foreach (int n in new[] { 1, 5 }) Btn($"Kinder +{n}", "babies " + n);
            Btn("Alle Kranken heilen", "cure");
            GUILayout.EndHorizontal();
        }

        // ================================================================ Trupps & Kampf

        private void DrawSquads()
        {
            Section("Trupps", "Alle Trupps heilen", "Munition auffüllen (je 100)");
            if (!Searching) GUILayout.Label("Trupps: " + (GameAccess.Game<SquadsController>()?.Squads?.Count ?? 0));
            GUILayout.BeginHorizontal();
            Btn("Alle Trupps heilen", "heal");
            Btn("Munition auffüllen (je 100)", "ammo");
            GUILayout.EndHorizontal();
            Section("Gegner", "Verstecke und Höhlen aufdecken", "Alle Infizierten-Horden töten");
            Btn("Verstecke und Höhlen aufdecken", "reveal");
            Btn("Alle Infizierten-Horden töten", "killhordes", danger: true);
        }

        // ================================================================ Welt & Zeit

        private void DrawWorld()
        {
            Section("Uhrzeit setzen", "6 Uhr", "12 Uhr", "18 Uhr", "22 Uhr");
            GUILayout.BeginHorizontal();
            foreach (int h in new[] { 6, 9, 12, 15, 18, 22 }) Btn(h + " Uhr", "hour " + h);
            GUILayout.EndHorizontal();
            Section("Spiel", "Jetzt speichern", "Laufende Missionen erledigen");
            Btn("Jetzt speichern", "save");
            Btn("Laufende Missionen erledigen", "missions", danger: true);
        }

        /// <summary>Alle laufenden Baustellen fertig. Rückgabe: Anzahl.</summary>
        private static int FinishAllConstruction()
        {
            int n = 0;
            var bc = GameAccess.Game<BuildingsController>();
            if (bc?.Buildings != null)
                foreach (var b in bc.Buildings.ToList())
                {
                    if (b == null) continue;
                    var st = b.StateMachine?.CurrentState?.StateType;
                    if (st == Gameplay.Rebuilding.StateMachine.StructureStateType.Constructing)
                    {
                        try
                        {
                            b.StateMachine.ChangeTo(Gameplay.Rebuilding.StateMachine.StructureStateType.Completed);
                            n++;
                        }
                        catch (Exception e) { Plugin.Log.LogDebug($"Sofort bauen Haus {b.ID}: {e.Message}"); }
                    }
                }
            foreach (var s in Sync.StructureSync.All().ToList())
            {
                try
                {
                    if (s.BuildStructureWork != null && !s.BuildStructureWork.IsDone)
                    {
                        s.BuildImmediately();
                        n++;
                    }
                }
                catch (Exception e) { Plugin.Log.LogDebug($"Sofort bauen Anlage: {e.Message}"); }
            }
            return n;
        }

        // ================================================================ Bewohner

        private static List<Gameplay.Units.Characters.Character> Adults(Func<Gameplay.Units.Characters.Character, bool> pick)
        {
            var list = new List<Gameplay.Units.Characters.Character>();
            var cc = GameAccess.Game<CitizensController>();
            if (cc?.Citizens == null) return list;
            foreach (var c in cc.Citizens)
                if (c != null && c.Group == null && !c.IsDead() && pick(c)) list.Add(c);
            return list;
        }

        private static Vector3 HqPos
        {
            get
            {
                var hq = HqController.MainHeadquarter;
                return hq != null ? hq.transform.position : Vector3.zero;
            }
        }

        private static string AddWorkers(int n)
        {
            GameAccess.Game<WorkersController>().CreateNewWorkers(n, HqPos + new Vector3(5f, 0f, 5f));
            return $"{n} Arbeiter dazu";
        }

        private static string RemoveWorkers(int n)
        {
            var list = Adults(c => !c.IsChild && !c.IsSick);
            int done = 0;
            for (int i = list.Count - 1; i >= 0 && done < n; i--, done++)
                list[i].Kill(SubtractionHpReason.None);
            return $"{done} Arbeiter weg";
        }

    }
}
