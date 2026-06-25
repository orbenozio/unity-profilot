using System.Collections.Generic;

namespace Profilot
{
    /// <summary>
    /// One detected anomaly, published by the runtime tripwire and consumed by the editor
    /// capture service. Carries the cheap counter snapshot taken at trip time so the event
    /// record has usable numbers even if the full-frame fetch later comes back partial.
    /// </summary>
    public readonly struct TripSignal
    {
        public readonly string Type;        // frame_hitch | gc_spike | draw_calls (SPEC.md section 14)
        public readonly string Metric;      // frameTimeMs | gcAllocBytes | drawCalls
        public readonly double Value;
        public readonly double Budget;
        public readonly int FrameCount;     // Time.frameCount at the trip
        public readonly int RepeatCount;    // repeats folded in by the cooldown gate (dedup.count - 1)

        // Cheap counter snapshot at trip time.
        public readonly double FrameTimeMs;
        public readonly long GcAllocBytes;
        public readonly double DrawCalls;

        public TripSignal(string type, string metric, double value, double budget, int frameCount,
            int repeatCount, double frameTimeMs, long gcAllocBytes, double drawCalls)
        {
            Type = type;
            Metric = metric;
            Value = value;
            Budget = budget;
            FrameCount = frameCount;
            RepeatCount = repeatCount;
            FrameTimeMs = frameTimeMs;
            GcAllocBytes = gcAllocBytes;
            DrawCalls = drawCalls;
        }
    }

    /// <summary>
    /// In-memory hand-off from the runtime tripwire (Player loop) to the editor capture
    /// service. In Play-in-Editor both live in the same process and AppDomain, so this is a
    /// plain shared queue - this is the internal trip channel from SPEC.md section 12,
    /// distinct from the file-based event store the CLI reads. The editor drains it on the
    /// next EditorApplication.update so the problem frame is fetched before it is pushed out
    /// of the profiler ring buffer (SPEC.md section 15).
    /// </summary>
    public static class ProfilotTripChannel
    {
        private static readonly Queue<TripSignal> Pending = new Queue<TripSignal>();
        private static readonly object Gate = new object();

        public static void Publish(in TripSignal signal)
        {
            lock (Gate)
            {
                Pending.Enqueue(signal);
            }
        }

        /// <summary>Moves all pending signals into <paramref name="into"/>; returns how many.</summary>
        public static int Drain(List<TripSignal> into)
        {
            lock (Gate)
            {
                int count = Pending.Count;
                while (Pending.Count > 0)
                    into.Add(Pending.Dequeue());
                return count;
            }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                Pending.Clear();
            }
        }
    }
}
