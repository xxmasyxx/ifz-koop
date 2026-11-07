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

        private static readonly string[] Tabs = { "Schalter", "Lager & Waffen", "Basis & Bewohner", "Forschung", "Trupps & Kampf", "Fahrzeuge", "Welt & Zeit" };

        // Fahrzeuge (Reiter „Fahrzeuge“): Umbauten wie in der Werkstatt, Fahrzeug per Klick auf die Karte erzeugen
        private static readonly (Gameplay.Vehicles.ModificationType mod, string label)[] Mods =
        {
            (Gameplay.Vehicles.ModificationType.ve_mod_trunk, "Großer Kofferraum"),
            (Gameplay.Vehicles.ModificationType.ve_mod_armor, "Panzerung"),
            (Gameplay.Vehicles.ModificationType.ve_mod_turbine, "Turbo"),
            (Gameplay.Vehicles.ModificationType.ve_mod_tank, "Großer Tank"),
            (Gameplay.Vehicles.ModificationType.ve_mod_ram, "Rammschutz"),
            (Gameplay.Vehicles.ModificationType.ve_mod_hcal, "Schweres MG"),
            (Gameplay.Vehicles.ModificationType.ve_mod_tank_cannon, "Panzerkanone"),
        };

        private static readonly (Gameplay.Vehicles.VehicleType type, string label)[] VehicleTypes =
        {
            (Gameplay.Vehicles.VehicleType.ve_minicar, "Kleinwagen"), (Gameplay.Vehicles.VehicleType.ve_sedan, "Limousine"),
            (Gameplay.Vehicles.VehicleType.ve_combi, "Kombi"), (Gameplay.Vehicles.VehicleType.ve_sport_car, "Sportwagen"),
            (Gameplay.Vehicles.VehicleType.ve_suv, "Geländewagen"), (Gameplay.Vehicles.VehicleType.ve_pickup, "Pick-up"),
            (Gameplay.Vehicles.VehicleType.ve_van, "Transporter"), (Gameplay.Vehicles.VehicleType.ve_truck, "Lastwagen"),
            (Gameplay.Vehicles.VehicleType.ve_cistern, "Tankwagen"), (Gameplay.Vehicles.VehicleType.ve_bus, "Bus"),
            (Gameplay.Vehicles.VehicleType.ve_bus_school, "Schulbus"), (Gameplay.Vehicles.VehicleType.ve_bus_coach, "Reisebus"),
            (Gameplay.Vehicles.VehicleType.ve_armoured_transport, "Panzerwagen"), (Gameplay.Vehicles.VehicleType.ve_command, "Kommandofahrzeug"),
            (Gameplay.Vehicles.VehicleType.ve_tank, "Panzer"),
        };

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
                case "take":
                {
                    var id = (ResourceID)Enum.Parse(typeof(ResourceID), a[1]);
                    float qty = float.Parse(a[2], inv);
                    if (qty <= 0f || qty > 100000f) return "Menge?";
                    var stock = GameAccess.Game<StockroomsController>();
                    if (stock?.Container == null) return "Kein Lager gefunden";
                    float have = stock.Container.Resources.TryGetValue(id, out var h) ? h : 0f;
                    if (have <= 0f) return LabelOf(id) + ": nichts im Lager";
                    float take = Mathf.Min(have, qty);
                    stock.Container.TryRemoveResource(id, take, removeAllIfNeed: true);
                    return $"{LabelOf(id)} −{take:0} (im Lager jetzt {Mathf.Max(0f, have - take):0})";
                }
                case "giveall":
                {
                    float qty = float.Parse(a[1], inv);
                    if (qty <= 0f || qty > 100000f) return "Menge?";
                    foreach (var x in Food.Concat(Materials).Concat(Combat)) if (HasResData(x.id)) Give(x.id, qty, null);
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
                    InstantBuild = a[1] == "1"; // gilt nur für das laufende Spiel (nicht mehr gespeichert)
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
                case "bld":
                    return BuildingCmd(a[1], long.Parse(a[2], inv));
                case "blook-enf":
                case "blook-wall":
                case "blook-roof":
                    return LookCmd(a);
                case "fixstock":
                {
                    var r = CleanStock();
                    return r.Count == 0 ? "Lager ist in Ordnung" : "Aus dem Lager entfernt: " + string.Join(", ", r);
                }
                case "vmod":
                case "vfix":
                case "vspawn":
                    return VehicleCmd(a);
                case "vdel":
                {
                    var v = FindVehicle(a[1]);
                    if (v == null) return "Fahrzeug nicht gefunden";
                    if (v.Owner != null) return "Erst mit dem Trupp aussteigen, dann löschen";
                    string label = VehicleLabel(v.Data.GetVehicleType());
                    Indestructible.Remove(v.Id);
                    SpeedBoost.Remove(v.Id);
                    v.DestroyVehicle();
                    _selVehicleStatic = null;
                    return label + " gelöscht";
                }
                case "vhard":
                {
                    var v = FindVehicle(a[1]);
                    if (v == null) return "Fahrzeug nicht gefunden";
                    bool on = a[2] == "1";
                    if (on) { Indestructible.Add(v.Id); v.Health.SetHealthAmount01(1f); } else Indestructible.Remove(v.Id);
                    return VehicleLabel(v.Data.GetVehicleType()) + (on ? " unzerstörbar" : " wieder zerstörbar");
                }
                case "instantmake":
                    InstantMake = a[1] == "1";
                    return InstantMake ? "Sofort herstellen AN" : "Sofort herstellen AUS";
                case "scavall":
                {
                    float pct = Mathf.Clamp(float.Parse(a[1], inv), 0f, 10000f);
                    ScavBoostAll = 1f + pct / 100f;
                    return pct < 1f ? "Plündern für alle normal" : $"Plündern für alle +{pct:0} % schneller";
                }
                case "instantdemolish":
                    InstantDemolish = a[1] == "1";
                    return InstantDemolish ? "Sofort abbauen AN" : "Sofort abbauen AUS";
                case "onehitall":
                    OneHitAll = a[1] == "1";
                    return OneHitAll ? "Sofort töten AN (alle Trupps)" : "Sofort töten für alle AUS";
                case "sqwalk":
                {
                    var groups = SquadsById(a[1].Split(',')).ToList();
                    if (groups.Count == 0) return "Trupp nicht gefunden";
                    float pct = Mathf.Clamp(float.Parse(a[2], inv), 50f, 2000f);
                    foreach (var g in groups)
                    {
                        if (Mathf.Abs(pct - 100f) < 1f) WalkBoost.Remove(g.Id); else WalkBoost[g.Id] = pct / 100f;
                    }
                    RebuildWalk();
                    return $"Lauftempo {pct:0} % ({string.Join(", ", groups.Select(g => SquadName(g.Id)))})";
                }
                case "sqscav":
                case "sqonehit":
                {
                    var groups = SquadsById(a[1].Split(',')).ToList();
                    if (groups.Count == 0) return "Trupp nicht gefunden";
                    string names = string.Join(", ", groups.Select(g => SquadName(g.Id)));
                    if (a[0] == "sqscav")
                    {
                        float pct = Mathf.Clamp(float.Parse(a[2], inv), 0f, 10000f);
                        foreach (var g in groups)
                        {
                            if (pct < 1f) ScavBoost.Remove(g.Id); else ScavBoost[g.Id] = 1f + pct / 100f;
                        }
                        return pct < 1f ? $"Plündern normal ({names})" : $"Plündern +{pct:0} % schneller ({names})";
                    }
                    bool on = a[2] == "1";
                    foreach (var g in groups) { if (on) OneHit.Add(g.Id); else OneHit.Remove(g.Id); }
                    return $"Sofort töten {(on ? "AN" : "AUS")} ({names})";
                }
                case "vspeed":
                {
                    var v = FindVehicle(a[1]);
                    if (v == null) return "Fahrzeug nicht gefunden";
                    float pct = Mathf.Clamp(float.Parse(a[2], inv), 50f, 2000f);
                    if (Mathf.Abs(pct - 100f) < 1f) SpeedBoost.Remove(v.Id); else SpeedBoost[v.Id] = pct / 100f;
                    v.UpdateMaxSpeed();
                    return VehicleLabel(v.Data.GetVehicleType()) + $": Tempo {pct:0} %";
                }
                case "sqskill":
                case "sqweapon":
                case "sqgear":
                    return SquadCmd(a);
                case "sqammo":
                {
                    var groups = SquadsById(a[1].Split(',')).ToList();
                    if (groups.Count == 0) return "Trupp nicht gefunden";
                    int k = 0;
                    foreach (var g in groups)
                        foreach (var c in g.Characters)
                            if (c?.ItemsContainer?.AmmunitionHandler?.Ammunition != null) { c.ItemsContainer.AmmunitionHandler.Ammunition.SetQuantity(100f); k++; }
                    return $"Munition aufgefüllt: {k} Figuren ({string.Join(", ", groups.Select(g => SquadName(g.Id)))})";
                }
                case "healsel":
                {
                    var groups = SquadsById(a[1].Split(',')).ToList();
                    if (groups.Count == 0) return "Trupp nicht gefunden";
                    return HealGroups(groups) + " Figuren geheilt (" + string.Join(", ", groups.Select(g => SquadName(g.Id))) + ")";
                }
                case "immortal":
                {
                    bool on = a[1] == "1";
                    if (!on && a[2] == "all")
                    {
                        int n = Immortal.Count;
                        Immortal.Clear();
                        return n + " Trupps wieder sterblich";
                    }
                    var groups = SquadsById(a[2].Split(',')).ToList();
                    if (groups.Count == 0) return "Trupp nicht gefunden";
                    foreach (var g in groups)
                    {
                        if (on) Immortal.Add(g.Id); else Immortal.Remove(g.Id);
                    }
                    if (on) HealGroups(groups);
                    return string.Join(", ", groups.Select(g => SquadName(g.Id))) + (on ? " unsterblich" : " wieder sterblich");
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
            if (InstantMake) on.Add("Sofort herstellen");
            if (InstantDemolish) on.Add("Sofort abbauen");
            if (ScavBoostAll > 1.001f) on.Add($"Plündern +{(ScavBoostAll - 1f) * 100f:0} % (alle)");
            if (OneHitAll) on.Add("Sofort töten (alle)");
            else if (OneHit.Count > 0) on.Add("Sofort töten (" + OneHit.Count + (OneHit.Count == 1 ? " Trupp)" : " Trupps)"));
            if (WalkBoost.Count > 0) on.Add(WalkBoost.Count + (WalkBoost.Count == 1 ? " Trupp" : " Trupps") + " mit anderem Lauftempo");
            if (ScavBoost.Count > 0) on.Add(ScavBoost.Count + (ScavBoost.Count == 1 ? " Trupp" : " Trupps") + " plündert schneller");
            if (SpeedBoost.Count > 0) on.Add(SpeedBoost.Count + (SpeedBoost.Count == 1 ? " Fahrzeug" : " Fahrzeuge") + " mit anderem Tempo");
            if (Immortal.Count > 0) on.Add(Immortal.Count + (Immortal.Count == 1 ? " Trupp" : " Trupps") + " unsterblich");
            if (IndestructibleBuildings.Count > 0) on.Add(IndestructibleBuildings.Count + (IndestructibleBuildings.Count == 1 ? " Gebäude" : " Gebäude") + " unzerstörbar");
            if (Indestructible.Count > 0) on.Add(Indestructible.Count + (Indestructible.Count == 1 ? " Fahrzeug" : " Fahrzeuge") + " unzerstörbar");
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
            float now = Time.realtimeSinceStartup;
            if (WalkBoost.Count > 0 && now >= _nextWalkRebuild && !IsGuest)
            {
                _nextWalkRebuild = now + 2f; // neue Mitglieder, Wechsel im Trupp
                try { RebuildWalk(); } catch { }
            }
            if ((!InstantBuild && !InstantMake && !InstantDemolish) || !Allowed || IsGuest)
                return; // Sofort-Bauen/-Herstellen/-Abbauen rechnet nur der Host
            if (now < _nextInstant)
                return;
            _nextInstant = now + InstantInterval;
            if (InstantBuild)
            {
                int n = FinishAllConstruction();
                if (n > 0)
                    Plugin.Log.LogInfo($"IFZCOOP Entwickler: {n} Baustellen sofort fertig");
            }
            if (InstantDemolish)
            {
                int d = DemolishAllNow();
                if (d > 0)
                    Plugin.Log.LogInfo($"IFZCOOP Entwickler: {d} Abbau-Arbeiten sofort fertig");
            }
            if (InstantMake)
            {
                int m = MakeAllNow();
                if (m > 0)
                    Plugin.Log.LogInfo($"IFZCOOP Entwickler: {m} Aufträge sofort hergestellt");
            }
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
            // Auswahl merken, solange die Maus nicht über dem Fenster ist: ein Klick ins Fenster kann im Spiel die
            // Trupp-Auswahl aufheben, die Knöpfe sollen trotzdem für die vorher gewählten Trupps gelten.
            if (Event.current.type == EventType.Layout)
            {
                var mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
                if (!_rect.Contains(mouse))
                {
                    _selIds = CurrentSelection();
                    _selBuilding = _session.Presence?.CurrentBuildingId() ?? 0;
                    _selVehicle = CurrentVehicleId();
                }
            }
            _rect = GUILayout.Window(WindowId, _rect, DrawContents, IsGuest ? "Entwickler-Menü (Gast, der Host führt aus)" : "Entwickler-Menü");
            Menu.ModUi.Report(WindowId, _rect);
        }

        // ================================================================ ausgewählte Trupps

        /// <summary>Unsterbliche Trupps (Group.Id). Wirkt beim Host/Einzelspiel: UnitPatches.ImmortalPrefix auf Character.SubtractHp.</summary>
        public static readonly HashSet<string> Immortal = new HashSet<string>();

        private List<string> _selIds = new List<string>();
        private long _selBuilding;
        private string _selVehicle;
        private static string _selVehicleStatic = "";
        private int _spawnIdx;
        private Controllers.InfoPanelController _infoPanel;

        private static readonly HarmonyLib.AccessTools.FieldRef<Controllers.VehiclesController, Gameplay.Vehicles.Vehicle.Factory> VehicleFactory =
            HarmonyLib.AccessTools.FieldRefAccess<Controllers.VehiclesController, Gameplay.Vehicles.Vehicle.Factory>("_vehicleFactory");

        /// <summary>Angeklicktes Fahrzeug (Fahrzeug-Fenster offen) oder das Fahrzeug des ersten ausgewählten Trupps.</summary>
        private string CurrentVehicleId()
        {
            try
            {
                if (_infoPanel == null) _infoPanel = UnityEngine.Object.FindObjectOfType<Controllers.InfoPanelController>();
                if (_infoPanel != null && _infoPanel.CurrentDisplayedPanel is UI.InfoPanels.AbandonedVehiclePanelInfo p)
                {
                    var v = p.GetCurrentVehicle();
                    if (v != null && !string.IsNullOrEmpty(v.Id)) return v.Id;
                }
                var sel = GameAccess.Game<SquadsController>()?.SelectedSquads;
                if (sel != null)
                    foreach (var g in sel)
                    {
                        var v = g?.VehicleHandler?.Vehicle;
                        if (v != null && !string.IsNullOrEmpty(v.Id)) return v.Id;
                    }
            }
            catch { }
            return null;
        }

        private static Gameplay.Vehicles.Vehicle FindVehicle(string id)
        {
            var list = GameAccess.Game<Controllers.VehiclesController>()?.Vehicles;
            if (list == null || string.IsNullOrEmpty(id)) return null;
            foreach (var v in list)
                if (v != null && v.Id == id) return v;
            return null;
        }

        private static string VehicleLabel(Gameplay.Vehicles.VehicleType t)
        {
            foreach (var x in VehicleTypes) if (x.type == t) return x.label;
            return t.ToString();
        }

        private static string ModLabel(Gameplay.Vehicles.ModificationType m)
        {
            foreach (var x in Mods) if (x.mod == m) return x.label;
            return m.ToString();
        }

        /// <summary>null = passt; sonst der Grund, warum der Umbau nicht geht.</summary>
        private static string ModBlocked(Gameplay.Vehicles.Vehicle v, Gameplay.Vehicles.ModificationType m)
        {
            if (v.ModificationHandler.HasModification(m)) return "schon eingebaut";
            var cfg = HarmonyLib.Traverse.Create(v.ModificationHandler).Field("_carWorkshopConfig").GetValue<Gameplay.CarWorkshopConfig>();
            var data = cfg?.GetModificationData(m);
            if (cfg == null || data == null) return "gibt es nicht";
            if (!data.IsAvailableFor(v.Data.GetVehicleType())) return "passt nicht zu diesem Fahrzeug";
            var excl = cfg.MutuallyExclusiveModifications;
            if (excl != null && excl.Contains(m))
                foreach (var other in excl)
                    if (other != m && v.ModificationHandler.HasModification(other)) return "geht nicht zusammen mit " + ModLabel(other);
            return null;
        }

        private static string VehicleCmd(string[] a)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            switch (a[0])
            {
                case "vmod":
                {
                    var v = FindVehicle(a[1]);
                    if (v == null) return "Fahrzeug nicht gefunden";
                    var m = (Gameplay.Vehicles.ModificationType)int.Parse(a[2], inv);
                    string why = ModBlocked(v, m);
                    if (why != null) return ModLabel(m) + ": " + why;
                    v.ModificationHandler.AddModification(m);
                    return ModLabel(m) + " eingebaut (" + VehicleLabel(v.Data.GetVehicleType()) + ")";
                }
                case "vfix":
                {
                    var v = FindVehicle(a[1]);
                    if (v == null) return "Fahrzeug nicht gefunden";
                    v.Health.SetHealthAmount01(1f);
                    v.Fuel.FuelTank.SetQuantity(v.Fuel.FuelTank.MaxQuantity);
                    return VehicleLabel(v.Data.GetVehicleType()) + " repariert und vollgetankt";
                }
                case "vspawn":
                {
                    var type = (Gameplay.Vehicles.VehicleType)int.Parse(a[1], inv);
                    var pos = new Vector3(float.Parse(a[2], inv), float.Parse(a[3], inv), float.Parse(a[4], inv));
                    var vc = GameAccess.Game<Controllers.VehiclesController>();
                    var ids = GameAccess.Game<Serialization.Data.IdProvider>();
                    var factory = vc != null ? VehicleFactory(vc) : null;
                    if (vc == null || ids == null || factory == null) return "Fahrzeuge gerade nicht möglich";
                    var v = factory.Create(ids.GetNextId(), type, pos, Quaternion.identity);
                    vc.InitAbandonedVehicle(v);
                    return VehicleLabel(type) + " erzeugt";
                }
            }
            return "?";
        }

        private static Gameplay.Buildings.Building FindBuilding(long id)
        {
            var bc = GameAccess.Game<BuildingsController>();
            if (bc?.Buildings == null || id == 0) return null;
            foreach (var b in bc.Buildings)
                if (b != null && b.ID == id) return b;
            return null;
        }

        private static string BuildingLabel(long id)
        {
            var b = FindBuilding(id);
            if (b == null) return "Haus " + id;
            string st = b.StateMachine?.CurrentState?.StateType.ToString() ?? "?";
            float hp = 0f;
            try { hp = b.Health.GetHealthAmount01() * 100f; } catch { }
            return $"Haus {id} ({st}, {hp:0} % Leben)";
        }

        /// <summary>Befehle für ein einzelnes Gebäude: repair, finish, scavenge, demolish.</summary>
        private static string BuildingCmd(string what, long id)
        {
            var b = FindBuilding(id);
            if (b == null) return "Gebäude nicht gefunden";
            var st = b.StateMachine?.CurrentState?.StateType;
            switch (what)
            {
                case "repair":
                    b.Health.SetHealthAmount01(1f);
                    return $"Haus {id} repariert (volles Leben)";
                case "finish":
                    if (st == Gameplay.Rebuilding.StateMachine.StructureStateType.Constructing)
                    {
                        FinishBuilding(b);
                        return $"Haus {id} fertig gebaut";
                    }
                    return $"Haus {id} wird gerade nicht gebaut ({st})";
                case "scavenge":
                    if (st != Gameplay.Rebuilding.StateMachine.StructureStateType.Abandoned) return $"Haus {id} ist nicht verlassen ({st})";
                    if (b.IsScavenged()) return $"Haus {id} ist schon geplündert";
                    b.InitializeAndEndScavengeWork();
                    return $"Haus {id} geplündert";
                case "demolish":
                    b.StartDemolish();
                    return $"Haus {id} wird abgerissen";
                case "hard":
                    if (b.Health == null) return "kein Leben";
                    IndestructibleBuildings[b.ID] = b.Health;
                    b.Health.SetHealthAmount01(1f);
                    return $"Haus {id} unzerstörbar";
                case "soft":
                    IndestructibleBuildings.Remove(b.ID);
                    return $"Haus {id} wieder zerstörbar";
                case "upgrade":
                {
                    if (st != Gameplay.Rebuilding.StateMachine.StructureStateType.Completed) return $"Haus {id}: Upgrade geht nur bei fertigen Gebäuden ({st})";
                    var ph = b.PartialBuildingHandler;
                    if (ph == null || ph.CurrentPartialValue >= 0.995f) return $"Haus {id} ist schon ganz ausgebaut";
                    ph.SetTargetValue(1f);
                    b.StartExtraAdapt();
                    var w = b.ExtraAdaptationWork;
                    if (w != null && !w.IsDone) w.BuildImmediately();
                    return $"Haus {id} auf 100 % ausgebaut";
                }
            }
            return "?";
        }

        /// <summary>Unzerstörbare Gebäude (ID → ihr Health). UnitPatches.HealthHpPrefix auf Health.SubtractHp.</summary>
        public static readonly Dictionary<long, Gameplay.Units.Health> IndestructibleBuildings = new Dictionary<long, Gameplay.Units.Health>();

        public static bool IsIndestructible(Gameplay.Units.Health h)
        {
            if (IndestructibleBuildings.Count == 0 || h == null) return false;
            foreach (var x in IndestructibleBuildings.Values) if (ReferenceEquals(x, h)) return true;
            return false;
        }

        private static readonly (int type, string label)[] Enforcements =
        {
            (1, "Holzbretter"), (2, "Metallgitter"), (3, "Ziegelverstärkung"), (4, "Diebstahlschutz"),
        };

        private static readonly (string hex, string label)[] LookColors =
        {
            ("E8E2D4", "Weiß"), ("8C8C8C", "Grau"), ("3B3B3B", "Dunkel"), ("9E3B2E", "Rot"),
            ("3E6B3A", "Grün"), ("33557A", "Blau"), ("C9A13B", "Gelb"), ("7A5234", "Braun"),
        };

        private static string LookCmd(string[] a)
        {
            var b = FindBuilding(long.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture));
            if (b == null) return "Gebäude nicht gefunden";
            Color c = Color.white;
            switch (a[0])
            {
                case "blook-enf":
                    return Sync.ProductionSync.InstantCustomize(b, false, c, false, c, true, int.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture));
                case "blook-wall":
                    ColorUtility.TryParseHtmlString("#" + a[2], out c);
                    return Sync.ProductionSync.InstantCustomize(b, true, c, false, Color.white, false, 0);
                case "blook-roof":
                    ColorUtility.TryParseHtmlString("#" + a[2], out c);
                    return Sync.ProductionSync.InstantCustomize(b, false, Color.white, true, c, false, 0);
            }
            return "?";
        }

        private List<string> CurrentSelection()
        {
            var ids = new List<string>();
            try
            {
                var sel = GameAccess.Game<SquadsController>()?.SelectedSquads;
                if (sel != null)
                    foreach (var g in sel)
                        if (g != null && !string.IsNullOrEmpty(g.Id) && !ids.Contains(g.Id)) ids.Add(g.Id);
                // Trupp-Fenster offen, aber nicht „ausgewählt“ (Bericht 2026-11-07): den gezeigten Trupp nehmen
                if (ids.Count == 0)
                {
                    if (_infoPanel == null) _infoPanel = UnityEngine.Object.FindObjectOfType<Controllers.InfoPanelController>();
                    if (_infoPanel != null && _infoPanel.CurrentDisplayedPanel is UI.InfoPanels.SquadPanelInfo sp
                        && sp.Squad != null && !string.IsNullOrEmpty(sp.Squad.Id))
                        ids.Add(sp.Squad.Id);
                }
            }
            catch { }
            return ids;
        }

        // ================================================================ Sofort herstellen, Fahrzeuge unzerstörbar

        /// <summary>„Sofort herstellen“: Trupp-Ausbildung, Werkstatt-Aufträge, Produktion. Nur im laufenden Spiel.</summary>
        public static bool InstantMake;

        /// <summary>Unzerstörbare Fahrzeuge (Vehicle.Id). UnitPatches.VehicleHpPrefix auf Vehicle.SubtractHp.</summary>
        public static readonly HashSet<string> Indestructible = new HashSet<string>();

        /// <summary>Plündern schneller je Trupp (Group.Id → Faktor, 1 = normal). UnitPatches.ScavBoostPrefix.</summary>
        public static readonly Dictionary<string, float> ScavBoost = new Dictionary<string, float>();

        /// <summary>Sofort töten: diese Trupps (Group.Id) bzw. alle Trupps. UnitPatches.OneHitPrefix.</summary>
        public static readonly HashSet<string> OneHit = new HashSet<string>();
        public static bool OneHitAll;

        /// <summary>Tempo je Fahrzeug (Vehicle.Id → Faktor). UnitPatches.VehicleSpeedPostfix.</summary>
        public static readonly Dictionary<string, float> SpeedBoost = new Dictionary<string, float>();

        /// <summary>Lauftempo je Trupp (Group.Id → Faktor) und daraus je Figur (Movement → Faktor) für UnitPatches.WalkPostfix.</summary>
        public static readonly Dictionary<string, float> WalkBoost = new Dictionary<string, float>();
        public static readonly Dictionary<Gameplay.Units.Movements.Movement, float> WalkByMovement = new Dictionary<Gameplay.Units.Movements.Movement, float>();
        private static float _nextWalkRebuild;

        public static void RebuildWalk()
        {
            WalkByMovement.Clear();
            if (WalkBoost.Count == 0) return;
            foreach (var g in SquadsById(WalkBoost.Keys.ToList()))
            {
                float f = WalkBoost[g.Id];
                foreach (var c in g.Characters)
                    if (c != null && c.Movement != null) WalkByMovement[c.Movement] = f;
            }
        }

        public static void ResetGameOnly()
        {
            if (IndestructibleBuildings.Count > 0) IndestructibleBuildings.Clear();
            if (WalkBoost.Count > 0) { WalkBoost.Clear(); WalkByMovement.Clear(); }
            InstantBuild = false;
            InstantMake = false;
            InstantDemolish = false;
            ScavBoostAll = 1f;
            OneHitAll = false;
            if (Immortal.Count > 0) Immortal.Clear();
            if (Indestructible.Count > 0) Indestructible.Clear();
            if (ScavBoost.Count > 0) ScavBoost.Clear();
            if (OneHit.Count > 0) OneHit.Clear();
            if (SpeedBoost.Count > 0) SpeedBoost.Clear();
        }

        private readonly Dictionary<string, string> _numText = new Dictionary<string, string>();

        /// <summary>Schieberegler (min..sliderMax) mit Zahlenfeld daneben, ins Feld geht auch mehr (bis fieldMax).</summary>
        private float SliderField(string key, float value, float min, float sliderMax, float fieldMax, float sliderWidth)
        {
            float onSlider = Mathf.Clamp(value, min, sliderMax);
            float moved = Mathf.Round(GUILayout.HorizontalSlider(onSlider, min, sliderMax, GUILayout.Width(sliderWidth)) / 10f) * 10f;
            if (Mathf.Abs(moved - onSlider) > 0.5f) { value = moved; _numText.Remove(key); }
            string name = "ifzcoop_num_" + key;
            bool focused = GUI.GetNameOfFocusedControl() == name;
            string shown = focused && _numText.TryGetValue(key, out var typed) ? typed : value.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            GUI.SetNextControlName(name);
            string now = GUILayout.TextField(shown, 5, GUILayout.Width(52f));
            if (now != shown)
            {
                _numText[key] = now;
                if (float.TryParse(now, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    value = Mathf.Clamp(parsed, min, fieldMax);
            }
            GUILayout.Label("%", GUILayout.Width(16f));
            return value;
        }

        private string _lastSelKey = "", _lastVehKey = "";
        private float _scavPct = 100f;
        private float _walkPct = 200f;
        private float _speedPct = 200f;

        private static readonly HarmonyLib.AccessTools.FieldRef<Gameplay.Buildings.Building, Gameplay.Buildings.GarageHandler> GarageRef =
            HarmonyLib.AccessTools.FieldRefAccess<Gameplay.Buildings.Building, Gameplay.Buildings.GarageHandler>("GarageHandler");

        /// <summary>„Sofort abbauen“: Abbau/Rückbau von Häusern und Anlagen, Fahrzeuge zerlegen in der Werkstatt.</summary>
        public static bool InstantDemolish;

        /// <summary>Plündern schneller für alle Spieler-Trupps (Faktor, 1 = normal); einzelne Trupps (ScavBoost) gehen vor.</summary>
        public static float ScavBoostAll = 1f;
        private float _scavAllPct = 0f;

        private static int DemolishAllNow()
        {
            int n = 0;
            var works = new List<Gameplay.Units.Workers.WorkSystem.Works.DisassembleWork>();
            var bc = GameAccess.Game<BuildingsController>();
            if (bc?.Buildings != null)
                foreach (var b in bc.Buildings)
                {
                    if (b == null) continue;
                    if (b.DeconstructWork != null && !b.DeconstructWork.IsDone) works.Add(b.DeconstructWork);
                    if (b.DeadaptationWork != null && !b.DeadaptationWork.IsDone) works.Add(b.DeadaptationWork);
                }
            foreach (var s in Sync.StructureSync.All())
                if (s?.DeconstructWork != null && !s.DeconstructWork.IsDone) works.Add(s.DeconstructWork);
            foreach (var w in works)
            {
                try
                {
                    // Zeit voll: beim nächsten Arbeitsschritt fallen alle Rohstoffe, danach ist der Abbau fertig
                    if (w.TimeDeconstructing < w.TimeToDeconstruct) { w.SetTimeDeconstructing(w.TimeToDeconstruct); n++; }
                }
                catch (Exception e) { Plugin.Log.LogDebug("Sofort abbauen: " + e.Message); }
            }
            // Werkstatt: Zerlege-Aufträge sofort fertig
            var shop = GameAccess.Game<Gameplay.Units.Workers.CarWorkshopController>();
            if (shop != null && bc?.Buildings != null)
                foreach (var b in bc.Buildings.ToList())
                {
                    try
                    {
                        var w = b?.CarWorkshopWork;
                        if (w?.CurrentRequest == null || w.CurrentRequest.IsComplete || w.CurrentRequest.Type != Gameplay.Buildings.CarWorkshopRequestType.Deconstruction) continue;
                        w.CurrentRequest.SetProgress(1f);
                        HarmonyLib.AccessTools.Method(typeof(Gameplay.Units.Workers.CarWorkshopController), "HandleRequestCompleted").Invoke(shop, new object[] { w });
                        n++;
                    }
                    catch (Exception e) { Plugin.Log.LogDebug("Sofort zerlegen: " + e.Message); }
                }
            return n;
        }

        private static int MakeAllNow()
        {
            int n = 0;
            // Trupps: Ausbildungszeit voll, fertig sobald die Arbeiter im Gebäude sind (SquadCreatorModule.CustomUpdate)
            try
            {
                var sc = GameAccess.Game<SquadCreatorController>();
                if (sc != null)
                    foreach (var s in sc.StructuresWithSquadCreator.ToList())
                    {
                        var m = Sync.SquadQueueSync.Module(s);
                        if (m?.CurrentRequest == null || m.CurrentRequest.Progress >= 1f) continue;
                        var t = HarmonyLib.Traverse.Create(m);
                        float total = t.Field("_totalProductionTime").GetValue<float>();
                        if (total > 0f && t.Field("_currentProductionTime").GetValue<float>() < total)
                        {
                            t.Field("_currentProductionTime").SetValue(total);
                            n++;
                        }
                    }
            }
            catch (Exception e) { Plugin.Log.LogDebug("Sofort herstellen Trupps: " + e.Message); }

            var bc = GameAccess.Game<BuildingsController>();
            if (bc?.Buildings == null) return n;
            var shop = GameAccess.Game<Gameplay.Units.Workers.CarWorkshopController>();
            foreach (var b in bc.Buildings.ToList())
            {
                if (b == null) continue;
                // Werkstatt: laufenden Auftrag abschließen (wie Testbefehl "shop done")
                try
                {
                    var w = b.CarWorkshopWork;
                    if (shop != null && w?.CurrentRequest != null && !w.CurrentRequest.IsComplete
                        && (w.CurrentRequest.Type == Gameplay.Buildings.CarWorkshopRequestType.Deconstruction || (GarageRef(b)?.HasGarage ?? false)))
                    {
                        w.CurrentRequest.SetProgress(1f);
                        HarmonyLib.AccessTools.Method(typeof(Gameplay.Units.Workers.CarWorkshopController), "HandleRequestCompleted").Invoke(shop, new object[] { w });
                        n++;
                    }
                }
                catch (Exception e) { Plugin.Log.LogDebug($"Sofort herstellen Werkstatt {b.ID}: {e.Message}"); }
                // Produktion: begonnene Runde gleich fertig (beim nächsten Arbeitsschritt kommt das Ergebnis)
                try
                {
                    var pw = b.ProductionWork;
                    var pd = pw?.ProductionData;
                    if (pd != null && pw.CurrentProductionTime > 0f && pw.CurrentProductionTime < pd.GetProductionTime())
                    {
                        pw.SetCurrentProductionTime(pd.GetProductionTime());
                        n++;
                    }
                }
                catch (Exception e) { Plugin.Log.LogDebug($"Sofort herstellen Produktion {b.ID}: {e.Message}"); }
            }
            return n;
        }

        // ================================================================ Trupps: Fähigkeiten, Waffen, Schutz

        private static readonly (Gameplay.Units.Skills.SkillId id, string label)[] SkillDefs =
        {
            (Gameplay.Units.Skills.SkillId.Slasher, "Nahkämpfer"), (Gameplay.Units.Skills.SkillId.Strong, "Stark"),
            (Gameplay.Units.Skills.SkillId.HawkEye, "Adlerauge"), (Gameplay.Units.Skills.SkillId.SharpShooter, "Scharfschütze"),
            (Gameplay.Units.Skills.SkillId.RaceDriver, "Rennfahrer"), (Gameplay.Units.Skills.SkillId.EconomicDriver, "Sparfahrer"),
            (Gameplay.Units.Skills.SkillId.Shoplifter, "Langfinger"), (Gameplay.Units.Skills.SkillId.Inspector, "Spürnase"),
        };

        private readonly Dictionary<Gameplay.Units.Skills.SkillId, Sprite> _skillIcons = new Dictionary<Gameplay.Units.Skills.SkillId, Sprite>();

        private Sprite SkillIcon(Gameplay.Units.Skills.SkillId id, IEnumerable<Group> groups)
        {
            if (_skillIcons.TryGetValue(id, out var s) && s != null) return s;
            try
            {
                foreach (var g in groups)
                    foreach (var c in g.Characters)
                        foreach (var sk in c.SkillsHandler.Skills)
                            if (sk.Id == id && sk.IconSprite != null) { _skillIcons[id] = sk.IconSprite; return sk.IconSprite; }
            }
            catch { }
            return null;
        }

        /// <summary>Wie viele Figuren der Trupps haben die Fähigkeit aktiv (von wie vielen).</summary>
        private static (int on, int all) SkillCount(IEnumerable<Group> groups, Gameplay.Units.Skills.SkillId id)
        {
            int on = 0, all = 0;
            foreach (var g in groups)
                foreach (var c in g.Characters)
                {
                    if (c == null || c.IsDead()) continue;
                    all++;
                    if (c.SkillsHandler != null && c.SkillsHandler.HasActiveSkill(id)) on++;
                }
            return (on, all);
        }

        private static string SquadCmd(string[] a)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var groups = SquadsById(a[1].Split(',')).ToList();
            if (groups.Count == 0) return "Trupp nicht gefunden";
            string names = string.Join(", ", groups.Select(g => SquadName(g.Id)));
            int k = 0;
            switch (a[0])
            {
                case "sqskill":
                {
                    var id = (Gameplay.Units.Skills.SkillId)int.Parse(a[2], inv);
                    bool on = a[3] == "1";
                    foreach (var g in groups)
                        foreach (var c in g.Characters)
                        {
                            if (c?.SkillsHandler == null || c.IsDead()) continue;
                            foreach (var sk in c.SkillsHandler.Skills)
                                if (sk.Id == id && sk.IsActivated != on) { sk.SetActiveStatus(on); k++; }
                        }
                    string label = SkillDefs.FirstOrDefault(x => x.id == id).label ?? id.ToString();
                    return $"{label} {(on ? "gegeben" : "entfernt")}: {k} Figuren ({names})";
                }
                case "sqweapon":
                {
                    var id = (ResourceID)int.Parse(a[2], inv);
                    foreach (var g in groups)
                        foreach (var c in g.Characters)
                        {
                            if (c?.ItemsContainer == null || c.IsDead()) continue;
                            if (c.ItemsContainer.WeaponHandler.TryAssignWeapon(id, dropPreviousWeapon: false))
                            {
                                c.CharacterTextureRandomizer?.SetWeaponAppearance(id);
                                k++;
                            }
                        }
                    return $"{LabelOf(id)} an {k} Figuren ({names})";
                }
                case "sqgear":
                {
                    var id = (ResourceID)int.Parse(a[2], inv);
                    foreach (var g in groups)
                        foreach (var c in g.Characters)
                        {
                            if (c?.ItemsContainer == null || c.IsDead()) continue;
                            c.ItemsContainer.GearHandler.AssignGear(id, 1f, dropPreviousGear: false);
                            k++;
                        }
                    return $"{LabelOf(id)} an {k} Figuren ({names})";
                }
            }
            return "?";
        }

        private static IEnumerable<Group> SquadsById(IEnumerable<string> ids)
        {
            var set = new HashSet<string>(ids);
            var squads = GameAccess.Game<SquadsController>()?.Squads;
            if (squads == null) yield break;
            foreach (var g in squads)
                if (g != null && set.Contains(g.Id)) yield return g;
        }

        private static string SquadName(string id)
        {
            foreach (var g in SquadsById(new[] { id }))
                return string.IsNullOrEmpty(g.GroupName) ? id : g.GroupName;
            return id;
        }

        /// <summary>Name plus, was für diesen Trupp gerade gilt (unsterblich, Sofort töten, Tempo).</summary>
        private static string SquadWithState(string id)
        {
            var s = new List<string>();
            if (Immortal.Contains(id)) s.Add("unsterblich");
            if (OneHit.Contains(id)) s.Add("Sofort töten");
            if (ScavBoost.TryGetValue(id, out var f1)) s.Add("Plündern +" + ((f1 - 1f) * 100f).ToString("0") + " %");
            if (WalkBoost.TryGetValue(id, out var f2)) s.Add("Lauftempo " + (f2 * 100f).ToString("0") + " %");
            return SquadName(id) + (s.Count > 0 ? " <color=#ffd24a>(" + string.Join(", ", s) + ")</color>" : "");
        }

        private static int HealGroups(IEnumerable<Group> groups)
        {
            int k = 0;
            foreach (var g in groups)
                foreach (var c in g.Characters)
                    if (c != null && !c.IsDead()) { c.Health.SetHealthAmount01(1f); k++; }
            return k;
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
            GUI.tooltip = ""; // sonst bleibt der Hinweis eines anderen Reiters stehen
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
                _tab = GUILayout.SelectionGrid(_tab, Tabs, 4); // 7 Reiter: zwei Reihen

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(440f));
            try
            {
                if (Searching)
                {
                    DrawFlags(); DrawStock(); DrawBase(); DrawResearch(); DrawSquads(); DrawVehicles(); DrawWorld();
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
                        case 5: DrawVehicles(); break;
                        case 6: DrawWorld(); break;
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
            DrawTooltip();
            GUI.DragWindow();
        }

        // ================================================================ Schalter

        private void DrawFlags()
        {
            Section("Dauer-Schalter (bleiben an, bis du sie ausmachst)", FlagDefs.Select(f => f.label).Concat(new[] { "Sofort bauen", "Sofort herstellen", "Sofort töten", "Sofort abbauen", "Plündern schneller" }).ToArray());
            if (Match("Sofort bauen"))
            {
                bool now = GUILayout.Toggle(InstantBuild, " Sofort bauen  (jede Baustelle wird gleich fertig, nur in diesem Spiel)");
                if (now != InstantBuild)
                    Run("instant " + (now ? "1" : "0"));
            }
            if (Match("Sofort töten"))
            {
                bool now = GUILayout.Toggle(OneHitAll, " Sofort töten  (jeder Treffer deiner Trupps tötet; nur in diesem Spiel)");
                if (now != OneHitAll)
                    Run("onehitall " + (now ? "1" : "0"));
            }
            if (Match("Plündern schneller"))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(" Plündern schneller (alle Trupps): +", GUILayout.Width(230f));
                _scavAllPct = SliderField("scavall", _scavAllPct, 0f, 500f, 10000f, 160f);
                if (GUILayout.Button(new GUIContent("Übernehmen", "Gilt für alle Trupps ohne eigene Einstellung. 0 % = normal. „Schnell plündern“ oben = sofort."), GUILayout.Width(100f)))
                    Run("scavall " + _scavAllPct.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
                GUILayout.EndHorizontal();
                if (!Searching) GUILayout.Label(ScavBoostAll > 1.001f ? $"   Jetzt: +{(ScavBoostAll - 1f) * 100f:0} % für alle" : "   Jetzt: normal", _hint);
            }
            if (Match("Sofort abbauen"))
            {
                bool now = GUILayout.Toggle(InstantDemolish, " Sofort abbauen  (Häuser/Anlagen abbauen und zurückbauen, Fahrzeuge in der Werkstatt zerlegen)");
                if (now != InstantDemolish)
                    Run("instantdemolish " + (now ? "1" : "0"));
            }
            if (Match("Sofort herstellen"))
            {
                bool now = GUILayout.Toggle(InstantMake, " Sofort herstellen  (Trupp-Ausbildung, Fahrzeuge aus der Werkstatt, Produktion; nur in diesem Spiel)");
                if (now != InstantMake)
                    Run("instantmake " + (now ? "1" : "0"));
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
            var shown = items.Where(x => Match(x.label) && HasResData(x.id)).ToArray();
            if (shown.Length == 0) return;
            Section(title, shown.Select(x => x.label).ToArray());
            for (int i = 0; i < shown.Length; i += 4)
            {
                GUILayout.BeginHorizontal();
                for (int j = i; j < i + 4 && j < shown.Length; j++)
                {
                    int click = IconButton2(ResIcon(shown[j].id), shown[j].label + "\n±" + Amount,
                        $"{shown[j].label}: Linksklick +{Amount} ins Lager, Rechtsklick −{Amount} aus dem Lager", 140f);
                    if (click == 1) Run($"give {shown[j].id} {Amount}");
                    else if (click == 2) Run($"take {shown[j].id} {Amount}");
                }
                GUILayout.EndHorizontal();
            }
        }

        // ================================================================ Bilder aus dem Spiel

        private readonly Dictionary<ResourceID, Sprite> _resIcons = new Dictionary<ResourceID, Sprite>();
        private readonly Dictionary<Gameplay.Vehicles.ModificationType, Sprite> _modIcons = new Dictionary<Gameplay.Vehicles.ModificationType, Sprite>();
        private GUIStyle _iconText, _tip;

        private Sprite ResIcon(ResourceID id)
        {
            if (_resIcons.TryGetValue(id, out var s)) return s;
            try { s = Data.Resorces.ResourcesDataContainer.Instance?.GetResourceDataByType(id)?.SpriteUI; }
            catch { s = null; }
            _resIcons[id] = s;
            return s;
        }

        private Sprite ModIcon(Gameplay.Vehicles.Vehicle v, Gameplay.Vehicles.ModificationType m)
        {
            if (_modIcons.TryGetValue(m, out var s) && s != null) return s;
            try
            {
                var cfg = HarmonyLib.Traverse.Create(v.ModificationHandler).Field("_carWorkshopConfig").GetValue<Gameplay.CarWorkshopConfig>();
                s = cfg?.GetModificationData(m)?.ModificationSprite;
            }
            catch { s = null; }
            _modIcons[m] = s;
            return s;
        }

        private readonly Dictionary<Gameplay.Vehicles.VehicleType, Sprite> _vehIcons = new Dictionary<Gameplay.Vehicles.VehicleType, Sprite>();
        private GUIStyle _cardText;

        /// <summary>Bild des Fahrzeugs wie im Fahrzeug-Fenster (GroupIcons.conceptSprite, sonst iconSprite).</summary>
        private Sprite VehicleIcon(Gameplay.Vehicles.VehicleType t)
        {
            if (_vehIcons.TryGetValue(t, out var s)) return s;
            s = null;
            try
            {
                var stats = GameAccess.Game<Gameplay.Vehicles.VehiclesStats>();
                if (stats?.allVehicleStats != null)
                    foreach (var d in stats.allVehicleStats)
                        if (d != null && d.GetId() == t.ToString())
                        {
                            s = d.GroupIcons.conceptSprite != null ? d.GroupIcons.conceptSprite : d.GroupIcons.iconSprite;
                            break;
                        }
            }
            catch { s = null; }
            if (s != null || GameAccess.IsGameStarted) _vehIcons[t] = s;
            return s;
        }

        /// <summary>Bildkarte: großes Bild oben, Name darunter, Hinweis beim Drüberfahren.</summary>
        private bool VehicleCard(Sprite icon, string label, string tooltip)
        {
            if (_cardText == null)
                _cardText = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter };
            var r = GUILayoutUtility.GetRect(134f, 86f, GUILayout.Width(134f), GUILayout.Height(86f));
            bool clicked = GUI.Button(r, new GUIContent("", tooltip));
            if (icon != null) DrawSprite(new Rect(r.x + 5f, r.y + 4f, r.width - 10f, 58f), icon);
            GUI.Label(new Rect(r.x + 2f, r.yMax - 24f, r.width - 4f, 22f), label, _cardText);
            return clicked;
        }

        /// <summary>Sprite in IMGUI zeichnen (auch aus einem Atlas), Seitenverhältnis bleibt.</summary>
        private static void DrawSprite(Rect r, Sprite s)
        {
            if (s == null || s.texture == null) return;
            var uvs = s.uv;
            if (uvs == null || uvs.Length == 0) return;
            float x0 = 1f, y0 = 1f, x1 = 0f, y1 = 0f;
            foreach (var u in uvs) { x0 = Mathf.Min(x0, u.x); y0 = Mathf.Min(y0, u.y); x1 = Mathf.Max(x1, u.x); y1 = Mathf.Max(y1, u.y); }
            var uv = new Rect(x0, y0, x1 - x0, y1 - y0);
            float aspect = s.rect.height > 0f ? s.rect.width / s.rect.height : 1f;
            float w = r.width, h = r.height;
            if (aspect > r.width / Mathf.Max(1f, r.height)) h = w / aspect; else w = h * aspect;
            GUI.DrawTextureWithTexCoords(new Rect(r.x + (r.width - w) / 2f, r.y + (r.height - h) / 2f, w, h), s.texture, uv, true);
        }

        /// <summary>Wie IconButton, aber unterscheidet Links- (1) und Rechtsklick (2); 0 = nichts.</summary>
        private int IconButton2(Sprite icon, string text, string tooltip, float width)
        {
            var e = Event.current;
            // Rechtsklick selbst erkennen (Platz des nächsten Knopfes ist erst nach GetRect bekannt, darum vorher merken)
            bool right = e.type == EventType.MouseDown && e.button == 1;
            Vector2 mouse = e.mousePosition;
            int before = GUIUtility.hotControl;
            var r = GUILayoutUtility.GetRect(width, 46f, GUILayout.Width(width), GUILayout.Height(46f));
            if (right && r.Contains(mouse))
            {
                e.Use();
                return 2;
            }
            bool clicked = DrawIconButtonAt(r, icon, text, tooltip, true);
            return clicked && e.button == 0 ? 1 : 0;
        }

        private bool DrawIconButtonAt(Rect r, Sprite icon, string text, string tooltip, bool enabled)
        {
            if (_iconText == null)
                _iconText = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleLeft, wordWrap = true };
            bool old = GUI.enabled;
            GUI.enabled = old && enabled;
            bool clicked = GUI.Button(r, new GUIContent("", tooltip));
            float textX = r.x + 6f;
            if (icon != null)
            {
                var c = GUI.color;
                if (!GUI.enabled) GUI.color = new Color(1f, 1f, 1f, 0.35f);
                DrawSprite(new Rect(r.x + 4f, r.y + 4f, 38f, 38f), icon);
                GUI.color = c;
                textX = r.x + 46f;
            }
            GUI.Label(new Rect(textX, r.y + 2f, r.xMax - textX - 4f, r.height - 4f), text, _iconText);
            GUI.enabled = old;
            return clicked;
        }

        /// <summary>Knopf mit Spielbild links und Text rechts; der Hinweis erscheint beim Drüberfahren.</summary>
        private bool IconButton(Sprite icon, string text, string tooltip, float width, bool enabled = true)
        {
            if (_iconText == null)
            {
                _iconText = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleLeft, wordWrap = true };
            }
            var r = GUILayoutUtility.GetRect(width, 46f, GUILayout.Width(width), GUILayout.Height(46f));
            bool old = GUI.enabled;
            GUI.enabled = old && enabled;
            bool clicked = GUI.Button(r, new GUIContent("", tooltip));
            float textX = r.x + 6f;
            if (icon != null)
            {
                var c = GUI.color;
                if (!GUI.enabled) GUI.color = new Color(1f, 1f, 1f, 0.35f);
                DrawSprite(new Rect(r.x + 4f, r.y + 4f, 38f, 38f), icon);
                GUI.color = c;
                textX = r.x + 46f;
            }
            GUI.Label(new Rect(textX, r.y + 2f, r.xMax - textX - 4f, r.height - 4f), text, _iconText);
            GUI.enabled = old;
            return clicked;
        }

        /// <summary>Hinweis-Kästchen an der Maus (GUI.tooltip vom Knopf darunter).</summary>
        private void DrawTooltip()
        {
            string tip = GUI.tooltip;
            if (string.IsNullOrEmpty(tip) || Event.current.type != EventType.Repaint) return;
            if (_tip == null)
            {
                _tip = new GUIStyle(GUI.skin.box) { fontSize = 13, wordWrap = true, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(8, 8, 5, 5) };
                _tip.normal.textColor = Color.white;
            }
            var content = new GUIContent(tip);
            float w = Mathf.Min(320f, _tip.CalcSize(content).x + 4f);
            float h = _tip.CalcHeight(content, w);
            var m = Event.current.mousePosition;
            float x = Mathf.Min(m.x + 16f, _rect.width - w - 4f);
            var bg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0f, 0f, 0f, 1f);
            GUI.Box(new Rect(x, m.y + 20f, w, h), content, _tip);
            GUI.Box(new Rect(x, m.y + 20f, w, h), content, _tip); // zweimal = deckender
            GUI.backgroundColor = bg;
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

        /// <summary>
        /// Kennt das Spiel diesen Rohstoff (Bild, Daten)? Ohne Daten stürzt das Lager-Fenster ab und Tauschen mit dem
        /// Lager hängt (Bericht 2026-11-07: Panzer-Munition/Flammenwerfer über F7 ins Lager gelegt).
        /// </summary>
        public static bool HasResData(ResourceID id)
        {
            try { return Data.Resorces.ResourcesDataContainer.Instance?.GetResourceDataByType(id) != null; }
            catch { return false; }
        }

        /// <summary>Rohstoffe ohne Spieldaten aus dem Lager entfernen. Rückgabe: entfernte Namen.</summary>
        public static List<string> CleanStock()
        {
            var removed = new List<string>();
            var stock = GameAccess.Game<StockroomsController>();
            if (stock?.Container == null || Data.Resorces.ResourcesDataContainer.Instance == null) return removed;
            foreach (var kv in stock.Container.Resources.ToList())
            {
                if (kv.Value <= 0f || HasResData(kv.Key)) continue;
                try
                {
                    stock.Container.TryRemoveResource(kv.Key, kv.Value, removeAllIfNeed: true);
                    removed.Add(kv.Key + " (" + kv.Value.ToString("0") + ")");
                }
                catch (Exception e) { Plugin.Log.LogWarning("Lager reparieren " + kv.Key + ": " + e.Message); }
            }
            if (removed.Count > 0) Plugin.Log.LogWarning("IFZCOOP Lager repariert, entfernt: " + string.Join(", ", removed));
            return removed;
        }

        private static string Give(ResourceID id, float qty, string label)
        {
            var stock = GameAccess.Game<StockroomsController>();
            if (stock?.Container == null)
                return "Kein Lager gefunden";
            if (!HasResData(id))
                return (label ?? id.ToString()) + ": kennt das Spiel nicht als Lager-Rohstoff";
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

            Section("Ausgewähltes Gebäude", "Gebäude reparieren", "Gebäude fertig bauen", "Gebäude plündern", "Gebäude abreißen",
                "Upgrade auf 100 %", "Unzerstörbar", "Verstärkung", "Wandfarbe", "Dachfarbe", "Holzbretter", "Metallgitter", "Ziegelverstärkung", "Diebstahlschutz");
            if (!Searching)
                GUILayout.Label(_selBuilding == 0 ? "Ausgewählt: keins (Gebäude im Spiel anklicken)" : "Ausgewählt: " + BuildingLabel(_selBuilding));
            if (_selBuilding != 0)
            {
                string id = _selBuilding.ToString(System.Globalization.CultureInfo.InvariantCulture);
                GUILayout.BeginHorizontal();
                Btn("Gebäude reparieren", "bld repair " + id);
                Btn("Gebäude fertig bauen", "bld finish " + id);
                Btn("Gebäude plündern", "bld scavenge " + id);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                Btn("Upgrade auf 100 %", "bld upgrade " + id);
                bool hardB = IndestructibleBuildings.ContainsKey(_selBuilding);
                if (!IsGuest) Btn(hardB ? "Unzerstörbar: AN (Klick = aus)" : "Unzerstörbar machen", "bld " + (hardB ? "soft " : "hard ") + id);
                else { Btn("Unzerstörbar an", "bld hard " + id); Btn("aus", "bld soft " + id); }
                GUILayout.EndHorizontal();

                // Aussehen: Verstärkung („Mauer“), Wand- und Dachfarbe – sofort, wie „Anpassen“ im Spiel
                if (Match("Verstärkung") || Enforcements.Any(e => Match(e.label)))
                {
                    if (!Searching) GUILayout.Label("Verstärkung (sofort):", _hint);
                    GUILayout.BeginHorizontal();
                    foreach (var (et, el) in Enforcements)
                        if (Match(el) || Match("Verstärkung")) Btn(el, $"blook-enf {id} {et}");
                    GUILayout.EndHorizontal();
                }
                if (Match("Wandfarbe") || Match("Dachfarbe") || Match("Farbe"))
                {
                    foreach (var (kind, title) in new[] { ("wall", "Wandfarbe"), ("roof", "Dachfarbe") })
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(title + ":", GUILayout.Width(80f));
                        foreach (var (hex, cl) in LookColors)
                        {
                            ColorUtility.TryParseHtmlString("#" + hex, out var col);
                            var old = GUI.backgroundColor;
                            GUI.backgroundColor = col;
                            if (GUILayout.Button(new GUIContent("", title + ": " + cl), GUILayout.Width(44f), GUILayout.Height(22f)))
                                Run($"blook-{kind} {id} {hex}");
                            GUI.backgroundColor = old;
                        }
                        GUILayout.EndHorizontal();
                    }
                }
                Btn("Gebäude abreißen", "bld demolish " + id, danger: true);
            }

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
            if (!Searching)
            {
                var all = GameAccess.Game<SquadsController>()?.Squads;
                GUILayout.Label("Trupps insgesamt: " + (all?.Count ?? 0) + (all != null && all.Count > 0 ? "  (" + string.Join(", ", all.Where(g => g != null).Select(g => SquadName(g.Id) + (Immortal.Contains(g.Id) ? " ♥" : ""))) + ")" : ""), _onStyle);
            }
            GUILayout.BeginHorizontal();
            Btn("Alle Trupps heilen", "heal");
            Btn("Munition auffüllen (je 100)", "ammo");
            GUILayout.EndHorizontal();

            Section("Ausgewählte Trupps", "Ausgewählte heilen", "Unsterblich an", "Unsterblich aus", "Alle wieder sterblich", "Sofort töten", "Plündern schneller", "Lauftempo", "Fähigkeiten", "Munition auffüllen (diese Trupps)");
            if (!Searching)
            {
                GUILayout.Label(_selIds.Count == 0
                    ? "Ausgewählt: keiner (Trupp im Spiel anklicken, mit Umschalt mehrere)"
                    : "Ausgewählt: " + string.Join(", ", _selIds.Select(SquadWithState)), _onStyle);
            }
            if (_selIds.Count > 0)
            {
                string ids = string.Join(",", _selIds);
                GUILayout.BeginHorizontal();
                Btn("Ausgewählte heilen", "healsel " + ids);
                Btn("Unsterblich an", "immortal 1 " + ids);
                Btn("Unsterblich aus", "immortal 0 " + ids);
                GUILayout.EndHorizontal();
                Btn("Munition auffüllen (diese Trupps)", "sqammo " + ids);

                var groups = SquadsById(_selIds).ToList();

                // Regler auf die echten Werte des gewählten Trupps setzen, sobald die Auswahl wechselt
                // (vorher zeigten sie den zuletzt eingestellten Wert eines anderen Trupps)
                string selKey = string.Join(",", _selIds);
                if (selKey != _lastSelKey)
                {
                    _lastSelKey = selKey;
                    float sc = 0f, wk = 100f;
                    foreach (var g in groups)
                    {
                        if (ScavBoost.TryGetValue(g.Id, out var f1)) sc = (f1 - 1f) * 100f;
                        if (WalkBoost.TryGetValue(g.Id, out var f2)) wk = f2 * 100f;
                    }
                    _scavPct = Mathf.Round(sc);
                    _walkPct = Mathf.Round(wk);
                    _numText.Remove("scav");
                    _numText.Remove("walk");
                }

                // Sofort töten nur für diese Trupps
                if (Match("Sofort töten"))
                {
                    bool allOne = groups.Count > 0 && groups.All(g => OneHit.Contains(g.Id));
                    GUILayout.BeginHorizontal();
                    Btn(allOne ? "Sofort töten: AN (Klick = aus)" : "Sofort töten (nur diese Trupps)", "sqonehit " + ids + (allOne ? " 0" : " 1"));
                    GUILayout.EndHorizontal();
                }
                // Plündern schneller: Schieberegler 0–500 %
                if (Match("Plündern schneller"))
                {
                    float cur = 0f;
                    foreach (var g in groups) if (ScavBoost.TryGetValue(g.Id, out var f)) cur = Mathf.Max(cur, (f - 1f) * 100f);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Plündern schneller: +", GUILayout.Width(150f));
                    _scavPct = SliderField("scav", _scavPct, 0f, 500f, 10000f, 200f);
                    if (GUILayout.Button(new GUIContent("Übernehmen", "Gilt nur für die ausgewählten Trupps. 0 % = normal."), GUILayout.Width(100f)))
                        Run($"sqscav {ids} {_scavPct.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}");
                    GUILayout.EndHorizontal();
                    if (!Searching) GUILayout.Label(cur > 0f ? $"Jetzt: +{cur:0} % schneller" : "Jetzt: normal", _hint);
                }
                // Lauftempo: 50–500 %
                if (Match("Lauftempo"))
                {
                    float curW = 100f;
                    foreach (var g in groups) if (WalkBoost.TryGetValue(g.Id, out var wf)) curW = wf * 100f;
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Lauftempo:", GUILayout.Width(150f));
                    _walkPct = SliderField("walk", _walkPct, 50f, 500f, 2000f, 200f);
                    if (GUILayout.Button(new GUIContent("Übernehmen", "Gilt nur für die ausgewählten Trupps (zu Fuß). 100 % = normal."), GUILayout.Width(100f)))
                        Run($"sqwalk {ids} {_walkPct.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}");
                    GUILayout.EndHorizontal();
                    if (!Searching) GUILayout.Label($"Jetzt: {curW:0} %", _hint);
                }

                // Fähigkeiten: Klick schaltet um (an, wenn noch nicht alle sie haben, sonst aus)
                if (SkillDefs.Any(sd => Match(sd.label)) || Match("Fähigkeiten"))
                {
                    if (!Searching) GUILayout.Label("Fähigkeiten (Klick = an/aus für alle im Trupp):", _hint);
                    var shownSkills = SkillDefs.Where(sd => Match(sd.label) || Match("Fähigkeiten")).ToArray();
                    for (int i = 0; i < shownSkills.Length; i += 4)
                    {
                        GUILayout.BeginHorizontal();
                        for (int j = i; j < i + 4 && j < shownSkills.Length; j++)
                        {
                            var (sid, slabel) = shownSkills[j];
                            var (on, all) = SkillCount(groups, sid);
                            bool full = all > 0 && on == all;
                            string text = slabel + "\n" + (full ? "✔ alle" : on > 0 ? $"{on}/{all}" : "–");
                            string tip = full ? slabel + " entfernen" : slabel + " allen im Trupp geben";
                            if (IconButton(SkillIcon(sid, groups), text, tip, 140f))
                                Run($"sqskill {ids} {(int)sid} {(full ? 0 : 1)}");
                        }
                        GUILayout.EndHorizontal();
                    }
                }
                // Waffen und Schutzkleidung für alle im Trupp
                var weapons = Weapons.Where(x => HasResData(x.id) && Match(x.label)).ToArray();
                if (weapons.Length > 0)
                {
                    if (!Searching) GUILayout.Label("Waffe für alle im Trupp:", _hint);
                    for (int i = 0; i < weapons.Length; i += 4)
                    {
                        GUILayout.BeginHorizontal();
                        for (int j = i; j < i + 4 && j < weapons.Length; j++)
                            if (IconButton(ResIcon(weapons[j].id), weapons[j].label, weapons[j].label + " allen im Trupp in die Hand geben", 140f))
                                Run($"sqweapon {ids} {(int)weapons[j].id}");
                        GUILayout.EndHorizontal();
                    }
                }
                var armor = Armor.Where(x => HasResData(x.id) && Match(x.label)).ToArray();
                if (armor.Length > 0)
                {
                    if (!Searching) GUILayout.Label("Schutzkleidung für alle im Trupp:", _hint);
                    for (int i = 0; i < armor.Length; i += 4)
                    {
                        GUILayout.BeginHorizontal();
                        for (int j = i; j < i + 4 && j < armor.Length; j++)
                            if (IconButton(ResIcon(armor[j].id), armor[j].label, armor[j].label + " allen im Trupp anziehen", 140f))
                                Run($"sqgear {ids} {(int)armor[j].id}");
                        GUILayout.EndHorizontal();
                    }
                }
            }
            if (!IsGuest && Immortal.Count > 0)
            {
                if (!Searching) GUILayout.Label("<color=#ffd24a>Unsterblich: " + string.Join(", ", Immortal.Select(SquadName)) + "</color>", _onStyle);
                Btn("Alle wieder sterblich", "immortal 0 all");
            }
            Section("Gegner", "Verstecke und Höhlen aufdecken", "Alle Infizierten-Horden töten");
            Btn("Verstecke und Höhlen aufdecken", "reveal");
            Btn("Alle Infizierten-Horden töten", "killhordes", danger: true);
        }

        // ================================================================ Fahrzeuge

        private void DrawVehicles()
        {
            Section("Ausgewähltes Fahrzeug", "Reparieren und volltanken", "Fahrzeug löschen", "Unzerstörbar", "Tempo", "Geschwindigkeit", "Umbauen", "Panzerung", "Turbo", "Schweres MG", "Panzerkanone", "Kofferraum", "Tank", "Rammschutz");
            var v = FindVehicle(_selVehicle);
            if (!Searching)
                GUILayout.Label(v == null
                    ? "Ausgewählt: keins (Fahrzeug anklicken, oder einen Trupp im Fahrzeug auswählen)"
                    : "Ausgewählt: " + VehicleLabel(v.Data.GetVehicleType()) + "  (" + _selVehicle + ")");
            if (v != null)
            {
                GUILayout.BeginHorizontal();
                Btn("Reparieren und volltanken", "vfix " + v.Id);
                bool hard = Indestructible.Contains(v.Id);
                if (Match("Unzerstörbar"))
                {
                    if (!IsGuest) Btn(hard ? "Unzerstörbar: AN (Klick = aus)" : "Unzerstörbar machen", "vhard " + v.Id + (hard ? " 0" : " 1"));
                    else
                    {
                        Btn("Unzerstörbar an", "vhard " + v.Id + " 1");
                        Btn("aus", "vhard " + v.Id + " 0");
                    }
                }
                GUILayout.EndHorizontal();
                if (v.Owner == null) Btn("Fahrzeug löschen", "vdel " + v.Id, danger: true);
                else if (Match("Fahrzeug löschen") && !Searching) GUILayout.Label("Löschen geht erst, wenn kein Trupp drinsitzt.", _hint);
                if (Match("Tempo") || Match("Geschwindigkeit"))
                {
                    float curSpeed = SpeedBoost.TryGetValue(v.Id, out var sf) ? sf * 100f : 100f;
                    if (v.Id != _lastVehKey)
                    {
                        _lastVehKey = v.Id;
                        _speedPct = Mathf.Round(curSpeed);
                        _numText.Remove("speed");
                    }
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Tempo:", GUILayout.Width(60f));
                    _speedPct = SliderField("speed", _speedPct, 50f, 500f, 2000f, 220f);
                    if (GUILayout.Button(new GUIContent("Übernehmen", "Gilt nur für dieses Fahrzeug. 100 % = normal."), GUILayout.Width(100f)))
                        Run($"vspeed {v.Id} {_speedPct.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}");
                    if (GUILayout.Button(new GUIContent("Normal", "Tempo zurück auf 100 %"), GUILayout.Width(70f)))
                    {
                        _speedPct = 100f;
                        Run($"vspeed {v.Id} 100");
                    }
                    GUILayout.EndHorizontal();
                    if (!Searching) GUILayout.Label($"Jetzt: {curSpeed:0} % (Höchstgeschwindigkeit)", _hint);
                }
                if (!Searching) GUILayout.Label("Umbauen (wie in der Werkstatt, sofort und kostenlos):", _hint);
                int col = 0;
                GUILayout.BeginHorizontal();
                foreach (var (mod, label) in Mods)
                {
                    string why = null;
                    try { why = ModBlocked(v, mod); } catch { }
                    if (!Match(label)) continue;
                    if (col == 4) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); col = 0; }
                    col++;
                    bool ok = why == null;
                    string text = ok ? label : label + "\n(" + (why == "schon eingebaut" ? "drin" : "passt nicht") + ")";
                    string tip = ok ? label + " einbauen (sofort, kostenlos)" : label + ": " + why;
                    if (IconButton(ModIcon(v, mod), text, tip, 140f, ok))
                        Run("vmod " + v.Id + " " + (int)mod);
                }
                GUILayout.EndHorizontal();
                if (!Searching) GUILayout.Label("Ausgegraut = schon eingebaut oder passt nicht zu diesem Fahrzeug. Ausbauen geht in der Werkstatt.", _hint);
            }

            Section("Fahrzeug erzeugen", "Fahrzeug erzeugen", "Panzer", "Bus", "Lastwagen", "Panzerwagen");
            if (Searching && !VehicleTypes.Any(t => Match(t.label)) && !Match("Fahrzeug erzeugen")) return;
            if (!Searching) GUILayout.Label("Fahrzeug anklicken, dann einmal auf die Karte klicken = ein Fahrzeug (Rechtsklick bricht ab). Es steht herrenlos da: mit einem Trupp einsteigen.", _hint);
            var shown = VehicleTypes.Where(t => !Searching || Match(t.label) || Match("Fahrzeug erzeugen")).ToArray();
            for (int i = 0; i < shown.Length; i += 4)
            {
                GUILayout.BeginHorizontal();
                for (int j = i; j < i + 4 && j < shown.Length; j++)
                    if (VehicleCard(VehicleIcon(shown[j].type), shown[j].label, shown[j].label + " erzeugen: danach einmal auf die Karte klicken"))
                        StartSpawnCursor(shown[j].type);
                GUILayout.EndHorizontal();
            }
        }

        private void StartSpawnCursor(Gameplay.Vehicles.VehicleType type)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            try
            {
                // Genau ein Fahrzeug je Knopfdruck. Der Klick auf den Knopf selbst (kommt im Spiel als Loslassen an) und
                // Klicks ins Entwickler-Fenster zählen nicht (Bericht 2026-11-07: Panzer unter dem Fenster, jeder Klick einer).
                float armedAt = Time.realtimeSinceStartup + 0.35f;
                int startFrame = Time.frameCount;
                bool done = false;
                GameInput.CursorsManager.SelectCursor(new GameInput.Debug.InstantiateObjectCursor(pos =>
                {
                    if (done || Time.frameCount <= startFrame + 1 || Time.realtimeSinceStartup < armedAt) return;
                    var mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
                    if (Visible && _rect.Contains(mouse)) return;
                    done = true;
                    Run($"vspawn {(int)type} {pos.x.ToString("0.##", inv)} {pos.y.ToString("0.##", inv)} {pos.z.ToString("0.##", inv)}");
                    try { GameInput.CursorsManager.SelectDefaultCursor(); } catch { }
                }));
                _last = "„" + VehicleLabel(type) + "“: jetzt einmal auf die Karte klicken (Rechtsklick = abbrechen)";
            }
            catch (Exception e)
            {
                _last = "Geht gerade nicht: " + e.Message;
            }
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

        /// <summary>
        /// Baustelle wie das Spiel selbst fertigstellen (Building.AdaptImmediately: BuildImmediately → Arbeit erledigt →
        /// Spiel wechselt auf „fertig“ mit allen Fertig-Schritten). Früher direkt ChangeTo(Completed): dabei fehlten
        /// Schritte, ein Umbau wurde z. B. zum Lager statt zum gewählten Gebäude (Bericht 2026-11-07).
        /// </summary>
        private static void FinishBuilding(Gameplay.Buildings.Building b)
        {
            var w = b.BuildStructureWork;
            if (w != null && !w.IsDone)
            {
                b.BuildImmediately();
                if (b.StateMachine?.CurrentState?.StateType != Gameplay.Rebuilding.StateMachine.StructureStateType.Constructing)
                    return;
            }
            if (b.StateMachine?.CurrentState?.StateType == Gameplay.Rebuilding.StateMachine.StructureStateType.Constructing)
                b.StateMachine.ChangeTo(Gameplay.Rebuilding.StateMachine.StructureStateType.Completed);
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
                            FinishBuilding(b);
                            n++;
                        }
                        catch (Exception e) { Plugin.Log.LogDebug($"Sofort bauen Haus {b.ID}: {e.Message}"); }
                    }
                    else if (st == Gameplay.Rebuilding.StateMachine.StructureStateType.ExtraAdapting)
                    {
                        try
                        {
                            var ew = b.ExtraAdaptationWork;
                            if (ew != null && !ew.IsDone) { ew.BuildImmediately(); n++; }
                        }
                        catch (Exception e) { Plugin.Log.LogDebug($"Sofort erweitern Haus {b.ID}: {e.Message}"); }
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
