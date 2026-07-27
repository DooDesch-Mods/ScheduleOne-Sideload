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
    }
}
