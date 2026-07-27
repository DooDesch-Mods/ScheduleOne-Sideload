#if SNITCH
using Snitch.Api;
using Sideload.Devtools;
using Sideload.Host;

namespace Sideload.Profiling
{
    /// <summary>
    /// Sideload's Snitch panel: the engine's live state and its dev controls, inside the overlay the player already
    /// has, on the master key they already know.
    ///
    /// This replaces a second overlay of our own. One overlay beats two - a mod that paints its own HUD next to
    /// Snitch's is exactly the fragmentation Hotline was built to end - and it comes with the web dashboard and the
    /// timeline for free, so a rebuild storm shows up as a graph rather than as a number that moves too fast to read.
    ///
    /// Auto-discovered by the Snitch host on bind (a no-op when the host is absent). Compiled only under the SNITCH
    /// symbol - Debug plus EnableSnitch, see Workspace/build/Snitch.props.
    /// </summary>
    internal static class SnitchProbe
    {
        public static void Register()
        {
            Panel p = Profiler.RegisterPanel("Sideload", "Sideload");

            // ----- live gauges -----
            // Boxes is the size of the page; renders is the number that matters. A page that changes one label
            // should render once. If this climbs every frame, something marks the DOM dirty on every tick.
            p.Counter("Pages", () => WebView.Live.Count, "views");
            p.Counter("Boxes", () => Sum(v => v.BoxCount), "boxes");
            p.Counter("Renders", () => Sum(v => v.RenderCount), "total");
            p.Counter("RenderTime", () => WebView.Live.Count == 0 ? 0d : WebView.Live[^1].LastRenderMs, "ms");
            p.Counter("Reloads", () => Sum(v => v.ReloadCount), "total");

            // Where the render budget goes, by page - the distribution answers "which app is expensive" without
            // needing a second gauge per app.
            p.State(() =>
            {
                var snapshot = new StateSnapshot { Title = "Sideload boxes" };
                foreach (WebView view in WebView.Live) snapshot.Add(view.AppId, view.BoxCount);
                return snapshot;
            });

            // ----- readout -----
            // The two questions that actually come up while building an app: did my edit reach the game, and is the
            // script alive. Both are one glance here.
            p.Text(() =>
            {
                if (WebView.Live.Count == 0) return "no page mounted";

                var sb = new System.Text.StringBuilder();
                foreach (WebView view in WebView.Live)
                    sb.Append(view.Stats).Append('\n').Append("  ").Append(view.WatchReportPlain()).Append("\n\n");
                return sb.ToString().TrimEnd();
            });

            // ----- controls -----
            p.Toggle("Outline boxes", () => LayoutOverlay.Outlines, on =>
            {
                LayoutOverlay.Outlines = on;
                foreach (WebView view in WebView.Live) view.DebugRebuild();
            });

            p.Action("Rebuild pages", () =>
            {
                foreach (WebView view in WebView.Live) view.DebugRebuild();
                p.Write("rebuilt from the document in memory");
            });

            p.Action("Reload from disk", () =>
            {
                foreach (WebView view in WebView.Live) view.DebugReload();
                p.Write("reloaded every page from its bundle");
            });

            p.Action("Dump layout", () =>
            {
                foreach (WebView view in WebView.Live) view.DebugDumpLayout();
                p.Write("computed tree written to the MelonLoader log");
            });

            p.Log();

            // A page that rebuilds is the thing worth measuring, so make it a lever: with rendering off, whatever
            // frame time remains is the game's, not ours.
            Profiler.RegisterAblationLever("sideload.render",
                apply: () => WebView.RenderingDisabled = true,
                restore: () => WebView.RenderingDisabled = false);
        }

        private static double Sum(Func<WebView, double> read)
        {
            double total = 0;
            foreach (WebView view in WebView.Live) total += read(view);
            return total;
        }
    }
}
#endif
