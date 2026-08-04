#if DEBUG
using Sideload.Host;

namespace Sideload.Devtools
{
    /// <summary>
    /// The surface the ScheduleMCP bridge mod drives Sideload through, so an agent can inspect and steer a mounted page
    /// instead of reading the log by hand: what is registered, what the dev overlay would show, click an element,
    /// evaluate a snippet, reload from disk, flip the outlines.
    ///
    /// Shaped exactly like <see cref="Bridge.SideloadBridge"/> and for the same reason: public static delegate fields
    /// whose signatures use only BCL types. The two mods therefore share no type, neither references the other, and
    /// the bridge binds by reflection or degrades to "probe unavailable".
    ///
    /// Every call must happen on the Unity main thread. The bridge's dispatcher already guarantees that - it pumps
    /// queued commands from OnUpdate - so nothing here does any thread marshalling of its own.
    ///
    /// Debug builds only, because the devtools it drives (DebugClick / DebugEval / DebugReload) are themselves
    /// Debug-only. A Release Sideload.dll simply has no McpProbe type, which is the signal the bridge reports.
    /// </summary>
    public static class McpProbe
    {
        /// <summary>Bumped only on a breaking change to an existing delegate signature.</summary>
        public static readonly int AbiVersion = 1;

        /// <summary>-&gt; { ok, apps: [ { id, title, iconLabel, bundlePrefix, hostAssembly, overrideRoot,
        /// overrideExists, mounted, files } ] }. Reads the registry, so it lists apps even before the phone exists.</summary>
        public static readonly Func<Dictionary<string, object>> ListApps = ListAppsImpl;

        /// <summary>-&gt; { ok, views: [ WebView.DebugStats() ] }. One entry per MOUNTED page; a registered app whose
        /// panel has never been opened has no view and does not appear.</summary>
        public static readonly Func<Dictionary<string, object>> Stats = StatsImpl;

        /// <summary>appId (empty = newest view), CSS selector. Clicks through the real uGUI raycast; the outcome is
        /// written to the log as a [Sideload/probe] line.</summary>
        public static readonly Func<string, string, Dictionary<string, object>> Click = ClickImpl;

        /// <summary>appId (empty = newest view), JavaScript source. Runs in the page's own Jint engine.</summary>
        public static readonly Func<string, string, Dictionary<string, object>> Eval = EvalImpl;

        /// <summary>appId (empty = every view). Rebuilds the page from disk exactly as a file change would.</summary>
        public static readonly Func<string, Dictionary<string, object>> Reload = ReloadImpl;

        /// <summary>-1 toggle, 0 off, 1 on. The F10 equivalent, including the reload that makes it take effect.</summary>
        public static readonly Func<int, Dictionary<string, object>> Outlines = OutlinesImpl;

        /// <summary>appId, bundle-relative path. Resolves the file the way the renderer does (override folder first,
        /// embedded resource second), which is the only way to reach the shipped copy from outside the game.</summary>
        public static readonly Func<string, string, Dictionary<string, object>> ReadFile = ReadFileImpl;

        /// <summary>appId, open. Takes the phone out and shows that app, or closes it and puts the phone away -
        /// exactly what a player does with their hands, which is the one thing a harness cannot do.</summary>
        public static readonly Func<string, bool, Dictionary<string, object>> OpenApp = OpenAppImpl;

        /// <summary>The three files every app bundle is built from; reported per app so a caller can see at a glance
        /// which of them a Mods/&lt;id&gt;/ folder currently overrides.</summary>
        private static readonly string[] CanonicalFiles = { "index.html", "app.css", "app.js" };

