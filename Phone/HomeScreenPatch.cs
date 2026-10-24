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

        /// <summary>
        /// Put an app on a phone that is already up. Registration usually happens at mod init, long before the home
        /// screen exists, and the postfix below spawns everything in one go - but an app can also be declared while
        /// the game runs (a project opened in a studio, a mod that unlocks one), and without this it would wait for
        /// the next scene load. Registering an id that is already live replaces it: the old host lets go of its game
        /// listeners, and building the new one drops the old panel and icon by name.
        /// </summary>
        internal static void SpawnLate(AppRegistration reg)
        {
            if (reg == null)
            {
                Core.Log?.Warning("SpawnLate called without a registration - nothing to spawn.");
                return;
            }

            if (_home == null)
            {
                Core.Log?.Msg($"'{reg.Id}' registered before the home screen exists - it spawns with the phone.");
                return;
            }

            if (!string.Equals(SceneManager.GetActiveScene().name, "Main", StringComparison.OrdinalIgnoreCase))
            {
                Core.Log?.Msg($"'{reg.Id}' registered outside the Main scene - it spawns with the phone.");
                return;
            }

            for (int i = _hosts.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(_hosts[i].Id, reg.Id, StringComparison.OrdinalIgnoreCase)) continue;
                _hosts[i].Release();
                _hosts.RemoveAt(i);
            }

            try
            {
                PhoneAppHost host = PhoneAppHost.Spawn(_home, reg);
                if (host == null)
                {
                    Core.Log?.Warning($"'{reg.Id}' could not be put on the running phone - see the line above.");
                    return;
                }

                _hosts.Add(host);
                if (_hosts.Count == 1) Prebuild.Listen();
                Core.Log?.Msg($"'{reg.Id}' put on the running phone.");
            }
            catch (Exception e)
            {
                Core.Log?.Error($"spawning '{reg.Id}' late failed: {e}");
            }
        }

        /// <summary>Take one app off the phone, if it is on it. Nothing to do before the home screen exists.</summary>
        internal static void Despawn(string appId)
        {
            bool removed = false;

            for (int i = _hosts.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(_hosts[i].Id, appId, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    _hosts[i].Teardown();
                    removed = true;
                }
                catch (Exception e) { Core.Log?.Error($"removing '{appId}' failed: {e.Message}"); }
                _hosts.RemoveAt(i);
            }

            if (removed) Core.Log?.Msg($"'{appId}' taken off the phone.");
            else Core.Log?.Msg($"'{appId}' was not on the phone - nothing to remove.");
        }

        /// <summary>Reload one app's page from disk, or every app's when the id is empty.</summary>
        internal static void Reload(string appId)
        {
            bool all = string.IsNullOrWhiteSpace(appId);
            int reloaded = 0;

            for (int i = 0; i < _hosts.Count; i++)
            {
                if (!_hosts[i].IsAlive) continue;
                if (!all && !string.Equals(_hosts[i].Id, appId, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    _hosts[i].Reload();
                    reloaded++;
                }
                catch (Exception e) { Core.Log?.Error($"reloading '{_hosts[i].Id}' failed: {e.Message}"); }
            }

            Core.Log?.Msg(reloaded > 0 ? $"reloaded {reloaded} app page(s) ({(all ? "all" : appId)})." : $"reload of '{appId}': no app on the phone to reload.");
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
            //
            // RELEASED, not merely dropped. Each host holds a GameInput exit listener and a closeApps handler, and
            // clearing this list does nothing about either - the game keeps calling them for the rest of the
            // session, on objects that no longer exist. One load cycle per app used to be one more corpse on
            // GameInput.exitListeners, and a tester's log reached 244 of them before anybody noticed.
            for (int i = 0; i < _hosts.Count; i++) _hosts[i]?.Release();
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

            if (_hosts.Count > 0) Prebuild.Listen();

#if DEBUG
            if (_hosts.Count > 0) Devtools.AutoOpen.Arm();
#endif
        }
    }
}
