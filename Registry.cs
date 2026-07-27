using System.Reflection;

namespace Sideload
{
    /// <summary>
    /// One app declared by a host mod. Created by <see cref="Registry.RegisterApp"/> (via the Sideload.Api shim)
    /// during mod init, long before the phone exists; <see cref="Phone.HomeScreenPatch"/> turns each entry into a
    /// live panel and home-screen icon once the gameplay scene's HomeScreen starts.
    /// </summary>
    internal sealed class AppRegistration
    {
        /// <summary>Stable id, unique per app. Doubles as the GameObject name and the override folder name.</summary>
        internal string Id;

        /// <summary>Human-readable app title.</summary>
        internal string Title;

        /// <summary>Caption under the home-screen icon.</summary>
        internal string IconLabel;

        /// <summary>Embedded-resource prefix of the web bundle inside <see cref="HostAssembly"/>.</summary>
        internal string BundlePrefix;

        /// <summary>The host mod's assembly - the web files are embedded resources in it.</summary>
        internal Assembly HostAssembly;

        /// <summary>Resolves bundle paths for this app (embedded resource, overridden by a file on disk).</summary>
        internal Bundle.AppBundle Bundle;

        /// <summary>Home-screen picture, built on first use by <see cref="Phone.AppIconSprite"/> and kept for reuse.</summary>
        internal UnityEngine.Sprite IconSprite;

        /// <summary>Which way the phone is holding this app right now.</summary>
        internal bool Portrait;

        /// <summary>
        /// The orientation the app named FIRST - what it opens in when the player has no saved preference. Kept apart
        /// from <see cref="Portrait"/> so "what may this app do" never depends on what it happens to be doing.
        /// Landscape by default: it is the wider viewport, and an app that says nothing has only styled that one.
        /// </summary>
        internal bool DeclaredPortrait;

        /// <summary>
        /// Whether the app declared both orientations. Only an app that says so may be turned - by the player or by
        /// its own page - because turning one that never styled the other shape produces a screen the player is
        /// stuck in.
        /// </summary>
        internal bool CanTurn;

        /// <summary>True when this app is willing to be held the given way round.</summary>
        internal bool Supports(bool portrait) => CanTurn || DeclaredPortrait == portrait;

        /// <summary>
        /// Unread count on the home-screen icon. Held here rather than on the live host because the phone is rebuilt
        /// on a scene change and the count is not: a mod sets it once and it survives being respawned.
        /// </summary>
        internal int Badge;

        /// <summary>Raised when <see cref="Registry.SetOrientation"/> turns a live app.</summary>
        internal Action OrientationChanged;
    }

    /// <summary>
    /// The set of apps host mods have declared. Registration happens at mod-init time and is pure bookkeeping - no
    /// Unity object is touched here, so load order between Sideload and its consumers does not matter.
    /// </summary>
    internal static class Registry
    {
        private static readonly List<AppRegistration> _apps = new List<AppRegistration>();

        internal static IReadOnlyList<AppRegistration> Apps => _apps;