        private static Dictionary<string, object> ListAppsImpl()
        {
            try
            {
                var apps = new List<object>();
                foreach (AppRegistration reg in Registry.Apps)
                {
                    var files = new Dictionary<string, object>();
                    foreach (string name in CanonicalFiles) files[name] = SourceOf(reg, name);

                    apps.Add(new Dictionary<string, object>
                    {
                        ["id"] = reg.Id,
                        ["title"] = reg.Title,
                        ["iconLabel"] = reg.IconLabel,
                        ["bundlePrefix"] = reg.BundlePrefix,
                        ["hostAssembly"] = reg.HostAssembly?.GetName().Name ?? "",
                        ["overrideRoot"] = reg.Bundle?.OverrideRoot ?? "",
                        ["overrideExists"] = reg.Bundle != null && Directory.Exists(reg.Bundle.OverrideRoot),
                        ["mounted"] = Find(reg.Id) != null,
                        ["files"] = files,
                    });
                }

                return Ok(new Dictionary<string, object> { ["apps"] = apps, ["mountedViews"] = WebView.Live.Count });
            }
            catch (Exception e) { return Error("listing apps failed: " + e.Message); }
        }

        private static Dictionary<string, object> StatsImpl()
        {
            try
            {
                var views = new List<object>();
                foreach (WebView view in WebView.Live) views.Add(view.DebugStats());

                return Ok(new Dictionary<string, object>
                {
                    ["views"] = views,
                    ["outlines"] = LayoutOverlay.Outlines,
                    ["dumpTree"] = LayoutOverlay.DumpTree,
                });
            }
            catch (Exception e) { return Error("reading stats failed: " + e.Message); }
        }

        private static Dictionary<string, object> ClickImpl(string appId, string selector)
        {
            if (string.IsNullOrWhiteSpace(selector)) return Error("selector is empty");

            WebView view = Find(appId);
            if (view == null) return NoView(appId);

            try
            {
                view.DebugClick(selector);
                return Ok(new Dictionary<string, object>
                {
                    ["appId"] = view.AppId,
                    ["selector"] = selector,
                    ["dispatched"] = true,
                });
            }
            catch (Exception e) { return Error("click failed: " + e.Message); }
        }

        private static Dictionary<string, object> EvalImpl(string appId, string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return Error("code is empty");

            WebView view = Find(appId);
            if (view == null) return NoView(appId);

            try
            {
                string value = view.DebugEvaluate(code, out bool failed);

                // The value IS the answer for a one-line probe, so it comes back rather than only the page's health.
                // A broken snippet is reported as failed with its message; it never takes the page down.
                Dictionary<string, object> stats = view.DebugStats();
                return Ok(new Dictionary<string, object>
                {
                    ["appId"] = view.AppId,
                    ["evaluated"] = true,
                    ["value"] = value,
                    ["failed"] = failed,
                    ["scriptStatus"] = stats.TryGetValue("scriptStatus", out object s) ? s : "unknown",
                    ["scriptError"] = stats.TryGetValue("scriptError", out object e) ? e : "",
                });
            }
            catch (Exception ex) { return Error("eval failed: " + ex.Message); }
        }

        private static Dictionary<string, object> ReloadImpl(string appId)
        {
            try
            {
                var reloaded = new List<object>();
                foreach (WebView view in Targets(appId))
                {
                    view.DebugReload();
                    reloaded.Add(view.AppId);
                }

                if (reloaded.Count == 0) return NoView(appId);
                return Ok(new Dictionary<string, object> { ["reloaded"] = reloaded });
            }
            catch (Exception e) { return Error("reload failed: " + e.Message); }
        }

        /// <summary>
        /// Open an app the way the player would, phone and all.
        ///
        /// Everything else on this probe steers a page that is already on screen. Getting it there was the one step
        /// that still needed a hand on the keyboard, which meant an app reached by anything other than its icon - a
        /// hijacked key, a notification - could not be signed off without a human. Raising first is deliberate: the
        /// page is built on first open, and one built while its panel is hidden measures every line far too short.
        /// </summary>
        private static Dictionary<string, object> OpenAppImpl(string appId, bool open)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(appId)) return Error("open: no appId given.");

                if (!open)
                {
                    Registry.SetAppOpen(appId, false);
                    Registry.SetPhoneRaised(false);
                    return Ok(new Dictionary<string, object> { ["appId"] = appId, ["open"] = false });
                }

                if (!Registry.SetPhoneRaised(true))
                    return Error("open: the game refused to raise the phone - paused, asleep, dead or arrested.");

                Registry.SetAppOpen(appId, true);

