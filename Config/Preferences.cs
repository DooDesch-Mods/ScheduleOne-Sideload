using MelonLoader;

namespace Sideload.Config
{
    /// <summary>
    /// MelonPreferences wrapper. The category id is prefixed with the mod name ("Sideload_...") so it is
    /// auto-detected by the "Mod Manager &amp; Phone App" settings UI.
    ///
    /// Everything here is a developer tool and everything is off by default. The devtools server accepts a socket
    /// that can evaluate arbitrary JavaScript inside a mounted page, so a player who never turns it on has no port
    /// open and no code path into the engine: <see cref="DevTools"/> is the single gate, checked before the server is
    /// ever constructed.
    /// </summary>
    internal static class Preferences
    {
        private const string CategoryId = "Sideload_01_Main";

        private static MelonPreferences_Category _category;
        private static MelonPreferences_Entry<bool> _devTools;
        private static MelonPreferences_Entry<int> _devToolsPort;
        private static MelonPreferences_Entry<bool> _devToolsAutoOpen;
        private static MelonPreferences_Entry<string> _devToolsFrontend;
        private static MelonPreferences_Entry<bool> _devToolsFetchFrontend;
        private static MelonPreferences_Entry<bool> _autoOpenApp;
        private static MelonPreferences_Entry<bool> _appKeys;

        internal static void Initialize()
        {
            if (_category != null) return;

            _category = MelonPreferences.CreateCategory(CategoryId, "Sideload (Web UI Framework)");

            _devTools = _category.CreateEntry("DevTools", false, "Chrome DevTools",
                "OFF (default): nothing listens, and no page can be inspected from outside the game. ON: Sideload " +
                "runs a Chrome DevTools Protocol server on 127.0.0.1 so you can attach the real DevTools UI to a " +
                "mounted page - console, evaluate, Elements tree. Anything that can reach the port can run code in " +
                "your pages, so leave this off unless you are building an app.");

            _devToolsPort = _category.CreateEntry("DevToolsPort", 9333, "DevTools port",
                "The loopback port the devtools server listens on. Change it only if 9333 clashes with another " +
                "tool. Clamped 1024-65535.", false, false,
                new MelonLoader.Preferences.ValueRange<int>(1024, 65535));

            _devToolsAutoOpen = _category.CreateEntry("DevToolsAutoOpen", true, "Open the browser automatically",
                "ON (default): once the first page is mounted, Chrome (or Edge) opens at the devtools landing page, " +
                "where one click attaches the inspector. OFF: the address is only written to the log. Has no effect " +
                "while Chrome DevTools is off.");

            _devToolsFrontend = _category.CreateEntry("DevToolsFrontend", "", "Local DevTools frontend folder",
                "EMPTY (default): Sideload uses its own copy under UserData/Sideload/devtools-frontend, downloading " +
                "it once if DevToolsFetchFrontend allows, and falls back to Google's servers otherwise. Set this to " +
                "a folder holding your own copy of the frontend (for example " +
                "node_modules/@react-native/debugger-frontend/dist/third-party/front_end) to override all of that " +
                "and serve yours instead. Nothing about your page leaves the machine in any case - the frontend is " +
                "static JavaScript talking to 127.0.0.1.");

            _devToolsFetchFrontend = _category.CreateEntry("DevToolsFetchFrontend", true,
                "Download the DevTools interface once",
                "ON (default): the first time you switch Chrome DevTools on, Sideload downloads the DevTools " +
                "interface in the background - the npm package @react-native/debugger-frontend, about 4.5 MB over " +
                "the wire and 16 MB on disk - into UserData/Sideload/devtools-frontend. It happens once per machine, " +
                "never while Chrome DevTools is off, and never blocks the game: DevTools works from Google's servers " +
                "until the copy lands and offline afterwards. OFF: nothing is downloaded and the interface comes " +
                "from Google's servers every time, which needs internet. " +
                "Workspace/tools/install-devtools-frontend.ps1 does the same thing by hand.");

            _autoOpenApp = _category.CreateEntry("AutoOpenAppInDebug", true, "Open an app by itself (debug builds)",
                "ON (default): a debug build raises the phone a few seconds after the world loads and opens the " +
                "first registered app, so a milestone can be signed off from a screenshot without anyone pressing " +
                "keys. OFF: the phone is left alone. Turn this off when you are photographing the game's OWN " +
                "screens - an app opened this way sits on top of them and cannot be dismissed from the home " +
                "screen. No effect in a release build, where this never runs at all.");

            _appKeys = _category.CreateEntry("AppKeys", true, "Let apps answer a key",
                "ON (default): an app may ask for a key that reaches it with the phone in your pocket - press it and " +
                "the app comes up ready to use. Only a key the app asked for is read, only while the game would let " +
                "you take your phone out anyway, and never while you are typing, paused, or in a station, shop or " +
                "the console. OFF: no app gets a key and you open everything from the home screen.");
        }

        /// <summary>The gate for the whole devtools feature. False on a fresh install and in every shipped build.</summary>
        internal static bool DevTools => _devTools?.Value ?? false;

        /// <summary>Whether a debug build may open an app on its own. Irrelevant in release, where it cannot.</summary>
        internal static bool AutoOpenAppInDebug => _autoOpenApp?.Value ?? true;

        /// <summary>The player's one switch over every key an app claimed. On by default: an app only gets the keys it
        /// asked for, and only where the game's own phone key would work.</summary>
        internal static bool AppKeys => _appKeys?.Value ?? true;

        internal static int DevToolsPort => Math.Clamp(_devToolsPort?.Value ?? 9333, 1024, 65535);

        internal static bool DevToolsAutoOpen => _devToolsAutoOpen?.Value ?? true;

        /// <summary>Folder holding the developer's own copy of the DevTools frontend, or empty to let
        /// <see cref="Devtools.Cdp.FrontendCache"/> decide. Set, it overrides every other source.</summary>
        internal static string DevToolsFrontend => (_devToolsFrontend?.Value ?? "").Trim();

        /// <summary>Whether Sideload may fetch the DevTools frontend itself so the feature works offline. Only ever
        /// acted on when <see cref="DevTools"/> is already on.</summary>
        internal static bool DevToolsFetchFrontend => _devToolsFetchFrontend?.Value ?? true;
    }
}
