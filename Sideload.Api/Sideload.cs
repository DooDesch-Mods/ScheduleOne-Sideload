using System;
using System.Collections.Generic;
using System.Reflection;

namespace Sideload.Api
{
    /// <summary>
    /// The Sideload framework's modder API. Reference Sideload.Api.dll OR drop this single file into your mod.
    /// Register an app and Sideload renders your HTML/CSS/JS bundle as a real phone app - no uGUI assembly by hand,
    /// no layout arithmetic, hot-reloadable while the game runs.
    ///
    /// Every call is a zero-overhead no-op when Sideload is not installed and lights up automatically when it is, so
    /// you can ship this unconditionally with no hard dependency. Check <see cref="Available"/> only if you want to
    /// fall back to your own UI.
    ///
    /// <code>
    ///   using Sideload.Api;
    ///   Apps.Register("prophunt", "PropHunt.App", title: "PropHunt", iconLabel: "PropHunt");
    /// </code>
    ///
    /// The bundle prefix is the embedded-resource prefix of your web files inside your own assembly, so
    /// <c>PropHunt/App/index.html</c> embedded as <c>PropHunt.App.index.html</c> is reached with prefix
    /// <c>"PropHunt.App"</c>. A file at <c>Mods/&lt;id&gt;/index.html</c> overrides the embedded copy.
    ///
    /// All calls MUST be made from the Unity main thread.
    /// </summary>
    public static class Apps
    {
        private static bool _bound;
        private static int _probeAttempts;
        private static readonly List<Action> _pending = new List<Action>();

        private static Action<string, string, string, string, Assembly> _registerApp;
        private static Action<string, string, Func<string, string, string>> _handle;
        private static Action<string, string, string> _emit;
        private static Action<string, string> _allowHost;
        private static Action<string, string> _declareOrientations;
        private static Action<string, int> _setBadge;
        private static Action<string, string, string> _notify;
        private static Action<string, string, string, float> _notifyFor;
        private static Func<string, bool> _isOnScreen;
        private static Action<string, string, byte[]> _setImage;
        private static Action<string, bool> _setIconHidden;
        private static Action<string, bool> _setAppOpen;
        private static Func<string, bool> _isAppOpen;

        /// <summary>True only when the Sideload host is installed AND bound. You rarely need this - the API is a safe
        /// no-op when absent; use it to decide whether to build a fallback UI instead.</summary>
        public static bool Available { get { EnsureBound(); return _bound; } }

        /// <summary>
        /// Declare an app. It appears on the in-game phone with its own icon and renders
        /// <c>index.html</c> from your bundle. Load-order-proof: registering before Sideload has loaded is fine, the
        /// call is replayed once the host appears.
        /// </summary>
        /// <param name="id">Stable, unique id. Also the folder name under Mods/ that overrides the embedded files.</param>
        /// <param name="bundlePrefix">Embedded-resource prefix of your web files inside the calling assembly.</param>
        /// <param name="title">App title. Defaults to <paramref name="id"/>.</param>
        /// <param name="iconLabel">Caption under the home-screen icon. Defaults to the title.</param>
        /// <param name="hostAssembly">The assembly holding the embedded bundle. Defaults to the caller's assembly;
        /// pass it explicitly if you wrap this call in a helper of your own.</param>
        public static AppHandle Register(string id, string bundlePrefix, string title = null, string iconLabel = null,
                                         Assembly hostAssembly = null)
        {
            var handle = new AppHandle(id);
            if (string.IsNullOrEmpty(id)) return handle;

            Assembly asm = hostAssembly ?? Assembly.GetCallingAssembly();
            string t = title, il = iconLabel, prefix = bundlePrefix;

            EnsureBound();
            if (_registerApp != null) _registerApp(id, t, il, prefix, asm);
            else _pending.Add(() => _registerApp?.Invoke(id, t, il, prefix, asm));
            return handle;
        }

        /// <summary>Queue work until the host is there, or run it now if it already is. Everything an AppHandle does
        /// goes through here, so a mod can wire its whole app up in OnInitializeMelon regardless of load order.</summary>
        internal static void WhenBound(Action work)
        {
            if (work == null) return;

            EnsureBound();
            if (_bound) work();
            else _pending.Add(work);
        }

        internal static void HandleCall(string appId, string name, Func<string, string> handler)
        {
            if (_handle == null || handler == null) return;
            _handle(appId, name, (app, argument) => handler(argument));
        }

