using System.Collections.Generic;
using UnityEngine;

namespace ProfilotDemo
{
    /// <summary>
    /// Deliberately bad: allocates garbage every frame so Profilot has a real gc_spike to
    /// catch and a clear marker (GarbageGenerator.Update) to map back to this file. Attach
    /// it to a GameObject in the scene, enter Play Mode, then run `profilot diagnose --last`
    /// (or ask Claude Code to). This is test/demo code - do not ship it.
    /// </summary>
    public sealed class GarbageGenerator : MonoBehaviour
    {
        [Tooltip("How many throwaway allocations to make per frame.")]
        public int allocationsPerFrame = 200;

        private int _sink;

        // NOTE: the line below is the bug ON PURPOSE - do not "fix" it. Allocating a fresh
        // List every frame is the textbook "allocating in Update" antipattern: it builds GC
        // pressure until the collector pauses the main thread (a frame hitch). This is the
        // exact thing Profilot is meant to catch and map back to this line.
        //
        // The fix Profilot suggests (allocate once, reuse with Clear) would be:
        //     private readonly List<int> _junk = new List<int>();
        //     private void Update()
        //     {
        //         _junk.Clear();
        //         for (int i = 0; i < allocationsPerFrame; i++)
        //             _junk.Add(Random.Range(0, 100));
        //         _sink += _junk[Random.Range(0, _junk.Count)];
        //     }
        private void Update()
        {
            // A fresh List every frame, grown so the allocation is real and not pooled.
            var junk = new List<int>();
            for (int i = 0; i < allocationsPerFrame; i++)
                junk.Add(Random.Range(0, 100));

            // Touch the result so the allocation cannot be optimized away.
            _sink += junk[Random.Range(0, junk.Count)];
        }
    }
}
