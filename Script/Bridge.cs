using Jint;
using Jint.Native;
using MelonLoader.Utils;

namespace Sideload.Script
{
    /// <summary>
    /// The `s1` global: everything a page can reach outside its own document. Two directions.
    ///
    ///   * <c>s1.call(name, arg)</c> - the page asks the host mod for something and gets the answer back immediately.
    ///     Synchronous on purpose: the handler runs C# on the same frame on the same thread, so there is nothing to
    ///     await, and a promise would only add a tick of latency plus a class of unhandled-rejection bugs.
    ///   * <c>s1.on(name, fn)</c> - the host pushes an event at the page whenever it likes.
    ///
    /// Values cross as strings. A page that wants structure sends JSON and parses it, which keeps the boundary honest:
    /// no CLR object ever leaks into script, and no script object ever reaches a mod.
    /// </summary>
    public sealed class Bridge
    {
        /// <summary>Handlers keyed by "appId/name", or by "/name" when a mod registers one for every app.
        /// Scoping by app is what keeps two mods from fighting over a plain name like "list".</summary>
        private static readonly Dictionary<string, Func<string, string, string>> _handlers =
            new Dictionary<string, Func<string, string, string>>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, List<Bridge>> _subscribers =
            new Dictionary<string, List<Bridge>>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, List<JsValue>> _listeners =
            new Dictionary<string, List<JsValue>>(StringComparer.OrdinalIgnoreCase);

        private readonly ScriptHost _host;
        private readonly string _appId;

        internal Bridge(ScriptHost host, string appId)
        {
            _host = host;
            _appId = appId;
            Storage = new JsStorage(appId);
        }

        /// <summary>`s1.storage` - a small JSON key/value store per app.</summary>
        public JsStorage Storage { get; }

        /// <summary>The id the app was registered under. Handy for logging and for storage keys a mod also reads.</summary>
        public string AppId => _appId;

        // ------------------------------------------------------------- host-side API --

        /// <summary>
        /// Register what `s1.call(name, ...)` does for one app - or for every app when <paramref name="appId"/> is
        /// null. The handler receives (appId, argument) and returns the answer; throwing is fine and surfaces as a
        /// script error rather than taking the frame down.
        /// </summary>
        internal static void Handle(string appId, string name, Func<string, string, string> handler)
        {
            if (string.IsNullOrWhiteSpace(name) || handler == null) return;
            _handlers[Key(appId, name)] = handler;
        }

        /// <summary>Push an event at the pages of one app, or at every page when <paramref name="appId"/> is null.</summary>
        internal static void Emit(string appId, string name, string payload)
        {
            if (string.IsNullOrWhiteSpace(name)) return;

            // The tap runs BEFORE the subscriber lookup, and that position is load-bearing. A page only subscribes
            // once it has been built, so an app that is open on a companion device but was never opened in-game has
            // no in-game listener at all - taking the early return first would drop exactly the normal case.
            Tap?.Invoke(appId, name, payload ?? "");

            if (!_subscribers.TryGetValue(name, out List<Bridge> bridges)) return;

            foreach (Bridge bridge in bridges.ToArray())
                if (appId == null || string.Equals(bridge._appId, appId, StringComparison.OrdinalIgnoreCase))
                    bridge.Deliver(name, payload ?? "");
        }

        /// <summary>
        /// A listener on every host event, for a mirror of the page that lives outside this process. Null unless
        /// something asked for it, and never more than one - a second consumer would be a second reason to keep
        /// events alive, and the one that exists is a companion server that already multiplexes.
        /// </summary>
        internal static Action<string, string, string> Tap;

        /// <summary>
        /// Run a registered `s1.call` handler without a page. Same lookup and the same failure behaviour as
        /// <see cref="Call"/>, which delegates here - one path, so the two cannot drift.
        ///
        /// MUST be called on the Unity main thread: handlers touch game state.
        /// </summary>
        internal static string Invoke(string appId, string name, string argument)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";

            // This app's own handler wins; a handler registered for every app is the fallback.
            if (!_handlers.TryGetValue(Key(appId, name), out Func<string, string, string> handler)
                && !_handlers.TryGetValue(Key(null, name), out handler))
            {
                Core.Log?.Warning($"[Sideload] {appId}: s1.call('{name}') has no handler.");
                return "";
            }

            try { return handler(appId, argument ?? "") ?? ""; }
            catch (Exception e)
            {
                Core.Log?.Error($"[Sideload] {appId}: s1.call('{name}') threw: {e.Message}");
                return "";
            }
        }

        private static string Key(string appId, string name) => (appId ?? "") + "\u0000" + name.Trim();

        // -------------------------------------------------------------- script-side API --

