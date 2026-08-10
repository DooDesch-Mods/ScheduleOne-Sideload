using System.Reflection;
using AngleSharp.Dom;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
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
        /// <summary>How long script may run in one go. Two limits, not one - see <see cref="TimeBudget"/>.</summary>
        private readonly TimeBudget _budget = new TimeBudget();

        /// <summary>
        /// Every console call and every uncaught script error, mirrored to whoever is listening: (appId, level, the
        /// arguments as the script passed them, the formatted line). The level is a console method name - "log",
        /// "info", "warn", "error" - or "exception" for an error the page did not print itself.
        ///
        /// Null unless the devtools protocol server is running, which is what keeps the log path free of it. Raised on
        /// the main thread, so a handler may read the values it is given.
        /// </summary>
        internal static Action<string, string, object[], string> Diagnostics = null;

        /// <summary>One registration. The capture flag is part of the identity: the DOM lets the same function be
        /// registered twice for one type, once per phase, and removing one must not remove the other.</summary>
        private readonly struct Listener
        {
            internal Listener(JsValue handler, bool capture)
            {
                Handler = handler;
                Capture = capture;
            }

            internal JsValue Handler { get; }

            internal bool Capture { get; }
        }

        private readonly Dictionary<INode, Dictionary<string, List<Listener>>> _listeners = new();
        private readonly Dictionary<INode, JsNode> _wrappers = new();
        private readonly List<Timer> _timers = new();
        private readonly string _appId;

        private Promises _promises;
        private FetchApi _fetch;
        private IDocument _document;
        private JsDocument _documentObject;
        private Bridge _bridge;
        private int _nextTimerId = 1;
        private bool _failed;

        internal ScriptHost(string appId, IDocument document, Action onDomChanged,
                            Action<IElement> onFocusRequested, Action<IElement> onScrollToEnd,
                            Action<IElement> onPaintOnlyChange = null,
                            Func<IElement, float[]> onRectRequested = null,
                            Action<IElement> onBlurRequested = null,
                            Func<IElement> onActiveElementRequested = null,
                            Func<float[]> onViewportRequested = null)
        {
            _appId = appId;
            _document = document;
            OnDomChanged = onDomChanged;
            OnFocusRequested = onFocusRequested;
            OnScrollToEnd = onScrollToEnd;
            OnPaintOnlyChange = onPaintOnlyChange;
            OnRectRequested = onRectRequested;
            OnBlurRequested = onBlurRequested;
            OnActiveElementRequested = onActiveElementRequested;
            OnViewportRequested = onViewportRequested;

            Engine = new Engine(options =>
            {
                options.Constraints.Constraints.Add(_budget);
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

        /// <summary>`el.blur()`. The counterpart to focus, and the only way a page can hand the keyboard back to
        /// the game - which it owes the player the moment it took it.</summary>
        internal Action<IElement> OnBlurRequested { get; }

        internal Action<IElement> OnScrollToEnd { get; }

        /// <summary>Where an element ended up, in css pixels, as x/y/width/height. Supplied by the view, because the
        /// layout pass is the only thing that knows.</summary>
        internal Func<IElement, float[]> OnRectRequested { get; }

        /// <summary>`document.activeElement`. The caret lives in TextMeshPro rather than in the document, so only the
        /// view can answer it.</summary>
        internal Func<IElement> OnActiveElementRequested { get; }

        /// <summary>`window.innerWidth`/`innerHeight`, in css pixels.</summary>
        internal Func<float[]> OnViewportRequested { get; }

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
            _documentObject = new JsDocument(this, _document);
            Engine.SetValue("document", _documentObject);
        }

        /// <summary>The one `document` object, so `node.ownerDocument === document` holds - which a renderer checks
        /// before it will mount into a node.</summary>
        internal JsValue DocumentObject => JsValue.FromObject(Engine, _documentObject);

        internal IElement ActiveElement() => OnActiveElementRequested?.Invoke();

        /// <summary>
        /// Retire this engine. A page that is reloaded or destroyed must let go of its host subscriptions, its DOM
        /// wrappers and its timers - the bridge is held by a STATIC list, so without this the whole engine stays
        /// reachable for the rest of the session.
        /// </summary>
        internal void Dispose()
        {
            _bridge?.Dispose();
            _listeners.Clear();
            _inlineHandlers.Clear();
            _wrappers.Clear();
            _timers.Clear();
        }

        /// <summary>
        /// Forget a subtree that has left the document. `_wrappers` and `_listeners` are strong maps keyed by
        /// element, so a page that creates and removes nodes - which is what re-rendering a list does - would retain
        /// every node it ever built.
        /// </summary>
        internal void Forget(INode node)
        {
            if (node == null) return;

            _wrappers.Remove(node);
            _listeners.Remove(node);
            _inlineHandlers.Remove(node);

            foreach (INode child in node.ChildNodes) Forget(child);
        }

        internal void MarkDirty() => OnDomChanged?.Invoke();

        /// <summary>
        /// Repaint one box instead of rebuilding the page. Only for properties that provably cannot move anything -
        /// see <see cref="DomApi.JsStyle"/> for the list and why each entry is on it. Falls back to a full rebuild
        /// when no repaint path is wired.
        /// </summary>
        /// <summary>
        /// New text for an element whose box is nothing but that text. The view takes it if the string measures
        /// exactly as the old one did, and says no otherwise - so the caller must fall back to a full rebuild.
        ///
        /// A clock is the case: one text node a second, four glyphs different, the same width every time.
        /// </summary>
        internal bool TryRetext(IElement element, string text) =>
            OnTextOnlyChange != null && OnTextOnlyChange(element, text);

        internal Func<IElement, string, bool> OnTextOnlyChange { get; set; }

        internal void MarkPaintDirty(IElement element)
        {
            if (OnPaintOnlyChange == null) { MarkDirty(); return; }
            OnPaintOnlyChange(element);
        }

        internal void RequestFocus(IElement element) => OnFocusRequested?.Invoke(element);

        internal void RequestBlur(IElement element) => OnBlurRequested?.Invoke(element);

        internal void RequestScrollToEnd(IElement element) => OnScrollToEnd?.Invoke(element);

        internal float[] RectOf(IElement element) => OnRectRequested?.Invoke(element) ?? new[] { 0f, 0f, 0f, 0f };

        internal JsElement Wrap(IElement element) => (JsElement)WrapNode(element);

        /// <summary>
        /// The ONE wrapper for a node, for as long as that node is in the document.
        ///
        /// Identity is the load-bearing part, not the caching. A renderer compares the node it is looking at with
        /// `parent.childNodes[i]` and with `node.nextSibling` to decide whether to move anything; hand it a fresh
        /// wrapper each time and every comparison says "different", so it moves every node on every update.
        /// </summary>
        internal JsNode WrapNode(INode node)
        {
            if (node == null) return null;
            if (_wrappers.TryGetValue(node, out JsNode existing)) return existing;

            JsNode wrapper = node switch
            {
                IElement element => new JsElement(this, element),
                IText text => new JsText(this, text),
                IComment comment => new JsComment(this, comment),
                _ => null,
            };

            if (wrapper != null) _wrappers[node] = wrapper;
            return wrapper;
        }

        internal JsValue WrapNodes(IEnumerable<INode> nodes)
        {
            var items = new List<JsValue>();
            if (nodes != null)
                foreach (INode n in nodes)
                {
                    JsNode wrapped = WrapNode(n);
                    if (wrapped != null) items.Add(wrapped);
                }

            return new JsArray(Engine, items.ToArray());
        }

        internal JsValue WrapNodes(IEnumerable<IElement> elements)
        {
            var items = new List<JsValue>();
            if (elements != null)
                foreach (IElement e in elements)
                {
                    JsNode wrapped = WrapNode(e);
                    if (wrapped != null) items.Add(wrapped);
                }

            return new JsArray(Engine, items.ToArray());
        }

        /// <summary>Execute the app's script. Any error becomes a log line and disables scripting for this page - the
        /// rendered HTML stays on screen, which is far more useful than a blank panel.</summary>
        /// <summary>
        /// Scripts already parsed, keyed by their own text.
        ///
        /// Parsing dominates loading a page and nothing else comes close: a bundled framework measures around
        /// 63 ms to parse and 3 ms to run, on a 250 ms budget for the whole build. The same bytes are parsed
        /// again on every reload, on every reopen, and once per view when two apps share a library - so the
        /// cache is keyed by the SOURCE rather than by the file name, and a hot reload that did not actually
        /// change a file costs nothing.
        ///
        /// Static, and deliberately: two apps running the same framework should parse it once between them.
        /// A prepared script carries no engine state, so sharing one across engines is safe.
        /// </summary>
        private static readonly Dictionary<string, Prepared<Esprima.Ast.Script>> _prepared =
            new Dictionary<string, Prepared<Esprima.Ast.Script>>(StringComparer.Ordinal);

        /// <summary>Above this the cache is not worth the memory - a small inline script parses in microseconds
        /// and there are many of them, while the one that costs is always a bundle.</summary>
        private const int PrepareFrom = 2048;

        internal void Run(string source, string fileName)
        {
            if (string.IsNullOrWhiteSpace(source)) return;

            WarnAboutAwaitedFetch(source, fileName);

            try
            {
                // On the load ceiling, not the handler one: a framework evaluates its modules AND performs its first
                // render inside this single call, and a page that legitimately needs half a second to appear must not
                // be killed by the limit that exists to catch a runaway click handler.
                _budget.During(Engine, TimeBudget.Load, () =>
                {
                    if (source.Length >= PrepareFrom)
                    {
                        if (!_prepared.TryGetValue(source, out Prepared<Esprima.Ast.Script> script))
                            _prepared[source] = script = Engine.PrepareScript(source, fileName);

                        Engine.Execute(script);
                    }
                    else
                    {
                        Engine.Execute(source, fileName);
                    }
                });

                Model.Platform.Msg($"{fileName} executed ({source.Length} chars).");
            }
            catch (Exception e)
            {
                _failed = true;
                LastError = Describe(e);
                Model.Platform.Error($"{fileName} failed: {LastError}");
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

            Model.Platform.Warning(
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
            "click", "dblclick", "contextmenu", "mousedown", "mouseup",
            "mouseenter", "mouseleave", "mouseover", "mouseout", "mousemove",
            "wheel", "input", "change", "keydown",
            "focus", "blur", "focusin", "focusout",
            "dragstart", "drag", "dragend", "orientationchange", "back",
        };

        /// <summary>
        /// The events that do NOT bubble, as the DOM has them.
        ///
        /// `mouseenter` and `mouseleave` are the pair a tooltip uses: one that bubbled would open and shut as the
        /// pointer crossed each nested box on the way in. `focus` and `blur` do not bubble either, which is why
        /// `focusin` and `focusout` exist at all - a form that wants to hear about any of its fields listens to
        /// those on the form.
        /// </summary>
        private static readonly HashSet<string> NonBubbling = new(StringComparer.OrdinalIgnoreCase)
        {
            "mouseenter", "mouseleave", "focus", "blur",
        };

        /// <summary>Whether an event of this type bubbles. Asked by the view, so the rule lives in one place rather
        /// than at each of the dozen call sites that raise one.</summary>
        internal static bool Bubbles(string type) => !NonBubbling.Contains(type ?? "");

        internal void AddListener(INode node, string type, JsValue handler, bool capture = false)
        {
            if (node == null || string.IsNullOrEmpty(type) || handler == null || !handler.IsObject()) return;

            if (!Dispatchable.Contains(type))
                Model.Diagnostics.Report(Model.DiagnosticKind.DeadEventListener, type);

            if (!_listeners.TryGetValue(node, out Dictionary<string, List<Listener>> byType))
                _listeners[node] = byType = new Dictionary<string, List<Listener>>(StringComparer.OrdinalIgnoreCase);

            if (!byType.TryGetValue(type, out List<Listener> handlers))
                byType[type] = handlers = new List<Listener>();

            // The DOM ignores a second registration of the same function in the same phase. Without that rule a
            // component that re-attaches its handler on every update runs it once more each time.
            foreach (Listener existing in handlers)
                if (existing.Capture == capture && Same(existing.Handler, handler)) return;

            handlers.Add(new Listener(handler, capture));

            // A listener decides whether the element gets a hit target at all - WireInteraction asks
            // ElementsListeningFor. Adding one to an already-painted box therefore has to rebuild, or the box
            // stays inert until some unrelated change happens to repaint it. That is invisible while a page
            // wires everything up front, and immediate the moment anything attaches a handler later, which is
            // what every framework does when a prop appears.
            MarkDirty();
        }

        internal void RemoveListener(INode node, string type, JsValue handler, bool capture = false)
        {
            if (node == null || handler == null || string.IsNullOrEmpty(type)) return;
            if (!_listeners.TryGetValue(node, out Dictionary<string, List<Listener>> byType)) return;
            if (!byType.TryGetValue(type, out List<Listener> handlers)) return;

            if (handlers.RemoveAll(l => l.Capture == capture && Same(l.Handler, handler)) > 0)
            {
                // Same reason as adding one: the last listener leaving means the element should stop taking the
                // pointer, and until a rebuild it keeps intercepting clicks meant for whatever is behind it.
                MarkDirty();
            }
        }

        private static bool Same(JsValue a, JsValue b) => ReferenceEquals(a, b) || Equals(a, b);

        /// <summary>
        /// `el.onclick`. One handler per element per type, replacing whatever was there - which is the difference
        /// between this and addEventListener, and the reason both exist.
        /// </summary>
        internal JsValue InlineHandler(INode node, string type)
        {
            if (_inlineHandlers.TryGetValue(node, out Dictionary<string, JsValue> byType)
                && byType.TryGetValue(type, out JsValue handler)) return handler;

            return JsValue.Null;
        }

        internal void SetInlineHandler(INode node, string type, JsValue handler)
        {
            if (!_inlineHandlers.TryGetValue(node, out Dictionary<string, JsValue> byType))
                _inlineHandlers[node] = byType = new Dictionary<string, JsValue>(StringComparer.OrdinalIgnoreCase);

            if (byType.TryGetValue(type, out JsValue previous)) RemoveListener(node, type, previous);

            if (handler == null || !handler.IsObject()) { byType.Remove(type); return; }

            byType[type] = handler;
            AddListener(node, type, handler);
        }

        private readonly Dictionary<INode, Dictionary<string, JsValue>> _inlineHandlers = new();

        /// <summary>Which elements listen for that event type. Asked before wiring pointer handling, so a page that
        /// uses no script stays free of the extra hit targets.</summary>
        internal IEnumerable<IElement> ElementsListeningFor(string type)
        {
            foreach (KeyValuePair<INode, Dictionary<string, List<Listener>>> pair in _listeners)
                if (pair.Key is IElement element
                    && pair.Value.TryGetValue(type, out List<Listener> handlers) && handlers.Count > 0)
                    yield return element;
        }

        /// <summary>
        /// Fire an event at an element and let it bubble to the document, exactly as the DOM does: each ancestor's
        /// handlers run in registration order, and `stopPropagation` ends the walk.
        /// </summary>
        internal JsEvent Dispatch(IElement target, string type, string value = "", string key = "", string source = "",
                                  Input.PointerSpot spot = default,
                                  float deltaX = 0f, float deltaY = 0f, float wheelDelta = 0f,
                                  bool ctrl = false, bool shift = false, bool alt = false, bool repeat = false,
                                  bool hasSelection = false, bool? bubbles = null,
                                  float clientX = 0f, float clientY = 0f, int button = 0, int detail = 0)
        {
            var evt = new JsEvent(type, WrapNode(target))
            {
                Value = value ?? "", Key = key ?? "", Source = source ?? "",
                OffsetX = spot.OffsetX, OffsetY = spot.OffsetY, NormX = spot.NormX, NormY = spot.NormY,
                DeltaX = deltaX, DeltaY = deltaY, WheelDelta = wheelDelta,
                CtrlKey = ctrl, ShiftKey = shift, AltKey = alt, Repeat = repeat, HasSelection = hasSelection,
                ClientX = clientX, ClientY = clientY, Button = button, Detail = detail,
            };
            if (_failed || target == null) return evt;

            // The type decides, unless a caller overrides it. Passing the rule in from a dozen call sites is
            // how half of them end up disagreeing with the other half.
            bool climbs = bubbles ?? Bubbles(type);
            evt.Bubbles = climbs;

            // The path, root first. A browser walks it down running the capturing listeners, then back up running the
            // bubbling ones, and the two halves are not interchangeable: a delegating listener registered with
            // capture exists precisely to see the event BEFORE the element it happened on can stop it.
            var path = new List<IElement>();
            for (IElement walk = target; walk != null; walk = walk.ParentElement)
            {
                path.Add(walk);

                // mouseenter/mouseleave do not bubble, here as in a browser: a tooltip that fired again for every
                // ancestor would open and shut as the pointer crossed each nested box on the way in. Such an event
                // has no path beyond its target - not even a capturing one, which is what a browser does too.
                if (!climbs) break;
            }

            for (int i = path.Count - 1; i > 0; i--)
            {
                evt.EventPhase = 1;
                if (!RunHandlers(path[i], type, evt, capture: true)) return evt;
            }

            evt.EventPhase = 2;
            if (!RunHandlers(target, type, evt, capture: true)) return evt;
            if (!RunHandlers(target, type, evt, capture: false)) return evt;

            for (int i = 1; i < path.Count; i++)
            {
                evt.EventPhase = 3;
                if (!RunHandlers(path[i], type, evt, capture: false)) return evt;
            }

            return evt;
        }

        /// <summary>Runs one node's handlers for one phase. False means the walk is over.</summary>
        private bool RunHandlers(IElement node, string type, JsEvent evt, bool capture)
        {
            if (!_listeners.TryGetValue(node, out Dictionary<string, List<Listener>> byType)) return true;
            if (!byType.TryGetValue(type, out List<Listener> handlers) || handlers.Count == 0) return true;

            JsNode self = WrapNode(node);
            evt.CurrentTarget = self;

            // Copied first: a handler is allowed to remove itself or its siblings while the event is running.
            foreach (Listener listener in handlers.ToArray())
            {
                if (listener.Capture != capture) continue;

                InvokeOn(listener.Handler, self, $"{type} handler on <{node.LocalName}>",
                         JsValue.FromObject(Engine, evt));
                if (evt.ImmediatelyStopped) return false;
            }

            return !evt.PropagationStopped;
        }

        /// <summary>
        /// `node.dispatchEvent(evt)` - the page raising its own event. Only the type is taken from the object handed
        /// in; everything else a synthetic event could carry has no meaning here, and inventing values for it would
        /// let a page fake a pointer position the renderer never measured.
        /// </summary>
        internal JsValue DispatchFromScript(INode node, ObjectInstance evt)
        {
            if (node is not IElement element || evt == null) return true;

            JsValue type = evt.Get("type");
            if (type.IsUndefined() || type.IsNull()) return true;

            JsEvent raised = Dispatch(element, type.ToString());
            return !raised.DefaultPrevented;
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

        private void Invoke(JsValue callback, string what, params JsValue[] args) =>
            InvokeOn(callback, JsValue.Undefined, what, args);

        /// <summary>
        /// Call a script function with a `this` of our choosing.
        ///
        /// An event listener is called with `this` set to the element it was registered on, and that is not a
        /// nicety: Preact registers ONE shared function for every element and has it look up
        /// `this._listeners[type]` to find the real handler. Called with an undefined `this` it throws on the first
        /// click - the page mounts, updates, reorders its lists, and does nothing at all when touched.
        /// </summary>
        private void InvokeOn(JsValue callback, JsValue self, string what, params JsValue[] args)
        {
            try { Engine.Invoke(callback, self, Array.ConvertAll(args, a => (object)a)); }
            catch (Exception e)
            {
                LastError = Describe(e);
                Model.Platform.Error($"{what} failed: {LastError}");
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
            Model.Platform.Error($"[{_appId}] {line}");
            Diagnostics?.Invoke(_appId, "error", null, $"[{_appId}] {line}");
        }

        private void Bind()
        {
            Types = new DomTypes(Engine);

            _documentObject = new JsDocument(this, _document);
            Engine.SetValue("document", _documentObject);
            _bridge = new Bridge(this, _appId);
            Engine.SetValue("s1", _bridge);

            // The same store under the name a library knows. `sessionStorage` is the same object rather than a
            // second one that empties on reload: an app is reloaded by a developer saving a file, and losing the
            // page's state on every save is a worse lie than outliving the session.
            Engine.SetValue("localStorage", _bridge.Storage);
            Engine.SetValue("sessionStorage", _bridge.Storage);

            Engine.SetValue("console", new Console(_appId));

            BindWindow();

            // A rejection nobody in the chain took would otherwise disappear - Jint has no unhandled-rejection hook,
            // and a page that forgot a `.catch` would look like a page whose fetch never came back.
            _promises = new Promises(Engine, message => Fault("unhandled promise rejection: " + message));
            _fetch = new FetchApi(Engine, _appId, _promises, line => Fault(line));
            _fetch.Install();

            WebApis.Install(Engine);

            Engine.SetValue("setTimeout", new Func<JsValue, double, int>((fn, ms) => AddTimer(fn, ms, repeating: false)));
            Engine.SetValue("setInterval", new Func<JsValue, double, int>((fn, ms) => AddTimer(fn, ms, repeating: true)));
            Engine.SetValue("clearTimeout", new Action<double>(ClearTimer));
            Engine.SetValue("clearInterval", new Action<double>(ClearTimer));

            // One frame at 60 Hz. A page that animates uses this rather than setInterval, and so does every hook
            // implementation that defers an effect until after paint - without it they fall back to a 100 ms timer
            // and the first frame of every animation is late.
            Engine.SetValue("requestAnimationFrame", new Func<JsValue, int>(fn => AddTimer(fn, 16, repeating: false)));
            Engine.SetValue("cancelAnimationFrame", new Action<double>(ClearTimer));
        }

        /// <summary>
        /// `window`, which IS the global object - the same arrangement a browser has, where `window.foo = 1` and
        /// `var foo = 1` reach the same place.
        ///
        /// That equivalence is the point. Bundled code assigns its export to `window.Something` and reads it back as
        /// a bare name a moment later; a `window` that was a separate object would swallow the first half and leave
        /// the page with an undefined global and no error to explain it.
        /// </summary>
        /// <summary>The DOM's type chain, so `instanceof` works. Built once per engine, before the first wrapper -
        /// a node created earlier would carry the plain object prototype for the rest of its life.</summary>
        internal DomTypes Types { get; private set; }

        private void BindWindow()
        {
            ObjectInstance global = Engine.Global;
            Engine.SetValue("window", global);
            Engine.SetValue("self", global);
            Engine.SetValue("globalThis", global);

            Engine.SetValue("addEventListener", new ClrFunction(Engine, "addEventListener", (_, a) =>
            {
                AddListener(Root(), a.Length > 0 ? a[0].ToString() : null, a.Length > 1 ? a[1] : null,
                            JsDocument.IsCapture(a.Length > 2 ? a[2] : null));
                return JsValue.Undefined;
            }));

            Engine.SetValue("removeEventListener", new ClrFunction(Engine, "removeEventListener", (_, a) =>
            {
                RemoveListener(Root(), a.Length > 0 ? a[0].ToString() : null, a.Length > 1 ? a[1] : null,
                               JsDocument.IsCapture(a.Length > 2 ? a[2] : null));
                return JsValue.Undefined;
            }));

            // Sizes rather than a full screen object: a page asks for these to decide a layout, and everything else
            // a browser hangs off window here would be a number this renderer cannot honestly answer. Accessors
            // rather than values, because the phone turns and the numbers swap while the page is running.
            global.FastSetProperty("innerWidth", new GetSetPropertyDescriptor(
                new ClrFunction(Engine, "innerWidth", (_, _) => Viewport()[0]), null, false, true));
            global.FastSetProperty("innerHeight", new GetSetPropertyDescriptor(
                new ClrFunction(Engine, "innerHeight", (_, _) => Viewport()[1]), null, false, true));

            // Device pixels per css pixel. Always 1: the renderer measures in css pixels and the phone's own scale is
            // applied after layout, so a page that divided by this would end up drawing at the wrong size.
            Engine.SetValue("devicePixelRatio", 1);
        }

        private INode Root() => (INode)_document.Body ?? _document.DocumentElement;

        private float[] Viewport() => OnViewportRequested?.Invoke() ?? new[] { 0f, 0f };

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
                    case "warn": Model.Platform.Warning(line); break;
                    case "error": Model.Platform.Error(line); break;
                    default: Model.Platform.Msg(line); break;
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