        /// <summary>
        /// Declare an app. Called through the reflection bridge by Sideload.Api, so every parameter is a plain BCL
        /// type. A duplicate id replaces the earlier entry rather than adding a second icon for the same app.
        /// </summary>
        internal static void RegisterApp(string id, string title, string iconLabel, string bundlePrefix, Assembly host)
        {
            if (string.IsNullOrWhiteSpace(id) || host == null) return;

            var reg = new AppRegistration
            {
                Id = id.Trim(),
                Title = string.IsNullOrWhiteSpace(title) ? id : title,
                IconLabel = string.IsNullOrWhiteSpace(iconLabel) ? (string.IsNullOrWhiteSpace(title) ? id : title) : iconLabel,
                BundlePrefix = bundlePrefix ?? "",
                HostAssembly = host,
            };
            reg.Bundle = new Bundle.AppBundle(reg.Id, reg.BundlePrefix, host);

            for (int i = 0; i < _apps.Count; i++)
            {
                if (string.Equals(_apps[i].Id, reg.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _apps[i] = reg;
                    Core.Log?.Warning($"[Sideload] app '{reg.Id}' registered twice; the later registration wins.");
                    return;
                }
            }

            _apps.Add(reg);
            Core.Log?.Msg($"[Sideload] app registered: {reg.Id} ('{reg.Title}') from {host.GetName().Name}");
        }

        /// <summary>
        /// Declare which orientations an app supports, in preference order: the first is what it opens in, and naming
        /// a second is what permits turning at all. Called through the bridge by the mod, so the set arrives as one
        /// comma-separated string and the delegate signature never has to change.
        ///
        /// Naming nothing recognisable leaves the app landscape-only, which is what an app that never mentions
        /// orientation gets - and the only safe reading of silence.
        /// </summary>
        internal static void DeclareOrientations(string appId, string orientations)
        {
            AppRegistration reg = Find(appId);
            if (reg == null) { Core.Log?.Warning($"[Sideload] orientation: no app '{appId}'."); return; }

            Model.OrientationSet set = Model.OrientationSet.Parse(orientations);

            foreach (string bad in set.Ignored)
                Core.Log?.Warning($"[Sideload] '{appId}' declared orientation '{bad}', which is neither 'portrait' " +
                                  "nor 'landscape' - ignored.");

            if (!set.Declared) return;

            reg.DeclaredPortrait = set.Portrait;
            reg.CanTurn = set.CanTurn;
            reg.Portrait = set.Portrait;
        }

        /// <summary>
        /// Turn a live app. Reached from three places - the player's rotate key, <c>s1.setOrientation</c> in the page,
        /// and the remembered choice at spawn - and all three are refused the same way when the app never declared
        /// that orientation.
        /// </summary>
        internal static void SetOrientation(string appId, string orientation)
        {
            AppRegistration reg = Find(appId);
            if (reg == null) { Core.Log?.Warning($"[Sideload] setOrientation: no app '{appId}'."); return; }

            bool portrait = string.Equals((orientation ?? "").Trim(), "portrait", StringComparison.OrdinalIgnoreCase);
            if (reg.Portrait == portrait) return;

            if (!reg.Supports(portrait))
            {
                Core.Log?.Warning($"[Sideload] '{reg.Id}' was asked for {(portrait ? "portrait" : "landscape")} but " +
                                  "only declared the other one - ignored. Declare both to allow turning: " +
                                  "Apps.Register(...).Orientation(\"landscape\", \"portrait\").");
                return;
            }

            reg.Portrait = portrait;
            reg.OrientationChanged?.Invoke();
        }

        /// <summary>
        /// Put an unread count on an app's home-screen icon, zero to clear it. Recorded even when the phone does not
        /// exist yet, so a mod may set it during init and it appears the moment the app is spawned.
        /// </summary>
        internal static void SetBadge(string appId, int count)
        {
            AppRegistration reg = Find(appId);
            if (reg == null) { Core.Log?.Warning($"[Sideload] badge: no app '{appId}'."); return; }

            reg.Badge = Math.Max(0, count);

            Phone.PhoneAppHost host = LiveHost(appId);
            if (host != null) host.SetBadge(reg.Badge);
        }

        /// <summary>
        /// Raise one of the game's own phone notifications for an app. Silently does nothing when the app is not on a
        /// phone yet - a notification with nothing behind it is worse than none.
        /// </summary>
        internal static void Notify(string appId, string title, string subtitle)
        {
            Phone.PhoneAppHost host = LiveHost(appId);
            if (host == null) return;

            host.Notify(title, subtitle);
        }

        /// <summary>
        /// Whether this app is the one the phone is showing. A mod asks before interrupting: a message arriving in
        /// the conversation already on screen is not worth a notification, and the same message arriving while the
        /// phone is in the player's pocket is.
        /// </summary>
        internal static bool IsOnScreen(string appId)
        {
            Phone.PhoneAppHost host = LiveHost(appId);
            return host != null && host.IsShowing;
        }

        private static Phone.PhoneAppHost LiveHost(string appId)
        {
            IReadOnlyList<Phone.PhoneAppHost> hosts = Phone.HomeScreenPatch.Hosts;

            for (int i = 0; i < hosts.Count; i++)
                if (string.Equals(hosts[i].Id, appId, StringComparison.OrdinalIgnoreCase) && hosts[i].IsAlive)
                    return hosts[i];

            return null;
        }

        /// <summary>The registration for an id, or null. Case-insensitive, like every other id comparison here.</summary>
        internal static AppRegistration Find(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId)) return null;

            for (int i = 0; i < _apps.Count; i++)
                if (string.Equals(_apps[i].Id, appId, StringComparison.OrdinalIgnoreCase)) return _apps[i];

            return null;
        }
    }
}
