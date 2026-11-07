using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Controllers;
using Gameplay.Buildings;
using Gameplay.GameResources;
using Gameplay.InGameResources;
using Gameplay.Production;
using Gameplay.Rebuilding;
using Gameplay.Rebuilding.StateMachine;
using Gameplay.Research;
using Gameplay.Units.Workers;
using Gameplay.Units.Workers.WorkSystem;
using Gameplay.Units.Workers.WorkSystem.Works;
using Gameplay.Vehicles;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using Newtonsoft.Json;
using Serialization.Data.MapObjects.Buildings;
using UI.InfoPanels;
using UnityEngine;
using GameJson = Utilities.JsonSerializer;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Einstellungen der Betriebe: was eine Produktion herstellt (laufendes und nächstes Rezept, Restmenge, Fortschritt grob),
    /// Werkstatt-Aufträge (Warteschlange, gewählter Auftrag, Fortschritt, Restkosten), Sägewerk-Schalter („nur nachgewachsene
    /// Bäume“), Reparaturtrupp-Wichtigkeiten je Gebäudeart und Gebäude-Anpassung (Farbe, Verstärkung, Name, laufender Auftrag).
    ///
    /// Host: 1×/s eine Unterschrift über alles; bei Änderung (und alle 10 s) der ganze Stand (Msg.ProductionState).
    /// Gast: übernimmt den Stand (Rezepte mit ProductionWork.ProductionData/SetProductionData, Werkstatt-Warteschlange wie
    /// CarWorkshopHandler.Deserialize). Eigene Wirkung ist gesperrt (UnitPatches): Produktion fertigstellen/Kosten abziehen,
    /// Werkstatt-Auftrag abschließen oder abbrechen (beides erzeugt sonst Fahrzeuge mit Gast-IDs), Anpassung abschließen.
    /// Klicks des Gastes (Rezept wählen, Werkstatt-Auftrag geben/wählen/abbrechen, Schalter, Wichtigkeit, Anpassen) gehen als
    /// Wunsch (Msg.ProductionRequest) an den Host, der sie mit den Spiel-Methoden ausführt.
    /// </summary>
    internal sealed class ProductionSync
    {
        private const float SendInterval = 1f;
        private const float ForceInterval = 10f;

        public enum Op : byte
        {
            Production = 1,
            ShopBuild = 10, ShopModify = 11, ShopScrap = 12, ShopCancel = 13, ShopSelect = 14,
            Customize = 20,
            Sawmill = 30, Handyman = 31,
        }

        private readonly Session _session;
        private string _sentSig;
        private float _nextSend;
        private float _nextForce;
        private Snapshot _last;
        private float _nextReapply;

        public int Applied { get; private set; }
        public int ProdChanged { get; private set; }
        public int ShopRebuilt { get; private set; }
        public int LookChanged { get; private set; }
        public int Cleaned { get; private set; }
        public int GaragesPlaced { get; private set; }
        public int MissingProductions { get; private set; }
        public int RequestsSent { get; private set; }
        public int RequestsDone { get; private set; }

        public ProductionSync(Session session)
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
            _last = null;
        }

        // ================================================================ Zugriff

        private static readonly AccessTools.FieldRef<Building, CarWorkshopHandler> ShopHandlerF =
            AccessTools.FieldRefAccess<Building, CarWorkshopHandler>("CarWorkshopHandler");
        private static readonly AccessTools.FieldRef<Building, GarageHandler> GarageF =
            AccessTools.FieldRefAccess<Building, GarageHandler>("GarageHandler");
        private static readonly AccessTools.FieldRef<Building, BuildingCustomizationHandler> CustF =
            AccessTools.FieldRefAccess<Building, BuildingCustomizationHandler>("CustomizationHandler");
        private static readonly AccessTools.FieldRef<Building, BuildingEnforcementHandler> EnfF =
            AccessTools.FieldRefAccess<Building, BuildingEnforcementHandler>("EnforcementHandler");
        private static readonly AccessTools.FieldRef<CarWorkshopHandler, Building> HandlerBuildingF =
            AccessTools.FieldRefAccess<CarWorkshopHandler, Building>("_building");
        private static readonly AccessTools.FieldRef<ProductionWork, int> DailyF =
            AccessTools.FieldRefAccess<ProductionWork, int>("_dailyProducedAmount");
        private static readonly AccessTools.FieldRef<Structure, string> NameF =
            AccessTools.FieldRefAccess<Structure, string>("StructureName");
        private static readonly AccessTools.FieldRef<Structure, bool> NameOverF =
            AccessTools.FieldRefAccess<Structure, bool>("IsStructureNameOverriden");

        private static BuildingsController Bc => GameAccess.Game<BuildingsController>();
        private static CarWorkshopController Shops => GameAccess.Game<CarWorkshopController>();

        private static string F(float f) => Mathf.RoundToInt(f * 2f).ToString(CultureInfo.InvariantCulture);
        private static string P(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>Gleicher Schlüssel bei Host und Gast: Haus-ID, sonst Lage (Felder, Fischer …).</summary>
        public static string KeyOf(Structure s)
        {
            if (s == null)
                return null;
            if (s is Building b)
                return "b" + b.ID.ToString(CultureInfo.InvariantCulture);
            var p = s.transform.position;
            return "s" + F(p.x) + "," + F(p.z);
        }

        private static StructureStateType StateOf(Structure s) => s?.StateMachine?.CurrentState?.StateType ?? StructureStateType.None;

        /// <summary>Alle Produktionen mit Schlüssel (sortiert), aus der Arbeitsliste des Spiels.</summary>
        public static List<KeyValuePair<string, ProductionWork>> Productions()
        {
            var list = new List<KeyValuePair<string, ProductionWork>>();
            var works = GameAccess.Game<WorkController>()?.Works;
            if (works == null)
                return list;
            var seen = new HashSet<ProductionWork>();
            var count = new Dictionary<string, int>();
            foreach (var w in works)
            {
                if (!(w is ProductionWork pw) || !seen.Add(pw) || pw.Structure == null)
                    continue;
                string k = KeyOf(pw.Structure);
                count.TryGetValue(k, out int n);
                count[k] = n + 1;
                list.Add(new KeyValuePair<string, ProductionWork>(n == 0 ? k : k + "#" + n, pw));
            }
            list.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
            return list;
        }

        private static ProductionWork FindProduction(string key) => Productions().FirstOrDefault(kv => kv.Key == key).Value;

        private static string ProdKeyOf(ProductionWork pw) => Productions().FirstOrDefault(kv => ReferenceEquals(kv.Value, pw)).Key;

        /// <summary>Häuser mit Werkstatt oder Garage.</summary>
        private static List<Building> ShopBuildings()
        {
            var c = Shops;
            if (c == null)
                return new List<Building>();
            return c.BuildingsWithWorkshop.Concat(c.BuildingsWithGarage).Where(b => b != null && ShopHandlerF(b) != null)
                .Distinct().OrderBy(b => b.ID).ToList();
        }

        private static string ReqJson(CarWorkshopRequest q)
        {
            var s = new SerializedCarWorkshopRequest();
            s.SerializeRequest(q);
            s.Progress = 0f;
            return JsonConvert.SerializeObject(s, GameJson.DefaultSettings);
        }

        private static void RaiseQueueUpdated(CarWorkshopHandler h)
        {
            try
            {
                var d = AccessTools.Field(typeof(CarWorkshopHandler), "OnQueueUpdated")?.GetValue(h) as System.Action;
                d?.Invoke();
            }
            catch (Exception e) { Plugin.Log.LogDebug("Werkstatt-Anzeige: " + e.Message); }
        }

        // ================================================================ Stand

        private sealed class ProdE
        {
            public string Key, Cur, Next;
            public float Time, Limit;
            public int Daily;
        }

        private sealed class ShopE
        {
            public long Id;
            public List<string> Json = new List<string>();
            public List<float> Prog = new List<float>();
            public bool HasWork;
            public float TimeTo, TimeDone, CurRes, ConsumeRes;
            public List<KeyValuePair<int, int>> Cost = new List<KeyValuePair<int, int>>();
            public List<KeyValuePair<int, int>> Used = new List<KeyValuePair<int, int>>();
            public bool HasGarage;
            public Vector3 GaragePos, GarageEuler;
        }

        private sealed class LookE
        {
            public long Id;
            public bool DefWall, DefRoof;
            public Color Wall, Roof;
            public int Enf;
            public string Name; // null = nicht umbenannt
            public bool HasReq, ReqWall, ReqRoof, ReqEnf;
            public Color ReqWallColor, ReqRoofColor;
            public int ReqEnfType;
        }

        private sealed class Snapshot
        {
            public List<ProdE> Prods = new List<ProdE>();
            public List<ShopE> Shops = new List<ShopE>();
            public List<LookE> Looks = new List<LookE>();
            public List<KeyValuePair<long, bool>> Saws = new List<KeyValuePair<long, bool>>();
            public List<KeyValuePair<long, List<KeyValuePair<int, int>>>> Handy = new List<KeyValuePair<long, List<KeyValuePair<int, int>>>>();

            public string Sig()
            {
                var sb = new StringBuilder();
                foreach (var p in Prods)
                    sb.Append(p.Key).Append(' ').Append(p.Cur).Append(' ').Append(p.Next).Append(' ')
                      .Append(Mathf.RoundToInt(p.Limit)).Append(' ').Append(p.Daily).Append(' ').Append(Mathf.RoundToInt(p.Time * 2f)).Append('|');
                sb.Append('#');
                foreach (var s in Shops)
                {
                    sb.Append(s.Id).Append(':');
                    foreach (var j in s.Json) sb.Append(j.GetHashCode()).Append(',');
                    foreach (var p in s.Prog) sb.Append(Mathf.RoundToInt(p * 20f)).Append(',');
                    if (s.HasWork) sb.Append('w').Append(Mathf.RoundToInt(s.TimeDone * 2f)).Append('/').Append(Mathf.RoundToInt(s.TimeTo * 2f));
                    foreach (var c in s.Cost) sb.Append('c').Append(c.Key).Append('=').Append(c.Value);
                    if (s.HasGarage) sb.Append('g').Append(F(s.GaragePos.x)).Append(',').Append(F(s.GaragePos.z)).Append(',').Append(Mathf.RoundToInt(s.GarageEuler.y));
                    sb.Append('|');
                }
                sb.Append('#');
                foreach (var l in Looks)
                    sb.Append(l.Id).Append(l.DefWall ? "-" : ColorText(l.Wall)).Append('/').Append(l.DefRoof ? "-" : ColorText(l.Roof))
                      .Append('/').Append(l.Enf).Append('/').Append(l.Name ?? "").Append('/')
                      .Append(l.HasReq ? (l.ReqWall ? ColorText(l.ReqWallColor) : "-") + (l.ReqRoof ? ColorText(l.ReqRoofColor) : "-") + (l.ReqEnf ? l.ReqEnfType.ToString() : "-") : "x").Append('|');
                sb.Append('#');
                foreach (var s in Saws) sb.Append(s.Key).Append(s.Value ? '1' : '0').Append('|');
                sb.Append('#');
                foreach (var h in Handy)
                {
                    sb.Append(h.Key).Append(':');
                    foreach (var p in h.Value) sb.Append(p.Key).Append('=').Append(p.Value).Append(',');
                    sb.Append('|');
                }
                return sb.ToString();
            }
        }

        private static string ColorText(Color c) =>
            Mathf.RoundToInt(c.r * 255f) + "," + Mathf.RoundToInt(c.g * 255f) + "," + Mathf.RoundToInt(c.b * 255f);

        private static bool IsLookDefault(Building b, BuildingCustomizationHandler ch, BuildingEnforcementHandler eh)
        {
            return ch.HasDefaultWallColor && ch.HasDefaultRoofColor && ch.CustomizationRequest == null
                   && (eh == null || eh.CurrentEnforcementType == EnforcementType.WoodenPlanks) && !NameOverF(b);
        }

        private static Snapshot Take()
        {
            var s = new Snapshot();
            foreach (var kv in Productions())
            {
                var pw = kv.Value;
                s.Prods.Add(new ProdE
                {
                    Key = kv.Key,
                    Cur = pw.ProductionData?.Id ?? "",
                    Next = pw.NextProductionData?.Id ?? "",
                    Time = pw.CurrentProductionTime,
                    Limit = pw.ProductionLimit,
                    Daily = pw.DailyProducedAmount,
                });
            }

            foreach (var b in ShopBuildings())
            {
                var h = ShopHandlerF(b);
                var e = new ShopE { Id = b.ID };
                foreach (var q in h.RequestsQueue)
                {
                    e.Json.Add(ReqJson(q));
                    e.Prog.Add(q.Progress);
                }
                var w = b.CarWorkshopWork;
                if (w != null && w.CurrentRequest != null && w.Cost != null)
                {
                    e.HasWork = true;
                    e.TimeTo = w.TimeToConstruct;
                    e.TimeDone = w.TimeConstructing;
                    e.CurRes = w.CurrentResourceTime;
                    e.ConsumeRes = w.ConsumeResourceTime;
                    foreach (var c in w.Cost.OrderBy(c => (int)c.Key)) e.Cost.Add(new KeyValuePair<int, int>((int)c.Key, c.Value));
                    if (w.UsedResources != null)
                        foreach (var c in w.UsedResources.OrderBy(c => (int)c.Key)) e.Used.Add(new KeyValuePair<int, int>((int)c.Key, c.Value));
                }
                var g = GarageF(b);
                if (g != null && g.HasGarage)
                {
                    e.HasGarage = true;
                    e.GaragePos = g.CurrentGarage.transform.position;
                    e.GarageEuler = g.CurrentGarage.transform.eulerAngles;
                }
                s.Shops.Add(e);
            }

            var bc = Bc;
            if (bc?.Buildings != null)
            {
                foreach (var b in bc.Buildings)
                {
                    if (b == null)
                        continue;
                    var ch = CustF(b);
                    var eh = EnfF(b);
                    if (ch != null && !IsLookDefault(b, ch, eh))
                    {
                        var l = new LookE
                        {
                            Id = b.ID,
                            DefWall = ch.HasDefaultWallColor,
                            Wall = ch.WallsColor,
                            DefRoof = ch.HasDefaultRoofColor,
                            Roof = ch.RoofColor,
                            Enf = eh != null ? (int)eh.CurrentEnforcementType : (int)EnforcementType.WoodenPlanks,
                            Name = NameOverF(b) ? NameF(b) ?? "" : null,
                        };
                        var q = ch.CustomizationRequest;
                        if (q != null)
                        {
                            l.HasReq = true;
                            l.ReqWall = q.ShouldChangeWallsColor;
                            l.ReqWallColor = q.WallsColorToSet;
                            l.ReqRoof = q.ShouldChangeRoofColor;
                            l.ReqRoofColor = q.RoofColorToSet;
                            l.ReqEnf = q.ShouldApplyEnforcement;
                            l.ReqEnfType = (int)q.EnforcementTypeToSet;
                        }
                        s.Looks.Add(l);
                    }
                    if (b.SawmillWork != null)
                        s.Saws.Add(new KeyValuePair<long, bool>(b.ID, b.SawmillWork.PlayerCondition));
                    if (b.HandymanWork?.SectionPriorities != null)
                        s.Handy.Add(new KeyValuePair<long, List<KeyValuePair<int, int>>>(b.ID,
                            b.HandymanWork.SectionPriorities.OrderBy(p => (int)p.Key).Select(p => new KeyValuePair<int, int>((int)p.Key, p.Value)).ToList()));
                }
            }
            s.Looks.Sort((x, y) => x.Id.CompareTo(y.Id));
            s.Saws.Sort((x, y) => x.Key.CompareTo(y.Key));
            s.Handy.Sort((x, y) => x.Key.CompareTo(y.Key));
            return s;
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

            Snapshot s;
            try { s = Take(); }
            catch (Exception e) { Plugin.Log.LogDebug($"Produktion-Stand: {e.Message}"); return; }
            string sig = s.Sig();
            if (!force && sig == _sentSig)
                return;
            _sentSig = sig;
            _session.BroadcastToGuests(Protocol.Build(Msg.ProductionState, w => Write(w, s)));
        }

        private static void WriteColor(BinaryWriter w, Color c)
        {
            w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a);
        }

        private static Color ReadColor(BinaryReader r) => new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        private static void WritePairs(BinaryWriter w, List<KeyValuePair<int, int>> l)
        {
            w.Write(l.Count);
            foreach (var p in l) { w.Write(p.Key); w.Write(p.Value); }
        }

        private static List<KeyValuePair<int, int>> ReadPairs(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 1000) throw new InvalidDataException("zu viele Paare");
            var l = new List<KeyValuePair<int, int>>(n);
            for (int i = 0; i < n; i++) l.Add(new KeyValuePair<int, int>(r.ReadInt32(), r.ReadInt32()));
            return l;
        }

        private static void Write(BinaryWriter w, Snapshot s)
        {
            w.Write(s.Prods.Count);
            foreach (var p in s.Prods)
            {
                w.Write(p.Key); w.Write(p.Cur); w.Write(p.Next); w.Write(p.Time); w.Write(p.Limit); w.Write(p.Daily);
            }
            w.Write(s.Shops.Count);
            foreach (var e in s.Shops)
            {
                w.Write(e.Id);
                w.Write(e.Json.Count);
                for (int i = 0; i < e.Json.Count; i++) { w.Write(e.Json[i]); w.Write(e.Prog[i]); }
                w.Write(e.HasWork);
                if (e.HasWork)
                {
                    w.Write(e.TimeTo); w.Write(e.TimeDone); w.Write(e.CurRes); w.Write(e.ConsumeRes);
                    WritePairs(w, e.Cost);
                    WritePairs(w, e.Used);
                }
                w.Write(e.HasGarage);
                if (e.HasGarage)
                {
                    w.Write(e.GaragePos.x); w.Write(e.GaragePos.y); w.Write(e.GaragePos.z);
                    w.Write(e.GarageEuler.x); w.Write(e.GarageEuler.y); w.Write(e.GarageEuler.z);
                }
            }
            w.Write(s.Looks.Count);
            foreach (var l in s.Looks)
            {
                w.Write(l.Id);
                w.Write(l.DefWall); WriteColor(w, l.Wall);
                w.Write(l.DefRoof); WriteColor(w, l.Roof);
                w.Write(l.Enf);
                w.Write(l.Name != null);
                if (l.Name != null) w.Write(l.Name);
                w.Write(l.HasReq);
                if (l.HasReq)
                {
                    w.Write(l.ReqWall); WriteColor(w, l.ReqWallColor);
                    w.Write(l.ReqRoof); WriteColor(w, l.ReqRoofColor);
                    w.Write(l.ReqEnf); w.Write(l.ReqEnfType);
                }
            }
            w.Write(s.Saws.Count);
            foreach (var p in s.Saws) { w.Write(p.Key); w.Write(p.Value); }
            w.Write(s.Handy.Count);
            foreach (var h in s.Handy) { w.Write(h.Key); WritePairs(w, h.Value); }
        }

        private static Snapshot Read(BinaryReader r)
        {
            var s = new Snapshot();
            int n = r.ReadInt32();
            if (n < 0 || n > 20000) throw new InvalidDataException("zu viele Produktionen");
            for (int i = 0; i < n; i++)
                s.Prods.Add(new ProdE { Key = r.ReadString(), Cur = r.ReadString(), Next = r.ReadString(), Time = r.ReadSingle(), Limit = r.ReadSingle(), Daily = r.ReadInt32() });
            n = r.ReadInt32();
            if (n < 0 || n > 5000) throw new InvalidDataException("zu viele Werkstätten");
            for (int i = 0; i < n; i++)
            {
                var e = new ShopE { Id = r.ReadInt64() };
                int m = r.ReadInt32();
                if (m < 0 || m > 500) throw new InvalidDataException("zu viele Aufträge");
                for (int j = 0; j < m; j++) { e.Json.Add(r.ReadString()); e.Prog.Add(r.ReadSingle()); }
                e.HasWork = r.ReadBoolean();
                if (e.HasWork)
                {
                    e.TimeTo = r.ReadSingle(); e.TimeDone = r.ReadSingle(); e.CurRes = r.ReadSingle(); e.ConsumeRes = r.ReadSingle();
                    e.Cost = ReadPairs(r);
                    e.Used = ReadPairs(r);
                }
                e.HasGarage = r.ReadBoolean();
                if (e.HasGarage)
                {
                    e.GaragePos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    e.GarageEuler = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                }
                s.Shops.Add(e);
            }
            n = r.ReadInt32();
            if (n < 0 || n > 50000) throw new InvalidDataException("zu viele Häuser");
            for (int i = 0; i < n; i++)
            {
                var l = new LookE { Id = r.ReadInt64() };
                l.DefWall = r.ReadBoolean(); l.Wall = ReadColor(r);
                l.DefRoof = r.ReadBoolean(); l.Roof = ReadColor(r);
                l.Enf = r.ReadInt32();
                if (r.ReadBoolean()) l.Name = r.ReadString();
                l.HasReq = r.ReadBoolean();
                if (l.HasReq)
                {
                    l.ReqWall = r.ReadBoolean(); l.ReqWallColor = ReadColor(r);
                    l.ReqRoof = r.ReadBoolean(); l.ReqRoofColor = ReadColor(r);
                    l.ReqEnf = r.ReadBoolean(); l.ReqEnfType = r.ReadInt32();
                }
                s.Looks.Add(l);
            }
            n = r.ReadInt32();
            if (n < 0 || n > 50000) throw new InvalidDataException("zu viele Sägewerke");
            for (int i = 0; i < n; i++) s.Saws.Add(new KeyValuePair<long, bool>(r.ReadInt64(), r.ReadBoolean()));
            n = r.ReadInt32();
            if (n < 0 || n > 50000) throw new InvalidDataException("zu viele Reparaturtrupps");
            for (int i = 0; i < n; i++) s.Handy.Add(new KeyValuePair<long, List<KeyValuePair<int, int>>>(r.ReadInt64(), ReadPairs(r)));
            return s;
        }

        public void HostOnRequest(Peer from, BinaryReader r)
        {
            var op = (Op)r.ReadByte();
            string info = op.ToString();
            try
            {
                switch (op)
                {
                    case Op.Production:
                    {
                        string key = r.ReadString();
                        string id = r.ReadString();
                        bool all = r.ReadBoolean();
                        var pw = FindProduction(key);
                        var pd = pw?.Data?.GetProductionData(id);
                        if (pd == null) { Plugin.Log.LogWarning($"IFZCOOP production-Wunsch: {key}/{id} gibt es beim Host nicht"); return; }
                        ApplyClick(pw, pd, all);
                        info += $" {key} {id} all={all} -> {pw.ProductionData?.Id}/{pw.NextProductionData?.Id}";
                        break;
                    }
                    case Op.ShopBuild:
                    case Op.ShopModify:
                    case Op.ShopScrap:
                    {
                        long bid = r.ReadInt64();
                        string vid = r.ReadString();
                        string json = r.ReadString();
                        var b = Bc?.GetBuildingById(bid);
                        var c = Shops;
                        if (b == null || c == null || b.CarWorkshopWork == null) { Plugin.Log.LogWarning($"IFZCOOP shop-Wunsch: keine Werkstatt {bid}"); return; }
                        Vehicle v = null;
                        if (op != Op.ShopBuild)
                        {
                            v = GameAccess.Game<VehiclesController>()?.GetVehicleById(vid);
                            if (v == null || v.UnderCarWorkshop) { Plugin.Log.LogWarning($"IFZCOOP shop-Wunsch: Fahrzeug {vid} fehlt oder ist schon drin"); return; }
                        }
                        if (!GarageF(b).HasGarage)
                        {
                            // Ohne Garage scheitern Fahrzeug-Aufträge (GarageQueue) und das Ausliefern eines neuen Autos
                            if (op != Op.ShopBuild) { Plugin.Log.LogWarning($"IFZCOOP shop-Wunsch {op}: Werkstatt {bid} hat keine Garage"); return; }
                            Plugin.Log.LogWarning($"IFZCOOP shop-Wunsch: Werkstatt {bid} hat keine Garage – Auto kann nicht ausgeliefert werden");
                        }
                        if (op == Op.ShopScrap)
                        {
                            c.CreateDeconstructionRequestFor(b, v);
                        }
                        else
                        {
                            var sr = JsonConvert.DeserializeObject<SerializedCarWorkshopRequest>(json, GameJson.DefaultSettings);
                            sr.Progress = 0f;
                            sr.IsSelected = false;
                            var q = new CarWorkshopRequest();
                            q.Deserialize(sr);
                            if (op == Op.ShopBuild)
                                c.CreateConstructionRequestFor(b, q);
                            else
                            {
                                // Was vom Fahrzeug abhängt, nimmt der Host von seinem Fahrzeug
                                q.ModificationsBefore = v.ModificationHandler.GetAllCurrentModifications();
                                q.PreviousColor = v.VehicleColor;
                                q.PreviousHealth = v.Health.GetHealthAmount01();
                                q.ResourcesInVehicle = v.Trunk.Resources.Select(p => new ResourceQuantity(p.Key, p.Value)).ToArray();
                                q.AmmoQuantity = v.AmmoContainer.AmmoCrate.Quantity;
                                q.HasCamoBefore = v.VehiclePainter.HasCamo;
                                q.HasSkinBefore = v.VehiclePainter.HasSkin;
                                q.HasFireBefore = v.VehiclePainter.HasFire;
                                q.HasEmblemBefore = v.VehiclePainter.ShouldShowEmblem;
                                c.CreateModificationRequestFor(b, v, q);
                            }
                        }
                        info += $" {bid} {vid} -> {ShopHandlerF(b).RequestsQueue.Count} Aufträge";
                        break;
                    }
                    case Op.ShopCancel:
                    case Op.ShopSelect:
                    {
                        long bid = r.ReadInt64();
                        int index = r.ReadInt32();
                        int type = r.ReadInt32();
                        var b = Bc?.GetBuildingById(bid);
                        var h = b != null ? ShopHandlerF(b) : null;
                        if (h == null || index < 0 || index >= h.RequestsQueue.Count || (int)h.RequestsQueue[index].Type != type)
                        {
                            Plugin.Log.LogWarning($"IFZCOOP shop-Wunsch {op}: Auftrag {bid}/{index} passt nicht");
                            return;
                        }
                        if (op == Op.ShopCancel) h.CancelRequest(h.RequestsQueue[index]);
                        else h.SelectRequest(h.RequestsQueue[index]);
                        info += $" {bid} #{index}";
                        break;
                    }
                    case Op.Customize:
                    {
                        long bid = r.ReadInt64();
                        var q = ReadCustomization(r, out string name);
                        var b = Bc?.GetBuildingById(bid);
                        if (b == null) return;
                        ApplyCustomize(b, q, name);
                        info += $" {bid} -> {StateOf(b)}";
                        break;
                    }
                    case Op.Sawmill:
                    {
                        long bid = r.ReadInt64();
                        bool on = r.ReadBoolean();
                        var sw = Bc?.GetBuildingById(bid)?.SawmillWork;
                        if (sw == null) return;
                        sw.SetPlayerCondition(on);
                        info += $" {bid} {on}";
                        break;
                    }
                    case Op.Handyman:
                    {
                        long bid = r.ReadInt64();
                        int sec = r.ReadInt32();
                        int prio = r.ReadInt32();
                        var hw = Bc?.GetBuildingById(bid)?.HandymanWork;
                        if (hw == null) return;
                        hw.ChangeSectionPriority((BuildingSection)sec, prio);
                        info += $" {bid} {(BuildingSection)sec}={prio}";
                        break;
                    }
                    default:
                        return;
                }
                RequestsDone++;
                _nextSend = 0f; // gleich zurückmelden
                Plugin.Log.LogInfo($"IFZCOOP production {info} (von {from.Name})");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"IFZCOOP production-Wunsch {op}: {e.Message}"); }
        }

        /// <summary>Wie ProductionPanel.OnProductionBarClicked (ohne Forschungs-Anzeige).</summary>
        public static void ApplyClick(ProductionWork pw, global::ProductionData pd, bool all)
        {
            if (pw.IsNextProduction(pd))
            {
                pw.CancelNextProduction();
                if (all && pw.Structure is Building b)
                    Bc?.CancelNextBuildingsProduction(b, pd);
                else if (all && pw.Structure is Gameplay.Rebuilding.Farmlands.Farmland f)
                    GameAccess.Game<Gameplay.Rebuilding.Farmlands.FarmlandsController>()?.CancelNextFarmlandsProduction(f, pd);
            }
            else
            {
                pw.ChangeProduction(pd);
                if (all && pw.Structure is Building b)
                    Bc?.SetBuildingsProduction(b, pd);
                else if (all && pw.Structure is Gameplay.Rebuilding.Farmlands.Farmland f)
                    GameAccess.Game<Gameplay.Rebuilding.Farmlands.FarmlandsController>()?.SetAllFarmlandsProduction(f, pd);
            }
        }

        /// <summary>Wie CustomizeBuildingController.OnConstructionConfirmed.</summary>
        public static void ApplyCustomize(Building b, CustomizationRequest q, string name)
        {
            q.FilterRequestFor(b);
            if (!string.IsNullOrEmpty(name))
                b.OverrideStructureName(name);
            if (q.HasAnythingToChange() && StateOf(b) == StructureStateType.Completed)
            {
                CustF(b).SetRequest(q);
                b.StateMachine.ChangeTo(StructureStateType.Customization);
            }
        }

        private static void WriteCustomization(BinaryWriter w, CustomizationRequest q, string name)
        {
            w.Write(q.ShouldChangeWallsColor); WriteColor(w, q.WallsColorToSet);
            w.Write(q.ShouldChangeRoofColor); WriteColor(w, q.RoofColorToSet);
            w.Write(q.ShouldApplyEnforcement); w.Write((int)q.EnforcementTypeToSet);
            w.Write(name ?? "");
        }

        private static CustomizationRequest ReadCustomization(BinaryReader r, out string name)
        {
            bool wall = r.ReadBoolean(); var wc = ReadColor(r);
            bool roof = r.ReadBoolean(); var rc = ReadColor(r);
            bool enf = r.ReadBoolean(); int et = r.ReadInt32();
            name = r.ReadString();
            return MakeCustomization(wall, wc, roof, rc, enf, et, name);
        }

        /// <summary>
        /// Entwickler-Menü: Aussehen sofort ändern (Verstärkung, Wand-/Dachfarbe) wie „custom done“: Anpassung starten,
        /// Auftrag anwenden, Arbeit beenden. Repariert vorher, weil das Spiel sonst erst eine Reparatur verlangt.
        /// </summary>
        public static string InstantCustomize(Building b, bool wall, Color wc, bool roof, Color rc, bool enf, int et)
        {
            if (b == null) return "Gebäude nicht gefunden";
            if (CustF(b) == null) return "dieses Gebäude kann man nicht anpassen";
            if (StateOf(b) != StructureStateType.Completed) return "geht nur bei fertigen Gebäuden (" + StateOf(b) + ")";
            b.Health?.SetHealthAmount01(1f);
            var q = MakeCustomization(wall, wc, roof, rc, enf, et, null);
            ApplyCustomize(b, q, null);
            if (StateOf(b) != StructureStateType.Customization) return "nichts zu ändern";
            CustF(b).ApplyRequest();
            var cw = b.CurrentWork;
            if (cw is CustomizationWork || cw is RepairStructureWork)
                cw.DeleteWork();
            if (StateOf(b) == StructureStateType.Customization)
                b.StateMachine.ChangeTo(StructureStateType.Completed);
            return "Aussehen geändert: " + LookLine(b);
        }

        /// <summary>
        /// Entwickler-Menü „Sofort anpassen“: laufende Anpassung (auch die vorgeschaltete Reparatur) sofort fertig.
        /// Wie „custom done“: volles Leben, Auftrag anwenden, Arbeit beenden → das Spiel wechselt auf „fertig“.
        /// </summary>
        public static bool FinishCustomizationNow(Building b)
        {
            if (b == null || StateOf(b) != StructureStateType.Customization || CustF(b) == null) return false;
            b.Health?.SetHealthAmount01(1f);
            CustF(b).ApplyRequest();
            var cw = b.CurrentWork;
            if (cw is CustomizationWork || cw is RepairStructureWork)
                cw.DeleteWork();
            if (StateOf(b) == StructureStateType.Customization)
                b.StateMachine.ChangeTo(StructureStateType.Completed);
            return true;
        }

        private static CustomizationRequest MakeCustomization(bool wall, Color wc, bool roof, Color rc, bool enf, int et, string name)
        {
            var q = new CustomizationRequest();
            if (wall) q.SetWallColor(wc);
            if (roof) q.SetRoofColor(rc);
            if (enf) q.SetEnforcementType((EnforcementType)et);
            if (!string.IsNullOrEmpty(name)) q.SetStructureName(name);
            return q;
        }

        // ================================================================ Gast: Klicks

        private bool GuestCanAsk => _session.Role == Role.Guest && _session.GuestControlsLocked && !UnitSync.NetApplying && GameAccess.IsGameStarted;
        private bool IsGuest => _session.Role == Role.Guest;

        private void Send(Op op, Action<BinaryWriter> body)
        {
            _session.SendToHost(Protocol.Build(Msg.ProductionRequest, w =>
            {
                w.Write((byte)op);
                body(w);
            }));
            RequestsSent++;
            Plugin.Log.LogInfo($"IFZCOOP production-Wunsch {op} an den Host");
        }

        /// <summary>Rezept wählen (Fenster oder Testbefehl). false = beim Gast nicht selbst ausführen.</summary>
        public bool Click(ProductionWork pw, global::ProductionData pd, bool all)
        {
            if (!GuestCanAsk || pw == null || pd == null)
                return true;
            string key = ProdKeyOf(pw);
            if (key == null)
                return false;
            Send(Op.Production, w => { w.Write(key); w.Write(pd.Id ?? ""); w.Write(all); });
            return false;
        }

        /// <summary>Prefix ProductionPanel.OnProductionBarClicked.</summary>
        public bool GuestProductionBar(object panel, UI.InfoPanels.NestedUIElements.ProductionBar bar, bool all)
        {
            if (!GuestCanAsk || bar == null || bar.ProductionData == null)
                return true;
            try
            {
                // Gesperrtes Rezept: das Spiel zeigt nur die Forschung – das darf der Gast selbst
                if (!SaveHandler.SaveData.ContentStateController.IsContentUnlocked(bar.ProductionData.Id))
                    return true;
            }
            catch { }
            ProductionWork pw = null;
            try { pw = Traverse.Create(panel).Field("_productionWork").GetValue<ProductionWork>(); } catch { }
            return Click(pw, bar.ProductionData, all);
        }

        /// <summary>Prefix CarWorkshopController.CreateConstructionRequestFor.</summary>
        public bool GuestShopBuild(Building b, CarWorkshopRequest q)
        {
            if (!GuestCanAsk || b == null || q == null)
                return true;
            string json = ReqJson(q);
            Send(Op.ShopBuild, w => { w.Write(b.ID); w.Write(""); w.Write(json); });
            return false;
        }

        /// <summary>Prefix CarWorkshopController.CreateModificationRequestFor / CreateDeconstructionRequestFor.</summary>
        public bool GuestShopVehicle(Building b, Vehicle v, CarWorkshopRequest q, bool scrap)
        {
            if (!GuestCanAsk || b == null || v == null)
                return true;
            string json = q != null ? ReqJson(q) : "";
            Send(scrap ? Op.ShopScrap : Op.ShopModify, w => { w.Write(b.ID); w.Write(v.Id ?? ""); w.Write(json); });
            return false;
        }

        /// <summary>Prefix CarWorkshopHandler.AddRequest (Rückfall, falls CreateConstructionRequestFor eingebettet wurde).</summary>
        public bool GuestAddRequest(CarWorkshopHandler h, CarWorkshopRequest q, bool manually)
        {
            if (!manually || !GuestCanAsk || h == null || q == null)
                return true;
            var b = HandlerBuildingF(h);
            if (q.Type == CarWorkshopRequestType.Construction && b != null)
                GuestShopBuild(b, q);
            else
                Plugin.Log.LogWarning("IFZCOOP Werkstatt-Auftrag beim Gast ohne Fahrzeug verworfen: " + q.Type);
            return false;
        }

        /// <summary>Prefix CarWorkshopHandler.SelectRequest.</summary>
        public bool GuestSelectRequest(CarWorkshopHandler h, CarWorkshopRequest q)
        {
            if (!GuestCanAsk || h == null || q == null)
                return true;
            var b = HandlerBuildingF(h);
            int i = h.RequestsQueue.IndexOf(q);
            if (b != null && i >= 0 && h.SelectedRequest != q)
                Send(Op.ShopSelect, w => { w.Write(b.ID); w.Write(i); w.Write((int)q.Type); });
            return false;
        }

        /// <summary>Prefix CarWorkshopController.HandleRequestCanceled: beim Gast nie selbst (erzeugt Fahrzeuge).</summary>
        public bool GuestCancelRequest(Building b, CarWorkshopRequest q)
        {
            if (!IsGuest)
                return true;
            if (GuestCanAsk && b != null && q != null)
            {
                int i = ShopHandlerF(b)?.RequestsQueue.IndexOf(q) ?? -1;
                if (i >= 0)
                    Send(Op.ShopCancel, w => { w.Write(b.ID); w.Write(i); w.Write((int)q.Type); });
            }
            return false;
        }

        /// <summary>Prefix SawmillWork.SetPlayerCondition.</summary>
        public bool GuestSawmill(SawmillWork sw, bool on)
        {
            if (!GuestCanAsk || sw == null)
                return true;
            if (sw.Structure is Building b)
                Send(Op.Sawmill, w => { w.Write(b.ID); w.Write(on); });
            return false;
        }

        /// <summary>Prefix HandymanWork.ChangeSectionPriority.</summary>
        public bool GuestHandyman(HandymanWork hw, BuildingSection sec, int prio)
        {
            if (!GuestCanAsk || hw == null)
                return true;
            if (hw.Structure is Building b)
                Send(Op.Handyman, w => { w.Write(b.ID); w.Write((int)sec); w.Write(prio); });
            return false;
        }

        /// <summary>Anpassen (Testbefehl oder Fenster): beim Gast als Wunsch, beim Host direkt.</summary>
        public void Customize(Building b, CustomizationRequest q, string name)
        {
            if (b == null || q == null)
                return;
            if (GuestCanAsk)
            {
                Send(Op.Customize, w => { w.Write(b.ID); WriteCustomization(w, q, name); });
                return;
            }
            if (!IsGuest)
                ApplyCustomize(b, q, name);
        }

        /// <summary>Prefix CustomizeBuildingController.OnConstructionConfirmed.</summary>
        public bool GuestCustomizeConfirm(CustomizeBuildingController ctrl)
        {
            if (!GuestCanAsk || ctrl == null)
                return true;
            try
            {
                var t = Traverse.Create(ctrl);
                var b = t.Field("_building").GetValue<Building>();
                var panel = t.Field("_panel").GetValue<BuildingCustomizationInfoPanel>();
                if (b == null || panel == null)
                    return false;
                var q = panel.GetCustomizationRequest();
                q.FilterRequestFor(b);
                panel.ClosePanel();
                Customize(b, q, q.NameToSet);
                t.Field("_building").SetValue(null);
            }
            catch (Exception e) { Plugin.Log.LogWarning("IFZCOOP Anpassen beim Gast: " + e.Message); }
            return false;
        }

        /// <summary>Prefix BuildingCustomizationState.Enter beim Gast: Auftrag vom Host einsetzen, sonst leerer (kein Fehler).</summary>
        public void GuestCustomizationEnter(object state)
        {
            if (!IsGuest || state == null)
                return;
            try
            {
                var b = Traverse.Create(state).Field("_building").GetValue<Building>();
                var ch = b != null ? CustF(b) : null;
                if (ch == null || ch.CustomizationRequest != null)
                    return;
                var l = _last?.Looks.FirstOrDefault(x => x.Id == b.ID && x.HasReq);
                ch.SetRequest(l != null ? MakeCustomization(l.ReqWall, l.ReqWallColor, l.ReqRoof, l.ReqRoofColor, l.ReqEnf, l.ReqEnfType, null) : new CustomizationRequest());
            }
            catch (Exception e) { Plugin.Log.LogDebug("Anpassen beginnen (Gast): " + e.Message); }
        }

        // ================================================================ Gast: Stand

        public void GuestOnState(BinaryReader r)
        {
            var s = Read(r);
            _last = s;
            UnitSync.NetApplying = true;
            try
            {
                Apply(s, fromHost: true);
                Applied++;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Produktion-Stand: {e.Message}"); }
            finally { UnitSync.NetApplying = false; }
        }

        public void GuestFrame()
        {
            if (_last == null || _session.Role != Role.Guest || !_session.GuestControlsLocked)
                return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextReapply)
                return;
            _nextReapply = now + 1f;
            UnitSync.NetApplying = true;
            try { Apply(_last, fromHost: false); }
            catch (Exception e) { Plugin.Log.LogDebug($"Produktion erneut: {e.Message}"); }
            finally { UnitSync.NetApplying = false; }
        }

        private void Apply(Snapshot s, bool fromHost)
        {
            // Produktionen
            var mine = Productions().ToDictionary(k => k.Key, k => k.Value);
            int missing = 0;
            var refresh = new List<ProductionWork>();
            foreach (var e in s.Prods)
            {
                if (!mine.TryGetValue(e.Key, out var pw)) { missing++; continue; }
                try
                {
                    bool changed = false;
                    if (!string.IsNullOrEmpty(e.Cur) && pw.ProductionData?.Id != e.Cur)
                    {
                        var pd = pw.Data?.GetProductionData(e.Cur);
                        if (pd != null) { pw.ProductionData = pd; changed = true; }
                    }
                    string next = pw.NextProductionData?.Id ?? "";
                    if (next != e.Next)
                    {
                        if (string.IsNullOrEmpty(e.Next)) pw.CancelNextProduction();
                        else pw.SetProductionData(e.Next, isNext: true);
                        changed = true;
                        refresh.Add(pw);
                    }
                    if (pw.Data != null && pw.Data.isProductionLimited && Mathf.Abs(pw.ProductionLimit - e.Limit) > 0.01f)
                    {
                        pw.SetProductionLimit(e.Limit);
                        changed = true;
                    }
                    if (fromHost)
                    {
                        if (Mathf.Abs(pw.CurrentProductionTime - e.Time) > 0.001f)
                        {
                            pw.SetCurrentProductionTime(e.Time);
                            pw.UpdateProgress();
                        }
                        DailyF(pw) = e.Daily;
                    }
                    if (changed) ProdChanged++;
                }
                catch (Exception ex) { Plugin.Log.LogDebug($"Produktion {e.Key}: {ex.Message}"); }
            }
            MissingProductions = missing;
            foreach (var pw in refresh) RefreshPanels(pw);

            var bc = Bc;
            if (bc == null)
                return;

            // Werkstätten
            var hostShops = new HashSet<long>();
            foreach (var e in s.Shops)
            {
                hostShops.Add(e.Id);
                var b = bc.GetBuildingById(e.Id);
                if (b == null) continue;
                try { ApplyShop(b, e, fromHost); }
                catch (Exception ex) { Plugin.Log.LogDebug($"Werkstatt {e.Id}: {ex.Message}"); }
            }
            foreach (var b in ShopBuildings())
            {
                if (hostShops.Contains(b.ID)) continue;
                var h = ShopHandlerF(b);
                if (h != null && h.HasRequests)
                {
                    try { ApplyShop(b, new ShopE { Id = b.ID }, fromHost); }
                    catch (Exception ex) { Plugin.Log.LogDebug($"Werkstatt {b.ID} leeren: {ex.Message}"); }
                }
            }

            // Aussehen, Sägewerk, Reparaturtrupp
            var looks = s.Looks.ToDictionary(l => l.Id);
            var saws = s.Saws.ToDictionary(p => p.Key, p => p.Value);
            var handy = s.Handy.ToDictionary(p => p.Key, p => p.Value);
            foreach (var b in bc.Buildings)
            {
                if (b == null) continue;
                try
                {
                    looks.TryGetValue(b.ID, out var l);
                    ApplyLook(b, l);
                    if (b.SawmillWork != null && saws.TryGetValue(b.ID, out bool on) && b.SawmillWork.PlayerCondition != on)
                    {
                        b.SawmillWork.SetPlayerCondition(on);
                        ProdChanged++;
                    }
                    if (b.HandymanWork?.SectionPriorities != null && handy.TryGetValue(b.ID, out var prios))
                        foreach (var p in prios)
                        {
                            var sec = (BuildingSection)p.Key;
                            if (!b.HandymanWork.SectionPriorities.TryGetValue(sec, out int cur) || cur != p.Value)
                            {
                                b.HandymanWork.ChangeSectionPriority(sec, p.Value);
                                ProdChanged++;
                            }
                        }
                }
                catch (Exception ex) { Plugin.Log.LogDebug($"Haus {b.ID}: {ex.Message}"); }
            }
        }

        private static void RefreshPanels(ProductionWork pw)
        {
            try
            {
                var m = AccessTools.Method(typeof(UI.InfoPanels.NestedUIElements.ProductionPanel), "RefreshWaitingState");
                foreach (var p in Resources.FindObjectsOfTypeAll<UI.InfoPanels.NestedUIElements.ProductionPanel>())
                {
                    if (p == null || !p.gameObject.activeInHierarchy) continue;
                    if (ReferenceEquals(Traverse.Create(p).Field("_productionWork").GetValue(), pw))
                        m?.Invoke(p, null);
                }
            }
            catch (Exception e) { Plugin.Log.LogDebug("Produktion-Fenster: " + e.Message); }
        }

        private void ApplyShop(Building b, ShopE e, bool fromHost)
        {
            var h = ShopHandlerF(b);
            if (h == null)
                return;
            // Garage an der Stelle des Hosts (die eigene Würfel-Garage des Gastes wird ersetzt)
            var g = GarageF(b);
            if (g != null && e.HasGarage)
            {
                var cur = g.HasGarage ? g.CurrentGarage.transform : null;
                if (cur == null || (cur.position - e.GaragePos).sqrMagnitude > 0.25f || Mathf.Abs(Mathf.DeltaAngle(cur.eulerAngles.y, e.GarageEuler.y)) > 2f)
                {
                    if (cur != null) g.DespawnGarage();
                    g.SpawnGarage(e.GaragePos, Quaternion.Euler(e.GarageEuler));
                    GaragesPlaced++;
                    Plugin.Log.LogInfo($"IFZCOOP Garage {b.ID} vom Host");
                }
            }
            var mineJson = h.RequestsQueue.Select(ReqJson).ToList();
            if (!mineJson.SequenceEqual(e.Json))
            {
                // Wie CarWorkshopHandler.Deserialize
                h.Clear();
                h.SelectedRequest = null;
                foreach (var json in e.Json)
                {
                    var sr = JsonConvert.DeserializeObject<SerializedCarWorkshopRequest>(json, GameJson.DefaultSettings);
                    var q = new CarWorkshopRequest();
                    q.Deserialize(sr);
                    h.AddRequest(q, isAddManually: false);
                    if (q.IsSelected)
                        h.SelectRequest(q);
                }
                if (e.Json.Count == 0)
                    RaiseQueueUpdated(h);
                if (b.CarWorkshopWork != null && b.CarWorkshopWork.CurrentRequest != h.SelectedRequest)
                    b.CarWorkshopWork.SetRequest();
                ShopRebuilt++;
                Plugin.Log.LogInfo($"IFZCOOP Werkstatt {b.ID}: {e.Json.Count} Aufträge vom Host");
            }
            if (!fromHost)
                return;
            for (int i = 0; i < e.Prog.Count && i < h.RequestsQueue.Count; i++)
                if (Mathf.Abs(h.RequestsQueue[i].Progress - e.Prog[i]) > 0.001f)
                    h.RequestsQueue[i].SetProgress(e.Prog[i]);
            var w = b.CarWorkshopWork;
            if (e.HasWork && w != null && w.CurrentRequest != null)
            {
                w.SetTimeToConstruct(e.TimeTo);
                w.SetTimeConstructing(e.TimeDone);
                w.SetCurrentResourceTime(e.CurRes);
                w.SetConsumeResourceTime(e.ConsumeRes);
                // in place: der ResourcesDispatcher der Arbeit hält dasselbe Wörterbuch
                if (w.Cost != null)
                {
                    w.Cost.Clear();
                    foreach (var c in e.Cost) w.Cost[(ResourceID)c.Key] = c.Value;
                }
                if (w.UsedResources != null)
                {
                    w.UsedResources.Clear();
                    foreach (var c in e.Used) w.UsedResources[(ResourceID)c.Key] = c.Value;
                }
            }
        }

        private void ApplyLook(Building b, LookE l)
        {
            var ch = CustF(b);
            if (ch == null)
                return;
            var eh = EnfF(b);
            bool changed = false;
            bool defWall = l?.DefWall ?? true, defRoof = l?.DefRoof ?? true;
            if (defWall) { if (!ch.HasDefaultWallColor) { ch.ClearWallColor(); changed = true; } }
            else if (ch.HasDefaultWallColor || ch.WallsColor != l.Wall) { ch.SetWallColor(l.Wall); changed = true; }
            if (defRoof) { if (!ch.HasDefaultRoofColor) { ch.ClearRoofColor(); changed = true; } }
            else if (ch.HasDefaultRoofColor || ch.RoofColor != l.Roof) { ch.SetRoofColor(l.Roof); changed = true; }
            int enf = l?.Enf ?? (int)EnforcementType.WoodenPlanks;
            if (eh != null && (int)eh.CurrentEnforcementType != enf) { eh.SetEnforcement((EnforcementType)enf); changed = true; }
            if (l?.Name != null && (!NameOverF(b) || NameF(b) != l.Name)) { b.OverrideStructureName(l.Name); changed = true; }

            if (l != null && l.HasReq)
            {
                var q = ch.CustomizationRequest;
                if (q == null || q.ShouldChangeWallsColor != l.ReqWall || q.ShouldChangeRoofColor != l.ReqRoof || q.ShouldApplyEnforcement != l.ReqEnf
                    || (l.ReqEnf && (int)q.EnforcementTypeToSet != l.ReqEnfType))
                {
                    ch.SetRequest(MakeCustomization(l.ReqWall, l.ReqWallColor, l.ReqRoof, l.ReqRoofColor, l.ReqEnf, l.ReqEnfType, null));
                    changed = true;
                }
            }
            else if (StateOf(b) != StructureStateType.Customization && (b.CustomizationWork != null || b.CustomizationRepairWork != null))
            {
                // Host ist fertig und BuildingSync hat das Haus schon umgestellt: liegengebliebene Arbeit des Gastes wegräumen
                CleanupCustomization(b);
                Cleaned++;
            }
            if (changed) LookChanged++;
        }

        private static void CleanupCustomization(Building b)
        {
            var w = b.CustomizationWork;
            if (w != null)
            {
                bool cur = ReferenceEquals(b.CurrentWork, w);
                w.DeleteWork();
                b.OnDeleteCustomizationWork();
                if (cur) b.CurrentWork = null;
            }
            var rw = b.CustomizationRepairWork;
            if (rw != null)
            {
                bool cur = ReferenceEquals(b.CurrentWork, rw);
                rw.DeleteWork();
                b.OnDeleteCustomizationRepairWork();
                if (cur) b.CurrentWork = null;
            }
            CustF(b)?.DiscardRequest();
        }

        // ================================================================ Testbefehle

        private static Vector3 HqPos
        {
            get
            {
                var hq = HqController.MainHeadquarter;
                return hq != null ? hq.transform.position : Vector3.zero;
            }
        }

        private static Building Nearest(Func<Building, bool> ok)
        {
            Building best = null;
            float bestD = float.MaxValue;
            var hq = HqPos;
            var bc = Bc;
            if (bc?.Buildings == null)
                return null;
            foreach (var b in bc.Buildings)
            {
                if (b == null || !ok(b)) continue;
                float d = (b.transform.position - hq).sqrMagnitude;
                if (d < bestD) { bestD = d; best = b; }
            }
            return best;
        }

        private static Building Target(string arg, Func<Building, bool> ok)
        {
            if (arg != null && long.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
                return Bc?.GetBuildingById(id);
            return Nearest(ok);
        }

        private static Color ParseColor(string s)
        {
            var p = s.Split(',');
            return new Color(int.Parse(p[0], CultureInfo.InvariantCulture) / 255f, int.Parse(p[1], CultureInfo.InvariantCulture) / 255f,
                int.Parse(p[2], CultureInfo.InvariantCulture) / 255f, 1f);
        }

        /// <summary>
        /// "prod [near|&lt;Haus-ID&gt;] &lt;Nr|Rezept-ID&gt; [all]" – Rezept wählen wie im Fenster (beim Gast als Wunsch)
        /// "shop garage|add [Auto-Nr]|scrap|cancel &lt;i&gt;|select &lt;i&gt;|done|progress &lt;0..1&gt;" – an der nächsten Werkstatt
        /// "custom &lt;r,g,b|-&gt; &lt;r,g,b|-&gt; &lt;Verstärkung 0..4|-&gt; [Name]" – nächstes schlichtes fertiges Haus anpassen (Wand, Dach);
        /// "custom done" (Host) – laufende Anpassung sofort fertig
        /// "saw 0|1" – Sägewerk „nur nachgewachsene Bäume“; "handy &lt;Gebäudeart&gt; &lt;Wichtigkeit&gt;" – Reparaturtrupp
        /// </summary>
        public string Qa(string[] a)
        {
            switch (a[0].ToLowerInvariant())
            {
                case "prod":
                {
                    string tgt = a.Length > 2 ? a[1] : "near";
                    string sel = a.Length > 2 ? a[2] : a.Length > 1 ? a[1] : "0";
                    bool all = a.Length > 3 && a[3] == "all";
                    var b = Target(tgt, x => x.ProductionWork != null && !x.IsHq);
                    var pw = b?.ProductionWork;
                    if (pw == null) return "keine Produktion";
                    global::ProductionData pd = int.TryParse(sel, out int idx)
                        ? (idx >= 0 && idx < pw.Data.productions.Count ? pw.Data.productions[idx] : null)
                        : pw.Data.GetProductionData(sel);
                    if (pd == null) return "kein Rezept " + sel + " (" + string.Join(",", pw.Data.productions.Select(p => p.Id)) + ")";
                    if (Click(pw, pd, all))
                        ApplyClick(pw, pd, all);
                    return $"Produktion {KeyOf(b)} {pd.Id} -> cur={pw.ProductionData?.Id} next={pw.NextProductionData?.Id} ({string.Join(",", pw.Data.productions.Select(p => p.Id))})";
                }
                case "shop":
                {
                    var b = Nearest(x => x.CarWorkshopWork != null);
                    if (b == null) return "keine Werkstatt";
                    var h = ShopHandlerF(b);
                    var c = Shops;
                    string what = a.Length > 1 ? a[1] : "add";
                    int n = a.Length > 2 && int.TryParse(a[2], out int nn) ? nn : 0;
                    switch (what)
                    {
                        case "garage":
                        {
                            var g = GarageF(b);
                            if (g.HasGarage) return "Garage " + b.ID + " schon da";
                            if (IsGuest) return "Garage " + b.ID + " fehlt (kommt vom Host)";
                            if (b.TryGenerateRandomGarage() && g.HasGarage) return "Garage " + b.ID + " gewürfelt";
                            // Kein freier Platz laut Spiel: für den Test am Eingang erzwingen, nach außen gerichtet
                            var center = b.transform.position;
                            var ent = b.GetEntrances();
                            var at = ent != null && ent.Length > 0 ? ent[0] : center + b.transform.forward * 8f;
                            var dir = at - center;
                            dir.y = 0f;
                            if (dir.sqrMagnitude < 0.01f) dir = b.transform.forward;
                            dir.Normalize();
                            g.SpawnGarage(at + dir * 2f, Quaternion.LookRotation(dir));
                            return "Garage " + b.ID + (g.HasGarage ? " erzwungen am Eingang" : " NICHT angelegt");
                        }
                        case "add":
                        {
                            var cfg = JutsuGamesConfig.Runtime.Configs.GetConfig<Gameplay.CarWorkshopConfig>();
                            var cars = cfg.AvailableCars;
                            if (n < 0 || n >= cars.Length) return "Auto-Nr 0.." + (cars.Length - 1);
                            var data = cars[n];
                            var q = new CarWorkshopRequest
                            {
                                Type = CarWorkshopRequestType.Construction,
                                OutputVehicleType = data.GetVehicleType(),
                                InputResources = data.ConstructionResources,
                                OutputResources = Array.Empty<VehicleData.CarConstructionResource>(),
                                ModificationsToInstall = Array.Empty<ModificationType>(),
                                ModificationsToRemove = Array.Empty<ModificationType>(),
                                ModificationsBefore = Array.Empty<ModificationType>(),
                                Time = data.WorkshopConstructionTime,
                                PreviousColor = Color.white,
                                ColorToSet = Color.white,
                            };
                            c.CreateConstructionRequestFor(b, q);
                            break;
                        }
                        case "scrap":
                        {
                            Vehicle best = null;
                            float bestD = float.MaxValue;
                            foreach (var v in GameAccess.Game<VehiclesController>().Vehicles)
                            {
                                if (v == null || v.UnderCarWorkshop || v.HasOwner) continue;
                                float d = (v.transform.position - b.transform.position).sqrMagnitude;
                                if (d < bestD) { bestD = d; best = v; }
                            }
                            if (best == null) return "kein freies Fahrzeug";
                            if (!GarageF(b).HasGarage) return "Werkstatt " + b.ID + " ohne Garage (erst \"shop garage\" beim Host)";
                            c.CreateDeconstructionRequestFor(b, best);
                            break;
                        }
                        case "cancel":
                            if (n >= h.RequestsQueue.Count) return "kein Auftrag " + n;
                            h.CancelRequest(h.RequestsQueue[n]);
                            break;
                        case "select":
                            if (n >= h.RequestsQueue.Count) return "kein Auftrag " + n;
                            h.SelectRequest(h.RequestsQueue[n]);
                            break;
                        case "done":
                        {
                            if (IsGuest) return "nur Host";
                            var w = b.CarWorkshopWork;
                            if (w?.CurrentRequest == null) return "kein laufender Auftrag";
                            if (w.CurrentRequest.Type != CarWorkshopRequestType.Deconstruction && !GarageF(b).HasGarage)
                                return "Werkstatt " + b.ID + " ohne Garage: neues Auto kann nicht ausgeliefert werden (erst \"shop garage\")";
                            w.CurrentRequest.SetProgress(1f);
                            AccessTools.Method(typeof(CarWorkshopController), "HandleRequestCompleted").Invoke(c, new object[] { w });
                            break;
                        }
                        case "progress":
                        {
                            if (IsGuest) return "nur Host";
                            var w = b.CarWorkshopWork;
                            if (w?.CurrentRequest == null) return "kein laufender Auftrag";
                            float f = a.Length > 2 ? float.Parse(a[2], CultureInfo.InvariantCulture) : 0.5f;
                            w.SetTimeConstructing(f * w.TimeToConstruct);
                            w.CurrentRequest.SetProgress(Mathf.Min(f, 0.95f));
                            break;
                        }
                        default:
                            return "shop garage|add|scrap|cancel|select|done|progress";
                    }
                    return $"Werkstatt {b.ID} {what}: {ShopLine(b)}";
                }
                case "custom":
                {
                    if (a.Length > 1 && a[1] == "done")
                    {
                        // Host, Testabkürzung: laufende Anpassung sofort fertig (wie HandleCustomizationEnd mit fertiger Arbeit)
                        if (IsGuest) return "nur Host";
                        var cb = Nearest(x => StateOf(x) == StructureStateType.Customization);
                        if (cb == null) return "keine Anpassung läuft";
                        CustF(cb).ApplyRequest();
                        var cw = cb.CurrentWork;
                        if (cw is CustomizationWork || cw is RepairStructureWork)
                            cw.DeleteWork(); // OnDeleted -> HandleCustomizationEnd/HandleRepairEnd -> Completed
                        if (StateOf(cb) == StructureStateType.Customization)
                            cb.StateMachine.ChangeTo(StructureStateType.Completed);
                        return $"Anpassung {cb.ID} fertig -> {StateOf(cb)} {LookLine(cb)}";
                    }
                    // Ein schlichtes fertiges Haus (keine Produktion, Werkstatt, Sägewerk, Reparaturtrupp)
                    var b = Nearest(x => !x.IsHq && StateOf(x) == StructureStateType.Completed && CustF(x) != null
                                         && x.ProductionWork == null && x.CarWorkshopWork == null && x.SawmillWork == null && x.HandymanWork == null);
                    if (b == null) return "kein fertiges Haus";
                    bool wall = a.Length > 1 && a[1] != "-";
                    bool roof = a.Length > 2 && a[2] != "-";
                    bool enf = a.Length > 3 && a[3] != "-";
                    var q = MakeCustomization(wall, wall ? ParseColor(a[1]) : Color.white, roof, roof ? ParseColor(a[2]) : Color.white,
                        enf, enf ? int.Parse(a[3], CultureInfo.InvariantCulture) : 0, null);
                    string name = a.Length > 4 ? string.Join(" ", a.Skip(4)).Replace('_', ' ') : null;
                    bool repair = b.Health != null && !b.Health.IsMax;
                    Customize(b, q, name);
                    return $"Anpassen {b.ID} -> {StateOf(b)} {LookLine(b)}{(repair ? " (erst Reparatur)" : "")}";
                }
                case "saw":
                {
                    var b = Nearest(x => x.SawmillWork != null);
                    if (b == null) return "kein Sägewerk";
                    b.SawmillWork.SetPlayerCondition(a.Length > 1 && a[1] == "1");
                    return $"Sägewerk {b.ID} cond={b.SawmillWork.PlayerCondition}";
                }
                case "handy":
                {
                    var b = Nearest(x => x.HandymanWork != null);
                    if (b == null) return "kein Reparaturtrupp";
                    var sec = (BuildingSection)Enum.Parse(typeof(BuildingSection), a[1]);
                    b.HandymanWork.ChangeSectionPriority(sec, int.Parse(a[2], CultureInfo.InvariantCulture));
                    return $"Reparaturtrupp {b.ID} {sec}={b.HandymanWork.SectionPriorities[sec]}";
                }
            }
            return "?";
        }

        // ================================================================ Status

        private static string ShopLine(Building b)
        {
            var h = ShopHandlerF(b);
            if (h == null) return "-";
            int sel = h.SelectedRequest != null ? h.RequestsQueue.IndexOf(h.SelectedRequest) : -1;
            string q = string.Join(",", h.RequestsQueue.Select(r => r.Type + ":" + r.InputVehicleType + ">" + r.OutputVehicleType + ":" + P(r.Time)));
            return $"n={h.RequestsQueue.Count} sel={sel} garage={(GarageF(b)?.HasGarage == true ? 1 : 0)} q={q}";
        }

        private static string LookLine(Building b)
        {
            var ch = CustF(b);
            var eh = EnfF(b);
            if (ch == null) return "-";
            var q = ch.CustomizationRequest;
            string req = q == null ? "-" : (q.ShouldChangeWallsColor ? ColorText(q.WallsColorToSet) : "-") + "/" + (q.ShouldChangeRoofColor ? ColorText(q.RoofColorToSet) : "-") + "/" + (q.ShouldApplyEnforcement ? q.EnforcementTypeToSet.ToString() : "-");
            return $"wall={(ch.HasDefaultWallColor ? "-" : ColorText(ch.WallsColor))} roof={(ch.HasDefaultRoofColor ? "-" : ColorText(ch.RoofColor))} enf={eh?.CurrentEnforcementType} name={(NameOverF(b) ? NameF(b)?.Replace(' ', '_') : "-")} req={req}";
        }

        /// <summary>
        /// Zum Vergleichen: prod/shop/look/saw/handy. Fortschritt in eigenen Zeilen (prodprog/shopprog), die Host und Gast
        /// nur ungefähr gleich haben.
        /// </summary>
        public static IEnumerable<string> StatusLines()
        {
            var prods = Productions();
            yield return $"prodcount n={prods.Count}";
            foreach (var kv in prods)
            {
                var pw = kv.Value;
                yield return $"prod {kv.Key} cur={pw.ProductionData?.Id ?? "-"} next={pw.NextProductionData?.Id ?? "-"} lim={Mathf.RoundToInt(pw.ProductionLimit)}";
                yield return $"prodprog {kv.Key} p={Mathf.RoundToInt(pw.Progress * 100f)} day={pw.DailyProducedAmount}";
            }
            foreach (var b in ShopBuildings())
            {
                yield return $"shop {b.ID} {ShopLine(b)}";
                var w = b.CarWorkshopWork;
                var h = ShopHandlerF(b);
                yield return $"shopprog {b.ID} p={string.Join(",", h.RequestsQueue.Select(r => Mathf.RoundToInt(r.Progress * 100f)))} t={(w != null ? P(w.TimeConstructing) + "/" + P(w.TimeToConstruct) : "-")}";
            }
            var bc = Bc;
            if (bc?.Buildings == null)
                yield break;
            foreach (var b in bc.Buildings.Where(x => x != null).OrderBy(x => x.ID))
            {
                var ch = CustF(b);
                if (ch != null && !IsLookDefault(b, ch, EnfF(b)))
                    yield return $"look {b.ID} {LookLine(b)}";
                if (b.SawmillWork != null)
                    yield return $"saw {b.ID} cond={(b.SawmillWork.PlayerCondition ? 1 : 0)}";
                if (b.HandymanWork?.SectionPriorities != null)
                    yield return $"handy {b.ID} {string.Join(",", b.HandymanWork.SectionPriorities.OrderBy(p => (int)p.Key).Select(p => p.Key + "=" + p.Value))}";
            }
        }

        public string StatusLine() =>
            $"productionApplied={Applied} prodChanged={ProdChanged} shopRebuilt={ShopRebuilt} lookChanged={LookChanged} cleaned={Cleaned} garages={GaragesPlaced} missingProductions={MissingProductions} requestsSent={RequestsSent} requestsDone={RequestsDone}";
    }
}
