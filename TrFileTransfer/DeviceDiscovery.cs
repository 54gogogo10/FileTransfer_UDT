using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    /// <summary>A discovered LAN device.</summary>
    public struct DeviceInfo
    {
        public string Name;
        public string Ip;
        public int Port;
        public bool SupportsTcp;
        public bool SupportsUdt;
        /// <summary>Server requires a pairing code (0x05) before any transfer.</summary>
        public bool RequiresPairing;
    }

    /// <summary>Wire format for the UDP discovery protocol: probe 0xD1 → response 0xD2.</summary>
    public static class DiscoveryProtocol
    {
        public const int DefaultPort = 45000;
        public const byte Probe = 0xD1;
        public const byte Response = 0xD2;
        /// <summary>Protocol bitmap bits: bit0=TCP bit1=UDT bit2=pairing required.</summary>
        public const byte FlagTcp = 1;
        public const byte FlagUdt = 2;
        public const byte FlagPairing = 4;

        /// <summary>Response layout: [0xD2][nameLen:1][name][port:2 big-endian][protocols:1 (bit0=TCP bit1=UDT bit2=pairing)].</summary>
        public static byte[] BuildResponse(string name, int port, bool tcp, bool udt, bool pairing)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name ?? "");
            int len = Math.Min(nameBytes.Length, 255);
            var buf = new byte[1 + 1 + len + 2 + 1];
            buf[0] = Response;
            buf[1] = (byte)len;
            Buffer.BlockCopy(nameBytes, 0, buf, 2, len);
            buf[2 + len] = (byte)((port >> 8) & 0xFF);
            buf[3 + len] = (byte)(port & 0xFF);
            buf[4 + len] = (byte)((tcp ? FlagTcp : 0) | (udt ? FlagUdt : 0) | (pairing ? FlagPairing : 0));
            return buf;
        }

        public static DeviceInfo? ParseResponse(byte[] data, int count, IPEndPoint from)
        {
            if (count < 4 || data[0] != Response) return null;
            int nameLen = data[1];
            if (1 + 1 + nameLen + 2 + 1 > count) return null;
            string name = Encoding.UTF8.GetString(data, 2, nameLen);
            int port = (data[2 + nameLen] << 8) | data[3 + nameLen];
            byte prot = data[4 + nameLen];
            return new DeviceInfo
            {
                Name = name,
                Ip = from.Address.ToString(),
                Port = port,
                SupportsTcp = (prot & FlagTcp) != 0,
                SupportsUdt = (prot & FlagUdt) != 0,
                RequiresPairing = (prot & FlagPairing) != 0
            };
        }
    }

    /// <summary>Listens for discovery probes and answers with the local server's endpoint.</summary>
    public class DiscoveryServer : IDisposable
    {
        private UdpClient _udp;
        private CancellationTokenSource _cts;
        private readonly int _port;

        public DiscoveryServer(int port)
        {
            _port = port;
        }

        /// <summary>Starts answering probes without the pairing flag (compat overload).</summary>
        public void Start(string serverName, int serverPort, bool tcp, bool udt)
        {
            Start(serverName, serverPort, tcp, udt, false);
        }

        /// <summary>Starts answering probes. Safe to call again to refresh the payload.</summary>
        public void Start(string serverName, int serverPort, bool tcp, bool udt, bool pairing)
        {
            Stop();
            _cts = new CancellationTokenSource();
            try
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Any, _port));
            }
            catch (SocketException)
            {
                // Discovery port unavailable (in use / firewalled) — transfers still work
                _udp = null;
                return;
            }
            var ct = _cts.Token;
            Task.Run(() => Loop(ct, serverName, serverPort, tcp, udt, pairing));
        }

        private async Task Loop(CancellationToken ct, string serverName, int serverPort, bool tcp, bool udt, bool pairing)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await _udp.ReceiveAsync();
                    if (result.Buffer.Length >= 1 && result.Buffer[0] == DiscoveryProtocol.Probe)
                    {
                        var resp = DiscoveryProtocol.BuildResponse(serverName, serverPort, tcp, udt, pairing);
                        await _udp.SendAsync(resp, resp.Length, result.RemoteEndPoint);
                    }
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException)
                {
                    // On Windows a UDP socket gets poisoned by ICMP port-unreachable
                    // (ConnectionReset) after replying to a probe sender that already
                    // vanished — the next ReceiveAsync throws. Keep serving; only a
                    // real teardown (Stop → ObjectDisposedException) ends the loop.
                    if (ct.IsCancellationRequested) break;
                }
                catch (Exception) { if (ct.IsCancellationRequested) break; }
            }
        }

        public void Stop()
        {
            if (_cts != null) { _cts.Cancel(); _cts = null; }
            if (_udp != null) { try { _udp.Close(); } catch { } _udp = null; }
        }

        public void Dispose() { Stop(); }
    }

    /// <summary>Broadcasts probes and collects device responses.</summary>
    public static class DiscoveryClient
    {
        /// <param name="targetIp">Broadcast address by default; pass a specific IP to scan a single host.</param>
        public static async Task<DeviceInfo[]> Scan(int port = DiscoveryProtocol.DefaultPort,
            int timeoutMs = 1500, string targetIp = "255.255.255.255")
        {
            var results = new List<DeviceInfo>();
            using (var udp = new UdpClient())
            {
                udp.EnableBroadcast = true;
                var target = new IPEndPoint(IPAddress.Parse(targetIp), port);
                var probe = new byte[1] { DiscoveryProtocol.Probe };
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

                // Send 3 probes spaced 200 ms apart, collecting responses in between
                for (int i = 0; i < 3 && DateTime.UtcNow < deadline; i++)
                {
                    try { await udp.SendAsync(probe, 1, target); } catch { }
                    var wait = Task.Delay(200);
                    while (!wait.IsCompleted && DateTime.UtcNow < deadline)
                    {
                        var recv = udp.ReceiveAsync();
                        var done = await Task.WhenAny(recv, wait);
                        if (done == recv && recv.IsCompleted && !recv.IsFaulted)
                        {
                            var r = recv.Result;
                            var info = DiscoveryProtocol.ParseResponse(r.Buffer, r.Buffer.Length, r.RemoteEndPoint);
                            if (info.HasValue && !results.Exists(d => d.Ip == info.Value.Ip && d.Port == info.Value.Port))
                                results.Add(info.Value);
                        }
                        else break;
                    }
                }
            }
            return results.ToArray();
        }
    }
}
