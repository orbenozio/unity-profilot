using NUnit.Framework;
using Profilot.Editor;

namespace Profilot.EditorTests
{
    /// <summary>
    /// The rule that decides whether a profiler frame is the one that tripped (V-1). A frame must
    /// breach the trip's own threshold and sit close to the trip's value on the trip's metric.
    /// </summary>
    public class FrameMatchTests
    {
        private static TripSignal Hitch(double frameMs, double budget) =>
            new TripSignal("frame_hitch", "frameTimeMs", frameMs, budget, 1, 0, frameMs, 0, 0);

        private static TripSignal Gc(long bytes) =>
            new TripSignal("gc_spike", "gcAllocBytes", bytes, 0, 1, 0, 5, bytes, 0);

        private static TripSignal Draws(double draws, double budget) =>
            new TripSignal("draw_calls", "drawCalls", draws, budget, 1, 0, 5, 0, draws);

        private static ProfilotEventCapture.FrameStats Frame(double ms, double gc = 0, double draws = -1, bool counter = true) =>
            new ProfilotEventCapture.FrameStats { Valid = true, FrameMs = ms, Gc = gc, GcFromCounter = counter, Draws = draws };

        [Test]
        public void A_mild_hitch_does_not_match_a_normal_neighbour()
        {
            // baseline 7 ms -> trips above 14 ms; 0.7 matched the 8 ms neighbour (7/15 = 0.47).
            Assert.IsFalse(ProfilotEventCapture.MatchFrame(Hitch(15, 14), Frame(8), out _));
            Assert.IsTrue(ProfilotEventCapture.MatchFrame(Hitch(15, 14), Frame(15.5), out _));
        }

        [Test]
        public void A_hitch_candidate_must_breach_the_threshold_itself()
        {
            Assert.IsFalse(ProfilotEventCapture.MatchFrame(Hitch(15, 14), Frame(13.9), out _));
        }

        [Test]
        public void Draw_calls_match_on_the_draw_counter_not_frame_time()
        {
            // Same frame time as the trip, but a normal draw count: not the frame.
            Assert.IsFalse(ProfilotEventCapture.MatchFrame(Draws(400, 200), Frame(5, draws: 120), out _));
            Assert.IsTrue(ProfilotEventCapture.MatchFrame(Draws(400, 200), Frame(5, draws: 390), out _));
            // No readable counter: never claims a match.
            Assert.IsFalse(ProfilotEventCapture.MatchFrame(Draws(400, 200), Frame(5), out _));
        }

        [Test]
        public void A_small_leak_does_not_match_any_frame_through_the_slack()
        {
            // 20 KB trip vs a 2 KB frame through the hierarchy fallback: the flat 64 KB slack
            // made this distance 0. The slack is now capped at half the trip.
            Assert.IsFalse(ProfilotEventCapture.MatchFrame(Gc(20_000), Frame(5, gc: 2_000, counter: false), out _));
            Assert.IsTrue(ProfilotEventCapture.MatchFrame(Gc(20_000), Frame(5, gc: 14_000, counter: false), out _));
            Assert.AreEqual(10_000, ProfilotEventCapture.GcSlack(20_000));
            Assert.AreEqual(64 * 1024, ProfilotEventCapture.GcSlack(10_000_000));
        }

        [Test]
        public void The_gc_counter_is_compared_exactly()
        {
            Assert.IsTrue(ProfilotEventCapture.MatchFrame(Gc(1_000_000), Frame(5, gc: 1_000_000), out double exact));
            Assert.IsFalse(ProfilotEventCapture.MatchFrame(Gc(1_000_000), Frame(5, gc: 600_000), out _));
            Assert.Less(exact, 0.01);
        }

        [Test]
        public void An_invalid_frame_never_matches()
        {
            var st = Frame(15);
            st.Valid = false;
            Assert.IsFalse(ProfilotEventCapture.MatchFrame(Hitch(15, 14), st, out _));
        }
    }
}
