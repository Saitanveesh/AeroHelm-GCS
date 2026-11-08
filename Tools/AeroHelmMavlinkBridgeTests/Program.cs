// Pure, no-hardware tests compile the ACTUAL GCS receive-only bridge code.
// The stub is for the native Program login fields; not deployed with plugin.
using System;
using AeroHelm.GcsCloud;

namespace MissionPlanner
{
    public static class Program
    {
        public static string AeroHelmOperatorToken;
        public static string AeroHelmCloudOrigin;
        public static DateTime AeroHelmTokenExpiresUtc;
    }
}

namespace AeroHelm.GcsCloud
{
    internal static class BridgeContracts
    {
        static int count;
        static void Assert(bool ok,string error)
        {
            count++;
            if(!ok)throw new InvalidOperationException("FAIL: "+error);
        }

        static byte[] Frame(byte kind,int id,int length)
        {
            int header=kind==0xFE?6:10;
            byte[] payload=new byte[header+length+2];
            payload[0]=kind;
            payload[1]=(byte)length;
            if(kind==0xFE)
                payload[5]=(byte)id;
            else
            {
                payload[7]=(byte)id;
                payload[8]=(byte)(id>>8);
                payload[9]=(byte)(id>>16);
            }
            return payload;
        }

        // Intentionally independent CRC builder for verification of the
        // production integrity gate; do not reuse production CRC functions.
        static void Seal(byte[] frame,int id)
        {
            int header=frame[0]==0xFE?6:10;
            int crcIndex=header+frame[1];
            ushort crc=0xffff;
            for(int i=1;i<=crcIndex;i++)
            {
                byte value=i==crcIndex ? (byte)(id==0?50:39) : frame[i];
                byte b=(byte)(value^(byte)crc);
                b=(byte)(b ^ (byte)(b<<4));
                crc=(ushort)((crc>>8)^(b<<8)^(b<<3)^(b>>4));
            }
            frame[crcIndex]=(byte)crc;
            frame[crcIndex+1]=(byte)(crc>>8);
        }

