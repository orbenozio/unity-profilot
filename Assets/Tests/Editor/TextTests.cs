using NUnit.Framework;
using Profilot.Editor;

namespace Profilot.EditorTests
{
    /// <summary>The plain-language strings on event cards and notifications.</summary>
    public class TextTests
    {
        [Test]
        public void Values_carry_their_unit()
        {
            Assert.AreEqual("36.2 ms", ProfilotText.FormatValue("frameTimeMs", 36.24));
            Assert.AreEqual("1.2 MB", ProfilotText.FormatValue("gcAllocBytes", 1_250_000));
            Assert.AreEqual("15 KB", ProfilotText.FormatValue("gcAllocBytes", 15_360));
            Assert.AreEqual("312 draw calls", ProfilotText.FormatValue("drawCalls", 312));
        }

        [Test]
        public void Cards_say_what_happened()
        {
            Assert.AreEqual("Frame took 48 ms (trips above 18.2 ms)", ProfilotText.Describe("frame_hitch", "frameTimeMs", 48, 18.2));
            Assert.AreEqual("Allocated 1.2 MB in one frame", ProfilotText.Describe("gc_spike", "gcAllocBytes", 1_250_000, 0));
        }

        [Test]
        public void Markers_and_runs_read_cleanly()
        {
            Assert.AreEqual("GarbageGenerator.Update",
                ProfilotText.CleanMarker("Assembly-CSharp.dll!ProfilotDemo::GarbageGenerator.Update() [Invoke]"));
            Assert.AreEqual("Nov 4, 14:32:05", ProfilotText.RunLabel("2026-11-04_14-32-05"));
            Assert.AreEqual("session", ProfilotText.RunLabel("session"));
        }
    }
}
