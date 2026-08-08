namespace Sideload.Model
{
    /// <summary>
    /// The two things the script engine needs from the machine it is running on: somewhere to write a line, and
    /// somewhere to keep a file. Both are handed in by the mod at start-up rather than reached for directly.
    ///
    /// This exists so <c>Script/</c> can be compiled without MelonLoader. Everything else in the engine that is
    /// Unity-free is already covered by tests; the JavaScript surface was the one part that could only be exercised
    /// by starting the game, which is why its gaps went unnoticed for so long. <see cref="Net.HostAllowlist"/> takes
    /// the same route for the same reason.
    ///
    /// Unset means silence, not a crash: a test run wires nothing and prints nothing.
    /// </summary>
    internal static class Platform
    {
        internal static Action<string> LogMsg = null;

        internal static Action<string> LogWarning = null;

        internal static Action<string> LogError = null;

        /// <summary>Where an app's own storage file goes. Falls back to the process directory, which is where a
        /// headless run wants it and where a misconfigured game would at least still work.</summary>
        internal static Func<string> UserDataDirectory = () => AppContext.BaseDirectory;

        /// <summary>Whether the named app is currently held portrait, and the way to turn it. Behind the seam because
        /// the app registry carries a Unity sprite for each icon and so cannot be linked without the engine.</summary>
        internal static Func<string, bool> IsPortrait = _ => false;

        internal static Action<string, string> SetOrientation = (_, _) => { };

        internal static void Msg(string line) => LogMsg?.Invoke(line);

        internal static void Warning(string line) => LogWarning?.Invoke(line);

        internal static void Error(string line) => LogError?.Invoke(line);
    }
}
