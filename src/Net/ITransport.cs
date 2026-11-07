using System;
using Steamworks;

namespace IfzCoop.Net
{
    /// <summary>
    /// Nachrichtenweg zwischen Host und Gästen. Zuverlässig und geordnet pro Gegenstelle.
    /// Gegenstellen heißen CSteamID; bei der Direktverbindung sind das vom Host vergebene Kennungen.
    /// </summary>
    internal interface ITransport
    {
        event Action<CSteamID, byte[]> Received;
        event Action<CSteamID> SessionFailed;

        void Send(CSteamID to, byte[] data);

        /// <summary>Jeden Frame: senden und empfangen.</summary>
        void Pump();

        /// <summary>Nur senden.</summary>
        void Flush();

        void Close(CSteamID peer);

        void CloseAll();
    }
}
