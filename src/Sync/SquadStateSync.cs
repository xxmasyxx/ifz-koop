using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Controllers;
using Gameplay.Buildings;
using Gameplay.Units;
using IfzCoop.Game;
using IfzCoop.Net;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Trupp-Zustand jenseits der Position (Analyse W15; Paket K): Name, Haltung (Feuer frei/halten, Stellung halten/frei bewegen)
    /// und ob der Trupp in einem Haus ist (beim Gast verschwinden die Figuren dann wie beim Host im Haus).
    ///
    /// Host: 1×/s je Spieler-Trupp eine Unterschrift; bei Änderung (und alle 10 s) Name, Haltungen, Haus-ID.
    /// Gast: übernimmt das. Ändert der Gast Name oder Haltung, gilt es sofort bei ihm und geht als Wunsch an den Host.
    /// </summary>
    internal sealed class SquadStateSync
    {
        private const float SendInterval = 1f;
        private const float ForceInterval = 10f;

        private readonly Session _session;
        private readonly Dictionary<string, string> _sent = new Dictionary<string, string>();
        private float _nextSend;
        private float _nextForce;

        public int Applied { get; private set; }
        public int EnteredApplied { get; private set; }
        public int RequestsSent { get; private set; }

        public SquadStateSync(Session session)
        {
            _session = session;
        }

        public void ResendAll() => _sent.Clear();

        public void Reset() => ResendAll();

        private static IEnumerable<Group> Squads()
        {
            var squads = GameAccess.Game<SquadsController>()?.Squads;
            if (squads == null)
                yield break;
            foreach (var g in squads)
                if (g != null && !string.IsNullOrEmpty(g.Id) && !g.IsEmpty())
                    yield return g;
        }

        private static long BuildingOf(Group g) => g.Enterable is Building b ? b.ID : 0L;

        private static List<KeyValuePair<int, bool>> StancesOf(Group g)
        {
            var list = new List<KeyValuePair<int, bool>>();
            var st = g.StancesHandler;
            if (st == null)
                return list;
            foreach (var kv in st.Stances.OrderBy(k => (int)k.Key))
                list.Add(new KeyValuePair<int, bool>((int)kv.Key, kv.Value.IsActive));
            return list;
        }

        private static string Sig(Group g)
        {
            var sb = new StringBuilder();
            sb.Append(g.GroupName).Append('|').Append(BuildingOf(g)).Append('|');
            foreach (var s in StancesOf(g)) sb.Append(s.Key).Append(s.Value ? '+' : '-');
            return sb.ToString();
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

            var changed = new List<Group>();
            foreach (var g in Squads())
            {
                string sig = Sig(g);
                if (force || !_sent.TryGetValue(g.Id, out var last) || last != sig)
                {
                    _sent[g.Id] = sig;
                    changed.Add(g);
                }
            }
            if (changed.Count == 0)
                return;
            _session.BroadcastToGuests(Protocol.Build(Msg.SquadState, w =>
            {
                w.Write(changed.Count);
                foreach (var g in changed)
                {
                    w.Write(g.Id);
                    w.Write(g.GroupName ?? "");
                    w.Write(BuildingOf(g));
                    var st = StancesOf(g);
                    w.Write(st.Count);
                    foreach (var s in st)
                    {
                        w.Write(s.Key);
                        w.Write(s.Value);
                    }
                }
            }));
        }

        public void HostOnRequest(Peer from, BinaryReader r)
        {
            string id = r.ReadString();
            byte what = r.ReadByte();
            var g = Squads().FirstOrDefault(x => x.Id == id);
            if (g == null)
                return;
            if (what == 0)
            {
                string name = r.ReadString();
                g.SetGroupName(name);
                Plugin.Log.LogInfo($"IFZCOOP squad {id} name '{name}' (von {from.Name})");
            }
            else
            {
                var stance = (GroupStanceId)r.ReadInt32();
                bool active = r.ReadBoolean();
                g.StancesHandler?.SetStateToStance(stance, active);
                Plugin.Log.LogInfo($"IFZCOOP squad {id} stance {stance}={active} (von {from.Name})");
            }
        }

        // ================================================================ Gast

        private bool GuestCanAsk => _session.Role == Role.Guest && _session.GuestControlsLocked && !UnitSync.NetApplying;

        /// <summary>Postfix Group.SetGroupName beim Gast.</summary>
        public void GuestRenamed(Group g, string name)
        {
            if (!GuestCanAsk || g == null || g.Fraction != Fraction.Player)
                return;
            _session.SendToHost(Protocol.Build(Msg.SquadStateRequest, w =>
            {
                w.Write(g.Id);
                w.Write((byte)0);
                w.Write(name ?? "");
            }));
            RequestsSent++;
        }

        /// <summary>Postfix StancesHandler.SetStateToStance beim Gast.</summary>
        public void GuestStance(StancesHandler handler, GroupStanceId stance, bool active)
        {
            if (!GuestCanAsk || handler == null)
                return;
            var g = Squads().FirstOrDefault(x => ReferenceEquals(x.StancesHandler, handler));
            if (g == null)
                return;
            _session.SendToHost(Protocol.Build(Msg.SquadStateRequest, w =>
            {
                w.Write(g.Id);
                w.Write((byte)1);
                w.Write((int)stance);
                w.Write(active);
            }));
            RequestsSent++;
        }

        public void GuestOnState(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 1000)
                throw new InvalidDataException("zu viele Trupps");
            var bc = GameAccess.Game<BuildingsController>();
            UnitSync.NetApplying = true;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    string id = r.ReadString();
                    string name = r.ReadString();
                    long building = r.ReadInt64();
                    int k = r.ReadInt32();
                    if (k < 0 || k > 64)
                        throw new InvalidDataException("zu viele Haltungen");
                    var stances = new List<KeyValuePair<int, bool>>();
                    for (int j = 0; j < k; j++) stances.Add(new KeyValuePair<int, bool>(r.ReadInt32(), r.ReadBoolean()));

                    var g = Squads().FirstOrDefault(x => x.Id == id);
                    if (g == null)
                        continue;
                    try
                    {
                        if (!string.IsNullOrEmpty(name) && g.GroupName != name)
                            g.SetGroupName(name);
                        foreach (var s in stances)
                            if (g.StancesHandler != null && g.StancesHandler.IsActive((GroupStanceId)s.Key) != s.Value)
                                g.StancesHandler.SetStateToStance((GroupStanceId)s.Key, s.Value);
                        ApplyBuilding(g, building == 0 ? null : bc?.GetBuildingById(building));
                        Applied++;
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"Trupp-Zustand {id}: {e.Message}"); }
                }
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        private void ApplyBuilding(Group g, Building b)
        {
            bool changed = false;
            foreach (var c in g.Characters)
            {
                if (c == null || c.IsDead())
                    continue;
                var inside = c.Enterable as Building;
                if (b != null && inside != b)
                {
                    if (inside != null) c.ExitEnterableImmediately();
                    c.EnteredEnterableImmediately(b);
                    changed = true;
                }
                else if (b == null && inside != null)
                {
                    c.ExitEnterableImmediately();
                    changed = true;
                }
            }
            if (changed)
            {
                g.Pointer?.SetIndoorsState(isIndoors: b != null);
                EnteredApplied++;
                Plugin.Log.LogInfo($"IFZCOOP squad {g.Id} {(b != null ? "in Haus " + b.ID : "draußen")}");
            }
        }

        /// <summary>Für die Statusdatei.</summary>
        public static IEnumerable<string> StatusLines()
        {
            foreach (var g in Squads().OrderBy(x => x.Id, StringComparer.Ordinal))
                yield return $"squadstate {g.Id} name={g.GroupName?.Replace(' ', '_')} house={BuildingOf(g)} stances={string.Join(",", StancesOf(g).Select(s => (GroupStanceId)s.Key + (s.Value ? "+" : "-")))}";
        }
    }
}
