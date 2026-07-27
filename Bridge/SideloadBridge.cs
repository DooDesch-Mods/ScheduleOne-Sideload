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
    }
}