        internal static void EmitEvent(string appId, string name, string payload) => _emit?.Invoke(appId, name, payload);

        internal static void AllowNetHost(string appId, string host) => _allowHost?.Invoke(appId, host);

        internal static void Orient(string appId, string orientations) => _declareOrientations?.Invoke(appId, orientations);

        internal static void Badge(string appId, int count) => _setBadge?.Invoke(appId, count);

        /// <summary>
        /// Falls back to the three-argument host method when the installed Sideload predates NotifyFor: the
        /// notification still goes out, just at Sideload's own duration rather than the one that was asked for.
        /// </summary>
        internal static void Notify(string appId, string title, string subtitle, float seconds)
        {
            if (_notifyFor != null) _notifyFor(appId, title, subtitle, seconds);
            else if (_notify != null) _notify(appId, title, subtitle);
        }

        internal static bool OnScreen(string appId) => _isOnScreen != null && _isOnScreen(appId);

        internal static void SetImage(string appId, string name, byte[] png) => _setImage?.Invoke(appId, name, png);

        internal static void HideIcon(string appId, bool hidden) => _setIconHidden?.Invoke(appId, hidden);

        internal static void OpenApp(string appId, bool open) => _setAppOpen?.Invoke(appId, open);

        internal static bool AppIsOpen(string appId) => _isAppOpen != null && _isAppOpen(appId);

        /// <summary>
        /// Whether the installed host understands iconless apps and programmatic opening. False against an older
        /// Sideload - and a mod that supplies its own way in has to notice, because registering an app it cannot open
        /// on a host that gives it no icon leaves the player with an app they can never reach.
        /// </summary>
        internal static bool HasOpen { get { EnsureBound(); return _setAppOpen != null && _setIconHidden != null; } }

        // ----- reflection handshake (runs until it binds, then latches) -----

        private static void EnsureBound()
        {
            if (_bound) return;   // bound once, never probe again (fast path)
            try
            {
                Type t = FindBridge((_probeAttempts++ % 30) == 0);
                if (t == null) return;   // host not present yet - cheap re-probe next call (load-order proof)

                object abi = t.GetField("AbiVersion", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (abi is int v && v < 1) return;

                _registerApp = Get<Action<string, string, string, string, Assembly>>(t, "RegisterApp");
                if (_registerApp == null) return;   // partial table - try again next call

                // Added after ABI 1, so an older host simply leaves these null and the calls stay no-ops.
                _handle = Get<Action<string, string, Func<string, string, string>>>(t, "Handle");
                _emit = Get<Action<string, string, string>>(t, "Emit");
                _allowHost = Get<Action<string, string>>(t, "AllowHost");
                _declareOrientations = Get<Action<string, string>>(t, "DeclareOrientations");
                _setBadge = Get<Action<string, int>>(t, "SetBadge");
                _notify = Get<Action<string, string, string>>(t, "Notify");
                _isOnScreen = Get<Func<string, bool>>(t, "IsAppOnScreen");
                _setImage = Get<Action<string, string, byte[]>>(t, "SetImage");
                _setIconHidden = Get<Action<string, bool>>(t, "SetIconHidden");
                _setAppOpen = Get<Action<string, bool>>(t, "SetAppOpen");
                _isAppOpen = Get<Func<string, bool>>(t, "IsAppOpen");

                _bound = true;

                for (int i = 0; i < _pending.Count; i++) { try { _pending[i](); } catch { } }
                _pending.Clear();
            }
            catch { /* any failure -> stays a no-op, retries next call */ }
        }

        private static T Get<T>(Type t, string field) where T : class
        {
            object v = t.GetField(field, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            return v as T;   // works because Func<>/Action<> are shared BCL types in both assemblies
        }

        private static Type FindBridge(bool scan)
        {
            Type t = Type.GetType("Sideload.Bridge.SideloadBridge, Sideload", false);
            if (t != null || !scan) return t;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType("Sideload.Bridge.SideloadBridge", false); if (t != null) return t; }
                catch { }
            }
            return null;
        }
    }

    /// <summary>Handle to a registered app: the two-way channel between your C# and the app's JavaScript. Kept
    /// deliberately thin; every method stays a no-op when Sideload is absent.</summary>
    public sealed class AppHandle
    {
        private readonly string _id;
        internal AppHandle(string id) { _id = id ?? ""; }

