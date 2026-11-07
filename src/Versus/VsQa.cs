using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Controllers;
using Controllers.Time;
using IfzCoop.Game;
using UnityEngine;
using ZenjectExtensions.Installers;

namespace IfzCoop.Versus
{
    /// <summary>
    /// Testmodus für Versus (VERSUS-TECHNIK 10.2).
    /// Startoptionen:
    ///   -ifzvs-host-direct &lt;Port&gt;   hostet Versus direkt (TCP), sobald ein neues Spiel in der HQ-Wahl steht
    ///   -ifzvs-host                   hostet Versus über eine Steam-Lobby (ebenso)
    ///   -ifzvs-join &lt;IP:Port&gt;       tritt direkt bei (auch aus dem Hauptmenü, bis zu 30 Versuche)
    /// Befehle (BepInEx\ifzcoop-cmd.txt, über QaDriver): „vs …“, siehe Execute.
    /// Statuszeilen „vs.*“ in BepInEx\ifzcoop-status.txt.
    /// </summary>
    internal static class VsQa
    {
        private static readonly int HostPort;
        private static readonly bool HostSteam;
        private static readonly string JoinHost;
        private static readonly int JoinPort;

        private static bool _hostDone;
        private static float _selectingSince = -1f;
        private static int _joinAttempts;
        private static float _nextJoin;

        public static bool HasArgs => HostPort > 0 || HostSteam || JoinHost != null;

        static VsQa()
        {
            var args = Environment.GetCommandLineArgs();
            int.TryParse(QaDriver.Arg(args, "-ifzvs-host-direct"), out HostPort);
            foreach (var a in args)
                if (string.Equals(a, "-ifzvs-host", StringComparison.OrdinalIgnoreCase)) HostSteam = true;
            var join = QaDriver.Arg(args, "-ifzvs-join");
            if (!string.IsNullOrEmpty(join))
            {
                var parts = join.Split(':');
                JoinHost = parts[0];
                if (parts.Length < 2 || !int.TryParse(parts[1], out JoinPort))
                    JoinPort = VsProtocol.DefaultPort;
            }
        }

        /// <summary>Jedes Bild (Plugin.Update): automatisch hosten bzw. beitreten.</summary>
        public static void Update()
        {
            var vs = VersusSession.Instance;
            if (vs == null || !HasArgs)
                return;
            float now = Time.realtimeSinceStartup;
            Application.runInBackground = true;

            if ((HostPort > 0 || HostSteam) && !_hostDone && !VersusSession.Active)
            {
                bool selecting = vs.CanHost(out _);
                if (!selecting)
                    _selectingSince = -1f;
                else if (_selectingSince < 0f)
                    _selectingSince = now;
                else if (now - _selectingSince > 5f && (HostPort > 0 || (Plugin.Instance?.Session?.SteamReady ?? false)))
                {
                    _hostDone = true;
                    if (HostPort > 0) vs.StartHostDirect(HostPort);
                    else vs.StartHost();
                    Plugin.Log.LogInfo("IFZVS QA host: " + vs.Status);
                }
            }

            bool inMenu = ScenesManager.IsMainSceneActive && !ScenesManager.IsSceneSwitching;
            if (JoinHost != null && !VersusSession.Active && _joinAttempts < 30 && now >= _nextJoin && now > 15f
                && (inMenu || GameAccess.IsGameStarted))
            {
                _joinAttempts++;
                _nextJoin = now + 10f;
                Plugin.Log.LogInfo($"IFZVS QA Beitritt Versuch {_joinAttempts}");
                vs.JoinDirect(JoinHost, JoinPort);
            }
        }

