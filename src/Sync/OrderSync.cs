using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Gameplay.Units;
using Gameplay.Units.Orders;
using Gameplay.Units.Player;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using Newtonsoft.Json;
using Serialization.Data.Orders;
using Steamworks;
using GameJson = Utilities.JsonSerializer;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Trupp-Befehle aller Spieler wirken bei allen.
    ///
    /// Abgefangen wird OrdersHandlers.GiveOrder, aber nur, wenn der Aufruf von einer Spielereingabe kommt
    /// (Klick auf Karte, Gebäude, Gegner, Fahrzeug, Trupp-Panel …). Befehle, die das Spiel selbst vergibt
    /// (Ereignisse, Flucht, Laden), laufen auf jedem Rechner von allein und werden nicht verschickt.
    ///
    /// Der Befehl wird mit dem Speicherformat des Spiels übertragen (Order.GetSerializedData → JSON) und
    /// beim Empfänger so ausgeführt, wie das Spiel gespeicherte Befehle lädt (OrdersLoader).
    /// Der Absender führt seinen Befehl sofort selbst aus; der Host verteilt Gast-Befehle an alle anderen.
    /// </summary>
    internal sealed class OrderSync
    {
        // Klassen, aus denen Spielereingaben kommen (Aufrufer von GiveOrder im Spielcode).
        private static readonly HashSet<string> InputTypes = new HashSet<string>
        {
            "SquadsController", "SquadWeaponCursor", "EnemyMouseEventsHandler", "SwarmClickHandler",
            "VirtualGroup", "AreaScavengeCursor", "Vehicle", "SquadLabel", "SquadPanelInfo",
        };

        // Klassen, die selbst Befehle vergeben; liegt eine davon im Aufrufstapel, ist es keine Eingabe.
        private static readonly HashSet<string> GameLogicTypes = new HashSet<string>
        {
            "EventsSystemController", "GroupBuilder", "OrdersLoader", "RunAwayFromEnterableHandler",
            "GameConsoleCommandHandler", "ExchangeStarter",
        };

        private static readonly AccessTools.FieldRef<OrdersHandlers, Group> GroupField =
            AccessTools.FieldRefAccess<OrdersHandlers, Group>("_group");

        private readonly Session _session;
        private bool _applyingRemote;

        public int Sent { get; private set; }
        public int Received { get; private set; }

        public OrderSync(Session session)
        {
            _session = session;
        }

        private bool Active =>
            (_session.Role == Role.Host && _session.ActiveGuestCount > 0) || _session.GuestControlsLocked;

        /// <summary>Prefix von OrdersHandlers.GiveOrder.</summary>
        public void OnGiveOrder(OrdersHandlers handlers, Order order, bool clearOrders, bool overrideClearByShifting, bool checkLoop, bool clearRelatedData)
        {
            if (_applyingRemote || !Active || order == null)
                return;

            var group = GroupField(handlers);
            if (group == null || !IsPlayerSquad(group) || !CameFromPlayerInput())
                return;

            // Shift (Befehl anhängen) beim Absender auflösen, wie GiveOrder es gleich selbst tut.
            bool effectiveClear = clearOrders;
            if (clearOrders && overrideClearByShifting)
                effectiveClear = !IsShiftPressed(handlers);

            string json;
            try
            {
                var data = order.GetSerializedData();
                if (data == null)
                    return;
                json = JsonConvert.SerializeObject(data, typeof(OrderSerializedData), GameJson.DefaultSettings);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Befehl {order.GetType().Name} lässt sich nicht übertragen: {e.Message}");
                return;
            }

            var message = Build(group.Id, json, effectiveClear, checkLoop, clearRelatedData);
            if (_session.Role == Role.Host)
                _session.BroadcastToGuests(message);
            else
                _session.SendToHost(message);
            _session.SquadViews?.NoteOrder(group.Id, _session.MyId.m_SteamID);
            Sent++;
            Plugin.Log.LogInfo($"IFZCOOP order out: {order.GetType().Name} -> {group.Id}");
        }

        public void HostOnOrder(Peer from, BinaryReader r)
        {
            var o = Read(r);
            if (Apply(o, from.Name))
            {
                _session.SquadViews?.NoteOrder(o.GroupId, from.Id.m_SteamID);
                _session.BroadcastToGuests(Build(o.GroupId, o.Json, o.Clear, o.CheckLoop, o.ClearRelated), except: from.Id);
            }
        }

        public void GuestOnOrder(BinaryReader r)
        {
            Apply(Read(r), "Host");
        }

        private bool Apply(Incoming o, string who)
        {
            var group = FindSquad(o.GroupId);
            if (group == null || group.OrdersHandlers == null)
            {
                Plugin.Log.LogWarning($"Befehl von {who} für Trupp {o.GroupId} verworfen: Trupp gibt es hier nicht");
                return false;
            }

            Order order;
            try
            {
                var data = JsonConvert.DeserializeObject<OrderSerializedData>(o.Json, GameJson.DefaultSettings);
                GameAccess.GameContainer?.Inject(data);
                order = data?.DeserializeOrder();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Befehl von {who} nicht lesbar: {e.Message}");
                return false;
            }
            if (order == null)
            {
                Plugin.Log.LogWarning($"Befehl von {who} für {o.GroupId}: Ziel gibt es hier nicht");
                return false;
            }

            _applyingRemote = true;
            try
            {
                group.OrdersHandlers.GiveOrder(order, o.Clear, overrideClearByShifting: false, checkLoop: o.CheckLoop, clearRelatedData: o.ClearRelated);
            }
            finally
            {
                _applyingRemote = false;
            }
            Received++;
            Plugin.Log.LogInfo($"IFZCOOP order in: {order.GetType().Name} -> {o.GroupId} (von {who})");
            return true;
        }

        // ------------------------------------------------------------- Hilfen

        private sealed class Incoming
        {
            public string GroupId;
            public string Json;
            public bool Clear;
            public bool CheckLoop;
            public bool ClearRelated;
        }

        private static byte[] Build(string groupId, string json, bool clear, bool checkLoop, bool clearRelated)
        {
            return Protocol.Build(Msg.SquadOrder, w =>
            {
                w.Write(groupId);
                w.Write(json);
                w.Write(clear);
                w.Write(checkLoop);
                w.Write(clearRelated);
            });
        }

        private static Incoming Read(BinaryReader r)
        {
            return new Incoming
            {
                GroupId = r.ReadString(),
                Json = r.ReadString(),
                Clear = r.ReadBoolean(),
                CheckLoop = r.ReadBoolean(),
                ClearRelated = r.ReadBoolean(),
            };
        }

        private static Group FindSquad(string id)
        {
            var squads = GameAccess.Game<SquadsController>()?.Squads;
            if (squads == null)
                return null;
            for (int i = 0; i < squads.Count; i++)
                if (squads[i] != null && squads[i].Id == id)
                    return squads[i];
            return null;
        }

        private static bool IsPlayerSquad(Group group)
        {
            var squads = GameAccess.Game<SquadsController>()?.Squads;
            if (squads == null)
                return false;
            for (int i = 0; i < squads.Count; i++)
                if (ReferenceEquals(squads[i], group))
                    return true;
            return false;
        }

        private static bool CameFromPlayerInput()
        {
            bool input = false;
            var frames = new StackTrace(2, false).GetFrames();
            if (frames == null)
                return false;
            foreach (var frame in frames)
            {
                var type = frame.GetMethod()?.DeclaringType;
                // Lambdas und Iteratoren stecken in verschachtelten Compiler-Klassen.
                while (type != null && type.IsNested && type.Name.StartsWith("<", StringComparison.Ordinal))
                    type = type.DeclaringType;
                if (type == null)
                    continue;
                if (GameLogicTypes.Contains(type.Name))
                    return false;
                if (InputTypes.Contains(type.Name))
                    input = true;
            }
            return input;
        }

        // GameKeysController.IsShifPressed (so heißt es im Spiel) kommt aus einer Basisklasse; per Traverse
        // gelesen, damit es egal ist, ob es Feld oder Eigenschaft ist.
        private static bool IsShiftPressed(OrdersHandlers handlers)
        {
            try
            {
                var keys = Traverse.Create(handlers).Field("_gameKeysController");
                if (keys.GetValue() == null)
                    return false;
                var prop = keys.Property("IsShifPressed");
                if (prop.PropertyExists())
                    return prop.GetValue<bool>();
                var field = keys.Field("IsShifPressed");
                return field.FieldExists() && field.GetValue<bool>();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
