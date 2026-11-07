using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Gameplay.Units;
using Gameplay.Units.Characters;
using Gameplay.Units.Interfaces;
using IfzCoop.Game;
using IfzCoop.Net;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Statistik je Spieler (Wunsch 2026-11-06: „wer hat wie viele getötet, Schaden verursacht, gebaut, abgerissen,
    /// Trupps hergestellt“). Angezeigt im Esc-Menü (Knopf „Statistik“, Menu\StatsWindow).
    ///
    /// Gezählt wird nur beim Host (und im Einzelspiel), er rechnet ja alles:
    /// - Getötet / Schaden / Verluste: Character.ReceiveDamage (UnitPatches). Der Angreifer kommt aus dem IDamageApplier
    ///   (CharacterDamageAreaApplier = Figur, auch Granaten/Geschosse; oder eine Gruppe). Gehört er zu einem Trupp des
    ///   Spielers, zählt es für den, der den Trupp zuletzt gesteuert hat (SquadViewSync: Befehl oder Auswahl), sonst für
    ///   den Host. Türme zählen für niemanden.
    /// - Gebaut / Abgerissen / Repariert: an den Stellen, an denen die Mod Gast-Aktionen abfängt (BuildingSync.GuestAction,
    ///   StructureSync.GuestPlace/GuestWallEnd/GuestStructAction). Beim Host läuft dort auch seine eigene Aktion durch;
    ///   führt der Host den Wunsch eines Gastes aus, setzt der Wunsch-Empfänger <see cref="Actor"/> auf den Gast.
    /// - Trupps ausgebildet: SquadCreatorController.AddCreationRequestAtStructure (Gast-Wunsch über SquadSync mit Actor).
    /// - Befehle: SquadViewSync.NoteOrder (nur echte Spielereingaben).
    ///
    /// Der Host schickt die Zahlen alle 2 s (bei Änderung) an die Gäste (Msg.Stats), dazu die Entwickler-Schalter
    /// (SaveData-Flags), damit z. B. „Forschung kostenlos“ auch im Forschungsfenster des Gastes stimmt.
    /// Die Zahlen gelten für die laufende Sitzung (nicht im Spielstand gespeichert).
    /// </summary>
    internal sealed class StatsSync
    {
        public enum Stat { Kills = 0, Damage = 1, Losses = 2, Built = 3, Demolished = 4, Repaired = 5, Squads = 6, Orders = 7 }

        public const int StatCount = 8;
        public static readonly string[] Labels = { "Getötet", "Schaden", "Verluste", "Gebaut", "Abgerissen", "Repariert", "Trupps ausgebildet", "Befehle" };

        private const float SendInterval = 2f;

        private readonly Session _session;
        private readonly Dictionary<ulong, float[]> _values = new Dictionary<ulong, float[]>();
        private readonly Dictionary<ulong, string> _names = new Dictionary<ulong, string>();
        private string _sentSig;
        private float _nextSend;

        /// <summary>Host: wer gerade handelt, während ein Gast-Wunsch ausgeführt wird (sonst der Host selbst).</summary>
        public static ulong? Actor;

        public int Received { get; private set; }

        public StatsSync(Session session)
        {
            _session = session;
        }

        public void Reset()
        {
            _values.Clear();
            _names.Clear();
            _sentSig = null;
        }

        public void ResendAll() => _sentSig = null;

        private bool Counting => _session.Role != Role.Guest && GameAccess.IsGameStarted;

        private ulong Me => _session.MyId.m_SteamID;

        public ulong ActorOrMe => Actor ?? Me;

        /// <summary>Aktion unter dem Namen eines Gastes ausführen (Host, beim Abarbeiten eines Wunsches).</summary>
        public static void As(ulong who, System.Action act)
        {
            var old = Actor;
            Actor = who;
            try { act(); }
            finally { Actor = old; }
        }

        public void Add(ulong who, Stat s, float v = 1f)
        {
            if (!Counting || v <= 0f)
                return;
            if (who == 0UL) who = Me;
            if (!_values.TryGetValue(who, out var arr))
                _values[who] = arr = new float[StatCount];
            arr[(int)s] += v;
        }

        public void AddForActor(Stat s, float v = 1f) => Add(ActorOrMe, s, v);

        // ================================================================ Kampf

        /// <summary>Spieler, dem ein Trupp zugerechnet wird (zuletzt gesteuert, sonst ausgewählt, sonst der Host).</summary>
        private ulong OwnerOfSquad(Group g)
        {
            if (g == null || string.IsNullOrEmpty(g.Id))
                return Me;
            var views = _session.SquadViews;
            ulong who = views != null ? views.LastBy(g.Id) : 0UL;
            if (who == 0UL && views != null) who = views.ControllerOf(g.Id);
            return who != 0UL ? who : Me;
        }

        internal static Group AttackerGroup(IDamageApplier applier)
        {
            switch (applier)
            {
                case CharacterDamageAreaApplier cda when cda.Character is Character c:
                    return c.Group;
                case Group g:
                    return g;
                default:
                    return null;
            }
        }

        /// <summary>Postfix Character.ReceiveDamage (Host/Einzelspiel). wasAlive kommt aus dem Prefix.</summary>
        public void OnDamage(Character target, float dmg, IDamageApplier applier, bool applied, bool wasAlive)
        {
            if (!Counting || target == null || !applied)
                return;
            var attacker = AttackerGroup(applier);
            bool attackerIsPlayer = attacker != null && attacker.Fraction == Fraction.Player;
            bool targetIsPlayer = target.Fraction == Fraction.Player;
            bool died = wasAlive && target.IsDead();
            if (attackerIsPlayer && !targetIsPlayer)
            {
                ulong who = OwnerOfSquad(attacker);
                Add(who, Stat.Damage, dmg);
                if (died) Add(who, Stat.Kills);
            }
            if (targetIsPlayer && died && target.Group != null && target.Group.Fraction == Fraction.Player)
            {
                var squads = GameAccess.Game<SquadsController>()?.Squads;
                if (squads != null && squads.Contains(target.Group))
                    Add(OwnerOfSquad(target.Group), Stat.Losses);
            }
        }

        // ================================================================ Abgleich

        private static int Flags()
        {
            var d = SaveHandler.SaveData;
            if (d == null) return 0;
            int f = 0;
            if (d.FastConstruction) f |= 1;
            if (d.FastResearch) f |= 2;
            if (d.FreeResearch) f |= 4;
            if (d.NoHunger) f |= 8;
            if (d.PerfectMood) f |= 16;
            if (d.FastScavenge) f |= 32;
            if (d.InfiniteAmmo) f |= 64;
            if (d.PerfectWeather) f |= 128;
            return f;
        }

        private static void ApplyFlags(int f)
        {
            var d = SaveHandler.SaveData;
            if (d == null) return;
            d.FastConstruction = (f & 1) != 0;
            d.FastResearch = (f & 2) != 0;
            d.FreeResearch = (f & 4) != 0;
            d.NoHunger = (f & 8) != 0;
            d.PerfectMood = (f & 16) != 0;
            d.FastScavenge = (f & 32) != 0;
            d.InfiniteAmmo = (f & 64) != 0;
            d.PerfectWeather = (f & 128) != 0;
        }

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (_session.Role != Role.Host || _session.ActiveGuestCount == 0 || now < _nextSend || !GameAccess.IsGameStarted)
                return;
            _nextSend = now + SendInterval;
            int flags = Flags();
            var sb = new StringBuilder().Append(flags);
            foreach (var kv in _values.OrderBy(k => k.Key))
            {
                sb.Append('|').Append(kv.Key);
                foreach (var v in kv.Value) sb.Append(',').Append(Mathf.RoundToInt(v));
            }
            string sig = sb.ToString();
            if (sig == _sentSig)
                return;
            _sentSig = sig;
            var rows = _values.ToList();
            _session.BroadcastToGuests(Protocol.Build(Msg.Stats, w =>
            {
                w.Write(flags);
                w.Write(rows.Count);
                foreach (var kv in rows)
                {
                    w.Write(kv.Key);
                    w.Write(_session.PlayerColors.NameOf(kv.Key) ?? "");
                    w.Write(kv.Value.Length);
                    foreach (var v in kv.Value) w.Write(v);
                }
            }));
        }

        public void GuestOnState(BinaryReader r)
        {
            int flags = r.ReadInt32();
            int n = r.ReadInt32();
            if (n < 0 || n > 64) throw new InvalidDataException("zu viele Spieler");
            var values = new Dictionary<ulong, float[]>();
            var names = new Dictionary<ulong, string>();
            for (int i = 0; i < n; i++)
            {
                ulong id = r.ReadUInt64();
                names[id] = r.ReadString();
                int k = r.ReadInt32();
                if (k < 0 || k > 64) throw new InvalidDataException("zu viele Werte");
                var arr = new float[StatCount];
                for (int j = 0; j < k; j++)
                {
                    float v = r.ReadSingle();
                    if (j < StatCount) arr[j] = v;
                }
                values[id] = arr;
            }
            _values.Clear();
            foreach (var kv in values) _values[kv.Key] = kv.Value;
            _names.Clear();
            foreach (var kv in names) _names[kv.Key] = kv.Value;
            try { ApplyFlags(flags); } catch (Exception e) { Plugin.Log.LogDebug("Schalter übernehmen: " + e.Message); }
            Received++;
        }

        // ================================================================ Anzeige

        internal sealed class Row
        {
            public ulong Id;
            public string Name;
            public Color Color;
            public float[] Values;
        }

        /// <summary>Alle Spieler der Sitzung (auch ohne Zahlen), sortiert nach Getötet.</summary>
        public List<Row> Rows()
        {
            var ids = new HashSet<ulong>(_values.Keys) { Me };
            try
            {
                foreach (var p in _session.PlayerColors.KnownPlayers())
                    ids.Add(p);
            }
            catch { }
            var list = new List<Row>();
            foreach (var id in ids)
            {
                if (id == 0UL) continue;
                string name = _names.TryGetValue(id, out var nm) && !string.IsNullOrEmpty(nm) ? nm : _session.PlayerColors.NameOf(id);
                list.Add(new Row
                {
                    Id = id,
                    Name = string.IsNullOrEmpty(name) ? id.ToString(CultureInfo.InvariantCulture) : name,
                    Color = _session.PlayerColors.ColorOf(id),
                    Values = _values.TryGetValue(id, out var v) ? v : new float[StatCount],
                });
            }
            return list.OrderByDescending(r => r.Values[(int)Stat.Kills]).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public IEnumerable<string> StatusLines()
        {
            foreach (var r in Rows())
                yield return "stats " + r.Id + " " + string.Join(" ", r.Values.Select((v, i) => Labels[i].Split(' ')[0] + "=" + Mathf.RoundToInt(v)));
            yield return $"statsReceived={Received} flags={Flags()}";
        }
    }
}
