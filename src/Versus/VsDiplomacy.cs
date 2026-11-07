using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Controllers;
using IfzCoop.Game;
using IfzCoop.Net;
using Steamworks;
using UnityEngine;

namespace IfzCoop.Versus
{
    /// <summary>
    /// Bündnisse im Versus (KONZEPT-VERSUS Abschnitt 11, Wunsch 4/5).
    ///
    /// - Der Host führt die Bündnis-Tabelle (Paare von Plätzen) und schickt sie bei jeder Änderung an alle (VsRelations).
    ///   Am Anfang nach Spielart: „Alle verbündet“ = alle, „Teams“ = gleiches Team, „Jeder gegen jeden“ = keiner.
    /// - Anfrage: Klick auf den Namen im Pausenmenü → „Verbünden“ (VsAllyRequest an den Host) → der Host fragt den anderen
    ///   (VsAllyAsk) → der antwortet Annehmen/Ablehnen (VsAllyAnswer). „Bündnis beenden“ geht sofort, ohne Rückfrage.
    /// - Geplünderte Häuser: Jeder meldet seine fertig durchsuchten Häuser (VsScavenged), der Host verteilt sie an alle.
    ///   Man übernimmt sie, wenn „Durchsuchte Häuser sichtbar“ an ist oder der Plünderer verbündet ist (auch nachträglich,
    ///   sobald das Bündnis steht).
    /// - Noch offen (braucht „fremde Trupps sehen“ und Kampf): Trupps der Verbündeten sehen, sich nicht angreifen,
    ///   Gebäude der Verbündeten, in fremde Lager liefern.
    /// </summary>
    internal sealed class VsDiplomacy
    {
        private const float ScanInterval = 2f;
        private const int MaxIds = 2000;

        private readonly VersusSession _vs;
        private readonly HashSet<int> _allied = new HashSet<int>();

        // Host: offene Anfragen (Paar → wer gefragt hat)
        private readonly Dictionary<int, byte> _hostPending = new Dictionary<int, byte>();
        // Host: geplünderte Häuser je Platz (für Nachzügler)
        private readonly Dictionary<byte, HashSet<long>> _hostScavenged = new Dictionary<byte, HashSet<long>>();

        // jeder Spieler: Anfragen an mich, eigene gesendete Anfragen
        public readonly List<byte> IncomingAsks = new List<byte>();
        private readonly HashSet<byte> _sentTo = new HashSet<byte>();

        // jeder Spieler: Häuser
        private readonly HashSet<long> _mineReported = new HashSet<long>();
        private readonly Dictionary<long, byte> _remote = new Dictionary<long, byte>();
        private readonly HashSet<long> _remoteApplied = new HashSet<long>();
        private float _nextScan;

        public int HousesShared => _remoteApplied.Count;
        public int HousesReported => _mineReported.Count;

        public VsDiplomacy(VersusSession vs)
        {
            _vs = vs;
        }

        public void Reset()
        {
            _allied.Clear();
            _hostPending.Clear();
            _hostScavenged.Clear();
            IncomingAsks.Clear();
            _sentTo.Clear();
            _mineReported.Clear();
            _remote.Clear();
            _remoteApplied.Clear();
            _nextScan = 0f;
        }

        private static int Key(int a, int b) => a < b ? a * 16 + b : b * 16 + a;

        public bool IsAllied(int a, int b) => a == b || _allied.Contains(Key(a, b));

        public bool IsAlliedWithMe(int slot) => IsAllied(_vs.MySlot, slot);

        public bool AskSentTo(int slot) => _sentTo.Contains((byte)slot);

        public string RelationLabel(int slot)
        {
            if (slot == _vs.MySlot) return "Du";
            if (IsAlliedWithMe(slot)) return "verbündet";
            if (IncomingAsks.Contains((byte)slot)) return "will sich verbünden";
            if (AskSentTo(slot)) return "Anfrage gesendet";
            return "kein Bündnis";
        }

