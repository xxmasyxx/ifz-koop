using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Animations;
using Controllers;
using Controllers.CharacterLogic;
using Gameplay.Buildings;
using Gameplay.Core.EnterableSystem;
using Gameplay.FogOfWar;
using Gameplay.GameResources;
using Gameplay.Units.Characters;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using ScriptableObjectScripts.UI;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Arbeiter-Figuren als Marionetten des Hosts (Analyse W4 Stufe 2, Teil „Figuren“; DESIGN: Arbeiter 2–5 Hz).
    /// Gilt für alle Bürger außerhalb von Trupps (Arbeiter, Kinder, Kranke – wie PopulationSync).
    ///
    /// Host: alle 0,3 s für jeden Bürger, der sich bewegt oder geändert hat (mehr als 15 cm, Blick, Tempo, Haus, Kiste,
    /// Arbeits-Animation), einen kurzen Eintrag; alle 5 s alle. Auch ohne Änderung geht jede 0,3 s eine (fast leere) Nachricht
    /// raus: wer darin fehlt, steht beim Host (der Gast hört dann auf vorauszurechnen).
    /// Gast: Character.CustomUpdate läuft für diese Figuren nur noch als Marionetten-Takt (gleiten, drehen, Laufanimation,
    /// Nebel des Krieges). Eigene KI (WorkerBehaviour.Tick), Arbeit ausführen (WorkModule.ExecuteWork, also kein eigener
    /// Baufortschritt) und Griffe ins Lager (ResourceInHandModule.GetResourceFrom/ReturnResourceTo/ReturnResourceToStructure)
    /// sind gesperrt (UnitPatches). Ein-/Austreten in Häuser, Kiste in der Hand und Arbeits-Animation kommen vom Host.
    /// Die Zuteilung (WorkSync) und die Bürgerliste (PopulationSync) bleiben unberührt.
    /// </summary>
    internal sealed class WorkerPosSync
    {
        private const float SendInterval = 0.3f;
        private const float FullInterval = 5f;
        private const float MinMove = 0.15f;
        private const float TeleportDistance = 25f;
        private const float MaxExtrapolate = 0.45f;
        private const float MaxSpeed = 15f;
        private const float StatsWindow = 10f;

        [Flags]
        private enum F : byte { None = 0, Inside = 1, Box = 2, Working = 4, Moving = 8 }

        /// <summary>Wo der Host-Bürger drin ist.</summary>
        private enum Where : byte { None = 0, Building = 1, WorkPlace = 2, Other = 3 }

        private sealed class Sample
        {
            public Vector3 Pos;
            public byte Yaw;
            public byte Speed;
            public F Flags;
            public Where Where;
            public long BuildingId;
            public int Box;
            public float BoxQty;
            public byte Emp;
        }

        private sealed class Target
        {
            public string Id;
            public Sample S = new Sample();
            public Vector3 Vel;
            public float HostTime;
            public float RecvAt;
            public float AdoptedAt;
            public bool Stopped;      // eigener Weg des Gastes einmal abgebrochen
            public bool Dirty;        // Haus/Kiste/Animation neu anwenden
            public float NextWorkAnim;
        }

        /// <summary>Gast mit Host-Daten: der Prefix von Character.CustomUpdate fragt nur dieses Feld ab (billig).</summary>
        internal static WorkerPosSync Active;

        /// <summary>Gesperrte Lager-Griffe von Gast-Figuren (UnitPatches.WorkerStockPrefix).</summary>
        internal static int StockBlocked;

        private static readonly AccessTools.FieldRef<CharacterAnimationController, bool> WorkingField =
            AccessTools.FieldRefAccess<CharacterAnimationController, bool>("Working");
        private static readonly AccessTools.FieldRef<Character, FogOfWarObserver> ObserverField =
            AccessTools.FieldRefAccess<Character, FogOfWarObserver>("_fogOfWarObserver");
        private static readonly AccessTools.FieldRef<Character, bool> UpdateFogField =
            AccessTools.FieldRefAccess<Character, bool>("_updateFogOfWar");

        private readonly Session _session;

        // Host
        private readonly Dictionary<string, Sample> _sent = new Dictionary<string, Sample>();
        private float _nextSend;
        private float _nextFull;
        private float _rateStart = -1f;
        private long _rateBytes;

        public int MessagesSent { get; private set; }
        public long EntriesSent { get; private set; }
        public int LastEntries { get; private set; }
        public float BytesPerSecond { get; private set; }

        // Gast
        private readonly Dictionary<string, Target> _byId = new Dictionary<string, Target>();
        private readonly Dictionary<Character, Target> _byChar = new Dictionary<Character, Target>();
        private readonly Dictionary<Character, Resource> _handCrates = new Dictionary<Character, Resource>();
        private float _nextSecond;
        private MainResourcesContainer _watchedStock;
        private float _winStart = -1f;
        private double _winSum;
        private int _winCount;
        private float _winMax;
        private string _logOnce;

        public int MessagesReceived { get; private set; }
        public int Teleports { get; private set; }
        public int Entered { get; private set; }
        public int Exited { get; private set; }
        public int BoxesCreated { get; private set; }
        public int BoxesRemoved { get; private set; }
        public int StockLocal { get; private set; }
        public float DevMean { get; private set; }
        public float DevMax { get; private set; }
        public int DevCount { get; private set; }
        public float DevMaxTotal { get; private set; }
        public int DevOver2m { get; private set; }

        public WorkerPosSync(Session session)
        {
            _session = session;
        }

        public void ResendAll()
        {
            _sent.Clear();
            _nextSend = 0f;
            _nextFull = 0f;
        }

        public void Reset()
        {
            ResendAll();
            _byId.Clear();
            _byChar.Clear();
            _handCrates.Clear();
            if (_watchedStock != null)
            {
                try { _watchedStock.OnResourcesUpdated -= OnStockUpdated; } catch { }
                _watchedStock = null;
            }
            Active = null;
        }

        private bool GuestPuppet => _session.Role == Role.Guest && _session.GuestControlsLocked;

        // ================================================================ Gemeinsam

        private static IEnumerable<Character> Civilians()
        {
            var list = GameAccess.Game<CitizensController>()?.Citizens;
            if (list == null)
                yield break;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c != null && c.Group == null && !string.IsNullOrEmpty(c.Id) && !(c.Health != null && c.Health.IsDead()) && c.Movement != null)
                    yield return c;
            }
        }

        private static byte YawToByte(float yaw) => (byte)Mathf.RoundToInt(Mathf.Repeat(yaw, 360f) / 360f * 255f);

        private static float ByteToYaw(byte b) => b / 255f * 360f;

        private static bool IsWorking(Character c)
        {
            var anim = c.AnimationController;
            if (anim == null)
                return false;
            try { return WorkingField(anim); } catch { return false; }
        }

        private static Sample TakeSample(Character c)
        {
            var s = new Sample();
            var m = c.Movement;
            s.Pos = m.Position;
            s.Yaw = YawToByte(m.transform.eulerAngles.y);
            s.Speed = (byte)Mathf.Clamp(Mathf.RoundToInt(m.NormalizedSpeed * 100f), 0, 255);
            if (s.Speed > 0) s.Flags |= F.Moving;
            if (c.IsInsideEnterable())
            {
                s.Flags |= F.Inside;
                var e = c.Enterable;
                if (e is Building b)
                {
                    s.Where = Where.Building;
                    s.BuildingId = b.ID;
                }
                else
                {
                    IEnterable we = null;
                    try { we = c.WorkModule?.CurrentWork?.Enterable; } catch { }
                    s.Where = we != null && ReferenceEquals(we, e) ? Where.WorkPlace : Where.Other;
                }
            }
            var rih = c.ResourceInHandModule;
            if (rih != null && rih.HaveResource() && rih.Resource.ResourceCrate != null)
            {
                s.Flags |= F.Box;
                s.Box = (int)rih.Resource.ResourceCrate.ResourceID;
                s.BoxQty = rih.Resource.ResourceCrate.Quantity;
            }
            if (IsWorking(c))
            {
                s.Flags |= F.Working;
                try { s.Emp = (byte)(c.WorkModule?.CurrentWork?.EmploymentType() ?? Employment.Unemployed); } catch { }
            }
            return s;
        }

        private static bool Changed(Sample a, Sample b)
        {
            if (a.Flags != b.Flags || a.Where != b.Where || a.BuildingId != b.BuildingId || a.Box != b.Box || a.Emp != b.Emp)
                return true;
            if (Mathf.Abs(a.BoxQty - b.BoxQty) > 0.01f)
                return true;
            if ((a.Pos - b.Pos).sqrMagnitude > MinMove * MinMove)
                return true;
            int dy = Math.Abs(a.Yaw - b.Yaw);
            if (Math.Min(dy, 256 - dy) >= 4)
                return true;
            if ((a.Speed == 0) != (b.Speed == 0) || Math.Abs(a.Speed - b.Speed) >= 15)
                return true;
            return false;
        }

        // ================================================================ Host

        public void HostUpdate()
        {
            if (_session.ActiveGuestCount == 0 || !GameAccess.IsGameStarted)
                return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend)
                return;
            _nextSend = now + SendInterval;
            bool full = now >= _nextFull;
            if (full) _nextFull = now + FullInterval;

            var changed = new List<KeyValuePair<string, Sample>>();
            var seen = new HashSet<string>();
            foreach (var c in Civilians())
            {
                if (!seen.Add(c.Id))
                    continue;
                Sample s;
                try { s = TakeSample(c); }
                catch (Exception e) { Plugin.Log.LogDebug($"Arbeiter-Ort {c.Id}: {e.Message}"); continue; }
                // Nur Gesendetes merken: sonst würde langsames Kriechen (je Takt unter der Schwelle) nie gemeldet.
                if (full || !_sent.TryGetValue(c.Id, out var prev) || Changed(prev, s))
                {
                    changed.Add(new KeyValuePair<string, Sample>(c.Id, s));
                    _sent[c.Id] = s;
                }
            }
            var removed = new List<string>();
            foreach (var id in _sent.Keys)
                if (!seen.Contains(id)) removed.Add(id);
            foreach (var id in removed) _sent.Remove(id);
            if (changed.Count > 65000) changed.RemoveRange(65000, changed.Count - 65000);
            if (removed.Count > 65000) removed.RemoveRange(65000, removed.Count - 65000);

            var msg = Protocol.Build(Msg.WorkerPos, w =>
            {
                w.Write(now);
                w.Write(full);
                w.Write((ushort)changed.Count);
                foreach (var kv in changed)
                {
                    var s = kv.Value;
                    w.Write(kv.Key);
                    w.Write(s.Pos.x);
                    w.Write(s.Pos.z);
                    w.Write((short)Mathf.Clamp(Mathf.RoundToInt(s.Pos.y * 10f), short.MinValue, short.MaxValue));
                    w.Write(s.Yaw);
                    w.Write(s.Speed);
                    w.Write((byte)s.Flags);
                    if ((s.Flags & F.Inside) != 0)
                    {
                        w.Write((byte)s.Where);
                        if (s.Where == Where.Building)
                            w.Write(s.BuildingId);
                    }
                    if ((s.Flags & F.Box) != 0)
                    {
                        w.Write(s.Box);
                        w.Write(s.BoxQty);
                    }
                    if ((s.Flags & F.Working) != 0)
                        w.Write(s.Emp);
                }
                w.Write((ushort)removed.Count);
                foreach (var id in removed) w.Write(id);
            });
            _session.BroadcastToGuests(msg);
            MessagesSent++;
            EntriesSent += changed.Count;
            LastEntries = changed.Count;

            if (_rateStart < 0f) _rateStart = now;
            _rateBytes += msg.Length;
            if (now - _rateStart >= StatsWindow)
            {
                BytesPerSecond = _rateBytes / (now - _rateStart);
                _rateStart = now;
                _rateBytes = 0;
            }
        }

        // ================================================================ Gast: empfangen

        public void GuestOnState(BinaryReader r)
        {
            float hostTime = r.ReadSingle();
            bool full = r.ReadBoolean();
            int n = r.ReadUInt16();
            var entries = new List<KeyValuePair<string, Sample>>(n);
            for (int i = 0; i < n; i++)
            {
                string id = r.ReadString();
                var s = new Sample();
                float x = r.ReadSingle();
                float z = r.ReadSingle();
                float y = r.ReadInt16() / 10f;
                s.Pos = new Vector3(x, y, z);
                s.Yaw = r.ReadByte();
                s.Speed = r.ReadByte();
                s.Flags = (F)r.ReadByte();
                if ((s.Flags & F.Inside) != 0)
                {
                    s.Where = (Where)r.ReadByte();
                    if (s.Where == Where.Building)
                        s.BuildingId = r.ReadInt64();
                }
                if ((s.Flags & F.Box) != 0)
                {
                    s.Box = r.ReadInt32();
                    s.BoxQty = r.ReadSingle();
                }
                if ((s.Flags & F.Working) != 0)
                    s.Emp = r.ReadByte();
                entries.Add(new KeyValuePair<string, Sample>(id, s));
            }
            int m = r.ReadUInt16();
            var removed = new List<string>(m);
            for (int i = 0; i < m; i++) removed.Add(r.ReadString());

            MessagesReceived++;
            if (!GuestPuppet)
                return;
            Active = this;
            float now = Time.realtimeSinceStartup;

            var civ = new Dictionary<string, Character>();
            foreach (var c in Civilians())
                if (!civ.ContainsKey(c.Id)) civ[c.Id] = c;

            var inMsg = new HashSet<string>();
            foreach (var kv in entries)
            {
                var s = kv.Value;
                inMsg.Add(kv.Key);
                bool isNew = !_byId.TryGetValue(kv.Key, out var t);
                if (isNew)
                {
                    t = new Target { Id = kv.Key, AdoptedAt = now };
                    _byId[kv.Key] = t;
                }
                civ.TryGetValue(kv.Key, out var ch);

                // Abweichung: angezeigte Gast-Position gegen die neue Host-Position (draußen, nach der Eingewöhnung)
                if (!isNew && ch != null && now - t.AdoptedAt > 1.5f && (t.S.Flags & F.Inside) == 0 && (s.Flags & F.Inside) == 0
                    && !ch.IsInsideEnterable())
                {
                    var d = ch.Position - s.Pos;
                    d.y = 0f;
                    Record(d.magnitude, now);
                }

                var vel = Vector3.zero;
                if (!isNew && (s.Flags & F.Moving) != 0 && (s.Flags & F.Inside) == 0)
                {
                    float dt = hostTime - t.HostTime;
                    if (dt > 0.05f && dt < 1.5f)
                    {
                        vel = (s.Pos - t.S.Pos) / dt;
                        vel.y = 0f;
                        if (vel.sqrMagnitude > MaxSpeed * MaxSpeed)
                            vel = Vector3.zero;
                    }
                }
                bool stateChanged = isNew || t.S.Flags != s.Flags || t.S.Where != s.Where || t.S.BuildingId != s.BuildingId
                    || t.S.Box != s.Box || t.S.Emp != s.Emp;
                t.S = s;
                t.Vel = vel;
                t.HostTime = hostTime;
                t.RecvAt = now;
                t.Dirty |= stateChanged || full;
            }
            // Nicht dabei = beim Host unverändert: steht, also nicht weiter vorausrechnen.
            foreach (var t in _byId.Values)
                if (!inMsg.Contains(t.Id))
                    t.Vel = Vector3.zero;
            foreach (var id in removed)
                DropTarget(id);
            if (full)
                foreach (var id in _byId.Keys.Where(k => !inMsg.Contains(k)).ToList())
                    DropTarget(id);

            // Zuordnung Figur → Ziel neu (Figuren können neu angelegt oder ersetzt worden sein)
            foreach (var kv in _byChar.ToList())
                if (kv.Key == null || !_byId.ContainsKey(kv.Value.Id) || !civ.TryGetValue(kv.Value.Id, out var cur) || !ReferenceEquals(cur, kv.Key))
                    _byChar.Remove(kv.Key);
            foreach (var t in _byId.Values)
            {
                if (!civ.TryGetValue(t.Id, out var c))
                    continue;
                if (!_byChar.ContainsKey(c))
                {
                    _byChar[c] = t;
                    t.Stopped = false;
                    t.Dirty = true;
                }
                if (t.Dirty)
                {
                    t.Dirty = false;
                    ApplyState(c, t, now);
                }
            }
        }

        /// <summary>Host hat den Bürger nicht mehr (Trupp, tot): Ziel weg, selbst angelegte Kiste in der Hand weg.</summary>
        private void DropTarget(string id)
        {
            _byId.Remove(id);
            foreach (var kv in _handCrates.ToList())
            {
                if (kv.Key == null || kv.Key.Id != id)
                    continue;
                _handCrates.Remove(kv.Key);
                try
                {
                    var rih = kv.Key.ResourceInHandModule;
                    if (kv.Value != null && !kv.Value.IsDestroying && rih != null && ReferenceEquals(rih.Resource, kv.Value))
                    {
                        kv.Value.Destroy(Resource.DestroyReason.ReturnedToResourceContainer);
                        BoxesRemoved++;
                    }
                }
                catch (Exception e) { LogOnce("Kiste weg " + id + ": " + e.Message); }
            }
        }

        private void Record(float d, float now)
        {
            if (_winStart < 0f) _winStart = now;
            _winSum += d;
            _winCount++;
            if (d > _winMax) _winMax = d;
            if (d > DevMaxTotal) DevMaxTotal = d;
            if (d > 2f) DevOver2m++;
        }

        private void RollWindow(float now)
        {
            if (_winStart < 0f || now - _winStart < StatsWindow)
                return;
            DevCount = _winCount;
            DevMean = _winCount > 0 ? (float)(_winSum / _winCount) : 0f;
            DevMax = _winMax;
            _winStart = now;
            _winSum = 0;
            _winCount = 0;
            _winMax = 0f;
        }

        // ================================================================ Gast: Zustand (Haus, Kiste, Arbeit)

        private void ApplyState(Character c, Target t, float now)
        {
            try { ApplyBox(c, t); }
            catch (Exception e) { LogOnce("Kiste " + c.Id + ": " + e.Message); }
            try { ApplyInside(c, t); }
            catch (Exception e) { LogOnce("Haus " + c.Id + ": " + e.Message); }
            try { ApplyWorkAnim(c, t, now, force: true); }
            catch (Exception e) { LogOnce("Animation " + c.Id + ": " + e.Message); }
        }

        private void ApplyBox(Character c, Target t)
        {
            var rih = c.ResourceInHandModule;
            if (rih == null)
                return;
            bool want = (t.S.Flags & F.Box) != 0;
            bool have = rih.HaveResource();
            if (have && (!want || rih.Resource.ResourceCrate == null || (int)rih.Resource.ResourceCrate.ResourceID != t.S.Box))
            {
                // Kiste weg ohne Lager: nur das Objekt zerstören (gibt nichts ins Lager, fällt nicht zu Boden)
                var res = rih.Resource;
                _handCrates.Remove(c);
                res.Destroy(Resource.DestroyReason.ReturnedToResourceContainer);
                BoxesRemoved++;
                have = false;
            }
            if (!want || have)
                return;
            float q = t.S.BoxQty > 0f ? t.S.BoxQty : 1f;
            var crate = new ResourceCrate((ResourceID)t.S.Box, q, Mathf.Max(1f, q));
            var created = rih.CreateResource(crate);
            if (created == null)
                return;
            rih.AssignResource(created);
            _handCrates[c] = created;
            BoxesCreated++;
        }

        private void ApplyInside(Character c, Target t)
        {
            bool hostIn = (t.S.Flags & F.Inside) != 0;
            bool guestIn = c.IsInsideEnterable();
            if (hostIn)
            {
                IEnterable want = null;
                if (t.S.Where == Where.Building)
                    want = GameAccess.Game<BuildingsController>()?.GetBuildingById(t.S.BuildingId);
                else if (t.S.Where == Where.WorkPlace)
                    want = c.WorkModule?.CurrentWork?.Enterable;
                if (want == null || (want is UnityEngine.Object o && o == null))
                    return; // beim Gast nicht zu finden: draußen weiter dem Host-Ort folgen
                if (guestIn && ReferenceEquals(c.Enterable, want))
                    return;
                if (guestIn)
                    c.ExitEnterable();
                c.EnteredEnterableImmediately(want);
                Entered++;
            }
            else if (guestIn)
            {
                c.ExitEnterable();
                c.Movement.Teleport(t.S.Pos);
                Exited++;
            }
        }

        private static void ApplyWorkAnim(Character c, Target t, float now, bool force)
        {
            var anim = c.AnimationController;
            if (anim == null)
                return;
            bool hostWork = (t.S.Flags & F.Working) != 0;
            bool guestWork = WorkingField(anim);
            if (hostWork)
            {
                // Wie beim Host (Work.Execute etwa 1×/s): immer wieder setzen, solange die Figur steht
                if (force || !guestWork || (t.S.Speed == 0 && now >= t.NextWorkAnim))
                {
                    anim.SetWorkAnimation((Employment)t.S.Emp);
                    t.NextWorkAnim = now + 1f;
                }
            }
            else if (guestWork)
            {
                anim.StopWorkAnimation();
            }
        }

        // ================================================================ Gast: jeden Frame

        /// <summary>
        /// Prefix von Character.CustomUpdate beim Gast. true = erledigt (Original nicht ausführen).
        /// Ersatz für Zustandsmaschine/Weg/Hausautomatik: zum Host-Ort gleiten, drehen, Laufanimation, Nebel des Krieges.
        /// </summary>
        public bool PuppetTick(Character c)
        {
            if (c == null || c.Group != null || !_byChar.TryGetValue(c, out var t))
                return false;
            try
            {
                c.UpdateCurrentFrame();
                var m = c.Movement;
                if (m == null)
                    return true;
                if (!t.Stopped)
                {
                    t.Stopped = true;
                    m.StopMoving();
                }
                float now = Time.realtimeSinceStartup;
                if (!c.IsInsideEnterable())
                {
                    float age = Mathf.Clamp(now - t.RecvAt, 0f, MaxExtrapolate);
                    var want = t.S.Pos + t.Vel * age;
                    var cur = m.Position;
                    if ((cur - want).sqrMagnitude > TeleportDistance * TeleportDistance)
                    {
                        m.Teleport(want);
                        Teleports++;
                    }
                    else
                    {
                        float k = 1f - Mathf.Exp(-10f * Time.unscaledDeltaTime);
                        m.SetPosition(Vector3.Lerp(cur, want, k), updateOnExpedition: false);
                    }
                    var tr = m.transform;
                    float kr = 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime);
                    tr.rotation = Quaternion.Slerp(tr.rotation, Quaternion.Euler(0f, ByteToYaw(t.S.Yaw), 0f), kr);
                    if (c.IsUpdateFrame)
                    {
                        var anim = c.AnimationController;
                        if (anim != null)
                        {
                            try { anim.SetSpeed(t.S.Speed / 100f); } catch { }
                            if ((t.S.Flags & F.Working) != 0)
                                ApplyWorkAnim(c, t, now, force: false);
                        }
                    }
                }
                if (UpdateFogField(c))
                {
                    var obs = ObserverField(c);
                    if (obs != null && obs.DetectionEnabled)
                        obs.CustomUpdate(c.Position);
                }
            }
            catch (Exception e)
            {
                LogOnce("Marionetten-Takt Arbeiter: " + e.Message);
            }
            return true;
        }

        /// <summary>Prefix Character.DestroyCharacter: beim Gast die Kiste in der Hand verschwinden lassen (nicht ins Lager, nicht zu Boden).</summary>
        public void OnCharacterDestroyed(Character c)
        {
            if (c == null)
                return;
            _byChar.Remove(c);
            _handCrates.Remove(c);
            if (!GuestPuppet || c.Group != null)
                return;
            var rih = c.ResourceInHandModule;
            if (rih != null && rih.HaveResource())
            {
                rih.Resource.Destroy(Resource.DestroyReason.ReturnedToResourceContainer);
                BoxesRemoved++;
            }
        }

        public void GuestFrame()
        {
            if (_session.Role != Role.Guest)
                return;
            if (!_session.GuestControlsLocked)
            {
                if (Active != null) Active = null;
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (now < _nextSecond)
                return;
            _nextSecond = now + 1f;
            RollWindow(now);
            WatchStock();
            foreach (var c in _handCrates.Keys.ToList())
                if (c == null || _handCrates[c] == null) _handCrates.Remove(c);
            foreach (var c in _byChar.Keys.ToList())
                if (c == null || c.Group != null) _byChar.Remove(c);
        }

        /// <summary>Zählt Lager-Änderungen beim Gast, die nicht vom Host kommen (sollte 0 bleiben).</summary>
        private void WatchStock()
        {
            var stock = GameAccess.Game<StockroomsController>()?.Container;
            if (stock == null)
                return;
            try
            {
                if (_watchedStock != null && !ReferenceEquals(_watchedStock, stock))
                    _watchedStock.OnResourcesUpdated -= OnStockUpdated;
                // jedes Mal neu anmelden: das Spiel setzt die Ereignisse beim Leeren auf null
                stock.OnResourcesUpdated -= OnStockUpdated;
                stock.OnResourcesUpdated += OnStockUpdated;
                _watchedStock = stock;
            }
            catch (Exception e) { LogOnce("Lager beobachten: " + e.Message); }
        }

        private void OnStockUpdated()
        {
            if (GuestPuppet && !UnitSync.NetApplying)
                StockLocal++;
        }

        private void LogOnce(string text)
        {
            if (text == _logOnce)
                return;
            _logOnce = text;
            Plugin.Log.LogWarning("IFZCOOP workerpos " + text);
        }

        // ================================================================ Test und Status

        /// <summary>Testbefehl (Host): ersten Arbeiter draußen an HQ + (dx, dz) versetzen.</summary>
        public static string QaTeleport(Vector3 hqPos, float dx, float dz)
        {
            foreach (var c in Civilians())
            {
                if (c.IsChild || c.IsSick || c.IsInsideEnterable())
                    continue;
                c.Movement.Teleport(hqPos + new Vector3(dx, 0f, dz));
                return "versetzt " + c.Id + " nach " + c.Position.x.ToString("0", CultureInfo.InvariantCulture) + "," + c.Position.z.ToString("0", CultureInfo.InvariantCulture);
            }
            return "kein Arbeiter draußen";
        }

        private static string InsideKey(Character c)
        {
            if (!c.IsInsideEnterable())
                return "-";
            if (c.Enterable is Building b)
                return b.ID.ToString(CultureInfo.InvariantCulture);
            return "o";
        }

        /// <summary>Für die Statusdatei (beide Seiten gleich gerechnet): Zähler und je Bürger Ort (1 m), Haus, Kiste.</summary>
        public static IEnumerable<string> StatusLines()
        {
            var inv = CultureInfo.InvariantCulture;
            var all = Civilians().OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
            int inside = 0, box = 0, working = 0, moving = 0;
            var lines = new List<string>(all.Count);
            foreach (var c in all)
            {
                bool inH = c.IsInsideEnterable();
                bool hasBox = c.ResourceInHandModule != null && c.ResourceInHandModule.HaveResource();
                if (inH) inside++;
                if (hasBox) box++;
                if (IsWorking(c)) working++;
                if (c.Movement.NormalizedSpeed > 0f) moving++;
                var p = c.Position;
                string boxId = hasBox && c.ResourceInHandModule.Resource.ResourceCrate != null ? c.ResourceInHandModule.Resource.ResourceCrate.ResourceID.ToString() : "-";
                lines.Add($"wpos {c.Id} {p.x.ToString("0", inv)},{p.z.ToString("0", inv)} in={InsideKey(c)} box={boxId}");
            }
            yield return $"workerpos civilians={all.Count} inside={inside} box={box} working={working} moving={moving}";
            foreach (var l in lines)
                yield return l;
        }

        public string StatusLine()
        {
            var inv = CultureInfo.InvariantCulture;
            if (_session.Role == Role.Host)
                return $"workerposStats role=host sent={MessagesSent} entries={EntriesSent} lastEntries={LastEntries} bytesPerSec={BytesPerSecond.ToString("0", inv)} tracked={_sent.Count}";
            return $"workerposStats role=guest received={MessagesReceived} targets={_byId.Count} puppets={_byChar.Count} devMean={DevMean.ToString("0.00", inv)} devMax={DevMax.ToString("0.00", inv)} devN={DevCount} devMaxTotal={DevMaxTotal.ToString("0.00", inv)} devOver2m={DevOver2m} teleports={Teleports} entered={Entered} exited={Exited} boxesCreated={BoxesCreated} boxesRemoved={BoxesRemoved} stockBlocked={StockBlocked} stockLocal={StockLocal}";
        }
    }
}
