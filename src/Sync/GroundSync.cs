using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Gameplay.GameResources;
using Gameplay.InGameResources;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Kisten am Boden (Analyse P2 Bodenkisten, P7 Fleisch). Gameplay.GameResources.Resource, verwaltet von
    /// ResourcesController.Resources (ID → Kiste). Sie entstehen beim Fallenlassen, beim Tod (Fleisch, Waffen, Munition),
    /// beim Abbau; sie verschwinden beim Aufheben oder Ablaufen.
    ///
    /// Host: 2×/s Abgleich der Kisten, die am Boden liegen (nicht in der Hand eines Arbeiters): neue, geänderte Menge,
    /// verschwunden. Nach dem Laden eines Gastes die ganze Liste (der Gast räumt dann Bodenkisten weg, die der Host nicht hat).
    /// Gast: legt Kisten mit der ID des Hosts an (wie beim Laden: CreateResource, Deserialize, AddResource) und entfernt sie
    /// auf Ansage. Eigene Kisten legt er nicht mehr ab (ResourcesController.DropResourceToCustomPos gesperrt, UnitPatches).
    /// </summary>
    internal sealed class GroundSync
    {
        private const float SendInterval = 0.5f;
        private const int MaxPerMessage = 300;

        private sealed class Crate
        {
            public string Id;
            public int Type;
            public float Quantity;
            public Vector3 Position;
            public float Expiration;
            public bool Visible;
        }

        private static readonly AccessTools.FieldRef<Resource, float> ExpirationField =
            AccessTools.FieldRefAccess<Resource, float>("_expirationTime");
        private static readonly AccessTools.FieldRef<Resource, bool> VisibleField =
            AccessTools.FieldRefAccess<Resource, bool>("_isVisible");

        private readonly Session _session;
        private readonly Dictionary<string, Crate> _sent = new Dictionary<string, Crate>();
        private bool _sendFull = true;
        private float _nextSend;

        public int Applied { get; private set; }

        public GroundSync(Session session)
        {
            _session = session;
        }

        public void ResendAll()
        {
            _sent.Clear();
            _sendFull = true;
        }

        public void Reset() => ResendAll();

        // ================================================================ Host

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend || _session.ActiveGuestCount == 0 || !GameAccess.IsGameStarted)
                return;
            _nextSend = now + SendInterval;
            var controller = GameAccess.Game<ResourcesController>();
            if (controller == null)
                return;

            var current = new Dictionary<string, Crate>();
            foreach (var kv in controller.Resources)
            {
                var r = kv.Value;
                if (r == null || r.IsDestroying || r.Character != null || r.ResourceCrate == null)
                    continue;
                current[kv.Key] = new Crate
                {
                    Id = kv.Key,
                    Type = (int)r.ResourceCrate.ResourceID,
                    Quantity = r.ResourceCrate.Quantity,
                    Position = r.Position,
                    Expiration = ExpirationField(r),
                    Visible = VisibleField(r),
                };
            }

            var upserts = new List<Crate>();
            foreach (var c in current.Values)
                if (_sendFull || !_sent.TryGetValue(c.Id, out var last) || Mathf.Abs(last.Quantity - c.Quantity) > 0.001f)
                    upserts.Add(c);
            var gone = new List<string>();
            foreach (var id in _sent.Keys)
                if (!current.ContainsKey(id))
                    gone.Add(id);
            bool full = _sendFull;
            _sendFull = false;
            _sent.Clear();
            foreach (var kv in current) _sent[kv.Key] = kv.Value;

            if (!full && upserts.Count == 0 && gone.Count == 0)
                return;
            // Volle Liste in einem Stück (der Gast räumt danach auf), sonst in Häppchen.
            if (full)
            {
                Send(true, upserts, gone);
                return;
            }
            for (int start = 0; start < Math.Max(upserts.Count, 1); start += MaxPerMessage)
                Send(false, upserts.GetRange(start, Math.Min(MaxPerMessage, Math.Max(0, upserts.Count - start))), start == 0 ? gone : new List<string>());
        }

        private void Send(bool full, List<Crate> upserts, List<string> gone)
        {
            _session.BroadcastToGuests(Protocol.Build(Msg.GroundState, w =>
            {
                w.Write(full);
                w.Write(upserts.Count);
                foreach (var c in upserts)
                {
                    w.Write(c.Id);
                    w.Write(c.Type);
                    w.Write(c.Quantity);
                    w.Write(c.Position.x);
                    w.Write(c.Position.y);
                    w.Write(c.Position.z);
                    w.Write(c.Expiration);
                    w.Write(c.Visible);
                }
                w.Write(gone.Count);
                foreach (var id in gone) w.Write(id);
            }));
        }

        // ================================================================ Gast

        private static readonly Func<ResourcesController, ResourceCrate, Vector3, bool, float, float, Resource> CreateResource =
            AccessTools.MethodDelegate<Func<ResourcesController, ResourceCrate, Vector3, bool, float, float, Resource>>(
                AccessTools.Method(typeof(ResourcesController), "CreateResource"));
        private static readonly Action<ResourcesController, Resource> AddResource =
            AccessTools.MethodDelegate<Action<ResourcesController, Resource>>(
                AccessTools.Method(typeof(ResourcesController), "AddResource"));

        public void GuestOnState(BinaryReader r)
        {
            bool full = r.ReadBoolean();
            int n = r.ReadInt32();
            if (n < 0 || n > 100000)
                throw new InvalidDataException("zu viele Kisten");
            var upserts = new List<Crate>(n);
            for (int i = 0; i < n; i++)
            {
                upserts.Add(new Crate
                {
                    Id = r.ReadString(),
                    Type = r.ReadInt32(),
                    Quantity = r.ReadSingle(),
                    Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                    Expiration = r.ReadSingle(),
                    Visible = r.ReadBoolean(),
                });
            }
            int g = r.ReadInt32();
            if (g < 0 || g > 100000)
                throw new InvalidDataException("zu viele Kisten");
            var gone = new List<string>(g);
            for (int i = 0; i < g; i++) gone.Add(r.ReadString());

            var controller = GameAccess.Game<ResourcesController>();
            if (controller == null)
                return;
            UnitSync.NetApplying = true;
            try
            {
                foreach (var c in upserts)
                {
                    try { Upsert(controller, c); }
                    catch (Exception e) { Plugin.Log.LogWarning($"Bodenkiste {c.Id}: {e.Message}"); }
                }
                foreach (var id in gone)
                    Remove(controller, id);
                if (full)
                {
                    // Bodenkisten, die der Host nicht hat (vom Gast selbst abgelegt), weg. Kisten in Arbeiterhänden bleiben.
                    var keep = new HashSet<string>();
                    foreach (var c in upserts) keep.Add(c.Id);
                    var extra = new List<string>();
                    foreach (var kv in controller.Resources)
                        if (kv.Value != null && kv.Value.Character == null && !keep.Contains(kv.Key))
                            extra.Add(kv.Key);
                    foreach (var id in extra)
                        Remove(controller, id);
                }
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        private void Upsert(ResourcesController controller, Crate c)
        {
            if (controller.Resources.TryGetValue(c.Id, out var existing) && existing != null)
            {
                if (existing.ResourceCrate != null && Mathf.Abs(existing.ResourceCrate.Quantity - c.Quantity) > 0.001f)
                {
                    existing.ResourceCrate.SetQuantity(c.Quantity);
                    Applied++;
                }
                return;
            }
            if (CreateResource == null || AddResource == null)
                return;
            var type = (ResourceID)c.Type;
            var resource = CreateResource(controller, new ResourceCrate(type, c.Quantity), c.Position, false, 1f, c.Expiration);
            if (resource == null)
                return;
            resource.Deserialize(new Resource.SerializedData(c.Id, new ResourceQuantity(type, c.Quantity), c.Position, c.Expiration, c.Visible));
            if (c.Visible) resource.Show();
            else resource.Hide();
            AddResource(controller, resource);
            Applied++;
        }

        private void Remove(ResourcesController controller, string id)
        {
            if (!controller.Resources.TryGetValue(id, out var resource) || resource == null || resource.IsDestroying)
                return;
            resource.Destroy(Resource.DestroyReason.PickedBySquad);
            Applied++;
        }

        /// <summary>Für die Statusdatei: Zahl der Bodenkisten und Prüfsumme (ID, Art, Menge).</summary>
        public static string StatusLine()
        {
            var controller = GameAccess.Game<ResourcesController>();
            if (controller == null)
                return "ground=-";
            int count = 0;
            long sum = 0;
            foreach (var kv in controller.Resources)
            {
                var r = kv.Value;
                if (r == null || r.IsDestroying || r.Character != null || r.ResourceCrate == null)
                    continue;
                count++;
                string s = kv.Key + ":" + (int)r.ResourceCrate.ResourceID + ":" + r.ResourceCrate.Quantity.ToString("0.###", CultureInfo.InvariantCulture);
                uint h = 2166136261u;
                foreach (char ch in s) h = (h ^ ch) * 16777619u;
                sum += h;
            }
            return $"ground={count} groundHash={sum}";
        }
    }
}
