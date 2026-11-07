using System;
using System.Collections.Generic;
using System.IO;
using Controllers;
using Controllers.Dragging.Strategies;
using Gameplay.GameResources;
using Gameplay.Units;
using Gameplay.Units.Characters;
using Gameplay.Units.Player.Workers;
using Gameplay.Vehicles;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Tauschen per Ziehen (Analyse W11; Paket K): Kisten, Waffen, Schutz, Munition, Sprit, Medizin zwischen Trupp-Rucksack,
    /// Figur, Kofferraum, Haus und Lager. Alles läuft im Spiel über RegularDragStrategy (PlaceResource, SwapResource, ApplyEffect).
    ///
    /// Gast: Prefix fängt den Aufruf ab, beschreibt beide Seiten (Art + ID) und schickt sie an den Host; lokal passiert nichts
    /// (sonst doppelt, sobald der Stand vom Host kommt). Seiten, die sich nicht beschreiben lassen (Konvoi), laufen
    /// wie bisher nur lokal.
    /// Host: baut dieselbe RegularDragStrategy mit seinen Behältern und führt sie aus. Das Ergebnis kommt über ContainerSync.
    /// </summary>
    internal sealed class ExchangeSync
    {
        internal enum Op : byte { Place = 0, Swap = 1, Effect = 2 }

        internal enum Kind : byte { None = 0, Squad = 1, Trunk = 2, Building = 3, Stock = 4, Items = 5, Fuel = 6, Ammo = 7, Body = 8, Tile = 9 }

        private static readonly AccessTools.FieldRef<RegularDragStrategy, IDragContainerHandler> FirstField =
            AccessTools.FieldRefAccess<RegularDragStrategy, IDragContainerHandler>("_firstContainer");
        private static readonly AccessTools.FieldRef<RegularDragStrategy, IDragContainerHandler> SecondField =
            AccessTools.FieldRefAccess<RegularDragStrategy, IDragContainerHandler>("_secondContainer");
        private static readonly AccessTools.FieldRef<RegularDragStrategy, IEffectGainer> GainerField =
            AccessTools.FieldRefAccess<RegularDragStrategy, IEffectGainer>("_effectGainer");
        private static readonly AccessTools.FieldRef<RegularDragStrategy, bool> LockedField =
            AccessTools.FieldRefAccess<RegularDragStrategy, bool>("_isDraggingLockedResource");
        private static readonly AccessTools.FieldRef<RegularDragStrategy, bool> OnLockedField =
            AccessTools.FieldRefAccess<RegularDragStrategy, bool>("_isDraggingOnLockedResource");
        private static readonly AccessTools.FieldRef<RegularDragStrategy, bool> DoubleField =
            AccessTools.FieldRefAccess<RegularDragStrategy, bool>("_isDoubleClicking");

        private readonly Session _session;

        public int Sent { get; private set; }
        public int Done { get; private set; }
        public int Local { get; private set; }

        public ExchangeSync(Session session)
        {
            _session = session;
        }

        // ================================================================ Beschreiben / Auflösen

        private static IEnumerable<Character> AllCharacters()
        {
            var squads = GameAccess.Game<SquadsController>()?.Squads;
            if (squads != null)
                foreach (var g in squads)
                    if (g != null)
                        foreach (var c in g.Characters)
                            if (c != null) yield return c;
            var workers = GameAccess.Game<WorkersController>()?.Workers;
            if (workers != null)
                foreach (var c in workers)
                    if (c != null) yield return c;
        }

        internal static bool Describe(object o, out Kind kind, out string id)
        {
            kind = Kind.None;
            id = "";
            if (o == null)
                return false;
            var stock = GameAccess.Game<StockroomsController>();
            if (stock != null && ReferenceEquals(o, stock.Container)) { kind = Kind.Stock; return true; }
            var squads = GameAccess.Game<SquadsController>()?.Squads;
            if (squads != null)
                foreach (var g in squads)
                    if (g != null && ReferenceEquals(o, g.ResourcesContainer)) { kind = Kind.Squad; id = g.Id; return true; }
            var vehicles = GameAccess.Game<VehiclesController>()?.Vehicles;
            if (vehicles != null)
                foreach (var v in vehicles)
                {
                    if (v == null) continue;
                    if (ReferenceEquals(o, v.Trunk)) { kind = Kind.Trunk; id = v.Id; return true; }
                    if (ReferenceEquals(o, v.Fuel)) { kind = Kind.Fuel; id = v.Id; return true; }
                    if (ReferenceEquals(o, v.AmmoContainer)) { kind = Kind.Ammo; id = v.Id; return true; }
                }
            foreach (var c in AllCharacters())
            {
                if (ReferenceEquals(o, c.ItemsContainer)) { kind = Kind.Items; id = c.Id; return true; }
                if (ReferenceEquals(o, c.CharacterDragEffectGainer)) { kind = Kind.Body; id = c.Id; return true; }
            }
            string tileKey = ExpeditionSync.DescribeTile(o);
            if (tileKey != null) { kind = Kind.Tile; id = tileKey; return true; }
            var buildings = GameAccess.Game<BuildingsController>()?.Buildings;
            if (buildings != null && o is ResourcesContainer)
                foreach (var b in buildings)
                    if (b != null && ReferenceEquals(o, b.ResourcesContainer)) { kind = Kind.Building; id = b.ID.ToString(); return true; }
            return false;
        }

        internal static object Resolve(Kind kind, string id)
        {
            switch (kind)
            {
                case Kind.Stock: return GameAccess.Game<StockroomsController>()?.Container;
                case Kind.Squad:
                    var squads = GameAccess.Game<SquadsController>()?.Squads;
                    if (squads != null) foreach (var g in squads) if (g != null && g.Id == id) return g.ResourcesContainer;
                    return null;
                case Kind.Trunk:
                case Kind.Fuel:
                case Kind.Ammo:
                    var v = GameAccess.Game<VehiclesController>()?.GetVehicleById(id);
                    if (v == null) return null;
                    return kind == Kind.Trunk ? (object)v.Trunk : kind == Kind.Fuel ? (object)v.Fuel : v.AmmoContainer;
                case Kind.Items:
                case Kind.Body:
                    foreach (var c in AllCharacters())
                        if (c.Id == id) return kind == Kind.Items ? (object)c.ItemsContainer : c.CharacterDragEffectGainer;
                    return null;
                case Kind.Tile:
                    return ExpeditionSync.ResolveTile(id);
                case Kind.Building:
                    if (!long.TryParse(id, out long bid)) return null;
                    return GameAccess.Game<BuildingsController>()?.GetBuildingById(bid)?.ResourcesContainer;
            }
            return null;
        }

        // ================================================================ Gast

        /// <summary>Prefix der drei RegularDragStrategy-Methoden beim Gast.</summary>
        public bool GuestDrag(RegularDragStrategy s, Op op, ResourceID r1, float q1, ResourceID r2, float q2, bool fromLocked)
        {
            if (_session.Role != Role.Guest || !_session.GuestControlsLocked || UnitSync.NetApplying)
                return true;
            object first = FirstField(s);
            object second = op == Op.Effect ? (object)GainerField(s) : SecondField(s);
            if (!Describe(first, out var k1, out var id1) || !Describe(second, out var k2, out var id2))
            {
                Local++;
                return true;
            }
            bool locked = op == Op.Effect ? fromLocked : LockedField(s);
            _session.SendToHost(Protocol.Build(Msg.DragRequest, w =>
            {
                w.Write((byte)op);
                w.Write((byte)k1); w.Write(id1);
                w.Write((byte)k2); w.Write(id2);
                w.Write((int)r1); w.Write(q1);
                w.Write((int)r2); w.Write(q2);
                w.Write(locked);
                w.Write(OnLockedField(s));
                w.Write(DoubleField(s));
            }));
            Sent++;
            return false;
        }

        // ================================================================ Host

        public void HostOnRequest(Peer from, BinaryReader r)
        {
            var op = (Op)r.ReadByte();
            var k1 = (Kind)r.ReadByte(); string id1 = r.ReadString();
            var k2 = (Kind)r.ReadByte(); string id2 = r.ReadString();
            var r1 = (ResourceID)r.ReadInt32(); float q1 = r.ReadSingle();
            var r2 = (ResourceID)r.ReadInt32(); float q2 = r.ReadSingle();
            bool locked = r.ReadBoolean();
            bool onLocked = r.ReadBoolean();
            bool dbl = r.ReadBoolean();

            var first = Resolve(k1, id1) as IDragContainerHandler;
            var second = Resolve(k2, id2);
            if (first == null || second == null)
            {
                Plugin.Log.LogWarning($"Tausch von {from.Name}: Behälter nicht gefunden ({k1} {id1} -> {k2} {id2})");
                return;
            }
            try
            {
                switch (op)
                {
                    case Op.Place:
                    case Op.Swap:
                        var target = second as IDragContainerHandler;
                        if (target == null) return;
                        var s = new RegularDragStrategy(first, target, new Dictionary<ResourceID, float>(), new Dictionary<ResourceID, float>(),
                            new Dictionary<ResourceID, float>(), new Dictionary<ResourceID, float>(), locked, onLocked, dbl);
                        if (op == Op.Place) s.PlaceResource(r1, q1);
                        else s.SwapResource(r1, q1, r2, q2);
                        break;
                    case Op.Effect:
                        var gainer = second as IEffectGainer;
                        if (gainer == null) return;
                        new RegularDragStrategy(first, gainer).ApplyEffect(r1, q1, locked);
                        break;
                }
                Done++;
                Plugin.Log.LogInfo($"IFZCOOP drag {op} {r1} {q1:0.##} {k1} {id1} -> {k2} {id2} (von {from.Name})");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Tausch von {from.Name}: {e}");
            }
        }

        public string StatusLine() => $"drag sent={Sent} done={Done} local={Local}";
    }
}
