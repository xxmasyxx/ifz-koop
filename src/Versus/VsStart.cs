using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Controllers;
using Controllers.CharacterLogic;
using Gameplay.Buildings;
using Gameplay.GameResources;
using Gameplay.Units;
using Gameplay.Units.Player.Workers;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using UnityEngine;

namespace IfzCoop.Versus
{
    /// <summary>
    /// V1 gemeinsamer Start (VERSUS-TECHNIK 2.1, 2.2, 1.7).
    ///
    /// - Heimliche HQ-Wahl: Der Bestätigen-Knopf der HQ-Wahl (HqSetterController.OnSelectionConfirmed, über einen
    ///   PopupButtonSettings-Delegate aufgerufen, also patchbar) wird abgefangen.
    ///   Gast: meldet das Gebäude dem Host (VsHqChosen). Der Host prüft nur den Abstand zu den anderen HQs
    ///   (mindestens MinHqDistance) und antwortet (VsHqAnswer). Erst dann läuft die Bestätigung beim Gast.
    ///   Host: bestätigt als Letzter. Sein Klick wartet, bis alle Gäste gewählt haben (Zeitlimit 3 min oder „Ohne sie starten“).
    ///   Erst mit seiner Wahl füllt das Spiel beim Host die Welt (FirstHqAdaptedSignalAsync); Verstecke meiden dann auch
    ///   die Gast-HQs (Postfix auf HideoutSpawnPointSpawner.IsToCloseHq), und Welt-Gruppen im Umkreis R_safe um die
    ///   Gast-HQs werden entfernt (Schonzone wie am eigenen HQ).
    /// - ID-Bereiche: Gast auf Platz n versetzt IdProvider._lastID um n × 100 000 000 (Muster Session.OffsetGuestIds).
    /// - Startbedingungen: jeder Rechner nach seiner eigenen HQ-Wahl, mit denselben Spiel-Funktionen wie das
    ///   Entwickler-Menü (WorkersController.CreateNewWorkers, Character.Kill, GroupBuilder.SpawnGroupAt,
    ///   StockroomsController.Container.ForceAddResource).
    /// </summary>
    internal sealed class VsStart
    {
        public const float MinHqDistance = 150f;
        public const float SafeRadius = 150f;
        private const float HostWaitLimit = 180f;

        private static readonly System.Reflection.FieldInfo SelectedInfo = AccessTools.Field(typeof(HqSetterController), "_selectedBuilding");

        /// <summary>Gebäude, das gerade im HQ-Auswahlfenster steht (privates Feld des Spiels).</summary>
        private static Building SelectedField(HqSetterController setter)
        {
            return setter == null || SelectedInfo == null ? null : SelectedInfo.GetValue(setter) as Building;
        }

        private readonly VersusSession _vs;

        /// <summary>true, solange der Mod die HQ-Bestätigung selbst auslöst (Prefix lässt sie dann durch).</summary>
        public bool AllowConfirm;

        public bool MyHqConfirmed { get; private set; }
        public long MyHqId { get; private set; }
        public Vector3 MyHqPos { get; private set; }

        // Gast: Wahl gemeldet, Antwort steht aus
        public bool AwaitingAnswer { get; private set; }
        private long _requestedHq;
        private float _requestedAt;
        public int Rejections { get; private set; }

        // Host: eigene Wahl wartet auf die Gäste
        public bool HostPending { get; private set; }
        private long _hostPendingId;
        private Vector3 _hostPendingPos;
        private float _hostPendingSince;
        public bool ForceStart;

        // Startbedingungen
        private float _hqSetAt = -1f;
        private int _lastWorkers = -1;
        private float _workersStableSince;
        public bool Applied { get; private set; }
        public string AppliedSummary { get; private set; } = "-";

        // Schonzone (Host)
        private float _safeZone1 = -1f, _safeZone2 = -1f;
        public int SafeZoneRemoved { get; private set; }
        public int HideoutsMoved { get; internal set; }

        public uint IdBase { get; private set; }

