using System.Diagnostics;
using MelonLoader.Utils;

namespace Sideload.Devtools.Cdp
{
    /// <summary>
    /// Opens DevTools as its own application window, the way React Native's Metro does: a Chromium browser in
    /// app mode, on a profile of its own, pointed straight at the inspector.
    ///
    /// The two flags are what make it feel like a tool rather than a tab:
    ///   --app=&lt;url&gt;        no tab strip, no omnibox, no bookmarks - just DevTools in a window
    ///   --user-data-dir    a separate profile, so it is a SEPARATE browser process: it does not join the
    ///                      developer's existing session, cannot disturb their tabs, and closing it closes only this
    ///
    /// `--app=` accepts http and https. It does NOT accept `devtools://` - Chrome silently discards that on the
    /// command line and opens a new tab instead, which is why the inspector is reached through a frontend URL.
    /// </summary>
    internal static class ChromeLauncher
    {
        /// <summary>Where a Chromium browser lives on Windows, in the order worth trying.</summary>
        private static readonly string[] Candidates =
        {
            @"%ProgramFiles%\Google\Chrome\Application\chrome.exe",
            @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe",
            @"%LocalAppData%\Google\Chrome\Application\chrome.exe",
            @"%ProgramFiles%\Google\Chrome Beta\Application\chrome.exe",
            @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe",
            @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe",
        };

        private static Process _window;

        /// <summary>
        /// Open DevTools on a target. With no target yet, the landing page opens instead so the developer can pick
        /// one as soon as a page mounts.
        /// </summary>
        internal static void Open(int port, string targetId)
        {
            string url = targetId == null
                ? $"http://127.0.0.1:{port}/"
                : Targets.FrontendUrl(port, targetId);

            string browser = Find();
            if (browser == null)
            {
                Core.Log?.Warning($"[Sideload/cdp] no Chrome or Edge found - open http://127.0.0.1:{port}/ yourself.");
                return;
            }

            // A window from a previous mount is still the right window; a second one would just be in the way.
            if (_window != null && !_window.HasExited)
            {
                Core.Log?.Msg("[Sideload/cdp] devtools window is already open.");
                return;
            }

            try
            {
                _window = Process.Start(new ProcessStartInfo
                {
                    FileName = browser,
                    Arguments = $"--app=\"{url}\" --user-data-dir=\"{ProfileDirectory()}\" " +
                                "--no-first-run --no-default-browser-check --disable-features=Translate " +
                                "--window-size=1500,950",
                    UseShellExecute = false,
                });

                Core.Log?.Msg($"[Sideload/cdp] devtools opened in {Path.GetFileNameWithoutExtension(browser)} " +
                              $"(own window, own profile) on '{targetId ?? "the target list"}'.");
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"[Sideload/cdp] launching the browser failed ({e.Message}) - " +
                                  $"open http://127.0.0.1:{port}/ yourself.");
            }
        }

        /// <summary>Close the window we opened. A window the developer opened themselves is left alone.</summary>
        internal static void Close()
        {
            try
            {
                if (_window != null && !_window.HasExited) _window.Kill(entireProcessTree: true);
            }
            catch { /* it closed on its own, which is the same outcome */ }
            finally { _window = null; }
        }

        /// <summary>
        /// A profile of Sideload's own, kept out of the developer's Chrome. Persistent rather than temporary on
        /// purpose: DevTools stores its panel layout, dock side and settings there, and a fresh profile every launch
        /// would reset all of it and re-show the first-run prompts.
        /// </summary>
        private static string ProfileDirectory()
        {
            string path = Path.Combine(MelonEnvironment.UserDataDirectory, "Sideload", "devtools-profile");
            Directory.CreateDirectory(path);
            return path;
        }

        private static string Find()
        {
            foreach (string candidate in Candidates)
            {
                string path = Environment.ExpandEnvironmentVariables(candidate);

                // An unexpanded variable means the folder does not exist on this machine, and File.Exists on a path
                // with a '%' in it is a needless exception.
                if (path.Contains('%')) continue;
                if (File.Exists(path)) return path;
            }

            return null;
        }
    }
}
