using UnityEngine;

namespace Profilot.PlayTests
{
    /// <summary>
    /// Golden-scenario components (SPEC.md section 17 phase 3, M3/M5). Each is deliberately
    /// bad in ONE specific, well-known way, so the calibration harness can assert both that
    /// Profilot caught the right event type (M5 recall) and that the captured dominant marker
    /// names the responsible method (M3 mapping accuracy). They live in the test assembly (it
    /// cannot reference Assembly-CSharp, where the ProfilotDemo lives) and their class names
    /// are the responsible markers the harness looks for. Not shipped.
    /// </summary>
    public sealed class GcSpikeScenario : MonoBehaviour
    {
        // Tuned to a sweet spot: large enough to out-allocate the PlayMode test runner's own
        // per-frame allocations (its TestRunnerCoroutine spikes ~220KB), so PickBestFrame maps
        // the gc_spike back here - but small enough that the GC collection pause stays under
        // the tripwire's hitch floor, so the frame is classified gc_spike and not frame_hitch.
        public int bytesPerFrame = 1_000_000;
        private long _sink;

        // Bug on purpose: a fresh heap allocation every frame (the textbook "allocating in
        // Update"). Builds GC pressure -> a gc_spike whose allocating marker is
        // GcSpikeScenario.Update.
        private void Update()
        {
            var junk = new byte[bytesPerFrame];
            junk[0] = 1;
            junk[junk.Length - 1] = 2;
            _sink += junk[0] + junk[junk.Length - 1];
        }
    }

    /// <summary>
    /// A synchronous stall on the main thread (stands in for synchronous load or heavy compute
    /// in Update). Allocation-free on purpose, so it trips the frame_hitch path and not
    /// gc_spike; the responsible marker is SyncHitchScenario.Update.
    ///
    /// It waits <see cref="startAfterFrames"/> frames before stalling, so the tripwire's frame
    /// baseline seeds on normal frames first (a stall from frame 0 would make the baseline
    /// itself huge and nothing would ever trip). After that it stalls EVERY frame: a single
    /// isolated hitch can age out of the editor-side capture window under fast frame rates
    /// (SPEC.md phase 3 finding), so a sustained heavy stretch is what reliably validates
    /// hitch detection + marker mapping - just as the GC scenario allocates every frame.
    /// </summary>
    public sealed class SyncHitchScenario : MonoBehaviour
    {
        public int startAfterFrames = 75;
        public double stallMs = 40.0;

        private int _frame;
        private double _sink;

        private void Update()
        {
            _frame++;
            if (_frame < startAfterFrames)
                return;

            // Busy-loop the main thread for ~stallMs. Uses realtime, not Stopwatch, to stay
            // allocation-free so the frame trips as a hitch rather than a gc_spike.
            double start = Time.realtimeSinceStartupAsDouble;
            double acc = 0;
            double budget = stallMs / 1000.0;
            while (Time.realtimeSinceStartupAsDouble - start < budget)
                acc += System.Math.Sqrt(acc + 1.0);
            _sink += acc;
        }
    }

    /// <summary>
    /// A MILD, isolated hitch: steady work every frame raises the baseline, and every
    /// <see cref="everyFrames"/> frames one frame takes about 2.5x as long. This is the common
    /// case the 0.6 matcher got wrong - a normal neighbouring frame "matched" the trip.
    /// </summary>
    public sealed class MildHitchScenario : MonoBehaviour
    {
        public int startAfterFrames = 75;
        public int everyFrames = 30;
        public double baseMs = 6.0;
        public double hitchMs = 16.0;

        private int _frame;
        private double _sink;

        private void Update()
        {
            _frame++;
            bool hitch = _frame >= startAfterFrames && _frame % everyFrames == 0;
            Burn(hitch ? hitchMs : baseMs);
        }

        private void Burn(double ms)
        {
            double start = Time.realtimeSinceStartupAsDouble;
            double acc = 0;
            while (Time.realtimeSinceStartupAsDouble - start < ms / 1000.0)
                acc += System.Math.Sqrt(acc + 1.0);
            _sink += acc;
        }
    }

    /// <summary>
    /// A draw-call spike: after <see cref="startAfterFrames"/> frames, a burst of renderers with
    /// their own materials (so nothing batches) appears for a few frames, then hides again.
    /// Only meaningful where the player loop renders; headless runs may not draw at all.
    /// </summary>
    public sealed class DrawCallSpikeScenario : MonoBehaviour
    {
        public int startAfterFrames = 90;
        public int burstFrames = 4;
        public int renderers = 400;

        private int _frame;
        private GameObject[] _objects;

        private void Start()
        {
            var cam = new GameObject("draw-call-camera").AddComponent<Camera>();
            cam.transform.SetParent(transform);
            cam.transform.position = new Vector3(0, 0, -30);

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            _objects = new GameObject[renderers];
            for (int i = 0; i < renderers; i++)
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.transform.SetParent(transform);
                go.transform.position = new Vector3((i % 20) - 10, (i / 20) - 10, 0);
                var mat = new Material(shader) { color = new Color(i / (float)renderers, 0.5f, 0.5f) };
                go.GetComponent<Renderer>().sharedMaterial = mat;
                go.SetActive(false);
                _objects[i] = go;
            }
        }

        private void Update()
        {
            _frame++;
            bool on = _frame >= startAfterFrames && _frame < startAfterFrames + burstFrames;
            foreach (GameObject go in _objects)
                if (go.activeSelf != on)
                    go.SetActive(on);
        }
    }
}
