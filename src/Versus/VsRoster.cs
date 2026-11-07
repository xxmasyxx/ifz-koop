using System.Collections.Generic;
using System.IO;
using System.Text;
using IfzCoop.Sync;
using UnityEngine;

namespace IfzCoop.Versus
{
    /// <summary>Zustand eines Spielers in der Startphase (für Fenster und Statuszeilen).</summary>
    internal enum VsPlayerState : byte { Lobby = 0, Loading = 1, ChoosingHq = 2, HqChosen = 3, InGame = 4 }

    internal sealed class VsPlayer
    {
        public byte Slot;
        public ulong Id;
        public string Name = "?";
        public Color32 Color;
        public byte Team;
        public bool Alive = true;
        public VsPlayerState State;
    }

    /// <summary>
    /// Spielerliste (Plätze 0–7, der Host ist Platz 0). Der Host führt sie und schickt sie bei jeder Änderung an alle.
    /// Wo die HQs stehen, steht bewusst NICHT darin (heimliche Wahl, das weiß nur der Host).
    /// </summary>
    internal sealed class VsRoster
    {
        private readonly List<VsPlayer> _players = new List<VsPlayer>();

        public IReadOnlyList<VsPlayer> Players => _players;

        public int Count => _players.Count;

        public VsPlayer BySlot(int slot)
        {
            foreach (var p in _players)
                if (p.Slot == slot) return p;
            return null;
        }

        public VsPlayer ById(ulong id)
        {
            foreach (var p in _players)
                if (p.Id == id) return p;
            return null;
        }

        public void Clear() => _players.Clear();

        /// <summary>Freier Platz 1–7 (0 = Host), -1 = voll.</summary>
        public int FreeSlot(int max)
        {
            for (int s = 1; s < Mathf.Min(max, VsProtocol.MaxPlayers); s++)
                if (BySlot(s) == null) return s;
            return -1;
        }

        public VsPlayer Add(byte slot, ulong id, string name, Color32? wish)
        {
            var p = BySlot(slot) ?? new VsPlayer { Slot = slot };
            if (!_players.Contains(p))
                _players.Add(p);
            p.Id = id;
            p.Name = string.IsNullOrEmpty(name) ? "Spieler " + (slot + 1) : name;
            p.Color = PickColor(slot, wish);
            p.Team = slot;
            p.Alive = true;
            _players.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            return p;
        }

        public void Remove(int slot)
        {
            _players.RemoveAll(p => p.Slot == slot);
        }

        /// <summary>Gewünschte Farbe, wenn sie erlaubt und noch frei ist, sonst die Standardfarbe des Platzes.</summary>
        private Color32 PickColor(int slot, Color32? wish)
        {
            if (wish.HasValue && PlayerColorSync.IsAllowed(wish.Value, out _))
            {
                bool used = false;
                foreach (var p in _players)
                    if (p.Slot != slot && Near(p.Color, wish.Value)) used = true;
                if (!used)
                    return wish.Value;
            }
            var defaults = PlayerColorSync.Defaults;
            for (int i = 0; i < defaults.Length; i++)
            {
                var c = defaults[(slot + i) % defaults.Length];
                bool used = false;
                foreach (var p in _players)
                    if (p.Slot != slot && Near(p.Color, c)) used = true;
                if (!used)
                    return c;
            }
            return defaults[slot % defaults.Length];
        }

        private static bool Near(Color32 a, Color32 b)
        {
            return Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < 60;
        }

        public void Write(BinaryWriter w)
        {
            w.Write((byte)_players.Count);
            foreach (var p in _players)
            {
                w.Write(p.Slot);
                w.Write(p.Id);
                w.Write(p.Name ?? "?");
                w.Write(p.Color.r);
                w.Write(p.Color.g);
                w.Write(p.Color.b);
                w.Write(p.Team);
                w.Write(p.Alive);
                w.Write((byte)p.State);
            }
        }

        public void Read(BinaryReader r)
        {
            _players.Clear();
            int n = r.ReadByte();
            for (int i = 0; i < n; i++)
            {
                var p = new VsPlayer
                {
                    Slot = r.ReadByte(),
                    Id = r.ReadUInt64(),
                    Name = r.ReadString(),
                };
                byte cr = r.ReadByte(), cg = r.ReadByte(), cb = r.ReadByte();
                p.Color = new Color32(cr, cg, cb, 255);
                p.Team = r.ReadByte();
                p.Alive = r.ReadBoolean();
                p.State = (VsPlayerState)r.ReadByte();
                _players.Add(p);
            }
        }

        public string Signature()
        {
            var sb = new StringBuilder();
            foreach (var p in _players)
                sb.Append(p.Slot).Append(':').Append(p.Id).Append(':').Append(p.Name).Append(':')
                  .Append(p.Color.r).Append(',').Append(p.Color.g).Append(',').Append(p.Color.b).Append(':')
                  .Append(p.Team).Append(':').Append(p.Alive).Append(':').Append((int)p.State).Append('|');
            return sb.ToString();
        }

        /// <summary>Statuszeile: "0:Host:t0:InGame 1:Gast:t1:HqChosen".</summary>
        public string Compact()
        {
            var sb = new StringBuilder();
            foreach (var p in _players)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(p.Slot).Append(':').Append((p.Name ?? "?").Replace(' ', '_')).Append(":t").Append(p.Team).Append(':').Append(p.State);
            }
            return sb.Length == 0 ? "-" : sb.ToString();
        }

        public static string StateLabel(VsPlayerState s)
        {
            switch (s)
            {
                case VsPlayerState.Lobby: return "in der Lobby";
                case VsPlayerState.Loading: return "lädt den Spielstand";
                case VsPlayerState.ChoosingHq: return "wählt sein HQ";
                case VsPlayerState.HqChosen: return "HQ gewählt";
                case VsPlayerState.InGame: return "im Spiel";
            }
            return s.ToString();
        }
    }
}
