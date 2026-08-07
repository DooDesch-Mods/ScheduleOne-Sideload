using System.Reflection;

namespace Sideload.Bridge
{
    /// <summary>
    /// The host side of the reflection handshake with Sideload.Api. The shim locates this type by name and reads the
    /// static delegate fields; every signature uses only BCL types, so the two assemblies share no type and the shim
    /// stays a zero-overhead no-op when Sideload is not installed.
    ///
    /// Adding a field is backwards compatible (an older shim ignores it). Changing an existing signature is not -
    /// bump <see cref="AbiVersion"/> in that case so old shims refuse to bind instead of failing halfway.
    /// </summary>
    public static class SideloadBridge
    {
        /// <summary>Bumped only on a breaking change to an existing delegate signature.</summary>
        public static readonly int AbiVersion = 1;

        /// <summary>id, title, iconLabel, bundlePrefix, hostAssembly.</summary>
        public static readonly Action<string, string, string, string, Assembly> RegisterApp = Registry.RegisterApp;

        /// <summary>appId, name, handler(appId, argument) -> answer. Backs `s1.call(name, arg)` in the page.
        /// A null appId registers the handler for every app.</summary>
        public static readonly Action<string, string, Func<string, string, string>> Handle = Script.Bridge.Handle;

        /// <summary>appId, name, payload. Backs `s1.on(name, fn)` in the page. A null appId reaches every app.</summary>
        public static readonly Action<string, string, string> Emit = Script.Bridge.Emit;

        /// <summary>appId, host. Lets one app's page reach one host with `fetch`. Nothing is reachable without it -
        /// the allowlist starts empty and stays that way unless the app's own mod adds to it.</summary>
        public static readonly Action<string, string> AllowHost = Net.HostAllowlist.Allow;

        /// <summary>appId, and the orientations the app supports as a comma-separated list in preference order -
        /// "landscape", "portrait" or "landscape,portrait". The first is what the app opens in; naming two is what
        /// lets the player turn it. A list rather than a second delegate so this signature never has to change.</summary>
        public static readonly Action<string, string> DeclareOrientations = Registry.DeclareOrientations;

        /// <summary>appId, count. The unread badge on the app's home-screen icon; zero clears it.</summary>
        public static readonly Action<string, int> SetBadge = Registry.SetBadge;

        /// <summary>appId, title, subtitle. Raises one of the game's own phone notifications, with this app's icon.</summary>
        public static readonly Action<string, string, string> Notify = Registry.Notify;

        /// <summary>appId -> is this app the one on screen right now. What a mod needs to decide whether an event is
        /// worth interrupting the player for, or whether they are already looking at it.</summary>
        public static readonly Func<string, bool> IsAppOnScreen = Registry.IsOnScreen;

        /// <summary>appId, name, PNG bytes. A picture the mod produced at runtime, for the page to draw with
        /// <c>src="s1://&lt;name&gt;"</c>. Null or empty bytes remove it.</summary>
        public static readonly Action<string, string, byte[]> SetImage = Paint.ImageCache.Supply;

        // ---------------------------------------------------------------- added after ABI 1 --
        // Fields only, no changed signatures, so AbiVersion stays 1: an older shim never looks these up and binds
        // exactly as before. A NEWER shim against an OLDER host is the case that matters, and it is covered by the
        // shim leaving its delegate null and reporting the capability as absent.

        /// <summary>appId, hidden. Suppresses the home-screen icon, for an app whose way in is somewhere else.
        /// With no icon, <see cref="SetAppOpen"/> is the only route in.</summary>
        public static readonly Action<string, bool> SetIconHidden = Registry.SetIconHidden;

        /// <summary>appId, open. Opens or closes an app as pressing its icon would - closing whatever else is open
        /// and turning the phone to this app's orientation.</summary>
        public static readonly Action<string, bool> SetAppOpen = Registry.SetAppOpen;

        /// <summary>appId -> is this the app the phone has open, whether or not the phone itself is up.</summary>
        public static readonly Func<string, bool> IsAppOpen = Registry.IsAppOpen;

        /// <summary>raised -> did the game allow it. Takes the phone out of the player's pocket or puts it away.
        /// Deliberately not part of opening an app: a background update must not raise the phone.</summary>
        public static readonly Func<bool, bool> SetPhoneRaised = Registry.SetPhoneRaised;

        /// <summary>Is the phone out and on its phone screen right now.</summary>
        public static readonly Func<bool> IsPhoneRaised = Registry.IsPhoneRaised;

        /// <summary>appId, key declaration, handler(appId, key) -> did you take it. A key that reaches the app with
        /// the phone in the player's pocket. When more than one app wants the same key it goes to whichever notified
        /// last; a handler returning false passes it to the next one. A null handler releases this app's keys.</summary>
        public static readonly Action<string, string, Func<string, string, bool>> ClaimKeys = Registry.ClaimKeys;

        // ------------------------------------------------------- companion mirror (added after ABI 1) --
        // For a server that serves the SAME bundle to a real phone. Read-only apart from Invoke, which is the call a
        // page already makes - none of this grants a capability that did not exist, it moves the existing surface to
        // a second screen. All of it is main-thread only.

        /// <summary>Every registered app as JSON: id, title, iconLabel, portrait, declaredPortrait, canTurn,
        /// iconless, badge.</summary>
        public static readonly Func<string> ListAppsJson = CompanionAccess.ListAppsJson;

        /// <summary>appId, bundle-relative path -> file bytes, or null. Resolved exactly as the in-game view does,
        /// so the Mods folder override wins.</summary>
        public static readonly Func<string, string, byte[]> ReadBundleFile = CompanionAccess.ReadBundleFile;

        /// <summary>Framework file by name - `s1.css`. The same bytes a page linking it receives.</summary>
        public static readonly Func<string, byte[]> ReadFrameworkAsset = CompanionAccess.ReadFrameworkAsset;

        /// <summary>appId, name -> the PNG behind <c>s1://&lt;name&gt;</c>, or null.</summary>
        public static readonly Func<string, string, byte[]> ReadRuntimeImage = CompanionAccess.ReadRuntimeImage;

        /// <summary>appId, name, argument -> answer. Runs the app's `s1.call` handler with no page involved.</summary>
        public static readonly Func<string, string, string, string> Invoke = CompanionAccess.Invoke;

        /// <summary>Taps on everything the host pushes at pages: emit(appId,name,payload), badge(appId,count),
        /// notify(appId,title,subtitle). Null clears one. Fires on the main thread inside whatever caused it, so a
        /// tap must queue and return.</summary>
        public static readonly Action<Action<string, string, string>, Action<string, int>, Action<string, string, string>>
            SetCompanionTaps = CompanionAccess.SetTaps;
    }
}
