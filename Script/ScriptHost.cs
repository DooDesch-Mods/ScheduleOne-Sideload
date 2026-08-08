using System.Reflection;
using AngleSharp.Dom;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Jint.Runtime.Interop;

namespace Sideload.Script
{
    /// <summary>
    /// Runs one app's JavaScript against one document.
    ///
    /// The engine is Jint - managed, no native dependency, no JIT of its own - which is the only kind of scripting
    /// that survives being loaded into an IL2CPP game by a mod loader. Everything a page can reach is set up here:
    /// `document`, `console`, `s1`, and the timer functions.
    ///
    /// Scripts run synchronously on Unity's main thread. That is a deliberate simplification: a game UI has no use for
    /// a thread pool, and it means a handler can touch the DOM without any locking. A runaway loop is caught by the
    /// engine constraints below rather than by pre-emption.
    /// </summary>
    internal sealed class ScriptHost
    {
        /// <summary>A single handler may not run longer than this. Long enough for any honest page update, short
        /// enough that a mistake shows up as one hitched frame instead of a hung game.</summary>
        private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Every console call and every uncaught script error, mirrored to whoever is listening: (appId, level, the
        /// arguments as the script passed them, the formatted line). The level is a console method name - "log",
        /// "info", "warn", "error" - or "exception" for an error the page did not print itself.
        ///
        /// Null unless the devtools protocol server is running, which is what keeps the log path free of it. Raised on
        /// the main thread, so a handler may read the values it is given.
        /// </summary>
        internal static Action<string, string, object[], string> Diagnostics;

        private readonly Dictionary<IElement, Dictionary<string, List<JsValue>>> _listeners = new();
        private readonly Dictionary<IElement, JsElement> _wrappers = new();
        private readonly List<Timer> _timers = new();
        private readonly string _appId;

        private Promises _promises;
        private FetchApi _fetch;
        private IDocument _document;
        private Bridge _bridge;
        private int _nextTimerId = 1;
        private bool _failed;

        internal ScriptHost(string appId, IDocument document, Action onDomChanged,
                            Action<IElement> onFocusRequested, Action<IElement> onScrollToEnd,
                            Action<IElement> onPaintOnlyChange = null,
                            Func<IElement, float[]> onRectRequested = null)
        {
            _appId = appId;
            _document = document;
            OnDomChanged = onDomChanged;
            OnFocusRequested = onFocusRequested;
            OnScrollToEnd = onScrollToEnd;
            OnPaintOnlyChange = onPaintOnlyChange;
            OnRectRequested = onRectRequested;

            Engine = new Engine(options =>
            {
                options.TimeoutInterval(Budget);
                options.MaxStatements(2_000_000);
                options.LimitRecursion(256);
                options.Strict = false;

                // Everything the engine can do, on. Measured against this build rather than assumed: Jint 3.1.5
                // already covers ES2015 through ES2024 out of the box - classes with private fields, async/await,
                // optional chaining, logical assignment, toSorted, Object.groupBy - and generators plus the iterator
                // protocol are the only parts behind this flag. An app author should not have to write 2010
                // JavaScript because the host was cautious.
                options.ExperimentalFeatures = ExperimentalFeature.All;

                // Jint matches CLR member names verbatim by default, which would make the whole API PascalCase in
                // JavaScript. Lower-casing the first letter turns idiomatic C# into idiomatic JS with no second set of
                // names to keep in sync; both spellings are accepted so a typo in either direction still works.
                options.SetTypeResolver(new TypeResolver { MemberNameCreator = BothCasings });
            });

            Bind();
        }

        internal Engine Engine { get; }

        /// <summary>
        /// Give the running handler its time budget back.
        ///
        /// Called after a host call returns (Bridge.Call), because the budget is meant to catch a script that will
        /// not stop, and time spent inside the mod's own C# is not that - the page is blocked, not looping. Without
        /// this, a mod that takes 300 ms to answer fails the PAGE, and the log blames a handler that did nothing
        /// wrong.
        /// </summary>
        internal void RestartBudget() => Engine?.Constraints.Reset();

        internal Action OnDomChanged { get; }

        internal Action<IElement> OnFocusRequested { get; }

        internal Action<IElement> OnScrollToEnd { get; }

        /// <summary>Where an element ended up, in css pixels, as x/y/width/height. Supplied by the view, because the
        /// layout pass is the only thing that knows.</summary>
        internal Func<IElement, float[]> OnRectRequested { get; }

        /// <summary>
        /// An inline style changed that only affects how one box is PAINTED, never where anything sits. Null falls
        /// back to a full rebuild, so a host that does not offer the fast path still behaves correctly.
        /// </summary>
        internal Action<IElement> OnPaintOnlyChange { get; }

        /// <summary>True once a script error has taken the page down; further work is skipped rather than logged
        /// every frame.</summary>
        internal bool Failed => _failed;

        /// <summary>The most recent error, for the dev overlay - reading it off the screen beats hunting the log.</summary>
        internal string LastError { get; private set; } = "";

        /// <summary>The page changed underneath the script - re-point at the new document and drop stale wrappers.
        /// Listeners survive: they are keyed by element, and the elements themselves are not replaced.</summary>
        internal void Rebound(IDocument document)
        {
            _document = document;
            _wrappers.Clear();
            Engine.SetValue("document", new JsDocument(this, _document));
        }

        /// <summary>
        /// Retire this engine. A page that is reloaded or destroyed must let go of its host subscriptions, its DOM
        /// wrappers and its timers - the bridge is held by a STATIC list, so without this the whole engine stays
        /// reachable for the rest of the session.
        /// </summary>
        internal void Dispose()
        {
            _bridge?.Dispose();
            _listeners.Clear();
            _wrappers.Clear();
            _timers.Clear();
        }

        /// <summary>
        /// Forget a subtree that has left the document. `_wrappers` and `_listeners` are strong maps keyed by
        /// element, so a page that creates and removes nodes - which is what re-rendering a list does - would retain
        /// every node it ever built.
        /// </summary>
        internal void Forget(IElement element)
        {
            if (element == null) return;

            _wrappers.Remove(element);
            _listeners.Remove(element);

            foreach (IElement child in element.Children) Forget(child);
        }

        internal void MarkDirty() => OnDomChanged?.Invoke();

        /// <summary>
        /// Repaint one box instead of rebuilding the page. Only for properties that provably cannot move anything -
        /// see <see cref="DomApi.JsStyle"/> for the list and why each entry is on it. Falls back to a full rebuild
        /// when no repaint path is wired.
        /// </summary>
        internal void MarkPaintDirty(IElement element)
        {
            if (OnPaintOnlyChange == null) { MarkDirty(); return; }
            OnPaintOnlyChange(element);
        }

        internal void RequestFocus(IElement element) => OnFocusRequested?.Invoke(element);

        internal void RequestScrollToEnd(IElement element) => OnScrollToEnd?.Invoke(element);

        internal float[] RectOf(IElement element) => OnRectRequested?.Invoke(element) ?? new[] { 0f, 0f, 0f, 0f };

        internal JsElement Wrap(IElement element)
        {
            if (element == null) return null;
            if (_wrappers.TryGetValue(element, out JsElement existing)) return existing;

            var wrapper = new JsElement(this, element);
            _wrappers[element] = wrapper;
            return wrapper;
        }

        internal JsValue WrapAll(IEnumerable<IElement> elements)
        {
            var items = new List<JsValue>();
            if (elements != null)
                foreach (IElement e in elements) items.Add(JsValue.FromObject(Engine, Wrap(e)));

            return new JsArray(Engine, items.ToArray());
        }

        /// <summary>Execute the app's script. Any error becomes a log line and disables scripting for this page - the
        /// rendered HTML stays on screen, which is far more useful than a blank panel.</summary>
        internal void Run(string source, string fileName)
        {
            if (string.IsNullOrWhiteSpace(source)) return;

            WarnAboutAwaitedFetch(source, fileName);

            try
            {
                Engine.Execute(source, fileName);
                Core.Log?.Msg($"[Sideload] {fileName} executed ({source.Length} chars).");
            }
            catch (Exception e)
            {
                _failed = true;
                LastError = Describe(e);
                Core.Log?.Error($"[Sideload] {fileName} failed: {LastError}");
                Diagnostics?.Invoke(_appId, "exception", null, $"{fileName}: {LastError}");
            }
        }

        /// <summary>
        /// Shout about `await fetch(...)` before it runs, because the failure it produces is the worst kind: the game
        /// stops responding with nothing in the log.
        ///
        /// Jint 3.1.5 implements `await` as a blocking wait on the promise's wait handle, on the calling thread - the
        /// same main thread that would settle a fetch a frame later. Nothing can interrupt it: it is a wait handle,
        /// not a statement loop, so neither the time budget nor the statement cap ever fires.
        ///
        /// The check is deliberately coarse. A false warning costs a log line; a missed one costs the player their
        /// session.
        /// </summary>
        private static void WarnAboutAwaitedFetch(string source, string fileName)
        {
            if (source.IndexOf("fetch(", StringComparison.Ordinal) < 0) return;
            if (!System.Text.RegularExpressions.Regex.IsMatch(source, @"\bawait\b")) return;

            Core.Log?.Warning(
                $"[Sideload] {fileName} uses both `await` and `fetch(`. Awaiting a PENDING promise freezes the game " +
                "on this engine - it blocks the main thread that would settle it. Use `fetch(url).then(res => ...)` " +
                "instead. Awaiting an already-settled promise, such as `res.text()`, is safe.");
        }

        /// <summary>
        /// Evaluate a snippet and hand back what it produced, rendered as text. Separate from <see cref="Run"/>
        /// because a whole file has no value worth reporting while a one-line probe is nothing BUT its value - a tool
        /// that answers "" to `1 + 1` is a tool nobody trusts.
        ///
        /// A failure comes back as its message rather than as an exception: the caller is a debug tool, and a broken
        /// expression typed into it is an ordinary event, not a fault in the page.
        /// </summary>
        internal string Evaluate(string source, out bool failed)
        {
            failed = false;
            if (string.IsNullOrWhiteSpace(source)) return "";

            try
            {
                JsValue value = Engine.Evaluate(source);
                return value.IsUndefined() ? "undefined" : Describe(value);
            }
            catch (Exception e)
            {
                failed = true;
                LastError = Describe(e);
                return LastError;
            }
        }

        /// <summary>A value as a debug tool wants to read it: JSON for anything structured, the plain text for the
        /// rest. Falling back to ToString keeps a function or a host object from becoming an error.</summary>
        private string Describe(JsValue value)
        {
            try
            {
                if (value.IsNull()) return "null";
                if (value.IsString() || value.IsNumber() || value.IsBoolean()) return value.ToString();

                JsValue json = Engine.Evaluate("JSON.stringify").Call(JsValue.Undefined, value);
                return json.IsUndefined() ? value.ToString() : json.AsString();
            }
            catch { return value.ToString(); }
        }

        // ------------------------------------------------------------------ events --

        /// <summary>
        /// Every event type this engine can actually deliver. A page may register anything - `addEventListener`
        /// takes a string and asks no questions - but a listener on a type outside this set is dead code that
        /// looks alive, which is the most expensive kind of gap there is: the handler is right there in the file,
        /// it just never runs, and nothing anywhere says so.
        /// </summary>
        private static readonly HashSet<string> Dispatchable = new(StringComparer.OrdinalIgnoreCase)
        {
            "click", "mouseenter", "mouseleave", "wheel", "input", "keydown",
            "dragstart", "drag", "dragend", "orientationchange", "back",
        };

        internal void AddListener(IElement element, string type, JsValue handler)
        {
            if (element == null || string.IsNullOrEmpty(type) || handler == null || !handler.IsObject()) return;

            if (!Dispatchable.Contains(type))
                Model.Diagnostics.Report(Model.DiagnosticKind.DeadEventListener, type);

            if (!_listeners.TryGetValue(element, out Dictionary<string, List<JsValue>> byType))
                _listeners[element] = byType = new Dictionary<string, List<JsValue>>(StringComparer.OrdinalIgnoreCase);

            if (!byType.TryGetValue(type, out List<JsValue> handlers))
                byType[type] = handlers = new List<JsValue>();

            handlers.Add(handler);
        }

        internal void RemoveListener(IElement element, string type, JsValue handler)
        {
            if (element == null || handler == null) return;
            if (!_listeners.TryGetValue(element, out Dictionary<string, List<JsValue>> byType)) return;
            if (!byType.TryGetValue(type, out List<JsValue> handlers)) return;

            handlers.RemoveAll(h => ReferenceEquals(h, handler) || Equals(h, handler));
        }

        /// <summary>Which elements listen for that event type. Asked before wiring pointer handling, so a page that
        /// uses no script stays free of the extra hit targets.</summary>
        internal IEnumerable<IElement> ElementsListeningFor(string type)
        {
            foreach (KeyValuePair<IElement, Dictionary<string, List<JsValue>>> pair in _listeners)
                if (pair.Value.TryGetValue(type, out List<JsValue> handlers) && handlers.Count > 0)
                    yield return pair.Key;
        }

        /// <summary>
        /// Fire an event at an element and let it bubble to the document, exactly as the DOM does: each ancestor's
        /// handlers run in registration order, and `stopPropagation` ends the walk.
        /// </summary>
        internal JsEvent Dispatch(IElement target, string type, string value = "", string key = "", string source = "",
                                  Input.PointerSpot spot = default,
                                  float deltaX = 0f, float deltaY = 0f, float wheelDelta = 0f,
                                  bool ctrl = false, bool shift = false, bool alt = false, bool repeat = false,
                                  bool hasSelection = false, bool bubbles = true)
        {
            var evt = new JsEvent(type, Wrap(target))
            {
                Value = value ?? "", Key = key ?? "", Source = source ?? "",
                OffsetX = spot.OffsetX, OffsetY = spot.OffsetY, NormX = spot.NormX, NormY = spot.NormY,
                DeltaX = deltaX, DeltaY = deltaY, WheelDelta = wheelDelta,
                CtrlKey = ctrl, ShiftKey = shift, AltKey = alt, Repeat = repeat, HasSelection = hasSelection,
            };
            if (_failed || target == null) return evt;

            IElement node = target;
            while (node != null)
            {
                if (_listeners.TryGetValue(node, out Dictionary<string, List<JsValue>> byType)
                    && byType.TryGetValue(type, out List<JsValue> handlers)
                    && handlers.Count > 0)
                {
                    evt.CurrentTarget = Wrap(node);

                    // Copied first: a handler is allowed to remove itself or its siblings while the event is running.
                    foreach (JsValue handler in handlers.ToArray())
                    {
                        Invoke(handler, $"{type} handler on <{node.LocalName}>", JsValue.FromObject(Engine, evt));
                        if (evt.PropagationStopped) return evt;
                    }
                }

                // mouseenter/mouseleave do not bubble, here as in a browser: a tooltip that fired again for every
                // ancestor would open and shut as the pointer crossed each nested box on the way in.
                node = bubbles ? node.ParentElement : null;
            }

            return evt;
        }

        // ------------------------------------------------------------------ timers --

        private sealed class Timer
        {
            internal int Id;
            internal JsValue Callback;
            internal double Remaining;
            internal double Interval;   // 0 = one-shot
            internal bool Cancelled;
        }

        /// <summary>Advance timers by one frame. Called from the mod's update loop; nothing else drives the script
        /// once the page is built.</summary>
        internal void Tick(float deltaSeconds)
        {
            if (_failed) return;

            // Anything the network finished since the last frame is handed to its promise first, so a response that
            // landed mid-render settles in THIS frame's continuations rather than waiting for the next one.
            try { _fetch?.Settle(); }
            catch (Exception e) { Report("settling a fetch", e); }

            // Promise continuations scheduled from a previous frame settle here. Pump resets the engine's time budget
            // before draining: the budget is reset only by an entry point, and ProcessTasks is not one, so without
            // that reset every continuation more than 250 ms after the last script call dies with a TimeoutException
            // before running a statement.
            try { _promises?.Pump(); }
            catch (Exception e) { Report("a pending promise", e); }

            if (_timers.Count == 0) return;

            double ms = deltaSeconds * 1000.0;

            // Snapshot: a callback may add or clear timers, and mutating the list mid-walk would skip entries.
            foreach (Timer timer in _timers.ToArray())
            {
                if (timer.Cancelled) continue;

                timer.Remaining -= ms;
                if (timer.Remaining > 0) continue;

                if (timer.Interval > 0) timer.Remaining += timer.Interval;
                else timer.Cancelled = true;

                Invoke(timer.Callback, $"timer {timer.Id}");
            }

            _timers.RemoveAll(t => t.Cancelled);
        }

        private int AddTimer(JsValue callback, double delayMs, bool repeating)
        {
            if (callback == null || !callback.IsObject()) return 0;

            var timer = new Timer
            {
                Id = _nextTimerId++,
                Callback = callback,
                Remaining = Math.Max(delayMs, 0),
                Interval = repeating ? Math.Max(delayMs, 16) : 0,
            };
            _timers.Add(timer);
            return timer.Id;
        }

        private void ClearTimer(double id)
        {
            foreach (Timer timer in _timers)
                if (timer.Id == (int)id) timer.Cancelled = true;
        }

        // ------------------------------------------------------------------ plumbing --

        private void Invoke(JsValue callback, string what, params JsValue[] args)
        {
            try { Engine.Invoke(callback, args); }
            catch (Exception e)
            {
                LastError = Describe(e);
                Core.Log?.Error($"[Sideload] {what} failed: {LastError}");
                Diagnostics?.Invoke(_appId, "exception", null, $"{what} failed: {LastError}");
            }
        }

        /// <summary>A failure the page did not print itself: the log, plus an attached inspector if there is one -
        /// the same route <c>console.error</c> takes, so nothing that goes wrong is visible in only one place.</summary>
        private void Report(string what, Exception e)
        {
            LastError = Describe(e);
            Fault($"{what} failed: {LastError}");
        }

        private void Fault(string line)
        {
            Core.Log?.Error($"[Sideload] [{_appId}] {line}");
            Diagnostics?.Invoke(_appId, "error", null, $"[{_appId}] {line}");
        }

        private void Bind()
        {
            Engine.SetValue("document", new JsDocument(this, _document));
            _bridge = new Bridge(this, _appId);
            Engine.SetValue("s1", _bridge);

            Engine.SetValue("console", new Console(_appId));

            // A rejection nobody in the chain took would otherwise disappear - Jint has no unhandled-rejection hook,
            // and a page that forgot a `.catch` would look like a page whose fetch never came back.
            _promises = new Promises(Engine, message => Fault("unhandled promise rejection: " + message));
            _fetch = new FetchApi(Engine, _appId, _promises, line => Fault(line));
            _fetch.Install();

            Engine.SetValue("setTimeout", new Func<JsValue, double, int>((fn, ms) => AddTimer(fn, ms, repeating: false)));
            Engine.SetValue("setInterval", new Func<JsValue, double, int>((fn, ms) => AddTimer(fn, ms, repeating: true)));
            Engine.SetValue("clearTimeout", new Action<double>(ClearTimer));
            Engine.SetValue("clearInterval", new Action<double>(ClearTimer));
        }

        /// <summary>Both the camelCase spelling JavaScript expects and the original CLR name, so neither side has to
        /// guess.</summary>
        private static IEnumerable<string> BothCasings(MemberInfo member)
        {
            string name = member.Name;
            if (string.IsNullOrEmpty(name)) yield break;

            string camel = char.IsUpper(name[0]) ? char.ToLowerInvariant(name[0]) + name.Substring(1) : name;
            yield return camel;
            if (camel != name) yield return name;
        }

        /// <summary>A script error with its source position, which is the difference between a usable message and
        /// "Object reference not set".</summary>
        private static string Describe(Exception e)
        {
            if (e is JavaScriptException js)
                return $"{js.Message} ({js.Location.Source}:{js.Location.Start.Line}:{js.Location.Start.Column})";

            if (e is Esprima.ParserException parse)
                return $"syntax error: {parse.Message}";

            return e.ToString();
        }

        /// <summary>The `console` global. Everything lands in the MelonLoader log, prefixed with the app id so two
        /// apps cannot be confused for each other.</summary>
        public sealed class Console
        {
            private readonly string _appId;

            internal Console(string appId) => _appId = appId;

            public void Log(params object[] args) => Emit("log", args);

            public void Info(params object[] args) => Emit("info", args);

            public void Warn(params object[] args) => Emit("warn", args);

            public void Error(params object[] args) => Emit("error", args);

            /// <summary>To the log, and to an attached inspector if there is one. The arguments are passed on
            /// untouched so a console that can render values gets values rather than the flattened line.</summary>
            private void Emit(string level, object[] args)
            {
                string line = Format(args);

                switch (level)
                {
                    case "warn": Core.Log?.Warning(line); break;
                    case "error": Core.Log?.Error(line); break;
                    default: Core.Log?.Msg(line); break;
                }

                Diagnostics?.Invoke(_appId, level, args, line);
            }

            private string Format(object[] args)
            {
                if (args == null || args.Length == 0) return $"[{_appId}]";
                return $"[{_appId}] " + string.Join(" ", Array.ConvertAll(args, a => a?.ToString() ?? "null"));
            }
        }
    }
}
