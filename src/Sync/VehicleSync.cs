using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Controllers;
using Gameplay.Units;
using Gameplay.Vehicles;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using Newtonsoft.Json;
using UnityEngine;
using GameJson = Utilities.JsonSerializer;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Fahrzeuge (Analyse P4, Paket E). Die Position läuft weiter über UnitSync (VehicleSnapshot 5×/s), hier geht es um
    /// alles andere: neue und verschwundene Fahrzeuge, Zustand (Bergbar/Geborgen/Kaputt/In Reparatur), Besitzer, wer drin
    /// sitzt, HP, Sprit, Munition, Umbauten, Lack. Der Kofferraum-Inhalt kommt über ContainerSync.
    ///
    /// Host: 1×/s Unterschrift je Fahrzeug; bei Änderung das ganze VehicleSerialized (wie im Spielstand) plus die ID der
    /// Gruppe, die drin sitzt. Alle 5 s die Liste aller Fahrzeug-IDs (der Gast räumt eigene Fahrzeuge weg).
    /// Gast: legt fehlende Fahrzeuge mit der ID des Hosts an (Vehicle.Factory wie beim Laden), übernimmt den Zustand und
    /// lässt Trupps ein- und aussteigen (VehicleHandler.AssignVehicle/UnassignVehicle unter NetApplying). Ein Fahrzeug, das
    /// der Gast selbst erzeugt hat (eigene ID), aber in dem laut Host eine Gruppe sitzt, bekommt die ID des Hosts.
    /// </summary>
    internal sealed class VehicleSync
    {
        private const float SendInterval = 1f;
        private const float IdListInterval = 5f;
        private const float ReconcileInterval = 0.5f;

        private sealed class HostView
        {
            public string Occupant;   // Gruppe, die laut Host drin sitzt (VehicleHandler.Vehicle == Fahrzeug)
            public string Owner;
            public bool Waiting;
        }

        private static readonly Action<Vehicle, string> SetId =
            AccessTools.MethodDelegate<Action<Vehicle, string>>(AccessTools.PropertySetter(typeof(Vehicle), "Id"));
        private static readonly AccessTools.FieldRef<VehiclesController, Vehicle.Factory> FactoryField =
            AccessTools.FieldRefAccess<VehiclesController, Vehicle.Factory>("_vehicleFactory");

        private readonly Session _session;

        // Host
        private readonly Dictionary<string, string> _sentSig = new Dictionary<string, string>();
        private float _nextSend;
        private float _nextIdList;

        // Gast
        private readonly Dictionary<string, HostView> _hostView = new Dictionary<string, HostView>();
        private readonly Dictionary<string, string> _appliedLook = new Dictionary<string, string>();
        private readonly Dictionary<string, int> _foreignStrikes = new Dictionary<string, int>();
        private HashSet<string> _hostIds;
        private float _nextReconcile;

        public int Applied { get; private set; }
        public int Created { get; private set; }
        public int FuelApplied { get; private set; }
        public int Adopted { get; private set; }
        public int Removed { get; private set; }
        public int Entered { get; private set; }
        public int Exited { get; private set; }

        public VehicleSync(Session session)
        {
            _session = session;
        }

        public void ResendAll()
        {
            _sentSig.Clear();
            _nextIdList = 0f;
        }

        public void Reset()
        {
            ResendAll();
            _hostView.Clear();
            _appliedLook.Clear();
            _foreignStrikes.Clear();
            _hostIds = null;
        }

        private static List<Vehicle> AllVehicles() => GameAccess.Game<VehiclesController>()?.Vehicles;

        private static string OccupantOf(Vehicle v)
        {
            var owner = v.Owner;
            if (owner == null || owner.VehicleHandler == null)
                return "";
            return owner.VehicleHandler.Vehicle == v ? owner.Id ?? "" : "";
        }

        private static string R(float f, float step) => Mathf.RoundToInt(f / step).ToString(CultureInfo.InvariantCulture);

        private static float FuelFraction(Vehicle v)
        {
            float max = v.Fuel.FuelTank.MaxQuantity;
            return max > 0f ? v.Fuel.FuelTank.Quantity / max : 0f;
        }

        private static string LookSignature(Vehicle v)
        {
            var p = v.VehiclePainter;
            var sb = new StringBuilder();
            sb.Append(string.Join(",", v.ModificationHandler.ModificationsDict.Keys.Select(m => ((int)m).ToString(CultureInfo.InvariantCulture)).OrderBy(x => x)));
            sb.Append('|').Append(ColorUtility.ToHtmlStringRGBA(v.VehicleColor));
            if (p != null)
                sb.Append('|').Append(p.HasCustomizedColor).Append(ColorUtility.ToHtmlStringRGBA(p.Color))
                  .Append(p.HasCamo).Append(p.HasFire).Append(p.HasSkin).Append(p.ShouldShowEmblem);
            return sb.ToString();
        }

        private static string Signature(Vehicle v)
        {
            var sb = new StringBuilder();
            sb.Append((int)v.VehicleState).Append('|').Append(v.Owner != null ? v.Owner.Id : "").Append('|').Append(OccupantOf(v))
              .Append('|').Append(v.IsWaiting).Append(v.IsDiscovered).Append(v.IsDiscoveredWithRvoice).Append(v.UnderCarWorkshop)
              .Append(v.AutoDriveState).Append(v.DriveOffRoad).Append(v.EquipmentLockHandler.IsLocked)
              .Append('|').Append(R(v.Health.Hp, 1f))
              // Tankinhalt ist intern winzig (voller Pickup-Tank < 0,005), darum in Prozent vom Tank plus leer/nicht leer
              // (Bericht 2026-11-09: Host-Tank leer, Gast hatte noch Sprit – Schritte von 0,01 kamen nie zustande).
              .Append('|').Append(R(FuelFraction(v), 0.01f)).Append(v.Fuel.IsEmpty ? 'E' : 'F')
              .Append('|').Append(R(v.AmmoContainer.AmmoCrate.Quantity, 0.02f))
              .Append('|').Append(R(v.ProgressOfTakingVehicle, 0.05f))
              .Append('|').Append(R(v.CurrentAmmoCapacity, 0.01f))
              .Append('|').Append(LookSignature(v));
            return sb.ToString();
        }

        // ================================================================ Host

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend || _session.ActiveGuestCount == 0 || !GameAccess.IsGameStarted)
                return;
            _nextSend = now + SendInterval;
            var vehicles = AllVehicles();
            if (vehicles == null)
                return;

            var upserts = new List<Vehicle>();
            var seen = new HashSet<string>();
            foreach (var v in vehicles)
            {
                if (v == null || string.IsNullOrEmpty(v.Id))
                    continue;
                seen.Add(v.Id);
                string sig;
                try { sig = Signature(v); }
                catch (Exception e) { Plugin.Log.LogDebug($"Fahrzeug {v.Id}: {e.Message}"); continue; }
                if (!_sentSig.TryGetValue(v.Id, out var last) || last != sig)
                {
                    _sentSig[v.Id] = sig;
                    upserts.Add(v);
                }
            }
            var gone = _sentSig.Keys.Where(id => !seen.Contains(id)).ToList();
            foreach (var id in gone) _sentSig.Remove(id);

            bool sendIds = now >= _nextIdList;
            if (sendIds)
            {
                _nextIdList = now + IdListInterval;
                // Trupp-Fahrzeuge regelmäßig ganz: der Gast rechnet Verbrauch/Auftanken selbst und kann abweichen,
                // ohne dass sich beim Host etwas ändert
                foreach (var v in vehicles)
                    if (v != null && !string.IsNullOrEmpty(v.Id) && v.Owner != null && !upserts.Contains(v))
                        upserts.Add(v);
            }
            if (upserts.Count == 0 && gone.Count == 0 && !sendIds)
                return;
            Broadcast(upserts, gone, sendIds ? seen.ToList() : null);
        }

        /// <summary>Host: ein Fahrzeug sofort schicken (z. B. bevor eine neue Gruppe übertragen wird, die drin sitzt).</summary>
        public void HostSendNow(Vehicle v)
        {
            if (v == null || string.IsNullOrEmpty(v.Id) || _session.Role != Role.Host || _session.ActiveGuestCount == 0)
                return;
            try { _sentSig[v.Id] = Signature(v); } catch { }
            Broadcast(new List<Vehicle> { v }, new List<string>(), null);
        }

        private void Broadcast(List<Vehicle> upserts, List<string> gone, List<string> ids)
        {
            var entries = new List<KeyValuePair<string, string>>();
            foreach (var v in upserts)
            {
                try
                {
                    string json = JsonConvert.SerializeObject(new VehicleSerialized(v), GameJson.DefaultSettings);
                    entries.Add(new KeyValuePair<string, string>(json, OccupantOf(v)));
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Fahrzeug {v.Id} lässt sich nicht übertragen: {e.Message}");
                }
            }
            _session.BroadcastToGuests(Protocol.Build(Msg.VehicleState, w =>
            {
                w.Write(entries.Count);
                foreach (var e in entries)
                {
                    w.Write(e.Key);
                    w.Write(e.Value);
                }
                w.Write(gone.Count);
                foreach (var id in gone) w.Write(id);
                w.Write(ids != null);
                if (ids != null)
                {
                    w.Write(ids.Count);
                    foreach (var id in ids) w.Write(id);
                }
            }));
        }

        // ================================================================ Gast

        public void GuestOnState(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 10000)
                throw new InvalidDataException("zu viele Fahrzeuge");
            var entries = new List<KeyValuePair<string, string>>(n);
            for (int i = 0; i < n; i++)
                entries.Add(new KeyValuePair<string, string>(r.ReadString(), r.ReadString()));
            int g = r.ReadInt32();
            if (g < 0 || g > 10000)
                throw new InvalidDataException("zu viele Fahrzeuge");
            var gone = new List<string>(g);
            for (int i = 0; i < g; i++) gone.Add(r.ReadString());
            List<string> ids = null;
            if (r.ReadBoolean())
            {
                int k = r.ReadInt32();
                if (k < 0 || k > 10000)
                    throw new InvalidDataException("zu viele Fahrzeuge");
                ids = new List<string>(k);
                for (int i = 0; i < k; i++) ids.Add(r.ReadString());
            }

            var controller = GameAccess.Game<VehiclesController>();
            if (controller == null)
                return;

            UnitSync.NetApplying = true;
            try
            {
                foreach (var e in entries)
                {
                    VehicleSerialized s;
                    try
                    {
                        s = JsonConvert.DeserializeObject<VehicleSerialized>(e.Key, GameJson.DefaultSettings);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning("Fahrzeug nicht lesbar: " + ex.Message);
                        continue;
                    }
                    if (string.IsNullOrEmpty(s.Id))
                        continue;
                    _hostIds?.Add(s.Id);
                    _hostView[s.Id] = new HostView { Occupant = e.Value ?? "", Owner = s.OwnerId ?? "", Waiting = s.IsWaiting };
                    try { Upsert(controller, s, e.Value ?? ""); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"Fahrzeug {s.Id}: {ex}"); }
                }
                foreach (var id in gone)
                {
                    _hostView.Remove(id);
                    _hostIds?.Remove(id);
                    var v = controller.GetVehicleById(id);
                    if (v != null)
                        RemoveVehicle(v, "beim Host weg");
                }
                if (ids != null)
                {
                    _hostIds = new HashSet<string>(ids);
                    CleanupForeign(controller);
                }
                Reconcile(controller);
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        private void Upsert(VehiclesController controller, VehicleSerialized s, string occupant)
        {
            var v = controller.GetVehicleById(s.Id);
            if (v == null)
                v = Adopt(controller, s.Id, occupant);
            bool fresh = false;
            if (v == null)
            {
                var factory = FactoryField(controller);
                if (factory == null)
                    return;
                Vector3 pos = s.MapObject != null ? s.MapObject.Position : Vector3.zero;
                Quaternion rot = s.MapObject != null ? Quaternion.Euler(s.MapObject.Rotation) : Quaternion.identity;
                v = factory.Create(s.Id, s.VehicleType, pos, rot);
                if (v == null)
                    return;
                controller.AddVehicle(v);
                fresh = true;
                Created++;
                Plugin.Log.LogInfo($"IFZCOOP vehicle create {s.Id} {s.VehicleType}");
            }
            Apply(v, s, fresh);
            Applied++;
        }

        /// <summary>Gast hat das Fahrzeug selbst gebaut (eigene ID), die Gruppe darin kennt aber der Host: ID übernehmen.</summary>
        private Vehicle Adopt(VehiclesController controller, string hostId, string occupant)
        {
            if (string.IsNullOrEmpty(occupant) || SetId == null)
                return null;
            var group = UnitSync.FindGroupPublic(occupant);
            var local = group != null ? group.VehicleHandler?.Vehicle : null;
            if (local == null || local.Id == hostId || (_hostIds != null && _hostIds.Contains(local.Id)) || _hostView.ContainsKey(local.Id))
                return null;
            Plugin.Log.LogInfo($"IFZCOOP vehicle adopt {local.Id} -> {hostId}");
            SetId(local, hostId);
            Adopted++;
            return local;
        }

        private void Apply(Vehicle v, VehicleSerialized s, bool fresh)
        {
            if (s.IsDiscovered && !v.IsDiscovered)
                v.DiscoverVehicle();
            if (s.IsDiscoveredWithRvoice && !v.IsDiscoveredWithRvoice)
                v.SetDiscoveredWithRvoice();

            // Umbauten und Lack nur bei Änderung (Umbauten bauen den Kofferraum neu, darum danach Inhalt nachladen).
            string look = LookOf(s);
            if (fresh || !_appliedLook.TryGetValue(s.Id, out var lastLook) || lastLook != look)
            {
                _appliedLook[s.Id] = look;
                bool modsChanged = false;
                if (s.Modifications != null)
                    foreach (var m in s.Modifications)
                        if (!v.ModificationHandler.HasModification(m))
                        {
                            v.ModificationHandler.AddModification(m);
                            modsChanged = true;
                        }
                if (s.VehicleColor != null)
                    v.SetColor(s.VehicleColor.Deserialize());
                v.UpdateColor();
                var p = v.VehiclePainter;
                if (p != null)
                {
                    if (s.HasCustomizedColor && s.SerializableCustomizedColor != null)
                    {
                        v.VehicleColor = s.SerializableCustomizedColor.Deserialize();
                        p.SetColor(s.SerializableCustomizedColor.Deserialize());
                    }
                    p.SetSkinStatus(s.HasSkin);
                    p.SetFireStatus(s.HasFire);
                    p.SetCamoStatus(s.HasCamo);
                    p.SetShowEmblemStatus(s.ShouldShowEmblem);
                    p.UpdateVehicleSkins();
                }
                if ((fresh || modsChanged) && s.ResourcesInTrunk != null)
                {
                    v.Trunk.LoadResources(s.ResourcesInTrunk);
                    if (s.LockedResourcesInTrunk != null)
                        v.Trunk.LoadLockedResources(s.LockedResourcesInTrunk);
                }
            }

            v.SetAmmoCapacity(s.CurrentAmmoCapacity);
            if (Mathf.Abs(v.ProgressOfTakingVehicle - s.ProgressOfTakingVehicle) > 0.001f)
                v.SetRecoveryProgress(s.ProgressOfTakingVehicle);
            v.HitCounter = s.HitCounter;
            v.DriveOffRoad = s.DriveOffRoad;
            if (Mathf.Abs(v.Health.Hp - s.Hp) > 0.01f)
                v.Health.SetHp(s.Hp);
            // gleicher Wert wie FuelTank.Quantity beim Host; Toleranz relativ zum Tank (die Werte sind sehr klein)
            float fuelTol = Mathf.Max(v.Fuel.FuelTank.MaxQuantity * 0.002f, 1e-9f);
            bool hostEmpty = s.Fuel <= 0f;
            if (Mathf.Abs(v.Fuel.FuelTank.Quantity - s.Fuel) > fuelTol || hostEmpty != v.Fuel.IsEmpty)
            {
                v.Fuel.SetFuel(hostEmpty ? 0f : s.Fuel);
                FuelApplied++;
            }
            if (Mathf.Abs(v.AmmoContainer.AmmoCrate.Quantity - s.Ammo) > 0.0005f)
                v.AmmoContainer.SetAmmo(s.Ammo);
            if (v.EquipmentLockHandler.IsLocked != s.IsEquipmentLocked)
                v.EquipmentLockHandler.SetState(s.IsEquipmentLocked);
            if (v.VehicleState != s.State)
                v.SetState(s.State);
            if (v.UnderCarWorkshop != s.UnderCarWorkshop)
                v.SetUnderCarWorkshop(s.UnderCarWorkshop);
            if (v.AutoDriveState != s.AutoDriveState)
                v.SetAutoDriveState(s.AutoDriveState);
            v.UpdatePointer();
            v.UpdateMaxSpeed();
        }

        private static string LookOf(VehicleSerialized s)
        {
            var sb = new StringBuilder();
            if (s.Modifications != null)
                sb.Append(string.Join(",", s.Modifications.Select(m => ((int)m).ToString(CultureInfo.InvariantCulture)).OrderBy(x => x)));
            sb.Append('|').Append(s.VehicleColor != null ? ColorUtility.ToHtmlStringRGBA(s.VehicleColor.Deserialize()) : "");
            sb.Append('|').Append(s.HasCustomizedColor)
              .Append(s.SerializableCustomizedColor != null ? ColorUtility.ToHtmlStringRGBA(s.SerializableCustomizedColor.Deserialize()) : "")
              .Append(s.HasCamo).Append(s.HasFire).Append(s.HasSkin).Append(s.ShouldShowEmblem);
            return sb.ToString();
        }

        private void CleanupForeign(VehiclesController controller)
        {
            var foreign = new List<Vehicle>();
            foreach (var v in controller.Vehicles)
            {
                if (v == null || string.IsNullOrEmpty(v.Id))
                    continue;
                if (_hostIds.Contains(v.Id))
                {
                    _foreignStrikes.Remove(v.Id);
                    continue;
                }
                // zweimal hintereinander unbekannt: weg (beim ersten Mal kann die Übernahme noch kommen)
                _foreignStrikes.TryGetValue(v.Id, out int strikes);
                _foreignStrikes[v.Id] = ++strikes;
                if (strikes >= 2)
                    foreign.Add(v);
            }
            foreach (var v in foreign)
            {
                _foreignStrikes.Remove(v.Id);
                RemoveVehicle(v, "nur beim Gast");
            }
        }

        private void RemoveVehicle(Vehicle v, string why)
        {
            Plugin.Log.LogInfo($"IFZCOOP vehicle remove {v.Id} ({why})");
            _appliedLook.Remove(v.Id);
            try { v.DestroyVehicle(); }
            catch (Exception e) { Plugin.Log.LogWarning($"Fahrzeug {v.Id} entfernen: {e.Message}"); }
            Removed++;
        }

        /// <summary>Gast, jeden Frame: wer sitzt in welchem Auto (Gruppen können erst nach dem Fahrzeug ankommen).</summary>
        public void GuestFrame()
        {
            if (_session.Role != Role.Guest || !_session.GuestControlsLocked || _hostView.Count == 0)
                return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextReconcile)
                return;
            _nextReconcile = now + ReconcileInterval;
            var controller = GameAccess.Game<VehiclesController>();
            if (controller == null)
                return;
            UnitSync.NetApplying = true;
            try { Reconcile(controller); }
            finally { UnitSync.NetApplying = false; }
        }

        private void Reconcile(VehiclesController controller)
        {
            foreach (var kv in _hostView)
            {
                var v = controller.GetVehicleById(kv.Key);
                if (v == null)
                    continue;
                var want = kv.Value;
                try { ReconcileOne(v, want); }
                catch (Exception e) { Plugin.Log.LogWarning($"Fahrzeug {kv.Key} ein/aus: {e.Message}"); }
            }
        }

        private void ReconcileOne(Vehicle v, HostView want)
        {
            string have = OccupantOf(v);
            if (have == want.Occupant)
            {
                // niemand drin: Besitzer loslassen, wenn der Host keinen mehr hat
                if (want.Occupant.Length == 0 && want.Owner.Length == 0 && v.Owner != null)
                    v.AssignOwner(null);
                if (v.IsWaiting != want.Waiting)
                    v.IsWaiting = want.Waiting;
                return;
            }

            if (have.Length > 0)
            {
                var old = v.Owner;
                bool waiting = want.Waiting && want.Owner == old.Id;
                old.VehicleHandler.UnassignVehicle(isVehicleWaiting: waiting);
                Exited++;
                Plugin.Log.LogInfo($"IFZCOOP vehicle exit {v.Id} group={old.Id} waiting={waiting}");
            }
            if (want.Occupant.Length > 0)
            {
                var group = UnitSync.FindGroupPublic(want.Occupant);
                if (group == null || group.VehicleHandler == null)
                    return; // Gruppe kommt noch; nächster Durchlauf
                if (group.VehicleHandler.Vehicle != null && group.VehicleHandler.Vehicle != v)
                    group.VehicleHandler.UnassignVehicle(isVehicleWaiting: false);
                group.VehicleHandler.AssignVehicle(v, afterLoad: true);
                Entered++;
                Plugin.Log.LogInfo($"IFZCOOP vehicle enter {v.Id} group={group.Id}");
            }
        }

        /// <summary>Für die Statusdatei: Zahl der Fahrzeuge und je Fahrzeug die wichtigsten Werte.</summary>
        public static IEnumerable<string> StatusLines()
        {
            var vehicles = AllVehicles();
            if (vehicles == null)
                yield break;
            var inv = CultureInfo.InvariantCulture;
            var list = vehicles.Where(v => v != null && !string.IsNullOrEmpty(v.Id)).OrderBy(v => v.Id, StringComparer.Ordinal).ToList();
            yield return "vehicles=" + list.Count;
            foreach (var v in list)
            {
                string mods = string.Join(",", v.ModificationHandler.ModificationsDict.Keys.Select(m => m.ToString()).OrderBy(x => x));
                yield return $"vehicle {v.Id} {v.Data.GetVehicleType()} state={v.VehicleState} owner={(v.Owner != null ? v.Owner.Id : "-")} in={OccupantOf(v)} "
                    + $"hp={v.Health.Hp.ToString("0", inv)} fuel={(FuelFraction(v) * 100f).ToString("0", inv)}% ammo={v.AmmoContainer.AmmoCrate.Quantity.ToString("0.00", inv)} "
                    + $"disc={(v.IsDiscovered ? 1 : 0)} mods={mods} pos={v.Position.x.ToString("0", inv)},{v.Position.z.ToString("0", inv)}";
            }
        }
    }
}
