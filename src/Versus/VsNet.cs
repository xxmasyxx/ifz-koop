using System;
using System.Collections.Generic;
using System.IO;
using IfzCoop.Net;

namespace IfzCoop.Versus
{
    /// <summary>
    /// Versus-Protokoll. Eigenständig vom Koop (Protocol.Version): Versus läuft auf einem eigenen Steam-Kanal bzw.
    /// TCP-Port, eine Koop-Lobby und eine Versus-Lobby werden nie verwechselt (Lobby-Schlüssel „ifzversus“).
    /// Nachrichten 1–13 (Hello bis ResyncRequest) und Notice behalten ihr Format, dazu die Nummern ab 100 (VERSUS-TECHNIK 9.2).
    /// </summary>
    internal static class VsProtocol
    {
        /// <summary>Bei jeder inkompatiblen Änderung am Versus-Teil erhöhen (unabhängig von Protocol.Version).</summary>
        public const int Version = 2; // 2: Abstimmung über Pause/Vorspulen (SpeedRequest, VsVoteStatus)

        public const string LobbyKey = "ifzversus";

        /// <summary>Steam-Kanal der Versus-Sitzung (Koop = 0).</summary>
        public const int SteamChannel = 1;

        /// <summary>TCP-Port der Direktverbindung (Koop = 27120).</summary>
        public const int DefaultPort = 27121;

        public const int MaxPlayers = 8;

        /// <summary>ID-Versatz je Platz (IdProvider._lastID), damit Trupps, Figuren und Kisten zweier Spieler nie dieselbe ID haben.</summary>
        public const uint IdOffsetPerSlot = 100000000u;
    }

    /// <summary>Schalter und Zähler für den Versus-Teil (wie UnitSync.NetApplying im Koop).</summary>
    internal static class VsNet
    {
        /// <summary>true, solange ein Stand vom Host angewendet wird (Versus-Haken lassen das Original dann laufen).</summary>
        public static bool Applying;

        /// <summary>true, solange das Tempo des Hosts beim Gast gesetzt wird.</summary>
        public static bool ApplyingSpeed;

        private static readonly Dictionary<Msg, int> SentCount = new Dictionary<Msg, int>();
        private static readonly Dictionary<Msg, int> RecvCount = new Dictionary<Msg, int>();
        public static long BytesUp;
        public static long BytesDown;

        public static byte[] Build(Msg type, Action<BinaryWriter> body = null)
        {
            var data = Protocol.Build(type, body);
            SentCount.TryGetValue(type, out var n);
            SentCount[type] = n + 1;
            BytesUp += data.Length;
            return data;
        }

        public static void CountReceived(Msg type, int bytes)
        {
            RecvCount.TryGetValue(type, out var n);
            RecvCount[type] = n + 1;
            BytesDown += bytes;
        }

        public static int Sent(Msg type) => SentCount.TryGetValue(type, out var n) ? n : 0;
        public static int Received(Msg type) => RecvCount.TryGetValue(type, out var n) ? n : 0;

        public static void ResetCounters()
        {
            SentCount.Clear();
            RecvCount.Clear();
            BytesUp = 0;
            BytesDown = 0;
        }
    }
}
