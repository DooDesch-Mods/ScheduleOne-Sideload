#if DEBUG
using System.Globalization;
using UnityEngine;

namespace Sideload.Devtools
{
    /// <summary>
    /// DEBUG-only: <c>sideloadwheel [notches]</c> - turn the wheel over the newest page and say what took it.
    ///
    /// A page that scrolls and a page that is merely cropped are the same picture in one frame, so this is the one
    /// interaction that cannot be signed off from a screenshot. Automation can submit a console command; it cannot
    /// turn a wheel. The probe behind this runs the real raycast and the real <c>IScrollHandler</c> dispatch, so
    /// what it reports is what the player's wheel does - and when the answer is "nobody", it names the object that
    /// swallowed the notch instead of leaving a guess.
    ///
    /// Positive notches scroll toward the top, which is the sign a mouse reports. Default is -3, one firm push
    /// downwards, because "does the page move at all" is the question this is usually asked.
    /// </summary>
    internal static class WheelConsole
    {
        private static int _lastFrame = -1;
        private static string _lastSignature = "";

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
            if (!parts[0].Equals("sideloadwheel", StringComparison.OrdinalIgnoreCase)) return false;

            // Both SubmitCommand overloads are patched and the string one calls the list one, so a single
            // submission arrives twice. Same guard as KeyConsole, for the same reason.
            string signature = string.Join(" ", parts);
            if (Time.frameCount == _lastFrame && signature == _lastSignature) return true;
            _lastFrame = Time.frameCount;
            _lastSignature = signature;

            try
            {
                float notches = -3f;
                if (parts.Length > 1 && !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out notches))
                {
                    Core.Log?.Msg("usage: sideloadwheel [notches] - negative scrolls down, for example 'sideloadwheel -3'.");
                    return true;
                }

                // An app id, because several pages are mounted at once - the phone's app plus whatever else is
                // registered - and "the newest" is whichever mod happened to register last, not the one on screen.
                string appId = parts.Length > 2 ? parts[2] : null;
                Host.WebView page = appId != null ? Host.WebView.Find(appId) : Host.WebView.Newest;

                if (page == null) Core.Log?.Msg($"sideloadwheel: no page mounted{(appId != null ? " for " + appId : "")}.");
                else page.DebugWheel(notches);
            }
            catch (Exception e) { Core.Log?.Warning("sideloadwheel failed: " + e.Message); }

            return true;
        }
    }
}
#endif