        public VsStart(VersusSession vs)
        {
            _vs = vs;
        }

        public void Reset()
        {
            AllowConfirm = false;
            MyHqConfirmed = false;
            MyHqId = 0;
            MyHqPos = Vector3.zero;
            AwaitingAnswer = false;
            _requestedHq = 0;
            HostPending = false;
            ForceStart = false;
            _hqSetAt = -1f;
            _lastWorkers = -1;
            Applied = false;
            AppliedSummary = "-";
            _safeZone1 = _safeZone2 = -1f;
            SafeZoneRemoved = 0;
            HideoutsMoved = 0;
            Rejections = 0;
        }

        private static HqSetterController Setter => GameAccess.Game<HqSetterController>();

        // ================================================================ Haken

        /// <summary>Prefix von HqSetterController.OnSelectionConfirmed. false = Bestätigung (noch) nicht ausführen.</summary>
        public bool ConfirmPrefix(HqSetterController setter)
        {
            if (AllowConfirm || setter == null)
                return true;
            Building building;
            try
            {
                building = SelectedField(setter);
            }
            catch (Exception)
            {
                return true;
            }
            if (building == null || MyHqConfirmed)
                return true;

            if (_vs.Role == VsRole.Guest)
            {
                // Nur im Versus-Spielstand (ein eigenes Spiel in der Lobby-Wartezeit läuft normal).
                if (_vs.GuestState != VsGuestState.InGame)
                    return true;
                if (AwaitingAnswer)
                    return false;
                AwaitingAnswer = true;
                _requestedHq = building.ID;
                _requestedAt = Time.realtimeSinceStartup;
                var pos = building.Position;
                _vs.SendToHost(VsNet.Build(Msg.VsHqChosen, w => { w.Write(building.ID); w.Write(pos.x); w.Write(pos.z); }));
                _vs.Notice("HQ-Wahl an den Host gemeldet, warte auf Bestätigung ...");
                Plugin.Log.LogInfo($"IFZVS guest HQ request {building.ID}");
                return false;
            }

            if (_vs.Role == VsRole.Host)
            {
                if (_vs.Phase == VsPhase.Lobby || _vs.Phase == VsPhase.Transfer)
                {
                    _vs.Notice(_vs.Phase == VsPhase.Lobby
                        ? "Versus: Erst „Start“ drücken (Spielstand an die Gäste), dann das HQ wählen"
                        : "Versus: Warte, bis alle Gäste den Spielstand geladen haben");
                    Deny(setter);
                    return false;
                }
                if (TooClose(building.Position, 0, out string who))
                {
                    _vs.Notice($"Zu nah am Hauptquartier von {who} (mindestens {MinHqDistance:0} m), bitte ein anderes Gebäude");
                    Deny(setter);
                    return false;
                }
                if (!AllGuestsChosen && !ForceStart)
                {
                    HostPending = true;
                    _hostPendingId = building.ID;
                    _hostPendingPos = building.Position;
                    _hostPendingSince = Time.realtimeSinceStartup;
                    _vs.Notice($"Versus: Warte auf die Gäste ({ChosenCount} von {GuestTotal} haben ihr HQ gewählt)");
                    Plugin.Log.LogInfo($"IFZVS host HQ {building.ID} wartet auf Gäste");
                    return false;
                }
            }
            return true;
        }

        /// <summary>Postfix derselben Methode (nur wenn sie wirklich lief).</summary>
        public void ConfirmPostfix(HqSetterController setter, bool ran)
        {
            if (!ran || MyHqConfirmed || !VersusSession.Active)
                return;
            if (_vs.Role == VsRole.Guest && _vs.GuestState != VsGuestState.InGame)
                return;
            Building building = null;
            try { building = SelectedField(setter); } catch (Exception) { }
            if (building == null)
                return;
            MyHqConfirmed = true;
            MyHqId = building.ID;
            MyHqPos = building.Position;
            AwaitingAnswer = false;
            HostPending = false;
            _hqSetAt = Time.realtimeSinceStartup;
            _lastWorkers = -1;
            Plugin.Log.LogInfo($"IFZVS eigenes HQ {MyHqId} bei {MyHqPos.x:0},{MyHqPos.z:0}");
            if (_vs.Role == VsRole.Host)
            {
                _safeZone1 = _hqSetAt + 3f;
                _safeZone2 = _hqSetAt + 12f;
                _vs.SetHostPhase(VsPhase.Running, "Los! Alle Hauptquartiere stehen.");
            }
        }

