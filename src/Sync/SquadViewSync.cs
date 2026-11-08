using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Gameplay.Units;
using Gameplay.Units.Enemy;
using Gameplay.Units.Icons;
using Gameplay.Units.Movements;
using Gameplay.Units.Movements.Paths;
using Gameplay.Units.Orders;
using Gameplay.Units.Player;
using Gameplay.Units.States;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using Navigation.Paths;
using UnityEngine;
using Pointer = Gameplay.Units.Icons.Pointer;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Trupp-Anzeige wie beim Host: Weg-Striche, Befehlskette, Trupp-Zustand und wer einen Trupp steuert.
    ///
    /// Ursachen der Fehler beim Gast: Group.CustomUpdate läuft dort nur als Marionetten-Takt (UnitSync.PuppetTick).
    /// Damit laufen weder GroupMovement.DrawPath (Weg-Striche: einmal gezeichnet, nie mehr bewegt oder gelöscht)
    /// noch PlayerGroupStatusHandler.CustomUpdate (Zustand bleibt auf dem Wert aus den ersten Bildern nach dem Laden,
    /// meist „Unterwegs“) noch OrdersQueue.ExecuteOrder (die Befehlskette des Gastes bleibt stehen).
    ///
    /// Host: 5×/s je Trupp (und Gruppe mit Weg-Anzeige) eine Unterschrift aus Zustand, Steuerndem, Restweg
    ///       (Movement.RemainingPath), Linienart und Befehlskette (OrdersQueue.GetQueuePath); bei Änderung senden.
    ///       Wer einen Trupp steuert, bestimmt der Host: nur solange ein Spieler ihn ausgewählt hat (eigene Auswahl
    ///       und Auswahl der Gäste aus PresenceSync); abgewählt ist er wieder frei. Dazu merkt der Host je Trupp, wer
    ///       zuletzt wirklich etwas geändert hat (Befehl über OrderSync → NoteOrder, sonstige Änderungen über die Oberfläche
    ///       → NoteUiChange, beim Gast als Msg 82 Op 1 an den Host) und wer zuletzt einen Befehl gab (mit Zeit).
    /// Gast: setzt den Zustand (PlayerGroupStatusHandler.SetStatus, löst Symbol, Trupp-Fenster und Trupp-Liste aus),
    ///       zeichnet jeden Frame die Weg-Striche zur Marionette (PathDrawer.UpdatePath / DestroyLine) und die
    ///       Befehlskette (PathDrawer.UpdateNextPaths) aus den Host-Daten. Die eigene, stehende Kette des Gastes
    ///       wird nicht gezeichnet (Haken in UnitPatches).
    /// Beide: Befehlskette auch dann zeigen, wenn der Mitspieler den Trupp ausgewählt hat (so sieht man seine Planung);
    ///       Trupps, die ein anderer Spieler gerade ausgewählt hat, erscheinen in dessen Spielerfarbe (PlayerColorSync);
    ///       frei sind sie grün; ein Punkt zeigt, wer zuletzt wirklich etwas geändert hat (Schild und Trupp-Liste),
    ///       im Trupp-Fenster „Zuletzt gesteuert: Name“.
    /// </summary>
    internal sealed class SquadViewSync
    {
        private const float TickInterval = 0.1f;
        private const float SendInterval = 0.2f;
        private const float ForceInterval = 5f;
        private const float RemoteStale = 5f;
        private const float LabelScanInterval = 2f;
        private const int MaxPoints = 256;
        private const int MaxRecords = 200;


        [Flags]
        private enum F : byte
        {
            None = 0,
            Removed = 1,
            Squad = 2,
            Moving = 4,
            Expedition = 8,
            StartsAtGroup = 16,
        }

        private sealed class View
        {
            public string Id;
            public F Flags;
            public PlayerGroupStatus Status;
            public ulong Ctrl;
            public LineType Line;
            public readonly List<Vector3> Path = new List<Vector3>();
            public readonly List<OrderLine> Queue = new List<OrderLine>();
            // Gast
            public Group Group;
            public object ScavKey;
            public bool WasScavenging;
            // letzter Befehl (Host-Liste, mitgeschickt)
            public ulong OrderBy;
            public ulong LastBy;     // zuletzt gesteuert (ausgewählt oder Befehl)
            public int OrderSeq;
            public int OrderDay;
            public float OrderHour;
        }

        private sealed class LastOrder
        {
            public ulong By;
            public int Seq;
            public int Day;
            public float Hour;
            public float RealTime;   // Echtzeit auf diesem Rechner, als der Befehl bekannt wurde
        }

        private sealed class Chain
        {
            public bool Mine;
            public string Sig;
        }

        // ------------------------------------------------------------- Spiel-Zugriffe (privat im Spiel)

        private static readonly MethodInfo GetQueuePathMethod = AccessTools.Method(typeof(OrdersQueue), "GetQueuePath");
        private static readonly AccessTools.FieldRef<OrdersQueue, bool> NotStartedField =
            AccessTools.FieldRefAccess<OrdersQueue, bool>("_orderNotStarted");
        private static readonly AccessTools.FieldRef<PathDrawer, List<PathLine>> NextLinesField =
            AccessTools.FieldRefAccess<PathDrawer, List<PathLine>>("_nextPathLines");
        private static readonly MethodInfo SetStatusMethod = AccessTools.Method(typeof(PlayerGroupStatusHandler), "SetStatus");
        private static readonly FieldInfo StatusChangeField = AccessTools.Field(typeof(PlayerGroupStatusHandler), "OnStatusChange");
        private static readonly AccessTools.FieldRef<Pointer, SpriteRenderer> IconField =
            AccessTools.FieldRefAccess<Pointer, SpriteRenderer>("_icon");
        private static readonly Type LabelType = AccessTools.TypeByName("UI.SquadLabel");
        private static readonly MethodInfo LabelSetColorMethod = LabelType != null ? AccessTools.Method(LabelType, "SetColor") : null;

        /// <summary>true, solange dieses Modul selbst die Befehlskette zeichnet (der Haken lässt das durch).</summary>
        public static bool Drawing;

        /// <summary>Gast: vom Haken verworfene Zeichnungen der stehenden eigenen Befehlskette.</summary>
        public static int Blocked;

        private readonly Session _session;
        private float _nextTick;
        private float _nextSend;
        private float _nextForce;
        private float _nextLabelScan;

        // Host
        private readonly Dictionary<string, string> _sent = new Dictionary<string, string>();
        private readonly Dictionary<string, ulong> _ctrl = new Dictionary<string, ulong>();
        private HashSet<string> _prevMine = new HashSet<string>();
        private readonly Dictionary<ulong, HashSet<string>> _prevRemote = new Dictionary<ulong, HashSet<string>>();
        private int _orderSeq;

        // Beide: letzter Befehl je Trupp (Host rechnet, Gast übernimmt)
        private readonly Dictionary<string, LastOrder> _lastOrders = new Dictionary<string, LastOrder>();

        // Gast
        private readonly Dictionary<string, View> _views = new Dictionary<string, View>();

        // Beide
        private readonly Dictionary<Group, Chain> _chains = new Dictionary<Group, Chain>();
        private readonly Dictionary<Group, Color> _tinted = new Dictionary<Group, Color>();
        private readonly Dictionary<Component, Color> _tintedLabels = new Dictionary<Component, Color>();
        private readonly Dictionary<Pointer, Group> _dotPointers = new Dictionary<Pointer, Group>();
        private readonly HashSet<Component> _dotLabels = new HashSet<Component>();
        // Host: wer den Trupp zuletzt gesteuert hat (ausgewählt oder Befehl gegeben). Statisch und ohne Leeren bei Reset:
        // gehört zum Spiel, nicht zur Sitzung – bleibt beim Trennen stehen und kommt über SaveExtras in den Spielstand
        // (Bericht 2026-11-08: nach dem Laden waren die Punkte weg). Geleert nur beim Start eines (anderen) Spiels.
        private static readonly Dictionary<string, ulong> _last = new Dictionary<string, ulong>();

        /// <summary>Für den Spielstand (SaveExtras): Trupp-ID → Steam-ID dessen, der zuletzt etwas geändert hat.</summary>
        public static Dictionary<string, ulong> ExportLast() => new Dictionary<string, ulong>(_last);

        /// <summary>Aus dem Spielstand (SaveExtras) oder leer beim Start eines Spiels.</summary>
        public static void ImportLast(Dictionary<string, ulong> from)
        {
            _last.Clear();
            if (from != null)
                foreach (var kv in from)
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Value != 0UL) _last[kv.Key] = kv.Value;
        }

        public static int LastCount => _last.Count;
        private readonly List<Component> _labels = new List<Component>();
        private readonly List<OrderLine> _lineBuffer = new List<OrderLine>();

        public int Sent { get; private set; }
        public int Received { get; private set; }
        public int StatusSet { get; private set; }
        public int ChainDraws { get; private set; }
        public int CtrlChanges { get; private set; }
        public int ColorChanges { get; private set; }
        public int StopsSeen { get; private set; }

        public SquadViewSync(Session session)
        {
            _session = session;
        }

        private bool HostActive => _session.Role == Role.Host && _session.ActiveGuestCount > 0 && GameAccess.IsGameStarted;

        private bool GuestActive => _session.GuestControlsLocked && GameAccess.IsGameStarted;

        public void ResendAll()
        {
            _sent.Clear();
            _nextSend = 0f;
        }

        public void Reset()
        {
            try { RestoreAll(); } catch (Exception e) { Plugin.Log.LogDebug("Trupp-Anzeige zurücksetzen: " + e.Message); }
            _sent.Clear();
            _ctrl.Clear();
            _prevMine.Clear();
            _prevRemote.Clear();
            _lastOrders.Clear();
            _views.Clear();
            _chains.Clear();
            _tinted.Clear();
            _tintedLabels.Clear();
            _dotPointers.Clear();
            _dotLabels.Clear();
            _infoPanel = null;
            _labels.Clear();
            Drawing = false;
        }

        // ================================================================ Host

        /// <summary>Jeden Frame beim Host (eigener Takt innen).</summary>
        public void HostUpdate()
        {
            if (!HostActive)
            {
                if (_tinted.Count > 0 || _dotPointers.Count > 0 || _chains.Count > 0 || _ctrl.Count > 0)
                    Reset();
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (now < _nextTick)
                return;
            _nextTick = now + TickInterval;

            UpdateControllers();
            var squads = Squads();
            foreach (var g in squads)
            {
                try
                {
                    var queue = g.OrdersHandlers != null ? g.OrdersHandlers.OrdersQueue : null;
                    UpdateChain(g, host: true, queue != null ? HostQueueLines(g, queue, out _) : null);
                }
                catch (Exception e) { Plugin.Log.LogDebug("Befehlskette zeigen: " + e.Message); }
            }
            CleanupChains(squads);
            UpdateColors(squads, id => _ctrl.TryGetValue(id, out var c) ? c : 0UL, id => _last.TryGetValue(id, out var l) ? l : 0UL, now);

            if (now >= _nextSend)
            {
                _nextSend = now + SendInterval;
                if (now >= _nextForce)
                {
                    _nextForce = now + ForceInterval;
                    _sent.Clear();
                }
                SendChanged(squads);
            }
        }

        /// <summary>
        /// „Steuert gerade“ gilt nur, solange ein Spieler den Trupp ausgewählt hat (eigene Auswahl des Hosts,
        /// Auswahl der Gäste aus PresenceSync, 4×/s). Abwählen jeder Art (Klick ins Leere, anderer Trupp, Escape)
        /// leert die Auswahl, ein veralteter oder getrennter Mitspieler zählt nicht mehr: der Trupp ist wieder frei.
        /// Haben mehrere ihn ausgewählt, steuert ihn, wer ihn zuletzt neu ausgewählt hat.
        /// </summary>
        private void UpdateControllers()
        {
            ulong me = _session.MyId.m_SteamID;
            float now = Time.realtimeSinceStartup;
            var selectors = new Dictionary<string, List<ulong>>();
            var fresh = new Dictionary<string, ulong>();

            var mine = SelectedIds();
            foreach (var id in mine)
            {
                AddSelector(selectors, id, me);
                if (!_prevMine.Contains(id)) fresh[id] = me;
            }
            _prevMine = mine;

            var alive = new HashSet<ulong>();
            foreach (var remote in _session.Presence.Remotes)
            {
                if (now - remote.Updated > RemoteStale)
                    continue;
                alive.Add(remote.SteamId);
                if (!_prevRemote.TryGetValue(remote.SteamId, out var prev))
                    prev = new HashSet<string>();
                foreach (var id in remote.Squads)
                {
                    AddSelector(selectors, id, remote.SteamId);
                    if (!prev.Contains(id)) fresh[id] = remote.SteamId;
                }
                _prevRemote[remote.SteamId] = new HashSet<string>(remote.Squads);
            }
            // Mitspieler weg oder veraltet: beim Wiederkommen gilt seine Auswahl wieder als neu
            foreach (var id in new List<ulong>(_prevRemote.Keys))
                if (!alive.Contains(id)) _prevRemote.Remove(id);

            var next = new Dictionary<string, ulong>();
            foreach (var kv in selectors)
            {
                ulong who;
                if (fresh.TryGetValue(kv.Key, out var f))
                    who = f;
                else if (_ctrl.TryGetValue(kv.Key, out var cur) && kv.Value.Contains(cur))
                    who = cur;
                else
                    who = kv.Value[0];
                next[kv.Key] = who;
            }
            foreach (var kv in next)
                if (!_ctrl.TryGetValue(kv.Key, out var old) || old != kv.Value) CtrlChanges++;
            foreach (var kv in _ctrl)
                if (!next.ContainsKey(kv.Key)) CtrlChanges++;
            _ctrl.Clear();
            foreach (var kv in next)
                _ctrl[kv.Key] = kv.Value;
        }

        private static void AddSelector(Dictionary<string, List<ulong>> selectors, string id, ulong who)
        {
            if (string.IsNullOrEmpty(id))
                return;
            if (!selectors.TryGetValue(id, out var list))
            {
                list = new List<ulong>();
                selectors[id] = list;
            }
            if (!list.Contains(who))
                list.Add(who);
        }

        // ------------------------------------------------------------- „zuletzt gesteuert“
        // Nur echte Änderungen zählen, Auswählen/Anschauen nicht: Befehle (OrderSync → NoteOrder) und Änderungen über die
        // Oberfläche (Name, Haltung, Teilen, Mitglieder ziehen, Aus-/Einsteigen, Tanken, Sperren, Reparieren, Tauschen,
        // Wegwerfen, Munition; Haken in UnitPatches → NoteUiChange). Beim Gast geht die Meldung an den Host (Msg 82, Op 1).

        public int ActionsNoted { get; private set; }

        /// <summary>Host: Spieler „who“ hat am Trupp etwas geändert.</summary>
        public void NoteAction(string groupId, ulong who)
        {
            if (_session.Role != Role.Host || string.IsNullOrEmpty(groupId) || who == 0UL)
                return;
            _last[groupId] = who;
            ActionsNoted++;
        }

        // Aufrufer, die als Spielereingabe gelten (Oberfläche, Ziehen, Testbefehle). Was vom Spiel selbst oder aus einem
        // Wunsch eines Gastes beim Host kommt, hat keinen dieser Aufrufer im Stapel und zählt nicht.
        private static bool FromLocalUi()
        {
            var frames = new System.Diagnostics.StackTrace(1, false).GetFrames();
            if (frames == null)
                return false;
            bool ui = false;
            foreach (var frame in frames)
            {
                var method = frame.GetMethod();
                var type = method?.DeclaringType;
                while (type != null && type.IsNested && type.Name.StartsWith("<", StringComparison.Ordinal))
                    type = type.DeclaringType;
                if (type == null)
                    continue;
                string ns = type.Namespace ?? "";
                if (type.Name == "UnitPatches" || (type.Name == "SquadViewSync" && method.Name.StartsWith("Note", StringComparison.Ordinal)))
                    continue;
                if (type.Name == "QaDriver" || type.Name == "SquadViewSync")
                    return true;
                // Mod-Code im Stapel (z. B. Wunsch eines Gastes, den der Host ausführt): keine eigene Eingabe
                if (ns == "IfzCoop" || ns.StartsWith("IfzCoop.", StringComparison.Ordinal))
                    return false;
                if (ns == "UI" || ns.StartsWith("UI.", StringComparison.Ordinal) || ns.StartsWith("Controllers.Dragging", StringComparison.Ordinal)
                    || ns == "GameInput" || ns.StartsWith("UnityEngine.EventSystems", StringComparison.Ordinal) || ns.StartsWith("UnityEngine.UI", StringComparison.Ordinal))
                    ui = true;
            }
            return ui;
        }

        /// <summary>Haken (UnitPatches): an diesen Trupps hat der Spieler hier über die Oberfläche etwas geändert.</summary>
        public void NoteUiChange(params Group[] groups)
        {
            try
            {
                if (groups == null || groups.Length == 0)
                    return;
                bool host = _session.Role == Role.Host && _session.ActiveGuestCount > 0;
                bool guest = _session.GuestControlsLocked && !UnitSync.NetApplying;
                if (!host && !guest)
                    return;
                var ids = new List<string>();
                var squads = GameAccess.Game<SquadsController>()?.Squads;
                foreach (var g in groups)
                {
                    if (g == null || string.IsNullOrEmpty(g.Id) || ids.Contains(g.Id) || squads == null || !Contains(squads, g))
                        continue;
                    ids.Add(g.Id);
                }
                if (ids.Count == 0 || !FromLocalUi())
                    return;
                if (host)
                {
                    foreach (var id in ids)
                        NoteAction(id, _session.MyId.m_SteamID);
                    return;
                }
                _session.SendToHost(Protocol.Build(Msg.PlayerRequest, w =>
                {
                    w.Write((byte)1);
                    w.Write(ids.Count);
                    foreach (var id in ids) w.Write(id);
                }));
                ActionsNoted++;
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("Trupp-Änderung merken: " + e.Message);
            }
        }

        private static bool Contains(IReadOnlyList<Group> list, Group g)
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], g)) return true;
            return false;
        }

        /// <summary>Haken StancesHandler.SetStateToStance: Trupp zur Haltung suchen.</summary>
        public void NoteStanceChange(StancesHandler handler)
        {
            if (handler == null)
                return;
            foreach (var g in Squads())
                if (ReferenceEquals(g.StancesHandler, handler))
                {
                    NoteUiChange(g);
                    return;
                }
        }

        /// <summary>Haken für Aktionen ohne eigenen Trupp-Bezug (Tauschen, Wegwerfen, Munition): die offenen Trupp-Fenster.</summary>
        public void NoteOpenPanelsChange()
        {
            var list = new List<Group>();
            foreach (var p in OpenSquadPanels())
                list.Add(p.Squad);
            NoteUiChange(list.ToArray());
        }

        /// <summary>Host: Meldung eines Gastes (Msg 82, Op 1): er hat an diesen Trupps etwas geändert.</summary>
        public void HostOnChanged(Peer from, BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 64)
                throw new InvalidDataException("zu viele Trupps");
            for (int i = 0; i < n; i++)
                NoteAction(r.ReadString(), from.Id.m_SteamID);
        }

        /// <summary>
        /// Host: ein Spieler hat dem Trupp einen Befehl gegeben (OrderSync, nur Spielereingaben).
        /// </summary>
        public void NoteOrder(string groupId, ulong who)
        {
            if (_session.Role != Role.Host || string.IsNullOrEmpty(groupId))
                return;
            _orderSeq++;
            _last[groupId] = who;
            _session.Stats?.Add(who, StatsSync.Stat.Orders);
            _lastOrders[groupId] = new LastOrder
            {
                By = who,
                Seq = _orderSeq,
                Day = Controllers.Time.TimeController.Day,
                Hour = Controllers.Time.TimeController.Hour,
                RealTime = Time.realtimeSinceStartup,
            };
        }

        /// <summary>Wer hat dem Trupp zuletzt einen Befehl gegeben, vor wie vielen Sekunden (Echtzeit) und wann im Spiel.</summary>
        public bool TryGetLastOrder(string squadId, out ulong by, out float secondsAgo, out int day, out float hour)
        {
            by = 0UL;
            secondsAgo = 0f;
            day = 0;
            hour = 0f;
            if (string.IsNullOrEmpty(squadId) || !_lastOrders.TryGetValue(squadId, out var o))
                return false;
            by = o.By;
            secondsAgo = Time.realtimeSinceStartup - o.RealTime;
            day = o.Day;
            hour = o.Hour;
            return true;
        }

        /// <summary>Host: wer den Trupp zuletzt gesteuert hat (Befehl oder Änderung); 0 = unbekannt. Für StatsSync.</summary>
        public ulong LastBy(string squadId)
            => !string.IsNullOrEmpty(squadId) && _last.TryGetValue(squadId, out var who) ? who : 0UL;

        /// <summary>Wer den Trupp gerade steuert (ausgewählt hat); 0 = niemand.</summary>
        public ulong ControllerOf(string squadId)
        {
            if (string.IsNullOrEmpty(squadId))
                return 0UL;
            if (_session.Role == Role.Host)
                return _ctrl.TryGetValue(squadId, out var c) ? c : 0UL;
            return _views.TryGetValue(squadId, out var v) ? v.Ctrl : 0UL;
        }

        private void SendChanged(List<Group> squads)
        {
            var records = new List<View>();
            var present = new HashSet<string>();
            var eligible = new List<KeyValuePair<Group, bool>>();
            foreach (var g in squads)
                eligible.Add(new KeyValuePair<Group, bool>(g, true));
            var groups = GameAccess.Game<GroupsController>()?.Groups;
            if (groups != null)
                for (int i = 0; i < groups.Count; i++)
                {
                    var g = groups[i];
                    // andere Gruppen nur, wenn das Spiel ihren Weg zeigt (z. B. angenommene Einwanderer)
                    if (g != null && !string.IsNullOrEmpty(g.Id) && !g.IsEmpty() && g.PathDrawer != null && g.PathDrawer.Draw
                        && !squads.Contains(g))
                        eligible.Add(new KeyValuePair<Group, bool>(g, false));
                }

            foreach (var kv in eligible)
            {
                View v;
                try
                {
                    v = BuildHost(kv.Key, kv.Value);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogDebug($"Trupp-Anzeige {kv.Key.Id}: {e.Message}");
                    continue;
                }
                present.Add(v.Id);
                string sig = Sig(v);
                if (_sent.TryGetValue(v.Id, out var last) && last == sig)
                    continue;
                _sent[v.Id] = sig;
                records.Add(v);
            }
            foreach (var id in new List<string>(_sent.Keys))
            {
                if (present.Contains(id))
                    continue;
                _sent.Remove(id);
                records.Add(new View { Id = id, Flags = F.Removed });
            }

            for (int start = 0; start < records.Count; start += MaxRecords)
            {
                int count = Math.Min(MaxRecords, records.Count - start);
                int first = start;
                _session.BroadcastToGuests(Protocol.Build(Msg.SquadView, w =>
                {
                    w.Write(count);
                    for (int i = first; i < first + count; i++)
                        Write(w, records[i]);
                }));
                Sent += count;
            }
        }

        private View BuildHost(Group g, bool squad)
        {
            var v = new View { Id = g.Id, Flags = squad ? F.Squad : F.None };
            if (squad)
            {
                var h = g.PlayerGroupStatusHandler;
                v.Status = h != null ? h.Status : PlayerGroupStatus.None;
                v.Ctrl = _ctrl.TryGetValue(g.Id, out var c) ? c : 0UL;
                v.LastBy = _last.TryGetValue(g.Id, out var lb) ? lb : 0UL;
                if (_lastOrders.TryGetValue(g.Id, out var lo))
                {
                    v.OrderBy = lo.By;
                    v.OrderSeq = lo.Seq;
                    v.OrderDay = lo.Day;
                    v.OrderHour = lo.Hour;
                }
            }
            var drawer = g.PathDrawer;
            v.Line = drawer != null ? drawer.PathLineType : LineType.Normal;
            if (g.IsOnExpedition())
            {
                v.Flags |= F.Expedition;
            }
            else
            {
                // wie GroupMovement.DrawPath: Striche, solange das Ziel nicht erreicht ist
                var m = CurrentMovement(g);
                if (m != null && drawer != null && drawer.Draw && !m.HasReachedDestination())
                {
                    var rest = m.RemainingPath;
                    if (rest != null && rest.Count > 0)
                    {
                        v.Flags |= F.Moving;
                        for (int i = 0; i < rest.Count && i < MaxPoints; i++)
                            v.Path.Add(rest[i]);
                    }
                }
            }
            if (squad && g.OrdersHandlers != null && g.OrdersHandlers.OrdersQueue != null)
            {
                var lines = HostQueueLines(g, g.OrdersHandlers.OrdersQueue, out bool startsAtGroup);
                if (startsAtGroup)
                    v.Flags |= F.StartsAtGroup;
                for (int i = startsAtGroup ? 1 : 0; i < lines.Count && v.Queue.Count < MaxPoints; i++)
                    v.Queue.Add(lines[i]);
            }
            return v;
        }

        /// <summary>Befehlskette wie OrdersQueue.GetQueuePath (Kopie; der Spiel-Puffer wird wiederverwendet).</summary>
        private List<OrderLine> HostQueueLines(Group g, OrdersQueue queue, out bool startsAtGroup)
        {
            bool hasOrder = queue.CurrentOrder != null;
            startsAtGroup = (hasOrder && NotStartedField(queue)) || (!hasOrder && queue.GetQueueLength() > 0);
            var list = new List<OrderLine>();
            if (GetQueuePathMethod?.Invoke(queue, null) is List<OrderLine> game)
                list.AddRange(game);
            return list;
        }

        private static Movement CurrentMovement(Group g)
        {
            var vh = g.VehicleHandler;
            if (vh != null && vh.HaveVehicle() && vh.Vehicle != null)
                return vh.Vehicle.Movement;
            return g.Characters.Count > 0 && g.Characters[0] != null ? g.Characters[0].Movement : null;
        }

        // ================================================================ Gast

        public void GuestOnState(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > MaxRecords)
                throw new InvalidDataException("zu viele Trupp-Anzeigen");
            for (int i = 0; i < n; i++)
            {
                var v = Read(r);
                Received++;
                if ((v.Flags & F.Removed) != 0)
                {
                    if (_views.TryGetValue(v.Id, out var old))
                    {
                        _views.Remove(v.Id);
                        try { ForgetView(old); } catch (Exception e) { Plugin.Log.LogDebug("Trupp-Anzeige weg: " + e.Message); }
                    }
                    continue;
                }
                if (_views.TryGetValue(v.Id, out var existing))
                {
                    // Gast-Zustände behalten
                    v.Group = existing.Group;
                    v.ScavKey = existing.ScavKey;
                    v.WasScavenging = existing.WasScavenging;
                    // Zählt, wie oft der Host „läuft“ → „steht“ meldet (Ankunft oder kurzes Neuberechnen des Weges)
                    if ((existing.Flags & F.Moving) != 0 && (v.Flags & F.Moving) == 0)
                        StopsSeen++;
                }
                _views[v.Id] = v;
                if (v.OrderSeq != 0 && (!_lastOrders.TryGetValue(v.Id, out var lo) || lo.Seq != v.OrderSeq))
                    _lastOrders[v.Id] = new LastOrder { By = v.OrderBy, Seq = v.OrderSeq, Day = v.OrderDay, Hour = v.OrderHour, RealTime = Time.realtimeSinceStartup };
            }
        }

        /// <summary>Jeden Frame beim Gast: Weg-Striche zur Marionette; im Takt Zustand, Kette und Farben.</summary>
        public void GuestFrame()
        {
            if (!GuestActive)
            {
                if (_views.Count > 0 || _tinted.Count > 0 || _dotPointers.Count > 0 || _chains.Count > 0)
                    Reset();
                return;
            }
            foreach (var v in _views.Values)
            {
                var g = Resolve(v);
                if (g == null)
                    continue;
                try { DrawStripes(g, v); }
                catch (Exception e) { Plugin.Log.LogDebug("Weg-Striche: " + e.Message); }
            }

            float now = Time.realtimeSinceStartup;
            if (now < _nextTick)
                return;
            _nextTick = now + TickInterval;

            var squads = Squads();
            foreach (var g in squads)
            {
                if (!_views.TryGetValue(g.Id, out var v))
                    continue;
                try
                {
                    ApplyStatus(g, v);
                    UpdateChain(g, host: false, GuestQueueLines(g, v));
                }
                catch (Exception e) { Plugin.Log.LogDebug("Trupp-Zustand: " + e.Message); }
            }
            CleanupChains(squads);
            UpdateColors(squads, id => _views.TryGetValue(id, out var v) ? v.Ctrl : 0UL, id => _views.TryGetValue(id, out var v) ? v.LastBy : 0UL, now);
        }

        private Group Resolve(View v)
        {
            if (v.Group != null && v.Group.Id == v.Id && !v.Group.IsEmpty())
                return v.Group;
            v.Group = UnitSync.FindGroupPublic(v.Id);
            return v.Group != null && !v.Group.IsEmpty() ? v.Group : null;
        }

        /// <summary>Wie GroupMovement.DrawPath, nur mit dem Restweg des Hosts.</summary>
        private static void DrawStripes(Group g, View v)
        {
            var d = g.PathDrawer;
            if (d == null || (v.Flags & F.Expedition) != 0)
                return;
            if (!d.Draw)
            {
                if (d.HasPathLine) d.DestroyLine();
                return;
            }
            if ((v.Flags & F.Moving) != 0 && v.Path.Count > 0 && g.Characters.Count > 0)
            {
                if (d.PathLineType != v.Line)
                    d.SetLineType(v.Line);
                var m = CurrentMovement(g);
                d.UpdatePath(m != null ? m.Position : g.Position, v.Path);
            }
            else if (d.HasPathLine)
            {
                d.DestroyLine();
            }
            d.UpdateNextPaths();
        }

        private void ApplyStatus(Group g, View v)
        {
            var h = g.PlayerGroupStatusHandler;
            if (h == null || (v.Flags & F.Squad) == 0)
                return;
            if (h.Status != v.Status)
            {
                v.ScavKey = ScavKey(g);
                SetStatusMethod?.Invoke(h, new object[] { v.Status });
                StatusSet++;
            }
            else if (v.Status == PlayerGroupStatus.Scavenging)
            {
                // Haus/Plünder-Arbeit kam erst nach dem Zustand: Trupp-Fenster neu anmelden lassen (für „Plündern: x %“)
                var key = ScavKey(g);
                if (!ReferenceEquals(key, v.ScavKey))
                {
                    v.ScavKey = key;
                    (StatusChangeField?.GetValue(h) as Action)?.Invoke();
                }
            }

            if (v.Status == PlayerGroupStatus.Scavenging)
            {
                var sc = g.GetOccupiedScavengable();
                if (sc != null)
                    KeepScavengePercent(g, sc.GetScavengeProgress());
            }

            // Fortschrittsbalken am Schild wie ScavengeHandler.Execute
            var pointer = g.Pointer;
            if (pointer == null)
                return;
            if (v.Status == PlayerGroupStatus.Scavenging)
            {
                var s = g.GetOccupiedScavengable();
                if (s != null)
                {
                    pointer.SetProgress(s.GetScavengeProgress());
                    if (pointer is VehiclePointer vp)
                        vp.SetProgressBarColor(fuel: false);
                }
                v.WasScavenging = true;
            }
            else if (v.WasScavenging)
            {
                v.WasScavenging = false;
                var vh = g.VehicleHandler;
                if (vh == null || (!vh.HaveVehicle() && !vh.ReachedVehicle()))
                    pointer.HideProgressBar();
            }
        }

        private static object ScavKey(Group g)
        {
            var s = g.GetOccupiedScavengable();
            if (s == null)
                return null;
            return (object)s.ScavengeWork ?? s;
        }

        private List<OrderLine> GuestQueueLines(Group g, View v)
        {
            var list = new List<OrderLine>(v.Queue.Count + 1);
            if ((v.Flags & F.StartsAtGroup) != 0)
                list.Add(new OrderLine(g.Position, LineType.Normal));
            list.AddRange(v.Queue);
            return list;
        }

        private void ForgetView(View v)
        {
            var g = v.Group ?? UnitSync.FindGroupPublic(v.Id);
            if (g == null)
                return;
            if (g.PathDrawer != null && g.PathDrawer.HasPathLine)
                g.PathDrawer.DestroyLine();
            if (_chains.TryGetValue(g, out var c) && c.Mine && g.PathDrawer != null)
                g.PathDrawer.DespawnNextPath();
            _chains.Remove(g);
        }

        /// <summary>Haken-Prefix für PathDrawer.UpdateNextPaths(List&lt;OrderLine&gt;): beim Gast nur aus diesem Modul.</summary>
        public static bool AllowNextPaths()
        {
            var units = Plugin.Instance?.Session?.Units;
            if (units == null || !units.PuppetMode || Drawing)
                return true;
            Blocked++;
            return false;
        }

        // ================================================================ Beide: Befehlskette

        private void UpdateChain(Group g, bool host, List<OrderLine> lines)
        {
            var d = g.PathDrawer;
            if (d == null || lines == null)
                return;
            if (!_chains.TryGetValue(g, out var c))
            {
                c = new Chain();
                _chains[g] = c;
            }
            bool local = g.SelectionHandler != null && g.SelectionHandler.Selected;
            if (host && local)
            {
                // beim Host zeichnet das Spiel die Kette des selbst ausgewählten Trupps
                c.Mine = false;
                c.Sig = null;
                return;
            }
            bool want = local || RemoteSelected(g.Id);
            if (!want)
            {
                if (c.Mine)
                {
                    d.DespawnNextPath();
                    c.Mine = false;
                    c.Sig = null;
                }
                return;
            }
            string sig = Sig(lines);
            int expected = lines.Count > 1 ? lines.Count - 1 : 0;
            var drawn = NextLinesField(d);
            int actual = drawn != null ? drawn.Count : 0;
            if (c.Mine && sig == c.Sig && actual == expected)
                return;
            Drawing = true;
            try
            {
                d.UpdateNextPaths(lines);
            }
            finally
            {
                Drawing = false;
            }
            c.Mine = true;
            c.Sig = sig;
            ChainDraws++;
        }

        private void CleanupChains(List<Group> squads)
        {
            if (_chains.Count == 0)
                return;
            var set = new HashSet<Group>(squads);
            var gone = new List<Group>();
            foreach (var kv in _chains)
                if (kv.Key == null || !set.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var g in gone)
            {
                if (g != null && _chains[g].Mine && g.PathDrawer != null)
                    g.PathDrawer.DespawnNextPath();
                _chains.Remove(g);
            }
        }

        private bool RemoteSelected(string id)
        {
            float now = Time.realtimeSinceStartup;
            foreach (var remote in _session.Presence.Remotes)
                if (now - remote.Updated <= RemoteStale && remote.Squads.Contains(id))
                    return true;
            return false;
        }

        // ================================================================ Beide: Spielerfarben am Trupp

        /// <summary>
        /// Anzeige am Schild (Pointer) und in der Trupp-Liste (SquadLabel):
        /// - ein ANDERER Spieler hat den Trupp ausgewählt: Symbol in dessen Farbe;
        /// - frei: Spielfarbe (grün, grau bei untätig);
        /// - zuletzt gesteuert (echte Änderung: Befehl, Teilen, Name, Haltung …; NICHT bloßes Auswählen) von einem
        ///   anderen: zusätzlich ein kleiner Punkt in dessen Farbe, der bleibt, bis ein anderer etwas ändert.
        /// Dazu im Trupp-Fenster „Zuletzt gesteuert: Name“ in dessen Farbe.
        /// </summary>
        private void UpdateColors(List<Group> squads, Func<string, ulong> ctrlOf, Func<string, ulong> lastOf, float now)
        {
            ulong me = _session.MyId.m_SteamID;
            var colors = _session.PlayerColors;
            if (now >= _nextLabelScan)
            {
                _nextLabelScan = now + LabelScanInterval;
                ScanLabels();
            }
            var labelOf = new Dictionary<Group, Component>();
            foreach (var label in _labels)
            {
                if (label == null)
                    continue;
                var sq = LabelSquad(label);
                if (sq != null) labelOf[sq] = label;
            }

            var dotted = new HashSet<Pointer>();
            var dottedLabels = new HashSet<Component>();
            foreach (var g in squads)
            {
                ulong ctrl = ctrlOf(g.Id);
                ulong last = lastOf(g.Id);
                bool otherHas = ctrl != 0UL && ctrl != me;
                Color? tint = otherHas && !IsInactive(g) ? colors.ColorOf(ctrl) : (Color?)null;
                // Punkt: wer zuletzt wirklich etwas geändert hat (auch man selbst, in der eigenen Farbe);
                // bleibt auch, während jemand den Trupp nur anschaut
                Color? dot = last != 0UL ? colors.ColorOf(last) : (Color?)null;

                var pointer = g.Pointer;
                if (pointer != null)
                {
                    if (tint.HasValue)
                    {
                        var icon = IconField(pointer);
                        if (!_tinted.TryGetValue(g, out var cur) || cur != tint.Value || (icon != null && icon.color != tint.Value))
                        {
                            pointer.SetColor(tint.Value);
                            ColorChanges++;
                        }
                        _tinted[g] = tint.Value;
                    }
                    else if (_tinted.Remove(g))
                    {
                        pointer.SetColor(g.GetColor());
                        ColorChanges++;
                    }
                    if (dot.HasValue && SetPointerDot(pointer, dot.Value))
                    {
                        dotted.Add(pointer);
                        _dotPointers[pointer] = g;
                    }
                }
                if (labelOf.TryGetValue(g, out var lab))
                {
                    if (tint.HasValue)
                    {
                        if (!_tintedLabels.TryGetValue(lab, out var cur) || cur != tint.Value || LabelColor(lab) != tint.Value)
                            SetLabelColor(lab, tint.Value);
                        _tintedLabels[lab] = tint.Value;
                    }
                    else if (_tintedLabels.Remove(lab))
                    {
                        SetLabelColor(lab, IsInactive(g) ? Color.gray : GreenLabel());
                    }
                    if (dot.HasValue && SetLabelDot(lab, dot.Value))
                    {
                        dottedLabels.Add(lab);
                        _dotLabels.Add(lab);
                    }
                }
            }

            // Punkte, die nicht mehr gelten (oder an einem Schild hängen, das das Spiel einer anderen Gruppe gegeben hat)
            foreach (var p in new List<Pointer>(_dotPointers.Keys))
            {
                if (p != null && dotted.Contains(p) && p.Group == _dotPointers[p])
                    continue;
                if (p != null)
                    HidePointerDot(p);
                _dotPointers.Remove(p);
            }
            foreach (var lab in new List<Component>(_dotLabels))
            {
                if (lab != null && dottedLabels.Contains(lab))
                    continue;
                if (lab != null)
                    HideLabelDot(lab);
                _dotLabels.Remove(lab);
            }
            foreach (var g in new List<Group>(_tinted.Keys))
                if (g == null) _tinted.Remove(g);
            foreach (var l in new List<Component>(_tintedLabels.Keys))
                if (l == null) _tintedLabels.Remove(l);

            UpdatePanelText(lastOf);
        }

        private const string DotName = "IfzCoopDot";
        private const string PanelWarningId = "IfzCoop.LastCtrl";
        private static Sprite _dotSprite;
        private static readonly Type ImageType = AccessTools.TypeByName("UnityEngine.UI.Image");

        /// <summary>Weißer Punkt mit dunklem Rand (der Rand bleibt beim Einfärben dunkel, gut lesbar auf Grün).</summary>
        private static Sprite DotSprite()
        {
            if (_dotSprite != null)
                return _dotSprite;
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            float r = size / 2f;
            var dark = new Color(0.05f, 0.05f, 0.05f, 0.9f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                    Color c;
                    if (d < r - 7f)
                        c = Color.white;
                    else if (d < r - 6f)
                        c = Color.Lerp(Color.white, dark, d - (r - 7f));
                    else
                        c = new Color(dark.r, dark.g, dark.b, dark.a * Mathf.Clamp01(r - d));
                    tex.SetPixel(x, y, c);
                }
            tex.Apply();
            _dotSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
            _dotSprite.name = DotName;
            return _dotSprite;
        }

        /// <summary>Punkt oben rechts am Trupp-Symbol (Kind des Symbol-Halters, bleibt auch bei „im Haus“ sichtbar).</summary>
        private static bool SetPointerDot(Pointer p, Color color)
        {
            var icon = IconField(p);
            if (icon == null)
                return false;
            var it = icon.transform;
            var parent = it.parent != null ? it.parent : it;
            var t = parent.Find(DotName);
            SpriteRenderer sr;
            if (t == null)
            {
                var go = new GameObject(DotName) { layer = icon.gameObject.layer };
                go.transform.SetParent(parent, false);
                sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = DotSprite();
                if (icon.sharedMaterial != null)
                    sr.sharedMaterial = icon.sharedMaterial;
                t = go.transform;
            }
            else
            {
                sr = t.GetComponent<SpriteRenderer>();
                if (sr == null)
                    return false;
            }
            var b = icon.sprite != null ? icon.sprite.bounds : new Bounds(Vector3.zero, Vector3.one);
            var ls = it.localScale;
            var rot = parent == it ? Quaternion.identity : it.localRotation;
            var basePos = parent == it ? Vector3.zero : it.localPosition;
            var center = basePos + rot * Vector3.Scale(b.center, ls);
            float ex = b.extents.x * Mathf.Abs(ls.x), ey = b.extents.y * Mathf.Abs(ls.y);
            t.localPosition = center + rot * new Vector3(ex * 0.8f, ey * 0.8f, 0f);
            t.localRotation = rot;
            float s = Mathf.Max(ex, ey) * 2f * 0.38f;
            t.localScale = new Vector3(s, s, 1f);
            sr.sortingLayerID = icon.sortingLayerID;
            sr.sortingOrder = icon.sortingOrder + 5;
            sr.color = color;
            if (!t.gameObject.activeSelf)
                t.gameObject.SetActive(true);
            return true;
        }

        private static Transform PointerDot(Pointer p)
        {
            var icon = p != null ? IconField(p) : null;
            if (icon == null)
                return null;
            var parent = icon.transform.parent != null ? icon.transform.parent : icon.transform;
            return parent.Find(DotName);
        }

        private static void HidePointerDot(Pointer p)
        {
            var t = PointerDot(p);
            if (t != null && t.gameObject.activeSelf)
                t.gameObject.SetActive(false);
        }

        private static Color? PointerDotColor(Pointer p)
        {
            var t = PointerDot(p);
            if (t == null || !t.gameObject.activeSelf)
                return null;
            var sr = t.GetComponent<SpriteRenderer>();
            return sr != null ? sr.color : (Color?)null;
        }

        private static Component LabelFrame(Component label)
        {
            try { return Traverse.Create(label).Field("frameImage").GetValue() as Component; }
            catch { return null; }
        }

        /// <summary>Punkt oben rechts am Rahmen in der Trupp-Liste (UI-Image, per Reflection: kein Verweis auf UnityEngine.UI).</summary>
        private static bool SetLabelDot(Component label, Color color)
        {
            var frame = LabelFrame(label);
            if (frame == null || ImageType == null)
                return false;
            var t = frame.transform.Find(DotName);
            Component img;
            if (t == null)
            {
                var go = new GameObject(DotName, typeof(RectTransform)) { layer = frame.gameObject.layer };
                go.transform.SetParent(frame.transform, false);
                var rt = (RectTransform)go.transform;
                rt.anchorMin = new Vector2(1f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(1f, 1f);
                rt.anchoredPosition = new Vector2(-2f, -2f);
                rt.sizeDelta = new Vector2(14f, 14f);
                img = go.AddComponent(ImageType);
                var tr = Traverse.Create(img);
                tr.Property("sprite").SetValue(DotSprite());
                tr.Property("raycastTarget").SetValue(false);
                t = go.transform;
            }
            else
            {
                img = t.GetComponent(ImageType);
                if (img == null)
                    return false;
            }
            t.SetAsLastSibling();
            Traverse.Create(img).Property("color").SetValue(color);
            if (!t.gameObject.activeSelf)
                t.gameObject.SetActive(true);
            return true;
        }

        private static void HideLabelDot(Component label)
        {
            var frame = LabelFrame(label);
            var t = frame != null ? frame.transform.Find(DotName) : null;
            if (t != null && t.gameObject.activeSelf)
                t.gameObject.SetActive(false);
        }

        private static Color? LabelDotColor(Component label)
        {
            var frame = label != null ? LabelFrame(label) : null;
            var t = frame != null ? frame.transform.Find(DotName) : null;
            if (t == null || !t.gameObject.activeSelf || ImageType == null)
                return null;
            var img = t.GetComponent(ImageType);
            return img != null ? Traverse.Create(img).Property("color").GetValue<Color>() : (Color?)null;
        }

        // ------------------------------------------------------------- Trupp-Fenster: „Zuletzt gesteuert: Name“

        private static readonly AccessTools.FieldRef<UI.InfoPanels.SpecificPanelInfo, UI.InfoPanels.InfoPanelHeader> HeaderField =
            AccessTools.FieldRefAccess<UI.InfoPanels.SpecificPanelInfo, UI.InfoPanels.InfoPanelHeader>("header");
        private static readonly AccessTools.FieldRef<UI.InfoPanels.InfoPanelHeader, UI.InfoPanels.WarningsContainer> WarningsField =
            AccessTools.FieldRefAccess<UI.InfoPanels.InfoPanelHeader, UI.InfoPanels.WarningsContainer>("_warningsContainer");
        private Controllers.InfoPanelController _infoPanel;

        private List<UI.InfoPanels.SquadPanelInfo> OpenSquadPanels()
        {
            var list = new List<UI.InfoPanels.SquadPanelInfo>();
            if (_infoPanel == null)
                _infoPanel = UnityEngine.Object.FindObjectOfType<Controllers.InfoPanelController>();
            if (_infoPanel == null)
                return list;
            var a = _infoPanel.CurrentDisplayedPanel as UI.InfoPanels.SquadPanelInfo;
            if (a != null && a.Squad != null)
                list.Add(a);
            var b = _infoPanel.CurrentSecondDisplayedPanel as UI.InfoPanels.SquadPanelInfo;
            if (b != null && b.Squad != null && b != a)
                list.Add(b);
            return list;
        }

        private static readonly AccessTools.FieldRef<UI.InfoPanels.SquadPanelInfo, string> ScavTextField =
            AccessTools.FieldRefAccess<UI.InfoPanels.SquadPanelInfo, string>("_scavengingText");

        /// <summary>
        /// Gast: „Plündern: x %“ im Trupp-Fenster festhalten. Das Spiel baut die Zeile bei jedem Zustandswechsel neu (nur
        /// „Plündern“) und setzt die Zahl erst beim nächsten Fortschritts-Signal – beim Gast kommt das selten, die Zahl
        /// verschwand zeitweise (Bericht 2026-11-08). Text wie SquadPanelInfo.UpdateScavengeProgress.
        /// </summary>
        private void KeepScavengePercent(Group g, float progress)
        {
            try
            {
                foreach (var panel in OpenSquadPanels())
                {
                    if (panel.Squad != g)
                        continue;
                    var header = HeaderField(panel);
                    var box = header != null ? WarningsField(header) : null;
                    var w = box != null ? box.GetWarning("Code.SquadStatus.") : null;
                    string label = ScavTextField(panel);
                    if (w == null || string.IsNullOrEmpty(label))
                        continue;
                    string text = $"{label}: {progress * 100f:0.0}%";
                    if (w.Text != text)
                        w.SetWarningText(text);
                }
            }
            catch (Exception e) { Plugin.Log.LogDebug("Plündern-Prozent: " + e.Message); }
        }

        private string LastText(ulong last)
        {
            if (last == 0UL)
                return null;
            var colors = _session.PlayerColors;
            return $"Zuletzt gesteuert: <color=#{PlayerColorSync.Hex(colors.ColorOf(last))}>{colors.NameOf(last)}</color>";
        }

        private void UpdatePanelText(Func<string, ulong> lastOf)
        {
            foreach (var panel in OpenSquadPanels())
            {
                try
                {
                    var header = HeaderField(panel);
                    var box = header != null ? WarningsField(header) : null;
                    if (box == null)
                        continue;
                    ulong last = lastOf(panel.Squad.Id);
                    string text = LastText(last);
                    var w = box.GetWarning(PanelWarningId);
                    if (text == null)
                    {
                        if (w != null) box.RemoveWarning(PanelWarningId);
                        continue;
                    }
                    if (w == null)
                    {
                        box.AddWarning(PanelWarningId, text, DotSprite());
                        w = box.GetWarning(PanelWarningId);
                        if (w != null) w.transform.SetAsLastSibling();
                    }
                    else if (w.Text != text)
                    {
                        w.SetWarningText(text);
                    }
                    if (w != null)
                        Traverse.Create(w).Field("_warningImage").Property("color").SetValue(_session.PlayerColors.ColorOf(last));
                }
                catch (Exception e)
                {
                    Plugin.Log.LogDebug("Trupp-Fenster Zuletzt gesteuert: " + e.Message);
                }
            }
        }

        private string PanelText(Group g)
        {
            foreach (var panel in OpenSquadPanels())
            {
                if (panel.Squad != g)
                    continue;
                var header = HeaderField(panel);
                var box = header != null ? WarningsField(header) : null;
                var w = box != null ? box.GetWarning(PanelWarningId) : null;
                return w != null ? w.Text : "-";
            }
            return null;
        }

        private static bool IsInactive(Group g)
        {
            var sm = g.StateMachine;
            return sm != null && (sm.InState<InactiveState>() || sm.InState<ExchangeInactiveState>());
        }

        private void ScanLabels()
        {
            _labels.Clear();
            if (LabelType == null)
                return;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(LabelType))
                if (o is Component c) _labels.Add(c);
        }

        private static Group LabelSquad(Component label)
        {
            try { return Traverse.Create(label).Property("Squad").GetValue<Group>(); }
            catch { return null; }
        }

        private Component LabelOf(Group g)
        {
            foreach (var label in _labels)
                if (label != null && LabelSquad(label) == g) return label;
            return null;
        }

        private static Color LabelColor(Component label)
        {
            try { return Traverse.Create(label).Field("frameImage").Property("color").GetValue<Color>(); }
            catch { return Color.clear; }
        }

        private static void SetLabelColor(Component label, Color color)
        {
            try { LabelSetColorMethod?.Invoke(label, new object[] { color }); }
            catch (Exception e) { Plugin.Log.LogDebug("Trupp-Liste färben: " + e.Message); }
        }

        private static Color GreenLabel()
        {
            var cfg = ColorsMaterialsConfig.Instance;
            return cfg != null && cfg.dictionaryColors != null ? cfg.dictionaryColors.colorGreen : Color.green;
        }

        /// <summary>Beim Ende der Sitzung: Farben zurück, Punkte und Fenster-Zeile weg, eigene Ketten weg.</summary>
        private void RestoreAll()
        {
            foreach (var g in _tinted.Keys)
                if (g != null && g.Pointer != null) g.Pointer.SetColor(g.GetColor());
            foreach (var lab in _tintedLabels.Keys)
            {
                if (lab == null)
                    continue;
                var sq = LabelSquad(lab);
                SetLabelColor(lab, sq != null && IsInactive(sq) ? Color.gray : GreenLabel());
            }
            foreach (var p in _dotPointers.Keys)
                if (p != null) HidePointerDot(p);
            foreach (var lab in _dotLabels)
                if (lab != null) HideLabelDot(lab);
            UpdatePanelText(_ => 0UL);
            foreach (var kv in _chains)
                if (kv.Key != null && kv.Value.Mine && kv.Key.PathDrawer != null)
                    kv.Key.PathDrawer.DespawnNextPath();
        }

        // ================================================================ Hilfen

        private static List<Group> Squads()
        {
            var list = new List<Group>();
            var squads = GameAccess.Game<SquadsController>()?.Squads;
            if (squads == null)
                return list;
            for (int i = 0; i < squads.Count; i++)
                if (squads[i] != null && !string.IsNullOrEmpty(squads[i].Id) && !squads[i].IsEmpty())
                    list.Add(squads[i]);
            return list;
        }

        private static HashSet<string> SelectedIds()
        {
            var set = new HashSet<string>();
            var selected = GameAccess.Game<SquadsController>()?.SelectedSquads;
            if (selected != null)
                for (int i = 0; i < selected.Count; i++)
                    if (selected[i] != null && !string.IsNullOrEmpty(selected[i].Id)) set.Add(selected[i].Id);
            return set;
        }

        private static string Sig(View v)
        {
            var sb = new StringBuilder();
            sb.Append((int)v.Flags).Append('|').Append((int)v.Status).Append('|').Append(v.Ctrl).Append('|').Append((int)v.Line).Append('|')
              .Append(v.OrderSeq).Append('|').Append(v.LastBy).Append('|');
            foreach (var p in v.Path) AppendPoint(sb, p);
            sb.Append('|');
            foreach (var l in v.Queue) { AppendPoint(sb, l.targetPosition); sb.Append((int)l.lineType); }
            return sb.ToString();
        }

        private static string Sig(List<OrderLine> lines)
        {
            var sb = new StringBuilder();
            foreach (var l in lines) { AppendPoint(sb, l.targetPosition); sb.Append((int)l.lineType); }
            return sb.ToString();
        }

        private static void AppendPoint(StringBuilder sb, Vector3 p)
        {
            sb.Append(Mathf.RoundToInt(p.x * 2f)).Append(',').Append(Mathf.RoundToInt(p.z * 2f)).Append(';');
        }

        private static void Write(BinaryWriter w, View v)
        {
            w.Write(v.Id);
            w.Write((byte)v.Flags);
            if ((v.Flags & F.Removed) != 0)
                return;
            w.Write((short)v.Status);
            w.Write(v.Ctrl);
            w.Write((byte)v.Line);
            w.Write(v.OrderBy);
            w.Write(v.LastBy);
            w.Write(v.OrderSeq);
            w.Write(v.OrderDay);
            w.Write(v.OrderHour);
            w.Write((ushort)v.Path.Count);
            foreach (var p in v.Path) WriteVector(w, p);
            w.Write((ushort)v.Queue.Count);
            foreach (var l in v.Queue)
            {
                WriteVector(w, l.targetPosition);
                w.Write((byte)l.lineType);
            }
        }

        private static View Read(BinaryReader r)
        {
            var v = new View { Id = r.ReadString(), Flags = (F)r.ReadByte() };
            if ((v.Flags & F.Removed) != 0)
                return v;
            v.Status = (PlayerGroupStatus)r.ReadInt16();
            v.Ctrl = r.ReadUInt64();
            v.Line = (LineType)r.ReadByte();
            v.OrderBy = r.ReadUInt64();
            v.LastBy = r.ReadUInt64();
            v.OrderSeq = r.ReadInt32();
            v.OrderDay = r.ReadInt32();
            v.OrderHour = r.ReadSingle();
            int n = r.ReadUInt16();
            if (n > MaxPoints)
                throw new InvalidDataException("Weg zu lang");
            for (int i = 0; i < n; i++)
                v.Path.Add(ReadVector(r));
            int q = r.ReadUInt16();
            if (q > MaxPoints)
                throw new InvalidDataException("Befehlskette zu lang");
            for (int i = 0; i < q; i++)
                v.Queue.Add(new OrderLine(ReadVector(r), (LineType)r.ReadByte()));
            return v;
        }

        private static bool Near(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) < 0.02f && Mathf.Abs(a.g - b.g) < 0.02f && Mathf.Abs(a.b - b.b) < 0.02f;

        private static void WriteVector(BinaryWriter w, Vector3 v)
        {
            w.Write(v.x);
            w.Write(v.y);
            w.Write(v.z);
        }

        private static Vector3 ReadVector(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        // ================================================================ Test

        /// <summary>
        /// Statuszeilen je Trupp (Host und Gast vergleichbar): Zustand, Text des Trupp-Schilds, Plünder-Prozent,
        /// läuft (Weg-Striche nötig), Wegpunkte, Befehle in der Kette, Striche aktiv, Ketten-Linien, Steuernder.
        /// Dazu je Trupp die Farbe (unterscheidet sich zwischen Host und Gast mit Absicht).
        /// </summary>
        public IEnumerable<string> StatusLines()
        {
            var inv = CultureInfo.InvariantCulture;
            var lines = new List<string>
            {
                $"squadView sent={Sent} received={Received} actionsNoted={ActionsNoted} statusSet={StatusSet} chainDraws={ChainDraws} ctrlChanges={CtrlChanges} colorChanges={ColorChanges} blocked={Blocked} views={_views.Count} stopsSeen={StopsSeen} tinted={_tinted.Count} dots={_dotPointers.Count}",
            };
            bool host = _session.Role == Role.Host;
            ulong me = _session.MyId.m_SteamID;
            foreach (var g in Squads())
            {
                try
                {
                    View v;
                    if (host)
                        v = BuildHost(g, true);
                    else if (!_views.TryGetValue(g.Id, out v))
                        v = new View { Id = g.Id };
                    var h = g.PlayerGroupStatusHandler;
                    var st = h != null ? h.Status : PlayerGroupStatus.None;
                    string txt = "-";
                    try { if (h != null) txt = h.GetStatusText().Replace(' ', '_'); } catch { }
                    int pct = -1;
                    var s = g.GetOccupiedScavengable();
                    if (st == PlayerGroupStatus.Scavenging && s != null)
                        pct = Mathf.RoundToInt(s.GetScavengeProgress() * 100f);
                    var d = g.PathDrawer;
                    var drawn = d != null ? NextLinesField(d) : null;
                    int q = v.Queue.Count;
                    bool run = (v.Flags & F.Moving) != 0;
                    lines.Add($"sqview {g.Id} st={st} txt={txt} pct={pct} run={(run ? 1 : 0)} wp={v.Path.Count} q={q} line={(d != null && d.HasPathLine ? 1 : 0)} next={(drawn != null ? drawn.Count : 0)} ctrl={v.Ctrl}");
                    var icon = g.Pointer != null ? IconField(g.Pointer) : null;
                    // „spiel“ = Spielfarbe (von diesem Modul nicht eingefärbt), sonst die tatsächliche Farbe
                    string iconTxt = icon == null ? "-" : !_tinted.ContainsKey(g) || Near(icon.color, g.GetColor()) ? "spiel" : PlayerColorSync.Hex(icon.color);
                    var dotC = PointerDotColor(g.Pointer);
                    var lab = LabelOf(g);
                    string labTxt = lab == null ? "-" : !_tintedLabels.ContainsKey(lab) ? "spiel" : PlayerColorSync.Hex(LabelColor(lab));
                    var labDot = LabelDotColor(lab);
                    string who = v.Ctrl == 0UL ? "keiner" : v.Ctrl == me ? "ich" : "anderer";
                    string lastWho = v.LastBy == 0UL ? "keiner" : v.LastBy == me ? "ich" : "anderer";
                    string panel = PanelText(g);
                    string order = "keiner";
                    float ago = -1f;
                    if (TryGetLastOrder(g.Id, out var oby, out var oago, out _, out _))
                    {
                        order = oby == me ? "ich" : "anderer";
                        ago = oago;
                    }
                    lines.Add($"sqcolor {g.Id} icon={iconTxt} dot={(dotC.HasValue ? PlayerColorSync.Hex(dotC.Value) : "-")} label={labTxt} labeldot={(labDot.HasValue ? PlayerColorSync.Hex(labDot.Value) : "-")} ctrl={who} ctrlHex={(v.Ctrl != 0UL ? PlayerColorSync.Hex(_session.PlayerColors.ColorOf(v.Ctrl)) : "-")} last={lastWho} lastHex={(v.LastBy != 0UL ? PlayerColorSync.Hex(_session.PlayerColors.ColorOf(v.LastBy)) : "-")} panel={(panel == null ? "zu" : panel.Replace(' ', '_'))} order={order} orderAgo={ago.ToString("0", inv)} sel={(g.SelectionHandler != null && g.SelectionHandler.Selected ? 1 : 0)} pos={g.Position.x.ToString("0", inv)},{g.Position.z.ToString("0", inv)}");
                }
                catch (Exception e)
                {
                    lines.Add($"sqview {g.Id} fehler={e.Message}");
                }
            }
            return lines;
        }

        /// <summary>
        /// Testbefehle "sv …":
        ///   sv select &lt;id|first|0&gt;       Trupp auswählen (wie Klick) bzw. Auswahl leeren
        ///   sv chain &lt;n&gt; [id|first]       Trupp auswählen und n Häuser nacheinander plündern lassen (erstes ohne, weitere mit Shift)
        ///   sv move &lt;dx&gt; &lt;dz&gt; [id|first] Trupp auswählen und relativ zum Hauptquartier schicken
        ///   sv rename &lt;id&gt; &lt;Name&gt;, sv stance &lt;id&gt; &lt;Haltung&gt; 0|1  Änderungen wie über das Fenster (zählen als „gesteuert“)
        ///   sv panel &lt;id&gt;                  Trupp-Fenster öffnen (Zeile „Zuletzt gesteuert“)
        /// </summary>
        public string Qa(string[] a)
        {
            var sc = GameAccess.Game<SquadsController>();
            if (sc == null || a.Length < 2)
                return "kein Spiel / sv select|chain|move|rename|stance|panel";
            var inv = CultureInfo.InvariantCulture;
            switch (a[1])
            {
                case "select":
                {
                    string id = a.Length > 2 ? a[2] : "first";
                    sc.ClearSelectedSquads();
                    if (id == "0")
                        return "Auswahl leer";
                    var g = FindSquad(sc, id);
                    if (g == null)
                        return "kein Trupp " + id;
                    g.SelectionHandler.Select(tellOnRadio: false);
                    return "ausgewählt " + g.Id;
                }
                case "rename":
                {
                    // "sv rename <id> <Name_mit_Unterstrichen>": wie Umbenennen im Fenster (zählt als Änderung)
                    var g = FindSquad(sc, a.Length > 2 ? a[2] : "first");
                    if (g == null || a.Length < 4)
                        return "kein Trupp / Name fehlt";
                    g.SetGroupName(a[3].Replace('_', ' '));
                    return "Name " + g.GroupName;
                }
                case "stance":
                {
                    // "sv stance <id> <Haltung> 0|1"
                    var g = FindSquad(sc, a.Length > 2 ? a[2] : "first");
                    if (g == null || a.Length < 5 || g.StancesHandler == null)
                        return "kein Trupp / Haltung fehlt";
                    var st = (GroupStanceId)Enum.Parse(typeof(GroupStanceId), a[3]);
                    g.StancesHandler.SetStateToStance(st, a[4] == "1");
                    return "Haltung " + st + "=" + g.StancesHandler.IsActive(st);
                }
                case "panel":
                {
                    // "sv panel <id>": Trupp-Fenster öffnen (für „Zuletzt gesteuert“)
                    var g = FindSquad(sc, a.Length > 2 ? a[2] : "first");
                    if (g == null)
                        return "kein Trupp";
                    sc.ShowSquadInfoPanel(g);
                    return "Fenster " + g.Id;
                }
                case "move":
                {
                    var g = FindSquad(sc, a.Length > 4 ? a[4] : "first");
                    if (g == null)
                        return "kein Trupp";
                    sc.ClearSelectedSquads();
                    g.SelectionHandler.Select(tellOnRadio: false);
                    var hq = Controllers.HqController.MainHeadquarter;
                    var hqPos = hq != null ? hq.transform.position : g.Position;
                    sc.MoveSelectedTo(hqPos + new Vector3(float.Parse(a[2], inv), 0f, float.Parse(a[3], inv)));
                    return "Trupp " + g.Id + " geschickt";
                }
                case "chain":
                {
                    int n = a.Length > 2 ? int.Parse(a[2], inv) : 3;
                    var g = FindSquad(sc, a.Length > 3 ? a[3] : "first");
                    if (g == null)
                        return "kein Trupp";
                    sc.ClearSelectedSquads();
                    g.SelectionHandler.Select(tellOnRadio: false);
                    var targets = new List<Gameplay.Buildings.Building>();
                    var all = GameAccess.Game<Controllers.BuildingsController>()?.Buildings;
                    if (all == null)
                        return "keine Häuser";
                    var cand = new List<Gameplay.Buildings.Building>();
                    foreach (var b in all)
                        if (b != null && !b.IsHq && b.IsScavengable() && !b.IsScavenged() && b.ScavengeWork == null)
                            cand.Add(b);
                    var from = g.Position;
                    for (int k = 0; k < n && cand.Count > 0; k++)
                    {
                        Gameplay.Buildings.Building best = null;
                        float bestD = float.MaxValue;
                        foreach (var b in cand)
                        {
                            float dist = (b.transform.position - from).sqrMagnitude;
                            if (dist < bestD) { bestD = dist; best = b; }
                        }
                        cand.Remove(best);
                        targets.Add(best);
                        from = best.transform.position;
                    }
                    var keys = Traverse.Create(g.OrdersHandlers).Field("_gameKeysController").GetValue();
                    var shift = keys != null ? Traverse.Create(keys).Field("_isShiftPressed") : null;
                    var ids = new List<string>();
                    try
                    {
                        for (int k = 0; k < targets.Count; k++)
                        {
                            shift?.SetValue(k > 0);
                            sc.MoveSelected(targets[k], targets[k].transform.position);
                            ids.Add(targets[k].ID.ToString(inv));
                        }
                    }
                    finally
                    {
                        shift?.SetValue(false);
                    }
                    return $"Trupp {g.Id} plündert {string.Join(",", ids)}";
                }
            }
            return "unbekannt";
        }

        private static Group FindSquad(SquadsController sc, string id)
        {
            var squads = sc.Squads;
            for (int i = 0; i < squads.Count; i++)
            {
                var g = squads[i];
                if (g == null || g.IsEmpty())
                    continue;
                if (id == "first" || g.Id == id)
                    return g;
            }
            return null;
        }
    }
}
