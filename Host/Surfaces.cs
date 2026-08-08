using System.Reflection;
using UnityEngine;

namespace Sideload.Host
{
    /// <summary>
    /// A web bundle rendered somewhere that is not the phone: a column in the main menu, a panel on a machine, a
    /// board on a wall. The engine never needed the phone - <see cref="WebView"/> mounts into any RectTransform, and
    /// the phone is simply its first caller - but until now there was no door to it from outside.
    ///
    /// What a surface is NOT: an app. It has no home-screen icon, no orientation the player can turn, no badge and no
    /// notification. Those all belong to the phone. What it does share is the part that matters - the same renderer,
    /// the same CSS subset, the same <c>s1.call</c> / <c>s1.on</c> channel - so a page written for one works on the
    /// other with a different stylesheet.
    ///
    /// Lifetime is the caller's. A surface goes away when its panel is destroyed (the tick drops any view whose root
    /// is gone) or when <see cref="Unmount"/> is called; a scene reload therefore needs no bookkeeping here.
    /// </summary>
    internal static class Surfaces
    {
        private sealed class Mounted
        {
            internal string Id;
            internal WebView View;
        }

        private static readonly List<Mounted> _mounted = new List<Mounted>();

        /// <summary>
        /// Render a bundle into a panel.
        /// </summary>
        /// <param name="hostRect">A UnityEngine.RectTransform, passed as object so the API shim needs no Unity
        /// reference. Anything else is refused with a log line rather than a cast exception.</param>
        /// <param name="id">Stable id. Scopes <c>s1.call</c> handlers and <c>s1.storage</c>, and names the folder
        /// under Mods/ that overrides the embedded bundle - same rules as an app id, and the two share one namespace
        /// so a surface cannot quietly take an app's calls.</param>
        /// <param name="bundlePrefix">Embedded-resource prefix of the web files inside <paramref name="host"/>.</param>
        /// <param name="host">The assembly holding the bundle.</param>
        /// <param name="referenceShortSide">CSS pixels the panel's short side is worth, or 0 for device pixels.
        /// The phone fixes this at 400 because every app is written for the same panel; a surface has no such
        /// agreement, so it either names the width it was designed against or works in the panel's own units.</param>
        /// <returns>Whether a surface is now mounted under this id.</returns>
        internal static bool Mount(object hostRect, string id, string bundlePrefix, Assembly host, float referenceShortSide)
        {
            if (string.IsNullOrWhiteSpace(id) || host == null) return false;
            id = id.Trim();

            if (hostRect is not RectTransform rect || rect == null)
            {
                Core.Log?.Error($"surface '{id}': the mount target is not a live RectTransform.");
                return false;
            }

            // Remounting the same id is what a caller does when the menu scene came back, so it replaces rather than
            // stacking a second view on a panel nobody can see behind the first.
            Unmount(id);

            var bundle = new Bundle.AppBundle(id, bundlePrefix ?? "", host);
            WebView view = WebView.Mount(rect, bundle, id, referenceShortSide);
            if (view == null) return false;

            view.AutoBuild = true;
            _mounted.Add(new Mounted { Id = id, View = view });
            Core.Log?.Msg($"surface mounted: {id} from {host.GetName().Name}"
                          + (referenceShortSide > 0f ? $" (short side {referenceShortSide:0.#} css px)" : " (device pixels)"));
            return true;
        }

        /// <summary>Take a surface down now. Safe to call for an id that was never mounted.</summary>
        internal static void Unmount(string id)
        {
            for (int i = _mounted.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(_mounted[i].Id, id, StringComparison.OrdinalIgnoreCase)) continue;
                try { _mounted[i].View?.Dispose(); }
                catch (Exception e) { Core.Log?.Warning($"surface '{id}' teardown: {e.Message}"); }
                _mounted.RemoveAt(i);
            }
        }

        /// <summary>
        /// Whether a surface under this id is on screen right now.
        ///
        /// Answers false for one whose panel was destroyed without an Unmount - a scene reload does exactly that -
        /// so a caller can use this to decide whether to mount again instead of tracking scene loads itself.
        /// </summary>
        internal static bool IsMounted(string id)
        {
            for (int i = _mounted.Count - 1; i >= 0; i--)
            {
                Mounted m = _mounted[i];
                if (!string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
                if (m.View?.Root != null) return true;
                _mounted.RemoveAt(i);   // its panel is gone; the view already dropped itself on the last tick
                return false;
            }
            return false;
        }
    }
}
