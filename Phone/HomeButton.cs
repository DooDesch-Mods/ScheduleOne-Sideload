using UnityEngine;
using Il2CppScheduleOne;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;

namespace Sideload.Phone
{
    /// <summary>
    /// The home button on the phone's frame: a click on it closes the open app.
    ///
    /// The button is not a uGUI element and raises no event. It is a 3D collider named "Button" on the Overlay
    /// layer, and every vanilla app polls it itself: App&lt;T&gt;.Update casts a ray from the overlay camera through
    /// the mouse and closes itself when the primary click lands on that collider (App.cs:99-114). Sideload's apps
    /// do not derive from App&lt;T&gt;, so without this poll the button did nothing while one of them was open.
    /// </summary>
    internal static class HomeButton
    {
        /// <summary>The distance vanilla casts the ray: the phone sits right in front of the overlay camera.</summary>
        private const float RayLength = 2f;

        private const string ButtonName = "Button";

        private static int _overlayMask = -1;
        private static bool _warned;

        /// <summary>Called once a frame from the mod's update loop. Returns on the first check unless a click
        /// happened while one of our apps is on screen.</summary>
        internal static void Tick()
        {
            if (!Singleton<GameInput>.InstanceExists) return;
            if (!GameInput.GetButtonDown(GameInput.ButtonCode.PrimaryClick)) return;

            Press(GameInput.MousePosition);
        }

        /// <summary>A click at this screen point. Closes the app on screen when the point is on the home button and
        /// returns the id of the app it closed, or null.</summary>
        internal static string Press(Vector3 screenPoint)
        {
            PhoneAppHost host = ShowingApp();
            if (host == null) return null;

            try
            {
                Camera camera = OverlayCamera();
                if (camera == null || !TryOverlayMask(out int mask)) return null;

                Ray ray = camera.ScreenPointToRay(screenPoint);
                if (!Physics.Raycast(ray, out RaycastHit hit, RayLength, mask)) return null;
                if (hit.collider == null || hit.collider.gameObject.name != ButtonName) return null;

                Core.Log?.Msg($"home button closed '{host.Id}'.");
                host.Close();
                return host.Id;
            }
            catch (System.Exception e)
            {
                // Once per session: a game update that moved the button would otherwise log on every click.
                if (!_warned)
                {
                    _warned = true;
                    Core.Log?.Warning($"the home button check failed, the button will not close '{host.Id}': {e.Message}");
                }
                return null;
            }
        }

        private static Camera OverlayCamera()
        {
            if (!Singleton<GameplayMenu>.InstanceExists)
            {
                Core.Log?.Warning("home button: GameplayMenu is missing, the click is ignored.");
                return null;
            }

            Camera camera = Singleton<GameplayMenu>.Instance.OverlayCamera;
            if (camera == null) Core.Log?.Warning("home button: the overlay camera is missing, the click is ignored.");
            return camera;
        }

        private static bool TryOverlayMask(out int mask)
        {
            if (_overlayMask < 0)
            {
                int layer = LayerMask.NameToLayer("Overlay");
                if (layer < 0)
                {
                    Core.Log?.Warning("home button: the game has no Overlay layer, the click is ignored.");
                    mask = 0;
                    return false;
                }
                _overlayMask = 1 << layer;
            }

            mask = _overlayMask;
            return true;
        }

        /// <summary>The app the player is looking at right now, or null.</summary>
        private static PhoneAppHost ShowingApp()
        {
            IReadOnlyList<PhoneAppHost> hosts = HomeScreenPatch.Hosts;

            for (int i = 0; i < hosts.Count; i++)
                if (hosts[i].IsShowing) return hosts[i];

            return null;
        }

#if DEBUG
        /// <summary>
        /// Press the home button the way a click would, for automation that cannot move a mouse: find the collider,
        /// project its centre to the screen and send that point through <see cref="Press"/>, raycast included.
        /// </summary>
        internal static void DebugPress()
        {
            Camera camera = OverlayCamera();
            if (camera == null || !TryOverlayMask(out int mask))
            {
                Core.Log?.Msg("sideloadhome: FAIL no overlay camera or layer.");
                return;
            }

            Collider button = null;
            foreach (Collider c in Physics.OverlapSphere(camera.transform.position, RayLength, mask))
                if (c != null && c.gameObject.name == ButtonName) { button = c; break; }

            if (button == null)
            {
                Core.Log?.Msg("sideloadhome: FAIL no home button collider in front of the overlay camera (is the phone up?).");
                return;
            }

            PhoneAppHost before = ShowingApp();
            Vector3 point = camera.WorldToScreenPoint(button.bounds.center);
            string closed = Press(point);

            Core.Log?.Msg(closed != null
                ? $"sideloadhome: PASS closed '{closed}', active app now {(Il2CppScheduleOne.UI.Phone.Phone.ActiveApp == null ? "none" : Il2CppScheduleOne.UI.Phone.Phone.ActiveApp.name)}."
                : $"sideloadhome: FAIL nothing closed (app on screen: {(before == null ? "none" : before.Id)}, point {point}).");
        }
#endif
    }
}
