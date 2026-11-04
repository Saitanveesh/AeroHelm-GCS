// AeroHelm GCS native receive-only cloud MAVLink adapter (M1).
// Copyright AeroHelm contributors. GPL-3.0-or-later.
// An authenticated WSS stream is exposed to native Mission Planner on local
// TCP 127.0.0.1:5760. ALL GCS->TCP data is discarded, never forwarded to cloud.
// No remote arming, calibration, mission upload or parameter mutation.
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner;

namespace AeroHelm.GcsCloud
{
    internal sealed class AeroHelmReceiveOnlyBridge : IDisposable
    {
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 5760);
        private CancellationTokenSource cancellation;
        private Task worker;
        private volatile string state = "OFF";
        private long frames;
        private long heartbeats;
        private long attitudes;
        private long lastFrameUtcTicks;
        private long lastHeartbeatUtcTicks;
        private long lastAttitudeUtcTicks;
        private long lastRollCentideg;
        private long lastPitchCentideg;
        private long validAttitudeSamples;

        internal string Summary
        {
            get
            {
                string status = state;
                long count = Interlocked.Read(ref frames);
                long hb = Interlocked.Read(ref heartbeats);
                long att = Interlocked.Read(ref attitudes);
                long lastTicks = Interlocked.Read(ref lastFrameUtcTicks);
                string age = lastTicks == 0 ? "never" :
                    Math.Max(0, (int)(DateTime.UtcNow - new DateTime(
                        lastTicks, DateTimeKind.Utc)).TotalSeconds) + "s ago";
                var now = DateTime.UtcNow;
                long hbTicks = Interlocked.Read(ref lastHeartbeatUtcTicks);
                long attTicks = Interlocked.Read(ref lastAttitudeUtcTicks);
                bool liveHb = hbTicks > 0 &&
                    (now-new DateTime(hbTicks,DateTimeKind.Utc)).TotalSeconds < 5;
                bool liveAtt = attTicks > 0 &&
                    (now-new DateTime(attTicks,DateTimeKind.Utc)).TotalSeconds < 5;
                long decoded = Interlocked.Read(ref validAttitudeSamples);
                string angles = !liveAtt || decoded == 0 ?
                    "roll/pitch STALE OR UNKNOWN" :
                    "roll " + (Interlocked.Read(ref lastRollCentideg)/100.0).ToString(
                        "F1",System.Globalization.CultureInfo.InvariantCulture) +
                    " deg / pitch " + (Interlocked.Read(ref lastPitchCentideg)/100.0).ToString(
                        "F1",System.Globalization.CultureInfo.InvariantCulture) + " deg";
                return "LOCAL TCP / " + status + " | frames " + count +
                    " | HB " + hb + (liveHb ? " LIVE" : " STALE/MISSING") +
                    " | ATT " + att + (liveAtt ? " LIVE" : " STALE/MISSING") +
                    " | " + angles + " | last frame " + age;
            }
        }

        // Small, meaningful headline for HOME. The full Summary remains
        // available in LINK diagnostics; never duplicate it in cloud status.
        internal string CompactSummary
        {
            get
            {
                if (cancellation == null)
                {
                    if (state.StartsWith("PORT 5760", StringComparison.Ordinal))
                        return "LOCAL TCP / PORT 5760 OCCUPIED — CHECK LISTENER";
                    return "LOCAL TCP / " + state;
                }
                long hbTicks = Interlocked.Read(ref lastHeartbeatUtcTicks);
                long attTicks = Interlocked.Read(ref lastAttitudeUtcTicks);
                DateTime now = DateTime.UtcNow;
                bool hbLive = hbTicks > 0 &&
                    (now - new DateTime(hbTicks, DateTimeKind.Utc)).TotalSeconds < 5;
                bool attLive = attTicks > 0 &&
                    (now - new DateTime(attTicks, DateTimeKind.Utc)).TotalSeconds < 5;
                if (hbLive && attLive)
                    return "LOCAL TCP / REAL HEARTBEAT + ATTITUDE RECEIVED";
                return "LOCAL TCP / " + state +
                    " | HB " + (hbLive ? "LIVE" : "MISSING") +
                    " | ATT " + (attLive ? "LIVE" : "MISSING");
            }
        }

        internal bool IsListening { get { return cancellation != null; } }

        internal bool Start()
        {
            if (cancellation != null) return false;
            if (string.IsNullOrEmpty(Program.AeroHelmOperatorToken) ||
                DateTime.UtcNow >= Program.AeroHelmTokenExpiresUtc)
            {
                state = "LOGIN REQUIRED";
                return false;
            }
            try
            {
                listener.Start(1); // Exclusive loopback port; no public or LAN bind.
                cancellation = new CancellationTokenSource();
                var stop = cancellation.Token; // capture before shutdown can null the field
                state = "WAITING FOR NATIVE TCP CLIENT";
                worker = Task.Run(() => AcceptLoopAsync(stop));
                return true;
            }
            catch (SocketException)
            {
                // Another process can own this port (not necessarily Python).
                // Never kill/reuse unknown listeners or bind a public interface.
                state = "PORT 5760 OCCUPIED";
                return false;
            }
        }