        // ================================================================ Host: Tabelle

        /// <summary>Host beim Start: Bündnisse nach Spielart.</summary>
        public void HostInit()
        {
            _allied.Clear();
            _hostPending.Clear();
            var players = _vs.Roster.Players;
            for (int i = 0; i < players.Count; i++)
                for (int j = i + 1; j < players.Count; j++)
                {
                    bool ally = _vs.Settings.Mode == VsMode.AllAllied
                        || (_vs.Settings.Mode == VsMode.Teams && players[i].Team == players[j].Team);
                    if (ally) _allied.Add(Key(players[i].Slot, players[j].Slot));
                }
            HostBroadcastRelations();
        }

        private byte[] BuildRelations()
        {
            var pairs = new List<int>(_allied);
            return VsNet.Build(Msg.VsRelations, w =>
            {
                w.Write((byte)pairs.Count);
                foreach (int k in pairs)
                {
                    w.Write((byte)(k / 16));
                    w.Write((byte)(k % 16));
                }
            });
        }

        private void HostBroadcastRelations()
        {
            _vs.BroadcastWelcomed(BuildRelations());
            OnRelationsChanged();
        }

        /// <summary>Host: ein Gast hat den Spielstand geladen → Bündnisse und alle geplünderten Häuser nachreichen.</summary>
        public void HostOnPeerLoaded(VsPeer peer)
        {
            _vs.SendTo(peer.Id, BuildRelations());
            foreach (var kv in _hostScavenged)
                if (kv.Key != peer.Slot && kv.Value.Count > 0)
                    foreach (var msg in BuildScavenged(kv.Key, new List<long>(kv.Value)))
                        _vs.SendTo(peer.Id, msg);
        }

        // ================================================================ Anfragen

        /// <summary>Klick „Verbünden“ (kind 0) oder „Bündnis beenden“ (kind 1) für den Spieler auf Platz target.</summary>
        public void Request(byte target, byte kind)
        {
            if (target == _vs.MySlot)
                return;
            if (kind == 0) _sentTo.Add(target);
            if (_vs.Role == VsRole.Host)
                HostOnRequest(_vs.MySlot, target, kind);
            else
                _vs.SendToHost(VsNet.Build(Msg.VsAllyRequest, w => { w.Write(target); w.Write(kind); }));
        }

        /// <summary>Antwort auf eine Anfrage von Platz from.</summary>
        public void Answer(byte from, bool yes)
        {
            IncomingAsks.Remove(from);
            if (_vs.Role == VsRole.Host)
                HostOnAnswer(_vs.MySlot, from, yes);
            else
                _vs.SendToHost(VsNet.Build(Msg.VsAllyAnswer, w => { w.Write(from); w.Write(yes); }));
        }

        public void HostOnRequest(byte from, byte target, byte kind)
        {
            var a = _vs.Roster.BySlot(from);
            var b = _vs.Roster.BySlot(target);
            if (a == null || b == null || from == target)
                return;
            int k = Key(from, target);
            if (kind == 1)
            {
                _hostPending.Remove(k);
                if (_allied.Remove(k))
                {
                    HostBroadcastRelations();
                    TellSlot(from, "Bündnis mit " + b.Name + " beendet");
                    TellSlot(target, a.Name + " hat das Bündnis mit dir beendet");
                }
                return;
            }
            if (_allied.Contains(k))
            {
                TellSlot(from, "Ihr seid schon verbündet");
                return;
            }
            // Fragen sich beide gegenseitig, gilt das als Ja
            if (_hostPending.TryGetValue(k, out var asker) && asker == target)
            {
                HostOnAnswer(from, target, true);
                return;
            }
            _hostPending[k] = from;
            if (target == _vs.MySlot)
                LocalOnAsk(from);
            else
            {
                var peer = _vs.PeerBySlot(target);
                if (peer != null) _vs.SendTo(peer.Id, VsNet.Build(Msg.VsAllyAsk, w => w.Write(from)));
            }
            TellSlot(from, "Bündnis-Anfrage an " + b.Name + " geschickt");
        }

