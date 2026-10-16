using Il2CppScheduleOne.Persistence;
using UnityEngine;

namespace Sideload.Phone
{
    /// <summary>
    /// Build every app's page while the loading screen is still up, so the first open of an app shows a finished
    /// page instead of stalling the frame it was opened on.
    ///
    /// <para>The moment is <c>LoadManager.onLoadComplete</c>. Vanilla invokes it once the save is loaded and the
    /// local player exists, then keeps the loading screen up for another two seconds before closing it - so the page
    /// sees real game data, and the frames it costs are frames nobody watches. Earlier, at <c>HomeScreen.Start</c>,
    /// the save has not been read yet and a page would build against an empty game.</para>
    ///
    /// <para>A page only measures text under an active object, so each container is switched on for the build with
    /// its alpha at zero and raycasts off, and switched off again afterwards. The panel is never registered as the
    /// phone's open app; nothing the player or the game can see changes.</para>
    /// </summary>
    internal static class Prebuild
    {
        private static bool _listening;

        /// <summary>Subscribe once per game session. LoadManager is a persistent singleton, so one listener covers
        /// every later load as well.</summary>
        internal static void Listen()
        {
            if (_listening) return;

            try
            {
                if (!LoadManager.InstanceExists)
                {
                    Core.Log?.Warning("no LoadManager - app pages build on first open instead.");
                    return;
                }

                LoadManager.Instance.onLoadComplete.AddListener((UnityEngine.Events.UnityAction)All);
                _listening = true;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("could not listen for the end of loading - app pages build on first open instead: " + e.Message);
            }
        }

        private static void All()
        {
            IReadOnlyList<PhoneAppHost> hosts = HomeScreenPatch.Hosts;
            if (hosts.Count == 0) { Core.Log?.Msg("load complete - no app pages to prebuild."); return; }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            int built = 0;

            for (int i = 0; i < hosts.Count; i++)
            {
                PhoneAppHost host = hosts[i];
                if (host == null) continue;

                try { if (host.Prebuild()) built++; }
                catch (Exception e) { Core.Log?.Error($"prebuilding '{host.Id}' failed: {e}"); }
            }

            Core.Log?.Msg($"prebuilt {built}/{hosts.Count} app page(s) behind the loading screen in {watch.ElapsedMilliseconds} ms.");
        }
    }
}
