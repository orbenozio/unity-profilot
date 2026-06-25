using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

namespace Profilot.Editor
{
    /// <summary>
    /// Phase 0.5 spike (SPEC.md section 17 / risk R1). Verifies, in isolation, that
    /// ProfilerDriver + HierarchyFrameDataView actually return a usable per-marker
    /// hierarchy on this Unity version BEFORE Phase 2 builds the real frame-capture on
    /// top of them. ProfilerDriver lives in UnityEditorInternal and is flagged as a
    /// deprecation risk - this menu item is the early-warning probe, not product code.
    /// </summary>
    internal static class FrameDumpSpike
    {
        private const int MaxRows = 15;

        [MenuItem("Tools/Profilot/Debug/Dump Last Frame To Console")]
        private static void DumpLastFrame()
        {
            int lastFrame = ProfilerDriver.lastFrameIndex;
            if (lastFrame < 0)
            {
                Debug.LogWarning("[Profilot] No profiler frames captured yet. Enter Play Mode with the Profiler window recording, then run this again.");
                return;
            }

            // Thread 0 is the main thread. Sort by total time, descending.
            HierarchyFrameDataView frameView = ProfilerDriver.GetHierarchyFrameDataView(
                lastFrame,
                0,
                HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                HierarchyFrameDataView.columnTotalTime,
                false);

            if (frameView == null || !frameView.valid)
            {
                Debug.LogWarning($"[Profilot] Frame {lastFrame} has no valid hierarchy view. The frame may already have been pushed out of the profiler ring buffer (see SPEC.md, ring-buffer race).");
                return;
            }

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"[Profilot] Frame {lastFrame} - {frameView.frameTimeMs:F2} ms. Top main-thread markers by total time:");

                int rootId = frameView.GetRootItemID();
                var children = new List<int>();
                frameView.GetItemChildren(rootId, children);

                int shown = 0;
                foreach (int id in children)
                {
                    if (shown >= MaxRows)
                        break;

                    string name = frameView.GetItemName(id);
                    float total = frameView.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnTotalTime);
                    float self = frameView.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnSelfTime);
                    float gcAlloc = frameView.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnGcMemory);

                    sb.AppendLine($"  {name,-40} total {total,8:F3} ms | self {self,8:F3} ms | gc {gcAlloc,11:F0} B");
                    shown++;
                }

                Debug.Log(sb.ToString());
            }
            finally
            {
                frameView.Dispose();
            }
        }
    }
}