        /// <summary>
        /// „vs status“ · „vs host [Port]“ · „vs steamhost“ · „vs join &lt;IP:Port&gt;“ · „vs joinlobby &lt;ID&gt;“ ·
        /// „vs settings key=value …“ (Host, siehe VsSettings.TrySet) · „vs team &lt;Platz&gt; &lt;Team&gt;“ · „vs start [tile]“ ·
        /// „vs hq &lt;dx&gt; &lt;dz&gt;“ · „vs force“ · „vs pause“ (Leertaste) · „vs speed 0|1|2|3“ · „vs leave“.
        /// </summary>
        public static string Execute(string[] a)
        {
            var vs = VersusSession.Instance;
            if (vs == null)
                return "kein Versus";
            var inv = CultureInfo.InvariantCulture;
            string sub = a.Length > 1 ? a[1].ToLowerInvariant() : "status";
            switch (sub)
            {
                case "status":
                    return $"role={vs.Role} slot={vs.MySlot} phase={vs.Phase} state={vs.GuestState} status={vs.Status}";

                case "host":
                    vs.StartHostDirect(a.Length > 2 ? int.Parse(a[2], inv) : VsProtocol.DefaultPort);
                    return vs.Status;

                case "steamhost":
                    vs.StartHost();
                    return vs.Status;

                case "join":
                {
                    var parts = (a.Length > 2 ? a[2] : "127.0.0.1").Split(':');
                    vs.JoinDirect(parts[0], parts.Length > 1 ? int.Parse(parts[1], inv) : VsProtocol.DefaultPort);
                    return vs.Status;
                }

                case "joinlobby":
                    vs.JoinLobbyById(ulong.Parse(a[2], inv));
                    return "beitreten angefragt";

                case "settings":
                {
                    if (!vs.SettingsEditable)
                        return "nicht änderbar (nur Host vor dem Start): " + vs.Settings.Compact();
                    var errors = new List<string>();
                    for (int i = 2; i < a.Length; i++)
                    {
                        var kv = a[i].Split('=');
                        if (kv.Length != 2 || !vs.Settings.TrySet(kv[0], kv[1], out var err))
                            errors.Add(a[i]);
                    }
                    vs.MarkSettingsChanged();
                    return (errors.Count > 0 ? "Fehler: " + string.Join(",", errors) + " | " : "") + vs.Settings.Compact();
                }

                case "team":
                    vs.SetTeam(int.Parse(a[2], inv), int.Parse(a[3], inv));
                    return vs.Roster.Compact();

                case "start":
                    vs.StartGame(a.Length > 2 && a[2] == "tile");
                    return "phase=" + vs.Phase + " " + vs.Status;

                case "hq":
                {
                    float dx = a.Length > 2 ? float.Parse(a[2], inv) : 0f;
                    float dz = a.Length > 3 ? float.Parse(a[3], inv) : 0f;
                    return vs.Start.QaChooseHq(dx, dz);
                }

                case "force":
                    vs.Start.ForceStart = true;
                    return "erzwungen";

                case "pause":
                    GameAccess.SpeedSetter?.ChangeGameplayPauseState();
                    return "speed=" + TimeController.SpeedMultiplier.ToString("0.##", inv);

                case "speed":
                {
                    int s = int.Parse(a[2], inv);
                    var type = s <= 0 ? TimeSpeedType.Pause : s == 1 ? TimeSpeedType.Normal : s == 2 ? TimeSpeedType.Medium : TimeSpeedType.Fast;
                    GameAccess.SpeedSetter?.TrySetTimeSpeed(type);
                    return type + " speed=" + TimeController.SpeedMultiplier.ToString("0.##", inv);
                }

                case "leave":
                    vs.Leave("Testbefehl");
                    return "verlassen";

                case "ally":
                    vs.Diplomacy.Request(byte.Parse(a[2], inv), 0);
                    return vs.Diplomacy.Compact();

                case "allyend":
                    vs.Diplomacy.Request(byte.Parse(a[2], inv), 1);
                    return vs.Diplomacy.Compact();

                case "allyanswer":
                    vs.Diplomacy.Answer(byte.Parse(a[2], inv), a.Length > 3 && a[3] == "1");
                    return vs.Diplomacy.Compact();

                case "allyui":
                    VsDiplomacyUi.OpenFor(int.Parse(a[2], inv));
                    return "Fenster für Platz " + a[2];
            }
            return "unbekannt: vs " + sub;
        }

