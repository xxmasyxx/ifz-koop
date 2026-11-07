using System.Collections.Generic;
using System.Linq;
using Controllers.Time;
using IfzCoop.Game;
using IfzCoop.Net;
using UI.Clock;

namespace IfzCoop.Versus
{
    /// <summary>
    /// Abstimmung über Pause und Vorspulen (Wunsch des Nutzers, 6.11.2026, nur Versus):
    /// - Vorspulen (Mittel/Schnell) und Pause gelten erst, wenn ALLE Spieler sie gewählt haben. Bis dahin steht oben
    ///   „1 von 3 wollen vorspulen“. Wählen alle verschieden schnell, gilt das langsamste.
    /// - Play (normales Tempo) von einem Spieler: alle sofort wieder normal, alle Stimmen gelöscht. Auch aus der Pause heraus.
    /// - Leertaste: läuft das Spiel, ist es eine Stimme für Pause; steht es, ist es Play.
    /// Gezählt wird nur beim Host (Platz 0 = Host, dazu jeder Gast mit geladenem Spielstand). Gäste schicken SpeedRequest
    /// (Format wie im Koop: byte 0 = Tempo setzen + int TimeSpeedType, 1 = Leertaste). Erst ab Phase „Running“ (alle HQs
    /// stehen); davor gilt wie bisher nur der Host.
    /// </summary>
    internal sealed class VsVote
    {
        private readonly VersusSession _vs;
        private readonly Dictionary<byte, TimeSpeedType> _votes = new Dictionary<byte, TimeSpeedType>();
        private string _lastSent;
        private bool _pending;   // Einigung da, aber das Spiel hat das Tempo gesperrt (z. B. Funk in der Einführung)
        private float _nextTick;

        /// <summary>true, solange der Mod das abgestimmte Tempo beim Host anwendet (Haken lassen dann durch).</summary>
        public static bool Applying;

        public int VotesCast { get; private set; }
        public int Applied { get; private set; }

        /// <summary>Anzeige oben in der Mitte (Host rechnet, Gäste bekommen den Text).</summary>
        public string Text { get; set; } = "";

        public VsVote(VersusSession vs)
        {
            _vs = vs;
        }

        public static bool Enabled =>
            VersusSession.Active && VersusSession.Instance.Phase == VsPhase.Running;

        public void Reset()
        {
            _votes.Clear();
            Text = "";
            _lastSent = null;
            _pending = false;
        }

        // ------------------------------------------------------------ Host

        /// <summary>Stimme eines Spielers (Platz). Normal = Play für alle.</summary>
        public void Cast(byte slot, TimeSpeedType type)
        {
            VotesCast++;
            if (type == TimeSpeedType.Normal)
            {
                _votes.Clear();
                _pending = false;
                Apply(TimeSpeedType.Normal);
                Plugin.Log.LogInfo($"IFZVS Abstimmung: Platz {slot} drückt Play, alle normal");
            }
            else
            {
                _votes[slot] = type;
                Plugin.Log.LogInfo($"IFZVS Abstimmung: Platz {slot} will {type}");
                Evaluate();
            }
            Publish();
        }

        /// <summary>Leertaste eines Spielers: steht das Spiel, Play; sonst Stimme für Pause.</summary>
        public void Toggle(byte slot)
        {
            Cast(slot, TimeController.SpeedMultiplier < 0.01f ? TimeSpeedType.Normal : TimeSpeedType.Pause);
        }

        /// <summary>Spieler kam dazu oder ging: Stimmen Abwesender löschen, neu auswerten.</summary>
        public void OnPlayersChanged()
        {
            var present = new HashSet<byte>(Participants());
            foreach (var slot in _votes.Keys.ToList())
                if (!present.Contains(slot))
                    _votes.Remove(slot);
            Evaluate();
            Publish();
        }

        private List<byte> Participants()
        {
            var list = new List<byte> { 0 };
            list.AddRange(_vs.LoadedGuestSlots());
            return list;
        }

        private void Evaluate()
        {
            _pending = false;
            var players = Participants();
            if (players.Count == 0 || players.Any(p => !_votes.ContainsKey(p)))
                return;
            var wanted = players.Select(p => _votes[p]).ToList();
            TimeSpeedType result;
            if (wanted.All(t => t == TimeSpeedType.Pause))
                result = TimeSpeedType.Pause;
            else if (wanted.All(t => t == TimeSpeedType.Medium || t == TimeSpeedType.Fast))
                result = wanted.Contains(TimeSpeedType.Medium) ? TimeSpeedType.Medium : TimeSpeedType.Fast; // das langsamste
            else
                return; // Pause und Vorspulen gemischt: keine Einigung
            Apply(result);
            float m = TimeController.SpeedMultiplier;
            _pending = result == TimeSpeedType.Pause ? m > 0.01f : m <= 1.01f;
        }

        /// <summary>Host, jedes Bild: hat das Spiel eine Einigung wegen einer Tempo-Sperre abgelehnt, 1×/s erneut versuchen.</summary>
        public void Tick()
        {
            if (!_pending)
                return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _nextTick)
                return;
            _nextTick = now + 1f;
            Evaluate();
            Publish();
        }

        private void Apply(TimeSpeedType type)
        {
            var setter = GameAccess.SpeedSetter;
            if (setter == null)
                return;
            Applying = true;
            try
            {
                setter.TrySetTimeSpeed(type);
            }
            finally
            {
                Applying = false;
            }
            Applied++;
        }

        /// <summary>Text „x von n wollen …“ bilden und an alle schicken, wenn er sich geändert hat.</summary>
        private void Publish()
        {
            var players = Participants();
            int n = players.Count;
            int pause = players.Count(p => _votes.TryGetValue(p, out var t) && t == TimeSpeedType.Pause);
            int fast = players.Count(p => _votes.TryGetValue(p, out var t) && (t == TimeSpeedType.Medium || t == TimeSpeedType.Fast));
            var parts = new List<string>();
            bool paused = TimeController.SpeedMultiplier < 0.01f;
            bool fastNow = TimeController.SpeedMultiplier > 1.01f;
            if (pause > 0 && !(pause == n && paused))
                parts.Add($"{pause} von {n} wollen pausieren");
            if (fast > 0 && !(fast == n && fastNow))
                parts.Add($"{fast} von {n} wollen vorspulen");
            Text = parts.Count == 0 ? "" : string.Join(" · ", parts) + "  (Play: alle wieder normal)";
            if (Text == _lastSent)
                return;
            _lastSent = Text;
            _vs.BroadcastLoaded(VsNet.Build(Msg.VsVoteStatus, w => w.Write(Text)));
        }

        public string StatusLine()
        {
            string votes = string.Join(",", _votes.Select(kv => kv.Key + ":" + kv.Value));
            return $"vs.vote votes={(votes.Length == 0 ? "-" : votes)} cast={VotesCast} applied={Applied} pending={(_pending ? 1 : 0)} text={(Text.Length == 0 ? "-" : Text.Replace(' ', '_'))}";
        }
    }
}