        public static int Main()
        {
            try
            {
                int id;
                byte[] hb=Frame(0xFE,0,9);
                hb[6+8]=3; // MAVLink version is the last HEARTBEAT field.
                Seal(hb,0);
                Assert(AeroHelmReceiveOnlyBridge.ValidFrame(hb,hb.Length,out id),
                       "MAVLink v1 heartbeat envelope is valid");
                Assert(AeroHelmReceiveOnlyBridge.HasValidTelemetryChecksum(
                       hb,hb.Length,0), "nonempty HEARTBEAT has valid CRC");
                byte[] emptyHeartbeat=Frame(0xFE,0,0);
                Seal(emptyHeartbeat,0);
                Assert(!AeroHelmReceiveOnlyBridge.HasValidTelemetryChecksum(
                       emptyHeartbeat,emptyHeartbeat.Length,0),
                       "fabricated empty HEARTBEAT cannot mark cloud live");
                Assert(id==0,"heartbeat is ID 0");
                byte[] generic=Frame(0xFE,74,4);
                Assert(AeroHelmReceiveOnlyBridge.ValidFrame(generic,generic.Length,out id) &&
                       id==74,"generic telemetry must not be relabeled heartbeat");
                Assert(!AeroHelmReceiveOnlyBridge.ValidFrame(hb,hb.Length-1,out id),
                       "truncated v1 frame rejected");

                byte[] attitude=Frame(0xFD,30,28);
                Buffer.BlockCopy(BitConverter.GetBytes(0.25f),0,attitude,10+4,4);
                Buffer.BlockCopy(BitConverter.GetBytes(-0.1f),0,attitude,10+8,4);
                Seal(attitude,30);
                Assert(AeroHelmReceiveOnlyBridge.HasValidTelemetryChecksum(
                       attitude,attitude.Length,30),
                       "ATTITUDE CRC is independent evidence of frame integrity");
                byte[] tampered=(byte[])attitude.Clone();
                tampered[10+4]^=0x10;
                Assert(!AeroHelmReceiveOnlyBridge.HasValidTelemetryChecksum(
                       tampered,tampered.Length,30),
                       "corrupted ATTITUDE must not mark HUD telemetry live");
                float roll,pitch;
                Assert(AeroHelmReceiveOnlyBridge.TryReadAttitude(attitude,attitude.Length,
                       out roll,out pitch),"real MAVLink v2 ATTITUDE payload decoded");
                Assert(Math.Abs(roll-14.3239f)<0.01f &&
                       Math.Abs(pitch+5.72958f)<0.01f,
                       "radians converted to real degrees");
                Assert(!AeroHelmReceiveOnlyBridge.TryReadAttitude(generic,generic.Length,
                       out roll,out pitch),"generic frames cannot fake attitude");
                byte[] invalid=Frame(0xFD,30,28);
                Buffer.BlockCopy(BitConverter.GetBytes(float.NaN),0,invalid,10+4,4);
                Seal(invalid,30);
                Assert(!AeroHelmReceiveOnlyBridge.TryReadAttitude(invalid,invalid.Length,
                       out roll,out pitch),"NaN attitude rejected");
                byte[] shortPayload=Frame(0xFD,30,4);
                Assert(!AeroHelmReceiveOnlyBridge.TryReadAttitude(shortPayload,
                       shortPayload.Length,out roll,out pitch),
                       "short attitude payload rejected");
                byte[] signed=Frame(0xFD,30,28);
                signed[2]=1;
                Assert(!AeroHelmReceiveOnlyBridge.ValidFrame(signed,signed.Length,out id),
                       "signed MAVLink frame missing signature rejected");
                byte[] attitudeV1=Frame(0xFE,30,28);
                Buffer.BlockCopy(BitConverter.GetBytes(-0.12f),0,attitudeV1,6+4,4);
                Buffer.BlockCopy(BitConverter.GetBytes(0.07f),0,attitudeV1,6+8,4);
                Seal(attitudeV1,30);
                Assert(AeroHelmReceiveOnlyBridge.TryReadAttitude(
                    attitudeV1,attitudeV1.Length,out roll,out pitch),
                    "real MAVLink v1 attitude payload decoded");
                Assert(Math.Abs(roll+6.8755f)<0.01f &&
                       Math.Abs(pitch-4.0107f)<0.01f,
                       "MAVLink v1 roll and pitch preserve signs");
                byte[] unsupported=Frame(0xFD,30,28);
                unsupported[2]=0x80;
                Assert(!AeroHelmReceiveOnlyBridge.ValidFrame(
                    unsupported,unsupported.Length,out id),
                    "reject unknown MAVLink v2 incompatibility bit");
                var quality = new AeroHelmLinkQuality();
                long clock = System.Diagnostics.Stopwatch.Frequency;
                quality.ObserveAt(0, 100*clock);
                quality.ObserveAt(30, 100*clock);
                quality.ObserveAt(30, 101*clock);
                var fresh = quality.SnapshotAt(101*clock);
                Assert(fresh.HeartbeatFresh && fresh.AttitudeFresh,
                       "fresh monotonic HB/ATT samples pass");
                Assert(fresh.AttitudeHz>0 && fresh.HeartbeatHz>0,
                       "five-second rolling observed rates are nonzero");
                quality.ObserveAt(30, 105*clock);
                var gap = quality.SnapshotAt(105*clock);
                Assert(gap.AttitudeGapEvents==1 && gap.MaxAttitudeGapMs>=4000,
                       "real ATTITUDE interruption counted as a gap");
                quality.ObserveAt(30, 104*clock);
                Assert(quality.SnapshotAt(105*clock).AttitudeGapEvents==1,
                       "out-of-order time cannot roll diagnostics backwards");
                quality.RejectEvidence();
                Assert(quality.SnapshotAt(106*clock).RejectedEvidence==1,
                       "rejected evidence visible in diagnostics");
                var stale=quality.SnapshotAt(112*clock);
                Assert(!stale.HeartbeatFresh && !stale.AttitudeFresh &&
                       stale.AttitudeHz==0,
                       "prolonged outage blanks freshness and rolling rate");
                quality.Reset();
                var reset=quality.SnapshotAt(113*clock);
                Assert(!reset.HeartbeatFresh && !reset.AttitudeFresh &&
                       reset.AttitudeGapEvents==0 && reset.RejectedEvidence==0,
                       "new session cannot inherit previous live state");
                // Verify the exact production identity parser under net8.
                // It must not accept false, stringified booleans or a malformed digest.
                string hash = new string('a', 64);
                string validIdentity = "{\\"oracle_instance\\":{\\"configured\\":true,\\"verified\\":true,\\"instance_sha256\\":\\"" + hash + "\\"}}";
                Assert(VihangaLinkSession.VerifiedOracleDigest(validIdentity) == hash,
                       "private relay identity JSON is parsed by the real GCS session");
                foreach (string invalidJson in new[] {
                    "{}",
                    "{\\"oracle_instance\\":{\\"configured\\":false,\\"verified\\":true,\\"instance_sha256\\":\\"" + hash + "\\"}}",
                    "{\\"oracle_instance\\":{\\"configured\\":\\"true\\",\\"verified\\":true,\\"instance_sha256\\":\\"" + hash + "\\"}}",
                    "{\\"oracle_instance\\":{\\"configured\\":true,\\"verified\\":false,\\"instance_sha256\\":\\"" + hash + "\\"}}",
                    "{\\"oracle_instance\\":{\\"configured\\":true,\\"verified\\":true,\\"instance_sha256\\":\\"short\\"}}",
                })
                {
                    bool refused = false;
                    try { VihangaLinkSession.VerifiedOracleDigest(invalidJson); }
                    catch (InvalidOperationException) { refused = true; }
                    Assert(refused, "unverified/malformed Oracle VM identity rejected");
                }
                // A dedicated relay token must never replace the M1 JWT.
                global::MissionPlanner.Program.AeroHelmOperatorToken = "m1-original-token";
                global::MissionPlanner.Program.AeroHelmCloudOrigin = "https://m1-backend.ts.net";
                global::MissionPlanner.Program.AeroHelmTokenExpiresUtc = DateTime.UtcNow.AddMinutes(5);
                Environment.SetEnvironmentVariable("VIHANGA_LINK_ORIGIN",
                    "https://relay-backend.ts.net");
                try
                {
                    Assert(!VihangaLinkSession.Authenticated,
                        "dedicated relay may not inherit Oracle M1 JWT");
                    VihangaLinkSession.Accept(
                        "https://relay-backend.ts.net", "separate-relay-token", 600);
                    Assert(VihangaLinkSession.Authenticated &&
                           VihangaLinkSession.AccessToken == "separate-relay-token",
                        "dedicated JWT authenticates only the configured private relay");
                    Assert(global::MissionPlanner.Program.AeroHelmOperatorToken == "m1-original-token",
                        "relay sign-in must never overwrite Oracle M1 operator token");
                    var uri = AeroHelmReceiveOnlyBridge.PrivateWebSocketUri();
                    Assert(uri.Scheme == "wss" &&
                           uri.Host == "relay-backend.ts.net" &&
                           uri.AbsolutePath == "/ws/mavlink/view/AH-HW-001",
                        "native bridge uses exact dedicated private MAVLink viewer");
                    VihangaLinkSession.Clear();
                    Assert(!VihangaLinkSession.Authenticated,
                        "cleared relay JWT cannot remain authenticated");
                }
                finally
                {
                    VihangaLinkSession.Clear();
                    Environment.SetEnvironmentVariable("VIHANGA_LINK_ORIGIN", null);
                }
                Console.WriteLine("AeroHelm native bridge: PASS ("+count+" assertions)");
                return 0;
            }
            catch(Exception e)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }
        }
    }
}
