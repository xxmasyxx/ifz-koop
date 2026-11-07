using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;

namespace IfzCoop.Net
{
    /// <summary>
    /// Nachrichten über ISteamNetworkingMessages mit der eigenen Steam-Sitzung des Spiels
    /// (echte App-ID 1465460, Steam-Relay, keine Portfreigabe).
    /// Alles läuft zuverlässig und geordnet auf Kanal 0. Ausgehende Nachrichten stehen pro
    /// Empfänger in einer Warteschlange; ist Steams Sendepuffer voll, geht es im nächsten Frame weiter.
    /// </summary>
    internal sealed class SteamTransport : ITransport
    {
        private readonly int Channel;
        private const int SendFlags = Constants.k_nSteamNetworkingSend_Reliable | Constants.k_nSteamNetworkingSend_NoNagle;
        private const int MaxFailuresBeforeDrop = 600; // etwa 10 s bei 60 FPS

        private readonly IntPtr[] _recvBuffer = new IntPtr[64];
        private readonly Dictionary<ulong, Queue<byte[]>> _outgoing = new Dictionary<ulong, Queue<byte[]>>();
        private readonly Dictionary<ulong, int> _failures = new Dictionary<ulong, int>();

        private Callback<SteamNetworkingMessagesSessionRequest_t> _sessionRequest;
        private Callback<SteamNetworkingMessagesSessionFailed_t> _sessionFailed;

        /// <summary>Entscheidet, ob eine neue Sitzung eines fremden Spielers angenommen wird.</summary>
        public Func<CSteamID, bool> AcceptPeer;

        /// <summary>Kanal 0 = Koop (wie bisher), Kanal 1 = Versus.</summary>
        public SteamTransport(int channel = 0)
        {
            Channel = channel;
        }

        public event Action<CSteamID, byte[]> Received;
        public event Action<CSteamID> SessionFailed;

        public void Init()
        {
            if (_sessionRequest != null)
                return;
            _sessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
            _sessionFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);
        }

        public int QueuedFor(CSteamID peer)
        {
            return _outgoing.TryGetValue(peer.m_SteamID, out var q) ? q.Count : 0;
        }

        public void Send(CSteamID to, byte[] data)
        {
            if (!_outgoing.TryGetValue(to.m_SteamID, out var q))
            {
                q = new Queue<byte[]>();
                _outgoing[to.m_SteamID] = q;
            }
            q.Enqueue(data);
        }

        /// <summary>Jeden Frame aufrufen: sendet Wartendes und liest Eingegangenes.</summary>
        public void Pump()
        {
            Flush();
            Receive();
        }

        public void Close(CSteamID peer)
        {
            _outgoing.Remove(peer.m_SteamID);
            _failures.Remove(peer.m_SteamID);
            try
            {
                var id = Identity(peer);
                SteamNetworkingMessages.CloseSessionWithUser(ref id);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("CloseSession: " + e.Message);
            }
        }

        public void CloseAll()
        {
            foreach (var key in new List<ulong>(_outgoing.Keys))
                Close(new CSteamID(key));
            _outgoing.Clear();
            _failures.Clear();
        }

        /// <summary>Nur senden, nichts empfangen (z. B. beim Verlassen, ohne neue Nachrichten auszulösen).</summary>
        public void Flush()
        {
            List<ulong> drop = null;
            foreach (var kv in _outgoing)
            {
                var q = kv.Value;
                while (q.Count > 0)
                {
                    var result = TrySend(new CSteamID(kv.Key), q.Peek());
                    if (result == EResult.k_EResultOK)
                    {
                        q.Dequeue();
                        _failures[kv.Key] = 0;
                        continue;
                    }

                    // Puffer voll oder Verbindung baut sich noch auf: im nächsten Frame weiter.
                    _failures.TryGetValue(kv.Key, out var fails);
                    fails++;
                    _failures[kv.Key] = fails;
                    if (result != EResult.k_EResultLimitExceeded && fails == 1)
                        Plugin.Log.LogWarning($"Senden an {kv.Key} fehlgeschlagen: {result}, versuche weiter");
                    if (fails >= MaxFailuresBeforeDrop)
                    {
                        Plugin.Log.LogError($"Senden an {kv.Key} gibt auf nach {fails} Fehlversuchen ({result})");
                        (drop ??= new List<ulong>()).Add(kv.Key);
                    }
                    break;
                }
            }

            if (drop != null)
            {
                foreach (var key in drop)
                {
                    _outgoing.Remove(key);
                    SessionFailed?.Invoke(new CSteamID(key));
                }
            }
        }

        private EResult TrySend(CSteamID to, byte[] data)
        {
            var id = Identity(to);
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                return SteamNetworkingMessages.SendMessageToUser(ref id, handle.AddrOfPinnedObject(), (uint)data.Length, SendFlags, Channel);
            }
            finally
            {
                handle.Free();
            }
        }

        private void Receive()
        {
            for (int round = 0; round < 16; round++)
            {
                int count = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, _recvBuffer, _recvBuffer.Length);
                if (count <= 0)
                    return;

                for (int i = 0; i < count; i++)
                {
                    IntPtr ptr = _recvBuffer[i];
                    try
                    {
                        var msg = Marshal.PtrToStructure<SteamNetworkingMessage_t>(ptr);
                        var data = new byte[msg.m_cbSize];
                        if (msg.m_cbSize > 0)
                            Marshal.Copy(msg.m_pData, data, 0, msg.m_cbSize);
                        var from = msg.m_identityPeer.GetSteamID();
                        Received?.Invoke(from, data);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError("Fehler beim Verarbeiten einer Nachricht: " + e);
                    }
                    finally
                    {
                        SteamNetworkingMessage_t.Release(ptr);
                    }
                }
            }
        }

        private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t request)
        {
            var identity = request.m_identityRemote;
            var peer = identity.GetSteamID();
            bool accept = AcceptPeer != null && AcceptPeer(peer);
            if (accept)
            {
                SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
                Plugin.Log.LogInfo($"Sitzung von {peer.m_SteamID} angenommen");
            }
            else
            {
                Plugin.Log.LogInfo($"Sitzung von {peer.m_SteamID} abgelehnt (nicht in der Lobby)");
            }
        }

        private void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t failed)
        {
            var peer = failed.m_info.m_identityRemote.GetSteamID();
            Plugin.Log.LogWarning($"Steam-Sitzung mit {peer.m_SteamID} abgebrochen");
            SessionFailed?.Invoke(peer);
        }

        private static SteamNetworkingIdentity Identity(CSteamID steamId)
        {
            var id = new SteamNetworkingIdentity();
            id.SetSteamID(steamId);
            return id;
        }
    }
}