        /// <summary>The id this app was registered under.</summary>
        public string Id => _id;

        /// <summary>
        /// Answer <c>s1.call("&lt;name&gt;", arg)</c> from this app's page. The handler runs on the Unity main thread in
        /// the same frame as the call, so it may touch game state directly; whatever it returns is the call's value.
        /// Strings cross the boundary - send JSON for anything structured.
        /// <code>
        ///   app.OnCall("chat.threads", _ =&gt; Json.Of(Chat.Threads));
        ///   app.OnCall("chat.send", text =&gt; { Chat.Send(text); return "ok"; });
        /// </code>
        /// </summary>
        public AppHandle OnCall(string name, Func<string, string> handler)
        {
            string id = _id;
            Apps.WhenBound(() => Apps.HandleCall(id, name, handler));
            return this;
        }

        /// <summary>Push an event at this app's page, where <c>s1.on("&lt;name&gt;", fn)</c> is waiting for it. Use it when
        /// the game changes something the page did not ask for - a message arriving, a timer expiring.</summary>
        public void Emit(string name, string payload = "")
        {
            string id = _id;
            Apps.WhenBound(() => Apps.EmitEvent(id, name, payload));
        }

        /// <summary>
        /// Let this app's page reach one host with <c>fetch</c>. Without at least one of these the page reaches
        /// nothing: the allowlist starts empty and only the app's own mod can add to it, so a web bundle edited in the
        /// Mods folder can never talk to somewhere you did not name here.
        ///
        /// Give a bare host name. <c>*.example.com</c> covers any single label under it, but not
        /// <c>example.com</c> itself. Ports are ignored, and the scheme must be https unless the host is
        /// <c>127.0.0.1</c> or <c>localhost</c>.
        /// <code>
        ///   Apps.Register("mystash", "MyMod.Assets.mystash")
        ///       .AllowHost("api.example.com")
        ///       .AllowHost("*.cdn.example.com");
        /// </code>
        /// </summary>
        public AppHandle AllowHost(string host)
        {
            string id = _id;
            Apps.WhenBound(() => Apps.AllowNetHost(id, host));
            return this;
        }

        /// <summary>
        /// Which ways round the phone may hold this app, in preference order. The FIRST one is what the app opens in;
        /// naming a second is what lets the player turn it, with the rotate keys the game already binds. Say nothing
        /// and the app is landscape only, which is the only safe reading of silence - an app that never styled
        /// portrait must not be turned into it.
        ///
        /// Both are worth styling. Sideload evaluates <c>@media (orientation: portrait|landscape)</c> against the
        /// real viewport shape, so one stylesheet covers both without any script. The player's choice is remembered
        /// per app; you do not have to store it.
        /// <code>
        ///   .Orientation("landscape")               // landscape only, the phone never turns
        ///   .Orientation("portrait")                // portrait only
        ///   .Orientation("landscape", "portrait")   // both, opens landscape
        ///   .Orientation("portrait", "landscape")   // both, opens portrait
        /// </code>
        /// </summary>
        public AppHandle Orientation(params string[] supported)
        {
            string id = _id;
            string list = supported == null ? "" : string.Join(",", supported);
            Apps.WhenBound(() => Apps.Orient(id, list));
            return this;
        }

        /// <summary>
        /// The unread count on this app's home-screen icon - the same red badge the vanilla apps use. Zero clears it.
        /// Counts above 99 read as "99+".
        ///
        /// Set it whenever your own count changes, not on a timer: the value is remembered across a phone rebuild, so
        /// setting it once is enough and setting it again is cheap.
        /// <code>
        ///   app.Badge(unreadMessages);
        /// </code>
        /// </summary>
        public AppHandle Badge(int count)
        {
            string id = _id;
            Apps.WhenBound(() => Apps.Badge(id, count));
            return this;
        }

