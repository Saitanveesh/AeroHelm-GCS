// AeroHelm receive-only link diagnostics. Observability is not flight authority.
// Uses Stopwatch ticks: wall-clock changes never make stale telemetry appear fresh.
// No network I/O, FC commands, timers, secrets or background reconnect.
using System;
using System.Diagnostics;

namespace AeroHelm.GcsCloud
{
    internal sealed class AeroHelmLinkQuality
    {
        private const int WindowSeconds = 5;
        private const int BucketCount = 8;
        private const long Missing = -1;
        private readonly object sync = new object();
        private readonly long[] seconds = new long[BucketCount];
        private readonly int[] frames = new int[BucketCount];
        private readonly int[] heartbeats = new int[BucketCount];
        private readonly int[] attitudes = new int[BucketCount];
        private long lastTick;
        private long lastHeartbeat;
        private long lastAttitude;
        private long attitudeGaps;
        private long largestAttitudeGapMs;
        private long rejectedEvidence;

        internal AeroHelmLinkQuality() { Reset(); }

        internal void Reset()
        {
            lock (sync)
            {
                for (int i = 0; i < BucketCount; i++)
                {
                    seconds[i] = Missing;
                    frames[i] = heartbeats[i] = attitudes[i] = 0;
                }
                lastTick = lastHeartbeat = lastAttitude = Missing;
                attitudeGaps = largestAttitudeGapMs = rejectedEvidence = 0;
            }
        }

        internal void Observe(int messageId) { ObserveAt(messageId, Stopwatch.GetTimestamp()); }

        // Injection point for deterministic offline tests. Never use UTC to
        // decide whether a packet is live: NTP can change the wall clock.
        internal void ObserveAt(int messageId, long tick)
        {
            if (tick < 0) throw new ArgumentOutOfRangeException("tick");
            lock (sync)
            {
                if (lastTick != Missing && tick < lastTick)
                    return; // a backwards sample cannot revive telemetry
                lastTick = tick;
                long second = tick / Stopwatch.Frequency;
                int slot = (int)(second % BucketCount);
                if (seconds[slot] != second)
                {
                    seconds[slot] = second;
                    frames[slot] = heartbeats[slot] = attitudes[slot] = 0;
                }
                frames[slot]++;
                if (messageId == 0)
                {
                    heartbeats[slot]++;
                    lastHeartbeat = tick;
                }
                else if (messageId == 30)
                {
                    attitudes[slot]++;
                    if (lastAttitude != Missing)
                    {
                        long gap = (long)((tick - lastAttitude) * 1000.0 /
                                          Stopwatch.Frequency);
                        if (gap > largestAttitudeGapMs) largestAttitudeGapMs = gap;
                        if (gap > 2500) attitudeGaps++;
                    }
                    lastAttitude = tick;
                }
            }
        }

        internal void RejectEvidence()
        {
            lock (sync) rejectedEvidence++;
        }

        internal AeroHelmQualitySnapshot Snapshot()
        {
            return SnapshotAt(Stopwatch.GetTimestamp());
        }

        internal AeroHelmQualitySnapshot SnapshotAt(long tick)
        {
            if (tick < 0) throw new ArgumentOutOfRangeException("tick");
            lock (sync)
            {
                long currentSecond = tick / Stopwatch.Frequency;
                int f = 0, h = 0, a = 0;
                for (int i = 0; i < BucketCount; i++)
                {
                    if (seconds[i] != Missing && seconds[i] <= currentSecond &&
                        currentSecond - seconds[i] < WindowSeconds)
                    {
                        f += frames[i];
                        h += heartbeats[i];
                        a += attitudes[i];
                    }
                }
                long frameAge = AgeMs(tick, lastTick);
                long hbAge = AgeMs(tick, lastHeartbeat);
                long attAge = AgeMs(tick, lastAttitude);
                return new AeroHelmQualitySnapshot(
                    hbAge >= 0 && hbAge <= 5000,
                    attAge >= 0 && attAge <= 5000,
                    f / (double)WindowSeconds, h / (double)WindowSeconds,
                    a / (double)WindowSeconds, frameAge, hbAge, attAge,
                    attitudeGaps, largestAttitudeGapMs, rejectedEvidence);
            }
        }

        private static long AgeMs(long now, long then)
        {
            if (then == Missing || now < then) return Missing;
            return (long)((now - then) * 1000.0 / Stopwatch.Frequency);
        }
    }

    internal sealed class AeroHelmQualitySnapshot
    {
        internal readonly bool HeartbeatFresh;
        internal readonly bool AttitudeFresh;
        internal readonly double FrameHz;
        internal readonly double HeartbeatHz;
        internal readonly double AttitudeHz;
        internal readonly long FrameAgeMs;
        internal readonly long HeartbeatAgeMs;
        internal readonly long AttitudeAgeMs;
        internal readonly long AttitudeGapEvents;
        internal readonly long MaxAttitudeGapMs;
        internal readonly long RejectedEvidence;

        internal AeroHelmQualitySnapshot(bool heartbeatFresh, bool attitudeFresh,
            double frameHz, double heartbeatHz, double attitudeHz,
            long frameAgeMs, long heartbeatAgeMs, long attitudeAgeMs, long attitudeGapEvents,
            long maxAttitudeGapMs, long rejectedEvidence)
        {
            HeartbeatFresh = heartbeatFresh;
            AttitudeFresh = attitudeFresh;
            FrameHz = frameHz;
            HeartbeatHz = heartbeatHz;
            AttitudeHz = attitudeHz;
            FrameAgeMs = frameAgeMs;
            HeartbeatAgeMs = heartbeatAgeMs;
            AttitudeAgeMs = attitudeAgeMs;
            AttitudeGapEvents = attitudeGapEvents;
            MaxAttitudeGapMs = maxAttitudeGapMs;
            RejectedEvidence = rejectedEvidence;
        }
    }
}
