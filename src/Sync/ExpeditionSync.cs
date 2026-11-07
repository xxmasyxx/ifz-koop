using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Controllers;
using Expedition;
using Gameplay.GameResources;
using Gameplay.InGameResources;
using Gameplay.Units;
using Gameplay.Units.Characters;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using MapOperator.Runtime.Geography.Coordinates;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Felder der Umgebungskarte (Kartenrand außerhalb der Stadt, ExpeditionTilerTile): ob ein Feld durchsucht wird / ist,
    /// Fortschritt, noch zu findende Kisten, gefundener Restinhalt, Höhlen-Markierung, Kampf-Merker, letzte Horde, Plünder-Sperren
    /// (Marker) und welche Spieler-Trupps gerade in einem Feld sind.
    ///
    /// Host: 1×/s eine Unterschrift je Feld, das „etwas hat“ (wie im Spielstand: ExpeditionTileSerialized); geänderte Felder gehen
    /// kompakt an die Gäste, alle 10 s alles (Gast vergleicht vorher, wendet nur Abweichungen an).
    /// Gast: wendet an wie ExpeditionScavengeController.LoadExpeditionTile (ForceInitScavengeWork mit den Kisten des Hosts,
    /// SetLeftSpaceToScavenge, ResourcesContainer leeren + LoadResources) und frischt Anzeige/Symbole auf. Eigenes Plündern,
    /// Kisten-Würfeln, Horden-/Fahrzeug-Spawn und Ereignis-Auslöser an Feldern sind beim Gast gesperrt (UnitPatches).
    /// Entnehmen per Ziehen im Tauschfenster: ExchangeSync kennt das Feld als Seite (Kind.Tile, ID = "tx,ty,zoom").
    /// </summary>
    internal sealed class ExpeditionSync
    {
        private const float SendInterval = 1f;
        private const float ForceInterval = 10f;
        private const int MaxPerMessage = 200;

        private readonly Session _session;
        private readonly Dictionary<string, string> _sent = new Dictionary<string, string>();
        private string _sentInside;
        private float _nextSend;
        private float _nextForce;

        public int Sent { get; private set; }
        public int Applied { get; private set; }
        public int Missing { get; private set; }
        public int Entered { get; private set; }
        public int Exited { get; private set; }

        public ExpeditionSync(Session session)
        {
            _session = session;
        }

        public void ResendAll()
        {
            _sent.Clear();
            _sentInside = null;
            _nextSend = 0f;
        }

        public void Reset() => ResendAll();

        // ================================================================ Zugriff

        private static readonly AccessTools.FieldRef<ExpeditionTilerTile, HashSet<string>> BlockersField =
            AccessTools.FieldRefAccess<ExpeditionTilerTile, HashSet<string>>("_scavengeBlockerMarkerIds");

        private static readonly MethodInfo RefreshUiMethod = AccessTools.Method(typeof(ExpeditionTilerTile), "InvokeRefreshUI");

        private static ExpeditionGridController _gridCache;

        private static ExpeditionGridController Grid
        {
            get
            {
                if (_gridCache != null)
                    return _gridCache;
                _gridCache = GameAccess.Game<ExpeditionGridController>();
                if (_gridCache == null)
                    _gridCache = UnityEngine.Object.FindObjectOfType<ExpeditionGridController>();
                return _gridCache;
            }
        }

        public static string Key(GoogleGeoTileCoordinate c) =>
            c.Tx.ToString(CultureInfo.InvariantCulture) + "," + c.Ty.ToString(CultureInfo.InvariantCulture) + "," + c.Zoom.ToString(CultureInfo.InvariantCulture);

        private static bool TryParseKey(string key, out GoogleGeoTileCoordinate c)
        {
            c = default;
            var p = (key ?? "").Split(',');
            if (p.Length != 3
                || !int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                || !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
                || !int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int z))
                return false;
            c = new GoogleGeoTileCoordinate(x, y, z);
            return true;
        }

        private static ExpeditionTilerTile Find(GoogleGeoTileCoordinate c)
        {
            var all = Grid?.AllExpeditionTiles;
            if (all != null && all.TryGetValue(c, out var t) && t != null)
                return t;
            var avail = Grid?.AvailableExpeditionTiles;
            if (avail != null)
                foreach (var a in avail)
                    if (a != null && a.Coords.Equals(c))
                        return a;
            return null;
        }

        public static ExpeditionTilerTile FindByKey(string key) => TryParseKey(key, out var c) ? Find(c) : null;

        private static IEnumerable<ExpeditionTilerTile> Tiles()
        {
            var avail = Grid?.AvailableExpeditionTiles;
            if (avail == null)
                yield break;
            for (int i = 0; i < avail.Count; i++)
                if (avail[i] != null)
                    yield return avail[i];
        }

        private static ICollection<string> Blockers(ExpeditionTilerTile t)
        {
            try { return BlockersField(t) ?? (ICollection<string>)Array.Empty<string>(); }
            catch { return Array.Empty<string>(); }
        }

        private static void RefreshUi(ExpeditionTilerTile t)
        {
            try { RefreshUiMethod?.Invoke(t, null); }
            catch (Exception e) { Plugin.Log.LogDebug("Feld-Anzeige: " + e.Message); }
        }

        /// <summary>Hat das Feld etwas, das vom Neuzustand abweicht (dann gehört es in den Abgleich)?</summary>
        private static bool Interesting(ExpeditionTilerTile t) =>
            t.IsScavengingInitialized || t.ResourcesContainer.HasResources || t.MarkedAsLairCount != 0 || t.IsFightInside
            || t.LastSpawnedHordeTime != 0f || Blockers(t).Count > 0;

        private static string F(float f, string fmt = "0.##") => f.ToString(fmt, CultureInfo.InvariantCulture);

        private static string ResSig(ResourcesContainer c)
        {
            var sb = new StringBuilder();
            foreach (var kv in c.Resources.OrderBy(k => (int)k.Key))
                if (Mathf.Abs(kv.Value) > 0.0001f)
                    sb.Append((int)kv.Key).Append(':').Append(F(kv.Value, "0.###")).Append(',');
            return sb.ToString();
        }

        private static string CratesSig(IEnumerable<ResourceCrate> crates)
        {
            var sb = new StringBuilder();
            if (crates != null)
                foreach (var c in crates)
                    if (c != null)
                        sb.Append((int)c.ResourceID).Append(':').Append(F(c.Quantity, "0.###")).Append(',');
            return sb.ToString();
        }

        private static string TileSig(ExpeditionTilerTile t)
        {
            var sb = new StringBuilder();
            var w = t.IsScavengingInitialized ? t.ScavengeWork : null;
            if (w != null)
                sb.Append('I').Append(F(w.TimeToScavenge)).Append('/').Append(F(w.IsDone ? 0f : w.TimeLeftToScavenge)).Append('|').Append(CratesSig(w.ResourcesToFind));
            sb.Append('|').Append(ResSig(t.ResourcesContainer));
            sb.Append('|').Append(t.MarkedAsLairCount).Append(t.IsFightInside ? 'F' : '-').Append(F(t.LastSpawnedHordeTime, "0.#"));
            foreach (var b in Blockers(t).OrderBy(x => x, StringComparer.Ordinal))
                sb.Append('|').Append(b);
            return sb.ToString();
        }

        /// <summary>Spieler-Trupps, die gerade in einem Feld sind: Trupp-ID → Feld.</summary>
        private static SortedDictionary<string, ExpeditionTilerTile> SquadsInside()
        {
            var d = new SortedDictionary<string, ExpeditionTilerTile>(StringComparer.Ordinal);
            var squads = GameAccess.Game<SquadsController>()?.Squads;
            if (squads == null)
                return d;
            foreach (var g in squads)
                if (g != null && !string.IsNullOrEmpty(g.Id) && !g.IsEmpty() && g.Enterable is ExpeditionTilerTile t)
                    d[g.Id] = t;
            return d;
        }

        // ================================================================ Host

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend || _session.ActiveGuestCount == 0 || !GameAccess.IsGameStarted)
                return;
            _nextSend = now + SendInterval;
            bool force = now >= _nextForce;
            if (force) _nextForce = now + ForceInterval;
            if (Grid == null)
                return;

            var changed = new List<ExpeditionTilerTile>();
            SortedDictionary<string, ExpeditionTilerTile> inside;
            string insideSig;
            try
            {
                foreach (var t in Tiles())
                {
                    string key = Key(t.Coords);
                    bool known = _sent.TryGetValue(key, out var last);
                    if (!known && !Interesting(t))
                        continue;
                    string sig = TileSig(t);
                    if (!force && known && last == sig)
                        continue;
                    _sent[key] = sig;
                    changed.Add(t);
                }
                inside = SquadsInside();
                insideSig = string.Join(";", inside.Select(kv => kv.Key + "@" + Key(kv.Value.Coords)));
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("Felder-Stand: " + e.Message);
                return;
            }
            if (changed.Count == 0 && insideSig == _sentInside && !force)
                return;
            _sentInside = insideSig;

            int start = 0;
            do
            {
                int count = Math.Min(MaxPerMessage, changed.Count - start);
                int first = start;
                _session.BroadcastToGuests(Protocol.Build(Msg.ExpeditionState, w =>
                {
                    w.Write(inside.Count);
                    foreach (var kv in inside)
                    {
                        w.Write(kv.Key);
                        WriteCoords(w, kv.Value.Coords);
                    }
                    w.Write(count);
                    for (int i = first; i < first + count; i++)
                        WriteTile(w, changed[i]);
                }));
                Sent++;
                start += count;
            } while (start < changed.Count);
        }

        private static void WriteCoords(BinaryWriter w, GoogleGeoTileCoordinate c)
        {
            w.Write(c.Tx);
            w.Write(c.Ty);
            w.Write(c.Zoom);
        }

        private static GoogleGeoTileCoordinate ReadCoords(BinaryReader r) => new GoogleGeoTileCoordinate(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());

        private static void WriteTile(BinaryWriter w, ExpeditionTilerTile t)
        {
            WriteCoords(w, t.Coords);
            var work = t.IsScavengingInitialized ? t.ScavengeWork : null;
            w.Write(work != null);
            if (work != null)
            {
                w.Write(work.TimeToScavenge);
                w.Write(work.IsDone ? 0f : work.TimeLeftToScavenge);
                var crates = work.ResourcesToFind ?? new List<ResourceCrate>();
                int n = crates.Count(c => c != null);
                w.Write(n);
                foreach (var c in crates)
                {
                    if (c == null) continue;
                    w.Write((int)c.ResourceID);
                    w.Write(c.Quantity);
                    w.Write(c.MaxQuantity);
                }
            }
            var res = t.ResourcesContainer.ConvertToResourceQuantityList();
            w.Write(res.Count);
            foreach (var q in res)
            {
                w.Write((int)q.resourceType);
                w.Write(q.value);
            }
            w.Write(t.MarkedAsLairCount);
            w.Write(t.IsFightInside);
            w.Write(t.LastSpawnedHordeTime);
            var blockers = Blockers(t).ToList();
            w.Write(blockers.Count);
            foreach (var b in blockers) w.Write(b);
        }

        // ================================================================ Gast

        private sealed class TileData
        {
            public GoogleGeoTileCoordinate Coords;
            public bool Init;
            public float Total;
            public float Left;
            public List<ResourceCrate> Crates;
            public List<ResourceQuantity> Resources;
            public int Lair;
            public bool Fight;
            public float HordeTime;
            public List<string> Blockers;
        }

        private static TileData ReadTile(BinaryReader r)
        {
            var d = new TileData { Coords = ReadCoords(r), Init = r.ReadBoolean() };
            if (d.Init)
            {
                d.Total = r.ReadSingle();
                d.Left = r.ReadSingle();
                int n = r.ReadInt32();
                if (n < 0 || n > 5000)
                    throw new InvalidDataException("zu viele Kisten");
                d.Crates = new List<ResourceCrate>(n);
                for (int i = 0; i < n; i++)
                {
                    var id = (ResourceID)r.ReadInt32();
                    float q = r.ReadSingle();
                    float max = r.ReadSingle();
                    d.Crates.Add(new ResourceCrate(id, q, max));
                }
            }
            int m = r.ReadInt32();
            if (m < 0 || m > 1000)
                throw new InvalidDataException("zu viele Rohstoffe");
            d.Resources = new List<ResourceQuantity>(m);
            for (int i = 0; i < m; i++)
                d.Resources.Add(new ResourceQuantity((ResourceID)r.ReadInt32(), r.ReadSingle()));
            d.Lair = r.ReadInt32();
            d.Fight = r.ReadBoolean();
            d.HordeTime = r.ReadSingle();
            int b = r.ReadInt32();
            if (b < 0 || b > 1000)
                throw new InvalidDataException("zu viele Sperren");
            d.Blockers = new List<string>(b);
            for (int i = 0; i < b; i++) d.Blockers.Add(r.ReadString());
            return d;
        }

        public void GuestOnState(BinaryReader r)
        {
            int ni = r.ReadInt32();
            if (ni < 0 || ni > 1000)
                throw new InvalidDataException("zu viele Trupps");
            var inside = new Dictionary<string, GoogleGeoTileCoordinate>();
            for (int i = 0; i < ni; i++)
            {
                string id = r.ReadString();
                inside[id] = ReadCoords(r);
            }
            int n = r.ReadInt32();
            if (n < 0 || n > MaxPerMessage)
                throw new InvalidDataException("zu viele Felder");
            var tiles = new List<TileData>(n);
            for (int i = 0; i < n; i++) tiles.Add(ReadTile(r));

            if (Grid == null)
                return;

            UnitSync.NetApplying = true;
            try
            {
                foreach (var d in tiles)
                {
                    var t = Find(d.Coords);
                    if (t == null)
                    {
                        Missing++;
                        continue;
                    }
                    try
                    {
                        if (Apply(t, d))
                            Applied++;
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"Feld {Key(d.Coords)}: {e.Message}"); }
                }
            }
            finally
            {
                UnitSync.NetApplying = false;
            }

            // Ein- und Aussteigen ohne NetApplying: so bleiben Missions-Ziele u. Ä. beim Gast gesperrt (kommen vom Host)
            try { ApplyInside(inside); }
            catch (Exception e) { Plugin.Log.LogWarning("Trupps in Feldern: " + e.Message); }
        }

        /// <summary>Wie ExpeditionScavengeController.LoadExpeditionTile, aber nur, was abweicht. true = etwas geändert.</summary>
        private static bool Apply(ExpeditionTilerTile t, TileData d)
        {
            bool changed = false;
            if (t.MarkedAsLairCount != d.Lair) { t.MarkedAsLairCount = d.Lair; changed = true; }
            if (t.IsFightInside != d.Fight) { t.IsFightInside = d.Fight; changed = true; }
            if (Mathf.Abs(t.LastSpawnedHordeTime - d.HordeTime) > 0.01f) { t.LastSpawnedHordeTime = d.HordeTime; changed = true; }

            var mine = Blockers(t).ToList();
            foreach (var b in mine)
                if (!d.Blockers.Contains(b)) { t.RemoveScavengeBlocker(b); changed = true; }
            foreach (var b in d.Blockers)
                if (!mine.Contains(b)) { t.AddScavengeBlocker(b); changed = true; }

            if (d.Init)
            {
                var work = t.IsScavengingInitialized ? t.ScavengeWork : null;
                bool wasDone = work != null && work.IsDone;
                if (work == null)
                {
                    work = t.ForceInitScavengeWork(new List<ResourceCrate>(d.Crates));
                    changed = true;
                }
                else if (CratesSig(work.ResourcesToFind) != CratesSig(d.Crates))
                {
                    // Kisten vor der Restzeit setzen: „fertig“ heißt Restzeit 0 und keine Kiste mehr übrig
                    work.ResourcesToFind.Clear();
                    work.ResourcesToFind.AddRange(d.Crates);
                    changed = true;
                }
                if (Mathf.Abs(work.TimeToScavenge - d.Total) > 0.01f)
                {
                    work.SetSpaceToScavenge(d.Total);
                    changed = true;
                }
                bool shouldBeDone = d.Left <= 0f && d.Crates.Count == 0;
                if (d.Left <= 0f)
                    t.TileWasLoadedAsScavenged = true; // kein Fahrzeug-Fund beim Gast (VehiclesSpawner), Fahrzeuge kommen vom Host
                // Restzeit setzen löst OnProgressChange/OnFinish aus: Anzeige, Fragezeichen-Symbole weg (ExpeditionPoiIconsManager)
                if (Mathf.Abs(work.TimeLeftToScavenge - d.Left) > 0.01f || (shouldBeDone && !wasDone))
                {
                    work.SetLeftSpaceToScavenge(d.Left);
                    changed = true;
                }
            }

            var c = t.ResourcesContainer;
            string want = ResSig(d.Resources);
            if (ResSig(c) != want)
            {
                c.Clear();
                c.LoadResources(d.Resources);
                c.InvokeResourcesUpdated();
                changed = true;
            }

            if (changed)
                RefreshUi(t);
            return changed;
        }

        private static string ResSig(List<ResourceQuantity> list)
        {
            var sb = new StringBuilder();
            foreach (var q in list.Where(q => q != null && Mathf.Abs(q.value) > 0.0001f).OrderBy(q => (int)q.resourceType))
                sb.Append((int)q.resourceType).Append(':').Append(F(q.value, "0.###")).Append(',');
            return sb.ToString();
        }

        private void ApplyInside(Dictionary<string, GoogleGeoTileCoordinate> inside)
        {
            var squads = GameAccess.Game<SquadsController>()?.Squads;
            if (squads == null)
                return;
            foreach (var g in squads.ToList())
            {
                if (g == null || string.IsNullOrEmpty(g.Id) || g.IsEmpty())
                    continue;
                ExpeditionTilerTile target = inside.TryGetValue(g.Id, out var coords) ? Find(coords) : null;
                bool changed = false;
                foreach (var c in g.Characters.ToList())
                {
                    if (c == null || c.IsDead())
                        continue;
                    try
                    {
                        if (target != null)
                        {
                            if (ReferenceEquals(c.Enterable, target))
                                continue;
                            if (c.IsInVehicle) c.EnterEnterable(target);
                            else c.EnteredEnterableImmediately(target);
                            changed = true;
                        }
                        else if (c.Enterable is ExpeditionTilerTile)
                        {
                            if (c.IsInVehicle) c.ExitEnterable();
                            else c.ExitEnterableImmediately();
                            changed = true;
                        }
                    }
                    catch (Exception e) { Plugin.Log.LogDebug($"Figur {c.Id} Feld: {e.Message}"); }
                }
                if (!changed)
                    continue;
                if (target != null) Entered++;
                else Exited++;
                Plugin.Log.LogInfo($"IFZCOOP expedition Trupp {g.Id} {(target != null ? "in Feld " + Key(target.Coords) : "aus Feld")}");
            }
        }

        // ================================================================ Tausch (für ExchangeSync)

        /// <summary>Ist o der Behälter eines Feldes? Dann dessen Schlüssel, sonst null.</summary>
        public static string DescribeTile(object o)
        {
            if (!(o is ResourcesContainer))
                return null;
            foreach (var t in Tiles())
                if (ReferenceEquals(t.ResourcesContainer, o))
                    return Key(t.Coords);
            return null;
        }

        public static object ResolveTile(string key) => FindByKey(key)?.ResourcesContainer;

        // ================================================================ Testtreiber

        /// <summary>Feld Nummer i: begehbare, durchsuchbare Felder nach Abstand zum HQ (gleich bei Host und Gast).</summary>
        public static ExpeditionTilerTile QaTile(int i, Vector3 from)
        {
            var list = Tiles().Where(t => t.Scavengable && t.CanGoToTile())
                .OrderBy(t => (t.Pivot - from).sqrMagnitude).ThenBy(t => Key(t.Coords), StringComparer.Ordinal).ToList();
            return i >= 0 && i < list.Count ? list[i] : null;
        }

        public static string Describe(ExpeditionTilerTile t)
        {
            var w = t.IsScavengingInitialized ? t.ScavengeWork : null;
            return $"{Key(t.Coords)} p={F(t.GetScavengeProgress(), "0.00")} find={(w?.ResourcesToFind?.Count ?? -1)} res={ResSig(t.ResourcesContainer)} lair={t.MarkedAsLairCount} done={(t.IsScavenged() ? 1 : 0)}";
        }

        // ================================================================ Status

        /// <summary>Zusammenfassung (auch für die Prüfsumme; ohne laufenden Fortschritt).</summary>
        public static string SummaryLine()
        {
            if (Grid == null)
                return "expedition none";
            int all = 0, init = 0, done = 0, withRes = 0, lairs = 0;
            uint h = 2166136261u;
            foreach (var t in Tiles())
            {
                all++;
                if (t.IsScavengingInitialized) init++;
                if (t.IsScavenged()) done++;
                if (t.MarkedAsLairCount != 0) lairs++;
                if (!t.ResourcesContainer.HasResources)
                    continue;
                withRes++;
                foreach (char ch in Key(t.Coords) + ResSig(t.ResourcesContainer)) h = (h ^ ch) * 16777619u;
            }
            return $"expedition tiles={all} init={init} done={done} withRes={withRes} lairs={lairs} resHash={h}";
        }

        /// <summary>Für ChecksumSync: wie SummaryLine, ohne die Gesamtzahl der Felder (hängt nur an der Karte).</summary>
        public static string ChecksumLine() => System.Text.RegularExpressions.Regex.Replace(SummaryLine(), @" tiles=\d+", "");

        public static IEnumerable<string> StatusLines()
        {
            yield return SummaryLine();
            if (Grid == null)
                yield break;
            foreach (var t in Tiles().Where(Interesting).OrderBy(x => Key(x.Coords), StringComparer.Ordinal))
            {
                var w = t.IsScavengingInitialized ? t.ScavengeWork : null;
                yield return $"exptile {Key(t.Coords)} init={(w != null ? 1 : 0)} p={F(t.GetScavengeProgress(), "0.00")} done={(t.IsScavenged() ? 1 : 0)} find={(w?.ResourcesToFind?.Count ?? 0)} res={ResSig(t.ResourcesContainer)} lair={t.MarkedAsLairCount} fight={(t.IsFightInside ? 1 : 0)} blk={Blockers(t).Count}";
            }
            foreach (var kv in SquadsInside())
                yield return $"expinside {kv.Key} {Key(kv.Value.Coords)}";
        }

        public string StatusLine() =>
            $"expeditionSent={Sent} applied={Applied} missing={Missing} entered={Entered} exited={Exited}";
    }
}
