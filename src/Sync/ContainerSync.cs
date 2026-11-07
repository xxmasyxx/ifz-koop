using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Controllers;
using Gameplay.Buildings;
using Gameplay.GameResources;
using Gameplay.InGameResources;
using Gameplay.Units;
using Gameplay.Units.Characters;
using Gameplay.Vehicles;
using IfzCoop.Game;
using IfzCoop.Net;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Behälter und Ausrüstung (Analyse P1, P2 Hausinhalt, P3). Beim Gast tun die Marionetten nichts, also füllt sich
    /// dort weder ein Trupp-Rucksack noch ein Haus, und Munition/Schutz bleiben auf dem Stand beim Laden.
    ///
    /// Host prüft 2×/s Trupp-Rucksäcke (Group.ResourcesContainer), Kofferräume (Vehicle.Trunk) und Ausrüstung jeder
    /// Figur eigener Trupps (ItemsContainer: Waffe, Schutz + Haltbarkeit, Munition), 1×/s den Inhalt der Häuser
    /// (Building.ResourcesContainer, die Rohstoff-Symbole über dem Dach). Geschickt wird nur, was sich geändert hat;
    /// nach dem Laden eines Gastes alles.
    /// Gast: Behälter leeren und mit dem Stand des Hosts füllen, Ausrüstung setzen. Was beim Gast selbst Kisten finden,
    /// fallen lassen, abgeben oder auffüllen würde, ist gesperrt (UnitPatches).
    /// </summary>
    internal sealed class ContainerSync
    {
        private const float FastInterval = 0.5f;
        private const float BuildingInterval = 1f;
        private const int MaxPerMessage = 200;

        // BuildingIndex: kein Behälter, sondern die Liste aller Häuser, die beim Host etwas enthalten (Id = Komma-Liste).
        // Der Gast leert jedes andere Haus. Nötig nach ResendAll: leere Häuser schickt der Host sonst nie, ein beim Gast
        // hängengebliebener Inhalt (z. B. nach dem Plündern des HQ oder einem abgerissenen Haus) bliebe für immer stehen.
        private enum Kind : byte { Squad = 0, Trunk = 1, Building = 2, BuildingIndex = 3 }

        private readonly Session _session;
        private readonly Dictionary<string, string> _sentContainers = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _sentItems = new Dictionary<string, string>();
        private float _nextFast;
        private float _nextBuildings;
        private bool _fullBuildings = true;

        public int ContainersApplied { get; private set; }
        public int ItemsApplied { get; private set; }

        public ContainerSync(Session session)
        {
            _session = session;
        }

        public void ResendAll()
        {
            _sentContainers.Clear();
            _sentItems.Clear();
            _fullBuildings = true;
        }

        public int BuildingsCleared { get; private set; }

        public void Reset() => ResendAll();

        // ================================================================ Host

        private sealed class ContainerEntry
        {
            public Kind Kind;
            public string Id;
            public ResourcesContainer Container;
        }

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (_session.ActiveGuestCount == 0 || !GameAccess.IsGameStarted)
                return;

            if (now >= _nextFast)
            {
                _nextFast = now + FastInterval;
                var changed = new List<ContainerEntry>();
                var squads = GameAccess.Game<SquadsController>()?.Squads;
                if (squads != null)
                    foreach (var g in squads)
                        if (g != null && !string.IsNullOrEmpty(g.Id))
                            CheckContainer(changed, Kind.Squad, g.Id, g.ResourcesContainer);
                var vehicles = GameAccess.Game<VehiclesController>()?.Vehicles;
                if (vehicles != null)
                    foreach (var v in vehicles)
                        if (v != null && !string.IsNullOrEmpty(v.Id) && v.Trunk != null)
                            CheckContainer(changed, Kind.Trunk, v.Id, v.Trunk);
                SendContainers(changed);
                SendItems(squads);
            }

            if (now >= _nextBuildings)
            {
                _nextBuildings = now + BuildingInterval;
                var changed = new List<ContainerEntry>();
                var buildings = GameAccess.Game<BuildingsController>()?.Buildings;
                var mainStock = GameAccess.Game<StockroomsController>()?.Container;
                var nonEmpty = _fullBuildings ? new List<string>() : null;
                if (buildings != null)
                    foreach (var b in buildings)
                    {
                        if (b == null || b.ResourcesContainer == null)
                            continue;
                        // Lagerhaus: ResourcesContainer ist das gemeinsame Lager (StockSync), nicht das Haus
                        if (mainStock != null && ReferenceEquals(b.ResourcesContainer, mainStock))
                            continue;
                        string key = "b" + b.ID;
                        // leere Häuser nur, wenn sie vorher etwas hatten
                        if (b.ResourcesContainer.Resources.Count == 0 && !_sentContainers.ContainsKey(key))
                            continue;
                        string bid = b.ID.ToString(CultureInfo.InvariantCulture);
                        if (nonEmpty != null && Signature(b.ResourcesContainer) != EmptySignature)
                            nonEmpty.Add(bid);
                        CheckContainer(changed, Kind.Building, bid, b.ResourcesContainer);
                    }
                SendContainers(changed);
                if (nonEmpty != null && buildings != null)
                {
                    _fullBuildings = false;
                    string index = string.Join(",", nonEmpty);
                    _session.BroadcastToGuests(Protocol.Build(Msg.ContainerState, w =>
                    {
                        w.Write(1);
                        w.Write((byte)Kind.BuildingIndex);
                        w.Write(index);
                        w.Write(0);
                        w.Write(0); // keine Rohstoffe
                        w.Write(0); // keine gesperrten
                    }));
                }
            }
        }

        private void CheckContainer(List<ContainerEntry> changed, Kind kind, string id, ResourcesContainer c)
        {
            string key = KeyOf(kind, id);
            string sig = Signature(c);
            if (_sentContainers.TryGetValue(key, out var last) && last == sig)
                return;
            if (last == null && kind == Kind.Building && sig == EmptySignature)
                return;
            _sentContainers[key] = sig;
            changed.Add(new ContainerEntry { Kind = kind, Id = id, Container = c });
        }

        private static string KeyOf(Kind kind, string id) => (kind == Kind.Squad ? "s" : kind == Kind.Trunk ? "v" : "b") + id;

        private const string EmptySignature = "|";

        private static string Signature(ResourcesContainer c)
        {
            var sb = new StringBuilder();
            foreach (var kv in c.Resources)
                if (Mathf.Abs(kv.Value) > 0.0001f)
                    sb.Append((int)kv.Key).Append(':').Append(kv.Value.ToString("0.###", CultureInfo.InvariantCulture)).Append(',');
            sb.Append('|');
            foreach (var kv in c.LockedResources)
                if (kv.Value != 0)
                    sb.Append((int)kv.Key).Append(':').Append(kv.Value).Append(',');
            return sb.ToString();
        }

        private void SendContainers(List<ContainerEntry> changed)
        {
            for (int start = 0; start < changed.Count; start += MaxPerMessage)
            {
                int count = Math.Min(MaxPerMessage, changed.Count - start);
                int first = start;
                _session.BroadcastToGuests(Protocol.Build(Msg.ContainerState, w =>
                {
                    w.Write(count);
                    for (int i = first; i < first + count; i++)
                    {
                        var e = changed[i];
                        w.Write((byte)e.Kind);
                        w.Write(e.Id);
                        w.Write(e.Container.Capacity);
                        WriteQuantities(w, e.Container.ConvertToResourceQuantityList());
                        WriteQuantities(w, e.Container.LockedResourcesToList());
                    }
                }));
            }
        }

        private void SendItems(IReadOnlyList<Group> squads)
        {
            if (squads == null)
                return;
            var changed = new List<Character>();
            foreach (var g in squads)
            {
                if (g == null)
                    continue;
                foreach (var c in g.Characters)
                {
                    if (c == null || string.IsNullOrEmpty(c.Id) || c.ItemsContainer == null)
                        continue;
                    string sig = ItemsSignature(c.ItemsContainer.GetInfo());
                    if (_sentItems.TryGetValue(c.Id, out var last) && last == sig)
                        continue;
                    _sentItems[c.Id] = sig;
                    changed.Add(c);
                }
            }
            if (changed.Count == 0)
                return;
            _session.BroadcastToGuests(Protocol.Build(Msg.ItemsState, w =>
            {
                w.Write(changed.Count);
                foreach (var c in changed)
                {
                    var info = c.ItemsContainer.GetInfo();
                    w.Write(c.Id);
                    w.Write((int)info.Weapon);
                    w.Write((int)info.Gear);
                    w.Write(float.IsNaN(info.GearQuantity) ? 0f : info.GearQuantity);
                    w.Write(info.Ammo);
                }
            }));
        }

        // Munition (in Kisten) und Schutz auf 1/20 gerundet: sonst ginge im Kampf jede Kugel einzeln raus.
        private static string ItemsSignature(ItemsContainer.ItemsContainerInfo i) =>
            $"{(int)i.Weapon}|{(int)i.Gear}|{Mathf.RoundToInt((float.IsNaN(i.GearQuantity) ? 0f : i.GearQuantity) * 20f)}|{Mathf.RoundToInt(i.Ammo * 20f)}";

        // ================================================================ Gast

        public void GuestOnContainers(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > MaxPerMessage)
                throw new InvalidDataException("zu viele Behälter");
            UnitSync.NetApplying = true;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    var kind = (Kind)r.ReadByte();
                    string id = r.ReadString();
                    int capacity = r.ReadInt32();
                    var resources = ReadQuantities(r);
                    var locked = ReadQuantities(r);
                    try
                    {
                        ApplyContainer(kind, id, capacity, resources, locked);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"Behälter {kind} {id}: {e.Message}");
                    }
                }
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        private void ApplyContainer(Kind kind, string id, int capacity, List<ResourceQuantity> resources, List<ResourceQuantity> locked)
        {
            ResourcesContainer c = null;
            Building building = null;
            if (kind == Kind.BuildingIndex)
            {
                ClearOtherBuildings(id);
                return;
            }
            switch (kind)
            {
                case Kind.Squad:
                    c = UnitSync.FindGroupPublic(id)?.ResourcesContainer;
                    break;
                case Kind.Trunk:
                    var vehicles = GameAccess.Game<VehiclesController>()?.Vehicles;
                    if (vehicles != null)
                        foreach (var v in vehicles)
                            if (v != null && v.Id == id) { c = v.Trunk; break; }
                    break;
                case Kind.Building:
                    if (long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bid))
                    {
                        building = GameAccess.Game<BuildingsController>()?.GetBuildingById(bid);
                        c = building != null ? building.ResourcesContainer : null;
                    }
                    break;
            }
            if (c == null)
                return;
            if (ReferenceEquals(c, GameAccess.Game<StockroomsController>()?.Container))
                return; // Lagerhaus: das Lager kommt nur über StockSync
            if (c.Capacity < capacity)
                c.SetCapacity(capacity, force: true);
            c.Clear();
            c.LoadResources(resources);
            c.LoadLockedResources(locked);
            c.InvokeResourcesUpdated();
            if (building != null)
                building.RefreshAllResourceIcons();
            ContainersApplied++;
        }

        /// <summary>Gast: jedes Haus leeren, das beim Host nichts enthält (Liste der Häuser mit Inhalt vom Host).</summary>
        private void ClearOtherBuildings(string index)
        {
            var keep = new HashSet<string>(index.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            var buildings = GameAccess.Game<BuildingsController>()?.Buildings;
            if (buildings == null)
                return;
            var mainStock = GameAccess.Game<StockroomsController>()?.Container;
            int cleared = 0;
            foreach (var b in buildings)
            {
                var c = b?.ResourcesContainer;
                if (c == null || c.Resources.Count == 0 || (mainStock != null && ReferenceEquals(c, mainStock)))
                    continue;
                if (keep.Contains(b.ID.ToString(CultureInfo.InvariantCulture)) || Signature(c) == EmptySignature)
                    continue;
                c.Clear();
                c.LoadLockedResources(new List<ResourceQuantity>());
                c.InvokeResourcesUpdated();
                b.RefreshAllResourceIcons();
                cleared++;
            }
            BuildingsCleared += cleared;
            if (cleared > 0)
                Plugin.Log.LogInfo($"IFZCOOP Behälter: {cleared} Häuser geleert, die beim Host leer sind");
        }

        /// <summary>QA (nur Gast, nur zum Test): ein leeres Haus mit 3 Holz füllen, das der Host nicht hat.</summary>
        public static string QaDirty()
        {
            var buildings = GameAccess.Game<BuildingsController>()?.Buildings;
            var mainStock = GameAccess.Game<StockroomsController>()?.Container;
            if (buildings == null)
                return "keine Häuser";
            foreach (var b in buildings)
            {
                var c = b?.ResourcesContainer;
                if (c == null || (mainStock != null && ReferenceEquals(c, mainStock)) || Signature(c) != EmptySignature || c.Capacity < 3)
                    continue;
                c.LoadResources(new List<ResourceQuantity> { new ResourceQuantity(ResourceID.res_wood, 3f) });
                c.InvokeResourcesUpdated();
                return $"bdirty {b.ID} -> {Signature(c)}";
            }
            return "kein passendes Haus";
        }

        public void GuestOnItems(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 10000)
                throw new InvalidDataException("zu viele Figuren");
            var byId = new Dictionary<string, Character>();
            var squads = GameAccess.Game<SquadsController>()?.Squads;
            if (squads != null)
                foreach (var g in squads)
                    if (g != null)
                        foreach (var c in g.Characters)
                            if (c != null && !string.IsNullOrEmpty(c.Id)) byId[c.Id] = c;

            UnitSync.NetApplying = true;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    string id = r.ReadString();
                    var info = new ItemsContainer.ItemsContainerInfo
                    {
                        Weapon = (ResourceID)r.ReadInt32(),
                        Gear = (ResourceID)r.ReadInt32(),
                        GearQuantity = r.ReadSingle(),
                        Ammo = r.ReadSingle(),
                    };
                    if (!byId.TryGetValue(id, out var c) || c.ItemsContainer == null)
                        continue;
                    try
                    {
                        ApplyItems(c, info);
                        ItemsApplied++;
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"Ausrüstung {id}: {e.Message}");
                    }
                }
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        private static void ApplyItems(Character c, ItemsContainer.ItemsContainerInfo want)
        {
            var items = c.ItemsContainer;
            var have = items.GetInfo();
            if (want.Weapon != ResourceID.None && want.Weapon != have.Weapon)
            {
                items.WeaponHandler.TryAssignWeapon(want.Weapon, dropPreviousWeapon: false);
                c.CharacterTextureRandomizer?.SetWeaponAppearance(want.Weapon);
            }
            if (want.Gear != ResourceID.None
                && (want.Gear != have.Gear || Mathf.Abs(want.GearQuantity - have.GearQuantity) > 0.05f))
                items.GearHandler.AssignGear(want.Gear, want.GearQuantity, dropPreviousGear: false);
            var ammo = items.AmmunitionHandler?.Ammunition;
            if (ammo != null && Mathf.Abs(ammo.Quantity - want.Ammo) > 0.01f)
                ammo.SetQuantity(want.Ammo);
        }

        // ================================================================ Format

        private static void WriteQuantities(BinaryWriter w, List<ResourceQuantity> list)
        {
            int count = 0;
            foreach (var q in list) if (q != null) count++;
            w.Write(count);
            foreach (var q in list)
            {
                if (q == null)
                    continue;
                w.Write((int)q.resourceType);
                w.Write(q.value);
            }
        }

        private static List<ResourceQuantity> ReadQuantities(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 1000)
                throw new InvalidDataException("zu viele Rohstoffe");
            var list = new List<ResourceQuantity>(n);
            for (int i = 0; i < n; i++)
                list.Add(new ResourceQuantity((ResourceID)r.ReadInt32(), r.ReadSingle()));
            return list;
        }

        /// <summary>Für die Statusdatei: Rucksack und Ausrüstung je Trupp.</summary>
        public static IEnumerable<string> StatusLines()
        {
            var squads = GameAccess.Game<SquadsController>()?.Squads;
            if (squads == null)
                yield break;
            foreach (var g in squads)
            {
                if (g == null || g.Characters.Count == 0)
                    continue;
                var items = new List<string>();
                foreach (var c in g.Characters)
                {
                    if (c?.ItemsContainer == null)
                        continue;
                    var i = c.ItemsContainer.GetInfo();
                    items.Add($"{i.Weapon}/{i.Gear}/{i.Ammo.ToString("0.00", CultureInfo.InvariantCulture)}");
                }
                yield return $"pack {g.Id} {Signature(g.ResourcesContainer)} items={string.Join(",", items)}";
            }
        }

        /// <summary>Für die Statusdatei: Zahl der Häuser mit Inhalt und eine Prüfsumme darüber.</summary>
        public static string BuildingsStatus()
        {
            var buildings = GameAccess.Game<BuildingsController>()?.Buildings;
            if (buildings == null)
                return "houses=-";
            int count = 0;
            long sum = 0;
            var mainStock = GameAccess.Game<StockroomsController>()?.Container;
            foreach (var b in buildings)
            {
                if (b?.ResourcesContainer == null || b.ResourcesContainer.Resources.Count == 0)
                    continue;
                if (mainStock != null && ReferenceEquals(b.ResourcesContainer, mainStock))
                    continue;
                string sig = Signature(b.ResourcesContainer);
                if (sig == EmptySignature)
                    continue;
                count++;
                uint h = 2166136261u; // FNV-1a, gleich auf beiden Rechnern
                foreach (char ch in b.ID + sig) h = (h ^ ch) * 16777619u;
                sum += h;
            }
            return $"houses={count} housesHash={sum}";
        }
    }
}
