// Copyright (c) 2026 DooDesch. DooDesch API License (see LICENSE.md): you may copy this file unchanged.
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
    /// <remarks>
    /// Ids are compared case-insensitively and trimmed. Call and event names are compared case-insensitively.
    /// A bundle path is resolved in two places, and the file on disk wins: <c>Mods/&lt;id&gt;/&lt;path&gt;</c>, then the
    /// embedded resource <c>&lt;bundlePrefix&gt;.&lt;path with / turned into .&gt;</c>. So <c>css/app.css</c> with
    /// prefix <c>"MyMod.Assets.mystash"</c> is the resource <c>MyMod.Assets.mystash.css.app.css</c>.
    /// </remarks>
    /// <example>
    /// The csproj embeds the web files under the prefix you pass to <see cref="Register"/>:
    /// <code>
    /// &lt;EmbeddedResource Include="Assets/mystash/*"&gt;
    ///   &lt;LogicalName&gt;MyMod.Assets.mystash.%(Filename)%(Extension)&lt;/LogicalName&gt;
    /// &lt;/EmbeddedResource&gt;
    /// </code>
    /// Then in <c>OnInitializeMelon</c>:
    /// <code>
    /// Apps.Register("mystash", "MyMod.Assets.mystash", title: "Stash", iconLabel: "Stash")
    ///     .OnCall("items.list", _ =&gt; string.Join("\n", Items))
    ///     .OnCall("items.add", text =&gt; { Items.Add(text.Trim()); return "ok"; });
    /// </code>
    /// </example>
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
        private static Func<bool, bool> _setPhoneRaised;
        private static Func<bool> _isPhoneRaised;
        private static Action<string, string, Func<string, string, bool>> _claimKeys;
        private static Func<object, string, string, Assembly, float, bool> _mountSurface;
        private static Action<string> _unmountSurface;
        private static Func<string, bool> _isSurfaceMounted;

        /// <summary>True only when the Sideload host is installed AND bound. You rarely need this - the API is a safe
        /// no-op when absent; use it to decide whether to build a fallback UI instead.</summary>
        public static bool Available { get { EnsureBound(); return _bound; } }

        /// <summary>
        /// Declare an app. It appears on the in-game phone with its own icon and renders
        /// <c>index.html</c> from your bundle. Load-order-proof: registering before Sideload has loaded is fine, the
        /// call is replayed once the host appears.
        /// </summary>
        /// <param name="id">Stable, unique id, e.g. "mystash". Also the folder name under Mods/ that overrides the
        /// embedded files. Compared case-insensitively. Registering the same id again replaces the earlier app. Null
        /// or empty registers nothing and returns a handle whose calls do nothing.</param>
        /// <param name="bundlePrefix">Embedded-resource prefix of your web files inside the calling assembly, e.g.
        /// "MyMod.Assets.mystash" for <c>MyMod.Assets.mystash.index.html</c>. It is the <c>LogicalName</c> prefix in
        /// your csproj. Null means no prefix.</param>
        /// <param name="title">App title, e.g. "Stash". Null or blank uses <paramref name="id"/>.</param>
        /// <param name="iconLabel">Caption under the home-screen icon. Null or blank uses the title.</param>
        /// <param name="hostAssembly">The assembly holding the embedded bundle. Defaults to the caller's assembly;
        /// pass it explicitly if you wrap this call in a helper of your own.</param>
        /// <returns>The handle for the app's calls, events, badge and notifications. Every method on it chains.</returns>
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

        internal static bool RaisePhone(bool raised) => _setPhoneRaised != null && _setPhoneRaised(raised);

        internal static bool PhoneIsRaised() => _isPhoneRaised != null && _isPhoneRaised();

        internal static void ClaimKeys(string appId, string keys, Func<string, string, bool> handler) =>
            _claimKeys?.Invoke(appId, keys, handler);

        internal static bool MountSurface(object rect, string id, string prefix, Assembly host, float shortSide) =>
            _mountSurface != null && _mountSurface(rect, id, prefix, host, shortSide);

        internal static void UnmountSurface(string id) => _unmountSurface?.Invoke(id);

        internal static bool SurfaceIsMounted(string id) => _isSurfaceMounted != null && _isSurfaceMounted(id);

        /// <summary>Whether the installed host can render outside the phone. False before Sideload 1.13.0, where
        /// <see cref="Surfaces.Mount"/> answers false and the caller keeps whatever UI it already had.</summary>
        internal static bool HasSurfaces { get { EnsureBound(); return _mountSurface != null; } }

        /// <summary>Whether the installed host can hand an app a key at all. False against a Sideload older than
        /// 1.10.0, where <see cref="AppHandle.OnKey"/> is a silent no-op.</summary>
        internal static bool HasKeys { get { EnsureBound(); return _claimKeys != null; } }

        /// <summary>Whether the installed host can take the phone out of the player's pocket. False against a Sideload
        /// older than 1.5.0, where an app reached by a key rather than an icon has no way to make itself visible.</summary>
        internal static bool HasPhone { get { EnsureBound(); return _setPhoneRaised != null; } }

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
                _notifyFor = Get<Action<string, string, string, float>>(t, "NotifyFor");
                _isOnScreen = Get<Func<string, bool>>(t, "IsAppOnScreen");
                _setImage = Get<Action<string, string, byte[]>>(t, "SetImage");
                _setIconHidden = Get<Action<string, bool>>(t, "SetIconHidden");
                _setAppOpen = Get<Action<string, bool>>(t, "SetAppOpen");
                _isAppOpen = Get<Func<string, bool>>(t, "IsAppOpen");
                _setPhoneRaised = Get<Func<bool, bool>>(t, "SetPhoneRaised");
                _isPhoneRaised = Get<Func<bool>>(t, "IsPhoneRaised");
                _claimKeys = Get<Action<string, string, Func<string, string, bool>>>(t, "ClaimKeys");
                _mountSurface = Get<Func<object, string, string, Assembly, float, bool>>(t, "MountSurface");
                _unmountSurface = Get<Action<string>>(t, "UnmountSurface");
                _isSurfaceMounted = Get<Func<string, bool>>(t, "IsSurfaceMounted");

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
    /// <remarks>
    /// Strings cross the boundary in both directions. Send JSON for anything structured. Every call is queued until
    /// Sideload binds, except <see cref="Icon"/> and <see cref="Show"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// AppHandle app = Apps.Register("mystash", "MyMod.Assets.mystash", "Stash")
    ///     .Orientation("landscape", "portrait")
    ///     .OnCall("items.list", _ =&gt; string.Join("\n", Items));
    ///
    /// // Later, when the game changes something the page did not ask for:
    /// app.Emit("items.changed", Items.Count.ToString());
    /// app.Badge(Items.Count);
    /// if (!app.IsOnScreen) app.Notify("Stash", "New item");
    /// </code>
    /// </example>
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
        /// <param name="name">The name the page passes to <c>s1.call</c>, e.g. "chat.send". Compared
        /// case-insensitively. Null or blank registers nothing. The same name again replaces the handler.</param>
        /// <param name="handler">Gets the page's argument, or an empty string when it passed none. Its return
        /// value is what <c>s1.call</c> returns; null returns an empty string. An exception is logged and the page
        /// gets an empty string.</param>
        public AppHandle OnCall(string name, Func<string, string> handler)
        {
            string id = _id;
            Apps.WhenBound(() => Apps.HandleCall(id, name, handler));
            return this;
        }

        /// <summary>Push an event at this app's page, where <c>s1.on("&lt;name&gt;", fn)</c> is waiting for it. Use it when
        /// the game changes something the page did not ask for - a message arriving, a timer expiring.</summary>
        /// <param name="name">The name the page listens on with <c>s1.on</c>, e.g. "chat.message". Compared
        /// case-insensitively. Null or blank sends nothing.</param>
        /// <param name="payload">The string the page's listener receives, e.g. JSON. Null sends an empty string.</param>
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
        /// <param name="host">A bare host name such as "api.example.com", or <c>*.example.com</c> for any single label
        /// under it. A full URL is accepted and only its host is kept. A port is ignored.</param>
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
        /// <param name="supported">"landscape", "portrait" or both, in preference order. Case-insensitive. Any other
        /// value is logged as a warning and ignored. Nothing recognisable keeps the previous setting, landscape only by
        /// default.</param>
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
        /// <param name="count">The unread count. 0 clears the badge. A negative value counts as 0.</param>
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
        /// <param name="title">The headline, e.g. a sender name like "Jessi Waters". Null counts as empty.</param>
        /// <param name="subtitle">The line under it, e.g. "on my way". Empty by default.</param>
        /// <param name="seconds">
        /// How long it stays up. Leave it at zero for Sideload's own timing (9 seconds), which suits a headline plus a
        /// sentence. Raise it for something the player has to act on, lower it for a passing remark. Clamped to
        /// between 2 and 30 seconds - the slide-in cannot be dismissed, so an app does not get to hold the corner
        /// of the screen. Zero or below means Sideload's own timing. Ignored, without failing, on a Sideload too old
        /// to have it.
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
        /// <param name="name">The name after <c>s1://</c> in the page, e.g. "avatar/76561198". Trimmed. Null or blank
        /// does nothing.</param>
        /// <param name="png">PNG file bytes. Null or empty removes the picture.</param>
        public AppHandle Image(string name, byte[] png)
        {
            string id = _id;
            Apps.WhenBound(() => Apps.SetImage(id, name, png));
            return this;
        }

        /// <summary>
        /// Show or hide this app's home-screen icon while the game is running.
        ///
        /// For an app whose way in is a key rather than a square, but only sometimes: hash puts an icon there exactly
        /// while the game's console is switched on, because that is the only time it can run anything, and that
        /// setting is a live toggle. Safe to call with the same value repeatedly.
        ///
        /// Unlike <see cref="NoIcon"/> this is NOT queued until Sideload binds. A caller polling a condition would
        /// otherwise pile up one queued call per check against a host that never arrives; an unbound host simply
        /// ignores it, which is the right answer for a decision that is re-stated anyway.
        /// </summary>
        /// <param name="visible">True shows the icon, false hides it.</param>
        public AppHandle Icon(bool visible)
        {
            Apps.HideIcon(_id, !visible);
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

        /// <summary>
        /// Take the phone out AND open this app - what a key that opens an app has to mean, because
        /// <see cref="Open"/> on its own opens it on a phone that is still in the player's pocket.
        ///
        /// The order matters and is why this exists rather than two calls at the call site: the page is built the
        /// first time it is opened, and a page built while its panel is hidden measures every line about ten times
        /// too short. Raising first means the very first frame is laid out against the real viewport.
        ///
        /// Returns false when the game refused the phone - paused, asleep, dead, arrested - in which case nothing was
        /// opened either.
        /// <code>
        ///   if (consoleKeyPressed) app.Show();
        /// </code>
        /// </summary>
        public bool Show()
        {
            if (!Apps.RaisePhone(true)) return false;

            Apps.OpenApp(_id, true);
            return true;
        }

        /// <summary>
        /// Ask for a key that reaches this app with the phone still in the player's pocket - the way IN, as opposed to
        /// <c>data-keys</c>, which only ever reaches a focused field in an already-open page.
        ///
        /// Spell keys the way the DOM does, several separated by spaces or commas: <c>Enter</c>, <c>F8</c>,
        /// <c>Ctrl+Shift+K</c>. Modifiers match exactly, so <c>Enter</c> does not fire for Shift+Enter. <c>Escape</c>
        /// is refused - it is the game's own exit action.
        ///
        /// <para><b>Your handler returns whether it TOOK the press.</b> Return false and the key goes to the next app
        /// that wants it, which is how you decline a key you cannot use right now - a chat with no lobby behind it
        /// should not open, and should not swallow the key on its way past. The argument is the key that fired, so one
        /// handler can serve several.</para>
        ///
        /// <para><b>When two apps want the same key, the one that notified most recently gets it.</b> Two messengers
        /// installed together then behave the way a phone should: the key answers the conversation that is actually
        /// waiting. An app that has never notified still wins a key nobody else claimed - it simply sorts last.</para>
        ///
        /// <para>Sideload only reads the key where the game would let the player take their phone out anyway: never
        /// while they are typing, paused, asleep, arrested, or standing at a station, a shop or the developer console.
        /// While one of your apps is on screen it owns every key it claimed, and no other app is offered them.</para>
        ///
        /// <code>
        ///   app.OnKey("Enter", _ =&gt; { if (!Online) return false; return app.Show(); });
        /// </code>
        /// </summary>
        /// <param name="keys">One or more key declarations, separated by whitespace or commas, e.g. "Enter F8".
        /// A declaration is optional modifiers <c>Ctrl</c> (or <c>Control</c>), <c>Shift</c> and <c>Alt</c>, joined by
        /// <c>+</c> to one key name. Key names: the letters a to z, the digits 0 to 9, <c>F1</c> to <c>F12</c>,
        /// <c>Enter</c> (or <c>Return</c>), <c>Tab</c>, <c>Backspace</c>, <c>Delete</c>, <c>Insert</c>, <c>Home</c>,
        /// <c>End</c>, <c>PageUp</c>, <c>PageDown</c>, <c>ArrowUp</c>, <c>ArrowDown</c>, <c>ArrowLeft</c>,
        /// <c>ArrowRight</c> and <c>Space</c>. Case-insensitive. Punctuation keys are not accepted. A declaration that
        /// does not parse is logged as a warning and dropped; the rest still count.</param>
        /// <param name="handler">Runs on the Unity main thread. Gets the key that fired in its canonical spelling, e.g.
        /// "Enter", "Ctrl+Shift+k" or "F8"; letters come back lower-case. Returns whether it took the press. Null gives
        /// back every key this app holds.</param>
        public AppHandle OnKey(string keys, Func<string, bool> handler)
        {
            string id = _id;
            Apps.WhenBound(() => Apps.ClaimKeys(id, keys, handler == null ? null : (_, key) => handler(key)));
            return this;
        }

        /// <summary>
        /// Whether the installed Sideload can hand an app a key. False against anything older than 1.10.0, where
        /// <see cref="OnKey"/> is a silent no-op.
        ///
        /// Worth checking only when the key is the ONLY way into your app - pair it with <see cref="NoIcon"/> and a
        /// host that ignores both leaves the player with an app they can neither see nor reach.
        /// </summary>
        public static bool CanClaimKeys { get { return Apps.Available && Apps.HasKeys; } }

        /// <summary>Close this app and put the phone away. The mirror of <see cref="Show"/>.</summary>
        public AppHandle Hide()
        {
            string id = _id;
            Apps.WhenBound(() =>
            {
                Apps.OpenApp(id, false);
                Apps.RaisePhone(false);
            });
            return this;
        }
    }

    /// <summary>
    /// HTML somewhere other than the phone: a column in the main menu, a panel on a machine, a board on a wall.
    ///
    /// The renderer never cared about the phone - it draws into any RectTransform - so a surface is the same engine,
    /// the same CSS subset and the same <c>s1.call</c> / <c>s1.on</c> channel, only mounted somewhere else. What it
    /// does not have is everything that belongs to the phone: no home-screen icon, no orientation the player can
    /// turn, no badge, no notification.
    ///
    /// <code>
    ///   Surfaces.Mount(myPanelRectTransform, "sidehustle-menu", "SideHustle.Assets.menu")
    ///           .OnCall("menu.state", _ =&gt; StateJson());
    /// </code>
    ///
    /// Needs Sideload 1.13.0. Against anything older <see cref="Mount"/> answers a handle whose calls are no-ops and
    /// <see cref="Available"/> is false, so a mod ships this without a hard version pin and keeps its own UI as the
    /// fallback.
    /// </summary>
    /// <example>
    /// A panel survives no scene reload, so mount again whenever it is gone:
    /// <code>
    /// if (Surfaces.Available &amp;&amp; !Surfaces.IsMounted("sidehustle-menu"))
    ///     Surfaces.Mount(myPanelRectTransform, "sidehustle-menu", "SideHustle.Assets.menu");
    /// </code>
    /// </example>
    public static class Surfaces
    {
        /// <summary>Whether the installed Sideload can render outside the phone at all.</summary>
        public static bool Available { get { return Apps.Available && Apps.HasSurfaces; } }

        /// <summary>
        /// Render a bundle into a panel of your own.
        /// </summary>
        /// <param name="hostRect">Your <c>UnityEngine.RectTransform</c>. Typed as object only so this file stays
        /// compilable in a mod with no Unity reference; pass the RectTransform straight in.</param>
        /// <param name="id">Stable id, unique across apps AND surfaces - they share one namespace, so a surface
        /// cannot take an app's <c>s1.call</c> handlers. Also the folder under Mods/ that overrides the bundle. E.g.
        /// "sidehustle-menu". Compared case-insensitively and trimmed. Mounting the same id again replaces the old
        /// surface. Null or empty mounts nothing.</param>
        /// <param name="bundlePrefix">Embedded-resource prefix of the web files inside the calling assembly, e.g.
        /// "SideHustle.Assets.menu". Null means no prefix.</param>
        /// <param name="designShortSide">What the panel's short side is worth in CSS pixels. 0 (the default) maps
        /// one CSS pixel to one device unit, which is what a panel uGUI has already laid out wants. Give a number
        /// instead to get the phone's contract: the page is written for that width and scales with the panel. The
        /// phone uses 400.</param>
        /// <param name="hostAssembly">The assembly holding the bundle. Defaults to the caller's.</param>
        /// <returns>The handle for the surface's calls and events. It is returned even when nothing was mounted; check
        /// <see cref="IsMounted"/> to know.</returns>
        public static SurfaceHandle Mount(object hostRect, string id, string bundlePrefix,
                                          float designShortSide = 0f, Assembly hostAssembly = null)
        {
            var handle = new SurfaceHandle(id);
            if (string.IsNullOrEmpty(id) || hostRect == null) return handle;

            Assembly asm = hostAssembly ?? Assembly.GetCallingAssembly();
            // Not queued when the host is absent, unlike Register: a rect is a live object, and replaying a mount
            // later would aim at a panel that has since been destroyed.
            Apps.MountSurface(hostRect, id, bundlePrefix, asm, designShortSide);
            return handle;
        }

        /// <summary>Take a surface down. Safe for an id that was never mounted.</summary>
        /// <param name="id">The id passed to <see cref="Mount"/>. Compared case-insensitively.</param>
        public static void Unmount(string id) { Apps.UnmountSurface(id); }

        /// <summary>Whether a surface under this id is on screen. False once its panel is destroyed - a scene reload
        /// does that - so this is the check to remount on.</summary>
        /// <param name="id">The id passed to <see cref="Mount"/>. Compared case-insensitively.</param>
        public static bool IsMounted(string id) { return Apps.SurfaceIsMounted(id); }
    }

    /// <summary>Handle to a mounted surface: the same call and event channel an app has, minus everything that only
    /// makes sense on a phone.</summary>
    public sealed class SurfaceHandle
    {
        private readonly string _id;

        internal SurfaceHandle(string id) { _id = id; }

        /// <summary>The id this surface was mounted under.</summary>
        public string Id { get { return _id; } }

        /// <summary>Answer <c>s1.call(name, arg)</c> from this surface's page. Same rules as an app's handler: it
        /// runs on the Unity main thread in the same frame, and returns a string.</summary>
        /// <param name="name">The name the page passes to <c>s1.call</c>, e.g. "chat.send". Compared
        /// case-insensitively. Null or blank registers nothing. The same name again replaces the handler.</param>
        /// <param name="handler">Gets the page's argument, or an empty string when it passed none. Its return
        /// value is what <c>s1.call</c> returns; null returns an empty string. An exception is logged and the page
        /// gets an empty string.</param>
        public SurfaceHandle OnCall(string name, Func<string, string> handler)
        {
            string id = _id;
            Apps.WhenBound(() => Apps.HandleCall(id, name, handler));
            return this;
        }

        /// <summary>Push an event at this surface's page - <c>s1.on(name, fn)</c>. Nothing happens when the surface
        /// is not mounted; the page picks the state up when it next builds.</summary>
        /// <param name="name">The name the page listens on with <c>s1.on</c>, e.g. "chat.message". Compared
        /// case-insensitively. Null or blank sends nothing.</param>
        /// <param name="payload">The string the page's listener receives, e.g. JSON. Null sends an empty string.</param>
        public SurfaceHandle Emit(string name, string payload = "")
        {
            string id = _id;
            Apps.WhenBound(() => Apps.EmitEvent(id, name, payload));
            return this;
        }

        /// <summary>Let this surface's page reach one host with <c>fetch</c>. The allowlist starts empty.</summary>
        /// <param name="host">A bare host name such as "api.example.com", or <c>*.example.com</c> for any single label
        /// under it. A full URL is accepted and only its host is kept. A port is ignored.</param>
        public SurfaceHandle AllowHost(string host)
        {
            string id = _id;
            Apps.WhenBound(() => Apps.AllowNetHost(id, host));
            return this;
        }

        /// <summary>
        /// Publish a picture this surface's page can draw with <c>&lt;img src="s1://&lt;name&gt;"&gt;</c>, exactly as
        /// an app does. Null or empty bytes remove it.
        ///
        /// It was missing, and the store never cared: pictures are held by (id, name) and nothing asks whether
        /// the id belongs to an app or a surface. So a surface could always have them - it just had no supported
        /// way to say so, and the one mod that needed it reached past this file into the internals because it
        /// compiles the shim in as source. A mod that references the DLL could not have done that at all.
        ///
        /// PNG bytes rather than a texture, for the reason the whole file gives: nothing here references a Unity
        /// type and nothing here is going to start.
        /// <code>
        ///   surface.Image("icon/" + itemId, pngBytes);
        /// </code>
        /// </summary>
        /// <param name="name">The name after <c>s1://</c> in the page, e.g. "avatar/76561198". Trimmed. Null or blank
        /// does nothing.</param>
        /// <param name="png">PNG file bytes. Null or empty removes the picture.</param>
        public SurfaceHandle Image(string name, byte[] png)
        {
            string id = _id;
            Apps.WhenBound(() => Apps.SetImage(id, name, png));
            return this;
        }

        /// <summary>Take this surface down.</summary>
        public void Unmount() { Apps.UnmountSurface(_id); }
    }

    /// <summary>The phone itself, as opposed to any one app on it. Everything here is a no-op without Sideload.</summary>
    public static class PhoneScreen
    {
        /// <summary>Whether the phone is out and showing its phone screen - not the character tab, and not in the
        /// player's pocket.</summary>
        public static bool IsRaised { get { return Apps.Available && Apps.PhoneIsRaised(); } }

        /// <summary>
        /// Take the phone out. Returns false when the game refused: paused, asleep, dead or arrested. Safe to call
        /// when the phone is already out.
        /// </summary>
        public static bool Raise() { return Apps.RaisePhone(true); }

        /// <summary>Put the phone away, wherever it sits in the game's UI stack.</summary>
        public static bool Lower() { return Apps.RaisePhone(false); }

        /// <summary>
        /// Whether the installed Sideload can move the phone at all. False before 1.5.0, where <see cref="Raise"/>
        /// and <see cref="AppHandle.Show"/> are silent no-ops - which for an app with no icon means no way in.
        /// </summary>
        public static bool Available { get { return Apps.Available && Apps.HasPhone; } }
    }
}