        /// <summary>
        /// `s1.call(name, arg)` - ask the mod something and get its answer, synchronously.
        ///
        /// THE SCRIPT'S TIME BUDGET DOES NOT COVER THE MOD'S WORK. A handler may run for 250 ms
        /// (ScriptHost.Budget) and that limit is there to catch a runaway script, which this is not: the page is
        /// blocked waiting on C# it does not control, and killing it for the mod being slow punishes the wrong
        /// side. It reads as a page fault - "s1.on('changed') handler failed: The operation has timed out" - on a
        /// page whose own work took a millisecond.
        ///
        /// So the clock is restarted once the mod answers. What still bounds a genuine runaway is MaxStatements,
        /// which a loop calling this a million times reaches on its own.
        /// </summary>
        public string Call(string name, string argument = "")
        {
            try
            {
                return Invoke(_appId, name, argument);
            }
            finally
            {
                _host?.RestartBudget();
            }
        }

        public void On(string name, JsValue handler)
        {
            if (string.IsNullOrWhiteSpace(name) || handler == null || !handler.IsObject()) return;

            if (!_listeners.TryGetValue(name, out List<JsValue> list))
                _listeners[name] = list = new List<JsValue>();
            list.Add(handler);

            if (!_subscribers.TryGetValue(name, out List<Bridge> bridges))
                _subscribers[name] = bridges = new List<Bridge>();
            if (!bridges.Contains(this)) bridges.Add(this);
        }

        public void Log(params object[] args) =>
            Core.Log?.Msg($"[{_appId}] " + string.Join(" ", Array.ConvertAll(args ?? Array.Empty<object>(), a => a?.ToString() ?? "null")));

        /// <summary>
        /// `s1.orientation` - "portrait" or "landscape". Reads the app's setting rather than the measured viewport, so
        /// it answers the same during the rotation animation as after it.
        /// </summary>
        public string Orientation => Registry.Find(_appId)?.Portrait == true ? "portrait" : "landscape";

        /// <summary>
        /// `s1.setOrientation(v)` - turn the phone. The page keeps its document and its script; only the viewport and
        /// the cascade change, so `@media (orientation: ...)` is what decides what the app looks like afterwards.
        /// </summary>
        public void SetOrientation(string orientation) => Registry.SetOrientation(_appId, orientation);

        /// <summary>
        /// Unsubscribe this page from every host event. Without it a reload leaves the old bridge in the static
        /// subscriber list forever: it keeps its whole script engine, listeners and document alive, and the next
        /// Emit runs the handlers of every page that ever existed - so a hundred reloads means a hundred handlers
        /// firing for one event.
        /// </summary>
        internal void Dispose()
        {
            foreach (KeyValuePair<string, List<Bridge>> pair in _subscribers) pair.Value.Remove(this);
            _listeners.Clear();
        }

        private void Deliver(string name, string payload)
        {
            if (!_listeners.TryGetValue(name, out List<JsValue> list)) return;

            foreach (JsValue handler in list.ToArray())
            {
                try { _host.Engine.Invoke(handler, payload); }
                catch (Exception e) { Core.Log?.Error($"[Sideload] {_appId}: s1.on('{name}') handler failed: {e.Message}"); }
            }
        }

        /// <summary>
        /// `s1.storage`: strings keyed by name, one JSON file per app under UserData. Deliberately not the game save -
        /// an app's UI state (last tab, sort order, a draft message) should survive a reload but must never travel
        /// with a save file or diverge between co-op peers.
        /// </summary>
        public sealed class JsStorage
        {
            private readonly string _path;
            private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);
            private bool _loaded;

            internal JsStorage(string appId)
            {
                string folder = Path.Combine(MelonEnvironment.UserDataDirectory, "Sideload");
                _path = Path.Combine(folder, Sanitise(appId) + ".json");
            }

            public string Get(string key, string fallback = "")
            {
                Load();
                return key != null && _values.TryGetValue(key, out string value) ? value : fallback;
            }

            public void Set(string key, string value)
            {
                if (string.IsNullOrEmpty(key)) return;
                Load();
                _values[key] = value ?? "";
                Save();
            }

            public void Remove(string key)
            {
                if (string.IsNullOrEmpty(key)) return;
                Load();
                if (_values.Remove(key)) Save();
            }

            public void Clear()
            {
                Load();
                _values.Clear();
                Save();
            }

            private void Load()
            {
                if (_loaded) return;
                _loaded = true;

                try
                {
                    if (!File.Exists(_path)) return;
                    foreach (KeyValuePair<string, string> pair in MiniJson.ParseObject(File.ReadAllText(_path)))
                        _values[pair.Key] = pair.Value;
                }
                catch (Exception e) { Core.Log?.Warning("[Sideload] storage read failed: " + e.Message); }
            }

            private void Save()
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    File.WriteAllText(_path, MiniJson.WriteObject(_values));
                }
                catch (Exception e) { Core.Log?.Warning("[Sideload] storage write failed: " + e.Message); }
            }

            private static string Sanitise(string id)
            {
                var sb = new System.Text.StringBuilder(id?.Length ?? 0);
                foreach (char c in id ?? "app") sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
                return sb.Length == 0 ? "app" : sb.ToString();
            }
        }
    }
}
