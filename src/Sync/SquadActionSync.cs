using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Controllers;
using Controllers.Dragging.Strategies;
using Controllers.Exchange;
using Data.Resorces;
using Gameplay.GameResources;
using Gameplay.InGameResources;
using Gameplay.Units;
using Gameplay.Units.Characters;
using Gameplay.Units.Player.Workers;
using Gameplay.Units.Skills;
using Gameplay.Vehicles;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using UI;
using UI.InfoPanels;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Schalter im Trupp- und Fahrzeug-Fenster, Trupp teilen, Soldaten zwischen Trupps ziehen, Erfahrung/Fähigkeiten
    /// und Rohstoffe wegwerfen.
    ///
    /// Gast: Klicks gehen als Wunsch an den Host (SquadActionRequest). Aussteigen, Reparieren, Sprit in den Kofferraum,
    /// Teilen, Mitglieder ziehen und Wegwerfen passieren lokal nicht (Ergebnis kommt vom Host); Tanken an/aus,
    /// Munition nachfüllen und Ausrüstung sperren gelten sofort auch lokal (der Host bestätigt).
    /// Host: führt die Wünsche mit Spiel-Methoden aus. 1×/s Stand (SquadActionState): je Fahrzeug Tanken an/aus,
    /// Munition nachfüllen, Reparatur läuft + Fortschritt; je Trupp Ausrüstung gesperrt. Dazu Ereignisse
    /// (SquadActionEvent): Teilen bereit, Mitglied verschoben/getauscht, Trupp leer, Erfahrung/Fähigkeiten je Figur.
    /// Neue Trupps (auch der leere beim Teilen) kommen wie gehabt über SquadSync, Kofferraum/Rucksack über ContainerSync,
    /// Sprit/Zustand über VehicleSync.
    /// </summary>
    internal sealed class SquadActionSync
    {
        private const float SendInterval = 1f;
        private const float ForceInterval = 10f;
        private const float KnowledgeForceInterval = 30f;
        private const float LocalHold = 2.5f;
        private const float SplitTimeout = 300f;

        internal enum Op : byte
        {
            Leave = 1, Repair = 2, CancelRepair = 3, PourFuel = 4, FuelIn = 5, FuelOut = 6, AmmoRefill = 7,
            LockSquad = 8, LockVehicle = 9, Split = 10, Place = 11, Swap = 12, ExchangeEnd = 13,
            DiscardContainer = 14, DiscardCharacter = 15,
        }

        private enum Ev : byte { SplitReady = 1, Placed = 2, Swapped = 3, Emptied = 4, Knowledge = 5 }

        private static readonly AccessTools.FieldRef<AbandonedVehiclePanelInfo, Vehicle> AbandonedVehicleField =
            AccessTools.FieldRefAccess<AbandonedVehiclePanelInfo, Vehicle>("_vehicle");
        private static readonly AccessTools.FieldRef<ExchangeOffer, Group> OfferSquadField =
            AccessTools.FieldRefAccess<ExchangeOffer, Group>("SquadInExchange");
        private static readonly AccessTools.FieldRef<ExchangeWithSquadOffer, Group> OfferSecondField =
            AccessTools.FieldRefAccess<ExchangeWithSquadOffer, Group>("_secondSquad");
        private static readonly AccessTools.FieldRef<ResourceDiscardController, ResourcesContainer> DiscardContainerField =
            AccessTools.FieldRefAccess<ResourceDiscardController, ResourcesContainer>("_selectedContainer");
        private static readonly AccessTools.FieldRef<ResourceDiscardController, Character> DiscardCharacterField =
            AccessTools.FieldRefAccess<ResourceDiscardController, Character>("_selectedCharacter");
        private static readonly AccessTools.FieldRef<ResourceDiscardController, ResourceID> DiscardResourceField =
            AccessTools.FieldRefAccess<ResourceDiscardController, ResourceID>("_selectedResource");
        private static readonly AccessTools.FieldRef<ResourceDiscardController, DiscardResourceCanvas> DiscardCanvasField =
            AccessTools.FieldRefAccess<ResourceDiscardController, DiscardResourceCanvas>("_discardResourceCanvas");

        private readonly Session _session;

        // Host
        private string _stateSig;
        private float _nextSend;
        private float _nextForce;
        private readonly Dictionary<string, string> _knowSent = new Dictionary<string, string>();
        private float _nextKnowForce;
        private readonly Dictionary<string, float> _pendingSplits = new Dictionary<string, float>(); // leerer Trupp -> Zeit

        // Gast
        private readonly Dictionary<string, float> _localUntil = new Dictionary<string, float>();
        private readonly Dictionary<string, float> _pendingEmpty = new Dictionary<string, float>();
        private readonly List<PendingMove> _pendingMoves = new List<PendingMove>();
        private string _openMain, _openEmpty;
        private float _openUntil;
        private bool _endingExchange;
        private int _endingFrame;
        private Group _endA, _endB;

        private sealed class PendingMove
        {
            public string Character, Group;
            public int Index;
            public float Until;
        }

        public int RequestsSent { get; private set; }
        public int RequestsDone { get; private set; }
        public int StatesApplied { get; private set; }
        public int RepairsMirrored { get; private set; }
        public int MembersMoved { get; private set; }
        public int SplitsOpened { get; private set; }
        public int Emptied { get; private set; }
        public int KnowledgeApplied { get; private set; }
        public int Discards { get; private set; }

        public SquadActionSync(Session session)
        {
            _session = session;
        }

        public void ResendAll()
        {
            _stateSig = null;
            _nextSend = 0f;
            _knowSent.Clear();
        }

        public void Reset()
        {
            ResendAll();
            _pendingSplits.Clear();
            _localUntil.Clear();
            _pendingEmpty.Clear();
            _pendingMoves.Clear();
            _openMain = _openEmpty = null;
            _endingExchange = false;
            _endA = _endB = null;
        }

        // ================================================================ Zugriff

        private static IEnumerable<Group> Squads()
        {
            var squads = GameAccess.Game<SquadsController>()?.Squads;
            if (squads == null)
                yield break;
            foreach (var g in squads)
                if (g != null && !string.IsNullOrEmpty(g.Id))
                    yield return g;
        }

        private static IEnumerable<Vehicle> Vehicles()
        {
            var vs = GameAccess.Game<VehiclesController>()?.Vehicles;
            if (vs == null)
                yield break;
            foreach (var v in vs)
                if (v != null && !string.IsNullOrEmpty(v.Id))
                    yield return v;
        }

        private static Group FindSquad(string id) => string.IsNullOrEmpty(id) ? null : Squads().FirstOrDefault(g => g.Id == id);

        private static Vehicle FindVehicle(string id) => string.IsNullOrEmpty(id) ? null : GameAccess.Game<VehiclesController>()?.GetVehicleById(id);

        private static Character FindMember(string id) =>
            string.IsNullOrEmpty(id) ? null : GameAccess.Game<SquadsController>()?.FindSquadMember(id);

        private static Character FindAnyCharacter(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            return FindMember(id) ?? GameAccess.Game<WorkersController>()?.FindWorker(id);
        }

        private static IEnumerable<Character> KnowledgeCharacters()
        {
            var seen = new HashSet<string>();
            foreach (var g in Squads())
                foreach (var c in g.Characters)
                    if (c != null && !string.IsNullOrEmpty(c.Id) && c.KnowledgeGainer != null && seen.Add(c.Id))
                        yield return c;
            var workers = GameAccess.Game<WorkersController>()?.Workers;
            if (workers != null)
                foreach (var c in workers)
                    if (c != null && !string.IsNullOrEmpty(c.Id) && c.KnowledgeGainer != null && seen.Add(c.Id))
                        yield return c;
        }

        private static int IndexIn(Group g, Character c)
        {
            var list = g.Characters;
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], c)) return i;
            return -1;
        }

        private static string F(float f, string fmt = "0.00") => f.ToString(fmt, CultureInfo.InvariantCulture);

        private bool GuestCanAsk => _session.Role == Role.Guest && _session.GuestControlsLocked && !UnitSync.NetApplying;

        private bool HostWithGuests => _session.Role == Role.Host && _session.ActiveGuestCount > 0;

        private void Ask(Op op, Action<BinaryWriter> body)
        {
            _session.SendToHost(Protocol.Build(Msg.SquadActionRequest, w =>
            {
                w.Write((byte)op);
                body(w);
            }));
            RequestsSent++;
        }

        private void Hold(string key) => _localUntil[key] = Time.realtimeSinceStartup + LocalHold;

        private bool Held(string key) => _localUntil.TryGetValue(key, out var until) && Time.realtimeSinceStartup < until;

        // ================================================================ Gast: Klicks (Prefixe, true = Original laufen lassen)

        /// <summary>SquadPanelInfo.LeaveVehicleInvoke</summary>
        public bool GuestLeave(SquadPanelInfo p)
        {
            if (!GuestCanAsk)
                return true;
            var g = p?.Squad;
            if (g == null || g.VehicleHandler == null || !g.VehicleHandler.HaveVehicle())
                return false;
            Ask(Op.Leave, w => w.Write(g.Id));
            return false;
        }

        /// <summary>SquadPanelInfo.StartRepair</summary>
        public bool GuestSquadRepair(SquadPanelInfo p)
        {
            if (!GuestCanAsk)
                return true;
            var g = p?.Squad;
            var v = g != null ? g.VehicleHandler?.Vehicle : null;
            if (v == null || Expedition.ExpeditionGrid.IsOnExpedition(v.Position))
                return false;
            Ask(Op.Repair, w => { w.Write(g.Id); w.Write(v.Id); });
            _session.ShowNotice("Reparatur beim Host beauftragt");
            return false;
        }

        /// <summary>AbandonedVehiclePanelInfo.StartRepair</summary>
        public bool GuestAbandonedRepair(AbandonedVehiclePanelInfo p)
        {
            if (!GuestCanAsk)
                return true;
            var v = p != null ? AbandonedVehicleField(p) : null;
            if (v == null)
                return false;
            Ask(Op.Repair, w => { w.Write(""); w.Write(v.Id); });
            _session.ShowNotice("Reparatur beim Host beauftragt");
            return false;
        }

        /// <summary>VehicleInRepairPanel.OnCancelButtonClicked</summary>
        public bool GuestCancelRepair(VehicleInRepairPanel p)
        {
            if (!GuestCanAsk)
                return true;
            var v = p?.DisplayedVehicle;
            if (v == null)
                return false;
            Ask(Op.CancelRepair, w => w.Write(v.Id));
            return false;
        }

        /// <summary>VehicleInRepairPanel.HandleInfoPanelAction (Tastenkürzel Abbrechen)</summary>
        public bool GuestRepairPanelAction(VehicleInRepairPanel p, UI.InfoPanels.NestedUIElements.InfoPanelAction action)
        {
            if (action != UI.InfoPanels.NestedUIElements.InfoPanelAction.CancelInfoPanelButton)
                return true;
            return GuestCancelRepair(p);
        }

        /// <summary>SquadPanelInfo.PourFuelToTrunk</summary>
        public bool GuestPourFuel(SquadPanelInfo p)
        {
            if (!GuestCanAsk)
                return true;
            var v = p?.Squad != null ? p.Squad.VehicleHandler?.Vehicle : null;
            if (v == null)
                return false;
            Ask(Op.PourFuel, w => w.Write(v.Id));
            return false;
        }

        /// <summary>SquadPanelInfo.ChangeFuelInState / ChangeFuelOutState</summary>
        public bool GuestFuel(SquadPanelInfo p, bool input)
        {
            if (!GuestCanAsk)
                return true;
            var v = p?.Squad != null ? p.Squad.VehicleHandler?.Vehicle : null;
            if (v == null)
                return false;
            bool want = input ? !v.FuelInEnabled : !v.FuelOutEnabled;
            Hold((input ? "fi:" : "fo:") + v.Id);
            Ask(input ? Op.FuelIn : Op.FuelOut, w => { w.Write(v.Id); w.Write(want); });
            return true; // lokal sofort umschalten (Knopf-Text), Host bestätigt
        }

        /// <summary>VehicleAmmoContainerPanel.HandleButtonClicked</summary>
        public bool GuestAmmoRefill(VehicleAmmoContainerPanel p)
        {
            if (!GuestCanAsk)
                return true;
            var v = p?.CurrentVehicle;
            if (v == null)
                return false;
            bool want = !v.AmmoContainer.EnabledAmmoRefill;
            Hold("ar:" + v.Id);
            Ask(Op.AmmoRefill, w => { w.Write(v.Id); w.Write(want); });
            return true;
        }

        /// <summary>SquadPanelInfo.HandleSquadLockStateChanged / HandleVehicleLockStateChanged</summary>
        public bool GuestLock(SquadPanelInfo p, bool vehicle, bool locked)
        {
            if (!GuestCanAsk)
                return true;
            var g = p?.Squad;
            if (g == null)
                return false;
            if (vehicle)
            {
                var v = g.VehicleHandler?.Vehicle;
                if (v == null)
                    return false;
                Ask(Op.LockVehicle, w => { w.Write(v.Id); w.Write(locked); });
            }
            else
            {
                Hold("lk:" + g.Id);
                Ask(Op.LockSquad, w => { w.Write(g.Id); w.Write(locked); });
            }
            return true;
        }

        /// <summary>SquadSplitPreparer.PrepareSquadForSplit</summary>
        public bool GuestSplit(Group main)
        {
            if (!GuestCanAsk)
                return true;
            if (main == null || string.IsNullOrEmpty(main.Id))
                return false;
            try { GameInput.CursorsManager.SelectDefaultCursor(); } catch { }
            Ask(Op.Split, w => w.Write(main.Id));
            _session.ShowNotice("Teilen beim Host beauftragt");
            return false;
        }

        /// <summary>MembersDragStrategy.PlaceMember</summary>
        public bool GuestPlace(Character c, Group target, int index)
        {
            if (!GuestCanAsk)
                return true;
            if (c == null || target == null || string.IsNullOrEmpty(c.Id) || string.IsNullOrEmpty(target.Id))
                return false;
            Ask(Op.Place, w => { w.Write(c.Id); w.Write(target.Id); w.Write(index); });
            return false;
        }

        /// <summary>MembersDragStrategy.SwapMembers</summary>
        public bool GuestSwap(Character a, Character b, int ia, int ib)
        {
            if (!GuestCanAsk)
                return true;
            if (a == null || b == null || string.IsNullOrEmpty(a.Id) || string.IsNullOrEmpty(b.Id))
                return false;
            Ask(Op.Swap, w => { w.Write(a.Id); w.Write(b.Id); w.Write(ia); w.Write(ib); });
            return false;
        }

        /// <summary>Prefix ExchangeWithSquadOffer.InterruptExchange: beim Gast entscheidet der Host, ob ein Trupp leer ist.</summary>
        public void GuestExchangeEnding(ExchangeWithSquadOffer offer)
        {
            if (_session.Role != Role.Guest || !_session.GuestControlsLocked || offer == null)
                return;
            _endingExchange = true;
            _endingFrame = Time.frameCount;
            try { _endA = OfferSquadField(offer); _endB = OfferSecondField(offer); }
            catch { _endA = _endB = null; }
        }

        /// <summary>Postfix ExchangeWithSquadOffer.InterruptExchange.</summary>
        public void GuestExchangeEnded()
        {
            if (!_endingExchange)
                return;
            _endingExchange = false;
            string a = _endA != null ? _endA.Id ?? "" : "";
            string b = _endB != null ? _endB.Id ?? "" : "";
            _endA = _endB = null;
            if (_session.Role == Role.Guest && _session.GuestControlsLocked)
                Ask(Op.ExchangeEnd, w => { w.Write(a); w.Write(b); });
        }

        /// <summary>Prefix Group.SetGroupAsEmpty: beim Gast nicht selbst am Ende eines Tauschs (Host meldet es).</summary>
        public bool AllowSetEmpty(Group g)
        {
            // nur im selben Frame wie InterruptExchange (falls der Postfix wegen eines Fehlers nie kam)
            if (_session.Role == Role.Guest && _session.GuestControlsLocked && _endingExchange && _endingFrame == Time.frameCount && !UnitSync.NetApplying
                && g != null && g.Fraction == Fraction.Player)
                return false;
            return true;
        }

        /// <summary>Postfix Group.SetGroupAsEmpty beim Host: eigener Trupp leer → Gäste.</summary>
        public void HostSetEmpty(Group g)
        {
            if (!HostWithGuests || g == null || string.IsNullOrEmpty(g.Id) || g.Fraction != Fraction.Player)
                return;
            _pendingSplits.Remove(g.Id);
            _session.BroadcastToGuests(Protocol.Build(Msg.SquadActionEvent, w => { w.Write((byte)Ev.Emptied); w.Write(g.Id); }));
        }

        /// <summary>Postfix MembersDragStrategy.PlaceMember beim Host.</summary>
        public void HostPlaced(Character c, Group target, int index)
        {
            if (!HostWithGuests || c == null || target == null || string.IsNullOrEmpty(c.Id) || c.Group != target)
                return;
            _session.BroadcastToGuests(Protocol.Build(Msg.SquadActionEvent, w =>
            {
                w.Write((byte)Ev.Placed);
                w.Write(c.Id);
                w.Write(target.Id);
                w.Write(index);
            }));
        }

        /// <summary>Postfix MembersDragStrategy.SwapMembers beim Host. before = Trupp-IDs vor dem Tausch.</summary>
        public void HostSwapped(Character a, Character b, int ia, int ib, string[] before)
        {
            if (!HostWithGuests || a == null || b == null || a.Group == null || b.Group == null || before == null || before.Length < 2)
                return;
            int afterA = IndexIn(a.Group, a);
            int afterB = IndexIn(b.Group, b);
            _session.BroadcastToGuests(Protocol.Build(Msg.SquadActionEvent, w =>
            {
                w.Write((byte)Ev.Swapped);
                w.Write(a.Id); w.Write(b.Id);
                w.Write(ia); w.Write(ib);
                w.Write(before[0] ?? ""); w.Write(before[1] ?? "");
                w.Write(afterA); w.Write(afterB);
            }));
        }

        /// <summary>ResourceDiscardController.HandleDiscardClicked</summary>
        public bool GuestDiscard(ResourceDiscardController ctl)
        {
            if (!GuestCanAsk || ctl == null)
                return true;
            try
            {
                var res = DiscardResourceField(ctl);
                var character = DiscardCharacterField(ctl);
                if (character != null)
                {
                    Ask(Op.DiscardCharacter, w => { w.Write(character.Id); w.Write((int)res); });
                }
                else
                {
                    var container = DiscardContainerField(ctl);
                    var canvas = DiscardCanvasField(ctl);
                    float count = canvas != null ? canvas.ResourceCount : 0f;
                    if (container != null && ExchangeSync.Describe(container, out var kind, out var id))
                        Ask(Op.DiscardContainer, w => { w.Write((byte)kind); w.Write(id); w.Write((int)res); w.Write(count); });
                    else
                        _session.ShowNotice("Wegwerfen hier im Koop nicht möglich");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Wegwerfen (Gast): " + e.Message);
            }
            try { ctl.Clear(); } catch { }
            return false;
        }

        // ================================================================ Host: Wünsche

        public void HostOnRequest(Peer from, BinaryReader r)
        {
            var op = (Op)r.ReadByte();
            try
            {
                if (HostExecute(from, op, r))
                {
                    RequestsDone++;
                    _nextSend = 0f;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Trupp-Aktion {op} von {from.Name}: {e}");
            }
        }

        private bool HostExecute(Peer from, Op op, BinaryReader r)
        {
            switch (op)
            {
                case Op.Leave:
                {
                    var g = FindSquad(r.ReadString());
                    if (g == null || !g.VehicleHandler.HaveVehicle())
                        return false;
                    LeaveVehicle(g);
                    Plugin.Log.LogInfo($"IFZCOOP squadaction leave {g.Id} (von {from.Name})");
                    return true;
                }
                case Op.Repair:
                {
                    var g = FindSquad(r.ReadString());
                    var v = FindVehicle(r.ReadString());
                    if (v == null || v.RepairWork != null || Expedition.ExpeditionGrid.IsOnExpedition(v.Position))
                        return false;
                    if (g != null && g.VehicleHandler.Vehicle == v)
                        LeaveVehicle(g);
                    else if (v.Owner != null && v.Owner.VehicleHandler != null && v.Owner.VehicleHandler.Vehicle == v)
                        v.Owner.VehicleHandler.UnassignVehicle(isVehicleWaiting: false);
                    v.InitRepairWork(isDisplayed: false);
                    v.AssignWorkers();
                    Plugin.Log.LogInfo($"IFZCOOP squadaction repair {v.Id} (von {from.Name})");
                    return true;
                }
                case Op.CancelRepair:
                {
                    var v = FindVehicle(r.ReadString());
                    if (v == null || v.RepairWork == null)
                        return false;
                    v.RepairWork.DeleteWork();
                    Plugin.Log.LogInfo($"IFZCOOP squadaction cancelrepair {v.Id} (von {from.Name})");
                    return true;
                }
                case Op.PourFuel:
                {
                    var v = FindVehicle(r.ReadString());
                    if (v == null)
                        return false;
                    v.PourFuelToTrunk();
                    Plugin.Log.LogInfo($"IFZCOOP squadaction pour {v.Id} (von {from.Name})");
                    return true;
                }
                case Op.FuelIn:
                case Op.FuelOut:
                case Op.AmmoRefill:
                case Op.LockVehicle:
                {
                    var v = FindVehicle(r.ReadString());
                    bool on = r.ReadBoolean();
                    if (v == null)
                        return false;
                    if (op == Op.FuelIn) v.SetFuelInEnabled(on);
                    else if (op == Op.FuelOut) v.SetFuelOutEnabled(on);
                    else if (op == Op.AmmoRefill) { if (v.AmmoContainer.EnabledAmmoRefill != on) v.AmmoContainer.SwitchRefillState(); }
                    else if (v.EquipmentLockHandler.IsLocked != on) v.EquipmentLockHandler.SetState(on);
                    Plugin.Log.LogInfo($"IFZCOOP squadaction {op} {v.Id}={on} (von {from.Name})");
                    return true;
                }
                case Op.LockSquad:
                {
                    var g = FindSquad(r.ReadString());
                    bool on = r.ReadBoolean();
                    if (g == null || g.EquipmentLockHandler == null)
                        return false;
                    if (g.EquipmentLockHandler.IsLocked != on)
                        g.EquipmentLockHandler.SetState(on);
                    Plugin.Log.LogInfo($"IFZCOOP squadaction lock {g.Id}={on} (von {from.Name})");
                    return true;
                }
                case Op.Split:
                    return HostSplit(from, r.ReadString());
                case Op.Place:
                {
                    var c = FindMember(r.ReadString());
                    var g = FindSquad(r.ReadString());
                    int index = r.ReadInt32();
                    if (c == null || g == null || c.Group == g || c.Group == null)
                        return false;
                    new MembersDragStrategy().PlaceMember(c, g, Mathf.Clamp(index, 0, g.Characters.Count));
                    Plugin.Log.LogInfo($"IFZCOOP squadaction place {c.Id} -> {g.Id} (von {from.Name})");
                    return true;
                }
                case Op.Swap:
                {
                    var a = FindMember(r.ReadString());
                    var b = FindMember(r.ReadString());
                    int ia = r.ReadInt32(), ib = r.ReadInt32();
                    if (a == null || b == null || a.Group == null || b.Group == null)
                        return false;
                    new MembersDragStrategy().SwapMembers(a, b, Mathf.Max(0, ia), Mathf.Max(0, ib));
                    Plugin.Log.LogInfo($"IFZCOOP squadaction swap {a.Id} <-> {b.Id} (von {from.Name})");
                    return true;
                }
                case Op.ExchangeEnd:
                {
                    string a = r.ReadString(), b = r.ReadString();
                    foreach (var id in new[] { a, b })
                    {
                        _pendingSplits.Remove(id ?? "");
                        var g = FindSquad(id);
                        if (g != null && g.IsEmpty())
                            g.CheckEmptiness();
                    }
                    Plugin.Log.LogInfo($"IFZCOOP squadaction exchange end {a} {b} (von {from.Name})");
                    return true;
                }
                case Op.DiscardContainer:
                {
                    var kind = (ExchangeSync.Kind)r.ReadByte();
                    string id = r.ReadString();
                    var res = (ResourceID)r.ReadInt32();
                    float count = r.ReadSingle();
                    var container = ExchangeSync.Resolve(kind, id) as ResourcesContainer;
                    if (container == null || !DiscardFromContainer(container, res, count))
                        return false;
                    Discards++;
                    Plugin.Log.LogInfo($"IFZCOOP squadaction discard {res} {count:0.##} aus {kind} {id} (von {from.Name})");
                    return true;
                }
                case Op.DiscardCharacter:
                {
                    var c = FindAnyCharacter(r.ReadString());
                    var res = (ResourceID)r.ReadInt32();
                    if (c == null || !DiscardFromCharacter(c, res))
                        return false;
                    Discards++;
                    Plugin.Log.LogInfo($"IFZCOOP squadaction discard {res} von Figur {c.Id} (von {from.Name})");
                    return true;
                }
            }
            return false;
        }

        private static void LeaveVehicle(Group g)
        {
            var vh = g.VehicleHandler;
            if (!vh.HaveVehicleWaiting() && vh.HaveVehicle() && !vh.Vehicle.Movement.HasReachedDestination())
                vh.Vehicle.StopMoving();
            vh.UnassignVehicle(isVehicleWaiting: false);
        }

        private bool HostSplit(Peer from, string mainId)
        {
            var main = FindSquad(mainId);
            var creator = GameAccess.Game<SquadsCreator>();
            if (main == null || main.IsEmpty() || creator == null)
                return false;
            var sc = GameAccess.Game<SquadsController>();
            var requests = GameAccess.Game<SquadCreatorController>();
            if (sc != null && requests != null && SquadsController.AllSquadCount + requests.GetAllRequestsCount() >= sc.MaxSquadToCreateCount)
            {
                _session.SendTo(from.Id, Protocol.Build(Msg.Notice, w => w.Write("Keine weiteren Trupps möglich")));
                return false;
            }
            main.OrdersHandlers.EndCurrentOrder(dispose: true, cancel: true);
            main.StopMoving();
            // Baut über GroupBuilder.Build → SquadSync meldet den neuen (leeren) Trupp an alle Gäste, vor dieser Antwort.
            var empty = creator.CreateEmptySquadAt(main.transform.position);
            if (empty == null || string.IsNullOrEmpty(empty.Id))
                return false;
            _pendingSplits[empty.Id] = Time.realtimeSinceStartup;
            _session.SendTo(from.Id, Protocol.Build(Msg.SquadActionEvent, w =>
            {
                w.Write((byte)Ev.SplitReady);
                w.Write(main.Id);
                w.Write(empty.Id);
            }));
            Plugin.Log.LogInfo($"IFZCOOP squadaction split {main.Id} -> leer {empty.Id} (von {from.Name})");
            return true;
        }

        /// <summary>Wie ResourceDiscardController.HandleDiscardClicked, nur ohne Fenster.</summary>
        private static bool DiscardFromContainer(ResourcesContainer container, ResourceID res, float count)
        {
            var data = ResourcesDataContainer.Instance?.GetResourceDataByType(res);
            if (data == null || !data.CanBeDiscard)
                return false;
            float have = container.GetResourceQuantity(res);
            count = Mathf.Min(count, have);
            if (count <= 0f)
                return false;
            container.TryRemoveResource(res, count, removeAllIfNeed: true);
            if (data.DismountOutput != null)
            {
                foreach (var q in data.DismountOutput)
                {
                    if (q == null)
                        continue;
                    if (container.IsSquadOrVehicleContainer)
                    {
                        // wie im Spiel: Teile einzeln einladen, was nicht passt, fällt zu Boden
                        for (float left = q.value; left > 0f; left -= 1f)
                        {
                            float part = Mathf.Min(left, 1f);
                            if (container.HasEnoughSpace(q.resourceType, part))
                                container.AddResource(q.resourceType, part, ResourceAddReason.None);
                            else
                                container.InvokeResourceDrop(new ResourceCrate(q.resourceType, part));
                        }
                    }
                    else
                    {
                        container.ForceAddResource(q.resourceType, q.value * count, ResourceAddReason.None);
                    }
                }
            }
            return true;
        }

        /// <summary>Wie ResourceDiscardController.DiscardResourceFromCharacter.</summary>
        private static bool DiscardFromCharacter(Character c, ResourceID res)
        {
            var rdc = ResourcesDataContainer.Instance;
            var items = c.ItemsContainer;
            if (rdc == null || items == null)
                return false;
            var data = rdc.GetResourceDataByType(res);
            if (data == null || !data.CanBeDiscard)
                return false;
            var output = new List<ResourceQuantity>();
            if (rdc.IsGear(res))
            {
                if (items.GearHandler.IsCurrentGearDefault)
                    return false;
                output.Add(new ResourceQuantity(res, items.GearHandler.CurrentGearQuantity));
                items.GearHandler.AssignDefaultGear(dropPreviousGear: false);
            }
            if (rdc.IsWeapon(res))
            {
                var wh = items.WeaponHandler;
                if (wh.UseVehicleModificationWeapon || wh.UseDefaultWeapon)
                    return false;
                output.Add(new ResourceQuantity(res, 1f));
                var ammo = items.AmmunitionHandler.Ammunition;
                if (ammo != null)
                    output.Add(new ResourceQuantity(ammo.ResourceID, ammo.Quantity));
                if (data.DismountOutput != null)
                    foreach (var q in data.DismountOutput)
                        if (q != null) output.Add(q);
                wh.AssignDefaultWeapon(dropPreviousWeapon: false);
                items.AmmunitionHandler.Clear();
            }
            if (output.Count == 0)
                return false;
            foreach (var q in output)
                items.TryToDropCrate(new ResourceCrate(q.resourceType, q.value));
            return true;
        }

        // ================================================================ Host: Stand

        private sealed class VState { public string Id; public bool FuelIn, FuelOut, Ammo, Repair; public float Progress; }

        private static List<VState> TakeVehicles()
        {
            var list = new List<VState>();
            foreach (var v in Vehicles().OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                var rw = v.RepairWork;
                list.Add(new VState
                {
                    Id = v.Id, FuelIn = v.FuelInEnabled, FuelOut = v.FuelOutEnabled,
                    Ammo = v.AmmoContainer != null && v.AmmoContainer.EnabledAmmoRefill,
                    Repair = rw != null, Progress = rw != null ? rw.Progress : 0f,
                });
            }
            return list;
        }

        private static List<KeyValuePair<string, bool>> TakeLocks()
        {
            var list = new List<KeyValuePair<string, bool>>();
            foreach (var g in Squads().OrderBy(x => x.Id, StringComparer.Ordinal))
                if (g.Fraction == Fraction.Player && g.EquipmentLockHandler != null)
                    list.Add(new KeyValuePair<string, bool>(g.Id, g.EquipmentLockHandler.IsLocked));
            return list;
        }

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend || _session.ActiveGuestCount == 0 || !GameAccess.IsGameStarted)
                return;
            _nextSend = now + SendInterval;

            // Leere Trupps aus einem Teilen, das der Gast nie beendet hat
            if (_pendingSplits.Count > 0)
                foreach (var id in _pendingSplits.Where(kv => now - kv.Value > SplitTimeout).Select(kv => kv.Key).ToList())
                {
                    _pendingSplits.Remove(id);
                    var g = FindSquad(id);
                    if (g != null && g.IsEmpty())
                        g.CheckEmptiness();
                }

            try { SendState(now); }
            catch (Exception e) { Plugin.Log.LogDebug("Trupp-Aktionen Stand: " + e.Message); }
            try { SendKnowledge(now); }
            catch (Exception e) { Plugin.Log.LogDebug("Erfahrung Stand: " + e.Message); }
        }

        private void SendState(float now)
        {
            var vehicles = TakeVehicles();
            var locks = TakeLocks();
            var sb = new StringBuilder();
            foreach (var v in vehicles)
                sb.Append(v.Id).Append(v.FuelIn ? 'I' : '-').Append(v.FuelOut ? 'O' : '-').Append(v.Ammo ? 'A' : '-')
                  .Append(v.Repair ? 'R' + F(v.Progress) : "-").Append('|');
            foreach (var l in locks)
                sb.Append(l.Key).Append(l.Value ? '+' : '-').Append('|');
            string sig = sb.ToString();
            bool force = now >= _nextForce;
            if (!force && sig == _stateSig)
                return;
            if (force) _nextForce = now + ForceInterval;
            _stateSig = sig;
            _session.BroadcastToGuests(Protocol.Build(Msg.SquadActionState, w =>
            {
                w.Write(vehicles.Count);
                foreach (var v in vehicles)
                {
                    w.Write(v.Id);
                    w.Write(v.FuelIn);
                    w.Write(v.FuelOut);
                    w.Write(v.Ammo);
                    w.Write(v.Repair);
                    w.Write(v.Progress);
                }
                w.Write(locks.Count);
                foreach (var l in locks)
                {
                    w.Write(l.Key);
                    w.Write(l.Value);
                }
            }));
        }

        private static string KnowledgeSig(Character c)
        {
            var sb = new StringBuilder();
            foreach (var a in c.KnowledgeGainer.Serialize().OrderBy(x => (int)x.ID))
                sb.Append((int)a.ID).Append(':').Append(F(a.CurrentExperience)).Append(a.MaxStatus ? 'M' : '-').Append(a.LastTimeIncreasedByShootingRange ? 'S' : '-').Append(',');
            sb.Append('|');
            if (c.SkillsHandler != null)
                foreach (var s in c.SkillsHandler.Serialize().OrderBy(x => (int)x.ID))
                    if (s.IsActivated) sb.Append((int)s.ID).Append(',');
            return sb.ToString();
        }

        private void SendKnowledge(float now)
        {
            bool force = now >= _nextKnowForce;
            if (force) _nextKnowForce = now + KnowledgeForceInterval;
            var changed = new List<Character>();
            var alive = new HashSet<string>();
            foreach (var c in KnowledgeCharacters())
            {
                alive.Add(c.Id);
                string sig = KnowledgeSig(c);
                if (force || !_knowSent.TryGetValue(c.Id, out var last) || last != sig)
                {
                    _knowSent[c.Id] = sig;
                    changed.Add(c);
                }
            }
            foreach (var id in _knowSent.Keys.Where(k => !alive.Contains(k)).ToList())
                _knowSent.Remove(id);
            if (changed.Count == 0)
                return;
            _session.BroadcastToGuests(Protocol.Build(Msg.SquadActionEvent, w =>
            {
                w.Write((byte)Ev.Knowledge);
                w.Write(changed.Count);
                foreach (var c in changed)
                {
                    w.Write(c.Id);
                    var acts = c.KnowledgeGainer.Serialize();
                    w.Write(acts.Length);
                    foreach (var a in acts)
                    {
                        w.Write((int)a.ID);
                        w.Write(a.CurrentExperience);
                        w.Write(a.MaxStatus);
                        w.Write(a.LastTimeIncreasedByShootingRange);
                    }
                    var skills = c.SkillsHandler != null ? c.SkillsHandler.Serialize() : new SerializedSkill[0];
                    w.Write(skills.Length);
                    foreach (var s in skills)
                    {
                        w.Write((int)s.ID);
                        w.Write(s.IsActivated);
                    }
                }
            }));
        }

        // ================================================================ Gast: Stand und Ereignisse

        public void GuestOnState(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 10000)
                throw new InvalidDataException("zu viele Fahrzeuge");
            var vehicles = new List<VState>(n);
            for (int i = 0; i < n; i++)
                vehicles.Add(new VState { Id = r.ReadString(), FuelIn = r.ReadBoolean(), FuelOut = r.ReadBoolean(), Ammo = r.ReadBoolean(), Repair = r.ReadBoolean(), Progress = r.ReadSingle() });
            int k = r.ReadInt32();
            if (k < 0 || k > 10000)
                throw new InvalidDataException("zu viele Trupps");
            var locks = new List<KeyValuePair<string, bool>>(k);
            for (int i = 0; i < k; i++)
                locks.Add(new KeyValuePair<string, bool>(r.ReadString(), r.ReadBoolean()));

            UnitSync.NetApplying = true;
            try
            {
                foreach (var s in vehicles)
                {
                    var v = FindVehicle(s.Id);
                    if (v == null)
                        continue;
                    try { ApplyVehicle(v, s); }
                    catch (Exception e) { Plugin.Log.LogWarning($"Fahrzeug-Schalter {s.Id}: {e.Message}"); }
                }
                foreach (var l in locks)
                {
                    var g = FindSquad(l.Key);
                    if (g == null || g.EquipmentLockHandler == null || Held("lk:" + g.Id))
                        continue;
                    if (g.EquipmentLockHandler.IsLocked != l.Value)
                    {
                        try { g.EquipmentLockHandler.SetState(l.Value); }
                        catch (Exception e) { Plugin.Log.LogWarning($"Sperre {g.Id}: {e.Message}"); }
                    }
                }
                StatesApplied++;
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        private void ApplyVehicle(Vehicle v, VState s)
        {
            if (!Held("fi:" + v.Id) && v.FuelInEnabled != s.FuelIn)
                v.SetFuelInEnabled(s.FuelIn);
            if (!Held("fo:" + v.Id) && v.FuelOutEnabled != s.FuelOut)
                v.SetFuelOutEnabled(s.FuelOut);
            if (!Held("ar:" + v.Id) && v.AmmoContainer != null && v.AmmoContainer.EnabledAmmoRefill != s.Ammo)
                v.AmmoContainer.SwitchRefillState();

            // Reparatur als eigene Arbeit spiegeln (Fenster, Arbeiter-Zuteilung über WorkSync); ausführen tut sie nur der Host.
            var rw = v.RepairWork;
            if (s.Repair && rw == null)
            {
                v.InitRepairWork(isDisplayed: false);
                rw = v.RepairWork;
                RepairsMirrored++;
                Plugin.Log.LogInfo($"IFZCOOP squadaction Reparatur gespiegelt {v.Id}");
            }
            else if (!s.Repair && rw != null)
            {
                rw.DeleteWork();
                rw = null;
                RepairsMirrored++;
                Plugin.Log.LogInfo($"IFZCOOP squadaction Reparatur beendet {v.Id}");
            }
            if (rw != null && Mathf.Abs(rw.Progress - s.Progress) > 0.001f)
                rw.SetProgress(s.Progress);
        }

        public void GuestOnEvent(BinaryReader r)
        {
            var ev = (Ev)r.ReadByte();
            switch (ev)
            {
                case Ev.SplitReady:
                    _openMain = r.ReadString();
                    _openEmpty = r.ReadString();
                    _openUntil = Time.realtimeSinceStartup + 5f;
                    TryOpenSplit();
                    break;
                case Ev.Placed:
                {
                    var m = new PendingMove { Character = r.ReadString(), Group = r.ReadString(), Index = r.ReadInt32(), Until = Time.realtimeSinceStartup + 5f };
                    if (!TryPlace(m))
                        _pendingMoves.Add(m);
                    break;
                }
                case Ev.Swapped:
                {
                    string a = r.ReadString(), b = r.ReadString();
                    int ia = r.ReadInt32(), ib = r.ReadInt32();
                    string ga = r.ReadString(), gb = r.ReadString();
                    int afterA = r.ReadInt32(), afterB = r.ReadInt32();
                    ApplySwap(a, b, ia, ib, ga, gb, afterA);
                    break;
                }
                case Ev.Emptied:
                {
                    string id = r.ReadString();
                    _pendingEmpty[id] = Time.realtimeSinceStartup + 10f;
                    TryEmpty();
                    break;
                }
                case Ev.Knowledge:
                    ApplyKnowledge(r);
                    break;
            }
        }

        private void TryOpenSplit()
        {
            if (_openMain == null)
                return;
            var main = FindSquad(_openMain);
            var empty = FindSquad(_openEmpty);
            if (main == null || empty == null)
            {
                if (Time.realtimeSinceStartup > _openUntil)
                {
                    Plugin.Log.LogWarning($"Teilen: Trupp {_openMain}/{_openEmpty} fehlt beim Gast");
                    _openMain = _openEmpty = null;
                }
                return;
            }
            _openMain = _openEmpty = null;
            var ex = GameAccess.Game<ExchangeController>();
            if (ex == null || ex.IsInExchange)
            {
                _session.ShowNotice("Teilen: Tauschfenster ist schon offen");
                Ask(Op.ExchangeEnd, w => { w.Write(""); w.Write(empty.Id); }); // leeren Trupp beim Host wieder weg
                return;
            }
            UnitSync.NetApplying = true;
            try
            {
                empty.SetExchangeInactiveState();
                empty.ResourcesContainer.SetCapacity(0);
                ex.StartExchangeBetweenSplitSquad(main, empty);
                SplitsOpened++;
                Plugin.Log.LogInfo($"IFZCOOP squadaction Teilen offen {main.Id} -> {empty.Id}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Teilen öffnen: " + e);
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        private bool TryPlace(PendingMove m)
        {
            var c = FindMember(m.Character);
            var g = FindSquad(m.Group);
            if (c == null || g == null)
                return false;
            if (c.Group == g)
                return true;
            if (c.Group == null)
                return false;
            UnitSync.NetApplying = true;
            try
            {
                _session.Units?.Forget(c);
                new MembersDragStrategy().PlaceMember(c, g, Mathf.Clamp(m.Index, 0, g.Characters.Count));
                MembersMoved++;
                Plugin.Log.LogInfo($"IFZCOOP squadaction Mitglied {c.Id} -> {g.Id}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Mitglied {m.Character} -> {m.Group}: {e.Message}");
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
            return true;
        }

        private void ApplySwap(string a, string b, int ia, int ib, string ga, string gb, int afterA)
        {
            var ca = FindMember(a);
            var cb = FindMember(b);
            if (ca == null || cb == null || ca.Group == null || cb.Group == null)
                return;
            bool sameGroup = ga == gb;
            if (sameGroup)
            {
                // Reihenfolge im selben Trupp: nur tauschen, wenn sie noch nicht stimmt
                if (ca.Group != cb.Group || IndexIn(ca.Group, ca) == afterA)
                    return;
            }
            else if (ca.Group.Id != ga || cb.Group.Id != gb)
            {
                return; // schon getauscht (oder anders verschoben): UnitSync gleicht den Rest ab
            }
            UnitSync.NetApplying = true;
            try
            {
                _session.Units?.Forget(ca);
                _session.Units?.Forget(cb);
                new MembersDragStrategy().SwapMembers(ca, cb, Mathf.Max(0, ia), Mathf.Max(0, ib));
                MembersMoved++;
                Plugin.Log.LogInfo($"IFZCOOP squadaction Tausch {a} <-> {b}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Tausch {a} <-> {b}: {e.Message}");
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        private void TryEmpty()
        {
            if (_pendingEmpty.Count == 0)
                return;
            float now = Time.realtimeSinceStartup;
            foreach (var kv in _pendingEmpty.ToList())
            {
                var g = FindSquad(kv.Key);
                if (g == null)
                {
                    _pendingEmpty.Remove(kv.Key);
                    continue;
                }
                if (!g.IsEmpty())
                {
                    if (now > kv.Value) _pendingEmpty.Remove(kv.Key);
                    continue;
                }
                _pendingEmpty.Remove(kv.Key);
                UnitSync.NetApplying = true;
                try
                {
                    g.CheckEmptiness();
                    Emptied++;
                    Plugin.Log.LogInfo($"IFZCOOP squadaction Trupp leer {kv.Key}");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Trupp {kv.Key} leeren: {e.Message}");
                }
                finally
                {
                    UnitSync.NetApplying = false;
                }
            }
        }

        private void ApplyKnowledge(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 10000)
                throw new InvalidDataException("zu viele Figuren");
            UnitSync.NetApplying = true;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    string id = r.ReadString();
                    int na = r.ReadInt32();
                    if (na < 0 || na > 256)
                        throw new InvalidDataException("zu viele Tätigkeiten");
                    var acts = new SerializedKnowledgeActivity[na];
                    for (int j = 0; j < na; j++)
                        acts[j] = new SerializedKnowledgeActivity
                        {
                            ID = (KnowledgeActivityType)r.ReadInt32(),
                            CurrentExperience = r.ReadSingle(),
                            MaxStatus = r.ReadBoolean(),
                            LastTimeIncreasedByShootingRange = r.ReadBoolean(),
                        };
                    int ns = r.ReadInt32();
                    if (ns < 0 || ns > 256)
                        throw new InvalidDataException("zu viele Fähigkeiten");
                    var skills = new List<KeyValuePair<SkillId, bool>>(ns);
                    for (int j = 0; j < ns; j++)
                        skills.Add(new KeyValuePair<SkillId, bool>((SkillId)r.ReadInt32(), r.ReadBoolean()));

                    var c = FindAnyCharacter(id);
                    if (c == null)
                        continue;
                    try
                    {
                        c.KnowledgeGainer?.Deserialize(acts);
                        if (c.SkillsHandler != null)
                            foreach (var s in skills)
                            {
                                // nur geänderte: SetActiveStatus löst bei jedem Aktivieren die Wirkung aus
                                var skill = c.SkillsHandler.Skills.FirstOrDefault(x => x.Id == s.Key);
                                if (skill != null && skill.IsActivated != s.Value)
                                    skill.SetActiveStatus(s.Value);
                            }
                        KnowledgeApplied++;
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"Erfahrung {id}: {e.Message}"); }
                }
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        /// <summary>Gast, jeden Frame: Teilen öffnen, sobald der leere Trupp da ist; verschobene Mitglieder; leere Trupps.</summary>
        public void GuestFrame()
        {
            if (_session.Role != Role.Guest || !_session.GuestControlsLocked)
                return;
            if (_openMain != null)
                TryOpenSplit();
            if (_pendingMoves.Count > 0)
            {
                float now = Time.realtimeSinceStartup;
                for (int i = _pendingMoves.Count - 1; i >= 0; i--)
                {
                    var m = _pendingMoves[i];
                    if (TryPlace(m) || now > m.Until)
                        _pendingMoves.RemoveAt(i);
                }
            }
            if (_pendingEmpty.Count > 0)
                TryEmpty();
        }

        // ================================================================ Status / Test

        public static IEnumerable<string> StatusLines()
        {
            foreach (var v in TakeVehicles())
                yield return $"sactv {v.Id} fi={(v.FuelIn ? 1 : 0)} fo={(v.FuelOut ? 1 : 0)} ar={(v.Ammo ? 1 : 0)} rep={(v.Repair ? 1 : 0)}";
            foreach (var g in Squads().Where(x => x.Fraction == Fraction.Player).OrderBy(x => x.Id, StringComparer.Ordinal))
                yield return $"sactg {g.Id} lock={(g.EquipmentLockHandler != null && g.EquipmentLockHandler.IsLocked ? 1 : 0)} n={g.Characters.Count} members={string.Join(",", g.Characters.Where(c => c != null).Select(c => c.Id))}";
            foreach (var c in KnowledgeCharacters().OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                float xp = 0f;
                try { xp = c.KnowledgeGainer.GetAllActivitiesPoints(); } catch { }
                string skills = c.SkillsHandler != null ? string.Join(",", c.SkillsHandler.Skills.Where(s => s.IsActivated).Select(s => s.Id.ToString()).OrderBy(x => x)) : "";
                yield return $"sactk {c.Id} xp={F(xp, "0")} skills={skills}";
            }
        }

        public string StatusLine() =>
            $"squadActions sent={RequestsSent} done={RequestsDone} states={StatesApplied} repairs={RepairsMirrored} moved={MembersMoved} "
            + $"splits={SplitsOpened} emptied={Emptied} knowledge={KnowledgeApplied} discards={Discards}";

        private static T Panel<T>() where T : UnityEngine.Object =>
            Resources.FindObjectsOfTypeAll<T>().FirstOrDefault(p => p is Component c && c.gameObject.scene.IsValid());

        private static void Call(object target, string method, params object[] args) =>
            AccessTools.Method(target.GetType(), method).Invoke(target, args);

        /// <summary>
        /// Testbefehle "sa ...". Beim Gast werden die echten Fenster-Methoden aufgerufen (die Prefixe schicken den Wunsch),
        /// beim Host meist direkt die Spiel-Methode.
        /// </summary>
        public static string Qa(string[] a)
        {
            var s = Plugin.Instance?.Session;
            bool guest = s != null && s.Role == Role.Guest;
            var squads = Squads().Where(g => g.Fraction == Fraction.Player && !g.IsEmpty()).ToList();
            Group withCar = squads.FirstOrDefault(g => g.VehicleHandler != null && g.VehicleHandler.HaveVehicle());
            string what = a.Length > 1 ? a[1] : "";
            switch (what)
            {
                case "leave":
                case "repair":
                case "pour":
                case "fuelin":
                case "fuelout":
                case "lock":
                case "vlock":
                {
                    var g = what == "lock" ? squads.FirstOrDefault() : withCar;
                    if (g == null) return "kein passender Trupp";
                    var v = g.VehicleHandler.Vehicle;
                    bool on = a.Length > 2 && a[2] == "1";
                    if (!guest)
                    {
                        switch (what)
                        {
                            case "leave": LeaveVehicle(g); break;
                            case "repair": LeaveVehicle(g); v.InitRepairWork(false); v.AssignWorkers(); break;
                            case "pour": v.PourFuelToTrunk(); break;
                            case "fuelin": v.ChangeFuelInState(); break;
                            case "fuelout": v.ChangeFuelOutState(); break;
                            case "lock": g.EquipmentLockHandler.SetState(on); break;
                            case "vlock": v.EquipmentLockHandler.SetState(on); break;
                        }
                        return what + " (Host) " + g.Id + (v != null ? " " + v.Id : "");
                    }
                    var p = Panel<SquadPanelInfo>();
                    if (p == null) return "kein Trupp-Fenster";
                    AccessTools.PropertySetter(typeof(SquadPanelInfo), "Squad").Invoke(p, new object[] { g });
                    switch (what)
                    {
                        case "leave": Call(p, "LeaveVehicleInvoke"); break;
                        case "repair": Call(p, "StartRepair"); break;
                        case "pour": Call(p, "PourFuelToTrunk"); break;
                        case "fuelin": Call(p, "ChangeFuelInState"); break;
                        case "fuelout": Call(p, "ChangeFuelOutState"); break;
                        case "lock": Call(p, "HandleSquadLockStateChanged", on); break;
                        case "vlock": Call(p, "HandleVehicleLockStateChanged", on); break;
                    }
                    return what + " (Fenster) " + g.Id + (v != null ? " " + v.Id : "");
                }
                case "ammo":
                {
                    var v = withCar != null ? withCar.VehicleHandler.Vehicle : Vehicles().FirstOrDefault(x => x.AmmoContainer != null);
                    if (v == null) return "kein Fahrzeug";
                    if (!guest) { v.AmmoContainer.SwitchRefillState(); return "ammo (Host) " + v.Id + "=" + v.AmmoContainer.EnabledAmmoRefill; }
                    var p = Panel<VehicleAmmoContainerPanel>();
                    if (p == null) return "kein Munitions-Fenster";
                    AccessTools.Field(typeof(VehicleAmmoContainerPanel), "_currentVehicle").SetValue(p, v);
                    Call(p, "HandleButtonClicked");
                    return "ammo (Fenster) " + v.Id + "=" + v.AmmoContainer.EnabledAmmoRefill;
                }
                case "abrepair":
                {
                    var v = Vehicles().FirstOrDefault(x => x.Owner == null && x.RepairWork == null && x.IsDiscovered && !x.Health.IsMax)
                            ?? Vehicles().FirstOrDefault(x => x.Owner == null && x.RepairWork == null);
                    if (v == null) return "kein freies Fahrzeug";
                    if (!guest) { v.InitRepairWork(false); v.AssignWorkers(); return "abrepair (Host) " + v.Id; }
                    var p = Panel<AbandonedVehiclePanelInfo>();
                    if (p == null) return "kein Fahrzeug-Fenster";
                    AccessTools.Field(typeof(AbandonedVehiclePanelInfo), "_vehicle").SetValue(p, v);
                    Call(p, "StartRepair");
                    return "abrepair (Fenster) " + v.Id;
                }
                case "cancelrepair":
                {
                    var v = Vehicles().FirstOrDefault(x => x.RepairWork != null);
                    if (v == null) return "keine Reparatur";
                    if (!guest) { v.RepairWork.DeleteWork(); return "cancelrepair (Host) " + v.Id; }
                    var p = Panel<VehicleInRepairPanel>();
                    if (p == null) return "kein Reparatur-Fenster";
                    AccessTools.Field(typeof(VehicleInRepairPanel), "_displayedVehicle").SetValue(p, v);
                    AccessTools.Field(typeof(VehicleInRepairPanel), "_displayedWork").SetValue(p, v.RepairWork);
                    Call(p, "OnCancelButtonClicked");
                    return "cancelrepair (Fenster) " + v.Id;
                }
                case "split":
                {
                    var g = squads.FirstOrDefault(x => x.Characters.Count >= 2);
                    if (g == null) return "kein Trupp mit 2+ Leuten";
                    GameAccess.Game<SquadSplitPreparer>().PrepareSquadForSplit(g);
                    return "split " + g.Id;
                }
                case "place":
                {
                    // letztes Mitglied des ersten Trupps mit 2+ Leuten in den zuletzt entstandenen anderen Trupp (auch leer)
                    var from = squads.FirstOrDefault(x => x.Characters.Count >= 2);
                    var to = Squads().Where(x => x.Fraction == Fraction.Player && x != from).LastOrDefault();
                    if (from == null || to == null) return "keine zwei Trupps";
                    var c = from.Characters[from.Characters.Count - 1];
                    new MembersDragStrategy().PlaceMember(c, to, to.Characters.Count);
                    return "place " + c.Id + " " + from.Id + " -> " + to.Id;
                }
                case "swap":
                {
                    if (squads.Count < 2) return "keine zwei Trupps";
                    var c1 = squads[0].Characters[0];
                    var c2 = squads[1].Characters[0];
                    new MembersDragStrategy().SwapMembers(c1, c2, 0, 0);
                    return "swap " + c1.Id + " <-> " + c2.Id;
                }
                case "endx":
                {
                    // Tauschfenster beenden wie bei einem neuen Befehl
                    var ex = GameAccess.Game<ExchangeController>();
                    var g = ex?.SquadInExchange;
                    if (g == null) return "kein Tausch offen";
                    var field = AccessTools.Field(g.OrdersHandlers.GetType(), "OnTriedToAddOrGiveOrder");
                    (field?.GetValue(g.OrdersHandlers) as Delegate)?.DynamicInvoke();
                    return "endx " + g.Id + " inExchange=" + ex.IsInExchange;
                }
                case "xp":
                {
                    // "sa xp <KnowledgeActivityType> [n]": erstes Mitglied des ersten Trupps
                    var c = squads.FirstOrDefault()?.Characters.FirstOrDefault();
                    if (c?.KnowledgeGainer == null) return "keine Figur";
                    var type = (KnowledgeActivityType)Enum.Parse(typeof(KnowledgeActivityType), a.Length > 2 ? a[2] : "Shooting");
                    int n = a.Length > 3 ? int.Parse(a[3], CultureInfo.InvariantCulture) : 1;
                    for (int i = 0; i < n; i++) c.KnowledgeGainer.Increase(type);
                    var act = c.KnowledgeGainer.GetActivityById(type);
                    return "xp " + c.Id + " " + type + "=" + (act != null ? F(act.CurrentExperience) : "-");
                }
                case "skill":
                {
                    var c = squads.FirstOrDefault()?.Characters.FirstOrDefault();
                    if (c?.SkillsHandler == null) return "keine Figur";
                    var id = (SkillId)Enum.Parse(typeof(SkillId), a.Length > 2 ? a[2] : "SharpShooter");
                    c.SkillsHandler.SetSkillActivated(id, c.SkillsHandler.GetKnowledgeActivityTypeBySkill(id));
                    return "skill " + c.Id + " " + id + "=" + c.SkillsHandler.HasActiveSkill(id);
                }
                case "discard":
                case "discardw":
                {
                    var g = squads.FirstOrDefault();
                    var ctl = GameAccess.Game<ResourceDiscardController>();
                    if (g == null || ctl == null) return "kein Trupp";
                    if (what == "discard")
                    {
                        var res = (ResourceID)Enum.Parse(typeof(ResourceID), a.Length > 2 ? a[2] : "res_food");
                        float count = a.Length > 3 ? float.Parse(a[3], CultureInfo.InvariantCulture) : 1f;
                        DiscardContainerField(ctl) = g.ResourcesContainer;
                        DiscardCharacterField(ctl) = null;
                        DiscardResourceField(ctl) = res;
                        var canvas = DiscardCanvasField(ctl);
                        if (canvas != null) AccessTools.PropertySetter(typeof(DiscardResourceCanvas), "ResourceCount").Invoke(canvas, new object[] { count });
                    }
                    else
                    {
                        var c = g.Characters.FirstOrDefault(x => x.ItemsContainer != null && !x.ItemsContainer.WeaponHandler.UseDefaultWeapon);
                        if (c == null) return "keine Figur mit Waffe";
                        DiscardCharacterField(ctl) = c;
                        DiscardResourceField(ctl) = c.ItemsContainer.WeaponHandler.Weapon.ResourceType;
                    }
                    Call(ctl, "HandleDiscardClicked");
                    return what + " " + g.Id + " " + DiscardResourceField(ctl);
                }
            }
            return "sa leave|repair|pour|fuelin|fuelout|lock 0/1|vlock 0/1|ammo|abrepair|cancelrepair|split|place|swap|endx|xp <Art> [n]|skill <Id>|discard <Res> <Menge>|discardw";
        }
    }
}
