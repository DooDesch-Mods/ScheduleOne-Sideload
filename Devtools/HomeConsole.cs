#if DEBUG
using UnityEngine;

namespace Sideload.Devtools
{
    /// <summary>
    /// DEBUG-only: <c>sideloadhome</c> - press the phone's home button while an app is open and say what closed.
    ///
    /// The button is a 3D collider, not a uGUI element, so automation has nothing to click. This runs the same
    /// raycast a mouse click runs, aimed at the button's centre.
    /// </summary>
    internal static class HomeConsole
    {
        private static int _lastFrame = -1;

        internal static bool TryHandle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return Dispatch(raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        internal static bool TryHandle(Il2CppSystem.Collections.Generic.List<string> args)
        {
            if (args == null || args.Count == 0) return false;

            var parts = new string[args.Count];
            for (int i = 0; i < args.Count; i++) parts[i] = args[i];
            return Dispatch(parts);
        }

        private static bool Dispatch(string[] parts)
        {
            if (parts.Length == 0) return false;
            if (!parts[0].Equals("sideloadhome", StringComparison.OrdinalIgnoreCase)) return false;

            // Both SubmitCommand overloads are patched and the string one calls the list one, so a single
            // submission arrives twice. Same guard as KeyConsole, for the same reason.
            if (Time.frameCount == _lastFrame) return true;
            _lastFrame = Time.frameCount;

            try { Phone.HomeButton.DebugPress(); }
            catch (Exception e) { Core.Log?.Warning("sideloadhome failed: " + e.Message); }

            return true;
        }
    }
}
#endif
