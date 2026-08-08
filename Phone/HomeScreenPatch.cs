using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Il2CppScheduleOne.UI.Phone;

namespace Sideload.Phone
{
    /// <summary>
    /// Turns every registered app into a live phone app once the gameplay home screen starts.
    ///
    /// HomeScreen.Start is the right moment because that is when AppsCanvas, the icon container and the vanilla app
    /// panels all exist. The prologue scene has its own cut-down phone, so registration is limited to "Main" - the same
    /// restriction S1API applies, for the same reason.
    /// </summary>
    [HarmonyPatch(typeof(HomeScreen), "Start")]
    internal static class HomeScreenPatch
    {
        private static readonly List<PhoneAppHost> _hosts = new List<PhoneAppHost>();
        private static HomeScreen _home;

        /// <summary>The apps currently live on the phone.</summary>
        internal static IReadOnlyList<PhoneAppHost> Hosts => _hosts;

        /// <summary>
        /// Debug aid: name every child of the icon container the game itself spawns into, so the probe and the spawn
        /// path can never disagree about which of the phone's several look-alike containers is the real one.
        /// </summary>
        internal static void LogIconRow()
        {
            if (_home == null) { Core.Log?.Warning("[Sideload/probe] no HomeScreen to inspect."); return; }

            Transform icons = _home.appIconContainer;
            if (icons == null) { Core.Log?.Warning("[Sideload/probe] HomeScreen exposes no icon container."); return; }

            var names = new List<string>();
            for (int i = 0; i < icons.childCount; i++) names.Add(icons.GetChild(i).name);
            Core.Log?.Msg($"[Sideload/probe] real AppIcons: {icons.childCount} child(ren) -> {string.Join(", ", names)}");
        }

        private static void Postfix(HomeScreen __instance)
        {
            if (__instance == null) return;
            _home = __instance;
            if (!string.Equals(SceneManager.GetActiveScene().name, "Main", StringComparison.OrdinalIgnoreCase)) return;

            // Start runs once per HomeScreen, and this build has more than one - a second, mirrored phone hierarchy.
            // Spawning into both put every app on the row twice. Whichever comes first wins; the rest are skipped
            // while its panels are still alive.
            if (_hosts.Count > 0 && _hosts[0].IsAlive)
            {
                Core.Log?.Msg("apps are already live on a phone - skipping this second HomeScreen.");
                return;
            }

            // Returning to the menu and re-hosting runs Start again against a fresh hierarchy; the old hosts point at
            // destroyed objects, so drop them and rebuild rather than reusing.
            _hosts.Clear();

#if DEBUG
            Devtools.Probe.LogFonts();
            Devtools.Probe.LogAppPanels(__instance.transform.parent != null ? __instance.transform.parent.Find("AppsCanvas") : null);
#endif

            if (Registry.Apps.Count == 0)
            {
                Core.Log?.Msg("no apps registered - nothing to put on the phone.");
                return;
            }

            foreach (AppRegistration reg in Registry.Apps)
            {
                try
                {
                    PhoneAppHost host = PhoneAppHost.Spawn(__instance, reg);
                    if (host != null) _hosts.Add(host);
                }
                catch (Exception e)
                {
                    Core.Log?.Error($"spawning '{reg.Id}' failed: {e}");
                }
            }

            Core.Log?.Msg($"{_hosts.Count}/{Registry.Apps.Count} app(s) live on the phone.");

#if DEBUG
            if (_hosts.Count > 0) Devtools.AutoOpen.Arm();
#endif
        }
    }
}
