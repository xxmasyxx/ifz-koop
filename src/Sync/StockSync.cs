using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Controllers;
using Gameplay.GameResources;
using IfzCoop.Game;
using IfzCoop.Net;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Lager (DESIGN Nr. 10, Analyse P11/W7). Alle Lager teilen sich einen Vorrat: StockroomsController.Container,
    /// dazu VirtualResourcesContainer (u. a. Leichen). Die Rohstoffleiste oben und die Warn-Fähnchen rechnen daraus.
    ///
    /// Host: alle 2 s, wenn sich etwas geändert hat, beide Vorräte und die Haltbarkeitslisten (Nahrung, Fleisch, Leichen).
    /// Nach dem Laden eines Gastes sofort.
    /// Gast: gleicht seine Vorräte auf die Werte des Hosts an (ForceAdd/ForceRemove der Differenz) und übernimmt die
    /// Haltbarkeit. Essen (FoodConsumeController.HandleHourChange) und Verderb (MainResourcesContainer.TimeOfDayChanged)
    /// sind beim Gast gesperrt (UnitPatches), das rechnet der Host.
    /// </summary>
    internal sealed class StockSync
    {
        private const float SendInterval = 2f;

        private readonly Session _session;
        private byte[] _lastSent;
        private float _nextSend;

        public int Applied { get; private set; }

        public StockSync(Session session)
        {
            _session = session;
        }

        public void ResendAll() => _lastSent = null;

        public void Reset() => _lastSent = null;

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend || _session.ActiveGuestCount == 0)
                return;
            _nextSend = now + SendInterval;
            var stock = GameAccess.Game<StockroomsController>();
            if (stock == null)
                return;
            byte[] msg = Protocol.Build(Msg.StockState, w =>
            {
                WriteContainer(w, stock.Container);
                WriteContainer(w, stock.VirtualResourcesContainer);
                WriteList(w, stock.Container.GetFoodRationsExpirations());
                WriteList(w, stock.Container.GetMeatRationsExpirations());
                WriteList(w, stock.VirtualResourcesContainer.GetCorpsesRationsExpirations());
            });
            if (_lastSent != null && _lastSent.SequenceEqual(msg))
                return;
            _lastSent = msg;
            _session.BroadcastToGuests(msg);
        }

        public void GuestOnState(BinaryReader r)
        {
            var main = ReadContainer(r);
            var virt = ReadContainer(r);
            var food = ReadList(r);
            var meat = ReadList(r);
            var corpses = ReadList(r);
            var stock = GameAccess.Game<StockroomsController>();
            if (stock == null)
                return;
            UnitSync.NetApplying = true;
            try
            {
                Apply(stock.Container, main);
                Apply(stock.VirtualResourcesContainer, virt);
                stock.Container.LoadResourceExpirations(ResourceID.res_food_rations, food);
                stock.Container.LoadResourceExpirations(ResourceID.res_meat, meat);
                stock.VirtualResourcesContainer.LoadResourceExpirations(ResourceID.virt_corpse, corpses);
                Applied++;
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        private static readonly HashSet<string> LoggedErrors = new HashSet<string>();

        private static void Apply(MainResourcesContainer container, Dictionary<ResourceID, float> host)
        {
            var ids = new HashSet<ResourceID>(host.Keys);
            foreach (var kv in container.Resources)
                ids.Add(kv.Key);
            foreach (var id in ids)
            {
                host.TryGetValue(id, out float want);
                container.Resources.TryGetValue(id, out float have);
                float diff = want - have;
                if (Mathf.Abs(diff) < 0.0001f)
                    continue;
                try
                {
                    if (diff > 0f)
                        container.ForceAddResource(id, diff, ResourceAddReason.None);
                    else
                        container.ForceRemoveResource(id, -diff);
                }
                catch (Exception e)
                {
                    // Das Spiel ändert die Menge vor den Ereignissen; geworfen hat ein Abonnent. Erstes Mal mit Stack.
                    string key = id + (diff > 0f ? "+" : "-");
                    if (LoggedErrors.Add(key))
                        Plugin.Log.LogWarning($"Lager {id} angleichen ({(diff > 0f ? "dazu" : "weg")} {diff}): {e}");
                    else
                        Plugin.Log.LogWarning($"Lager {id} angleichen ({(diff > 0f ? "dazu" : "weg")}): {e.Message}");
                    try { container.InvokeResourcesUpdated(); }
                    catch (Exception e2) { Plugin.Log.LogDebug("Lager neu zeichnen: " + e2.Message); }
                }
            }
        }

        private static void WriteContainer(BinaryWriter w, ResourcesContainer c)
        {
            var items = c.Resources.Where(kv => Mathf.Abs(kv.Value) > 0.0001f).ToList();
            w.Write(items.Count);
            foreach (var kv in items)
            {
                w.Write((int)kv.Key);
                w.Write(kv.Value);
            }
        }

        private static Dictionary<ResourceID, float> ReadContainer(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 10000)
                throw new InvalidDataException("Lager zu groß");
            var d = new Dictionary<ResourceID, float>(n);
            for (int i = 0; i < n; i++)
                d[(ResourceID)r.ReadInt32()] = r.ReadSingle();
            return d;
        }

        private static void WriteList(BinaryWriter w, List<float> list)
        {
            w.Write(list.Count);
            foreach (var v in list) w.Write(v);
        }

        private static List<float> ReadList(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 10000)
                throw new InvalidDataException("Liste zu groß");
            var list = new List<float>(n);
            for (int i = 0; i < n; i++) list.Add(r.ReadSingle());
            return list;
        }

        /// <summary>Für die Statusdatei: Vorrat (gerundet), sortiert.</summary>
        public static string StatusLine()
        {
            var stock = GameAccess.Game<StockroomsController>();
            if (stock == null)
                return "stock=-";
            var parts = stock.Container.Resources.Where(kv => kv.Value > 0.0001f).OrderBy(kv => (int)kv.Key)
                .Select(kv => kv.Key + ":" + kv.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
            return "stock=" + string.Join(",", parts);
        }
    }
}
