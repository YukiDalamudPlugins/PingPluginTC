using PingPlugin.GameAddressDetectors;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace PingPlugin.PingTrackers
{
    /// <summary>
    /// Measures latency by timing a TCP handshake to one of the FFXIV game ports.
    /// Unlike the ICMP-based trackers (COM / Win32 GetRTTAndHopCount), this works on
    /// servers that filter ICMP echo - notably the Traditional Chinese servers hosted
    /// on Google Cloud Singapore, where the ICMP trackers report a constant 0ms.
    /// </summary>
    public class TcpPingTracker : PingTracker
    {
        // Standard FFXIV game/lobby ports. A successful handshake (SYN -> SYN/ACK) is ~1 RTT.
        private static readonly int[] GamePorts =
        {
            55021, 55022, 55023, 55024, 55025, 55026, 55027, 55028,
            54992, 54993, 54994, 55006, 55007,
        };

        private const int TimeoutMs = 3000;

        private readonly IPluginLog pluginLog;
        private int lastGoodPort;

        public TcpPingTracker(PingConfiguration config, GameAddressDetector addressDetector, IPluginLog pluginLog)
            : base(config, addressDetector, PingTrackerKind.TCP, pluginLog)
        {
            this.pluginLog = pluginLog;
        }

        protected override async Task PingLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (SeAddress != null && !IPAddress.IsLoopback(SeAddress))
                {
                    try
                    {
                        var rtt = await MeasureRtt(SeAddress, token);
                        Errored = rtt < 0;
                        if (!Errored)
                        {
                            NextRTTCalculation((ulong)rtt);
                        }
                        else if (Verbose)
                        {
                            pluginLog.Warning("TCP ping found no reachable game port - this may be temporary and acceptable.");
                        }
                    }
                    catch (Exception e)
                    {
                        Errored = true;
                        pluginLog.Error(e, "Error occurred when executing TCP ping.");
                    }
                }

                await Task.Delay(3000, token);
            }
        }

        private async Task<long> MeasureRtt(IPAddress address, CancellationToken token)
        {
            // Reuse the previously-successful port first to minimise connection churn.
            if (this.lastGoodPort != 0)
            {
                var rtt = await TryConnect(address, this.lastGoodPort, token);
                if (rtt >= 0) return rtt;
                this.lastGoodPort = 0;
            }

            foreach (var port in GamePorts)
            {
                if (token.IsCancellationRequested) break;

                var rtt = await TryConnect(address, port, token);
                if (rtt >= 0)
                {
                    this.lastGoodPort = port;
                    return rtt;
                }
            }

            return -1;
        }

        private static async Task<long> TryConnect(IPAddress address, int port, CancellationToken token)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var sw = Stopwatch.StartNew();
            try
            {
                var connectTask = socket.ConnectAsync(address, port);
                var finished = await Task.WhenAny(connectTask, Task.Delay(TimeoutMs, token));
                sw.Stop();

                if (finished == connectTask)
                {
                    // Surface any connect exception (e.g. connection refused) to the catch below.
                    await connectTask;
                    return sw.ElapsedMilliseconds; // SYN/ACK received -> ~1 RTT
                }

                return -1; // timed out / filtered
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                // An RST round-trip still reflects the network latency, so it's a valid sample.
                sw.Stop();
                return sw.ElapsedMilliseconds;
            }
            catch
            {
                return -1;
            }
        }
    }
}
