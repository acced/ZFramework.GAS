using System;
using Unity.Profiling;

namespace ZFramework.GAS.Editor.Tests
{
    /// <summary>Unity Mono can return zero unconditionally from GetAllocatedBytesForCurrentThread. Probe the recorder instead.</summary>
    internal sealed class AllocationMeter : IDisposable
    {
        private ProfilerRecorder _recorder;
        private readonly bool _previousEnabled, _previousEditor;
        public AllocationMeter()
        {
            _previousEnabled = UnityEngine.Profiling.Profiler.enabled; _previousEditor = UnityEditorInternal.ProfilerDriver.profileEditor;
            UnityEngine.Profiling.Profiler.enabled = true; UnityEditorInternal.ProfilerDriver.profileEditor = true;
            _recorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC.Alloc", 1048576,
                ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
        }
        public long Stop()
        {
            _recorder.Stop(); if (!_recorder.Valid) return -1;
            int sampleCount = _recorder.Count;
            // Native recorder counts can exceed capacity; saturated samples are incomplete.
            if (sampleCount >= _recorder.Capacity) return -1;
            // GC.Alloc is a marker: Value is duration, NOT bytes. Count allocations only.
            long count = 0; for (int i = 0; i < sampleCount; i++) count += _recorder.GetSample(i).Count; return count;
        }
        public void Dispose()
        { _recorder.Dispose(); UnityEngine.Profiling.Profiler.enabled = _previousEnabled; UnityEditorInternal.ProfilerDriver.profileEditor = _previousEditor; }
        public static bool Works()
        {
            using var meter = new AllocationMeter(); var bytes = new byte[4096]; GC.KeepAlive(bytes);
            return meter.Stop() >= 1;
        }
    }
}
