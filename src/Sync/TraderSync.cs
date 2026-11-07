using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Controllers.Dragging.Strategies;
using Fractions;
using Gameplay.GameResources;
using Gameplay.InGameResources;
using Gameplay.Trading;
using Gameplay.Units;
using Gameplay.Units.Enemy;
using Gameplay.Vehicles;
using Gameplay.Vendor;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using Trade;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Händler (Analyse W11, Paket K). Im Spiel: VendorController zählt die Abklingzeit (NextVendorTimeInHours, je Spielstunde
    /// OnHourChanged) und feuert dann VendorCooldownFinishSignal; ein Ereignis lässt die Händler-Gruppe (Fraktion Vendor, meist
    /// mit Fahrzeug) erscheinen und füllt ihren Kofferraum (FillResourcesContainer, Zufall). VendorBehavior fährt je nach
    /// VendorState zum HQ (VendorReachedHqSignal → Ereignis StartTrade) oder zum Kartenrand. Das Tauschfenster ist
    /// Trade.TradingController (Vorrat Spieler = Lager, Vorrat Händler = Group.ResourcesToTrade, zwei Angebots-Behälter,
    /// Annehmen/Abbrechen/Drohen). Händler-Gruppe und -Fahrzeug selbst kommen schon über UnitSync/VehicleSync, der
    /// Kofferraum zusätzlich über ContainerSync.
    ///
    /// Host: 1×/s (bei offenem Fenster 5×/s) der Stand: VendorController (Abklingzeit, letzte Angebots-Art, VendorState,
    /// Liefer-Listen), Vorrat der Fraktion Vendor und Player.BrokerResources, Handelsvorrat jeder Händler-Gruppe und das
    /// Tauschfenster (offen, Vorhandel, Handelspartner, beide Angebote). Gesendet nur bei Änderung, sonst alle 10 s.
    /// Gast: eigene Abklingzeit, Füllen und Liefer-Listen gesperrt (UnitPatches). Er übernimmt den Stand, öffnet/schließt
    /// das Tauschfenster wie der Host und zeigt dieselben Angebote. Ziehen im Fenster (RegularDragStrategy zwischen den vier
    /// Feldern) und die Knöpfe gehen als Wunsch an den Host; das Lager kommt über StockSync zurück.
    /// </summary>
    internal sealed class TraderSync
    {
        private const float SendInterval = 1f;
        private const float TradeInterval = 0.2f;
        private const float ForceInterval = 10f;
        private const float GuestRetryInterval = 1f;

        public enum Op : byte { Accept = 1, Cancel = 2, Threaten = 3, Place = 4, Swap = 5 }

        /// <summary>Die vier Felder des Tauschfensters.</summary>
        private enum Side : byte { PlayerStock = 0, PlayerOffer = 1, VendorStock = 2, VendorOffer = 3 }

        // ================================================================ Zugriff auf private Felder

        private static readonly AccessTools.FieldRef<TradingController, ResourcesContainer> PlayerOfferField =
            AccessTools.FieldRefAccess<TradingController, ResourcesContainer>("_playerOffer");
        private static readonly AccessTools.FieldRef<TradingController, ResourcesContainer> VendorOfferField =
            AccessTools.FieldRefAccess<TradingController, ResourcesContainer>("_vendorOffer");
        private static readonly AccessTools.FieldRef<TradingController, ITrader> CurPlayerField =
            AccessTools.FieldRefAccess<TradingController, ITrader>("_currentPlayer");
        private static readonly AccessTools.FieldRef<TradingController, ITrader> CurContractorField =
            AccessTools.FieldRefAccess<TradingController, ITrader>("_currentContractor");
        private static readonly AccessTools.FieldRef<TradingController, ITradeBroker> PlayerBrokerField =
            AccessTools.FieldRefAccess<TradingController, ITradeBroker>("_currentPlayerBroker");
        private static readonly AccessTools.FieldRef<TradingController, ITradeBroker> ContractorBrokerField =
            AccessTools.FieldRefAccess<TradingController, ITradeBroker>("_currentContractorBroker");
        private static readonly MethodInfo StopTradeMethod = AccessTools.Method(typeof(TradingController), "StopTrade");
        private static readonly MethodInfo OnResourcesUpdatedMethod = AccessTools.Method(typeof(TradingController), "OnResourcesUpdated");

        private static readonly AccessTools.FieldRef<RegularDragStrategy, IDragContainerHandler> FirstField =
            AccessTools.FieldRefAccess<RegularDragStrategy, IDragContainerHandler>("_firstContainer");
        private static readonly AccessTools.FieldRef<RegularDragStrategy, IDragContainerHandler> SecondField =
            AccessTools.FieldRefAccess<RegularDragStrategy, IDragContainerHandler>("_secondContainer");
        private static readonly AccessTools.FieldRef<RegularDragStrategy, bool> LockedField =
            AccessTools.FieldRefAccess<RegularDragStrategy, bool>("_isDraggingLockedResource");
        private static readonly AccessTools.FieldRef<RegularDragStrategy, bool> OnLockedField =
            AccessTools.FieldRefAccess<RegularDragStrategy, bool>("_isDraggingOnLockedResource");
        private static readonly AccessTools.FieldRef<RegularDragStrategy, bool> DoubleField =
            AccessTools.FieldRefAccess<RegularDragStrategy, bool>("_isDoubleClicking");

        private static readonly MethodInfo SetNextMethod = AccessTools.PropertySetter(typeof(VendorController), "NextVendorTimeInHours");
        private static readonly MethodInfo SetLastMethod = AccessTools.PropertySetter(typeof(VendorController), "LastOfferType");
        private static readonly MethodInfo SetSupplyMethod = AccessTools.PropertySetter(typeof(VendorController), "FractionSupplyResources");

        private static VendorController Vc => GameAccess.Game<VendorController>();
        private static TradingController Tc => GameAccess.Game<TradingController>();
        private static FractionsManager Fm => GameAccess.Game<FractionsManager>();

        // ================================================================ Stand

        private sealed class Box
        {
            public int Capacity;
            public List<ResourceQuantity> Resources = new List<ResourceQuantity>();
            public List<ResourceQuantity> Locked = new List<ResourceQuantity>();
        }

        private struct Ref
        {
            public byte Kind;   // 0 nichts, 1 Fraktion (Id = Zahl), 2 Gruppe (Id = Gruppen-ID)
            public string Id;
        }

        private sealed class State
        {
            public bool HasVc;
            public float Next;
            public OfferType Last;
            public VendorState VState;
            public Dictionary<Fraction, List<ResourceID>> Supply = new Dictionary<Fraction, List<ResourceID>>();
            public Box VendorFraction;
            public Box PlayerBroker;
            public List<KeyValuePair<string, Box>> Groups = new List<KeyValuePair<string, Box>>();
            public bool Active;
            public bool Pre;
            public Ref Contractor, Player, ContractorBroker, PlayerBroker2;
            public Box PlayerOffer, VendorOffer;
        }

        private readonly Session _session;
        private byte[] _lastSent;
        private float _nextSend;
        private float _nextForce;
        private State _last;
        private float _nextRetry;

        public int Applied { get; private set; }
        public int Opened { get; private set; }
        public int Closed { get; private set; }
        public int Sent { get; private set; }
        public int Done { get; private set; }
        public int Missing { get; private set; }

        public TraderSync(Session session)
        {
            _session = session;
        }

        public void ResendAll()
        {
            _lastSent = null;
            _nextSend = 0f;
        }

        public void Reset()
        {
            ResendAll();
            _last = null;
        }

        // ================================================================ Host

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend || _session.ActiveGuestCount == 0 || !GameAccess.IsGameStarted)
                return;
            var tc = Tc;
            _nextSend = now + (tc != null && tc.Active ? TradeInterval : SendInterval);
            bool force = now >= _nextForce;
            if (force) _nextForce = now + ForceInterval;

            byte[] msg;
            try { msg = BuildState(tc); }
            catch (Exception e) { Plugin.Log.LogDebug("Händler-Stand: " + e.Message); return; }
            if (!force && _lastSent != null && _lastSent.SequenceEqual(msg))
                return;
            _lastSent = msg;
            _session.BroadcastToGuests(msg);
        }

        /// <summary>Gruppen, deren Handelsvorrat mitgeht: alle der Fraktion Vendor, dazu der aktuelle Handelspartner.</summary>
        private static List<Group> TraderGroups(TradingController tc)
        {
            var list = new List<Group>();
            var groups = GameAccess.Game<GroupsController>()?.Groups;
            if (groups != null)
                foreach (var g in groups)
                    if (g != null && g.Fraction == Fraction.Vendor && !string.IsNullOrEmpty(g.Id) && !g.IsEmpty())
                        list.Add(g);
            if (tc != null && tc.Active)
            {
                if (CurContractorField(tc) is Group cg && !string.IsNullOrEmpty(cg.Id) && !list.Contains(cg)) list.Add(cg);
                if (ContractorBrokerField(tc) is Group bg && !string.IsNullOrEmpty(bg.Id) && !list.Contains(bg)) list.Add(bg);
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return list;
        }

        private static byte[] BuildState(TradingController tc)
        {
            var vc = Vc;
            var fm = Fm;
            var groups = TraderGroups(tc);
            return Protocol.Build(Msg.TraderState, w =>
            {
                w.Write(vc != null);
                if (vc != null)
                {
                    w.Write(vc.NextVendorTimeInHours);
                    w.Write((int)vc.LastOfferType);
                    w.Write((int)vc.VendorState);
                    var sup = vc.FractionSupplyResources ?? new Dictionary<Fraction, List<ResourceID>>();
                    w.Write(sup.Count);
                    foreach (var kv in sup.OrderBy(k => (int)k.Key))
                    {
                        w.Write((int)kv.Key);
                        var l = kv.Value ?? new List<ResourceID>();
                        w.Write(l.Count);
                        foreach (var r in l) w.Write((int)r);
                    }
                }
                WriteBox(w, fm?.Vendor?.ResourcesToTrade);
                WriteBox(w, fm?.Player?.BrokerResources);
                w.Write(groups.Count);
                foreach (var g in groups)
                {
                    w.Write(g.Id);
                    WriteBox(w, g.ResourcesToTrade);
                }
                bool active = tc != null && tc.Active;
                w.Write(active);
                if (active)
                {
                    w.Write(tc.PreTrade);
                    WriteRef(w, CurContractorField(tc));
                    WriteRef(w, CurPlayerField(tc));
                    WriteRef(w, ContractorBrokerField(tc));
                    WriteRef(w, PlayerBrokerField(tc));
                    WriteBox(w, PlayerOfferField(tc));
                    WriteBox(w, VendorOfferField(tc));
                }
            });
        }

        public void HostOnRequest(Peer from, BinaryReader r)
        {
            var op = (Op)r.ReadByte();
            var cref = ReadRef(r);
            Side s1 = 0, s2 = 0;
            ResourceID r1 = default, r2 = default;
            float q1 = 0f, q2 = 0f;
            bool locked = false, onLocked = false, dbl = false;
            if (op == Op.Place || op == Op.Swap)
            {
                s1 = (Side)r.ReadByte(); s2 = (Side)r.ReadByte();
                r1 = (ResourceID)r.ReadInt32(); q1 = r.ReadSingle();
                r2 = (ResourceID)r.ReadInt32(); q2 = r.ReadSingle();
                locked = r.ReadBoolean(); onLocked = r.ReadBoolean(); dbl = r.ReadBoolean();
            }

            var tc = Tc;
            if (tc == null || !tc.Active)
            {
                Plugin.Log.LogWarning($"Handel von {from.Name}: kein Tauschfenster offen ({op})");
                return;
            }
            if (!SameRef(CurContractorField(tc), cref))
            {
                Plugin.Log.LogWarning($"Handel von {from.Name}: anderer Handelspartner ({cref.Kind} {cref.Id})");
                return;
            }
            try
            {
                switch (op)
                {
                    case Op.Accept: tc.AcceptOffer(); break;
                    case Op.Cancel: tc.CancelOffer(); break;
                    case Op.Threaten: tc.ThreatenOffer(); break;
                    case Op.Place:
                    case Op.Swap:
                        var a = SideContainer(tc, s1);
                        var b = SideContainer(tc, s2);
                        if (a == null || b == null) return;
                        var st = new RegularDragStrategy(a, b, new Dictionary<ResourceID, float>(), new Dictionary<ResourceID, float>(),
                            new Dictionary<ResourceID, float>(), new Dictionary<ResourceID, float>(), locked, onLocked, dbl);
                        if (op == Op.Place) st.PlaceResource(r1, q1);
                        else st.SwapResource(r1, q1, r2, q2);
                        break;
                    default: return;
                }
                Done++;
                _nextSend = 0f;
                _session.Stock?.ResendAll();
                Plugin.Log.LogInfo($"IFZCOOP trade {op} {s1}->{s2} {r1} {q1:0.##} (von {from.Name}) offen={tc.Active}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Handel von {from.Name}: {e}");
            }
        }

        // ================================================================ Gast

        public void GuestOnState(BinaryReader r)
        {
            var s = new State();
            s.HasVc = r.ReadBoolean();
            if (s.HasVc)
            {
                s.Next = r.ReadSingle();
                s.Last = (OfferType)r.ReadInt32();
                s.VState = (VendorState)r.ReadInt32();
                int nf = r.ReadInt32();
                if (nf < 0 || nf > 100) throw new InvalidDataException("zu viele Fraktionen");
                for (int i = 0; i < nf; i++)
                {
                    var f = (Fraction)r.ReadInt32();
                    int n = r.ReadInt32();
                    if (n < 0 || n > 1000) throw new InvalidDataException("zu viele Lieferungen");
                    var l = new List<ResourceID>(n);
                    for (int j = 0; j < n; j++) l.Add((ResourceID)r.ReadInt32());
                    s.Supply[f] = l;
                }
            }
            s.VendorFraction = ReadBox(r);
            s.PlayerBroker = ReadBox(r);
            int ng = r.ReadInt32();
            if (ng < 0 || ng > 1000) throw new InvalidDataException("zu viele Händler");
            for (int i = 0; i < ng; i++)
            {
                string id = r.ReadString();
                s.Groups.Add(new KeyValuePair<string, Box>(id, ReadBox(r)));
            }
            s.Active = r.ReadBoolean();
            if (s.Active)
            {
                s.Pre = r.ReadBoolean();
                s.Contractor = ReadRef(r);
                s.Player = ReadRef(r);
                s.ContractorBroker = ReadRef(r);
                s.PlayerBroker2 = ReadRef(r);
                s.PlayerOffer = ReadBox(r);
                s.VendorOffer = ReadBox(r);
            }
            _last = s;

            UnitSync.NetApplying = true;
            try
            {
                ApplyVendorController(s);
                var fm = Fm;
                ApplyBox(fm?.Vendor?.ResourcesToTrade, s.VendorFraction);
                ApplyBox(fm?.Player?.BrokerResources, s.PlayerBroker);
                foreach (var kv in s.Groups)
                {
                    var g = UnitSync.FindGroupPublic(kv.Key);
                    if (g == null) { Missing++; continue; }
                    ApplyBox(g.ResourcesToTrade, kv.Value);
                }
                ApplyTrade(s);
                Applied++;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Händler-Stand anwenden: " + e.Message);
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        /// <summary>Gast, 1×/s: Fenster nachziehen, falls es beim Eintreffen noch nicht ging (Gruppe fehlte).</summary>
        public void GuestFrame()
        {
            if (_last == null || !_session.GuestControlsLocked)
                return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextRetry)
                return;
            _nextRetry = now + GuestRetryInterval;
            var tc = Tc;
            if (tc == null || tc.Active == _last.Active)
                return;
            UnitSync.NetApplying = true;
            try { ApplyTrade(_last); }
            catch (Exception e) { Plugin.Log.LogDebug("Tauschfenster nachziehen: " + e.Message); }
            finally { UnitSync.NetApplying = false; }
        }

        private static void ApplyVendorController(State s)
        {
            var vc = Vc;
            if (vc == null || !s.HasVc)
                return;
            if (vc.NextVendorTimeInHours != s.Next) SetNextMethod?.Invoke(vc, new object[] { s.Next });
            if (vc.LastOfferType != s.Last) SetLastMethod?.Invoke(vc, new object[] { s.Last });
            if (vc.VendorState != s.VState) vc.VendorState = s.VState;
            if (SupplySig(vc.FractionSupplyResources) != SupplySig(s.Supply))
                SetSupplyMethod?.Invoke(vc, new object[] { s.Supply });
        }

        private void ApplyTrade(State s)
        {
            var tc = Tc;
            if (tc == null)
                return;
            if (!s.Active)
            {
                if (tc.Active) CloseLocal(tc);
                return;
            }
            var contractor = ResolveRef(s.Contractor) as ITrader;
            var player = ResolveRef(s.Player) as ITrader;
            ITradeBroker cb = null, pb = null;
            if (s.Pre)
            {
                cb = ResolveRef(s.ContractorBroker) as ITradeBroker;
                pb = ResolveRef(s.PlayerBroker2) as ITradeBroker;
            }
            if (contractor == null || player == null || (s.Pre && (cb == null || pb == null)))
            {
                Missing++;
                return; // GuestFrame versucht es wieder
            }
            if (tc.Active && (!ReferenceEquals(CurContractorField(tc), contractor) || !ReferenceEquals(CurPlayerField(tc), player) || tc.PreTrade != s.Pre))
                CloseLocal(tc);
            if (!tc.Active)
            {
                if (s.Pre) tc.StartPreTrade(contractor, player, cb, pb);
                else tc.StartTrade(contractor, player);
                Opened++;
                Plugin.Log.LogInfo($"IFZCOOP trade Fenster offen wie beim Host ({s.Contractor.Kind} {s.Contractor.Id})");
            }
            ApplyBox(PlayerOfferField(tc), s.PlayerOffer);
            ApplyBox(VendorOfferField(tc), s.VendorOffer);
        }

        /// <summary>Fenster beim Gast schließen, ohne Rohstoffe zu bewegen (das Ergebnis kommt vom Host).</summary>
        private void CloseLocal(TradingController tc)
        {
            try
            {
                var handler = (Action)Delegate.CreateDelegate(typeof(Action), tc, OnResourcesUpdatedMethod);
                var p = CurPlayerField(tc);
                var c = CurContractorField(tc);
                if (p?.ResourcesToTrade != null) p.ResourcesToTrade.OnResourcesUpdated -= handler;
                if (c?.ResourcesToTrade != null) c.ResourcesToTrade.OnResourcesUpdated -= handler;
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("Tauschfenster abmelden: " + e.Message);
            }
            PlayerOfferField(tc).Clear();
            VendorOfferField(tc).Clear();
            StopTradeMethod.Invoke(tc, null);
            Closed++;
            Plugin.Log.LogInfo("IFZCOOP trade Fenster zu wie beim Host");
        }

        private bool GuestLocked => _session.Role == Role.Guest && _session.GuestControlsLocked && !UnitSync.NetApplying;

        /// <summary>Prefix von TradingController.AcceptOffer/CancelOffer/ThreatenOffer beim Gast.</summary>
        public bool GuestButton(Op op)
        {
            if (!GuestLocked)
                return true;
            var tc = Tc;
            if (tc == null || !tc.Active)
                return false;
            var contractor = CurContractorField(tc);
            _session.SendToHost(Protocol.Build(Msg.TraderRequest, w =>
            {
                w.Write((byte)op);
                WriteRef(w, contractor);
            }));
            Sent++;
            Plugin.Log.LogInfo("IFZCOOP trade Wunsch " + op);
            return false;
        }

        /// <summary>Prefix von RegularDragStrategy.PlaceResource/SwapResource beim Gast: nur Ziehen im Tauschfenster.</summary>
        public bool GuestDrag(RegularDragStrategy st, bool swap, ResourceID r1, float q1, ResourceID r2, float q2)
        {
            if (!GuestLocked)
                return true;
            var tc = Tc;
            if (tc == null || !tc.Active)
                return true;
            if (!SideOf(tc, FirstField(st), out var s1) || !SideOf(tc, SecondField(st), out var s2))
                return true;
            bool locked = LockedField(st), onLocked = OnLockedField(st), dbl = DoubleField(st);
            var contractor = CurContractorField(tc);
            _session.SendToHost(Protocol.Build(Msg.TraderRequest, w =>
            {
                w.Write((byte)(swap ? Op.Swap : Op.Place));
                WriteRef(w, contractor);
                w.Write((byte)s1); w.Write((byte)s2);
                w.Write((int)r1); w.Write(q1);
                w.Write((int)r2); w.Write(q2);
                w.Write(locked); w.Write(onLocked); w.Write(dbl);
            }));
            Sent++;
            return false;
        }

        // ================================================================ Hilfen

        private static ResourcesContainer SideContainer(TradingController tc, Side side)
        {
            switch (side)
            {
                case Side.PlayerStock: return CurPlayerField(tc)?.ResourcesToTrade;
                case Side.PlayerOffer: return PlayerOfferField(tc);
                case Side.VendorStock: return CurContractorField(tc)?.ResourcesToTrade;
                case Side.VendorOffer: return VendorOfferField(tc);
            }
            return null;
        }

        private static bool SideOf(TradingController tc, object c, out Side side)
        {
            side = Side.PlayerStock;
            if (c == null) return false;
            for (byte i = 0; i < 4; i++)
                if (ReferenceEquals(c, SideContainer(tc, (Side)i))) { side = (Side)i; return true; }
            return false;
        }

        private static void WriteRef(BinaryWriter w, object o)
        {
            if (o is FractionController f) { w.Write((byte)1); w.Write(((int)f.Fraction).ToString(CultureInfo.InvariantCulture)); }
            else if (o is Group g) { w.Write((byte)2); w.Write(g.Id ?? ""); }
            else { w.Write((byte)0); w.Write(""); }
        }

        private static Ref ReadRef(BinaryReader r) => new Ref { Kind = r.ReadByte(), Id = r.ReadString() };

        private static object ResolveRef(Ref rf)
        {
            switch (rf.Kind)
            {
                case 1:
                    if (!int.TryParse(rf.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int f)) return null;
                    try { return Fm?.Get((Fraction)f); } catch { return null; }
                case 2:
                    return UnitSync.FindGroupPublic(rf.Id);
            }
            return null;
        }

        private static bool SameRef(object o, Ref rf)
        {
            if (o is FractionController f) return rf.Kind == 1 && rf.Id == ((int)f.Fraction).ToString(CultureInfo.InvariantCulture);
            if (o is Group g) return rf.Kind == 2 && rf.Id == g.Id;
            return rf.Kind == 0;
        }

        private static string RefText(object o)
        {
            if (o is FractionController f) return "frac:" + f.Fraction;
            if (o is Group g) return "group:" + g.Id;
            return "-";
        }

        private static void WriteBox(BinaryWriter w, ResourcesContainer c)
        {
            w.Write(c != null);
            if (c == null) return;
            w.Write(c.Capacity);
            WriteQuantities(w, c.ConvertToResourceQuantityList());
            WriteQuantities(w, c.LockedResourcesToList());
        }

        private static Box ReadBox(BinaryReader r)
        {
            if (!r.ReadBoolean()) return null;
            return new Box { Capacity = r.ReadInt32(), Resources = ReadQuantities(r), Locked = ReadQuantities(r) };
        }

        private static void WriteQuantities(BinaryWriter w, List<ResourceQuantity> list)
        {
            var items = list.Where(q => q != null && Mathf.Abs(q.value) > 0.0001f).ToList();
            w.Write(items.Count);
            foreach (var q in items)
            {
                w.Write((int)q.resourceType);
                w.Write(q.value);
            }
        }

        private static List<ResourceQuantity> ReadQuantities(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 1000) throw new InvalidDataException("zu viele Rohstoffe");
            var list = new List<ResourceQuantity>(n);
            for (int i = 0; i < n; i++)
                list.Add(new ResourceQuantity((ResourceID)r.ReadInt32(), r.ReadSingle()));
            return list;
        }

        /// <summary>Behälter auf den Stand des Hosts bringen (nur bei Unterschied).</summary>
        private static void ApplyBox(ResourcesContainer c, Box b)
        {
            if (c == null || b == null)
                return;
            // Nie das gemeinsame Lager: das gleicht nur StockSync ab (Clear + LoadResources ginge bei vollem Lager verloren)
            if (ReferenceEquals(c, GameAccess.Game<Controllers.StockroomsController>()?.Container))
                return;
            if (Sig(c) == Sig(b))
                return;
            c.Clear();   // löst bei Angeboten UpdateOffer aus (setzt die Kapazität klein) – daher erst danach groß machen
            int need = 0;
            foreach (var q in b.Resources) need += Mathf.CeilToInt(q.value);
            foreach (var q in b.Locked) need += Mathf.CeilToInt(q.value);
            int cap = Math.Max(b.Capacity, need);
            if (c.Capacity < cap)
                c.SetCapacity(cap, force: true);
            c.LoadResources(b.Resources);
            c.LoadLockedResources(b.Locked);
            c.InvokeResourcesUpdated();
        }

        private static string Sig(IEnumerable<ResourceQuantity> res, IEnumerable<ResourceQuantity> locked)
        {
            var sb = new StringBuilder();
            foreach (var q in res.Where(q => q != null && Mathf.Abs(q.value) > 0.0001f).OrderBy(q => (int)q.resourceType))
                sb.Append(q.resourceType).Append(':').Append(q.value.ToString("0.##", CultureInfo.InvariantCulture)).Append(',');
            var l = locked.Where(q => q != null && Mathf.Abs(q.value) > 0.0001f).OrderBy(q => (int)q.resourceType).ToList();
            if (l.Count > 0)
            {
                sb.Append("|L");
                foreach (var q in l) sb.Append(q.resourceType).Append(':').Append(Mathf.RoundToInt(q.value)).Append(',');
            }
            return sb.Length == 0 ? "leer" : sb.ToString();
        }

        private static string Sig(ResourcesContainer c) =>
            c == null ? "-" : Sig(c.ConvertToResourceQuantityList(), c.LockedResourcesToList());

        private static string Sig(Box b) => b == null ? "-" : Sig(b.Resources, b.Locked);

        private static string SupplySig(Dictionary<Fraction, List<ResourceID>> d)
        {
            if (d == null || d.Count == 0) return "leer";
            return string.Join(";", d.OrderBy(k => (int)k.Key)
                .Select(k => k.Key + ":" + string.Join(",", (k.Value ?? new List<ResourceID>()).Select(x => x.ToString()))));
        }

        // ================================================================ Status

        public static IEnumerable<string> StatusLines()
        {
            var vc = Vc;
            var fm = Fm;
            var tc = Tc;
            if (vc == null)
                yield return "trader=-";
            else
            {
                float next = vc.NextVendorTimeInHours;
                string n = next > 1e20f ? "nie" : next.ToString("0", CultureInfo.InvariantCulture);
                yield return $"trader next={n} last={vc.LastOfferType} state={vc.VendorState} supply={SupplySig(vc.FractionSupplyResources)} frac={Sig(fm?.Vendor?.ResourcesToTrade)} broker={Sig(fm?.Player?.BrokerResources)}";
            }
            foreach (var g in TraderGroups(tc))
            {
                var v = g.VehicleHandler != null ? g.VehicleHandler.Vehicle : null;
                yield return $"tradergroup {g.Id} {g.Fraction} car={(v != null ? v.Id : "-")} goods={Sig(g.ResourcesToTrade)}";
            }
            if (tc == null || !tc.Active)
                yield return "trade active=False";
            else
                yield return $"trade active=True pre={tc.PreTrade} with={RefText(CurContractorField(tc))} player={RefText(CurPlayerField(tc))} give={Sig(PlayerOfferField(tc))} take={Sig(VendorOfferField(tc))}";
        }

        /// <summary>Für ChecksumSync: ohne Tauschfenster (das wechselt schnell).</summary>
        public static IEnumerable<string> ChecksumLines() => StatusLines().Where(l => !l.StartsWith("trade "));

        public string StatusLine() =>
            $"traderApplied={Applied} opened={Opened} closed={Closed} reqSent={Sent} reqDone={Done} missing={Missing}";

        // ================================================================ Testbefehle (QaDriver "trader ...")

        public static string QaCommand(string[] a, Vector3 hqPos)
        {
            var vc = Vc;
            var tc = Tc;
            var fm = Fm;
            var session = Plugin.Instance?.Session;
            bool guest = session != null && session.Role == Role.Guest;
            string sub = a.Length > 1 ? a[1].ToLowerInvariant() : "";
            switch (sub)
            {
                case "spawn":
                {
                    // Host: Händler-Gruppe neben dem HQ ("trader spawn [Fahrzeugtyp|None] [go]"), Vorrat wie im Spiel gewürfelt
                    if (guest) return "nur beim Host";
                    var config = JutsuGamesConfig.Runtime.Configs.GetConfig<GroupsConfig>();
                    var draft = config?.GetDraftByFraction(Fraction.Vendor);
                    if (draft == null) return "kein Händler-Bauplan";
                    var vt = a.Length > 2 && a[2] != "go" ? (VehicleType)Enum.Parse(typeof(VehicleType), a[2]) : VehicleType.ve_truck;
                    var g = GameAccess.Game<GroupBuilder>().Clear().SpawnGroupAt(hqPos + new Vector3(14f, 0f, 14f), draft, 2,
                        ResourceID.None, 0f, null, vt);
                    if (g == null) return "keine Gruppe";
                    var c = g.ResourcesToTrade;
                    if (c != null && c.Capacity > 1000) c.SetCapacity(40, force: true);
                    if (c != null) vc.FillResourcesContainer(c, Fraction.Vendor);
                    vc.VendorState = a.Contains("go") ? VendorState.ToTradeAtHQ : VendorState.Wait;
                    return $"Händler {g.Id} car={(g.VehicleHandler?.Vehicle != null ? g.VehicleHandler.Vehicle.Id : "-")} {Sig(c)}";
                }
                case "call":
                    // Host: wie im Spiel nach der Abklingzeit (das Ereignis entscheidet, ob ein Händler kommt)
                    if (guest) return "nur beim Host";
                    GameAccess.Game<Zenject.SignalBus>().Fire(new Gameplay.Signals.VendorCooldownFinishSignal());
                    return "VendorCooldownFinishSignal";
                case "cd":
                    vc.SetVendorCooldown(float.Parse(a[2], CultureInfo.InvariantCulture));
                    return "next=" + vc.NextVendorTimeInHours;
                case "state":
                    vc.VendorState = (VendorState)Enum.Parse(typeof(VendorState), a[2]);
                    return "state=" + vc.VendorState;
                case "fill":
                    vc.FillResourcesContainer(fm.Vendor.ResourcesToTrade, Fraction.Vendor);
                    return "frac=" + Sig(fm.Vendor.ResourcesToTrade);
                case "open":
                {
                    // Host: Tauschfenster wie Ereignis-Methode StartTrade (nächster Händler am HQ, sonst Fraktion Vendor)
                    if (guest) return "nur beim Host";
                    if (tc.Active) return "schon offen";
                    ITrader contractor = null;
                    if (!(a.Length > 2 && a[2] == "frac"))
                    {
                        float best = float.MaxValue;
                        foreach (var g in TraderGroups(null))
                        {
                            float d = (g.Position - hqPos).sqrMagnitude;
                            if (d < best) { best = d; contractor = g; }
                        }
                    }
                    if (contractor == null) contractor = fm.Vendor;
                    tc.StartTrade(contractor, fm.Player);
                    return "offen mit " + RefText(contractor);
                }
                case "give":
                case "ungive":
                case "take":
                case "untake":
                {
                    // Ziehen im Fenster: "trader give <ResourceID> <Menge>" (Lager -> Angebot), take (Händler -> Wunsch), un… zurück
                    if (!tc.Active) return "kein Tauschfenster";
                    var rid = (ResourceID)Enum.Parse(typeof(ResourceID), a[2]);
                    float q = float.Parse(a[3], CultureInfo.InvariantCulture);
                    Side from, to;
                    switch (sub)
                    {
                        case "give": from = Side.PlayerStock; to = Side.PlayerOffer; break;
                        case "ungive": from = Side.PlayerOffer; to = Side.PlayerStock; break;
                        case "take": from = Side.VendorStock; to = Side.VendorOffer; break;
                        default: from = Side.VendorOffer; to = Side.VendorStock; break;
                    }
                    var d = new Dictionary<ResourceID, float>[4];
                    for (int i = 0; i < 4; i++) d[i] = new Dictionary<ResourceID, float>();
                    new RegularDragStrategy(SideContainer(tc, from), SideContainer(tc, to), d[0], d[1], d[2], d[3], false, false, false)
                        .PlaceResource(rid, q);
                    return $"{sub} {rid} {q} give={Sig(PlayerOfferField(tc))} take={Sig(VendorOfferField(tc))} offer={tc.CurrentOfferType}";
                }
                case "accept":
                    if (!tc.Active) return "kein Tauschfenster";
                    tc.AcceptOffer();
                    return "accept offen=" + tc.Active;
                case "cancel":
                    if (!tc.Active) return "kein Tauschfenster";
                    tc.CancelOffer();
                    return "cancel offen=" + tc.Active;
                case "threaten":
                    if (!tc.Active) return "kein Tauschfenster";
                    tc.ThreatenOffer();
                    return "threaten offen=" + tc.Active;
            }
            return "trader spawn|call|cd|state|fill|open|give|ungive|take|untake|accept|cancel|threaten";
        }
    }
}