        private static void Deny(HqSetterController setter)
        {
            try
            {
                Traverse.Create(setter).Method("OnSelectionDenied").GetValue();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("IFZVS HQ-Wahl zurücknehmen: " + e.Message);
            }
        }

        private static void Confirm(HqSetterController setter, VsStart start)
        {
            start.AllowConfirm = true;
            try
            {
                Traverse.Create(setter).Method("OnSelectionConfirmed").GetValue();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("IFZVS HQ bestätigen: " + e);
            }
            finally
            {
                start.AllowConfirm = false;
            }
        }

        /// <summary>Postfix von HideoutSpawnPointSpawner.IsToCloseHq (Host): Verstecke auch nicht an Gast-HQs.</summary>
        public bool TooCloseToGuestHq(Vector3 position, float distance)
        {
            if (_vs.Role != VsRole.Host)
                return false;
            var p = new Vector2(position.x, position.z);
            foreach (var peer in _vs.Peers)
                if (peer.HqChosen && Vector2.Distance(p, new Vector2(peer.HqPos.x, peer.HqPos.z)) <= distance)
                {
                    HideoutsMoved++;
                    return true;
                }
            return false;
        }

        // ================================================================ Host

        public int GuestTotal => _vs.WelcomedGuestCount;

        public int ChosenCount
        {
            get
            {
                int n = 0;
                foreach (var p in _vs.Peers) if (p.Welcomed && p.HqChosen) n++;
                return n;
            }
        }

        public bool AllGuestsChosen
        {
            get
            {
                foreach (var p in _vs.Peers)
                    if (p.Welcomed && !p.HqChosen) return false;
                return true;
            }
        }

        public float HostWaitLeft => HostPending ? Mathf.Max(0f, HostWaitLimit - (Time.realtimeSinceStartup - _hostPendingSince)) : 0f;

        /// <summary>Abstand zu den HQs aller anderen Plätze (Host: auch seine wartende oder fertige Wahl).</summary>
        private bool TooClose(Vector3 pos, int exceptSlot, out string who)
        {
            who = null;
            var p = new Vector2(pos.x, pos.z);
            foreach (var peer in _vs.Peers)
            {
                if (!peer.HqChosen || peer.Slot == exceptSlot)
                    continue;
                if (Vector2.Distance(p, new Vector2(peer.HqPos.x, peer.HqPos.z)) < MinHqDistance)
                {
                    who = peer.Name;
                    return true;
                }
            }
            if (exceptSlot != 0)
            {
                Vector3? host = MyHqConfirmed ? MyHqPos : HostPending ? _hostPendingPos : (Vector3?)null;
                if (host.HasValue && Vector2.Distance(p, new Vector2(host.Value.x, host.Value.z)) < MinHqDistance)
                {
                    who = "dem Host";
                    return true;
                }
            }
            return false;
        }

        public void HostOnHqChosen(VsPeer peer, BinaryReader r)
        {
            long id = r.ReadInt64();
            float x = r.ReadSingle();
            float z = r.ReadSingle();
            bool ok = true;
            string reason = string.Empty;
            if (!peer.Loaded)
            {
                ok = false;
                reason = "Der Host ist noch nicht bereit";
            }
            else if (peer.HqChosen && peer.HqId != id)
            {
                ok = false;
                reason = "Du hast schon ein HQ gewählt";
            }
            else if (!peer.HqChosen && TooClose(new Vector3(x, 0f, z), peer.Slot, out string who))
            {
                ok = false;
                reason = $"Zu nah an einem anderen Hauptquartier (mindestens {MinHqDistance:0} m), bitte ein anderes Gebäude";
                Plugin.Log.LogInfo($"IFZVS HQ {id} von {peer.Name} abgelehnt: zu nah an {who}");
            }
            if (ok)
            {
                peer.HqChosen = true;
                peer.HqId = id;
                peer.HqPos = new Vector3(x, 0f, z);
                Plugin.Log.LogInfo($"IFZVS HQ von {peer.Name} (Platz {peer.Slot}): {id} bei {x:0},{z:0}");
            }
            _vs.SendTo(peer.Id, VsNet.Build(Msg.VsHqAnswer, w => { w.Write(ok); w.Write(id); w.Write(reason); }));
        }

