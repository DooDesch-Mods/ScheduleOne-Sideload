using System;

namespace Sideload.Profiling
{
    /// <summary>
    /// A named section of the per-frame path, timed when Snitch is compiled in and free otherwise.
    ///
    /// The reason this exists: Snitch could only ever time the render, so everything else Sideload does every frame -
    /// the script pump, the keyboard reclaim, the transitions - was charged to the containing MelonLoader
    /// <c>OnUpdate</c> with no way to tell which of them cost the time. A profiler that names the container and not
    /// the culprit sends the reader back to reading code, which is where an afternoon goes.
    ///
    /// Call sites are identical in both configurations: <c>using (Phase.Of("sideload.script")) { ... }</c>. Without
    /// the SNITCH symbol this is an empty struct whose Dispose does nothing, so the JIT removes it and no call site
    /// needs an #if of its own.
    /// </summary>
    internal static class Phase
    {
#if SNITCH
        internal static Snitch.Api.Scope Of(string label) => Snitch.Api.Profiler.Sample(label);
#else
        internal static Idle Of(string label) => default;

        /// <summary>Nothing, shaped like a scope so the using statement compiles.</summary>
        internal readonly struct Idle : IDisposable
        {
            public void Dispose() { }
        }
#endif
    }
}
