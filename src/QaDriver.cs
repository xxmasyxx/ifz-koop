using System;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;
using Controllers.Time;
using IfzCoop.Game;
using UI.Clock;
using UnityEngine;
using ZenjectExtensions.Installers;

namespace IfzCoop
{
    /// <summary>
    /// Testmodus über Startoptionen (nur aktiv, wenn eine davon gesetzt ist):
    ///   -ifzcoop-load &lt;Spielstand-Ordner&gt;   lädt diesen Spielstand aus dem Hauptmenü
    ///   -ifzcoop-host-direct &lt;Port&gt;          hostet direkt (TCP), sobald das Spiel läuft
    ///   -ifzcoop-join &lt;IP:Port&gt;              tritt direkt bei (versucht es bis zu 30-mal)
    ///   -ifzcoop-speed &lt;1|2|3&gt;               Host stellt dieses Tempo ein, sobald ein Gast im Spiel ist
    ///   -ifzcoop-name &lt;Name&gt;                  Anzeigename (zwei Spiele mit einem Steam-Konto)
    /// Schreibt alle 2 s den Zustand nach BepInEx\ifzcoop-status.txt und lässt das Spiel im Hintergrund weiterlaufen.
    /// </summary>
    internal sealed class QaDriver
    {
        private readonly Session _session;
        private string _load;
        private readonly int _hostPort;
        private readonly string _joinHost;
        private readonly int _joinPort;
        private readonly int _speed;
        private readonly string _statusPath;
        private string _statusTarget;

        internal static QaDriver Instance { get; private set; }

        /// <summary>Zustand wie in ifzcoop-status.txt, auch ohne Testmodus (für den Fehlerbericht F8).</summary>
        internal string StatusSnapshot()
        {
            string tmp = Path.Combine(Paths.BepInExRootPath, "ifzcoop-status-bericht.tmp");
            _statusTarget = tmp;
            try
            {
                WriteStatus();
                return File.Exists(tmp) ? File.ReadAllText(tmp) : "-";
            }
            finally
            {
                _statusTarget = null;
                try { File.Delete(tmp); } catch { }
            }
        }

        private bool _loadDone;
        private float _gameStartedAt = -1f;
        private bool _hostDone;
        private int _joinAttempts;
        private float _nextJoin;
        private bool _speedDone;
        private float _guestReadyAt = -1f;
        private float _nextStatus;

        public bool Active { get; }

        /// <summary>Für Patches: Testmodus läuft (z. B. Speicherwarnung überspringen).</summary>
        public static bool IsActive { get; private set; }

        private readonly bool _newGame;
        private readonly int _newGameX;
        private readonly int _newGameY;

        public QaDriver(Session session)
        {
            _session = session;
            Instance = this;
            var args = Environment.GetCommandLineArgs();
            _load = Arg(args, "-ifzcoop-load");
            int.TryParse(Arg(args, "-ifzcoop-host-direct"), out _hostPort);
            var join = Arg(args, "-ifzcoop-join");
            if (!string.IsNullOrEmpty(join))
            {
                var parts = join.Split(':');
                _joinHost = parts[0];
                if (parts.Length < 2 || !int.TryParse(parts[1], out _joinPort))
                    _joinPort = 27120;
            }
            int.TryParse(Arg(args, "-ifzcoop-speed"), out _speed);
            var newGame = Arg(args, "-ifzcoop-newgame");
            if (!string.IsNullOrEmpty(newGame))
            {
                var p = newGame.Split(',');
                if (p.Length >= 2 && int.TryParse(p[0], out var x) && int.TryParse(p[1], out var y))
                {
                    _newGameX = x;
                    _newGameY = y;
                    _newGame = true;
                }
            }
            Active = _load != null || _newGame || _hostPort > 0 || _joinHost != null || _speed > 0 || Versus.VsQa.HasArgs;
            IsActive = Active;
            _statusPath = Path.Combine(Paths.BepInExRootPath, "ifzcoop-status.txt");
            if (Active)
            {
                Application.runInBackground = true;
                Plugin.Log.LogInfo($"IFZCOOP QA aktiv: load={_load} host={_hostPort} join={_joinHost}:{_joinPort} speed={_speed}");
            }
        }

        private static string QaNewGame(string[] a)
        {
            var inv = CultureInfo.InvariantCulture;
            switch (a.Length > 1 ? a[1] : "")
            {
                case "globe":
                    UnityEngine.Object.FindObjectOfType<UI.EscMenu.MenuCanvas>().ShowCitySearch();
                    return "Globus";
                case "select":
                {
                    var sel = UnityEngine.Object.FindObjectOfType<MapTiler.TileSelector>();
                    if (sel == null) return "keine Kartenauswahl";
                    sel.Select(new Vector3Int(int.Parse(a[2], inv), int.Parse(a[3], inv), 15));
                    return "gewählt " + sel.CurrentTileCoord;
                }
                case "custom":
                {
                    var c = UnityEngine.Object.FindObjectOfType<GameCustomization.GameCustomizeController>();
                    if (c == null) return "kein Anpassungsfenster";
                    c.Show();
                    return "Anpassung offen";
                }
                case "diff":
                    GameAccess.Project<GameCustomization.GameCustomize>().LoadDifficultyLevel((GameCustomization.GameDifficulty)int.Parse(a[2], inv));
                    return "Schwierigkeit " + a[2];
                case "people":
                    GameAccess.Project<GameCustomization.GameCustomize>().LoadPeopleSettings(float.Parse(a[2], inv));
                    return "Bewohner x" + a[2];
                case "close":
                    UnityEngine.Object.FindObjectOfType<GameCustomization.GameCustomizeController>()?.Hide();
                    return "Anpassung zu";
                case "click":
                {
                    // sichtbaren Knopf mit diesem Text anklicken (wie ein Spieler), z. B. "mnew click EDINBURGH"
                    string want = string.Join(" ", a, 2, a.Length - 2);
                    foreach (var b in UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Button>())
                    {
                        if (b == null || !b.isActiveAndEnabled || !b.interactable) continue;
                        var t = b.GetComponentInChildren<TMPro.TMP_Text>(true);
                        if (t != null && t.text.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            b.onClick.Invoke();
                            return "geklickt: " + t.text.Replace("\n", " ");
                        }
                    }
                    return "kein Knopf mit " + want;
                }
            }
            return "?";
        }

        public static string Arg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        public void Update()
        {
            if (!Active)
                return;
            float now = Time.realtimeSinceStartup;
            Application.runInBackground = true;

            bool inMenu = ScenesManager.IsMainSceneActive && !ScenesManager.IsSceneSwitching;

            if (_load != null && !_loadDone && inMenu && now > 12f)
            {
                _loadDone = true;
                TryLoad();
            }

            if (_newGame && !_loadDone && inMenu && now > 12f)
            {
                _loadDone = true;
                Plugin.Log.LogInfo($"IFZCOOP QA neues Spiel auf Kachel {_newGameX},{_newGameY}");
                try
                {
                    Commands.MenuConsoleCommandsHandler.LoadTileWithConfiguration(_newGameX, _newGameY, 15, 1, 9, Vector2Int.zero, fromSuggestedCitiesList: false);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("IFZCOOP QA neues Spiel: " + e);
                }
            }

            if (GameAccess.IsGameStarted)
            {
                if (_gameStartedAt < 0f)
                    _gameStartedAt = now;
            }
            else
            {
                _gameStartedAt = -1f;
            }

            if (_hostPort > 0 && !_hostDone && _gameStartedAt > 0f && now - _gameStartedAt > 5f && _session.Role == Role.None)
            {
                _hostDone = true;
                _session.StartHostDirect(_hostPort);
            }

            if (_joinHost != null && _session.Role == Role.None && _joinAttempts < 30 && now >= _nextJoin
                && now > 15f && (inMenu || GameAccess.IsGameStarted))
            {
                _joinAttempts++;
                _nextJoin = now + 10f;
                Plugin.Log.LogInfo($"IFZCOOP QA Beitritt Versuch {_joinAttempts}");
                _session.JoinDirect(_joinHost, _joinPort);
            }

            if (_speed > 0 && !_speedDone && _session.Role == Role.Host && _session.ActiveGuestCount > 0)
            {
                if (_guestReadyAt < 0f)
                    _guestReadyAt = now;
                else if (now - _guestReadyAt > 5f)
                {
                    _speedDone = true;
                    var type = _speed >= 3 ? TimeSpeedType.Fast : _speed == 2 ? TimeSpeedType.Medium : TimeSpeedType.Normal;
                    GameAccess.SpeedSetter?.TrySetTimeSpeed(type);
                    Plugin.Log.LogInfo("IFZCOOP QA Tempo " + type);
                }
            }

            if (now >= _nextStatus)
            {
                _nextStatus = now + 2f;
                WriteStatus();
                ReadCommands();
            }
        }

