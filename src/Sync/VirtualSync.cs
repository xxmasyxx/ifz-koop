using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Convoy.Locations;
using Data.Resorces;
using Gameplay.GameResources;
using Gameplay.Units.Enemy;
using Gameplay.Units.Spawning;
using Gameplay.Units.Swarms;
using Gameplay.Units.Virtual;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using Newtonsoft.Json;
using UnityEngine;
using GameJson = Utilities.JsonSerializer;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Umgebungskarte (Analyse W10; Paket J): Horden außerhalb der Stadt (VirtualGroup) und Schwärme.
    ///
    /// Host: 1×/s Liste der virtuellen Gruppen (ID, Ort, Größe, sichtbar; neue mit VirtualGroup.SerializedInfo wie im Spielstand)
    /// und der Schwärme (ID, Ort, Gruppen-IDs).
    /// Gast: legt fehlende wie beim Laden an (gleiche ID), entfernt überzählige (beim Host z. B. in eine echte Gruppe umgewandelt,
    /// die kommt über UnitSync), lässt sie zum Ort des Hosts gleiten und zeigt sie wie beim Host. Eigene Spawns, Takte und
    /// Umwandlungen sind beim Gast gesperrt (UnitPatches).
    /// </summary>
    internal sealed class VirtualSync
    {
        private const float SendInterval = 1f;
        private const float TeleportDistance = 60f;

        private struct VEntry
        {
            public string Id;
            public Vector3 Pos;
            public int Size;
            public bool Visible;
        }

        private struct SEntry
        {
            public string Id;
            public Vector3 Pos;
            public List<string> Groups;
        }

        private readonly Session _session;
        private readonly HashSet<string> _sentGroups = new HashSet<string>();
        private float _nextSend;

        // Gast
        private readonly Dictionary<string, Vector3> _groupTargets = new Dictionary<string, Vector3>();
        private readonly Dictionary<string, Vector3> _swarmTargets = new Dictionary<string, Vector3>();

        public int Created { get; private set; }
        public int Removed { get; private set; }
        public int SwarmsCreated { get; private set; }
        public int SwarmsRemoved { get; private set; }

        public VirtualSync(Session session)
        {
            _session = session;
        }

        public void ResendAll() => _sentGroups.Clear();

        public void Reset()
        {
            ResendAll();
            _groupTargets.Clear();
            _swarmTargets.Clear();
        }

        private static VirtualGroupsController Groups => GameAccess.Game<VirtualGroupsController>();
        private static SwarmSpawner Spawner => GameAccess.Game<SwarmSpawner>();

        private static bool IsVisible(Swarm s) => Traverse.Create(s).Field<bool>("_isVisible").Value;

        // ================================================================ Host

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend || _session.ActiveGuestCount == 0 || !GameAccess.IsGameStarted)
                return;
            _nextSend = now + SendInterval;
            var vgc = Groups;
            if (vgc == null)
                return;

            var groups = vgc.Groups.Where(g => g != null && !g.IsEmpty() && !string.IsNullOrEmpty(g.ID)).ToList();
            var swarms = Spawner?.Swarms?.Where(s => s != null && !string.IsNullOrEmpty(s.Id)).ToList() ?? new List<Swarm>();
            if (groups.Count == 0 && swarms.Count == 0 && _sentGroups.Count == 0)
                return;

            var infos = new List<string>();
            foreach (var g in groups)
            {
                if (_sentGroups.Contains(g.ID))
                    continue;
                try { infos.Add(JsonConvert.SerializeObject(g.GetSerializedInfo(), GameJson.DefaultSettings)); }
                catch (Exception e) { Plugin.Log.LogWarning($"Horde {g.ID} lässt sich nicht übertragen: {e.Message}"); }
            }
            _sentGroups.Clear();
            foreach (var g in groups) _sentGroups.Add(g.ID);

            _session.BroadcastToGuests(Protocol.Build(Msg.VirtualState, w =>
            {
                w.Write(groups.Count);
                foreach (var g in groups)
                {
                    w.Write(g.ID);
                    Vector3 p = g.transform.position;
                    w.Write(p.x); w.Write(p.y); w.Write(p.z);
                    w.Write(g.ICharacters.Count);
                    w.Write(g.IsVisible());
                }
                w.Write(infos.Count);
                foreach (var i in infos) w.Write(i);
                w.Write(swarms.Count);
                foreach (var s in swarms)
                {
                    w.Write(s.Id);
                    Vector3 p = s.transform.position;
                    w.Write(p.x); w.Write(p.y); w.Write(p.z);
                    w.Write(IsVisible(s));
                    var ids = s.VirtualGroups.Where(v => v != null).Select(v => v.ID).ToList();
                    w.Write(ids.Count);
                    foreach (var id in ids) w.Write(id);
                }
            }));
        }

        // ================================================================ Gast

        public void GuestOnState(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 10000) throw new InvalidDataException("zu viele Horden");
            var vlist = new List<VEntry>(n);
            for (int i = 0; i < n; i++)
                vlist.Add(new VEntry { Id = r.ReadString(), Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), Size = r.ReadInt32(), Visible = r.ReadBoolean() });
            int m = r.ReadInt32();
            if (m < 0 || m > 10000) throw new InvalidDataException("zu viele Horden");
            var infos = new Dictionary<string, VirtualGroup.SerializedInfo>();
            for (int i = 0; i < m; i++)
            {
                string json = r.ReadString();
                try
                {
                    var info = JsonConvert.DeserializeObject<VirtualGroup.SerializedInfo>(json, GameJson.DefaultSettings);
                    if (info != null && !string.IsNullOrEmpty(info.ID)) infos[info.ID] = info;
                }
                catch (Exception e) { Plugin.Log.LogWarning("Horde nicht lesbar: " + e.Message); }
            }
            int k = r.ReadInt32();
            if (k < 0 || k > 10000) throw new InvalidDataException("zu viele Schwärme");
            var slist = new List<SEntry>(k);
            var swarmVisible = new Dictionary<string, bool>();
            for (int i = 0; i < k; i++)
            {
                var e = new SEntry { Id = r.ReadString(), Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()) };
                swarmVisible[e.Id] = r.ReadBoolean();
                int c = r.ReadInt32();
                if (c < 0 || c > 10000) throw new InvalidDataException("zu viele Gruppen");
                e.Groups = new List<string>(c);
                for (int j = 0; j < c; j++) e.Groups.Add(r.ReadString());
                slist.Add(e);
            }

            var vgc = Groups;
            if (vgc == null)
                return;
            UnitSync.NetApplying = true;
            try
            {
                var hostIds = new HashSet<string>(vlist.Select(v => v.Id));
                foreach (var g in vgc.Groups.Where(g => g != null && !hostIds.Contains(g.ID)).ToList())
                {
                    Plugin.Log.LogInfo($"IFZCOOP virtual remove {g.ID}");
                    _groupTargets.Remove(g.ID);
                    try
                    {
                        g.KillAllCharacters();
                        if (g != null && g.gameObject != null) UnityEngine.Object.Destroy(g.gameObject);
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"Horde {g.ID} entfernen: {e.Message}"); }
                    Removed++;
                }

                foreach (var v in vlist)
                {
                    var g = vgc.Get(v.Id);
                    if (g == null)
                    {
                        if (!infos.TryGetValue(v.Id, out var info))
                            continue;
                        try { g = Create(vgc, info); }
                        catch (Exception e) { Plugin.Log.LogWarning($"Horde {v.Id} anlegen: {e}"); }
                        if (g == null)
                            continue;
                    }
                    _groupTargets[v.Id] = v.Pos;
                    // Größe: überzählige Figuren sterben (Kämpfe draußen rechnet der Host)
                    int extra = g.ICharacters.Count - v.Size;
                    if (extra > 0 && v.Size > 0)
                    {
                        var list = g.ICharacters.OfType<VirtualCharacter>().Reverse().Take(extra).ToList();
                        foreach (var c in list) c.Health?.Kill();
                    }
                    if (v.Visible && !g.IsVisible()) g.Show();
                    else if (!v.Visible && g.IsVisible()) g.Hide();
                }

                ApplySwarms(slist, swarmVisible);
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        private VirtualGroup Create(VirtualGroupsController vgc, VirtualGroup.SerializedInfo info)
        {
            var tr = Traverse.Create(vgc);
            var factory = tr.Field<VirtualGroup.Factory>("_factory").Value;
            var charFactory = tr.Field<VirtualCharacter.Factory>("_charactersFactory").Value;
            var config = tr.Field<GroupsConfig>("_groupsConfig").Value;
            var draft = config?.GetDraftByName(info.DraftName);
            if (factory == null || charFactory == null || draft == null)
                return null;
            var g = factory.Create(info.ID, draft);
            g.Movement.Teleport(info.Position);
            if (info.PatrollingDisabled) g.DisablePatrolling();
            if (info.MyZone != LocationType.NormalPlace) g.LoadMyZone(info.MyZone, info.MyZoneLocation);
            foreach (var ci in info.Characters ?? new Controllers.CharacterLogic.CharacterInfo[0])
            {
                var c = charFactory.Create(g, ci, draft.CharacterDraft);
                c.SetWeapon(info.weaponID != ResourceID.None
                    ? ResourcesDataContainer.Instance.GetWeaponById(info.weaponID)
                    : ResourcesDataContainer.Instance.MeleeWeapon);
                g.Add(c);
            }
            if (info.HelpingIds != null) g.HelpingIds.AddRange(info.HelpingIds);
            AccessTools.Method(typeof(VirtualGroupsController), "Add")?.Invoke(vgc, new object[] { g });
            g.Hide(shouldHideImmediately: true);
            Created++;
            Plugin.Log.LogInfo($"IFZCOOP virtual create {info.ID} {info.DraftName} n={g.ICharacters.Count}");
            return g;
        }

        private void ApplySwarms(List<SEntry> slist, Dictionary<string, bool> visible)
        {
            var spawner = Spawner;
            if (spawner == null)
                return;
            var hostIds = new HashSet<string>(slist.Select(s => s.Id));
            foreach (var s in spawner.Swarms.Where(s => s != null && !hostIds.Contains(s.Id)).ToList())
            {
                Plugin.Log.LogInfo($"IFZCOOP swarm remove {s.Id}");
                _swarmTargets.Remove(s.Id);
                try
                {
                    s.HideAndDisable();
                    UnityEngine.Object.Destroy(s.gameObject);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"Schwarm {s.Id} entfernen: {e.Message}"); }
                SwarmsRemoved++;
            }
            var vgc = Groups;
            foreach (var e in slist)
            {
                var s = spawner.Get(e.Id);
                if (s == null)
                {
                    s = spawner.CreateSwarm(e.Id);
                    s.transform.position = e.Pos;
                    SwarmsCreated++;
                    Plugin.Log.LogInfo($"IFZCOOP swarm create {e.Id} groups={e.Groups.Count}");
                }
                foreach (var gid in e.Groups)
                {
                    var g = vgc?.Get(gid);
                    if (g != null && !s.VirtualGroups.Contains(g))
                        s.Add(g);
                }
                _swarmTargets[e.Id] = e.Pos;
                if (visible.TryGetValue(e.Id, out bool vis) && vis != IsVisible(s))
                    AccessTools.Method(typeof(Swarm), vis ? "Show" : "Hide")?.Invoke(s, null);
            }
        }

        /// <summary>Gast, jeden Frame: Horden und Schwärme zum Ort des Hosts gleiten lassen.</summary>
        public void GuestFrame()
        {
            if (!_session.GuestControlsLocked || (_groupTargets.Count == 0 && _swarmTargets.Count == 0))
                return;
            float k = 1f - Mathf.Exp(-4f * Time.deltaTime);
            var vgc = Groups;
            if (vgc != null)
                foreach (var g in vgc.Groups)
                {
                    if (g == null || !_groupTargets.TryGetValue(g.ID, out var t)) continue;
                    var tr = g.transform;
                    tr.position = (tr.position - t).sqrMagnitude > TeleportDistance * TeleportDistance ? t : Vector3.Lerp(tr.position, t, k);
                }
            var spawner = Spawner;
            if (spawner != null)
                foreach (var s in spawner.Swarms)
                {
                    if (s == null || !_swarmTargets.TryGetValue(s.Id, out var t)) continue;
                    var tr = s.transform;
                    tr.position = (tr.position - t).sqrMagnitude > TeleportDistance * TeleportDistance ? t : Vector3.Lerp(tr.position, t, k);
                }
        }

        /// <summary>Für die Statusdatei.</summary>
        public static IEnumerable<string> StatusLines()
        {
            var inv = CultureInfo.InvariantCulture;
            var vgc = Groups;
            if (vgc != null)
                foreach (var g in vgc.Groups.Where(x => x != null).OrderBy(x => x.ID, StringComparer.Ordinal).Take(15))
                    yield return $"virtual {g.ID} {g.Draft?.name} n={g.ICharacters.Count} pos={g.transform.position.x.ToString("0", inv)},{g.transform.position.z.ToString("0", inv)} vis={(g.IsVisible() ? 1 : 0)}";
            var sp = Spawner;
            if (sp != null)
                foreach (var s in sp.Swarms.Where(x => x != null).OrderBy(x => x.Id, StringComparer.Ordinal))
                    yield return $"swarm {s.Id} groups={s.VirtualGroups.Count}+{s.Groups.Count} pos={s.transform.position.x.ToString("0", inv)},{s.transform.position.z.ToString("0", inv)} vis={(IsVisible(s) ? 1 : 0)}";
        }
    }
}
