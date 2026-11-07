using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Controllers;
using Gameplay;
using Gameplay.GameResources;
using Gameplay.ObjectOnMap;
using Gameplay.Units.Player.Workers.WorkSystem;
using Gameplay.Units.Player.Workers.WorkSystem.AreaWorkSystem;
using Gameplay.Units.Workers;
using Gameplay.Units.Workers.WorkSystem;
using Gameplay.Units.Workers.WorkSystem.AreaWorkSystem;
using Gameplay.Units.Workers.WorkSystem.Works;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using MapOperator.Runtime.Configs;
using MapOperator.Runtime.Drafts;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Gebiets-Aufträge (Bäume fällen, Rohstoffe sammeln, Bäume pflanzen; AreaWorkController.CreateAreaWork).
    ///
    /// Host: 1×/s eine Unterschrift über alle Gebiete (Kennung, Art, erledigte Teile, Fortschritt, Unterarbeiten mit Ziel
    /// und Fortschritt); bei Änderung (und alle 10 s) der ganze Stand. Dazu „gerade fertig gewordene“ Unterarbeiten
    /// (30 s lang) und Sammelobjekte, die beim Host von der Karte verschwunden sind (GatherableObjectContainer).
    /// Jedes Gebiet bekommt beim Host eine feste Kennung "A&lt;n&gt;"; der Gast ordnet seine Gebiete über gemeinsame
    /// Unterarbeiten zu (beim Laden des Spielstands) oder legt sie selbst an (CreateAreaWork unter NetApplying).
    /// WorkSync benutzt die Kennung im Schlüssel (StableId), damit die Arbeiter-Zuteilung sicher passt.
    ///
    /// Gast: markiert er ein Gebiet (CreateAreaWork) oder löscht es (AggregateWork.DeleteWork an einem AreaWork),
    /// geht das als Wunsch an den Host; bei ihm selbst entsteht nichts. Die eigene Sammel-/Pflanzarbeit des Gastes
    /// (GatherResourcesWork.Execute, PlantTreeWork.Execute) ist gesperrt, Fortschritt, Fällen und Pflanzen kommen vom Host.
    /// </summary>
    internal sealed class AreaWorkSync
    {
        private const float SendInterval = 1f;
        private const float ForceInterval = 10f;
        private const float DoneKeep = 30f;
        private const float MatchDistance = 2.5f;

        public enum Op : byte { Create = 1, Delete = 2 }

        private enum Kind : byte { Object = 0, Spot = 1, Plant = 2 }

        private readonly Session _session;
        private string _sentSig;
        private float _nextSend;
        private float _nextForce;

        // Host: feste Kennungen; Gast: Kennung des Hosts für das zugeordnete eigene Gebiet
        private static readonly Dictionary<AreaWork, string> Ids = new Dictionary<AreaWork, string>();
        private static int _seq;
        // Gast: eigene Objekt-ID → ID des Hosts (wenn ein Objekt über die Lage gefunden wurde)
        private static readonly Dictionary<long, long> Alias = new Dictionary<long, long>();

        // Host: Unterarbeiten beim letzten Blick, gerade fertige Unterarbeiten, verschwundene Sammelobjekte
        private readonly Dictionary<AreaWork, Dictionary<string, AggregatableWork>> _prev = new Dictionary<AreaWork, Dictionary<string, AggregatableWork>>();
        private readonly List<(string area, string key, bool ok, float time)> _done = new List<(string, string, bool, float)>();
        private readonly List<(long id, Vector3 pos, bool gathered)> _gone = new List<(long, Vector3, bool)>();

        // Gast: schon abgeschlossene „fertig“-Einträge
        private readonly HashSet<string> _doneApplied = new HashSet<string>();
        private readonly HashSet<string> _createWarned = new HashSet<string>();

        public int Applied { get; private set; }
        public int Created { get; private set; }
        public int Matched { get; private set; }
        public int Deleted { get; private set; }
        public int CreateFailed { get; private set; }
        public int SubAdded { get; private set; }
        public int SubRemoved { get; private set; }
        public int SubDone { get; private set; }
        public int Felled { get; private set; }
        public int GoneApplied { get; private set; }
        public int RequestsSent { get; private set; }
        public int RequestsDone { get; private set; }

        public AreaWorkSync(Session session)
        {
            _session = session;
        }

        public void ResendAll()
        {
            _sentSig = null;
            _nextSend = 0f;
        }

        public void Reset()
        {
            ResendAll();
            Ids.Clear();
            Alias.Clear();
            HostLeft.Clear();
            _prev.Clear();
            _done.Clear();
            _gone.Clear();
            _doneApplied.Clear();
            _createWarned.Clear();
        }

        // ================================================================ Zugriff

        private static class Fields
        {
            public static readonly AccessTools.FieldRef<AreaWorkController, List<AreaWork>> AreaWorks =
                AccessTools.FieldRefAccess<AreaWorkController, List<AreaWork>>("_areaWorks");
            public static readonly AccessTools.FieldRef<AreaWork, int> Completed =
                AccessTools.FieldRefAccess<AreaWork, int>("_completedWorks");
            public static readonly AccessTools.FieldRef<AreaWork, Dictionary<ResourceID, float>> StartingProfit =
                AccessTools.FieldRefAccess<AreaWork, Dictionary<ResourceID, float>>("_startingProfit");
            public static readonly AccessTools.FieldRef<GatherResourcesWork, float> TimeGathering =
                AccessTools.FieldRefAccess<GatherResourcesWork, float>("_timeGathering");
            public static readonly AccessTools.FieldRef<GatherResourcesWork, float> TimeToGather =
                AccessTools.FieldRefAccess<GatherResourcesWork, float>("_timeToGather");
            public static readonly AccessTools.FieldRef<PlantTreeWork, float> TimePlanting =
                AccessTools.FieldRefAccess<PlantTreeWork, float>("_timePlanting");
            public static readonly AccessTools.FieldRef<PlantTreeWork, float> TimeToPlant =
                AccessTools.FieldRefAccess<PlantTreeWork, float>("_timeToPlant");
            public static readonly AccessTools.FieldRef<PlantTreeWork, string> TreeType =
                AccessTools.FieldRefAccess<PlantTreeWork, string>("_treeType");
            public static readonly AccessTools.FieldRef<AreaWorkTypesContainer, AreaWorkType[]> Types =
                AccessTools.FieldRefAccess<AreaWorkTypesContainer, AreaWorkType[]>("Types");
            public static readonly System.Reflection.MethodInfo Plant = AccessTools.Method(typeof(PlantTreeWork), "Plant");
        }

        private static readonly System.Reflection.MethodInfo SyncTransforms =
            Type.GetType("UnityEngine.Physics, UnityEngine.PhysicsModule")?.GetMethod("SyncTransforms", Type.EmptyTypes);

        private static AreaWorkController Awc => GameAccess.Game<AreaWorkController>();

        private static GatherableObjectContainer Objects => GameAccess.Game<GatherableObjectContainer>();

        private static string F(float f) => Mathf.RoundToInt(f * 2f).ToString(CultureInfo.InvariantCulture);

        private static bool Alive(GatherableObject o) => o != null && !o.IsDestroyed;

        /// <summary>Alle laufenden Gebiete in der Reihenfolge des Spiels.</summary>
        private static List<AreaWork> Areas(AreaWorkController awc)
        {
            var result = new List<AreaWork>();
            if (awc == null)
                return result;
            List<AreaWork> list = null;
            try { list = Fields.AreaWorks(awc); } catch { }
            if (list == null)
                return result;
            foreach (var a in list)
                if (a != null && !a.IsDone)
                    result.Add(a);
            return result;
        }

        private static List<AreaWork> Areas() => Areas(Awc);

        /// <summary>
        /// Feste Kennung eines Gebiets (für WorkSync.BaseKey): beim Host beim ersten Blick vergeben, beim Gast die des Hosts.
        /// null = (noch) unbekannt.
        /// </summary>
        public static string StableId(AreaWork aw)
        {
            if (aw == null)
                return null;
            if (Ids.TryGetValue(aw, out var id))
                return id;
            if (aw.IsDone)
                return null;
            var s = Plugin.Instance?.Session;
            if (s == null || s.Role != Role.Host)
                return null;
            id = "A" + (++_seq).ToString(CultureInfo.InvariantCulture);
            Ids[aw] = id;
            return id;
        }

        private static AreaWork ById(string id)
        {
            foreach (var kv in Ids)
                if (kv.Value == id && kv.Key != null && !kv.Key.IsDone)
                    return kv.Key;
            return null;
        }

        private static void CleanIds(List<AreaWork> live)
        {
            var set = new HashSet<AreaWork>(live);
            foreach (var k in Ids.Keys.ToList())
                if (!set.Contains(k)) Ids.Remove(k);
        }

        /// <summary>Gleicher Schlüssel einer Unterarbeit bei Host und Gast: Objekt-ID, sonst die Lage.</summary>
        private static string SubKey(Work w)
        {
            if (w is GatherResourcesWork g)
            {
                var o = g.GatherableObject;
                if (!ReferenceEquals(o, null) && o.Id != 0L && o.Id != -1L)
                {
                    long id = Alias.TryGetValue(o.Id, out var hid) ? hid : o.Id;
                    return "o" + id.ToString(CultureInfo.InvariantCulture);
                }
                var p = g.GetPosition();
                return "g" + F(p.x) + "," + F(p.z);
            }
            var q = w.GetPosition();
            return (w is PlantTreeWork ? "p" : "x") + F(q.x) + "," + F(q.z);
        }

        private static Dictionary<string, AggregatableWork> Subs(AreaWork a)
        {
            var d = new Dictionary<string, AggregatableWork>();
            foreach (var w in a.Works)
                if (w is AggregatableWork aw)
                    d[SubKey(aw)] = aw;
            return d;
        }

        private static Vector3 ObjPos(GatherableObject o)
        {
            try
            {
                if (o.GetWork() is GatherResourcesWork g)
                    return g.GetPosition();
                return o.transform.position;
            }
            catch { return Vector3.zero; }
        }

        private static float DistXZ(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Sammelobjekt des Hosts beim Gast: über die ID, sonst (z. B. selbst gepflanzte Bäume) über die Lage.</summary>
        private static GatherableObject FindObject(long id, Vector3 pos)
        {
            var c = Objects;
            if (c == null)
                return null;
            if (c.TryGet(id, out var o) && Alive(o) && (pos == Vector3.zero || DistXZ(ObjPos(o), pos) < MatchDistance))
                return o;
            if (pos == Vector3.zero)
                return null;
            GatherableObject best = null;
            float bestD = 1.5f;
            foreach (var kv in c.GatherableObjects)
            {
                var x = kv.Value;
                if (!Alive(x))
                    continue;
                float d = DistXZ(ObjPos(x), pos);
                if (d < bestD) { bestD = d; best = x; }
            }
            if (best != null && best.Id != id && best.Id != 0L)
                Alias[best.Id] = id;
            return best;
        }

        private static MapObjectDraft Draft(string treeType)
        {
            TreesConfig cfg = null;
            try { cfg = GameAccess.Game<MapEssentials.MapConfig>()?.mapGeneratorConfigs.GetConfig<TreesConfig>(); } catch { }
            if (cfg == null)
            {
                try { cfg = Traverse.Create(Awc.PlantTreesCursor).Field("_treesConfig").GetValue<TreesConfig>(); } catch { }
            }
            if (cfg == null)
                return null;
            switch (treeType)
            {
                case "coniferous": return cfg.ConiferousTreeSet.Trees.Elements[0];
                case "palms": return cfg.PalmsTreeSet.Trees.Elements[0];
                case "tropical": return cfg.TropicalTreeSet.Trees.Elements[1];
                default: return cfg.DeciduousTreeSet.Trees.Elements[0];
            }
        }

        private static AreaWorkType PlantType(AreaWorkController awc)
        {
            try
            {
                foreach (var t in Fields.Types(awc.AreaWorkTypesContainer))
                    if (t != null && t.Name == "PlantTree") return t;
            }
            catch { }
            return null;
        }

        /// <summary>Geister-Baum zum Pflanzen anlegen wie der Pflanz-Cursor (prüft die Lage). null = ungültig.</summary>
        private static PlantTreeWork NewPlantWork(AreaWorkController awc, Vector3 pos, string treeType, bool validate)
        {
            var draft = Draft(treeType);
            if (draft == null)
                return null;
            if (string.IsNullOrEmpty(treeType)) treeType = "deciduous";
            var tree = awc.CreateTree(draft, treeType);
            if (validate)
            {
                // wie AreaWorkController.CreateTreeWithDisabledCollider (PhysicsModule ist nicht verwiesen: über Reflection)
                try
                {
                    var col = AccessTools.Property(typeof(Gameplay.ObjectOnMap.Tree), "Collider")?.GetValue(tree);
                    col?.GetType().GetProperty("enabled")?.SetValue(col, false);
                }
                catch { }
                tree.SetPosition(pos);
                try { SyncTransforms?.Invoke(null, null); } catch { }
                return tree.OnCursorReleased(placeObject: true) as PlantTreeWork;
            }
            // wie WorkSerializer.LoadPlantTreeWork
            tree.SetCurrentLocalScale(Vector3.one * 0.3f);
            tree.SetPosition(pos);
            return tree.CreatePlantTreeWork(draft, treeType);
        }

        // ================================================================ Host

        private sealed class SubInfo
        {
            public string Key;
            public Kind Kind;
            public long Obj = -1;
            public Vector3 Pos;
            public string Tree = "";
            public float Time;
            public float Progress;
            public bool Gathered;
        }

        private sealed class AreaInfo
        {
            public string Id;
            public int Res;
            public string Type;
            public int Completed;
            public float Progress;
            public List<KeyValuePair<int, float>> Start = new List<KeyValuePair<int, float>>();
            public List<KeyValuePair<int, float>> Left = new List<KeyValuePair<int, float>>();
            public List<SubInfo> Subs = new List<SubInfo>();
        }

        // Gast: Restmenge je Gebiet vom Host. Die Anzeige „Gesammelte Ressourcen“ rechnet Start - Rest; beim Gast holt
        // niemand Kisten ab, ohne das stünde dort immer 0/220 (Fehlerbericht 2026-11-06).
        private static readonly Dictionary<AreaWork, Dictionary<ResourceID, float>> HostLeft =
            new Dictionary<AreaWork, Dictionary<ResourceID, float>>();
        private static readonly AccessTools.FieldRef<AreaWork, Dictionary<ResourceID, float>> ProfitRef =
            AccessTools.FieldRefAccess<AreaWork, Dictionary<ResourceID, float>>("_profit");
        private static readonly System.Reflection.MethodInfo ProfitChanged = AccessTools.Method(typeof(AreaWork), "WorkProfitChange");

        /// <summary>Postfix AreaWork.UpdateProfit beim Gast: Restmenge des Hosts statt der eigenen Rechnung.</summary>
        public static void GuestProfitPostfix(AreaWork area)
        {
            if (area == null || !HostLeft.TryGetValue(area, out var left))
                return;
            var p = ProfitRef(area);
            if (p == null)
                return;
            p.Clear();
            foreach (var kv in left) p[kv.Key] = kv.Value;
        }

        private static void ApplyLeft(AreaWork area, AreaInfo h)
        {
            var left = new Dictionary<ResourceID, float>();
            foreach (var kv in h.Left) left[(ResourceID)kv.Key] = kv.Value;
            if (HostLeft.TryGetValue(area, out var old) && old.Count == left.Count
                && old.All(kv => left.TryGetValue(kv.Key, out var v) && Mathf.Abs(v - kv.Value) < 0.01f))
                return;
            HostLeft[area] = left;
            // UpdateProfit (Postfix setzt die Werte des Hosts) und OnProfitChanged für offene Fenster
            try { ProfitChanged?.Invoke(area, new object[] { null }); }
            catch (Exception e) { Plugin.Log.LogDebug("Gebiet Restmenge: " + e.Message); }
        }

        private static SubInfo Describe(string key, AggregatableWork w)
        {
            var s = new SubInfo { Key = key, Pos = w.GetPosition(), Progress = w.Progress };
            if (w is GatherResourcesWork g)
            {
                var o = g.GatherableObject;
                s.Kind = !ReferenceEquals(o, null) && o.Id != 0L && o.Id != -1L ? Kind.Object : Kind.Spot;
                if (s.Kind == Kind.Object) s.Obj = o.Id;
                s.Time = Fields.TimeGathering(g);
                s.Gathered = g.IsGatheringDone;
            }
            else if (w is PlantTreeWork p)
            {
                s.Kind = Kind.Plant;
                s.Tree = Fields.TreeType(p) ?? "";
                s.Time = Fields.TimePlanting(p);
                s.Gathered = s.Time >= Fields.TimeToPlant(p);
            }
            else
            {
                s.Kind = Kind.Spot;
            }
            return s;
        }

        private static List<AreaInfo> Take(List<AreaWork> areas)
        {
            var list = new List<AreaInfo>();
            foreach (var a in areas)
            {
                var info = new AreaInfo
                {
                    Id = StableId(a),
                    Res = a.AreaWorkType != null ? (int)a.AreaWorkType.ResourceID : 0,
                    Type = a.AreaWorkType != null ? a.AreaWorkType.Name ?? "" : "",
                    Completed = a.CompletedWorks,
                    Progress = a.Progress,
                };
                if (a.StartingProfit != null)
                    foreach (var kv in a.StartingProfit)
                        info.Start.Add(new KeyValuePair<int, float>((int)kv.Key, kv.Value));
                if (a.Profit != null)
                    foreach (var kv in a.Profit)
                        info.Left.Add(new KeyValuePair<int, float>((int)kv.Key, kv.Value));
                foreach (var kv in Subs(a))
                    info.Subs.Add(Describe(kv.Key, kv.Value));
                list.Add(info);
            }
            return list;
        }

        /// <summary>Welche Unterarbeiten sind seit dem letzten Blick fertig geworden (fertig = beim Gast abschließen)?</summary>
        private void Track(List<AreaWork> areas, float now)
        {
            var cur = new Dictionary<AreaWork, Dictionary<string, AggregatableWork>>();
            foreach (var a in areas) cur[a] = Subs(a);
            foreach (var kv in _prev)
            {
                cur.TryGetValue(kv.Key, out var now2);
                string id = Ids.TryGetValue(kv.Key, out var i) ? i : null;
                if (id == null)
                    continue;
                foreach (var s in kv.Value)
                {
                    if (now2 != null && now2.ContainsKey(s.Key))
                        continue;
                    if (!s.Value.IsDone)
                        continue;
                    bool ok = s.Value is GatherResourcesWork g ? g.IsGatheringDone
                        : s.Value is PlantTreeWork p && Fields.TimePlanting(p) >= Fields.TimeToPlant(p);
                    _done.Add((id, s.Key, ok, now));
                }
            }
            _prev.Clear();
            foreach (var kv in cur) _prev[kv.Key] = kv.Value;
            _done.RemoveAll(d => now - d.time > DoneKeep);
        }

        private static string Sig(List<AreaInfo> list, List<(string area, string key, bool ok, float time)> done, int gone)
        {
            var sb = new StringBuilder();
            foreach (var a in list)
            {
                sb.Append('|').Append(a.Id).Append(' ').Append(a.Res).Append(' ').Append(a.Completed).Append(' ')
                    .Append(Mathf.RoundToInt(a.Progress * 100f)).Append(" L").Append(Mathf.RoundToInt(a.Left.Sum(kv => kv.Value)));
                foreach (var s in a.Subs)
                    sb.Append(' ').Append(s.Key).Append(':').Append(Mathf.RoundToInt(s.Progress * 100f)).Append(s.Gathered ? 'G' : '-');
            }
            sb.Append("|D");
            foreach (var d in done) sb.Append(' ').Append(d.area).Append('/').Append(d.key);
            sb.Append("|X").Append(gone);
            return sb.ToString();
        }

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend)
                return;
            _nextSend = now + SendInterval;
            if (_session.ActiveGuestCount == 0 || !GameAccess.IsGameStarted)
            {
                _gone.Clear();
                _prev.Clear();
                if (Ids.Count > 0)
                {
                    try { CleanIds(Areas()); } catch { }
                }
                return;
            }
            bool force = now >= _nextForce;
            if (force) _nextForce = now + ForceInterval;

            List<AreaInfo> list;
            try
            {
                var areas = Areas();
                Track(areas, now);
                list = Take(areas);
                CleanIds(areas);
            }
            catch (Exception e) { Plugin.Log.LogDebug($"Gebiete-Stand: {e.Message}"); return; }
            var done = _done.ToList();
            var gone = _gone.ToList();
            string sig = Sig(list, done, gone.Count);
            if (!force && sig == _sentSig)
                return;
            _sentSig = sig;
            _gone.Clear(); // nur einmal schicken (Gäste, die später laden, haben sie schon nicht mehr im Spielstand)
            _session.BroadcastToGuests(Protocol.Build(Msg.AreaWorkState, w =>
            {
                w.Write(list.Count);
                foreach (var a in list)
                {
                    w.Write(a.Id ?? "");
                    w.Write(a.Res);
                    w.Write(a.Type ?? "");
                    w.Write(a.Completed);
                    w.Write(a.Progress);
                    w.Write(a.Start.Count);
                    foreach (var kv in a.Start)
                    {
                        w.Write(kv.Key);
                        w.Write(kv.Value);
                    }
                    w.Write(a.Left.Count);
                    foreach (var kv in a.Left)
                    {
                        w.Write(kv.Key);
                        w.Write(kv.Value);
                    }
                    w.Write(a.Subs.Count);
                    foreach (var s in a.Subs)
                    {
                        w.Write(s.Key);
                        w.Write((byte)s.Kind);
                        w.Write(s.Obj);
                        w.Write(s.Pos.x);
                        w.Write(s.Pos.y);
                        w.Write(s.Pos.z);
                        w.Write(s.Tree ?? "");
                        w.Write(s.Time);
                        w.Write(s.Progress);
                        w.Write(s.Gathered);
                    }
                }
                w.Write(done.Count);
                foreach (var d in done)
                {
                    w.Write(d.area);
                    w.Write(d.key);
                    w.Write(d.ok);
                }
                w.Write(gone.Count);
                foreach (var g in gone)
                {
                    w.Write(g.id);
                    w.Write(g.pos.x);
                    w.Write(g.pos.z);
                    w.Write(g.gathered);
                }
            }));
        }

        /// <summary>Postfix GatherableObjectContainer.RemoveGatherable: ein Sammelobjekt ist beim Host von der Karte weg.</summary>
        public void HostOnGone(GatherableObject o)
        {
            if (_session.Role != Role.Host || _session.ActiveGuestCount == 0 || ReferenceEquals(o, null))
                return;
            if (o.Id == 0L || o.Id == -1L)
                return;
            bool gathered = o.GetWork() is GatherResourcesWork g && g.IsGatheringDone;
            _gone.Add((o.Id, ObjPos(o), gathered));
            if (_gone.Count > 20000)
                _gone.RemoveRange(0, _gone.Count - 20000);
        }

        public void HostOnRequest(Peer from, BinaryReader r)
        {
            var op = (Op)r.ReadByte();
            var awc = Awc;
            if (awc == null)
                return;
            try
            {
                if (op == Op.Create)
                {
                    int res = r.ReadInt32();
                    int n = r.ReadInt32();
                    if (n < 0 || n > 5000)
                        throw new InvalidDataException("zu viele Unterarbeiten");
                    var subs = new List<SubInfo>(n);
                    for (int i = 0; i < n; i++)
                    {
                        var s = new SubInfo { Kind = (Kind)r.ReadByte(), Obj = r.ReadInt64() };
                        s.Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        s.Tree = r.ReadString();
                        subs.Add(s);
                    }
                    var area = HostCreate(awc, res, subs);
                    if (area == null)
                    {
                        Plugin.Log.LogWarning($"IFZCOOP area-Wunsch: Gebiet ({(ResourceID)res}, {n} Ziele) nicht angelegt (von {from.Name})");
                        return;
                    }
                    Plugin.Log.LogInfo($"IFZCOOP area angelegt {StableId(area)} {area.AreaWorkType?.Name} {area.Works.Count}/{n} Ziele (von {from.Name})");
                }
                else if (op == Op.Delete)
                {
                    string id = r.ReadString();
                    var area = ById(id);
                    if (area == null)
                    {
                        Plugin.Log.LogWarning($"IFZCOOP area-Wunsch löschen: Gebiet {id} gibt es beim Host nicht");
                        return;
                    }
                    area.DeleteWork();
                    Plugin.Log.LogInfo($"IFZCOOP area gelöscht {id} (von {from.Name})");
                }
                RequestsDone++;
                _nextSend = 0f;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"IFZCOOP area-Wunsch {op}: {e.Message}"); }
        }

        /// <summary>Gebiet so anlegen, wie es der Cursor des Spiels tut (gleiche Prüfungen, Arbeiter automatisch).</summary>
        private static AreaWork HostCreate(AreaWorkController awc, int res, List<SubInfo> subs)
        {
            var type = awc.AreaWorkTypesContainer.GetType((ResourceID)res);
            if (type == null)
                return null;
            var works = new List<AggregatableWork>();
            var seen = new HashSet<GatherableObject>();
            foreach (var s in subs)
            {
                if (s.Kind == Kind.Plant)
                {
                    PlantTreeWork pw = null;
                    try { pw = NewPlantWork(awc, s.Pos, s.Tree, validate: true); }
                    catch (Exception e) { Plugin.Log.LogDebug($"Pflanzen {s.Pos}: {e.Message}"); }
                    if (pw != null) works.Add(pw);
                    continue;
                }
                var o = FindObject(s.Obj, s.Pos);
                if (!Alive(o) || o.IsGatheringBlocked || !seen.Add(o))
                    continue;
                if (o.GetWork() is AggregatableWork w)
                    works.Add(w);
            }
            if (works.Count == 0)
                return null;
            int max = 0;
            try { max = JutsuGamesConfig.Runtime.Configs.GetConfig<EconomyConfig>().MaxAutoAssignWorkers; } catch { }
            var area = awc.CreateAreaWork(type, works, max, assignWorkers: true);
            StableId(area);
            return area;
        }

        // ================================================================ Gast

        private bool GuestCanAsk => _session.Role == Role.Guest && _session.GuestControlsLocked && !UnitSync.NetApplying;

        /// <summary>Prefix AreaWorkController.CreateAreaWork. false = beim Gast nicht anlegen, Wunsch an den Host.</summary>
        public bool GuestCreate(AreaWorkType type, List<AggregatableWork> works)
        {
            if (!GuestCanAsk)
                return true;
            var subs = new List<SubInfo>();
            if (works != null)
                foreach (var w in works)
                    if (w != null)
                        subs.Add(Describe(SubKey(w), w));
            _session.SendToHost(Protocol.Build(Msg.AreaWorkRequest, wr =>
            {
                wr.Write((byte)Op.Create);
                wr.Write(type != null ? (int)type.ResourceID : 0);
                wr.Write(subs.Count);
                foreach (var s in subs)
                {
                    wr.Write((byte)s.Kind);
                    wr.Write(s.Obj);
                    wr.Write(s.Pos.x);
                    wr.Write(s.Pos.y);
                    wr.Write(s.Pos.z);
                    wr.Write(s.Tree ?? "");
                }
            }));
            RequestsSent++;
            // Geister-Bäume des Pflanz-Cursors wieder weg (der Host schickt seine)
            UnitSync.NetApplying = true;
            try
            {
                if (works != null)
                    foreach (var w in works)
                        if (w is PlantTreeWork p)
                        {
                            try { p.DeleteWork(); } catch (Exception e) { Plugin.Log.LogDebug($"Geister-Baum weg: {e.Message}"); }
                        }
            }
            finally { UnitSync.NetApplying = false; }
            Plugin.Log.LogInfo($"IFZCOOP area-Wunsch an Host: {type?.Name} {subs.Count} Ziele");
            return false;
        }

        /// <summary>Prefix AggregateWork.DeleteWork. false = Gebiet beim Gast nicht selbst löschen.</summary>
        public bool GuestDelete(AggregateWork work)
        {
            if (!(work is AreaWork area) || !GuestCanAsk)
                return true;
            string id = StableId(area);
            if (id == null)
            {
                Plugin.Log.LogWarning("IFZCOOP area löschen: Gebiet ist dem Host nicht zugeordnet");
                return false;
            }
            _session.SendToHost(Protocol.Build(Msg.AreaWorkRequest, w =>
            {
                w.Write((byte)Op.Delete);
                w.Write(id);
            }));
            RequestsSent++;
            return false;
        }

        public void GuestOnState(BinaryReader r)
        {
            int na = r.ReadInt32();
            if (na < 0 || na > 5000)
                throw new InvalidDataException("zu viele Gebiete");
            var host = new List<AreaInfo>(na);
            for (int i = 0; i < na; i++)
            {
                var a = new AreaInfo
                {
                    Id = r.ReadString(),
                    Res = r.ReadInt32(),
                    Type = r.ReadString(),
                    Completed = r.ReadInt32(),
                    Progress = r.ReadSingle(),
                };
                int ns = r.ReadInt32();
                if (ns < 0 || ns > 100)
                    throw new InvalidDataException("zu viele Rohstoffe");
                for (int j = 0; j < ns; j++) a.Start.Add(new KeyValuePair<int, float>(r.ReadInt32(), r.ReadSingle()));
                int nl = r.ReadInt32();
                if (nl < 0 || nl > 100)
                    throw new InvalidDataException("zu viele Rohstoffe (Rest)");
                for (int j = 0; j < nl; j++) a.Left.Add(new KeyValuePair<int, float>(r.ReadInt32(), r.ReadSingle()));
                int nsub = r.ReadInt32();
                if (nsub < 0 || nsub > 5000)
                    throw new InvalidDataException("zu viele Unterarbeiten");
                for (int j = 0; j < nsub; j++)
                {
                    var s = new SubInfo { Key = r.ReadString(), Kind = (Kind)r.ReadByte(), Obj = r.ReadInt64() };
                    s.Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    s.Tree = r.ReadString();
                    s.Time = r.ReadSingle();
                    s.Progress = r.ReadSingle();
                    s.Gathered = r.ReadBoolean();
                    a.Subs.Add(s);
                }
                host.Add(a);
            }
            int nd = r.ReadInt32();
            if (nd < 0 || nd > 100000)
                throw new InvalidDataException("zu viele fertige");
            var done = new List<(string area, string key, bool ok)>(nd);
            for (int i = 0; i < nd; i++) done.Add((r.ReadString(), r.ReadString(), r.ReadBoolean()));
            int ng = r.ReadInt32();
            if (ng < 0 || ng > 100000)
                throw new InvalidDataException("zu viele verschwundene");
            var gone = new List<(long id, Vector3 pos, bool gathered)>(ng);
            for (int i = 0; i < ng; i++)
            {
                long id = r.ReadInt64();
                float x = r.ReadSingle(), z = r.ReadSingle();
                gone.Add((id, new Vector3(x, 0f, z), r.ReadBoolean()));
            }

            var awc = Awc;
            if (awc == null)
                return;
            UnitSync.NetApplying = true;
            try
            {
                Apply(awc, host, done, gone);
                Applied++;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Gebiete-Stand: {e.Message}"); }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        private void Apply(AreaWorkController awc, List<AreaInfo> host, List<(string area, string key, bool ok)> done,
            List<(long id, Vector3 pos, bool gathered)> gone)
        {
            // 1. Verschwundene Sammelobjekte
            foreach (var g in gone)
            {
                try { ApplyGone(g.id, g.pos, g.gathered); }
                catch (Exception e) { Plugin.Log.LogDebug($"Objekt {g.id} weg: {e.Message}"); }
            }

            var local = Areas(awc);
            CleanIds(local);

            // 2. Beim Host fertig gewordene Unterarbeiten abschließen (Baum gefällt, Rohstoffe weg, Baum gepflanzt)
            var doneKeys = new HashSet<string>();
            foreach (var d in done)
            {
                string k = d.area + "|" + d.key;
                doneKeys.Add(k);
                if (_doneApplied.Contains(k))
                    continue;
                _doneApplied.Add(k);
                var area = ById(d.area);
                if (area == null)
                    continue;
                if (Subs(area).TryGetValue(d.key, out var sub) && !sub.IsDone)
                {
                    try { CompleteSub(area, sub, d.ok); SubDone++; }
                    catch (Exception e) { Plugin.Log.LogDebug($"Unterarbeit {d.key} fertig: {e.Message}"); }
                }
            }
            _doneApplied.RemoveWhere(k => !doneKeys.Contains(k));

            local = Areas(awc);
            var used = new HashSet<AreaWork>();

            // 3. Gebiete des Hosts zuordnen oder anlegen, dann Unterarbeiten und Fortschritt
            foreach (var h in host)
            {
                if (string.IsNullOrEmpty(h.Id))
                    continue;
                var area = ById(h.Id);
                if (area != null && !local.Contains(area))
                    area = null;
                if (area == null)
                {
                    area = MatchLocal(local, used, h);
                    if (area != null)
                    {
                        Ids[area] = h.Id;
                        Matched++;
                    }
                }
                if (area == null)
                {
                    area = CreateLocal(awc, h);
                    if (area == null)
                        continue;
                    Ids[area] = h.Id;
                    local.Add(area);
                    Created++;
                }
                used.Add(area);
                SyncArea(area, h);
            }

            // 4. Gebiete, die es beim Host nicht (mehr) gibt
            foreach (var a in Areas(awc))
            {
                if (used.Contains(a))
                    continue;
                try { a.DeleteWork(); Deleted++; }
                catch (Exception e) { Plugin.Log.LogDebug($"Gebiet löschen: {e.Message}"); }
                Ids.Remove(a);
            }
        }

        private static AreaWork MatchLocal(List<AreaWork> local, HashSet<AreaWork> used, AreaInfo h)
        {
            var keys = new HashSet<string>(h.Subs.Select(s => s.Key));
            AreaWork best = null;
            int bestN = 0;
            foreach (var a in local)
            {
                if (used.Contains(a) || Ids.ContainsKey(a))
                    continue;
                if ((a.AreaWorkType?.Name ?? "") != h.Type)
                    continue;
                int n = Subs(a).Keys.Count(keys.Contains);
                if (n > bestN) { bestN = n; best = a; }
            }
            return best;
        }

        private AggregatableWork BuildSub(AreaWorkController awc, AreaWork owner, SubInfo s)
        {
            if (s.Kind == Kind.Plant)
            {
                var pw = NewPlantWork(awc, s.Pos, s.Tree, validate: false);
                if (pw != null)
                {
                    Fields.TimePlanting(pw) = s.Time;
                    pw.SetProgress(s.Progress);
                }
                return pw;
            }
            if (s.Kind != Kind.Object)
                return null;
            var o = FindObject(s.Obj, s.Pos);
            if (!Alive(o) || !(o.GetWork() is GatherResourcesWork g))
                return null;
            if (o.AreaWork != null && o.AreaWork != owner)
            {
                // steckt noch in einem anderen (veralteten) Gebiet des Gastes
                try { o.AreaWork.TryRemoveWork(g); } catch { }
            }
            if (o.AreaWork == owner && owner != null)
                return null;
            o.Select();
            Fields.TimeGathering(g) = s.Time;
            return g;
        }

        private AreaWork CreateLocal(AreaWorkController awc, AreaInfo h)
        {
            var type = awc.AreaWorkTypesContainer.GetType((ResourceID)h.Res);
            if (type == null)
            {
                if (_createWarned.Add(h.Id)) Plugin.Log.LogWarning($"IFZCOOP area {h.Id}: Art {(ResourceID)h.Res} unbekannt");
                CreateFailed++;
                return null;
            }
            var works = new List<AggregatableWork>();
            foreach (var s in h.Subs)
            {
                AggregatableWork w = null;
                try { w = BuildSub(awc, null, s); }
                catch (Exception e) { Plugin.Log.LogDebug($"Unterarbeit {s.Key}: {e.Message}"); }
                if (w != null && !works.Contains(w)) works.Add(w);
            }
            if (works.Count == 0)
            {
                if (_createWarned.Add(h.Id)) Plugin.Log.LogWarning($"IFZCOOP area {h.Id}: keines der {h.Subs.Count} Ziele beim Gast gefunden");
                CreateFailed++;
                return null;
            }
            var area = awc.CreateAreaWork(type, works, 0, assignWorkers: false);
            if (area == null)
            {
                CreateFailed++;
                return null;
            }
            var start = new Dictionary<ResourceID, float>();
            foreach (var kv in h.Start) start[(ResourceID)kv.Key] = kv.Value;
            try { Fields.StartingProfit(area) = start; } catch { }
            return area;
        }

        private void SyncArea(AreaWork area, AreaInfo h)
        {
            var mine = Subs(area);
            var want = new Dictionary<string, SubInfo>();
            foreach (var s in h.Subs) want[s.Key] = s;

            // Unterarbeiten, die der Host nicht (mehr) hat
            foreach (var kv in mine)
            {
                if (want.ContainsKey(kv.Key) || kv.Value.IsDone)
                    continue;
                try
                {
                    area.TryRemoveWork(kv.Value);
                    if (kv.Value is PlantTreeWork p)
                        p.DeleteWork();
                    SubRemoved++;
                }
                catch (Exception e) { Plugin.Log.LogDebug($"Unterarbeit {kv.Key} heraus: {e.Message}"); }
            }

            // neue Unterarbeiten
            var awc = Awc;
            foreach (var kv in want)
            {
                if (mine.ContainsKey(kv.Key))
                    continue;
                try
                {
                    var w = BuildSub(awc, area, kv.Value);
                    if (w != null)
                    {
                        area.AddWork(w);
                        SubAdded++;
                    }
                }
                catch (Exception e) { Plugin.Log.LogDebug($"Unterarbeit {kv.Key} hinzu: {e.Message}"); }
            }

            // Fortschritt
            mine = Subs(area);
            foreach (var kv in want)
            {
                if (!mine.TryGetValue(kv.Key, out var w) || w.IsDone)
                    continue;
                var s = kv.Value;
                if (w is GatherResourcesWork g)
                {
                    if (s.Gathered && !g.IsGatheringDone)
                    {
                        Fell(g, s.Time);
                    }
                    else if (!g.IsGatheringDone)
                    {
                        Fields.TimeGathering(g) = s.Time;
                    }
                }
                else if (w is PlantTreeWork p)
                {
                    Fields.TimePlanting(p) = Mathf.Min(s.Time, Fields.TimeToPlant(p) - 0.01f);
                }
                if (Mathf.Abs(w.Progress - s.Progress) > 0.001f)
                    w.SetProgress(s.Progress);
            }
            try { Fields.Completed(area) = h.Completed; } catch { }
            if (Mathf.Abs(area.Progress - h.Progress) > 0.001f)
                area.SetProgress(h.Progress);
            ApplyLeft(area, h);
        }

        /// <summary>Gefällt beim Host: beim Gast fällt der Baum (bzw. verschwindet das Objekt), ohne eigene Kisten.</summary>
        private void Fell(GatherResourcesWork g, float hostTime)
        {
            float need = Fields.TimeToGather(g) + 0.01f;
            Fields.TimeGathering(g) = Mathf.Max(hostTime, need);
            g.GatheringDone(instantiateResources: false);
            Felled++;
        }

        private void CompleteSub(AreaWork area, AggregatableWork sub, bool ok)
        {
            if (sub is GatherResourcesWork g)
            {
                if (ok && !g.IsGatheringDone)
                    Fell(g, 0f);
                if (!g.IsDone)
                    g.MarkDone(); // Gebiet nimmt sie heraus, das Objekt verschwindet
                return;
            }
            if (sub is PlantTreeWork p)
            {
                if (ok)
                {
                    Fields.TimePlanting(p) = Fields.TimeToPlant(p);
                    Fields.Plant?.Invoke(p, null); // Baum wie beim Host (Größe/Drehung würfelt das Spiel selbst)
                    if (!p.IsDone) p.SetDone();
                }
                else
                {
                    area.TryRemoveWork(p);
                    p.DeleteWork();
                }
            }
        }

        private void ApplyGone(long id, Vector3 pos, bool gathered)
        {
            var o = FindObject(id, pos);
            if (!Alive(o))
                return;
            var g = o.GetWork() as GatherResourcesWork;
            if (g != null && g.IsGatheringDone)
                return; // fällt schon
            if (gathered && g != null)
                Fell(g, 0f);
            else
                o.Dispose();
            GoneApplied++;
        }

        // ================================================================ Testbefehle

        /// <summary>
        /// "area gather [Radius] [Anzahl] [ResourceID]" – Gebiet mit Sammelobjekten beim HQ (wie der Cursor),
        /// "area plant [Anzahl]" – Bäume pflanzen beim HQ, "area del [Nr]" – Gebiet löschen, "area list".
        /// Beim Gast gehen anlegen und löschen über die gesperrten Spiel-Methoden als Wunsch an den Host.
        /// </summary>
        public static string Qa(string[] a)
        {
            var awc = Awc;
            if (awc == null)
                return "kein AreaWorkController";
            var hq = HqController.MainHeadquarter;
            var hqPos = hq != null ? hq.transform.position : Vector3.zero;
            string what = a.Length > 1 ? a[1] : "list";
            int max = 0;
            try { max = JutsuGamesConfig.Runtime.Configs.GetConfig<EconomyConfig>().MaxAutoAssignWorkers; } catch { }
            switch (what)
            {
                case "gather":
                {
                    float radius = a.Length > 2 ? float.Parse(a[2], CultureInfo.InvariantCulture) : 80f;
                    int n = a.Length > 3 ? int.Parse(a[3], CultureInfo.InvariantCulture) : 4;
                    var res = a.Length > 4 ? (ResourceID)Enum.Parse(typeof(ResourceID), a[4]) : ResourceID.None;
                    var c = Objects;
                    if (c == null) return "keine Sammelobjekte";
                    var works = c.GatherableObjects.Values
                        .Where(o => Alive(o) && !o.IsGatheringBlocked && o.GetWork() is GatherResourcesWork)
                        .Where(o => res == ResourceID.None || ((GatherResourcesWork)o.GetWork()).ResourceToDrop == res)
                        .Select(o => (o, d: DistXZ(ObjPos(o), hqPos)))
                        .Where(x => x.d < radius)
                        .OrderBy(x => x.d)
                        .Take(n)
                        .Select(x => x.o.GetWork())
                        .ToList();
                    if (works.Count == 0) return "keine freien Sammelobjekte im Umkreis " + radius;
                    var area = awc.CreateAreaWork(awc.AreaWorkTypesContainer.GetType(res), works, max, assignWorkers: true);
                    return area != null ? $"Gebiet {StableId(area)} {area.AreaWorkType?.Name} subs={area.Works.Count}" : $"Wunsch gesendet ({works.Count} Ziele)";
                }

                case "plant":
                {
                    int n = a.Length > 2 ? int.Parse(a[2], CultureInfo.InvariantCulture) : 3;
                    var type = PlantType(awc);
                    if (type == null) return "keine Pflanz-Art";
                    string treeType = "deciduous";
                    try { treeType = GameAccess.Map.GetCurrentBiomeMostCommonTreeType() ?? treeType; } catch { }
                    var works = new List<AggregatableWork>();
                    for (int i = 0; i < 200 && works.Count < n; i++)
                    {
                        float ang = i * 0.7f;
                        float rad = 30f + (i / 9) * 4f;
                        var p = hqPos + new Vector3(Mathf.Cos(ang) * rad, 0f, Mathf.Sin(ang) * rad);
                        try { p = global::Utilities.TerrainUtilities.SampleHeight(p); } catch { }
                        var w = NewPlantWork(awc, p, treeType, validate: true);
                        if (w != null) works.Add(w);
                    }
                    if (works.Count == 0) return "kein Platz zum Pflanzen";
                    var area = awc.CreateAreaWork(type, works, max, assignWorkers: true);
                    return area != null ? $"Gebiet {StableId(area)} PlantTree subs={area.Works.Count}" : $"Wunsch gesendet ({works.Count} Bäume)";
                }

                case "del":
                {
                    int i = a.Length > 2 ? int.Parse(a[2], CultureInfo.InvariantCulture) : 0;
                    var list = Areas(awc).OrderBy(x => StableId(x) ?? "?", StringComparer.Ordinal).ToList();
                    if (i < 0 || i >= list.Count) return "kein Gebiet " + i;
                    string id = StableId(list[i]);
                    list[i].DeleteWork();
                    return "löschen " + id + (list[i].IsDone || !Areas(awc).Contains(list[i]) ? " (erledigt)" : " (Wunsch)");
                }

                default:
                    return string.Join("; ", Areas(awc).Select(x => (StableId(x) ?? "?") + " " + x.AreaWorkType?.Name + " " + x.Works.Count));
            }
        }

        // ================================================================ Status

        private static string Hash(IEnumerable<string> keys)
        {
            uint h = 2166136261;
            foreach (var k in keys.OrderBy(x => x, StringComparer.Ordinal))
                foreach (char ch in k + ";")
                {
                    h ^= ch;
                    h *= 16777619;
                }
            return h.ToString("x8", CultureInfo.InvariantCulture);
        }

        public static IEnumerable<string> StatusLines()
        {
            List<AreaWork> list;
            try { list = Areas(); } catch { list = new List<AreaWork>(); }
            var rows = new List<(string id, AreaWork a, Dictionary<string, AggregatableWork> subs)>();
            foreach (var a in list)
            {
                Dictionary<string, AggregatableWork> s;
                try { s = Subs(a); } catch { s = new Dictionary<string, AggregatableWork>(); }
                rows.Add((StableId(a) ?? "?" + a.AreaWorkType?.Name, a, s));
            }
            yield return $"areacount n={rows.Count} subs={rows.Sum(r => r.subs.Count)}";
            foreach (var r in rows.OrderBy(x => x.id, StringComparer.Ordinal))
                yield return $"areawork {r.id} type={r.a.AreaWorkType?.Name} subs={r.subs.Count} keys={Hash(r.subs.Keys)}";
            foreach (var r in rows.OrderBy(x => x.id, StringComparer.Ordinal))
                yield return $"areaprog {r.id} done={r.a.CompletedWorks} p={r.a.Progress.ToString("0.00", CultureInfo.InvariantCulture)} n={r.a.Workers.Count} felled={r.subs.Values.Count(w => w is GatherResourcesWork g && g.IsGatheringDone)} got={Mathf.RoundToInt((r.a.StartingProfit?.Values.Sum() ?? 0f) - (r.a.Profit?.Values.Sum() ?? 0f))}/{Mathf.RoundToInt(r.a.StartingProfit?.Values.Sum() ?? 0f)}";
            int objs = -1;
            try { objs = Objects?.GatherableObjects.Count ?? -1; } catch { }
            yield return $"areaobj gatherables={objs}";
        }

        /// <summary>Für die Prüfsumme: nur Kennung und Art (Unterarbeiten ändern sich beim Arbeiten laufend).</summary>
        public static IEnumerable<string> ChecksumLines()
        {
            var ids = new List<string>();
            foreach (var a in Areas())
                ids.Add((StableId(a) ?? "?") + " " + a.AreaWorkType?.Name);
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }

        public string StatusLine() =>
            $"areaApplied={Applied} created={Created} matched={Matched} deleted={Deleted} createFailed={CreateFailed} subAdded={SubAdded} subRemoved={SubRemoved} subDone={SubDone} felled={Felled} goneApplied={GoneApplied} requestsSent={RequestsSent} requestsDone={RequestsDone}";
    }
}
