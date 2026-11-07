using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using IfzCoop.Net;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Spielerfarben. Jeder Spieler wählt seine Farbe selbst (Mehrspieler-Fenster „Meine Farbe“; gespeichert in der Konfiguration
    /// [Allgemein] MeineFarbe als Hex, leer = automatisch). Grün ist nicht erlaubt: Grün heißt im Spiel „freier Trupp“,
    /// Grau „untätig“. Ohne eigene Wahl bekommt jeder eine gut unterscheidbare Standardfarbe.
    ///
    /// Host: kennt alle Spieler (sich selbst und die Gäste), verteilt Steam-ID, Name und Farbe an alle (Msg 81),
    ///       bei Änderung und alle 10 s. Gäste melden ihre gewählte Farbe (Msg 82, Op 0; Op 1 nutzt SquadViewSync).
    /// Gast: übernimmt die Liste; meldet seine Wahl, solange der Host sie noch nicht kennt.
    /// </summary>
    internal sealed class PlayerColorSync
    {
        private const float HostInterval = 1f;
        private const float ForceInterval = 10f;
        private const float RequestCooldown = 2f;
        private const int MaxPlayers = 16;

        /// <summary>Standardfarben (nie grün, nie grau): blau, orange, lila, gelb, pink, rot, hellblau, braun.</summary>
        public static readonly Color32[] Defaults =
        {
            new Color32(64, 153, 255, 255),
            new Color32(255, 140, 26, 255),
            new Color32(191, 102, 255, 255),
            new Color32(255, 225, 51, 255),
            new Color32(255, 77, 153, 255),
            new Color32(235, 51, 51, 255),
            new Color32(51, 217, 255, 255),
            new Color32(166, 102, 51, 255),
        };

        internal sealed class Player
        {
            public ulong Id;
            public string Name;
            public Color32 Color;
            public bool Chosen;
        }

        private readonly Session _session;
        private readonly List<Player> _players = new List<Player>();
        // Host: gewählte Farben der Gäste
        private readonly Dictionary<ulong, Color32?> _requested = new Dictionary<ulong, Color32?>();
        private string _sentSig;
        private float _nextHost;
        private float _nextForce;
        private float _nextRequest;

        public int Received { get; private set; }
        public int RequestsSent { get; private set; }

        public PlayerColorSync(Session session)
        {
            _session = session;
        }

        public IReadOnlyList<Player> Players => _players;

        public void ResendAll() => _sentSig = null;

        public void Reset()
        {
            Received = 0;
            _players.Clear();
            _requested.Clear();
            _sentSig = null;
        }

        // ================================================================ eigene Farbe

        /// <summary>Eigene gewählte Farbe aus der Konfiguration (null = automatisch oder ungültig).</summary>
        public static Color32? ConfiguredColor()
        {
            var hex = Plugin.MyColorSetting?.Value;
            if (string.IsNullOrWhiteSpace(hex))
                return null;
            if (!ColorUtility.TryParseHtmlString("#" + hex.Trim().TrimStart('#'), out var c))
                return null;
            return IsAllowed(c, out _) ? (Color32?)(Color32)c : null;
        }

        /// <summary>Eigene Farbe setzen (null = automatisch). false, wenn sie nicht erlaubt ist.</summary>
        public bool SetMyColor(Color? color, out string reason)
        {
            reason = null;
            if (color.HasValue && !IsAllowed(color.Value, out reason))
                return false;
            Plugin.MyColorSetting.Value = color.HasValue ? ColorUtility.ToHtmlStringRGB(color.Value) : string.Empty;
            _sentSig = null;
            _nextRequest = 0f;
            return true;
        }

        /// <summary>Grün (Farbton etwa 75°–165°) ist „frei“, zu blasse oder dunkle Farben sehen aus wie „untätig“ (grau).</summary>
        public static bool IsAllowed(Color c, out string reason)
        {
            Color.RGBToHSV(c, out float h, out float s, out float v);
            float deg = h * 360f;
            if (s < 0.35f || v < 0.45f)
            {
                reason = "zu blass oder zu dunkel (sieht aus wie ein untätiger, grauer Trupp)";
                return false;
            }
            if (deg >= 75f && deg <= 165f)
            {
                reason = "Grün ist für freie Trupps reserviert";
                return false;
            }
            reason = null;
            return true;
        }

        public Color MyColor => ColorOf(_session.MyId.m_SteamID);

        /// <summary>Farbe eines Spielers; unbekannt: Standardfarbe nach Steam-ID.</summary>
        public Color ColorOf(ulong id) => ColorOf(id, Defaults[(int)(id % (ulong)Defaults.Length)]);

        public Color ColorOf(ulong id, Color fallback)
        {
            foreach (var p in _players)
                if (p.Id == id) return p.Color;
            if (id == _session.MyId.m_SteamID)
            {
                var mine = ConfiguredColor();
                if (mine.HasValue) return mine.Value;
            }
            return fallback;
        }

        /// <summary>Alle Spieler aus der Farbliste (Host und Gäste der Sitzung).</summary>
        public IEnumerable<ulong> KnownPlayers()
        {
            foreach (var p in _players)
                yield return p.Id;
        }

        public string NameOf(ulong id)
        {
            foreach (var p in _players)
                if (p.Id == id) return p.Name;
            if (id == _session.MyId.m_SteamID)
                return _session.MyName;
            foreach (var r in _session.Presence.Remotes)
                if (r.SteamId == id) return r.Name;
            return "?";
        }

        // ================================================================ Host

        public void HostUpdate()
        {
            if (_session.Role != Role.Host)
                return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextHost)
                return;
            _nextHost = now + HostInterval;

            // Spieler: Host zuerst, dann die Gäste in Beitrittsreihenfolge
            var list = new List<Player>
            {
                new Player { Id = _session.MyId.m_SteamID, Name = _session.MyName, Chosen = false },
            };
            var mine = ConfiguredColor();
            if (mine.HasValue)
            {
                list[0].Color = mine.Value;
                list[0].Chosen = true;
            }
            var alive = new HashSet<ulong>();
            foreach (var peer in _session.Peers)
            {
                if (list.Count >= MaxPlayers)
                    break;
                ulong id = peer.Id.m_SteamID;
                alive.Add(id);
                var p = new Player { Id = id, Name = peer.Name ?? "?" };
                if (_requested.TryGetValue(id, out var want) && want.HasValue)
                {
                    p.Color = want.Value;
                    p.Chosen = true;
                }
                list.Add(p);
            }
            foreach (var id in new List<ulong>(_requested.Keys))
                if (!alive.Contains(id)) _requested.Remove(id);

            // Standardfarben: die erste, die keiner (gewählt oder schon vergeben) ähnlich hat
            var used = new List<Color32>();
            foreach (var p in list)
                if (p.Chosen) used.Add(p.Color);
            foreach (var p in list)
            {
                if (p.Chosen)
                    continue;
                Color32 pick = Defaults[0];
                bool found = false;
                foreach (var d in Defaults)
                {
                    bool clash = false;
                    foreach (var u in used)
                        if (Distance(u, d) < 90) { clash = true; break; }
                    if (!clash) { pick = d; found = true; break; }
                }
                if (!found)
                    pick = Defaults[used.Count % Defaults.Length];
                p.Color = pick;
                used.Add(pick);
            }

            _players.Clear();
            _players.AddRange(list);

            // auch an Gäste, die noch in der Lobby im Hauptmenü warten (Kacheln zeigen schon die Farben)
            if (list.Count < 2)
                return;
            string sig = Sig(list);
            if (sig == _sentSig && now < _nextForce)
                return;
            _sentSig = sig;
            _nextForce = now + ForceInterval;
            _session.BroadcastToLobby(Protocol.Build(Msg.PlayerColors, w =>
            {
                w.Write(list.Count);
                foreach (var p in list)
                {
                    w.Write(p.Id);
                    w.Write(p.Name ?? "?");
                    w.Write(p.Color.r);
                    w.Write(p.Color.g);
                    w.Write(p.Color.b);
                    w.Write(p.Chosen);
                }
            }));
        }

        /// <summary>Msg 82 vom Gast: Op 0 = gewählte Farbe, Op 1 = Trupps, an denen er etwas geändert hat (SquadViewSync).</summary>
        public void HostOnRequest(Peer from, BinaryReader r)
        {
            byte op = r.ReadByte();
            if (op == 1)
            {
                if (!from.AwaitingLoad)
                    _session.SquadViews?.HostOnChanged(from, r);
                return;
            }
            if (op != 0)
                return;
            bool chosen = r.ReadBoolean();
            var c = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), 255);
            if (chosen && !IsAllowed(c, out var reason))
            {
                Plugin.Log.LogInfo($"IFZCOOP Farbe von {from.Name} abgelehnt: {reason}");
                chosen = false;
            }
            _requested[from.Id.m_SteamID] = chosen ? (Color32?)c : null;
            _nextHost = 0f;
        }

        // ================================================================ Gast

        public void GuestOnState(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > MaxPlayers)
                throw new InvalidDataException("zu viele Spieler");
            var list = new List<Player>(n);
            for (int i = 0; i < n; i++)
            {
                var p = new Player { Id = r.ReadUInt64(), Name = r.ReadString() };
                p.Color = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), 255);
                p.Chosen = r.ReadBoolean();
                list.Add(p);
            }
            _players.Clear();
            _players.AddRange(list);
            Received++;
        }

        /// <summary>Gast, jeden Frame: eigene Wahl melden, solange die Liste des Hosts sie nicht zeigt.</summary>
        public void GuestUpdate()
        {
            // schon in der Lobby im Hauptmenü melden, sobald der Host die erste Liste geschickt hat
            if (_session.Role != Role.Guest || Received == 0)
                return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextRequest)
                return;
            _nextRequest = now + RequestCooldown;
            var mine = ConfiguredColor();
            Player entry = null;
            foreach (var p in _players)
                if (p.Id == _session.MyId.m_SteamID) entry = p;
            bool ok = entry != null && entry.Chosen == mine.HasValue
                      && (!mine.HasValue || Distance(entry.Color, mine.Value) == 0);
            if (ok)
                return;
            _session.SendToHost(Protocol.Build(Msg.PlayerRequest, w =>
            {
                w.Write((byte)0);
                var c = mine ?? new Color32(0, 0, 0, 255);
                w.Write(mine.HasValue);
                w.Write(c.r);
                w.Write(c.g);
                w.Write(c.b);
            }));
            RequestsSent++;
        }

        // ================================================================ Hilfen

        private static int Distance(Color32 a, Color32 b) => Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b);

        private static string Sig(List<Player> list)
        {
            var sb = new StringBuilder();
            foreach (var p in list)
                sb.Append(p.Id).Append(':').Append(p.Name).Append(':').Append(Hex(p.Color)).Append(p.Chosen ? '+' : '-').Append('|');
            return sb.ToString();
        }

        public static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        public IEnumerable<string> StatusLines()
        {
            ulong me = _session.MyId.m_SteamID;
            yield return $"playerColors players={_players.Count} received={Received} requestsSent={RequestsSent} mine={Hex(MyColor)} config={(Plugin.MyColorSetting?.Value ?? "")}";
            foreach (var p in _players)
                yield return $"pcolor {p.Id} name={(p.Name ?? "?").Replace(' ', '_')} hex={Hex(p.Color)} chosen={(p.Chosen ? 1 : 0)} me={(p.Id == me ? 1 : 0)}";
        }

        /// <summary>Testbefehl "pcolor &lt;Hex|auto&gt;".</summary>
        public string Qa(string[] a)
        {
            if (a.Length < 2 || a[1] == "auto")
            {
                SetMyColor(null, out _);
                return "Farbe automatisch";
            }
            if (!ColorUtility.TryParseHtmlString("#" + a[1].TrimStart('#'), out var c))
                return "keine Farbe: " + a[1];
            return SetMyColor(c, out var reason) ? "Farbe " + Hex(c) : "abgelehnt: " + reason;
        }
    }
}
