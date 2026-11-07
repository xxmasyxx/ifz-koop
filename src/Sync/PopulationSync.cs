using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Controllers.CharacterLogic;
using Controllers.Sickness;
using Gameplay.Units.Characters;
using Gameplay.Units.Player.Workers;
using IfzCoop.Game;
using IfzCoop.Net;
using Newtonsoft.Json;
using Serialization.Data.Citizens;
using UnityEngine;
using GameJson = Utilities.JsonSerializer;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Bevölkerung (Analyse W5, W6; Paket I): Bürger außerhalb von Trupps (Arbeiter, Kinder, Kranke).
    ///
    /// Host: 1×/s Unterschrift (IDs, Kind, krank). Bei Änderung die ganze ID-Liste mit Art und für neue oder geänderte Bürger
    /// das CharacterSerializedInfo (wie im Spielstand).
    /// Gast: legt fehlende Bürger wie beim Laden an (Arbeiter: WorkersController.CreateWorker + Deserialize; Kinder und Kranke:
    /// CitizensController.LoadCitizen), überzählige sterben, Krankheit wird gesetzt bzw. geheilt. Eigene Geburten und neue Arbeiter
    /// sind beim Gast gesperrt (UnitPatches). Tode kommen zusätzlich wie bisher über CharacterDied.
    /// </summary>
    internal sealed class PopulationSync
    {
        private const float SendInterval = 1f;

        private enum Kind : byte { Worker = 0, Child = 1, Sick = 2 }

        private readonly Session _session;
        private readonly Dictionary<string, Kind> _sent = new Dictionary<string, Kind>();
        private readonly Dictionary<string, float> _missingSince = new Dictionary<string, float>();
        private string _sentSig;
        private float _nextSend;
        private float _nextForce;

        public int Created { get; private set; }
        public int Removed { get; private set; }
        public int SickChanged { get; private set; }

        public PopulationSync(Session session)
        {
            _session = session;
        }

        public void ResendAll()
        {
            _sent.Clear();
            _sentSig = null;
        }

        public void Reset()
        {
            ResendAll();
            _missingSince.Clear();
        }

        private static IEnumerable<Character> Civilians()
        {
            var list = GameAccess.Game<CitizensController>()?.Citizens;
            if (list == null)
                yield break;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c != null && c.Group == null && !string.IsNullOrEmpty(c.Id) && !(c.Health != null && c.Health.IsDead()))
                    yield return c;
            }
        }

        private static Kind KindOf(Character c) => c.IsSick ? Kind.Sick : c.IsChild ? Kind.Child : Kind.Worker;

        // ================================================================ Host

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend || _session.ActiveGuestCount == 0 || !GameAccess.IsGameStarted)
                return;
            _nextSend = now + SendInterval;

            var all = Civilians().OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
            var sb = new StringBuilder();
            foreach (var c in all) sb.Append(c.Id).Append((int)KindOf(c)).Append(',');
            string sig = sb.ToString();
            // auch ohne Änderung alle 10 s die Liste (der Gast räumt dann Doppelte und Übriggebliebene weg)
            if (sig == _sentSig && now < _nextForce)
                return;
            _nextForce = now + 10f;

            var infos = new List<string>();
            foreach (var c in all)
            {
                var k = KindOf(c);
                if (_sent.TryGetValue(c.Id, out var last) && last == k)
                    continue;
                try { infos.Add(JsonConvert.SerializeObject(new CharacterSerializedInfo(c), GameJson.DefaultSettings)); }
                catch (Exception e) { Plugin.Log.LogWarning($"Bürger {c.Id} lässt sich nicht übertragen: {e.Message}"); continue; }
            }
            _sentSig = sig;
            _sent.Clear();
            foreach (var c in all) _sent[c.Id] = KindOf(c);

            _session.BroadcastToGuests(Protocol.Build(Msg.PopulationState, w =>
            {
                w.Write(all.Count);
                foreach (var c in all)
                {
                    w.Write(c.Id);
                    w.Write((byte)KindOf(c));
                }
                w.Write(infos.Count);
                foreach (var i in infos) w.Write(i);
            }));
        }

        // ================================================================ Gast

        public void GuestOnState(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 100000)
                throw new InvalidDataException("zu viele Bürger");
            var host = new Dictionary<string, Kind>(n);
            for (int i = 0; i < n; i++)
            {
                string id = r.ReadString();
                host[id] = (Kind)r.ReadByte();
            }
            int m = r.ReadInt32();
            if (m < 0 || m > 100000)
                throw new InvalidDataException("zu viele Bürger");
            var infos = new Dictionary<string, CharacterSerializedInfo>();
            for (int i = 0; i < m; i++)
            {
                string json = r.ReadString();
                try
                {
                    var info = JsonConvert.DeserializeObject<CharacterSerializedInfo>(json, GameJson.DefaultSettings);
                    if (info?.CharacterInfo != null && !string.IsNullOrEmpty(info.CharacterInfo.CharacterId))
                        infos[info.CharacterInfo.CharacterId] = info;
                }
                catch (Exception e) { Plugin.Log.LogWarning("Bürger nicht lesbar: " + e.Message); }
            }

            var cc = GameAccess.Game<CitizensController>();
            var wc = GameAccess.Game<WorkersController>();
            if (cc == null || wc == null)
                return;
            var mine = new Dictionary<string, Character>();
            var squadsCtl = GameAccess.Game<Gameplay.Units.SquadsController>();
            foreach (var c in Civilians())
            {
                // Doppelte: dieselbe Figur ist beim Gast auch Trupp-Mitglied (Trupp kam als neue Figur) oder doppelt in der Liste
                var member = squadsCtl?.FindSquadMember(c.Id);
                if ((member != null && !ReferenceEquals(member, c)) || mine.ContainsKey(c.Id))
                {
                    RemoveSilently(cc, wc, c);
                    continue;
                }
                mine[c.Id] = c;
            }

            UnitSync.NetApplying = true;
            try
            {
                // Beim Gast zu viel: sterben lassen (Gast-eigene Bürger oder beim Host gestorben)
                float now = Time.realtimeSinceStartup;
                foreach (var id in _missingSince.Keys.ToList())
                    if (host.ContainsKey(id) || !mine.ContainsKey(id)) _missingSince.Remove(id);
                foreach (var kv in mine)
                {
                    if (host.ContainsKey(kv.Key))
                        continue;
                    // erst nach 6 s: ein Arbeiter, der beim Host gerade in einen Trupp gewechselt ist, kommt über SquadSync
                    if (!_missingSince.TryGetValue(kv.Key, out var since)) { _missingSince[kv.Key] = now; continue; }
                    if (now - since < 6f)
                        continue;
                    _missingSince.Remove(kv.Key);
                    // Trupp-Mitglied beim Host? Dann ist der Bürger nicht weg, sondern in einen Trupp gewechselt (SquadSync)
                    if (GameAccess.Game<Gameplay.Units.SquadsController>()?.FindSquadMember(kv.Key) != null)
                        continue;
                    try
                    {
                        Plugin.Log.LogInfo($"IFZCOOP citizen remove {kv.Key}");
                        _session.Units.Forget(kv.Value);
                        kv.Value.Kill(Gameplay.Units.SubtractionHpReason.None);
                        Removed++;
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"Bürger {kv.Key} entfernen: {e.Message}"); }
                }

                foreach (var kv in host)
                {
                    infos.TryGetValue(kv.Key, out var info);
                    if (!mine.TryGetValue(kv.Key, out var c))
                    {
                        if (info == null)
                            continue; // kommt beim nächsten vollen Stand
                        try { Create(cc, wc, kv.Value, info); }
                        catch (Exception e) { Plugin.Log.LogWarning($"Bürger {kv.Key} anlegen: {e}"); }
                        continue;
                    }
                    bool hostSick = kv.Value == Kind.Sick;
                    if (hostSick && !c.IsSick && info?.Sickness != null)
                    {
                        c.GetSick(info.Sickness);
                        SickChanged++;
                    }
                    else if (!hostSick && c.IsSick)
                    {
                        c.CureSickness();
                        SickChanged++;
                    }
                }
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        /// <summary>Doppelte Figur ohne Tod (kein Grab, keine Stimmung) aus Listen und Welt nehmen.</summary>
        private void RemoveSilently(CitizensController cc, WorkersController wc, Character c)
        {
            try
            {
                Plugin.Log.LogInfo($"IFZCOOP citizen duplicate {c.Id} {c.Name}");
                _session.Units.Forget(c);
                wc.RemoveWorker(c);
                cc.RemoveCitizen(c);
                UnityEngine.Object.Destroy(c.gameObject);
                Removed++;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Doppelte Figur {c.Id}: {e.Message}"); }
        }

        private void Create(CitizensController cc, WorkersController wc, Kind kind, CharacterSerializedInfo info)
        {
            if (kind == Kind.Worker)
            {
                var c = wc.CreateWorker(info.CharacterInfo);
                c.Deserialize(info);
                if (info.Position != Vector3.zero)
                    c.Movement.Teleport(info.Position);
            }
            else
            {
                cc.LoadCitizen(info);
            }
            Created++;
            Plugin.Log.LogInfo($"IFZCOOP citizen create {info.CharacterInfo.CharacterId} {kind} {info.CharacterInfo.Name}");
        }

        /// <summary>Für die Statusdatei.</summary>
        public static string StatusLine()
        {
            var all = Civilians().OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
            uint hash = 2166136261;
            foreach (var c in all)
                foreach (char ch in c.Id + (int)KindOf(c))
                    hash = (hash ^ ch) * 16777619;
            var cc = GameAccess.Game<CitizensController>();
            return $"population civilians={all.Count} workers={all.Count(c => KindOf(c) == Kind.Worker)} children={all.Count(c => KindOf(c) == Kind.Child)} sick={all.Count(c => KindOf(c) == Kind.Sick)} citizensCount={CitizensController.CitizensCount} hash={hash:x8}";
        }
    }
}