        public void HostOnAnswer(byte answerer, byte asker, bool yes)
        {
            int k = Key(answerer, asker);
            if (!_hostPending.TryGetValue(k, out var who) || who != asker)
                return;
            _hostPending.Remove(k);
            var a = _vs.Roster.BySlot(asker);
            var b = _vs.Roster.BySlot(answerer);
            if (a == null || b == null)
                return;
            if (yes)
            {
                _allied.Add(k);
                HostBroadcastRelations();
                TellSlot(asker, b.Name + " hat das Bündnis angenommen");
                TellSlot(answerer, "Du bist jetzt mit " + a.Name + " verbündet");
            }
            else
            {
                TellSlot(asker, b.Name + " hat das Bündnis abgelehnt");
                if (asker == _vs.MySlot) _sentTo.Remove(answerer);
                else
                {
                    var peer = _vs.PeerBySlot(asker);
                    if (peer != null) _vs.SendTo(peer.Id, VsNet.Build(Msg.VsAllyAnswer, w => { w.Write(answerer); w.Write(false); }));
                }
            }
        }

        private void TellSlot(byte slot, string text)
        {
            if (slot == _vs.MySlot)
            {
                _vs.Notice(text, 6f);
                return;
            }
            var peer = _vs.PeerBySlot(slot);
            if (peer != null)
                _vs.SendTo(peer.Id, VsNet.Build(Msg.Notice, w => w.Write(text)));
        }

        // ================================================================ Gast/jeder: Empfang

        public void LocalOnAsk(byte from)
        {
            if (!IncomingAsks.Contains(from))
                IncomingAsks.Add(from);
            var p = _vs.Roster.BySlot(from);
            _vs.Notice((p?.Name ?? "Ein Spieler") + " möchte sich mit dir verbünden (Fenster oben)", 8f);
        }

        /// <summary>Gast: Antwort „abgelehnt“ zu meiner Anfrage (nur für die Anzeige).</summary>
        public void GuestOnAnswer(BinaryReader r)
        {
            byte other = r.ReadByte();
            r.ReadBoolean();
            _sentTo.Remove(other);
        }

        public void GuestOnRelations(BinaryReader r)
        {
            _allied.Clear();
            int n = r.ReadByte();
            for (int i = 0; i < n; i++)
            {
                int a = r.ReadByte(), b = r.ReadByte();
                _allied.Add(Key(a, b));
            }
            OnRelationsChanged();
        }

        private void OnRelationsChanged()
        {
            foreach (var s in new List<byte>(_sentTo))
                if (IsAlliedWithMe(s)) _sentTo.Remove(s);
            IncomingAsks.RemoveAll(s => IsAlliedWithMe(s));
            ApplyRemoteHouses();
        }

        // ================================================================ Geplünderte Häuser

        public void Update(float now)
        {
            if (now < _nextScan || !GameAccess.IsGameStarted || _vs.Phase != VsPhase.Running || !_vs.Start.MyHqConfirmed)
                return;
            _nextScan = now + ScanInterval;
            var buildings = GameAccess.Game<BuildingsController>()?.Buildings;
            if (buildings == null)
                return;
            List<long> fresh = null;
            for (int i = 0; i < buildings.Count; i++)
            {
                var b = buildings[i];
                var work = b != null ? b.ScavengeWork : null;
                if (work == null || !work.IsDone || _remote.ContainsKey(b.ID) || _mineReported.Contains(b.ID))
                    continue;
                // eigenes HQ nie melden: sonst stünde bei den anderen genau dort ein „geplündertes“ Haus (HQ-Wahl ist geheim)
                if (b.IsHq)
                    continue;
                _mineReported.Add(b.ID);
                (fresh ?? (fresh = new List<long>())).Add(b.ID);
            }
            if (fresh == null)
                return;
            if (_vs.Role == VsRole.Host)
                HostOnScavenged(_vs.MySlot, fresh);
            else
                foreach (var msg in BuildScavenged(_vs.MySlot, fresh))
                    _vs.SendToHost(msg);
        }

