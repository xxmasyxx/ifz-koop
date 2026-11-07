using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Controllers;
using Controllers.CharacterLogic;
using Gameplay.Buildings;
using Gameplay.Rebuilding;
using Gameplay.Units;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using Newtonsoft.Json;
using Serialization.Data.MapObjects.Buildings;
using UnityEngine;
using GameJson = Utilities.JsonSerializer;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Trupp-Ausbildung (Warteschlange im HQ/Gebäude) beim Gast anzeigen. Bisher sah der Gast nach „Ausbilden“ nichts,
    /// bis der fertige Trupp kam (Fehlerbericht 2026-11-06).
    ///
    /// Host: schickt die Warteschlangen aller Gebäude mit Trupp-Ausbildung (wie im Spielstand,
    ///       SerializedBuildingWithSquadModule) bei jeder Änderung und alle 5 s, dazu jede Sekunde den Fortschritt.
    /// Gast: baut die Warteschlange nach (nur zur Anzeige, eigenes Weiterzählen aus: SetState(false)); fertige Trupps
    ///       kommen weiter über SquadSync (Msg.SquadSpawn). „Löschen“ in der Warteschlange geht als Auftrag an den Host.
    /// </summary>
    internal sealed class SquadQueueSync
    {
        private const float ProgressInterval = 1f;
        private const float FullInterval = 5f;

        private static readonly AccessTools.FieldRef<Structure, SquadCreatorModule> ModuleRef =
            AccessTools.FieldRefAccess<Structure, SquadCreatorModule>("SquadCreatorModule");

        private readonly Session _session;
        private string _sentSig;
        private float _nextProgress;
        private float _nextFull;

        public int FullSent { get; private set; }
        public int FullApplied { get; private set; }
        public int ProgressApplied { get; private set; }
        public int CancelsSent { get; private set; }
        public int CancelsDone { get; private set; }
        public static bool NetApplying;

        public SquadQueueSync(Session session)
        {
            _session = session;
        }

        public static SquadCreatorModule Module(Structure s) => s == null ? null : ModuleRef(s);

        public void ResendAll()
        {
            _sentSig = null;
            _nextProgress = 0f;
        }

        // ================================================================ Host

        public void HostUpdate()
        {
            if (_session.Role != Role.Host || _session.ActiveGuestCount == 0 || !GameAccess.IsGameStarted)
                return;
            var ctrl = GameAccess.Game<SquadCreatorController>();
            if (ctrl == null)
                return;
            float now = Time.realtimeSinceStartup;

            string sig = Sig(ctrl, out int requests);
            if (sig != _sentSig || (requests > 0 && now >= _nextFull))
            {
                SendFull(ctrl);
                _sentSig = sig;
                _nextFull = now + FullInterval;
                _nextProgress = now + ProgressInterval;
                return;
            }
            if (requests == 0 || now < _nextProgress)
                return;
            _nextProgress = now + ProgressInterval;
            var list = new List<(string id, int idx, float p)>();
            foreach (var s in ctrl.StructuresWithSquadCreator)
            {
                if (!(s is Building b)) continue;
                var m = Module(s);
                if (m == null) continue;
                for (int i = 0; i < m.Requests.Count; i++)
                    if (m.Requests[i] != null && m.Requests[i].IsSelected)
                        list.Add((b.Id, i, m.Requests[i].Progress));
            }
            _session.BroadcastToGuests(Protocol.Build(Msg.SquadQueue, w =>
            {
                w.Write((byte)1);
                w.Write(list.Count);
                foreach (var e in list)
                {
                    w.Write(e.id);
                    w.Write(e.idx);
                    w.Write(e.p);
                }
            }));
        }

        private void SendFull(SquadCreatorController ctrl)
        {
            var infos = new List<SerializedBuildingWithSquadModule>();
            foreach (var s in ctrl.StructuresWithSquadCreator)
            {
                if (!(s is Building b)) continue;
                var info = new SerializedBuildingWithSquadModule();
                info.Serialize(b);
                infos.Add(info);
            }
            string json;
            try
            {
                json = JsonConvert.SerializeObject(infos, GameJson.DefaultSettings);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Trupp-Ausbildung nicht übertragbar: " + e.Message);
                return;
            }
            _session.BroadcastToGuests(Protocol.Build(Msg.SquadQueue, w =>
            {
                w.Write((byte)0);
                w.Write(json);
            }));
            FullSent++;
        }

        private static string Sig(SquadCreatorController ctrl, out int requests)
        {
            requests = 0;
            var sb = new StringBuilder();
            foreach (var s in ctrl.StructuresWithSquadCreator)
            {
                if (!(s is Building b)) continue;
                var m = Module(s);
                if (m == null) continue;
                sb.Append(b.Id).Append('[');
                foreach (var r in m.Requests)
                {
                    if (r == null) continue;
                    requests++;
                    sb.Append((int)r.RequestType).Append(r.IsSelected ? '*' : '-').Append(r.SquadName).Append(':');
                    foreach (var c in r.GetAllMembers())
                        sb.Append(c != null ? c.Id : "?").Append(',');
                    sb.Append(';');
                }
                sb.Append(']');
            }
            return sb.ToString();
        }

        /// <summary>Msg.SquadQueueCancel vom Gast: Auftrag Nr. idx im Gebäude löschen (wie das X in der Warteschlange).</summary>
        public void HostOnCancel(Peer from, BinaryReader r)
        {
            string buildingId = r.ReadString();
            int idx = r.ReadInt32();
            string squadName = r.ReadString();
            var ctrl = GameAccess.Game<SquadCreatorController>();
            var b = GameAccess.Game<BuildingsController>()?.GetBuildingById(buildingId);
            var m = Module(b);
            if (ctrl == null || m == null || idx < 0 || idx >= m.Requests.Count)
                return;
            var req = m.Requests[idx];
            if (req == null || (req.SquadName ?? "") != squadName)
                return;
            ctrl.HandleRequestDestroyed(req, b);
            CancelsDone++;
            Plugin.Log.LogInfo($"IFZCOOP Ausbildung in {buildingId} auf Wunsch von {from.Name} abgebrochen");
        }

        // ================================================================ Gast

        public void GuestOnState(BinaryReader r)
        {
            byte op = r.ReadByte();
            if (op == 1)
            {
                int n = r.ReadInt32();
                if (n < 0 || n > 200) return;
                var bc = GameAccess.Game<BuildingsController>();
                for (int i = 0; i < n; i++)
                {
                    string id = r.ReadString();
                    int idx = r.ReadInt32();
                    float p = r.ReadSingle();
                    var m = Module(bc?.GetBuildingById(id));
                    if (m == null || idx < 0 || idx >= m.Requests.Count) continue;
                    m.Requests[idx].SetProgress(Mathf.Clamp01(p));
                    ProgressApplied++;
                }
                return;
            }
            if (op != 0)
                return;
            string json = r.ReadString();
            List<SerializedBuildingWithSquadModule> infos;
            try
            {
                infos = JsonConvert.DeserializeObject<List<SerializedBuildingWithSquadModule>>(json, GameJson.DefaultSettings);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Trupp-Ausbildung nicht lesbar: " + e.Message);
                return;
            }
            var ctrl = GameAccess.Game<SquadCreatorController>();
            var citizens = GameAccess.Game<CitizensController>();
            var buildings = GameAccess.Game<BuildingsController>();
            if (infos == null || ctrl == null || citizens == null || buildings == null)
                return;
            NetApplying = true;
            try
            {
                foreach (var info in infos)
                {
                    if (info == null || string.IsNullOrEmpty(info.BuildingId)) continue;
                    var b = buildings.GetBuildingById(info.BuildingId);
                    var m = Module(b);
                    if (m == null) continue;
                    foreach (var old in m.Requests)
                        if (old != null)
                            foreach (var c in old.GetAllMembers())
                                if (c != null) c.SetUnderSquadProduction(false);
                    m.Clear();
                    if (!ctrl.StructuresWithSquadCreator.Contains(b))
                        ctrl.AddStructureWithSquadCreator(b);
                    if (info.Requests != null && info.Requests.Count > 0)
                        m.Deserialize(citizens, info);
                    foreach (var req in m.Requests)
                        if (req != null) req.CreationStructure = b as Gameplay.Core.EnterableSystem.IEnterable;
                    m.SetState(false); // nur Anzeige: Fortschritt und Fertigstellung macht der Host
                    m.InvokeQueueUpdated();
                }
                FullApplied++;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Trupp-Ausbildung übernehmen: " + e.Message);
            }
            finally
            {
                NetApplying = false;
            }
        }

        /// <summary>
        /// Prefix von SquadCreatorController.HandleRequestDestroyed beim Gast (X in der Warteschlange): an den Host
        /// schicken statt lokal löschen (sonst bekäme der Gast die Ausrüstung zurück, der Host bildet weiter aus).
        /// </summary>
        public bool ForwardCancel(SquadCreationRequest request, Structure structure)
        {
            if (_session.Role != Role.Guest || NetApplying || !(structure is Building b))
                return true;
            var m = Module(structure);
            int idx = m != null ? m.Requests.IndexOf(request) : -1;
            if (idx < 0)
                return false;
            _session.SendToHost(Protocol.Build(Msg.SquadQueueCancel, w =>
            {
                w.Write(b.Id);
                w.Write(idx);
                w.Write(request.SquadName ?? "");
            }));
            CancelsSent++;
            _session.ShowNotice("Abbruch der Ausbildung beim Host beauftragt", 4f);
            return false;
        }

        public string StatusLine()
        {
            var ctrl = GameAccess.Game<SquadCreatorController>();
            var parts = new List<string>();
            if (ctrl != null)
                foreach (var s in ctrl.StructuresWithSquadCreator)
                {
                    var m = Module(s);
                    if (m == null || m.Requests.Count == 0) continue;
                    var sel = m.CurrentRequest ?? (m.Requests.Count > 0 ? m.Requests[0] : null);
                    parts.Add($"{(s is Building b ? b.Id : "?")}:{m.Requests.Count}@{(sel != null ? sel.Progress : 0f):0.00}");
                }
            return $"squadQueue requests={string.Join(",", parts)} fullSent={FullSent} fullApplied={FullApplied} progressApplied={ProgressApplied} cancelsSent={CancelsSent} cancelsDone={CancelsDone}";
        }
    }
}