        public void HostOnPopulation(VsPeer peer, BinaryReader r)
        {
            peer.PopKnown = true;
            peer.PopApplied = r.ReadBoolean();
            peer.PopWorkers = r.ReadInt32();
            peer.PopChildren = r.ReadInt32();
            peer.PopSquads = r.ReadInt32();
            peer.PopSoldiers = r.ReadInt32();
            peer.PopResources = r.ReadString();
        }

        // ================================================================ Gast

        /// <summary>Gast hat den Versus-Spielstand geladen: eigener ID-Bereich.</summary>
        public void OnGuestLoaded()
        {
            MyHqConfirmed = false;
            Applied = false;
            AwaitingAnswer = false;
            OffsetIds(_vs.MySlot);
        }

        private void OffsetIds(int slot)
        {
            try
            {
                var ids = GameAccess.Game<Serialization.Data.IdProvider>();
                if (ids == null)
                    return;
                var field = Traverse.Create(ids).Field("_lastID");
                uint last = field.GetValue<uint>();
                uint offset = (uint)slot * VsProtocol.IdOffsetPerSlot;
                if (slot > 0 && last < offset)
                {
                    field.SetValue(last + offset);
                    Plugin.Log.LogInfo($"IFZVS IDs ab {last + offset} (Platz {slot}, vorher {last})");
                }
                IdBase = field.GetValue<uint>();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("IFZVS IDs versetzen: " + e.Message);
            }
        }

        public static uint CurrentLastId()
        {
            try
            {
                var ids = GameAccess.Game<Serialization.Data.IdProvider>();
                return ids == null ? 0u : Traverse.Create(ids).Field("_lastID").GetValue<uint>();
            }
            catch (Exception)
            {
                return 0u;
            }
        }

        public void GuestOnHqAnswer(BinaryReader r)
        {
            bool ok = r.ReadBoolean();
            long id = r.ReadInt64();
            string reason = r.ReadString();
            if (!AwaitingAnswer || id != _requestedHq)
                return;
            AwaitingAnswer = false;
            var setter = Setter;
            if (setter == null)
                return;
            Building selected = null;
            try { selected = SelectedField(setter); } catch (Exception) { }
            if (ok && selected != null && selected.ID == id)
            {
                Plugin.Log.LogInfo($"IFZVS guest HQ {id} bestätigt");
                Confirm(setter, this);
            }
            else
            {
                Rejections++;
                Deny(setter);
                _vs.Notice(ok ? "HQ-Wahl hat sich geändert, bitte neu wählen" : reason, 8f);
            }
        }

        // ================================================================ Takt