        // ------------------------------------------------------------- Befehlskanal
        // Zeilen in BepInEx\ifzcoop-cmd.txt werden ausgeführt und die Datei gelöscht:
        //   speed 0|1|2|3        Tempo (Host; beim Gast als Anfrage)
        //   squad <Größe>         Spieler-Trupp am Hauptquartier (Host)
        //   horde <Typ> <Größe> <Abstand>   Gruppe in Abstand zum Hauptquartier (Host)
        //   move <dx> <dz>        ersten Trupp auswählen und relativ zum Hauptquartier schicken (über SquadsController)
        //   resync                Gast: Spielstand neu holen
        //   hq                    neues Spiel: Hauptquartier wählen und bestätigen (Host)
        private void ReadCommands()
        {
            string path = Path.Combine(Paths.BepInExRootPath, "ifzcoop-cmd.txt");
            if (!File.Exists(path))
                return;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
                File.Delete(path);
            }
            catch (Exception)
            {
                return;
            }
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0)
                    continue;
                try
                {
                    Plugin.Log.LogInfo("IFZCOOP QA cmd: " + line + " -> " + Execute(line.Split(' ')));
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("IFZCOOP QA cmd " + line + ": " + e);
                }
            }
        }

        private string Execute(string[] a)
        {
            var hq = Controllers.HqController.MainHeadquarter;
            var hqPos = hq != null ? hq.transform.position : Vector3.zero;
            var builder = GameAccess.Game<Gameplay.Units.GroupBuilder>();
            var config = JutsuGamesConfig.Runtime.Configs.GetConfig<Gameplay.Units.Enemy.GroupsConfig>();
            var squads = GameAccess.Game<Gameplay.Units.SquadsController>();

            switch (a[0].ToLowerInvariant())
            {
                case "speed":
                    int s = int.Parse(a[1], CultureInfo.InvariantCulture);
                    var type = s <= 0 ? TimeSpeedType.Pause : s == 1 ? TimeSpeedType.Normal : s == 2 ? TimeSpeedType.Medium : TimeSpeedType.Fast;
                    GameAccess.SpeedSetter?.TrySetTimeSpeed(type);
                    return type.ToString();

                case "squad":
                {
                    int size = a.Length > 1 ? int.Parse(a[1], CultureInfo.InvariantCulture) : 4;
                    var g = builder.Clear().SpawnGroupAt(hqPos + new Vector3(8f, 0f, 8f), config.GetDraftByName("player"), size);
                    return g != null ? "Trupp " + g.Id : "kein Trupp";
                }

                case "horde":
                {
                    string draft = a.Length > 1 ? a[1] : "inf_human";
                    int size = a.Length > 2 ? int.Parse(a[2], CultureInfo.InvariantCulture) : 6;
                    float dist = a.Length > 3 ? float.Parse(a[3], CultureInfo.InvariantCulture) : 40f;
                    var g = builder.Clear().SpawnGroupAt(hqPos + new Vector3(dist, 0f, 0f), config.GetDraftByName(draft), size);
                    return g != null ? "Gruppe " + g.Id + " " + g.Fraction : "keine Gruppe";
                }

                case "carhorde":
                {
                    // Host: Gruppe in einem eigenen Auto (wie Banditen/Soldaten im Wagen), z. B. "carhorde provirus_soldier ve_pickup 60"
                    string draft = a.Length > 1 ? a[1] : "provirus_soldier";
                    var vt = (Gameplay.Vehicles.VehicleType)Enum.Parse(typeof(Gameplay.Vehicles.VehicleType), a.Length > 2 ? a[2] : "ve_pickup");
                    float dist = a.Length > 3 ? float.Parse(a[3], CultureInfo.InvariantCulture) : 60f;
                    var g = builder.Clear().SetCharactersCountToCreate(3).SetName("qa").AssignVehicle(vt, canLeaveVehicle: false)
                        .SetPosition(hqPos + new Vector3(0f, 0f, dist)).Build(config.GetDraftByName(draft));
                    return g != null ? "Gruppe " + g.Id + " " + g.Fraction + " Auto " + (g.VehicleHandler.Vehicle != null ? g.VehicleHandler.Vehicle.Id : "-") : "keine Gruppe";
                }

                case "move":
                {
                    if (squads == null || squads.Squads.Count == 0)
                        return "keine Trupps";
                    var first = squads.Squads[0];
                    first.SelectionHandler.Select(tellOnRadio: false);
                    float dx = float.Parse(a[1], CultureInfo.InvariantCulture);
                    float dz = float.Parse(a[2], CultureInfo.InvariantCulture);
                    squads.MoveSelectedTo(hqPos + new Vector3(dx, 0f, dz));
                    return "Trupp " + first.Id + " geschickt";
                }

                case "resync":
                    _session.RequestResync();
                    return "angefordert";

                case "hq":
                    return ChooseHq();

                case "addres":
                {
                    // Host: Rohstoff ins Lager legen, z. B. "addres res_food_rations 25"
                    var id = (Gameplay.GameResources.ResourceID)Enum.Parse(typeof(Gameplay.GameResources.ResourceID), a[1]);
                    float qty = a.Length > 2 ? float.Parse(a[2], CultureInfo.InvariantCulture) : 10f;
                    GameAccess.Game<Controllers.StockroomsController>().Container.ForceAddResource(id, qty, Gameplay.GameResources.ResourceAddReason.None);
                    return id + " +" + qty;
                }

                case "loot":
                {
                    // Host: Kisten in den Rucksack des ersten Trupps, z. B. "loot res_metal 2"
                    if (squads == null || squads.Squads.Count == 0)
                        return "keine Trupps";
                    var id = (Gameplay.GameResources.ResourceID)Enum.Parse(typeof(Gameplay.GameResources.ResourceID), a[1]);
                    float qty = a.Length > 2 ? float.Parse(a[2], CultureInfo.InvariantCulture) : 1f;
                    squads.Squads[0].ResourcesContainer.AddCrates(id, qty, Gameplay.GameResources.ResourceAddReason.None);
                    return "Trupp " + squads.Squads[0].Id + " " + id + " +" + qty;
                }

                case "ammo":
                {
                    // Host: Munition der ersten Figur des ersten Trupps setzen, z. B. "ammo 3"
                    if (squads == null || squads.Squads.Count == 0 || squads.Squads[0].Characters.Count == 0)
                        return "keine Trupps";
                    var c = squads.Squads[0].Characters[0];
                    c.ItemsContainer.AmmunitionHandler.Ammunition.SetQuantity(float.Parse(a[1], CultureInfo.InvariantCulture));
                    return "Figur " + c.Id + " Munition " + a[1];
                }

                case "drop":
                {
                    // Host: Kiste neben das HQ auf den Boden, z. B. "drop res_meat 3"
                    var id = (Gameplay.GameResources.ResourceID)Enum.Parse(typeof(Gameplay.GameResources.ResourceID), a[1]);
                    float qty = a.Length > 2 ? float.Parse(a[2], CultureInfo.InvariantCulture) : 1f;
                    var r = GameAccess.Game<Gameplay.GameResources.ResourcesController>()
                        .DropResourceToCustomPos(new Gameplay.GameResources.ResourceCrate(id, qty), hqPos + new Vector3(12f, 0f, 12f), shouldAddOffset: true);
                    return r != null ? "Kiste " + r.ID + " " + id + " " + qty : "keine Kiste";
                }

                case "pickup":
                {
                    // Host: eine Bodenkiste verschwinden lassen (wie aufgehoben)
                    foreach (var kv in GameAccess.Game<Gameplay.GameResources.ResourcesController>().Resources)
                        if (kv.Value != null && kv.Value.Character == null && !kv.Value.IsDestroying)
                        {
                            kv.Value.Destroy(Gameplay.GameResources.Resource.DestroyReason.PickedBySquad);
                            return "Kiste " + kv.Key + " weg";
                        }
                    return "keine Kiste";
                }

                case "vehicle":
                {
                    // Host: Fahrzeug neben das HQ stellen (wie die Spielkonsole), z. B. "vehicle ve_sedan"
                    var vtype = (Gameplay.Vehicles.VehicleType)Enum.Parse(typeof(Gameplay.Vehicles.VehicleType), a.Length > 1 ? a[1] : "ve_sedan");
                    var vc = GameAccess.Game<Controllers.VehiclesController>();
                    var veh = vc.CreateVehicleAtPosition(vtype, hqPos + new Vector3(15f, 0f, -10f));
                    vc.InitAbandonedVehicle(veh);
                    veh.DiscoverVehicle();
                    return "Fahrzeug " + veh.Id + " " + vtype;
                }

                case "enter":
                {
                    // Host: erster Trupp steigt ins nächste freie Fahrzeug
                    if (squads == null || squads.Squads.Count == 0)
                        return "keine Trupps";
                    var sq = squads.Squads[0];
                    Gameplay.Vehicles.Vehicle best = null;
                    float bestD = float.MaxValue;
                    foreach (var v in GameAccess.Game<Controllers.VehiclesController>().Vehicles)
                    {
                        if (v == null || (v.Owner != null && v.Owner != sq)) continue;
                        float d = (v.Position - sq.Position).sqrMagnitude;
                        if (d < bestD) { bestD = d; best = v; }
                    }
                    if (best == null)
                        return "kein Fahrzeug";
                    sq.VehicleHandler.AssignVehicle(best);
                    return "Trupp " + sq.Id + " in " + best.Id;
                }

                case "exit":
                {
                    // Host: erster Trupp steigt aus ("exit wait": Auto bleibt als wartendes Auto beim Trupp)
                    if (squads == null || squads.Squads.Count == 0)
                        return "keine Trupps";
                    var sq = squads.Squads[0];
                    if (sq.VehicleHandler.Vehicle == null)
                        return "Trupp " + sq.Id + " sitzt in keinem Auto";
                    sq.VehicleHandler.UnassignVehicle(a.Length > 1 && a[1] == "wait");
                    return "Trupp " + sq.Id + " ausgestiegen";
                }

                case "vdamage":
                case "vfuel":
                case "vmod":
                case "vgone":
                {
                    // Host, erstes Fahrzeug mit Besitzer (sonst das erste): "vdamage 30", "vfuel 0.5", "vmod ve_mod_hcal"
                    var list = GameAccess.Game<Controllers.VehiclesController>().Vehicles;
                    Gameplay.Vehicles.Vehicle v = null;
                    foreach (var x in list) if (x != null && x.Owner != null) { v = x; break; }
                    if (v == null && list.Count > 0) v = list[list.Count - 1];
                    if (v == null)
                        return "kein Fahrzeug";
                    switch (a[0].ToLowerInvariant())
                    {
                        case "vdamage": v.Health.SetHp(Mathf.Max(0f, v.Health.Hp - float.Parse(a[1], CultureInfo.InvariantCulture))); break;
                        case "vfuel": v.Fuel.SetFuel(float.Parse(a[1], CultureInfo.InvariantCulture)); break;
                        case "vgone": v.DestroyVehicle(); return "Fahrzeug " + v.Id + " zerstört";
                        default: v.ModificationHandler.AddModification((Gameplay.Vehicles.ModificationType)Enum.Parse(typeof(Gameplay.Vehicles.ModificationType), a[1])); break;
                    }
                    return "Fahrzeug " + v.Id + " " + a[0] + " " + a[1];
                }

                case "mission":
                {
                    // Host: Mission starten ("mission <id>"), erstes offenes Ziel der ersten Mission erledigen ("mission done"),
                    // erste Mission scheitern lassen ("mission fail")
                    var ec = GameAccess.Game<EventsSystem.EventsSystemController>();
                    string arg = a.Length > 1 ? a[1] : "done";
                    if (arg == "done" || arg == "fail")
                    {
                        if (ec.ActiveMissions.Count == 0)
                            return "keine Mission";
                        var m = ec.ActiveMissions[0];
                        if (arg == "fail")
                        {
                            ec.EndMission(m, EventsSystem.Missions.MissionResult.Fail);
                            return "Mission " + m.Id + " gescheitert";
                        }
                        foreach (var oe in m.ObjectiveExpressions)
                            if (!oe.objective.Completed)
                            {
                                oe.objective.Complete();
                                return "Ziel " + m.Id + "/" + oe.objectiveId + " erledigt";
                            }
                        return "alle Ziele erledigt";
                    }
                    return "Mission " + arg + " -> " + ec.StartMission(arg);
                }

                case "event":
                    // Host: Ereignis starten, z. B. "event <id>"
                    return "Ereignis " + a[1] + " -> " + GameAccess.Game<EventsSystem.EventsSystemController>().StartEvent(a[1]);

                case "marker":
                {
                    // Host: Ziel-Marker neben das HQ ("marker"), alle QA-Marker weg ("marker off"), ersten Marker aus-/einblenden ("marker hide|show")
                    var mc = GameAccess.Game<EventsSystem.EventsSystemController>().MarkersController;
                    string arg = a.Length > 1 ? a[1] : "";
                    var all = new System.Collections.Generic.List<EventsSystem.EventMarker>();
                    foreach (var o in mc.Objects) if (o is EventsSystem.EventMarker em && em != null) all.Add(em);
                    if (arg == "off")
                    {
                        int n = 0;
                        foreach (var em in all) if (em.Id.StartsWith("qa_")) { em.Destroy(); n++; }
                        return n + " Marker weg";
                    }
                    if (arg == "hide" || arg == "show")
                    {
                        if (all.Count == 0) return "kein Marker";
                        all[0].SetVisible(arg == "show");
                        return "Marker " + all[0].Id + " " + arg;
                    }
                    string id = "qa_" + UnityEngine.Random.Range(1000, 9999);
                    var mk = mc.SpawnMarker(hqPos + new Vector3(-20f, 0f, 20f), id, false, null, SpawnSystem.SpawnSettings.PlaceTypeToSpawn.Object, default);
                    return mk != null ? "Marker " + mk.Id : "kein Marker";
                }

                case "unlock":
                case "lock":
                {
                    // Host: Inhalt freischalten/sperren ("unlock <contentId>"); "unlock list" nennt die ersten gesperrten
                    var csc = SaveHandler.SaveData.ContentStateController;
                    if (a[1] == "list")
                    {
                        var ids = new System.Collections.Generic.List<string>();
                        foreach (var cs in csc.GetContentStates()) if (!cs.Unlocked && ids.Count < 15) ids.Add(cs.Id);
                        return string.Join(" ", ids);
                    }
                    bool ok = a[0] == "unlock" ? csc.UnlockContentById(a[1]) : csc.LockContentById(a[1]);
                    return a[0] + " " + a[1] + " -> " + ok;
                }

                case "research":
                {
                    // Forschung starten ("research <ResearchID>", "research any" = erste freie), abbrechen ("research cancel"),
                    // Material ("research sci 50"). Läuft über die Spiel-Methoden, beim Gast also als Wunsch an den Host.
                    var rc = GameAccess.Game<Gameplay.Research.ResearchController>();
                    if (a[1] == "sci")
                    {
                        rc.SetScientificMaterialsCount(float.Parse(a[2], CultureInfo.InvariantCulture));
                        return "Material " + a[2];
                    }
                    if (a[1] == "cancel")
                    {
                        if (rc.CurrentTechnology == null) return "keine Forschung";
                        var cid = rc.CurrentTechnology.GetID();
                        AccessTools.Method(typeof(Gameplay.Research.ResearchController), "CancelCurrentResearch").Invoke(rc, new object[] { cid });
                        return "abgebrochen " + cid;
                    }
                    Gameplay.Research.ResearchID rid = default;
                    if (a[1] == "any")
                    {
                        var ser = new ResearchControllerSerialized();
                        ser.Serialize(rc);
                        bool found = false;
                        foreach (var x in ser.Researches)
                            if (x.State == Gameplay.Research.ResearchStates.unlocked && rc.GetResearch(x.ResearchID).Group == Gameplay.Research.ResearchGroup.technology) { rid = x.ResearchID; found = true; break; }
                        if (!found) return "nichts freigeschaltet";
                    }
                    else rid = (Gameplay.Research.ResearchID)Enum.Parse(typeof(Gameplay.Research.ResearchID), a[1]);
                    AccessTools.Method(typeof(Gameplay.Research.ResearchController), "HandleResearchButtonClick").Invoke(rc, new object[] { rid });
                    return "Forschung " + rid + " geklickt";
                }

                case "rprogress":
                {
                    // Host: Fortschritt der laufenden Forschung um N Sekunden (Forscher-Arbeit)
                    var rc = GameAccess.Game<Gameplay.Research.ResearchController>();
                    rc.AddTechnologyResearchProgress(float.Parse(a[1], CultureInfo.InvariantCulture));
                    return "Fortschritt +" + a[1];
                }

                case "law":
                {
                    // Gesetz einführen wie über das Fenster ("law <LawID>"); "law list" nennt alle
                    var lc = GameAccess.Game<Gameplay.Laws.LawsController>();
                    var ser = new LawsControllerSerialized();
                    ser.Serialize(lc);
                    if (a[1] == "list")
                    {
                        var sbl = new StringBuilder();
                        foreach (var l in ser.Laws) sbl.Append(l.LawId).Append(l.IsEnabled ? "* " : " ");
                        return sbl.ToString();
                    }
                    var lid = (Gameplay.Laws.LawID)Enum.Parse(typeof(Gameplay.Laws.LawID), a[1]);
                    var law = AccessTools.Method(typeof(Gameplay.Laws.LawsController), "GetLaw").Invoke(lc, new object[] { lid }) as Gameplay.Laws.Law;
                    law.SetEnabled(true);
                    return "Gesetz " + lid;
                }

                case "mood":
                {
                    // Host: Stimmungs-Modifikator dazu ("mood <id>") oder Liste ("mood list")
                    var mc = GameAccess.Game<Gameplay.Mood.MoodController>();
                    if (a[1] == "died")
                    {
                        AccessTools.Method(typeof(Gameplay.Mood.MoodController), "AddCitizenDiedModifier").Invoke(mc, new object[] { false, true });
                        return "Tod-Modifikator";
                    }
                    return "Modifikator " + a[1] + " -> " + mc.AddModifier(a[1]);
                }

                case "build":
                {
                    // Bauaktion am nächsten passenden Haus beim HQ, über die Spiel-Methoden (beim Gast also als Wunsch):
                    // "build adapt <draftId> [Zielhöhe]", "build done" (Host: laufenden Umbau sofort fertig), "build deadapt",
                    // "build mark" / "build unmark", "build demolish"
                    var bc = GameAccess.Game<Controllers.BuildingsController>();
                    string what = a.Length > 1 ? a[1] : "adapt";
                    Gameplay.Buildings.Building best = null;
                    float bestD = float.MaxValue;
                    foreach (var b in bc.Buildings)
                    {
                        if (b == null || b.IsHq) continue;
                        var st = b.StateMachine?.CurrentState?.StateType ?? Gameplay.Rebuilding.StateMachine.StructureStateType.None;
                        bool ok;
                        switch (what)
                        {
                            case "adapt": ok = st == Gameplay.Rebuilding.StateMachine.StructureStateType.Abandoned && b.CanAdapt; break;
                            case "done": ok = st == Gameplay.Rebuilding.StateMachine.StructureStateType.Constructing; break;
                            case "deadapt": ok = st == Gameplay.Rebuilding.StateMachine.StructureStateType.Completed && b.CanDeadapt; break;
                            case "unmark": ok = b.IsReadyToDemolish; break;
                            default: ok = st == Gameplay.Rebuilding.StateMachine.StructureStateType.Abandoned && !b.IsReadyToDemolish; break;
                        }
                        if (!ok) continue;
                        float d = (b.transform.position - hqPos).sqrMagnitude;
                        if (d < bestD) { bestD = d; best = b; }
                    }
                    if (best == null) return "kein passendes Haus";
                    switch (what)
                    {
                        case "adapt":
                        {
                            var draft = Data.Building.BuildingsDatas.Instance.GetPlaceableObjectDraftById(a.Length > 2 ? a[2] : "bld_house");
                            if (best.ScavengeNotStarted()) best.InitializeAndEndScavengeWork(generateResources: false);
                            float target = a.Length > 3 ? float.Parse(a[3], CultureInfo.InvariantCulture) : 1f;
                            if (target >= 0.999f) GameAccess.Game<Controllers.AdaptController>().StartFullAdapt(best, draft);
                            else { best.PartialBuildingHandler.SetTargetValue(target); best.StartAdapt(draft); }
                            break;
                        }
                        case "done": best.StateMachine.ChangeTo(Gameplay.Rebuilding.StateMachine.StructureStateType.Completed); break;
                        case "deadapt": best.StartDeadaptation(); break;
                        case "mark": best.SetReadyToDemolish(true); break;
                        case "unmark": best.SetReadyToDemolish(false); break;
                        case "demolish": best.StartDemolish(); break;
                    }
                    return "Haus " + best.ID + " " + what + " -> " + best.StateMachine?.CurrentState?.StateType;
                }

                case "citizens":
                {
                    // Host: neue Arbeiter am HQ ("citizens add 3"), Kinder ("citizens baby 2"), ersten Arbeiter krank/gesund ("citizens sick|cure"),
                    // ersten Arbeiter sterben lassen ("citizens kill")
                    var wc = GameAccess.Game<Gameplay.Units.Player.Workers.WorkersController>();
                    var cc = GameAccess.Game<Controllers.CharacterLogic.CitizensController>();
                    int num = a.Length > 2 ? int.Parse(a[2], CultureInfo.InvariantCulture) : 1;
                    Gameplay.Units.Characters.Character first = null;
                    foreach (var x in cc.Citizens)
                        if (x != null && x.Group == null && !x.IsChild && !x.IsDead() && (a[1] == "cure" ? x.IsSick : !x.IsSick)) { first = x; break; }
                    switch (a[1])
                    {
                        case "add": wc.CreateNewWorkers(num, hqPos + new Vector3(5f, 0f, 5f)); return num + " Arbeiter";
                        case "baby": cc.MakeBabies(num, newborns: true); return num + " Kinder";
                        case "sick":
                        {
                            if (first == null) return "kein Arbeiter";
                            GameAccess.Game<Controllers.Sickness.SicknessController>().MakeCharacterSick(first);
                            return "krank " + first.Id + " -> " + first.IsSick;
                        }
                        case "cure": if (first == null) return "niemand krank"; first.CureSickness(); return "gesund " + first.Id;
                        case "kill": if (first == null) return "kein Arbeiter"; first.Kill(Gameplay.Units.SubtractionHpReason.None); return "tot " + first.Id;
                    }
                    return "?";
                }

                case "swarm":
                {
                    // Host: Schwarm am Kartenrand erzeugen (wie der Spawner)
                    var sw = GameAccess.Game<Gameplay.Units.Spawning.SwarmSpawner>().SpawnSwarm();
                    return sw != null ? "Schwarm " + sw.Id + " Gruppen=" + sw.VirtualGroups.Count : "kein Schwarm";
                }

                case "vhorde":
                {
                    // Host: Horde außerhalb der Stadt ("vhorde inf_human 8 600") oder vorhandene bewegen ("vhorde move dx dz")
                    var vgc = GameAccess.Game<Gameplay.Units.Enemy.VirtualGroupsController>();
                    if (a.Length > 1 && a[1] == "move")
                    {
                        if (vgc.Groups.Count == 0) return "keine Horde";
                        var vg0 = vgc.Groups[vgc.Groups.Count - 1];
                        vg0.Movement.Teleport(vg0.transform.position + new Vector3(float.Parse(a[2], CultureInfo.InvariantCulture), 0f, float.Parse(a[3], CultureInfo.InvariantCulture)));
                        return "Horde " + vg0.ID + " bewegt";
                    }
                    string draft = a.Length > 1 ? a[1] : "inf_human";
                    int size = a.Length > 2 ? int.Parse(a[2], CultureInfo.InvariantCulture) : 8;
                    float dist = a.Length > 3 ? float.Parse(a[3], CultureInfo.InvariantCulture) : 600f;
                    var vg = vgc.Create(config.GetDraftByName(draft), size);
                    vg.Movement.Teleport(hqPos + new Vector3(dist, 0f, 0f));
                    return "Horde " + vg.ID + " n=" + vg.ICharacters.Count;
                }

                case "drag":
                {
                    // Wie Ziehen im Tauschfenster: "drag <ResourceID> <Menge> squad2stock|stock2squad|squad2trunk" (erster Trupp, nächstes Auto)
                    var sqc = GameAccess.Game<Gameplay.Units.SquadsController>();
                    if (sqc == null || sqc.Squads.Count == 0) return "keine Trupps";
                    var sq = sqc.Squads[0];
                    var rid = (Gameplay.GameResources.ResourceID)Enum.Parse(typeof(Gameplay.GameResources.ResourceID), a[1]);
                    float q = float.Parse(a[2], CultureInfo.InvariantCulture);
                    Controllers.Dragging.Strategies.IDragContainerHandler from = sq.ResourcesContainer, to = GameAccess.Game<Controllers.StockroomsController>().Container;
                    if (a[3] == "stock2squad") { var t = from; from = to; to = t; }
                    else if (a[3] == "squad2trunk")
                    {
                        Gameplay.Vehicles.Vehicle best = null; float bestD = float.MaxValue;
                        foreach (var v in GameAccess.Game<Controllers.VehiclesController>().Vehicles) { if (v == null) continue; float d = (v.Position - sq.Position).sqrMagnitude; if (d < bestD) { bestD = d; best = v; } }
                        if (best == null) return "kein Auto";
                        to = best.Trunk;
                    }
                    var dicts = new System.Collections.Generic.Dictionary<Gameplay.GameResources.ResourceID, float>[4];
                    for (int i = 0; i < 4; i++) dicts[i] = new System.Collections.Generic.Dictionary<Gameplay.GameResources.ResourceID, float>();
                    new Controllers.Dragging.Strategies.RegularDragStrategy(from, to, dicts[0], dicts[1], dicts[2], dicts[3], false, false, false).PlaceResource(rid, q);
                    return "gezogen " + rid + " " + q + " " + a[3];
                }

                case "workset":
                {
                    // Host: "workset <Nr> prio|max <Wert>" (Nr wie in den workitem-Zeilen)
                    var kv = Sync.WorkSync.ByIndex(int.Parse(a[1]));
                    if (kv.Value == null) return "keine Arbeit";
                    var bus = GameAccess.Game<Zenject.SignalBus>();
                    if (a[2] == "prio") bus.Fire(new Gameplay.Units.Player.Workers.WorkSystem.Signals.WorkPriorityChangedSignal(kv.Value, int.Parse(a[3]), false));
                    else kv.Value.MaxWorkers = int.Parse(a[3]);
                    return kv.Key + " p=" + kv.Value.Priority + " m=" + kv.Value.MaxWorkers + " n=" + kv.Value.Workers.Count;
                }

                case "wpanel":
                {
                    // Knopf im Fenster eines Auftrags drücken: "wpanel <Nr> <Methode> [Wichtigkeit]"
                    var kv = Sync.WorkSync.ByIndex(int.Parse(a[1]));
                    if (kv.Value == null) return "keine Arbeit";
                    var ui = System.Linq.Enumerable.FirstOrDefault(Resources.FindObjectsOfTypeAll<UI.InfoPanels.WorkersCountChanger>());
                    if (ui == null) return "kein Fenster";
                    ui.SetWork(kv.Value);
                    var m = HarmonyLib.AccessTools.Method(typeof(UI.InfoPanels.WorkersCountChanger), a[2]);
                    m.Invoke(ui, a.Length > 3 ? new object[] { int.Parse(a[3]), true } : new object[0]);
                    return a[2] + " auf " + kv.Key + " p=" + kv.Value.Priority + " m=" + kv.Value.MaxWorkers + " n=" + kv.Value.Workers.Count;
                }

                case "wlabel":
                {
                    // Knopf der Übersicht unten rechts: "wlabel <Berufsart> <Methode> [Wichtigkeit]"
                    var emp = (ScriptableObjectScripts.UI.Employment)Enum.Parse(typeof(ScriptableObjectScripts.UI.Employment), a[1]);
                    var panel = System.Linq.Enumerable.FirstOrDefault(Resources.FindObjectsOfTypeAll<global::WorkersPanelController>(), p => p._labels != null);
                    if (panel?._labels == null || !panel._labels.TryGetValue(emp, out var label)) return "keine Übersicht";
                    var m = HarmonyLib.AccessTools.Method(typeof(global::WorkersLabel), a[2]);
                    m.Invoke(label, a.Length > 3 ? new object[] { int.Parse(a[3]), true } : new object[0]);
                    return a[2] + " " + emp;
                }

                case "exptile":
                {
                    // Feld der Umgebungskarte zeigen: "exptile <Nr>" (Nr = begehbare, durchsuchbare Felder nach Abstand zum HQ)
                    var t = Sync.ExpeditionSync.QaTile(int.Parse(a[1], CultureInfo.InvariantCulture), hqPos);
                    return t == null ? "kein Feld" : Sync.ExpeditionSync.Describe(t);
                }

                case "expscav":
                {
                    // Host: "expscav <Nr> <Anteil 0..1>" Feld bis zu diesem Anteil durchsuchen (1 = fertig, Funde liegen im Feld)
                    var t = Sync.ExpeditionSync.QaTile(int.Parse(a[1], CultureInfo.InvariantCulture), hqPos);
                    if (t == null) return "kein Feld";
                    float p = Mathf.Clamp01(float.Parse(a[2], CultureInfo.InvariantCulture));
                    var w = t.InitializeScavengeWork();
                    float startLeft = w.TimeLeftToScavenge, target = w.TimeToScavenge * (1f - p);
                    // in Schritten, damit wie beim echten Durchsuchen unterwegs Kisten gefunden werden
                    for (int i = 1; i <= 10 && !w.IsDone; i++) w.SetLeftSpaceToScavenge(Mathf.Lerp(startLeft, target, i / 10f));
                    return Sync.ExpeditionSync.Describe(t);
                }

                case "expres":
                {
                    // Host: "expres <Nr> <ResourceID> <Menge>" Rohstoff ins Feld legen
                    var t = Sync.ExpeditionSync.QaTile(int.Parse(a[1], CultureInfo.InvariantCulture), hqPos);
                    if (t == null) return "kein Feld";
                    t.AddResourceWithIcon((Gameplay.GameResources.ResourceID)Enum.Parse(typeof(Gameplay.GameResources.ResourceID), a[2]), float.Parse(a[3], CultureInfo.InvariantCulture));
                    return Sync.ExpeditionSync.Describe(t);
                }

                case "explair":
                {
                    // Host: "explair <Nr> <Zahl>" Höhlen-Markierung setzen
                    var t = Sync.ExpeditionSync.QaTile(int.Parse(a[1], CultureInfo.InvariantCulture), hqPos);
                    if (t == null) return "kein Feld";
                    t.MarkedAsLairCount = int.Parse(a[2], CultureInfo.InvariantCulture);
                    return Sync.ExpeditionSync.Describe(t);
                }

                case "expenter":
                case "expleave":
                {
                    // Host: "expenter <Nr> [Trupp-Nr]" Trupp sofort ins Feld, "expleave [Trupp-Nr]" wieder heraus
                    bool enter = a[0] == "expenter";
                    int si = a.Length > (enter ? 2 : 1) ? int.Parse(a[enter ? 2 : 1], CultureInfo.InvariantCulture) : 0;
                    if (squads == null || squads.Squads.Count <= si) return "kein Trupp";
                    var sq = squads.Squads[si];
                    var t = enter ? Sync.ExpeditionSync.QaTile(int.Parse(a[1], CultureInfo.InvariantCulture), hqPos) : null;
                    if (enter && t == null) return "kein Feld";
                    foreach (var c in System.Linq.Enumerable.ToList(sq.Characters))
                    {
                        if (c == null) continue;
                        if (enter) c.EnteredEnterableImmediately(t);
                        else if (c.Enterable is global::Expedition.ExpeditionTilerTile) c.ExitEnterableImmediately();
                    }
                    return "Trupp " + sq.Id + (enter ? " in Feld " + Sync.ExpeditionSync.Key(t.Coords) : " draußen");
                }

                case "exptake":
                {
                    // Wie Ziehen im Tauschfenster Feld -> Trupp: "exptake <Nr> <ResourceID> <Menge> [Trupp-Nr] [back]" (back = Trupp -> Feld)
                    // Beim Gast geht das über ExchangeSync an den Host.
                    var t = Sync.ExpeditionSync.QaTile(int.Parse(a[1], CultureInfo.InvariantCulture), hqPos);
                    if (t == null) return "kein Feld";
                    int si = a.Length > 4 ? int.Parse(a[4], CultureInfo.InvariantCulture) : 0;
                    if (squads == null || squads.Squads.Count <= si) return "kein Trupp";
                    var sq = squads.Squads[si];
                    var rid = (Gameplay.GameResources.ResourceID)Enum.Parse(typeof(Gameplay.GameResources.ResourceID), a[2]);
                    float q = float.Parse(a[3], CultureInfo.InvariantCulture);
                    Controllers.Dragging.Strategies.IDragContainerHandler from = t.ResourcesContainer, to = sq.ResourcesContainer;
                    if (a.Length > 5 && a[5] == "back") { var x = from; from = to; to = x; }
                    var dicts = new System.Collections.Generic.Dictionary<Gameplay.GameResources.ResourceID, float>[4];
                    for (int i = 0; i < 4; i++) dicts[i] = new System.Collections.Generic.Dictionary<Gameplay.GameResources.ResourceID, float>();
                    new Controllers.Dragging.Strategies.RegularDragStrategy(from, to, dicts[0], dicts[1], dicts[2], dicts[3], false, false, false).PlaceResource(rid, q);
                    return "gezogen " + rid + " " + q + " Trupp " + sq.Id + " / " + Sync.ExpeditionSync.Describe(t);
                }

                case "trader":
                    // Händler (TraderSync): spawn [Fahrzeug] [go] | call | cd <GTS> | state <VendorState> | fill | open [frac]
                    //   | give|ungive|take|untake <ResourceID> <Menge> | accept | cancel | threaten
                    return Sync.TraderSync.QaCommand(a, hqPos);

                case "wpos":
                {
                    // Arbeiter-Figuren (WorkerPosSync): "wpos tp <dx> <dz>" ersten Arbeiter (Host) relativ zum HQ versetzen; "wpos" Zähler
                    if (a.Length > 3 && a[1] == "tp")
                        return Sync.WorkerPosSync.QaTeleport(hqPos, float.Parse(a[2], CultureInfo.InvariantCulture), float.Parse(a[3], CultureInfo.InvariantCulture));
                    return _session.WorkerPos.StatusLine();
                }

                case "area":
                    // "area gather [Radius] [Anzahl] [ResourceID]" | "area plant [Anzahl]" | "area del [Nr]" | "area list"
                    return Sync.AreaWorkSync.Qa(a);

                case "prod":
                case "shop":
                case "custom":
                case "saw":
                case "handy":
                    // Produktion/Werkstatt/Anpassen/Sägewerk/Reparaturtrupp (ProductionSync.Qa); beim Gast als Wunsch an den Host
                    return _session.Production.Qa(a);

                case "side":
                case "immig":
                case "cue":
                case "storycam":
                    // StorySync: Seitenwechsel, Einwanderer, Story-Kamera/Ton (Hilfe in StorySync.Qa)
                    return _session.Story.Qa(a);

                case "struct":
                    // Gebaute Anlagen (StructureSync): "struct drafts", "struct place tower|wall|gate|farm|minefield|<Bauplan> [Abstand]"
                    // (Gast: als Wunsch), "struct done|ruin|deconstruct|canceldeconstruct|repair|cancelrepair|cancel|removeruin [id|last|near]"
                    return _session.Structures.Qa(a, hqPos);

                case "steamhost":
                    // Über Steam hosten wie der Knopf im F6-Fenster
                    _session.StartHost();
                    return "Rolle " + _session.Role + " Lobby " + _session.Lobby.m_SteamID + " " + _session.Status;

                case "invite":
                {
                    // Steam-Freund (Name enthält den Text, '_' = Leerzeichen) in die Lobby einladen: "invite hello_kitty"
                    if (_session.Lobby == Steamworks.CSteamID.Nil) return "keine Lobby";
                    string want = string.Join(" ", System.Linq.Enumerable.Skip(a, 1)).Replace('_', ' ').ToLowerInvariant();
                    int n = Steamworks.SteamFriends.GetFriendCount(Steamworks.EFriendFlags.k_EFriendFlagImmediate);
                    for (int i = 0; i < n; i++)
                    {
                        var id = Steamworks.SteamFriends.GetFriendByIndex(i, Steamworks.EFriendFlags.k_EFriendFlagImmediate);
                        string name = Steamworks.SteamFriends.GetFriendPersonaName(id) ?? "";
                        if (name.ToLowerInvariant().Contains(want))
                            return "eingeladen " + name + " -> " + Steamworks.SteamMatchmaking.InviteUserToLobby(_session.Lobby, id);
                    }
                    return "kein Freund mit " + want + " (" + n + " Freunde)";
                }

                case "alarm":
                {
                    // Alarm wie aus dem Funk: "alarm on|off" (beim Gast als Wunsch an den Host)
                    var wc = GameAccess.Game<Gameplay.Units.Workers.WorkSystem.WorkController>();
                    bool r = a[1] == "on" ? wc.StartAlarm() : wc.StopAlarm();
                    return "Alarm " + a[1] + " -> " + r + " jetzt=" + wc.IsAlarm;
                }

                case "hostdirect":
                    // Direkt-Host auch im Hauptmenü: "hostdirect <Port>"
                    _session.StartHostDirect(a.Length > 1 ? int.Parse(a[1], CultureInfo.InvariantCulture) : 27120);
                    return _session.Status;

                case "newgame":
                    // Neues Spiel aus dem Hauptmenü: "newgame <x> <y>"
                    Commands.MenuConsoleCommandsHandler.LoadTileWithConfiguration(int.Parse(a[1], CultureInfo.InvariantCulture), int.Parse(a[2], CultureInfo.InvariantCulture), 15, 1, 9, Vector2Int.zero, fromSuggestedCitiesList: false);
                    return "lade Kachel " + a[1] + "," + a[2];

                case "loadsave":
                    // Spielstand laden wie über „Spiel laden“: "loadsave <Ordnername>"
                    _load = a[1];
                    _loadDone = true; // nicht noch einmal über den Startparameter-Weg laden
                    TryLoad();
                    return "lade " + a[1];

                case "hqsuggest":
                {
                    // Gast: Haus als HQ vorschlagen, ohne Zahl = das vom Spiel vorgeschlagene HQ
                    long id = 0;
                    if (a.Length > 1) id = long.Parse(a[1], CultureInfo.InvariantCulture);
                    else
                    {
                        var setter = GameAccess.Game<Controllers.HqSetterController>();
                        var b = setter != null ? Traverse.Create(setter).Field("_proposedHq").GetValue<Gameplay.Buildings.Building>() : null;
                        if (b != null) id = b.ID;
                    }
                    return Sync.HqSuggest.QaSuggest(id);
                }

                case "mnew":
                    // Neues-Spiel-Ablauf im Hauptmenü durchklicken: "mnew globe | select <x> <y> | custom | diff <0-4> | people <x> | close"
                    return QaNewGame(a);

                case "msettings":
                    // Mehrspieler-Einstellungen: "msettings open|close|set <Zeile> <Nr>"
                    return Menu.MenuSettingsWindow.Qa(a);

                case "menutile":
                    // Kachel im Hauptmenü anklicken: "menutile <0-7>"
                    return Menu.MenuLobbyUi.QaClick(int.Parse(a[1], CultureInfo.InvariantCulture));

                case "uidump":
                    // Menü vermessen: "uidump [Canvas-Filter|*] [Tiefe]"
                    return Menu.UiDump.Run(a.Length > 1 ? a[1] : "*", a.Length > 2 ? int.Parse(a[2], CultureInfo.InvariantCulture) : 12);

                case "guestcheats":
                    // Host: Entwickler-Menü für Gäste an/aus (wie der Haken im F6-Fenster)
                    Plugin.GuestCheats.Value = a.Length > 1 && a[1] == "1";
                    _session.BroadcastSettings();
                    return "Gäste dürfen F7: " + Plugin.GuestCheats.Value;

                case "bugreport":
                    // Fehlerbericht wie F8 ohne Fenster: "bugreport <Text>"
                    return BugReport.QaReport(string.Join(" ", a, 1, a.Length - 1));

                case "bdirty":
                    // Behälter-Abgleich prüfen: beim Gast ein Haus füllen, das beim Host leer ist (Host muss es per Häuserliste leeren)
                    return Sync.ContainerSync.QaDirty();

                case "cheat":
                    return CheatWindow.Instance == null ? "kein Menü" : CheatWindow.Instance.Qa(a);

                case "sa":
                    // Trupp-Aktionen (SquadActionSync): "sa leave|repair|pour|fuelin|fuelout|lock 0/1|vlock 0/1|ammo|abrepair|cancelrepair|split|place|swap|endx|xp <Art> [n]|skill <Id>|discard <Res> <Menge>|discardw"
                    return Sync.SquadActionSync.Qa(a);

                case "zone":
                    // ZoneSync: "zone status|make <art>|reveal|take|hostile|stat <inf> <raid> <cit> <gath> <found>|stats|migrate|convoy|leave [umzug]|load <ordner>"
                    return _session.Zones.Qa(a);

                case "bld2":
                    // Häuser Teil 2 (BuildingSync.Qa): "bld2 damage <HP> [hq|id]", "bld2 heal", "bld2 panel [resume] [id]",
                    // "bld2 explosives|claim|deconstruct|split [id]", "bld2 create [Größe]", "bld2 status"; beim Gast als Wunsch an den Host
                    return _session.Buildings.Qa(a, hqPos);

                case "bres":
                    // Baustellen-Rohstoffe (BuildResSync): "bres status|step [n] [Schlüssel]|deliver [n] [Schlüssel]|panel [Schlüssel]|ui"
                    return _session.BuildRes.Qa(a, hqPos);

                case "sv":
                    // Trupp-Anzeige (SquadViewSync): "sv select <id|first|0>", "sv chain <n> [id]", "sv move <dx> <dz> [id]"
                    return _session.SquadViews.Qa(a);

                case "evmark":
                    // Ereignis-Marker (markers): "evmark house|off" (Host), "evmark local|kill|hide|arrive|lose" (Gast)
                    return _session.Events.Qa(a, hqPos);

                case "outline":
                    // Farbplatz fuer das Haus des Mitspielers: "outline 0" (blau) .. "outline 4"
                    if (a.Length > 1) Sync.PresenceOutline.Slot = int.Parse(a[1], CultureInfo.InvariantCulture);
                    return Sync.PresenceOutline.StatusLine();

                case "rstat":
                    // Rohstoffleiste (ResStatsSync): "rstat status|tip|prod <Res> <n>|scav <Res> <n>|use <Res> <n>"
                    return _session.ResStats.Qa(a);

                case "statswin":
                    // Statistik-Fenster wie der Knopf im Esc-Menü
                    Menu.StatsWindow.Toggle();
                    return "Statistik offen=" + Menu.StatsWindow.Visible;

                case "colorpick":
                {
                    // Farbrad: "colorpick open" | "colorpick close" | "colorpick <hex>" (wie Übernehmen)
                    if (a.Length > 1 && a[1] == "open") { Menu.ColorPickerWindow.Open(); return "Farbrad offen=" + Menu.ColorPickerWindow.Visible; }
                    if (a.Length > 1 && a[1] == "close") { Menu.ColorPickerWindow.Close(); return "Farbrad offen=" + Menu.ColorPickerWindow.Visible; }
                    if (a.Length < 2) return "colorpick open|close|<hex>";
                    bool okc = Menu.ColorPickerWindow.QaApply(a[1], out var whyc);
                    return "Farbe " + a[1] + " -> " + (okc ? "übernommen" : "abgelehnt: " + whyc) + " Konfig=" + Plugin.MyColorSetting.Value;
                }

                case "squadtrain":
                {
                    // Trupp-Ausbildung im Haupt-HQ: "squadtrain" (neuer Auftrag, beim Gast an den Host) | "squadtrain cancel" (letzten löschen)
                    var ctrl = GameAccess.Game<Gameplay.Units.SquadCreatorController>();
                    var trainHq = Controllers.HqController.MainHeadquarter;
                    if (ctrl == null || trainHq == null) return "kein HQ";
                    if (a.Length > 1 && a[1] == "cancel")
                    {
                        var m = Sync.SquadQueueSync.Module(trainHq);
                        if (m == null || m.Requests.Count == 0) return "keine Ausbildung";
                        ctrl.HandleRequestDestroyed(m.Requests[m.Requests.Count - 1], trainHq);
                        return _session.SquadQueue.StatusLine();
                    }
                    ctrl.AddCreationRequestAtStructure(new System.Collections.Generic.List<Gameplay.Units.Skills.StaticSkillInfo>(),
                        Gameplay.GameResources.ResourceID.eq_basic_melee_weapon, Gameplay.GameResources.ResourceID.eq_wear_default, trainHq, false);
                    return _session.SquadQueue.StatusLine();
                }

                case "pcolor":
                    // eigene Spielerfarbe (PlayerColorSync): "pcolor <Hex>" oder "pcolor auto"
                    return _session.PlayerColors.Qa(a);

                case "camxor":
                case "escprobe":
                case "esc":
                    // Sperren-Proben (UiLocks): camxor = XOR-Regression der Kamera, escprobe = Host-Esc "Hauptmenue"-Fenster
                    return UiLocks.Qa(a);

                case "outtrans":
                    // eigener ausgehender Funk wie ueber das Funkgeraet-Menue: "outtrans outtrans_keepaway"
                    return _session.Radio.QaOutgoing(a.Length > 1 ? a[1] : "outtrans_keepaway");

                case "camrescue":
                    return CameraRescue.Run();

                case "vs":
                    // Versus: "vs status|host|steamhost|join|joinlobby|settings|team|start|hq|force|pause|speed|leave"
                    return Versus.VsQa.Execute(a);

                case "stance":
                {
                    // Haltung des ersten Trupps: "stance HoldYourFire 1"; Name: "stance name Neuer_Name"
                    var sq = GameAccess.Game<Gameplay.Units.SquadsController>().Squads[0];
                    if (a[1] == "name") { sq.SetGroupName(a[2].Replace('_', ' ')); return "Name " + sq.GroupName; }
                    var st = (Gameplay.Units.GroupStanceId)Enum.Parse(typeof(Gameplay.Units.GroupStanceId), a[1]);
                    sq.StancesHandler.SetStateToStance(st, a[2] == "1");
                    return "Haltung " + st + "=" + sq.StancesHandler.IsActive(st);
                }

                case "house":
                {
                    // Host: erster Trupp sofort ins nächste Haus ("house in") oder heraus ("house out")
                    var sq = GameAccess.Game<Gameplay.Units.SquadsController>().Squads[0];
                    if (a[1] == "out") { foreach (var c in sq.Characters) c.ExitEnterableImmediately(); return "draußen"; }
                    Gameplay.Buildings.Building best = null; float bestD = float.MaxValue;
                    foreach (var b in GameAccess.Game<Controllers.BuildingsController>().Buildings)
                    { if (b == null || b.IsHq) continue; float d = (b.transform.position - sq.Position).sqrMagnitude; if (d < bestD) { bestD = d; best = b; } }
                    foreach (var c in sq.Characters) c.EnteredEnterableImmediately(best);
                    return "in Haus " + best.ID;
                }

                case "radio":
                    return _session.Radio.QaStart(a.Length > 1 ? a[1] : "call_guffin_noise");

                case "answer":
                    return _session.Radio.QaAnswer(a.Length > 1 ? int.Parse(a[1], CultureInfo.InvariantCulture) : 0);

                case "scavenge":
                {
                    // nächstes noch nicht durchsuchtes Haus beim HQ sofort durchsuchen (Host)
                    var bc = GameAccess.Game<Controllers.BuildingsController>();
                    Gameplay.Buildings.Building best = null;
                    float bestD = float.MaxValue;
                    foreach (var b in bc.Buildings)
                    {
                        if (b == null || b.IsScavenged() || b.ScavengeWork != null || !b.IsScavengable() || b.IsHq)
                            continue;
                        float d = (b.transform.position - hqPos).sqrMagnitude;
                        if (d < bestD) { bestD = d; best = b; }
                    }
                    if (best == null)
                        return "kein Haus";
                    best.InitializeAndEndScavengeWork();
                    return "Haus " + best.ID + " durchsucht";
                }

                case "select":
                {
                    // so tun, als wäre ein Haus angeklickt: "select <id>", "select near" (nächstes beim HQ), "select 0" (aus)
                    if (a.Length > 1 && a[1] == "near")
                    {
                        var bc = GameAccess.Game<Controllers.BuildingsController>();
                        Gameplay.Buildings.Building best = null;
                        float bestD = float.MaxValue;
                        foreach (var b in bc.Buildings)
                        {
                            if (b == null || b.IsHq)
                                continue;
                            float d = (b.transform.position - hqPos).sqrMagnitude;
                            if (d < bestD) { bestD = d; best = b; }
                        }
                        Sync.PresenceSync.QaSelectedBuilding = best != null ? best.ID : 0;
                    }
                    else
                        Sync.PresenceSync.QaSelectedBuilding = a.Length > 1 ? long.Parse(a[1], CultureInfo.InvariantCulture) : 0;
                    return "Haus " + Sync.PresenceSync.QaSelectedBuilding;
                }
            }
            return "unbekannt";
        }

        // Neues Spiel: Die Uhr ist gesperrt, bis ein Hauptquartier gewählt ist. "hq" wählt das vorgeschlagene
        // Gebäude (sonst das passende nächst der Kartenmitte) und bestätigt es wie der Klick im Auswahlfenster.
        private static string ChooseHq()
        {
            var setter = UnityEngine.Object.FindObjectOfType<Controllers.HqSetterController>();
            if (setter == null)
                return "keine Auswahl offen";
            if (!setter.IsSelecting)
            {
                // Bestätigt, aber die Kamerafahrt zum HQ (sie hebt die Zeitsperre auf) ist nicht fertig geworden.
                if (GameAccess.SpeedSetter == null || !GameAccess.SpeedSetter.IsGameplayTimeChangingLocked)
                    return "keine Auswahl offen";
                var antiFog = Traverse.Create(setter).Field("_startAntiFog").GetValue<GameObject>();
                if (antiFog != null)
                    UnityEngine.Object.Destroy(antiFog);
                GameAccess.SpeedSetter.UnblockTimeChanging();
                return "Zeitsperre aufgehoben";
            }
            var building = Traverse.Create(setter).Field("_proposedHq").GetValue<Gameplay.Buildings.Building>();
            if (building == null || building.FirstHqPossibility != Gameplay.Buildings.FirstHqPossibility.CouldBe)
            {
                var center = Traverse.Create(setter).Field("_centerTerrainPosition").GetValue<Vector3>();
                building = null;
                float best = float.MaxValue;
                foreach (var b in GameAccess.Game<Controllers.BuildingsController>().Buildings)
                {
                    if (b == null || b.FirstHqPossibility != Gameplay.Buildings.FirstHqPossibility.CouldBe)
                        continue;
                    float d = (b.transform.position - center).sqrMagnitude;
                    if (d < best)
                    {
                        best = d;
                        building = b;
                    }
                }
            }
            if (building == null)
                return "kein passendes Gebäude";
            var config = Traverse.Create(setter).Field("_firstHqConfig").GetValue<Core.Configs.FirstHqConfig>();
            Traverse.Create(setter).Method("ShowSelectionPopup", building, config.AdaptValueFor(building)).GetValue();
            Traverse.Create(setter).Method("OnSelectionConfirmed").GetValue();
            var p = building.transform.position;
            return "Hauptquartier " + building.Id + " bei " + p.x.ToString("0", CultureInfo.InvariantCulture) + "," + p.z.ToString("0", CultureInfo.InvariantCulture);
        }

        private void TryLoad()
        {
            try
            {
                string dir = SaveHandler.GetSaveFolderPath(_load);
                var info = SaveHandler.LoadSaveInfoByDirPath(dir);
                var validator = GameAccess.SaveValidator;
                if (info == null || validator == null)
                {
                    Plugin.Log.LogError("IFZCOOP QA Laden: Spielstand oder SaveValidator fehlt: " + dir);
                    return;
                }
                Plugin.Log.LogInfo("IFZCOOP QA lade " + dir);
                validator.TryRun(dir, info);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("IFZCOOP QA Laden: " + e);
            }
        }

        private void WriteStatus()
        {
            try
            {
                var s = _session;
                var sb = new StringBuilder();
                var inv = CultureInfo.InvariantCulture;
                sb.AppendLine("time=" + DateTime.Now.ToString("HH:mm:ss", inv));
                sb.AppendLine("version=" + Plugin.Version);
                sb.AppendLine("role=" + s.Role);
                sb.AppendLine("guestState=" + s.GuestState);
                sb.AppendLine("direct=" + s.Direct);
                sb.AppendLine("status=" + s.Status);
                sb.AppendLine("gameStarted=" + GameAccess.IsGameStarted);
                sb.AppendLine("day=" + TimeController.Day);
                sb.AppendLine("hour=" + TimeController.Hour.ToString("0.000", inv));
                sb.AppendLine("speed=" + TimeController.SpeedMultiplier.ToString("0.##", inv));
                var setter = GameAccess.SpeedSetter;
                if (setter != null)
                    sb.AppendLine($"timeLocked={setter.IsGameplayTimeChangingLocked} paused={setter.PauseController.IsGameplayPaused}"
                        + $" popupPaused={setter.PauseController.IsPopupPaused} uiPaused={setter.PauseController.IsUIPaused}");
                var mainHq = Controllers.HqController.MainHeadquarter;
                sb.AppendLine("hq=" + (mainHq != null ? mainHq.GetId() + " " + mainHq.transform.position.x.ToString("0", inv) + "," + mainHq.transform.position.z.ToString("0", inv) : "keins"));
                sb.AppendLine("activeGuests=" + s.ActiveGuestCount);
                sb.AppendLine("drift=" + s.TimeDriftSeconds.ToString("0.00", inv));
                sb.AppendLine("timeCorrections=" + s.TimeCorrections);
                sb.AppendLine("rttMs=" + s.HostRttMs.ToString("0", inv));
                sb.AppendLine("snapshots=" + s.Units.SnapshotsReceived);
                sb.AppendLine("spawns=" + s.Units.SpawnsApplied);
                sb.AppendLine("deaths=" + s.Units.DeathsApplied);
                sb.AppendLine("shots=" + s.Combat.ShotsPlayed);
                sb.AppendLine("projectiles=" + s.Combat.ProjectilesPlayed);
                sb.AppendLine("ordersSent=" + s.Orders.Sent);
                sb.AppendLine("ordersReceived=" + s.Orders.Received);
                var squads = GameAccess.Game<Gameplay.Units.SquadsController>()?.Squads;
                var groups = GameAccess.Game<Gameplay.Units.Enemy.GroupsController>()?.Groups;
                sb.AppendLine("squads=" + (squads?.Count ?? -1));
                sb.AppendLine("groups=" + (groups?.Count ?? -1));
                sb.AppendLine("notice=" + s.Notice);
                sb.AppendLine(s.Radio.StatusLine());
                sb.AppendLine(Sync.StockSync.StatusLine() + " stockApplied=" + s.Stock.Applied);
                sb.AppendLine(Sync.GroundSync.StatusLine() + " groundApplied=" + s.Ground.Applied);
                foreach (var line in Sync.VehicleSync.StatusLines())
                    sb.AppendLine(line);
                foreach (var line in Sync.EventSync.StatusLines())
                    sb.AppendLine(line);
                foreach (var line in Sync.WorldSync.StatusLines())
                    sb.AppendLine(line);
                foreach (var line in Sync.VirtualSync.StatusLines())
                    sb.AppendLine(line);
                sb.AppendLine($"virtualApplied created={s.Virtuals.Created} removed={s.Virtuals.Removed} swarmsCreated={s.Virtuals.SwarmsCreated} swarmsRemoved={s.Virtuals.SwarmsRemoved}");
                sb.AppendLine(s.Checksums.StatusLine());
                sb.AppendLine(s.Exchange.StatusLine());
                foreach (var line in Sync.ExpeditionSync.StatusLines())
                    sb.AppendLine(line);
                sb.AppendLine(s.Expeditions.StatusLine());
                foreach (var line in Sync.TraderSync.StatusLines())
                    sb.AppendLine(line);
                sb.AppendLine(s.Traders.StatusLine());
                foreach (var line in Sync.WorkerPosSync.StatusLines())
                    sb.AppendLine(line);
                sb.AppendLine(s.WorkerPos.StatusLine());
                foreach (var line in Sync.AreaWorkSync.StatusLines())
                    sb.AppendLine(line);
                sb.AppendLine(s.AreaWorks.StatusLine());
                foreach (var line in Sync.ProductionSync.StatusLines())
                    sb.AppendLine(line);
                sb.AppendLine(s.Production.StatusLine());
                foreach (var line in Sync.StorySync.StatusLines())
                    sb.AppendLine(line);
                sb.AppendLine(s.Story.StatusLine());
                foreach (var line in Sync.StructureSync.StatusLines())
                    sb.AppendLine(line);
                sb.AppendLine(s.Structures.StatusLine());
                foreach (var line in Sync.SquadActionSync.StatusLines())
                    sb.AppendLine(line);
                sb.AppendLine(s.SquadActions.StatusLine());
                foreach (var line in Sync.ZoneSync.StatusLines())
                    sb.AppendLine(line);
                sb.AppendLine(s.Zones.StatusLine());
                foreach (var line in Sync.BuildingSync.ExtraStatusLines())
                    sb.AppendLine(line);
                sb.AppendLine(s.Buildings.StatusLine2());
                foreach (var line in Sync.BuildResSync.StatusLines())
                    sb.AppendLine(line);
                sb.AppendLine(s.BuildRes.StatusLine());
                foreach (var line in s.SquadViews.StatusLines())
                    sb.AppendLine(line);
                sb.AppendLine(s.Events.GuardLine());
                sb.AppendLine(Sync.PresenceOutline.StatusLine());
                foreach (var line in Sync.ResStatsSync.StatusLines())
                    sb.AppendLine(line);
                sb.AppendLine(s.ResStats.StatusLine());
                sb.AppendLine(s.SquadQueue.StatusLine());
                sb.AppendLine(s.Demolish.StatusLine());
                foreach (var line in s.Stats.StatusLines())
                    sb.AppendLine(line);
                sb.AppendLine("cheatflags " + CheatWindow.FlagsLine());
                foreach (var line in Sync.DemolishSync.StatusLines())
                    sb.AppendLine(line);
                foreach (var line in s.PlayerColors.StatusLines())
                    sb.AppendLine(line);
                foreach (var line in UiLocks.StatusLines())
                    sb.AppendLine(line);
                sb.AppendLine($"uidiag camStuckLogged={CameraRescue.CamStuckLogged} rescues={CameraRescue.Count} radioFixed={CameraRescue.StuckRadioFixed} popupFixed={CameraRescue.StuckPopupFixed}");
                foreach (var line in Versus.VsQa.StatusLines())
                    sb.AppendLine(line);
                foreach (var line in Sync.WorkSync.StatusLines())
                    sb.AppendLine(line);
                sb.AppendLine(s.Works.StatusLine());
                foreach (var line in Sync.SquadStateSync.StatusLines())
                    sb.AppendLine(line);
                sb.AppendLine($"squadStateApplied={s.SquadStates.Applied} enteredApplied={s.SquadStates.EnteredApplied} requestsSent={s.SquadStates.RequestsSent}");
                sb.AppendLine(Sync.PopulationSync.StatusLine() + $" created={s.Population.Created} removed={s.Population.Removed} sickChanged={s.Population.SickChanged}");
                sb.AppendLine(Sync.BuildingSync.StatusLine() + $" buildingsApplied={s.Buildings.Applied} requestsSent={s.Buildings.RequestsSent} requestsDone={s.Buildings.RequestsDone}");
                foreach (var line in Sync.BuildingSync.DetailLines())
                    sb.AppendLine(line);
                sb.AppendLine($"worldApplied research={s.World.ResearchApplied} laws={s.World.LawsApplied} mood={s.World.MoodApplied} weather={s.World.WeatherApplied} requestsSent={s.World.RequestsSent} requestsDone={s.World.RequestsDone}");
                sb.AppendLine($"eventsApplied={s.Events.Applied} missionsStarted={s.Events.MissionsStarted} missionsEnded={s.Events.MissionsEnded} objectivesDone={s.Events.ObjectivesDone} markersSpawned={s.Events.MarkersSpawned} markersRemoved={s.Events.MarkersRemoved} contentChanged={s.Events.ContentChanged}");
                sb.AppendLine($"vehiclesApplied={s.Vehicles.Applied} created={s.Vehicles.Created} adopted={s.Vehicles.Adopted} removed={s.Vehicles.Removed} entered={s.Vehicles.Entered} exited={s.Vehicles.Exited}");
                sb.AppendLine(Sync.ContainerSync.BuildingsStatus() + $" containersApplied={s.Containers.ContainersApplied} itemsApplied={s.Containers.ItemsApplied} housesCleared={s.Containers.BuildingsCleared}");
                sb.AppendLine($"cheat guestAllowed={(CheatWindow.GuestAllowedByHost ? 1 : 0)} hostAllows={(Plugin.GuestCheats.Value ? 1 : 0)} guestRequests={CheatWindow.Instance?.GuestRequests ?? 0}");
                sb.AppendLine(Menu.MenuLobbyUi.StatusLine());
                sb.AppendLine(Menu.MenuSettingsWindow.StatusLine());
                sb.AppendLine(Menu.LobbyInfo.StatusLine());
                sb.AppendLine(Sync.HqSuggest.StatusLine());
                sb.AppendLine($"bugreport saved={BugReport.Saved} received={BugReport.Received} last={BugReport.LastPath.Replace(' ', '_')}");
                foreach (var line in Sync.ContainerSync.StatusLines())
                    sb.AppendLine(line);
                Sync.ScavengeSync.Count(out int scavDone, out int scavStarted);
                sb.AppendLine($"scavenged={scavDone} scavengeStarted={scavStarted} scavengeApplied={s.Scavenge.Applied}");
                int revealed = 0;
                var hideoutList = GameAccess.Game<Gameplay.Units.Enemy.Hideouts.HideoutsController>()?.Hideouts;
                if (hideoutList != null)
                    foreach (var h in hideoutList) if (h != null && h.IsRevealed) revealed++;
                sb.AppendLine($"hideoutsRevealed={revealed} hideoutsApplied={s.Hideouts.Applied}");
                foreach (var remote in s.Presence.Remotes)
                    sb.AppendLine($"remote {remote.Name} squads={string.Join(",", remote.Squads)} building={remote.Building}");
                var virtuals = GameAccess.Game<Gameplay.Units.Enemy.VirtualGroupsController>()?.Groups;
                if (virtuals != null)
                {
                    int vis = 0;
                    foreach (var v in virtuals) if (v != null && v.IsVisible()) vis++;
                    sb.AppendLine($"virtualGroups={virtuals.Count} visible={vis}");
                }
                var swarms = UnityEngine.Object.FindObjectsOfType<Gameplay.Units.Swarms.Swarm>();
                int swarmVis = 0;
                foreach (var sw in swarms) if (Traverse.Create(sw).Field("_isVisible").GetValue<bool>()) swarmVis++;
                sb.AppendLine($"swarms={swarms.Length} visible={swarmVis}");
                if (squads != null)
                    foreach (var g in squads)
                        if (g != null && g.Characters.Count > 0)
                            sb.AppendLine($"squad {g.Id} n={g.Characters.Count} pos={g.Position.x.ToString("0.0", inv)},{g.Position.z.ToString("0.0", inv)}");
                if (groups != null)
                    foreach (var g in groups)
                        if (g != null && g.Characters.Count > 0)
                            sb.AppendLine($"group {g.Id} {g.Fraction} n={g.Characters.Count} pos={g.Position.x.ToString("0.0", inv)},{g.Position.z.ToString("0.0", inv)} vis={(g.IsVisible() ? 1 : 0)}");
                File.WriteAllText(_statusTarget ?? _statusPath, sb.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("Statusdatei: " + e.Message);
            }
        }
    }
}
