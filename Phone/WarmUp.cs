using Sideload.Bundle;
using Sideload.Host;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Sideload.Phone
{
    /// <summary>
    /// Build one throwaway page while the scene is still coming up, so the first app a player opens does not pay
    /// for the framework's first use.
    ///
    /// <para>Measured on the live build, opening the same app twice in one session:</para>
    /// <code>
    ///   first open   html 56   css 25   script 201   render 488   = 770 ms
    ///   rebuild       html  0   css  1   script  15   render 106   = 122 ms
    /// </code>
    /// <para>The page is identical both times, so the 650 ms difference is not the app: it is AngleSharp and Jint
    /// being JIT-compiled, TextMeshPro resolving a font and building an atlas, and uGUI creating its first meshes
    /// and materials. Whoever opens the first app inherits all of it.</para>
    ///
    /// <para>Loading the assemblies at startup is not enough - that was tried and only moved the html phase from
    /// 168 ms to 56. The rest only warms by doing the real thing, which is why this builds an actual page through
    /// the actual pipeline instead of poking the parsers.</para>
    ///
    /// <para>Off screen rather than hidden: the layout measures text against the host rect, and a disabled object
    /// measures nothing. So the host is a real, active, correctly sized rect parked far outside the canvas.</para>
    /// </summary>
    internal static class WarmUp
    {
        private static bool _done;

        /// <summary>Runs once per game session. Safe to call whenever a canvas exists; later calls do nothing.</summary>
        internal static void Once(Transform appsCanvas)
        {
            if (_done || appsCanvas == null) return;

            _done = true;

            GameObject host = null;
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();

                host = new GameObject("SideloadWarmUp");
                RectTransform rect = host.AddComponent<RectTransform>();
                rect.SetParent(appsCanvas, false);
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(733f, 400f);       // an app's own viewport, so the same paths run
                rect.anchoredPosition = new Vector2(50000f, 0f);  // far outside anything the camera can see

                WebView view = WebView.Mount(rect, new AppBundle("sideload-warmup", "Sideload.Assets.warmup",
                                                                 typeof(Core).Assembly), "sideload-warmup");
                view?.EnsureBuilt();

                Core.Log?.Msg($"warmed the page pipeline in {watch.ElapsedMilliseconds} ms.");
            }
            catch (Exception e)
            {
                // A warm-up that fails costs nothing but the warmth. The first real page still builds itself.
                Core.Log?.Warning("warming the page pipeline failed: " + e.Message);
            }
            finally
            {
                // Destroying the host is also how the view lets go: WebView.TickAll drops any view whose root has
                // gone, releasing the script engine and its key registrations on the next frame.
                if (host != null) Object.Destroy(host);
            }
        }
    }
}