        /// <summary>Zeilen für BepInEx\ifzcoop-status.txt (QaDriver.WriteStatus).</summary>
        public static IEnumerable<string> StatusLines()
        {
            var vs = VersusSession.Instance;
            if (vs == null)
                yield break;
            var inv = CultureInfo.InvariantCulture;
            var koop = Plugin.Instance?.Session;
            yield return $"vs.role={vs.Role} slot={vs.MySlot} peers={vs.WelcomedGuestCount} loaded={vs.LoadedGuestCount} direct={vs.Direct} koopRole={(koop != null ? koop.Role.ToString() : "-")}";
            yield return $"vs.phase={(int)vs.Phase} {vs.Phase} state={vs.GuestState} startMode={vs.StartMode}";
            yield return "vs.status=" + vs.Status;
            if (!VersusSession.Active)
                yield break;
            yield return "vs.settings=" + vs.Settings.Compact();
            yield return "vs.players=" + vs.Roster.Compact();
            yield return "vs.diplomacy " + vs.Diplomacy.Compact();

            var st = vs.Start;
            var hq = HqController.MainHeadquarter;
            yield return "vs.hq=" + (st.MyHqConfirmed ? st.MyHqId + " " + st.MyHqPos.x.ToString("0", inv) + "," + st.MyHqPos.z.ToString("0", inv) : "keins")
                         + " mainHq=" + (hq != null ? hq.GetId() : "keins")
                         + $" awaiting={(st.AwaitingAnswer ? 1 : 0)} hostPending={(st.HostPending ? 1 : 0)} rejections={st.Rejections}";
            if (vs.Role == VsRole.Host)
            {
                var sb = new StringBuilder();
                if (st.MyHqConfirmed) sb.Append("0:").Append(st.MyHqId);
                foreach (var p in vs.Peers)
                    if (p.HqChosen)
                    {
                        if (sb.Length > 0) sb.Append(',');
                        sb.Append(p.Slot).Append(':').Append(p.HqId);
                    }
                yield return "vs.hqs=" + (sb.Length == 0 ? "-" : sb.ToString()) + $" chosen={st.ChosenCount}/{st.GuestTotal} safeRemoved={st.SafeZoneRemoved} hideoutsMoved={st.HideoutsMoved}";
                foreach (var p in vs.Peers)
                    yield return $"vs.peer {p.Slot} {p.Name.Replace(' ', '_')} loaded={(p.Loaded ? 1 : 0)} hq={(p.HqChosen ? p.HqId.ToString(inv) : "-")} rtt={p.RttMs.ToString("0", inv)}"
                                 + (p.PopKnown ? $" applied={(p.PopApplied ? 1 : 0)} workers={p.PopWorkers} children={p.PopChildren} squads={p.PopSquads} soldiers={p.PopSoldiers} res={p.PopResources}" : " pop=-");
            }

            VsStart.CountPopulation(out int workers, out int children, out int squads, out int soldiers);
            yield return $"vs.start applied={(st.Applied ? 1 : 0)} {st.AppliedSummary}";
            yield return $"vs.pop workers={workers} children={children} squads={squads} soldiers={soldiers} res={VsStart.ResourceLine(vs.Settings)}";
            yield return $"vs.ids last={VsStart.CurrentLastId()} base={vs.Start.IdBase} min={(uint)vs.MySlot * VsProtocol.IdOffsetPerSlot}";
            var c = MapEssentials.Map.Bound.center;
            yield return $"vs.map tile={SaveHandler.SelectedTilesCoordinates} center={c.x.ToString("0", inv)},{c.y.ToString("0", inv)}";
            yield return $"vs.time day={TimeController.Day} hour={TimeController.Hour.ToString("0.000", inv)} speed={TimeController.SpeedMultiplier.ToString("0.##", inv)}"
                         + $" hostSpeed={vs.HostSpeed.ToString("0.##", inv)} drift={vs.Clock.DriftSeconds.ToString("0.00", inv)} corrections={vs.Clock.Corrections} speedApplied={vs.Clock.SpeedApplied}";
            yield return $"vs.pause denied={VsPatches.PauseDenied} suppressed={VsPatches.PausesSuppressed}";
            yield return vs.Vote.StatusLine();
            yield return $"vs.weather {VsClock.WeatherLine()} sent={vs.Clock.WeatherSent} applied={vs.Clock.WeatherApplied}";
            yield return $"vs.hooks applied={VsPatches.Applied} missing={VsPatches.Missing}";
            yield return $"vs.bytes up={VsNet.BytesUp} down={VsNet.BytesDown}";
        }
    }
}
