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
        private long lastRollCentideg;
        private long lastPitchCentideg;
        private long validAttitudeSamples;
        private readonly AeroHelmLinkQuality quality = new AeroHelmLinkQuality();
        private volatile bool cloudStreamOpen;

        internal string Summary
        {
            get
            {
                string status = state;
                long count = Interlocked.Read(ref frames);
                long hb = Interlocked.Read(ref heartbeats);
                long att = Interlocked.Read(ref attitudes);
                var metrics = quality.Snapshot();
                string age = metrics.FrameAgeMs < 0 ? "never" :
                    (metrics.FrameAgeMs / 1000.0).ToString("F1",
                        System.Globalization.CultureInfo.InvariantCulture) + "s ago";
                bool liveHb = cloudStreamOpen && metrics.HeartbeatFresh;
                bool liveAtt = cloudStreamOpen && metrics.AttitudeFresh;
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
                    " | " + angles + " | last frame " + age +
                    " | local 5s HB " + metrics.HeartbeatHz.ToString("F1",
                        System.Globalization.CultureInfo.InvariantCulture) + "/s" +
                    " ATT " + metrics.AttitudeHz.ToString("F1",
                        System.Globalization.CultureInfo.InvariantCulture) + "/s" +
                    " | ATT gaps >2.5s " + metrics.AttitudeGapEvents +
                    " | invalid HB/ATT CRC " + metrics.RejectedEvidence;
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
                if (state == "WAITING FOR NATIVE TCP CLIENT")
                    return "LOCAL TCP / READY — CLICK FLIGHT TO START HUD";
                var metrics = quality.Snapshot();
                bool hbLive = cloudStreamOpen && metrics.HeartbeatFresh;
                bool attLive = cloudStreamOpen && metrics.AttitudeFresh;
                if (hbLive && attLive)
                    return "LOCAL TCP / REAL HEARTBEAT + ATTITUDE RECEIVED";
                return "LOCAL TCP / " + state +
                    " | HB " + (hbLive ? "LIVE" : "MISSING") +
                    " | ATT " + (attLive ? "LIVE" : "MISSING");
            }
        }

        // Current receive-only WSS evidence, not cached flight state.
        internal bool NativeHeartbeatFresh
        {
            get { return cloudStreamOpen && quality.Snapshot().HeartbeatFresh; }
        }
        internal bool NativeAttitudeFresh
        {
            get { return cloudStreamOpen && quality.Snapshot().AttitudeFresh; }
        }
        internal string AttitudeTroubleshooting
        {
            get
            {
                if(!NativeHeartbeatFresh) return
                    "No fresh native HEARTBEAT. Check Pi UART ownership, M1 telemetry WSS and login.";
                if(!NativeAttitudeFresh) return
                    "Fresh HEARTBEAT reaches the GCS, but MAVLink ATTITUDE (message 30) " +
                    "does not. Inspect FC stream configuration, Pi UART-agent message IDs " +
                    "and M1 WebSocket forwarding; do not synthesize roll/pitch or issue " +
                    "FC stream commands from the read-only cloud link.";
                return "Fresh real HEARTBEAT and ATTITUDE packets observed on the native GCS link.";
            }
        }

        internal bool IsListening { get { return cancellation != null; } }

        internal bool Start()
        {
            if (cancellation != null) return true; // Already listening; idempotent retry.
            if (string.IsNullOrEmpty(Program.AeroHelmOperatorToken) ||
                DateTime.UtcNow >= Program.AeroHelmTokenExpiresUtc)
            {
                state = "LOGIN REQUIRED; SIGN IN BEFORE CLOUD VIEW";
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
                // A new WebSocket is a new observation session. Never carry
                // previous-session freshness or angles into this one.
                cloudStreamOpen = false;
                quality.Reset();
                Interlocked.Exchange(ref validAttitudeSamples, 0);
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                ws.Options.SetRequestHeader("Authorization",
                    "Bearer " + Program.AeroHelmOperatorToken);
                state = "CONNECTING AUTHENTICATED CLOUD STREAM";
                await ws.ConnectAsync(url, linked.Token);
                cloudStreamOpen = true;
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
                        // The Pi checks all frame CRCs upstream. Independently
                        // verify HB/ATT CRC before asserting HUD readiness:
                        // a structurally valid or corrupted packet is NOT evidence.
                        if ((messageId == 0 || messageId == 30) &&
                            !HasValidTelemetryChecksum(packet, length, messageId))
                        {
                            quality.RejectEvidence();
                            continue;
                        }
                        await client.GetStream().WriteAsync(packet, 0, length, linked.Token);
                        quality.Observe(messageId);
                        Interlocked.Increment(ref frames);
                        if (messageId == 0)
                        {
                            Interlocked.Increment(ref heartbeats);
                        }
                        if (messageId == 30)
                        {
                            Interlocked.Increment(ref attitudes);
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
                        state = "RECEIVING READ-ONLY MAVLINK";
                    }
                }
                finally
                {
                    cloudStreamOpen = false;
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

        // The CRC validates frame integrity, NOT MAVLink2 signatures or
        // operator/aircraft identity. Only types with known CRC extras are
        // considered evidence of a live native HUD stream (0=50; 30=39).
        internal static bool HasValidTelemetryChecksum(byte[] frame, int size,
                                                        int expectedId)
        {
            int parsedId;
            if ((expectedId != 0 && expectedId != 30) ||
                !ValidFrame(frame, size, out parsedId) || parsedId != expectedId)
                return false;
            // A zero-length synthetic HEARTBEAT is not an autopilot HB.
            if (expectedId == 0 && frame[1] < 9) return false;
            if (expectedId == 30 && frame[1] < 12) return false;
            int headerLength = frame[0] == 0xFE ? 6 : 10;
            int crcIndex = headerLength + frame[1];
            ushort checksum = 0xFFFF;
            for (int i = 1; i < crcIndex; i++)
                checksum = AccumulateCrc(checksum, frame[i]);
            checksum = AccumulateCrc(checksum,
                expectedId == 0 ? (byte)50 : (byte)39);
            ushort expected = (ushort)(frame[crcIndex] | (frame[crcIndex + 1] << 8));
            return checksum == expected;
        }

        private static ushort AccumulateCrc(ushort crc, byte value)
        {
            byte temp = (byte)(value ^ (byte)crc);
            temp ^= (byte)(temp << 4);
            return (ushort)((crc >> 8) ^ (temp << 8) ^
                            (temp << 3) ^ (temp >> 4));
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
                !HasValidTelemetryChecksum(frame,size,30) ||
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
            cloudStreamOpen = false;
            cancellation.Cancel();
            listener.Stop();
            cancellation.Dispose();
            cancellation = null;
            state = "STOPPED";
        }
    }
}
