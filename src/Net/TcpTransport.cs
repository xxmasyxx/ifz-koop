using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Steamworks;

namespace IfzCoop.Net
{
    /// <summary>
    /// Direktverbindung über TCP (LAN, VPN, Portfreigabe oder zwei Spiele auf einem PC zum Testen).
    /// Jede Nachricht hat 4 Byte Länge (Little Endian) davor. Nicht blockierend, alles im Unity-Hauptthread.
    /// Der Host vergibt Kennungen ab DirectIdBase+2 an Gäste; der Host selbst ist DirectIdBase+1.
    /// </summary>
    internal sealed class TcpTransport : ITransport
    {
        public const ulong DirectIdBase = 0x0110000000000000UL;
        public static readonly CSteamID HostId = new CSteamID(DirectIdBase + 1);
        private const int MaxFrame = 64 * 1024 * 1024;

        private sealed class Conn
        {
            public CSteamID Id;
            public Socket Socket;
            public readonly byte[] Header = new byte[4];
            public int HeaderRead;
            public byte[] Body;
            public int BodyRead;
            public readonly Queue<byte[]> Out = new Queue<byte[]>();
            public byte[] Current;
            public int CurrentOffset;
        }

        private readonly Dictionary<ulong, Conn> _conns = new Dictionary<ulong, Conn>();
        private TcpListener _listener;
        private ulong _nextId = DirectIdBase + 2;

        public event Action<CSteamID, byte[]> Received;
        public event Action<CSteamID> SessionFailed;
        public event Action<CSteamID> Connected;

        public bool IsListening => _listener != null;

        public void Listen(int port)
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            Plugin.Log.LogInfo($"Direktverbindung: warte auf Port {port}");
        }

        /// <summary>Gast: verbindet zum Host (kurz blockierend, höchstens 5 s).</summary>
        public void Connect(string host, int port)
        {
            var client = new TcpClient { NoDelay = true };
            var task = client.ConnectAsync(host, port);
            if (!task.Wait(5000) || !client.Connected)
            {
                client.Close();
                throw new SocketException((int)SocketError.TimedOut);
            }
            Add(HostId, client.Client);
        }

        private void Add(CSteamID id, Socket socket)
        {
            socket.NoDelay = true;
            socket.Blocking = false;
            socket.SendBufferSize = 1 << 20;
            socket.ReceiveBufferSize = 1 << 20;
            _conns[id.m_SteamID] = new Conn { Id = id, Socket = socket };
        }

        public void Send(CSteamID to, byte[] data)
        {
            if (!_conns.TryGetValue(to.m_SteamID, out var conn))
                return;
            var frame = new byte[data.Length + 4];
            frame[0] = (byte)data.Length;
            frame[1] = (byte)(data.Length >> 8);
            frame[2] = (byte)(data.Length >> 16);
            frame[3] = (byte)(data.Length >> 24);
            Buffer.BlockCopy(data, 0, frame, 4, data.Length);
            conn.Out.Enqueue(frame);
        }

        public void Pump()
        {
            Accept();
            Flush();
            Receive();
        }

        private void Accept()
        {
            if (_listener == null)
                return;
            while (_listener.Pending())
            {
                var socket = _listener.AcceptSocket();
                var id = new CSteamID(_nextId++);
                Add(id, socket);
                Plugin.Log.LogInfo($"Direktverbindung: Gast verbunden von {socket.RemoteEndPoint} als {id.m_SteamID}");
                Connected?.Invoke(id);
            }
        }

        public void Flush()
        {
            List<Conn> broken = null;
            foreach (var conn in _conns.Values)
            {
                try
                {
                    while (true)
                    {
                        if (conn.Current == null)
                        {
                            if (conn.Out.Count == 0)
                                break;
                            conn.Current = conn.Out.Dequeue();
                            conn.CurrentOffset = 0;
                        }
                        int sent = conn.Socket.Send(conn.Current, conn.CurrentOffset, conn.Current.Length - conn.CurrentOffset, SocketFlags.None, out var error);
                        if (error == SocketError.WouldBlock)
                            break;
                        if (error != SocketError.Success)
                            throw new SocketException((int)error);
                        conn.CurrentOffset += sent;
                        if (conn.CurrentOffset >= conn.Current.Length)
                            conn.Current = null;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Direktverbindung {conn.Id.m_SteamID} senden: {e.Message}");
                    (broken ??= new List<Conn>()).Add(conn);
                }
            }
            Drop(broken);
        }

        private void Receive()
        {
            List<Conn> broken = null;
            var snapshot = new List<Conn>(_conns.Values);
            foreach (var conn in snapshot)
            {
                try
                {
                    for (int rounds = 0; rounds < 64; rounds++)
                    {
                        if (conn.Body == null)
                        {
                            int n = conn.Socket.Receive(conn.Header, conn.HeaderRead, 4 - conn.HeaderRead, SocketFlags.None, out var error);
                            if (error == SocketError.WouldBlock)
                                break;
                            if (error != SocketError.Success || n == 0)
                                throw new SocketException((int)(error == SocketError.Success ? SocketError.ConnectionReset : error));
                            conn.HeaderRead += n;
                            if (conn.HeaderRead < 4)
                                continue;
                            int length = conn.Header[0] | (conn.Header[1] << 8) | (conn.Header[2] << 16) | (conn.Header[3] << 24);
                            if (length < 0 || length > MaxFrame)
                                throw new InvalidOperationException("ungültige Nachrichtenlänge " + length);
                            conn.Body = new byte[length];
                            conn.BodyRead = 0;
                            conn.HeaderRead = 0;
                            if (length == 0)
                            {
                                var empty = conn.Body;
                                conn.Body = null;
                                Received?.Invoke(conn.Id, empty);
                            }
                        }
                        else
                        {
                            int n = conn.Socket.Receive(conn.Body, conn.BodyRead, conn.Body.Length - conn.BodyRead, SocketFlags.None, out var error);
                            if (error == SocketError.WouldBlock)
                                break;
                            if (error != SocketError.Success || n == 0)
                                throw new SocketException((int)(error == SocketError.Success ? SocketError.ConnectionReset : error));
                            conn.BodyRead += n;
                            if (conn.BodyRead < conn.Body.Length)
                                continue;
                            var body = conn.Body;
                            conn.Body = null;
                            Received?.Invoke(conn.Id, body);
                            if (!_conns.ContainsKey(conn.Id.m_SteamID))
                                break; // beim Verarbeiten geschlossen
                        }
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Direktverbindung {conn.Id.m_SteamID} getrennt: {e.Message}");
                    (broken ??= new List<Conn>()).Add(conn);
                }
            }
            Drop(broken);
        }

        private void Drop(List<Conn> broken)
        {
            if (broken == null)
                return;
            foreach (var conn in broken)
            {
                if (!_conns.Remove(conn.Id.m_SteamID))
                    continue;
                try { conn.Socket.Close(); } catch (Exception) { }
                SessionFailed?.Invoke(conn.Id);
            }
        }

        public void Close(CSteamID peer)
        {
            if (_conns.TryGetValue(peer.m_SteamID, out var conn))
            {
                _conns.Remove(peer.m_SteamID);
                try { conn.Socket.Shutdown(SocketShutdown.Both); } catch (Exception) { }
                try { conn.Socket.Close(); } catch (Exception) { }
            }
        }

        public void CloseAll()
        {
            foreach (var key in new List<ulong>(_conns.Keys))
                Close(new CSteamID(key));
            if (_listener != null)
            {
                try { _listener.Stop(); } catch (Exception) { }
                _listener = null;
            }
        }
    }
}