        private static IEnumerable<byte[]> BuildScavenged(byte slot, List<long> ids)
        {
            for (int start = 0; start < ids.Count; start += MaxIds)
            {
                int count = Math.Min(MaxIds, ids.Count - start);
                int first = start;
                yield return VsNet.Build(Msg.VsScavenged, w =>
                {
                    w.Write(slot);
                    w.Write(count);
                    for (int i = first; i < first + count; i++) w.Write(ids[i]);
                });
            }
        }

        private static List<long> ReadIds(BinaryReader r, out byte slot)
        {
            slot = r.ReadByte();
            int n = r.ReadInt32();
            if (n < 0 || n > MaxIds)
                throw new InvalidDataException("zu viele Häuser");
            var ids = new List<long>(n);
            for (int i = 0; i < n; i++) ids.Add(r.ReadInt64());
            return ids;
        }

        /// <summary>Host: Meldung eines Gastes (Platz kommt vom Absender, nicht aus der Nachricht).</summary>
        public void HostOnScavengedMsg(VsPeer peer, BinaryReader r)
        {
            var ids = ReadIds(r, out _);
            HostOnScavenged(peer.Slot, ids);
        }

        private void HostOnScavenged(byte slot, List<long> ids)
        {
            if (!_hostScavenged.TryGetValue(slot, out var set))
                _hostScavenged[slot] = set = new HashSet<long>();
            foreach (var id in ids) set.Add(id);
            foreach (var peer in _vs.Peers)
                if (peer.Welcomed && peer.Loaded && peer.Slot != slot)
                    foreach (var msg in BuildScavenged(slot, ids))
                        _vs.SendTo(peer.Id, msg);
            if (slot != _vs.MySlot)
                LocalOnScavenged(slot, ids);
        }

        public void GuestOnScavenged(BinaryReader r)
        {
            var ids = ReadIds(r, out var slot);
            if (slot != _vs.MySlot)
                LocalOnScavenged(slot, ids);
        }

        private void LocalOnScavenged(byte slot, List<long> ids)
        {
            foreach (var id in ids)
                if (!_mineReported.Contains(id) && !_remote.ContainsKey(id))
                    _remote[id] = slot;
            ApplyRemoteHouses();
        }

        /// <summary>Häuser anderer als durchsucht übernehmen, wenn „sichtbar“ an ist oder der Plünderer verbündet ist.</summary>
        private void ApplyRemoteHouses()
        {
            if (_remote.Count == _remoteApplied.Count || !GameAccess.IsGameStarted)
                return;
            var controller = GameAccess.Game<BuildingsController>();
            if (controller == null)
                return;
            bool all = _vs.Settings.ScavengedVisible;
            foreach (var kv in _remote)
            {
                if (_remoteApplied.Contains(kv.Key) || (!all && !IsAlliedWithMe(kv.Value)))
                    continue;
                _remoteApplied.Add(kv.Key);
                try
                {
                    var b = controller.GetBuildingById(kv.Key);
                    if (b == null || b.IsHq)
                        continue;
                    if (b.ScavengeWork == null)
                        b.InitializeScavengeWork(null);
                    var work = b.ScavengeWork;
                    if (work != null && !work.IsDone)
                        work.EndImminently(clearResources: true);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogDebug("IFZVS Haus " + kv.Key + " als geplündert setzen: " + e.Message);
                }
            }
        }

        public string Compact()
        {
            var sb = new StringBuilder();
            foreach (int k in _allied)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(k / 16).Append('+').Append(k % 16);
            }
            return $"allies={(sb.Length == 0 ? "-" : sb.ToString())} asks={IncomingAsks.Count} sent={_sentTo.Count} housesMine={_mineReported.Count} housesRemote={_remote.Count} housesApplied={_remoteApplied.Count}";
        }
    }
}
