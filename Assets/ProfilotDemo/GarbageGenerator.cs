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
