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

        public static int Main()
        {
            try
            {
                int id;
                byte[] hb=Frame(0xFE,0,0);
                Assert(AeroHelmReceiveOnlyBridge.ValidFrame(hb,hb.Length,out id),
                       "MAVLink v1 heartbeat envelope is valid");
                Assert(id==0,"heartbeat is ID 0");
                byte[] generic=Frame(0xFE,74,4);
                Assert(AeroHelmReceiveOnlyBridge.ValidFrame(generic,generic.Length,out id) &&
                       id==74,"generic telemetry must not be relabeled heartbeat");
                Assert(!AeroHelmReceiveOnlyBridge.ValidFrame(hb,hb.Length-1,out id),
                       "truncated v1 frame rejected");

                byte[] attitude=Frame(0xFD,30,28);
                Buffer.BlockCopy(BitConverter.GetBytes(0.25f),0,attitude,10+4,4);
                Buffer.BlockCopy(BitConverter.GetBytes(-0.1f),0,attitude,10+8,4);
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