        public void Update(float now)
        {
            if (_vs.Role == VsRole.Host && HostPending)
            {
                var setter = Setter;
                Building selected = null;
                try { if (setter != null) selected = SelectedField(setter); } catch (Exception) { }
                if (setter == null || !setter.IsShowPopup || selected == null || selected.ID != _hostPendingId)
                {
                    HostPending = false; // abgebrochen
                }
                else if (AllGuestsChosen || ForceStart || now - _hostPendingSince > HostWaitLimit)
                {
                    Plugin.Log.LogInfo($"IFZVS host bestätigt HQ {_hostPendingId} (gewählt {ChosenCount}/{GuestTotal}, erzwungen {ForceStart})");
                    HostPending = false;
                    Confirm(setter, this);
                }
            }

            if (_vs.Role == VsRole.Guest && AwaitingAnswer && now - _requestedAt > 20f)
            {
                // keine Antwort: noch einmal fragen
                _requestedAt = now;
                var setter = Setter;
                Building selected = null;
                try { if (setter != null) selected = SelectedField(setter); } catch (Exception) { }
                if (selected != null && selected.ID == _requestedHq)
                {
                    var pos = selected.Position;
                    long id = _requestedHq;
                    _vs.SendToHost(VsNet.Build(Msg.VsHqChosen, w => { w.Write(id); w.Write(pos.x); w.Write(pos.z); }));
                }
                else
                {
                    AwaitingAnswer = false;
                }
            }

            if (MyHqConfirmed && !Applied)
                TryApplyStartConditions(now);

            if (_vs.Role == VsRole.Host)
            {
                if (_safeZone1 > 0f && now >= _safeZone1) { _safeZone1 = -1f; ClearSafeZones(); }
                if (_safeZone2 > 0f && now >= _safeZone2) { _safeZone2 = -1f; ClearSafeZones(); }
            }
        }

        // ================================================================ Startbedingungen

        private void TryApplyStartConditions(float now)
        {
            if (HqController.MainHeadquarter == null || !GameAccess.IsGameStarted)
                return;
            // Die Start-Arbeiter entstehen per Koroutine nach der HQ-Wahl (WorkersController.OnHqAdopted): warten,
            // bis es welche gibt und ihre Zahl 2 s gleich bleibt (Notfall: nach 120 s trotzdem).
            int workers = WorkersController.WorkersCount;
            if (workers != _lastWorkers)
            {
                _lastWorkers = workers;
                _workersStableSince = now;
            }
            bool timeout = now - _hqSetAt > 120f;
            if (!timeout && (workers == 0 || now - _workersStableSince < 2f))
                return;
            ApplyStartConditions();
        }