        /// <summary>
        /// Raise one of the game's own phone notifications - the slide-in the vanilla apps use, carrying this app's
        /// icon. Nothing happens if the app is not on a phone yet.
        ///
        /// This interrupts whatever the player is doing, so spend it on what they would want to be interrupted for.
        /// A count that can wait belongs in <see cref="Badge"/>.
        /// <code>
        ///   app.Notify("Jessi Waters", "on my way");
        /// </code>
        /// </summary>
        /// <param name="seconds">
        /// How long it stays up. Leave it at zero for Sideload's own timing, which suits a headline plus a
        /// sentence. Raise it for something the player has to act on, lower it for a passing remark. Clamped to
        /// between 2 and 30 seconds - the slide-in cannot be dismissed, so an app does not get to hold the corner
        /// of the screen. Ignored, without failing, on a Sideload too old to have it.
        /// </param>
        public AppHandle Notify(string title, string subtitle = "", float seconds = 0f)
        {
            string id = _id;
            Apps.WhenBound(() => Apps.Notify(id, title, subtitle, seconds));
            return this;
        }

        /// <summary>
        /// Whether this app is the one the phone is showing right now. Ask before interrupting: an event the player
        /// is already watching happen does not deserve a notification, and the same event with the phone in their
        /// pocket does. False when Sideload is absent or the app is not on a phone yet.
        /// <code>
        ///   if (!app.IsOnScreen) app.Notify(sender, text);
        /// </code>
        /// </summary>
        public bool IsOnScreen { get { return Apps.Available && Apps.OnScreen(_id); } }

        /// <summary>
        /// Hand a picture your mod produced at runtime to the page, which draws it with
        /// <c>&lt;img src="s1://&lt;name&gt;"&gt;</c>. Null or empty bytes remove it, which is how you say "there is no
        /// picture for this one" and let the page fall back to whatever it draws without one.
        ///
        /// PNG bytes rather than a texture, because this file references no Unity type and is not going to start.
        /// Supplying the same name again replaces the picture.
        /// <code>
        ///   app.Image("avatar/" + steamId, pngBytes);
        /// </code>
        /// </summary>
        public AppHandle Image(string name, byte[] png)
        {
            string id = _id;
            Apps.WhenBound(() => Apps.SetImage(id, name, png));
            return this;
        }

        /// <summary>
        /// Give this app no home-screen icon. For an app whose way in already exists somewhere else - a vanilla icon
        /// your mod has taken over, a world object, another app handing off.
        ///
        /// With no icon, <see cref="Open"/> is the ONLY way in: call it from wherever your entry point is, or the app
        /// is unreachable. Check <see cref="CanOpenProgrammatically"/> first - against an older Sideload this is a
        /// no-op and the app would get an icon you did not plan for.
        /// <code>
        ///   Apps.Register("reflash-messages", "Reflash.Assets.reflash-messages", "Messages").NoIcon();
        /// </code>
        /// </summary>
        public AppHandle NoIcon()
        {
            string id = _id;
            Apps.WhenBound(() => Apps.HideIcon(id, true));
            return this;
        }

        /// <summary>
        /// Open this app as if the player had pressed its icon: whatever else is open closes first, and the phone
        /// turns to this app's orientation. Does nothing while the app is not on a phone - before the home screen
        /// exists there is nothing to open.
        /// <code>
        ///   if (playerPressedTheHijackedIcon) app.Open();
        /// </code>
        /// </summary>
        public AppHandle Open()
        {
            string id = _id;
            Apps.WhenBound(() => Apps.OpenApp(id, true));
            return this;
        }

        /// <summary>Close this app, returning the phone to its home screen. Does nothing if it is not the open one.</summary>
        public AppHandle Close()
        {
            string id = _id;
            Apps.WhenBound(() => Apps.OpenApp(id, false));
            return this;
        }

        /// <summary>
        /// Whether this app is the one the phone has open - true even with the phone in the player's pocket. For
        /// "can they actually see it", use <see cref="IsOnScreen"/>.
        /// </summary>
        public bool IsOpen { get { return Apps.Available && Apps.AppIsOpen(_id); } }

        /// <summary>
        /// Whether the installed Sideload understands <see cref="NoIcon"/> and <see cref="Open"/>. False against an
        /// older host, where both are silent no-ops.
        ///
        /// A mod that provides its own entry point must check this before registering anything: on an older host it
        /// would hide nothing, open nothing, and leave the player with apps they cannot reach. Refuse to set up and
        /// say which version is needed - that is a fixable message, an unreachable app is not.
        /// <code>
        ///   if (!AppHandle.CanOpenProgrammatically) { Log.Error("needs Sideload 1.1.0 or newer"); return; }
        /// </code>
        /// </summary>
        public static bool CanOpenProgrammatically { get { return Apps.Available && Apps.HasOpen; } }
    }
}