                return Ok(new Dictionary<string, object>
                {
                    ["appId"] = appId,
                    ["open"] = Registry.IsAppOpen(appId),
                    ["onScreen"] = Registry.IsOnScreen(appId),
                    ["phoneRaised"] = Registry.IsPhoneRaised(),
                });
            }
            catch (Exception e) { return Error("open failed: " + e.Message); }
        }

        private static Dictionary<string, object> OutlinesImpl(int mode)
        {
            try
            {
                LayoutOverlay.Outlines = mode < 0 ? !LayoutOverlay.Outlines : mode > 0;

                // Outlines are drawn while painting, so the flag only becomes visible once the pages are rebuilt -
                // the same thing the F10 handler does.
                var reloaded = new List<object>();
                foreach (WebView view in Targets(null))
                {
                    view.DebugReload();
                    reloaded.Add(view.AppId);
                }

                Core.Log?.Msg($"[Sideload/dev] outlines {(LayoutOverlay.Outlines ? "on" : "off")}.");
                return Ok(new Dictionary<string, object>
                {
                    ["outlines"] = LayoutOverlay.Outlines,
                    ["reloaded"] = reloaded,
                });
            }
            catch (Exception e) { return Error("toggling outlines failed: " + e.Message); }
        }

        private static Dictionary<string, object> ReadFileImpl(string appId, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return Error("path is empty");

            AppRegistration reg = FindApp(appId);
            if (reg?.Bundle == null) return Error($"no app registered as '{appId}'");

            try
            {
                string text = reg.Bundle.ReadText(path);
                return Ok(new Dictionary<string, object>
                {
                    ["appId"] = reg.Id,
                    ["path"] = path,
                    ["found"] = text != null,
                    ["source"] = SourceOf(reg, path),
                    ["overridePath"] = reg.Bundle.OverridePathOf(path),
                    ["resourceName"] = reg.Bundle.ResourceNameOf(path),
                    ["text"] = text ?? "",
                });
            }
            catch (Exception e) { return Error("reading the bundle file failed: " + e.Message); }
        }

        /// <summary>Which of the two sources a bundle path resolves from, using the renderer's own precedence.</summary>
        private static string SourceOf(AppRegistration reg, string path)
        {
            if (reg?.Bundle == null) return "missing";
            if (File.Exists(reg.Bundle.OverridePathOf(path))) return "override";
            return reg.Bundle.Exists(path) ? "embedded" : "missing";
        }

        /// <summary>The views a command applies to: one named app, or all of them when no id is given.</summary>
        private static List<WebView> Targets(string appId)
        {
            var targets = new List<WebView>();
            if (string.IsNullOrWhiteSpace(appId))
            {
                // Copied out of the live list: a reload rebuilds a page and must not run against a list in motion.
                foreach (WebView view in WebView.Live) targets.Add(view);
                return targets;
            }

            WebView match = Find(appId);
            if (match != null) targets.Add(match);
            return targets;
        }

        /// <summary>The named app's view, or the most recently mounted one when no id is given.</summary>
        private static WebView Find(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId)) return WebView.Newest;

            foreach (WebView view in WebView.Live)
                if (string.Equals(view.AppId, appId, StringComparison.OrdinalIgnoreCase)) return view;

            return null;
        }

        private static AppRegistration FindApp(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId)) return Registry.Apps.Count > 0 ? Registry.Apps[0] : null;

            foreach (AppRegistration reg in Registry.Apps)
                if (string.Equals(reg.Id, appId, StringComparison.OrdinalIgnoreCase)) return reg;

            return null;
        }

        private static Dictionary<string, object> Ok(Dictionary<string, object> data)
        {
            data["ok"] = true;
            return data;
        }

        private static Dictionary<string, object> Error(string message)
            => new Dictionary<string, object> { ["ok"] = false, ["error"] = message };

        private static Dictionary<string, object> NoView(string appId)
            => Error(string.IsNullOrWhiteSpace(appId)
                ? "no page is mounted - open a Sideload app on the phone first"
                : $"'{appId}' is not mounted - the app has to be open on the phone for this");
    }
}
#endif