        private async Task AcceptLoopAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client = null;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                    if (stop.IsCancellationRequested) return;
                    client.NoDelay = true;
                    await ForwardOneClientAsync(client, stop);
                }
                catch (ObjectDisposedException) when (stop.IsCancellationRequested) { return; }
                catch (SocketException) when (stop.IsCancellationRequested) { return; }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    state = DateTime.UtcNow >= Program.AeroHelmTokenExpiresUtc ?
                        "CLOUD SESSION EXPIRED; SIGN IN AGAIN" : "LOCAL TCP DISCONNECTED";
                }
                catch (Exception ex) when (!stop.IsCancellationRequested)
                {
                    state = "STREAM UNAVAILABLE: " + ex.GetType().Name;
                    try { await Task.Delay(1500, stop); }
                    catch (OperationCanceledException) { return; }
                }
                finally { if (client != null) client.Dispose(); }
            }
        }

        private static Uri PrivateWebSocketUri()
        {
            Uri origin;
            if (!Uri.TryCreate(Program.AeroHelmCloudOrigin, UriKind.Absolute, out origin) ||
                origin.Scheme != Uri.UriSchemeHttps ||
                !(origin.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase) ||
                  origin.IsLoopback) || origin.UserInfo.Length != 0 ||
                origin.AbsolutePath != "/" || origin.Query.Length != 0 ||
                origin.Fragment.Length != 0)
                throw new InvalidOperationException("Authenticated private cloud origin required.");

            string vehicle = Environment.GetEnvironmentVariable("AEROHELM_VEHICLE_ID");
            if (string.IsNullOrEmpty(vehicle)) vehicle = "AH-HW-001";
            if (vehicle.Length > 64) throw new InvalidOperationException("Invalid vehicle ID.");
            foreach (char ch in vehicle)
                if (!char.IsLetterOrDigit(ch) && ch != '-' && ch != '_')
                    throw new InvalidOperationException("Invalid vehicle ID.");

            return new UriBuilder("wss", origin.Host, origin.Port,
                "/ws/mavlink/view/" + Uri.EscapeDataString(vehicle)).Uri;
        }

        private async Task ForwardOneClientAsync(TcpClient client, CancellationToken stop)
        {
            // Never retain JWT beyond the connection. A separate login is NOT
            // required because startup authentication already provided it.
            if (string.IsNullOrEmpty(Program.AeroHelmOperatorToken) ||
                DateTime.UtcNow >= Program.AeroHelmTokenExpiresUtc)
            {
                state = "CLOUD SESSION EXPIRED; SIGN IN AGAIN";
                return;
            }
            Uri url = PrivateWebSocketUri();
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(stop))
            using (var ws = new ClientWebSocket())
            {
                // A live WebSocket must not outlive the authenticated operator
                // session merely because its handshake succeeded earlier.
                var lifetime = Program.AeroHelmTokenExpiresUtc - DateTime.UtcNow;
                if (lifetime <= TimeSpan.Zero)
                {
                    state = "CLOUD SESSION EXPIRED; SIGN IN AGAIN";
                    return;
                }
                linked.CancelAfter(lifetime);
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                ws.Options.SetRequestHeader("Authorization",
                    "Bearer " + Program.AeroHelmOperatorToken);
                state = "CONNECTING AUTHENTICATED CLOUD STREAM";
                await ws.ConnectAsync(url, linked.Token);
                state = "CLOUD CONNECTED; WAITING FOR MAVLINK FRAMES";

                // A separate read loop explicitly DROPS all bytes the native
                // GCS sends. It cannot reach the WebSocket or the Pixhawk.
                Task drain = DrainGcsAsync(client.GetStream(), linked);
                try
                {
                    var packet = new byte[512];
                    while (!linked.IsCancellationRequested && ws.State == WebSocketState.Open)
                    {
                        int length = 0;
                        WebSocketReceiveResult part;
                        do
                        {
                            if (length == packet.Length)
                                throw new InvalidDataException("Oversize MAVLink frame rejected.");
                            part = await ws.ReceiveAsync(
                                new ArraySegment<byte>(packet, length, packet.Length - length),
                                linked.Token);
                            if (part.MessageType == WebSocketMessageType.Close)
                                return;
                            if (part.MessageType != WebSocketMessageType.Binary)
                                throw new InvalidDataException("Unexpected nonbinary cloud message.");
                            length += part.Count;
                        } while (!part.EndOfMessage);

                        int messageId;
                        if (!ValidFrame(packet, length, out messageId))
                            continue; // Refuse malformed/unknown message envelopes.
                        await client.GetStream().WriteAsync(packet, 0, length, linked.Token);
                        Interlocked.Increment(ref frames);
                        long receivedTicks = DateTime.UtcNow.Ticks;
                        if (messageId == 0)
                        {
                            Interlocked.Increment(ref heartbeats);
                            Interlocked.Exchange(ref lastHeartbeatUtcTicks, receivedTicks);
                        }
                        if (messageId == 30)
                        {
                            Interlocked.Increment(ref attitudes);
                            Interlocked.Exchange(ref lastAttitudeUtcTicks, receivedTicks);
                            float rollDeg,pitchDeg;
                            if (TryReadAttitude(packet,length,out rollDeg,out pitchDeg))
                            {
                                Interlocked.Exchange(ref lastRollCentideg,
                                    (long)Math.Round(rollDeg*100));
                                Interlocked.Exchange(ref lastPitchCentideg,
                                    (long)Math.Round(pitchDeg*100));
                                Interlocked.Increment(ref validAttitudeSamples);
                            }
                        }
                        Interlocked.Exchange(ref lastFrameUtcTicks, receivedTicks);
                        state = "RECEIVING READ-ONLY MAVLINK";
                    }
                }
                finally
                {
                    linked.Cancel();
                    // The client stream is closed by the owning TCP client.
                    // DrainGcsAsync is cancellation-aware and has no send path.
                    var ignored = drain;
                    if (!stop.IsCancellationRequested)
                        state = DateTime.UtcNow >= Program.AeroHelmTokenExpiresUtc ?
                            "CLOUD SESSION EXPIRED; SIGN IN AGAIN" : "NATIVE TCP DISCONNECTED";
                }
            }
        }

        private static async Task DrainGcsAsync(NetworkStream stream,
                                                CancellationTokenSource linked)
        {
            var discarded = new byte[1024];
            try
            {
                while (!linked.IsCancellationRequested)
                    if (await stream.ReadAsync(discarded, 0, discarded.Length,
                                               linked.Token) == 0)
                        return;
            }
            catch (Exception ex) when (ex is IOException ||
                                       ex is ObjectDisposedException ||
                                       ex is OperationCanceledException) { }
            finally { linked.Cancel(); }
        }

        // MAVLink v1/v2 envelope only; Pi validates the CRC before uplink.
        // Reject frames with inconsistent length, never synthesize heartbeats.
        internal static bool ValidFrame(byte[] frame, int size, out int messageId)
        {
            messageId = -1;
            if (frame == null || size < 8 || size > frame.Length) return false;
            if (frame[0] == 0xFE && size == frame[1] + 8)
            {
                messageId = frame[5];
                return true;
            }
            if (frame[0] == 0xFD && size >= 12)
            {
                // MAVLink2 incompatibility flags other than signing cannot be
                // interpreted safely by this pass-through viewer.
                if ((frame[2] & ~0x01) != 0) return false;
                int signature = (frame[2] & 0x01) == 0 ? 0 : 13;
                if (size != frame[1] + 12 + signature) return false;
                messageId = frame[7] | (frame[8] << 8) | (frame[9] << 16);
                return true;
            }
            return false;
        }

        // MAVLink 1/2 ATTITUDE (ID 30): time_boot_ms followed by IEEE754
        // roll and pitch radians. This is passive decoding from *real frames*;
        // a stale/invalid source never generates a new measurement.
        internal static bool TryReadAttitude(byte[] frame, int size,
                                             out float rollDegrees,out float pitchDegrees)
        {
            rollDegrees=0;
            pitchDegrees=0;
            int messageId;
            if (!ValidFrame(frame,size,out messageId) || messageId!=30 ||
                frame[1]<12 || !BitConverter.IsLittleEndian)
                return false;
            int offset=frame[0]==0xFE?6:10;
            float roll=BitConverter.ToSingle(frame,offset+4);
            float pitch=BitConverter.ToSingle(frame,offset+8);
            if (float.IsNaN(roll) || float.IsInfinity(roll) ||
                float.IsNaN(pitch) || float.IsInfinity(pitch))
                return false;
            double r=roll*180.0/Math.PI;
            double p=pitch*180.0/Math.PI;
            if (Math.Abs(r)>180 || Math.Abs(p)>90)return false;
            rollDegrees=(float)r;
            pitchDegrees=(float)p;
            return true;
        }

        public void Dispose()
        {
            if (cancellation == null) return;
            cancellation.Cancel();
            listener.Stop();
            cancellation.Dispose();
            cancellation = null;
            state = "STOPPED";
        }
    }
}
