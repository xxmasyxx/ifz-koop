using System;
using System.Collections.Generic;
using System.IO;
using Gameplay.Units;
using Gameplay.Units.Characters;
using Gameplay.Units.Enemy.Hideouts;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using Newtonsoft.Json;
using UnityEngine;
using GameJson = Utilities.JsonSerializer;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Aufgedeckte Verstecke. Ein Versteck deckt sich auf, wenn ein Soldat oder Arbeiter das Gebäude betritt
    /// (Hideout.Reveal). Beim Gast betreten die Marionetten nichts, also bliebe dort die Hauptgruppe des Verstecks
    /// unsichtbar (Group.CanShow fragt Hideout.IsRevealed), auch wenn der Host sie sieht.
    /// Der Host meldet jede Sekunde neu aufgedeckte Verstecke, nach dem Laden eines Gastes alle.
    /// </summary>
    internal sealed class HideoutSync
    {
        private const float SendInterval = 1f;

        private static readonly Action<Hideout> RevealMethod =
            AccessTools.MethodDelegate<Action<Hideout>>(AccessTools.Method(typeof(Hideout), "Reveal"));

        private readonly Session _session;
        private readonly HashSet<string> _sent = new HashSet<string>();
        private float _nextSend;

        public int Applied { get; private set; }

        public HideoutSync(Session session)
        {
            _session = session;
        }

        public void ResendAll() { _sent.Clear(); _countSig = null; }

        public void Reset() { _sent.Clear(); _countSig = null; _pending.Clear(); _requested.Clear(); _createFailLogged.Clear(); _guestOnlyLogged.Clear(); GuestOnly = 0; }

        // ================================================================ Anzahl in der Höhle
        // Die Hauptgruppe einer Höhle wächst beim Host (Hideout.TryAddMainGroupMember); beim Gast ist HideoutsController.CustomUpdate
        // gesperrt, und unsichtbare Gruppen bekommen nur ihren Ankerpunkt. Darum stand beim Gast z. B. 39 statt 47 Infizierte
        // (Bericht 2026-11-09). Der Host meldet die Zahl, der Gast füllt auf oder nimmt weg.

        private const float CountForce = 10f;
        private string _countSig;
        private float _nextCountForce;
        private readonly Dictionary<string, int> _pending = new Dictionary<string, int>();
        public int MembersAdded { get; private set; }
        public int MembersRemoved { get; private set; }

        private static int AliveCount(Group g)
        {
            int n = 0;
            var list = g.Characters;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c != null && (c.Health == null || !c.Health.IsDead())) n++;
            }
            return n;
        }

        private void HostSendCounts(IReadOnlyCollection<Hideout> hideouts, float now)
        {
            var items = new List<(string id, string group, int count)>();
            var sb = new System.Text.StringBuilder();
            foreach (var h in hideouts)
            {
                var g = h?.MainGroup;
                if (g == null || string.IsNullOrEmpty(h.Id) || string.IsNullOrEmpty(g.Id))
                    continue;
                int n = AliveCount(g);
                items.Add((h.Id, g.Id, n));
                sb.Append(h.Id).Append(':').Append(n).Append(',');
            }
            string sig = sb.ToString();
            if (sig == _countSig && now < _nextCountForce)
                return;
            _countSig = sig;
            _nextCountForce = now + CountForce;
            _session.BroadcastToGuests(Protocol.Build(Msg.HideoutCount, w =>
            {
                w.Write((ushort)items.Count);
                foreach (var it in items)
                {
                    w.Write(it.id);
                    w.Write(it.group);
                    w.Write((ushort)Mathf.Clamp(it.count, 0, ushort.MaxValue));
                }
            }));
        }

        public void GuestOnCount(BinaryReader r)
        {
            int n = r.ReadUInt16();
            var host = new Dictionary<string, (string group, int count)>(n);
            for (int i = 0; i < n; i++)
            {
                string id = r.ReadString();
                string group = r.ReadString();
                host[id] = (group, r.ReadUInt16());
            }
            var hc = GameAccess.Game<HideoutsController>();
            var hideouts = hc?.Hideouts;
            if (hideouts == null)
                return;
            RequestMissing(hc, host.Keys);
            int guestOnly = 0;
            foreach (var h in hideouts)
            {
                if (h == null || string.IsNullOrEmpty(h.Id))
                    continue;
                if (!host.TryGetValue(h.Id, out var want))
                {
                    // Höhle nur beim Gast (der Host meldet nur Höhlen mit Hauptgruppe; eine leere zählt hier nicht)
                    if (h.MainGroup != null && !h.MainGroup.IsEmpty())
                    {
                        guestOnly++;
                        if (_guestOnlyLogged.Add(h.Id))
                            Plugin.Log.LogWarning($"IFZCOOP Höhle {h.Id} in Gebäude {h.Building?.ID} gibt es nur beim Gast ({AliveCount(h.MainGroup)} drin)");
                    }
                    continue;
                }
                var g = h.MainGroup;
                if (g == null || g.IsEmpty() || g.Id != want.group)
                {
                    _pending.Remove(h.Id);
                    continue;
                }
                int diff = want.count - AliveCount(g);
                if (diff == 0 || want.count == 0) // leer geräumt: das regelt das Ausräumen der Höhle selbst
                {
                    _pending.Remove(h.Id);
                    continue;
                }
                // erst beim zweiten gleichen Unterschied: Trupps, die gerade aus der Höhle ausrücken, kommen als eigene Gruppe
                if (!_pending.TryGetValue(h.Id, out var lastDiff) || lastDiff != diff)
                {
                    _pending[h.Id] = diff;
                    continue;
                }
                _pending.Remove(h.Id);
                UnitSync.NetApplying = true;
                try
                {
                    if (diff > 0) AddMembers(hc, h, g, Mathf.Min(diff, 100));
                    else RemoveMembers(g, Mathf.Min(-diff, 100));
                    Plugin.Log.LogInfo($"IFZCOOP Höhle {h.Id}: {want.count - diff} -> {want.count}");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Höhle {h.Id} angleichen: {e.Message}");
                }
                finally
                {
                    UnitSync.NetApplying = false;
                }
            }
            GuestOnly = guestOnly;
        }

        private readonly HashSet<string> _guestOnlyLogged = new HashSet<string>();
        public int GuestOnly { get; private set; }

        // ================================================================ fehlende Verstecke
        // Verstecke, die beim Host erst im Spiel entstehen (z. B. eine Horde wird zur Höhle, HideoutsController.TryConvertToHideout),
        // gibt es beim Gast nicht: dort ist HideoutsController.CustomUpdate gesperrt. Folge: beim Gast bleibt das Haus „verlassen“,
        // keine Höhle, keine Anzeige (Log „building … Abandoned -> Hideout“ alle paar Sekunden, Bericht 2026-11-09).
        // Der Gast fragt fehlende IDs an, der Host schickt sie wie im Spielstand (HideoutSerializedInfo), der Gast legt sie an.

        private const float RequestCooldown = 15f;
        private readonly Dictionary<string, float> _requested = new Dictionary<string, float>();
        private readonly HashSet<string> _createFailLogged = new HashSet<string>();
        public int HideoutsCreated { get; private set; }

        private void RequestMissing(HideoutsController hc, IEnumerable<string> hostIds)
        {
            float now = Time.realtimeSinceStartup;
            var missing = new List<string>();
            foreach (var id in hostIds)
            {
                if (string.IsNullOrEmpty(id) || (hc.TryGetHideout(id, out var have) && have != null))
                    continue;
                if (_requested.TryGetValue(id, out var last) && now - last < RequestCooldown)
                    continue;
                _requested[id] = now;
                missing.Add(id);
                if (missing.Count >= 20)
                    break;
            }
            if (missing.Count == 0)
                return;
            _session.SendToHost(Protocol.Build(Msg.HideoutRequest, w =>
            {
                w.Write((ushort)missing.Count);
                foreach (var id in missing) w.Write(id);
            }));
        }

        public void HostOnRequest(Peer from, BinaryReader r)
        {
            int n = r.ReadUInt16();
            if (n > 100)
                return;
            var hc = GameAccess.Game<HideoutsController>();
            var jsons = new List<string>();
            for (int i = 0; i < n; i++)
            {
                string id = r.ReadString();
                if (hc == null || !hc.TryGetHideout(id, out var h) || h == null || h.Building == null)
                    continue;
                try { jsons.Add(JsonConvert.SerializeObject(new HideoutSerializedInfo(h), GameJson.DefaultSettings)); }
                catch (Exception e) { Plugin.Log.LogWarning($"Versteck {id} verpacken: {e.Message}"); }
            }
            if (jsons.Count == 0)
                return;
            _session.SendTo(from.Id, Protocol.Build(Msg.HideoutInfo, w =>
            {
                w.Write((ushort)jsons.Count);
                foreach (var j in jsons) w.Write(j);
            }));
        }

        public void GuestOnInfo(BinaryReader r)
        {
            int n = r.ReadUInt16();
            var hc = GameAccess.Game<HideoutsController>();
            var bc = GameAccess.Game<global::Controllers.BuildingsController>();
            for (int i = 0; i < n; i++)
            {
                string json = r.ReadString();
                if (hc == null || bc == null)
                    continue;
                HideoutSerializedInfo info;
                try { info = JsonConvert.DeserializeObject<HideoutSerializedInfo>(json, GameJson.DefaultSettings); }
                catch (Exception e) { Plugin.Log.LogWarning("Versteck nicht lesbar: " + e.Message); continue; }
                if (info == null || string.IsNullOrEmpty(info.Id))
                    continue;
                if (hc.TryGetHideout(info.Id, out var have) && have != null)
                    continue;
                // Vorher selbst prüfen: scheitert die Prüfung im Spiel (ValidateSerializedInfo), löscht es die Gruppen des Verstecks
                var b = bc.GetBuildingById(info.OccupiedBuildingId);
                string why = b == null ? "Gebäude fehlt"
                    : !hc.IsBuildingValid(b) ? "Gebäude ungeeignet"
                    : b.Hideout != null ? "Gebäude hat schon ein Versteck"
                    : !string.IsNullOrEmpty(info.MainGroupId) && UnitSync.FindGroupPublic(info.MainGroupId) == null ? "Hauptgruppe noch nicht da"
                    : null;
                if (why != null)
                {
                    if (_createFailLogged.Add(info.Id + why))
                        Plugin.Log.LogInfo($"IFZCOOP Versteck {info.Id} noch nicht anlegbar: {why}");
                    continue; // nächste Anfrage in 15 s
                }
                UnitSync.NetApplying = true;
                try
                {
                    var h = hc.CreateHideout(info);
                    if (h != null)
                    {
                        HideoutsCreated++;
                        _requested.Remove(info.Id);
                        Plugin.Log.LogInfo($"IFZCOOP Versteck {info.Id} in Gebäude {b.ID} angelegt (beim Host neu entstanden)");
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Versteck {info.Id} anlegen: {e.Message}");
                }
                finally
                {
                    UnitSync.NetApplying = false;
                }
            }
        }

        private void AddMembers(HideoutsController hc, Hideout h, Group g, int count)
        {
            for (int i = 0; i < count; i++)
            {
                // wie Hideout.TryAddMainGroupMember
                var info = new Controllers.CharacterLogic.CharacterInfo { Name = "Enemy" };
                Character m = hc.CreateHideoutMember(g.Draft.CharacterDraft, info);
                if (m == null)
                    return;
                m.AssignGroup(g, silent: true);
                m.Movement.Teleport(h.Building);
                if (h.Draft.AssignMainGroupToVirtualFight)
                {
                    m.Movement.StopMoving();
                    m.Movement.Block();
                }
                m.DisableObject();
                MembersAdded++;
            }
        }

        private void RemoveMembers(Group g, int count)
        {
            var list = g.Characters;
            for (int i = list.Count - 1; i >= 0 && count > 0; i--)
            {
                var c = list[i];
                if (c == null || (c.Health != null && c.Health.IsDead()))
                    continue;
                c.Kill(SubtractionHpReason.None);
                MembersRemoved++;
                count--;
                list = g.Characters;
                if (i > list.Count) i = list.Count;
            }
        }

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend || _session.ActiveGuestCount == 0)
                return;
            _nextSend = now + SendInterval;
            var hideouts = GameAccess.Game<HideoutsController>()?.Hideouts;
            if (hideouts == null)
                return;
            HostSendCounts(hideouts, now);
            var revealed = new List<string>();
            foreach (var h in hideouts)
                if (h != null && h.IsRevealed && !string.IsNullOrEmpty(h.Id) && _sent.Add(h.Id))
                    revealed.Add(h.Id);
            if (revealed.Count == 0)
                return;
            _session.BroadcastToGuests(Protocol.Build(Msg.HideoutRevealed, w =>
            {
                w.Write(revealed.Count);
                foreach (var id in revealed) w.Write(id);
            }));
        }

        public void GuestOnRevealed(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 10000)
                throw new InvalidDataException("zu viele Verstecke");
            var ids = new HashSet<string>();
            for (int i = 0; i < n; i++) ids.Add(r.ReadString());
            var hideouts = GameAccess.Game<HideoutsController>()?.Hideouts;
            if (hideouts == null || RevealMethod == null)
                return;
            foreach (var h in hideouts)
            {
                if (h == null || h.IsRevealed || !ids.Contains(h.Id))
                    continue;
                UnitSync.NetApplying = true;
                try
                {
                    RevealMethod(h);
                    Applied++;
                    Plugin.Log.LogInfo("IFZCOOP Versteck aufgedeckt: " + h.Id);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Versteck {h.Id} aufdecken: {e.Message}");
                }
                finally
                {
                    UnitSync.NetApplying = false;
                }
            }
        }
    }
}