        private void ApplyStartConditions()
        {
            Applied = true;
            var s = _vs.Settings;
            var sb = new StringBuilder();
            var hq = HqController.MainHeadquarter;
            var hqPos = hq != null ? hq.Position : MyHqPos;
            try { ApplyHq(s, hq as Building); }
            catch (Exception e) { Plugin.Log.LogError("IFZVS HQ-Stärke: " + e); }
            if (!s.CustomStart)
            {
                AppliedSummary = "Standard (wie auf der Karte)";
                Plugin.Log.LogInfo("IFZVS Startbedingungen: Standard der Karte, nichts geändert");
                if (_vs.Role == VsRole.Guest)
                    _vs.SendToHost(BuildPopulation());
                return;
            }
            try
            {
                if (s.Workers > 0)
                {
                    int have = Adults(c => !c.IsChild && !c.IsSick).Count;
                    if (have < s.Workers)
                        GameAccess.Game<WorkersController>()?.CreateNewWorkers(s.Workers - have, hqPos + new Vector3(5f, 0f, 5f));
                    else if (have > s.Workers)
                    {
                        var list = Adults(c => !c.IsChild && !c.IsSick);
                        int remove = have - s.Workers;
                        for (int i = list.Count - 1; i >= 0 && remove > 0; i--, remove--)
                            list[i].Kill(SubtractionHpReason.None);
                    }
                    sb.Append("workers ").Append(have).Append("->").Append(s.Workers).Append(' ');
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("IFZVS Start-Einwohner: " + e);
            }

            try
            {
                // „Trupps am Anfang“ = Gesamtzahl: Trupps, die das Spiel schon mitgibt (z. B. Vorlage-Städte), zählen mit
                // (Bericht 2026-11-07: 1 eingestellt, 2 bekommen)
                CountPopulation(out _, out _, out int haveSquads, out _);
                int toMake = Mathf.Max(0, s.Squads - haveSquads);
                if (toMake > 0)
                {
                    var builder = GameAccess.Game<GroupBuilder>();
                    var config = JutsuGamesConfig.Runtime.Configs.GetConfig<Gameplay.Units.Enemy.GroupsConfig>();
                    var draft = config?.GetDraftByName("player");
                    int made = 0;
                    for (int i = 0; i < toMake && builder != null && draft != null; i++)
                    {
                        float a = i * Mathf.PI * 2f / Mathf.Max(1, toMake);
                        var pos = hqPos + new Vector3(Mathf.Cos(a) * 10f, 0f, Mathf.Sin(a) * 10f);
                        if (builder.Clear().SpawnGroupAt(pos, draft, s.SquadSize) != null)
                            made++;
                    }
                    sb.Append("squads ").Append(made).Append('x').Append(s.SquadSize).Append(' ');
                }
                else
                    sb.Append("squads schon ").Append(haveSquads).Append(' ');
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("IFZVS Start-Trupps: " + e);
            }

            try
            {
                var stock = GameAccess.Game<StockroomsController>();
                int n = 0;
                if (stock?.Container != null)
                    foreach (var kv in s.Resources)
                        if (kv.Value > 0)
                        {
                            stock.Container.ForceAddResource(kv.Key, kv.Value, ResourceAddReason.None);
                            n++;
                        }
                sb.Append("res ").Append(n);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("IFZVS Start-Rohstoffe: " + e);
            }

            try
            {
                int made = SpawnStartVehicles(s, hqPos);
                if (made > 0) sb.Append(" veh ").Append(made);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("IFZVS Start-Fahrzeuge: " + e);
            }

            AppliedSummary = sb.ToString().Trim();
            Plugin.Log.LogInfo("IFZVS Startbedingungen gesetzt: " + AppliedSummary);
            if (_vs.Role == VsRole.Guest)
                _vs.SendToHost(BuildPopulation());
        }

        /// <summary>Fester Lagerplatz des eigenen HQ (Versus „HQ bei allen gleich“), 0 = wie im Spiel.</summary>
        internal static int ForcedHqStorage;

        private static readonly AccessTools.FieldRef<Building, BuildingEnforcementHandler> EnforcementOf =
            AccessTools.FieldRefAccess<Building, BuildingEnforcementHandler>("EnforcementHandler");

        /// <summary>HQ bei allen gleich: Grund-Leben, Verstärkung (wie „Anpassen“ im Spiel) und Lagerplatz fest setzen.</summary>
        private static void ApplyHq(VsSettings s, Building hq)
        {
            ForcedHqStorage = 0;
            if (!s.HqEqual || hq == null)
                return;
            hq.Health.SetMaxHp(s.HqHp);
            var eh = EnforcementOf(hq);
            if (eh != null) eh.SetEnforcement((UI.InfoPanels.EnforcementType)s.HqEnforcement);
            hq.Health.SetHealthAmount01(1f);
            ForcedHqStorage = s.HqStorage;
            hq.StructureResourceContainer.SetCapacity(s.HqStorage, force: true);
            Plugin.Log.LogInfo("IFZVS HQ gleich: Leben " + hq.Health.MaxHp.ToString("0") + ", Verstärkung " + s.HqEnforcement + ", Lager " + s.HqStorage);
        }

        /// <summary>Postfix Building.UpdateStorage/UpdateProduction: das Spiel rechnet den Lagerplatz aus der Größe neu, der feste Wert bleibt.</summary>
        internal static void UpdateStoragePostfix(Building __instance)
        {
            if (ForcedHqStorage <= 0)
                return;
            if (!VersusSession.Active) { ForcedHqStorage = 0; return; }
            if (__instance != null && __instance == HqController.MainHeadquarter)
                __instance.StructureResourceContainer.SetCapacity(ForcedHqStorage, force: true);
        }

        private static readonly System.Reflection.MethodInfo NearestOnRoad =
            AccessTools.Method(typeof(Utilities.PathfindingUtils), "GetNearestOnRoad", new[] { typeof(Vector3) });

        /// <summary>Nächster Straßenpunkt (wie das Spiel herrenlose Autos abstellt), sonst die Stelle selbst.</summary>
        private static Vector3 OnRoad(Vector3 pos)
        {
            try
            {
                if (NearestOnRoad == null) return pos;
                object nn = NearestOnRoad.Invoke(null, new object[] { pos });
                var t = Traverse.Create(nn);
                if (t.Field("node").GetValue() == null) return pos;
                var p = t.Field("position").GetValue<Vector3>();
                return (p - pos).sqrMagnitude < 60f * 60f ? p : pos;
            }
            catch { return pos; }
        }

        /// <summary>Eingestellte Fahrzeuge im Kreis um das HQ, je auf die nächste Straße gesetzt, herrenlos (wie F7 „Fahrzeug erzeugen“).</summary>
        private static int SpawnStartVehicles(VsSettings s, Vector3 hqPos)
        {
            var list = new List<Gameplay.Vehicles.VehicleType>();
            foreach (var kv in s.Vehicles)
                for (int i = 0; i < kv.Value; i++) list.Add(kv.Key);
            if (list.Count == 0)
                return 0;
            var vc = GameAccess.Game<VehiclesController>();
            var ids = GameAccess.Game<Serialization.Data.IdProvider>();
            var factory = vc != null ? CheatWindow.VehicleFactory(vc) : null;
            if (vc == null || ids == null || factory == null)
                return 0;
            int made = 0;
            var used = new List<Vector3>();
            for (int i = 0; i < list.Count; i++)
            {
                float a = i * Mathf.PI * 2f / list.Count + 0.4f;
                float r = 22f + 6f * (i / 8);
                var want = hqPos + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                var pos = OnRoad(want);
                // nicht aufeinander stapeln
                foreach (var u in used)
                    if ((u - pos).sqrMagnitude < 36f) { pos = want; break; }
                used.Add(pos);
                try
                {
                    var v = factory.Create(ids.GetNextId(), list[i], pos, Quaternion.Euler(0f, a * Mathf.Rad2Deg, 0f));
                    vc.InitAbandonedVehicle(v);
                    CheatWindow.MakeReady(v);
                    made++;
                }
                catch (Exception e) { Plugin.Log.LogWarning("IFZVS Start-Fahrzeug " + list[i] + ": " + e.Message); }
            }
            return made;
        }

        internal static List<Gameplay.Units.Characters.Character> Adults(Func<Gameplay.Units.Characters.Character, bool> pick)
        {
            var list = new List<Gameplay.Units.Characters.Character>();
            var cc = GameAccess.Game<CitizensController>();
            if (cc?.Citizens == null)
                return list;
            foreach (var c in cc.Citizens)
                if (c != null && c.Group == null && !c.IsDead() && pick(c)) list.Add(c);
            return list;
        }

        public static void CountPopulation(out int workers, out int children, out int squads, out int soldiers)
        {
            workers = Adults(c => !c.IsChild).Count;
            children = Adults(c => c.IsChild).Count;
            squads = 0;
            soldiers = 0;
            var list = GameAccess.Game<SquadsController>()?.Squads;
            if (list == null)
                return;
            foreach (var g in list)
            {
                if (g == null || g.Characters.Count == 0)
                    continue;
                squads++;
                soldiers += g.Characters.Count;
            }
        }

        /// <summary>Vorrat der eingestellten Start-Rohstoffe im Lager („res_wood:120,res_metal:80“).</summary>
        public static string ResourceLine(VsSettings s)
        {
            var stock = GameAccess.Game<StockroomsController>();
            if (stock?.Container == null || s.Resources.Count == 0)
                return "-";
            var sb = new StringBuilder();
            var keys = new List<ResourceID>(s.Resources.Keys);
            keys.Sort((a, b) => ((int)a).CompareTo((int)b));
            foreach (var id in keys)
            {
                float have;
                try { have = stock.Container.GetResourceQuantity(id); }
                catch (Exception) { have = -1f; }
                if (sb.Length > 0) sb.Append(',');
                sb.Append(id).Append(':').Append(have.ToString("0", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public byte[] BuildPopulation()
        {
            CountPopulation(out int workers, out int children, out int squads, out int soldiers);
            string res = ResourceLine(_vs.Settings);
            bool applied = Applied;
            return VsNet.Build(Msg.VsPopulation, w =>
            {
                w.Write(applied);
                w.Write(workers);
                w.Write(children);
                w.Write(squads);
                w.Write(soldiers);
                w.Write(res);
            });
        }

        // ================================================================ Schonzone (Host)

        private void ClearSafeZones()
        {
            var groups = GameAccess.Game<Gameplay.Units.Enemy.GroupsController>()?.Groups;
            if (groups == null)
                return;
            var remove = new List<Group>();
            foreach (var g in groups)
            {
                if (g == null || g.Characters.Count == 0)
                    continue;
                var f = g.Fraction;
                if (f != Fraction.Infected && f != Fraction.Bandits && f != Fraction.Provirus && f != Fraction.Bandits_ransom)
                    continue;
                var gp = new Vector2(g.Position.x, g.Position.z);
                foreach (var peer in _vs.Peers)
                    if (peer.HqChosen && Vector2.Distance(gp, new Vector2(peer.HqPos.x, peer.HqPos.z)) < SafeRadius)
                    {
                        remove.Add(g);
                        break;
                    }
            }
            foreach (var g in remove)
            {
                try
                {
                    g.DestroySilent();
                    SafeZoneRemoved++;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("IFZVS Schonzone: " + e.Message);
                }
            }
            if (remove.Count > 0)
                Plugin.Log.LogInfo($"IFZVS Schonzone: {remove.Count} Welt-Gruppen an Gast-HQs entfernt");
        }

        // ================================================================ Test

        /// <summary>
        /// Testbefehl „vs hq &lt;dx&gt; &lt;dz&gt;“: passendes Gebäude (FirstHqPossibility.CouldBe) am nächsten zu Kartenmitte + (dx, dz)
        /// wählen und bestätigen wie der Klick im Auswahlfenster (Muster QaDriver.ChooseHq).
        /// </summary>
        public string QaChooseHq(float dx, float dz)
        {
            var setter = UnityEngine.Object.FindObjectOfType<HqSetterController>();
            if (setter == null)
                return "keine HQ-Wahl";
            if (MyHqConfirmed || HqController.MainHeadquarter != null)
                return "HQ steht schon: " + MyHqId;
            if (AwaitingAnswer)
                return "wartet auf Antwort des Hosts";
            if (HostPending)
                return "wartet auf Gäste " + ChosenCount + "/" + GuestTotal;
            var center = MapEssentials.Map.Bound.center;
            var target = new Vector2(center.x + dx, center.y + dz);
            Building best = null;
            float bestD = float.MaxValue;
            foreach (var b in GameAccess.Game<BuildingsController>().Buildings)
            {
                if (b == null || b.FirstHqPossibility != FirstHqPossibility.CouldBe)
                    continue;
                float d = Vector2.Distance(new Vector2(b.Position.x, b.Position.z), target);
                if (d < bestD)
                {
                    bestD = d;
                    best = b;
                }
            }
            if (best == null)
                return "kein passendes Gebäude";
            var config = Traverse.Create(setter).Field("_firstHqConfig").GetValue<Core.Configs.FirstHqConfig>();
            Traverse.Create(setter).Method("ShowSelectionPopup", best, config.AdaptValueFor(best)).GetValue();
            Traverse.Create(setter).Method("OnSelectionConfirmed").GetValue();
            var p = best.Position;
            string state = MyHqConfirmed ? "bestätigt" : AwaitingAnswer ? "an den Host gemeldet" : HostPending ? "wartet auf Gäste" : "abgelehnt";
            return "HQ " + best.ID + " bei " + p.x.ToString("0", CultureInfo.InvariantCulture) + "," + p.z.ToString("0", CultureInfo.InvariantCulture)
                   + " (" + bestD.ToString("0", CultureInfo.InvariantCulture) + " m vom Ziel): " + state;
        }
    }
}
