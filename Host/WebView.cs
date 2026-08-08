using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Sideload.Bundle;
using Sideload.Css;
using Sideload.Dom;
using Sideload.Input;
using Sideload.Layout;
using Sideload.Paint;
using Sideload.Script;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Sideload.Host
{
    /// <summary>
    /// A mounted app: one web bundle rendered into one RectTransform. This is the whole public surface of the engine
    /// and knows nothing about the phone - the phone adapter is just the first caller. Later hosts (main menu pages,
    /// Hotline panels, screens in the world) mount the same way.
    /// </summary>
    public sealed class WebView
    {
        /// <summary>Short side of the CSS viewport on the phone. The host rect is scaled so that a stylesheet can
        /// always assume a 400px-wide phone, whatever the real panel measures (see decision 5 in
        /// ARCHITECTURE.md).</summary>
        internal const float PhoneShortSide = 400f;

        /// <summary>
        /// What this view's short side is worth in CSS pixels, or 0 for "one CSS pixel is one device unit".
        ///
        /// The phone is fixed at 400 because every app is written for the same panel. A surface mounted somewhere
        /// else has no such agreement: a menu column is narrow and tall, a banner wide and flat, and the two cannot
        /// share one number. So the caller either names the width it designed against - and gets the phone's
        /// contract, a page that scales with the panel - or names nothing and gets device pixels, which is what a
        /// panel already laid out by uGUI wants.
        /// </summary>
        private readonly float _referenceShortSide;

        /// <summary>Every live view, so the mod's update loop can drive their timers and pending rebuilds. A view
        /// whose root has been destroyed drops out on the next tick.</summary>
        private static readonly List<WebView> _live = new List<WebView>();

        private readonly AppBundle _bundle;
        private readonly RectTransform _host;
        private readonly RectTransform _root;
        private readonly string _appId;

        private bool _built;
        private bool _rebuildQueued;
        private bool _resizeQueued;

        /// <summary>Build on the first frame the panel is up, and follow it when it changes shape. Set for a mounted
        /// surface, never for a phone app - the phone builds on open and re-measures on turn, both on purpose.</summary>
        internal bool AutoBuild;

        /// <summary>The host rect as it was last measured, so <see cref="AutoBuild"/> can tell a real change of shape
        /// from the same rect read again.</summary>
        private Vector2 _hostWas;

#if DEBUG
        private HotReload _watcher;
#endif

        private IDocument _document;
        private Stylesheet _sheet;
        private StyleContext _context;
        private Interaction _interaction;
        private ScriptHost _script;
        private Dictionary<IElement, Painter.PaintedBox> _painted;
        private Dictionary<IElement, RectTransform> _survivors;
        private LayoutNode _tree;
        private IElement _focused;
        private IElement _pinToEnd;

        /// <summary>A focus asked for before the field was painted, granted by the next render. See
        /// <see cref="Focus"/>.</summary>
        private IElement _focusWanted;

        /// <summary>The painted field carrying <c>data-typing</c>, or null. See <see cref="HoldTyping"/>.</summary>
        private IElement _typingHome;

        /// <summary>
        /// Whether the page is in front of the player, as opposed to merely being built and active.
        ///
        /// Supplied by whatever mounted the view, because only that knows: an app can be OPEN on a phone that is in
        /// the player's pocket, and the root object stays active throughout. <see cref="HoldTyping"/> is the one rule
        /// that must not run in that state - taking the keyboard with the phone away leaves a player who cannot move
        /// and a game that ignores every key. Null means the caller has no better answer than the active check the
        /// tick already made, which is correct for a view mounted outside the phone.
        /// </summary>
        internal Func<bool> IsVisible;

        /// <summary>The style each interactive box was last painted with, so a transition knows where it is coming
        /// from. Only elements that actually change state ever land in here.</summary>
        private readonly Dictionary<IElement, ComputedStyle> _styleWas = new();

        /// <summary>This view's form controls. Per view, not process-wide: two apps are mounted at once and each has
        /// its own document, so a shared map would hand one app's field to the other's script.</summary>
        private readonly Dictionary<IElement, Il2CppTMPro.TMP_InputField> _inputs = new();

        /// <summary>Keys each field declared through `data-keys`, parsed and resolved once per render. Empty for a
        /// page that asked for none, which is every page written before the keyboard channel existed.</summary>
        private readonly Dictionary<IElement, Input.Keys.Bound[]> _inputKeys = new();

        /// <summary>Field instance ids this view last published to the caret guard, so a rebuild replaces its own
        /// entries instead of leaving one behind for every field it ever painted.</summary>
        private readonly List<int> _publishedKeys = new();

        private readonly Input.Keys _keyboard = new();

        // Live numbers for the dev overlay. Cheap to keep and the only way to see, from inside the game, whether a
        // page is rebuilding once per change or once per frame.
        private int _renders;
        private int _reloads;
        private float _lastRenderMs;
        private int _boxes;

        private WebView(RectTransform host, RectTransform root, AppBundle bundle, string appId, float referenceShortSide)
        {
            _host = host;
            _root = root;
            _bundle = bundle;
            _appId = appId;
            _referenceShortSide = referenceShortSide;
        }

        /// <summary>Device units per CSS pixel for the rect as it stands now. One when the view maps 1:1.</summary>
        private float ScaleFor(float hostW, float hostH) =>
            _referenceShortSide > 0f ? Math.Min(hostW, hostH) / _referenceShortSide : 1f;

        /// <summary>
        /// Whether this view's colours have to be pre-converted to linear - see
        /// <see cref="Paint.BoxRenderer.ConvertToLinear"/> for what that costs when it is wrong.
        ///
        /// Decided from the canvas rather than asked of the caller: a mod mounting a panel has no way to know which
        /// answer its canvas needs, and getting it wrong is invisible until somebody looks at a dark surface.
        /// A camera-drawn canvas (the phone's, and any world-space panel) is converted back downstream; an overlay
        /// canvas is composited straight into the finished frame and is not.
        /// </summary>
        private bool WantsLinearColors()
        {
            try
            {
                Canvas canvas = _root != null ? _root.GetComponentInParent<Canvas>() : null;
                if (canvas == null) return true;
                return canvas.renderMode != RenderMode.ScreenSpaceOverlay;
            }
            catch { return true; }
        }

        /// <summary>The node everything of this view lives under. Destroying it disposes the view.</summary>
        public RectTransform Root => _root;

        /// <summary>Every live view, for the dev overlay.</summary>
        internal static IReadOnlyList<WebView> Live => _live;

        internal string AppId => _appId;

        // Read by the Snitch panel. Exposed as plain numbers rather than as a formatted string so the host can graph
        // them over time, which is what turns "renders 13" into "this page rebuilds every frame".
        internal int BoxCount => _boxes;

        internal int RenderCount => _renders;

        internal int ReloadCount => _reloads;

        internal float LastRenderMs => _lastRenderMs;

        /// <summary>Ablation lever: stop rendering entirely, so what is left of the frame time is the game's.</summary>
        internal static bool RenderingDisabled;

        /// <summary>A few lines of live state, for the dev overlay.</summary>
        internal string Stats =>
            $"{_appId}  {_root.sizeDelta.x:0}x{_root.sizeDelta.y:0}css @{_root.localScale.x:0.00}x\n" +
            $"  {_boxes} boxes, {_sheet?.Rules.Count ?? 0} rules, {_painted?.Count ?? 0} wired\n" +
            $"  renders {_renders} ({_lastRenderMs:0.0} ms)   reloads {_reloads}\n" +
            $"  script: {(_script == null ? "none" : _script.Failed ? "FAILED - " + _script.LastError : "ok")}";

        internal static WebView Mount(RectTransform host, AppBundle bundle, string appId,
                                      float referenceShortSide = PhoneShortSide)
        {
            if (host == null)
            {
                Core.Log?.Error("[Sideload] mount target is null - nothing will render.");
                return null;
            }

            RectTransform root = UiFactory.Rect("sideload-view", host);
            var view = new WebView(host, root, bundle, appId, referenceShortSide);
            _live.Add(view);
            return view;
        }

        /// <summary>Take this view down now rather than waiting for its rect to be destroyed. A surface that
        /// outlives its panel is the caller's to end; the phone never needs this because its panel dies with the
        /// scene.</summary>
        internal void Dispose()
        {
            // Before anything is destroyed. A focused TMP_InputField holds GameInput.IsTyping, and a field only
            // fires onDeselect when something else takes the selection - which nothing does when its container is
            // simply destroyed. The flag then stays raised for the rest of the session, and a raised flag means the
            // player cannot move and Escape does nothing. The phone has always done this on the way out
            // (Phone/PhoneScreen.Lower); a surface never did, so closing one after typing in it was a one-way trap.
            if (_focused != null) Paint.Painter.ReleaseKeyboard();

            _script?.Dispose();
            _script = null;
            foreach (int id in _publishedKeys) Input.Keys.Withdraw(id);
            _publishedKeys.Clear();
            _live.Remove(this);
            if (_root != null) Object.Destroy(_root.gameObject);
        }

        /// <summary>Drive every live view one frame: script timers first, then any rebuild those timers asked for.</summary>
        internal static void TickAll(float deltaSeconds)
        {
            Input.SmoothScroll.Advance(deltaSeconds);

            for (int i = _live.Count - 1; i >= 0; i--)
            {
                WebView view = _live[i];
                if (view._root == null)
                {
                    // The panel is gone, so this view never renders again - let go of the engine and, with it, this
                    // page's subscription to host events. Same keyboard rule as Dispose: a page that had the caret
                    // when its panel vanished would otherwise leave the game's typing flag raised for good.
                    if (view._focused != null) Paint.Painter.ReleaseKeyboard();
                    view._script?.Dispose();
                    view._script = null;
                    foreach (int id in view._publishedKeys) Input.Keys.Withdraw(id);
                    view._publishedKeys.Clear();
                    _live.RemoveAt(i);
                    continue;
                }

                try { view.Tick(deltaSeconds); }
                catch (Exception e) { Core.Log?.Error("[Sideload] view tick failed: " + e); }
            }
        }

        /// <summary>
        /// Build the page if it has not been built yet. Deferred on purpose: a panel that has never been shown has no
        /// laid-out rect, so measuring at mount time would read zeroes.
        /// </summary>
        /// <summary>Whether the page has been built. False means the next open pays for it - see Phone/AppFade.cs.</summary>
        internal bool Built => _built;

        internal void EnsureBuilt()
        {
            if (_built) return;

            // Same reason the tick refuses to rebuild off screen: the text probe is a TextMeshPro under this root and
            // it measures nonsense until Awake has run, which needs an active object. The caller opens the app first,
            // so this only bites when something else asks for a build early.
            if (_root == null || !_root.gameObject.activeInHierarchy)
            {
                Core.Log?.Warning($"[Sideload] {_appId}: asked to build while off screen - deferred until it is shown.");
                return;
            }

            _built = true;

            // Timed because this is the one call that can stall a frame: everything a page costs - parsing, the
            // cascade, the script, the first layout and every uGUI object it creates - happens here, on the frame
            // the player opened the app.
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            try { Build(); }
            catch (Exception e)
            {
                Core.Log?.Error("[Sideload] building the page failed: " + e);
                ShowError(e.Message);
            }

            Core.Log?.Msg($"[Sideload] {_appId}: first build took {watch.ElapsedMilliseconds} ms.");
        }

        private void Tick(float deltaSeconds)
        {
            if (RenderingDisabled) return;

#if DEBUG
            if (_watcher != null && _watcher.ShouldReload(deltaSeconds)) { Reload(); return; }
#endif

            Transitions.Tick(deltaSeconds);
            _script?.Tick(deltaSeconds);

            // Nothing is laid out while the app is off screen, and the pending flags are deliberately LEFT SET so it
            // happens the moment it is shown again.
            //
            // Not an optimisation. Text is measured by a TextMeshPro probe created under this root, and TMP sets
            // itself up in Awake, which never runs on an inactive object - so a page rebuilt while its panel is
            // hidden measures every line about ten times too short and comes back with one character per line. A
            // chat app hits this immediately, because messages arrive while the phone is in the player's pocket.
            if (!_root.gameObject.activeInHierarchy) return;

            // A surface has no icon to press, so nothing else would ever build it, and nothing else watches its panel
            // for a change of shape either. The phone opts out of both: it builds when the player opens the app, and
            // it re-measures when the player turns it.
            if (AutoBuild)
            {
                if (!_built) { EnsureBuilt(); return; }
                Rect now = _host.rect;
                if (!Mathf.Approximately(now.width, _hostWas.x) || !Mathf.Approximately(now.height, _hostWas.y))
                {
                    _hostWas = new Vector2(now.width, now.height);
                    QueueResize();
                }
            }

            // Before the rebuild, not after: a key that changes the DOM should show up in THIS frame's render rather
            // than waiting a frame, which is the difference between a list that walks with the arrows and one that
            // lags one press behind.
            TickKeyboard();
            HoldTyping();

            // A resize lays the page out too, so it subsumes whatever rebuild was pending.
            if (_resizeQueued)
            {
                _resizeQueued = false;
                _rebuildQueued = false;
                Resize();
                return;
            }

            if (!_rebuildQueued) return;
            _rebuildQueued = false;
            Rebuild();
        }

        /// <summary>
        /// Throw the whole page away and build it again from disk - what a file in the override folder changing
        /// means, and what `Page.reload` means to an attached inspector. Unlike a rebuild this drops the script
        /// engine too, because the script itself may be what changed - carrying its state over would leave listeners
        /// from the previous version attached.
        /// </summary>
        internal void Reload()
        {
            Core.Log?.Msg("[Sideload] reloading the page from disk.");

            // Images are cached for the session, so without this an edited PNG keeps drawing the old one.
            Paint.ImageCache.Forget(_appId);

            for (int i = _root.childCount - 1; i >= 0; i--) Object.Destroy(_root.GetChild(i).gameObject);

            _script?.Dispose();

            _built = false;
            _reloads++;
            _rebuildQueued = false;
            _script = null;
            _painted = null;
            _focused = null;

            EnsureBuilt();
        }

        // --------------------------------------------------------- devtools protocol --
        //
        // The smallest surface the CDP server needs: read the page, mark it dirty, reload it. Not Debug-only,
        // because the server itself ships in every build and is gated by a preference instead (Config.Preferences).

        /// <summary>The parsed page. Null until the view has been built.</summary>
        internal IDocument Document => _document;

        /// <summary>The page's script engine, for evaluating in its own context. Null until the view has been
        /// built.</summary>
        internal ScriptHost Script => _script;

        /// <summary>The parsed stylesheet the cascade runs against, so the inspector can show which rules matched.
        /// Null until the view has been built.</summary>
        internal Stylesheet Sheet => _sheet;

        /// <summary>Orientation and live interaction state, the two inputs the cascade needs besides the sheet. The
        /// inspector has to resolve against the same context or it would report a style the page is not wearing.</summary>
        internal StyleContext StyleContext => _context;

        /// <summary>The document was changed from outside the script. Queues the same deferred re-render a script
        /// mutation does, so a burst of edits costs one rebuild.</summary>
        internal void MarkDirty() => QueueRebuild();

        /// <summary>
        /// The host rect is about to change shape. Deferred to the next tick rather than applied on the spot, because
        /// the caller may be a script running inside the very build that is about to render: re-laying out underneath
        /// it would render twice and leave the second pass measuring a viewport from before the change.
        /// </summary>
        internal void QueueResize() => _resizeQueued = true;

        /// <summary>
        /// The host rect changed shape - re-measure the viewport, flip the orientation the cascade sees, and lay the
        /// page out again. Deliberately not a reload: the document, the script and everything the page was showing
        /// survive a rotation, which is the difference between turning a phone and restarting an app.
        /// </summary>
        private void Resize()
        {
            if (!_built || _root == null || _document == null || _context == null) return;

            Rect hostRect = _host.rect;
            float hostW = hostRect.width, hostH = hostRect.height;
            if (hostW < 1f || hostH < 1f) return;

            float scale = ScaleFor(hostW, hostH);
            float cssW = hostW / scale, cssH = hostH / scale;

            if (Mathf.Approximately(cssW, _root.sizeDelta.x) && Mathf.Approximately(cssH, _root.sizeDelta.y)) return;

            Orientation was = _context.Orientation;

            _root.sizeDelta = new Vector2(cssW, cssH);
            _root.localScale = new Vector3(scale, scale, 1f);
            _hostWas = new Vector2(hostW, hostH);
            _context.Orientation = cssW >= cssH ? Orientation.Landscape : Orientation.Portrait;

            Rebuild();
            Core.Log?.Msg($"[Sideload] {_appId}: viewport is now {cssW:0.#}x{cssH:0.#} css px ({_context.Orientation}).");

            // After the layout, not before: a handler that changes the document then gets one more rebuild on the
            // next tick rather than being rendered away by this one.
            //
            // The event exists because `@media` can only move boxes. A page whose SHAPE changes with the orientation -
            // two panes side by side becoming one pane at a time - also has state to decide, and it cannot decide it
            // from a stylesheet: which of the two panes the player should land on is a question about what they were
            // just looking at.
            if (was != _context.Orientation)
                _script?.Dispatch(_document?.Body ?? _document?.DocumentElement, "orientationchange",
                                  value: _context.Orientation == Orientation.Portrait ? "portrait" : "landscape");
        }

        private void Build()
        {
            Rect hostRect = _host.rect;
            float hostW = hostRect.width, hostH = hostRect.height;
            if (hostW < 1f || hostH < 1f)
            {
                Core.Log?.Warning($"[Sideload] host rect is {hostW}x{hostH} - the page cannot be sized yet.");
                return;
            }

            // One CSS pixel is `scale` device units, so every phone stylesheet works against the same 400px short
            // side. A surface that named no reference gets scale 1 and therefore device pixels.
            float scale = ScaleFor(hostW, hostH);
            float cssW = hostW / scale, cssH = hostH / scale;

            _root.anchorMin = _root.anchorMax = new Vector2(0.5f, 0.5f);
            _root.pivot = new Vector2(0.5f, 0.5f);
            _root.anchoredPosition = Vector2.zero;
            _root.sizeDelta = new Vector2(cssW, cssH);
            _root.localScale = new Vector3(scale, scale, 1f);
            _hostWas = new Vector2(hostW, hostH);

#if DEBUG
            _watcher ??= HotReload.Start(_bundle?.OverrideRoot, _appId);
#endif

            // Phase timings, because "the first app takes a moment" is not something to guess at: the first page of
            // a session pays for warming AngleSharp and Jint on top of its own work, and only a split shows which.
            var phase = System.Diagnostics.Stopwatch.StartNew();
            long tRead, tParse, tCss, tScript;

            string html = _bundle?.ReadText("index.html");
            if (string.IsNullOrEmpty(html)) { ShowError("index.html not found in bundle or override"); return; }
            tRead = phase.ElapsedMilliseconds;

            _document = new HtmlParser().ParseDocument(html);
            tParse = phase.ElapsedMilliseconds;

            string tooBig = TooLargeToRender(_document);
            if (tooBig != null) { ShowError(tooBig); return; }

            long css = 0, script = 0;

            // One listener across parse, cascade, script and first render - every stage drops something, and a
            // report split over four log lines is a report nobody assembles.
            CollectDiagnostics(() =>
            {
                _sheet = CssParser.Parse(CollectCss(_document));

                // The whole sheet, not only what ends up matching. See SheetAudit for why that distinction is the
                // difference between a useful report and silence on exactly the stylesheets that need one.
                Css.SheetAudit.Scan(_sheet);
                css = phase.ElapsedMilliseconds;

                _interaction = new Interaction(OnStateChanged, OnClicked, OnDragged, OnWheel, OnHover, _root);
                _context = new StyleContext
                {
                    Orientation = cssW >= cssH ? Orientation.Landscape : Orientation.Portrait,
                    StateOf = _interaction.StateOf,

                    // What `vh`, `vw` and friends measure against. The same numbers the page is designed for, so
                    // `height: 100vh` and `height: 100%` on a sized body agree - a stylesheet that uses both must
                    // not get two different answers.
                    ViewportWidth = cssW,
                    ViewportHeight = cssH,

                    // `<meta name="sideload" content="web-defaults">`. Anything built by a web toolchain wants
                    // this: Tailwind writes `.flex` and means a row, because in a browser it never has to say so.
                    // Opt-in and not a new default - every existing app is written against the column, and
                    // flipping it under them would reflow every box they did not think to declare.
                    WebDefaults = WantsWebDefaults(_document),
                };

                // The script runs BEFORE the first render, not after: it may build half the page and it registers
                // the click listeners that decide which boxes need a hit target. Rendering first would either miss
                // those or force a second full pass one frame later.
                _script = new ScriptHost(_appId, _document, QueueRebuild, Focus, PinToEnd, RepaintOnly, RectOf, Blur);
                RunScripts(_document);
                script = phase.ElapsedMilliseconds;

                Render(cssW, cssH, hostW, hostH, scale);
            });

            tCss = css;
            tScript = script;

            // Per-build reporting is off unless a developer asks for it: an app that redraws on a timer rebuilds once a
            // second, and these two lines plus the wiring and scroll-area lines then fill the log of every player
            // running that app. Config.Preferences.LogPageBuilds turns them all on together.
            if (Config.Preferences.LogPageBuilds)
                Core.Log?.Msg($"[Sideload] {_appId}: read {tRead} ms, html {tParse - tRead} ms, css {tCss - tParse} ms, "
                              + $"script {tScript - tCss} ms, render {phase.ElapsedMilliseconds - tScript} ms "
                              + $"= {phase.ElapsedMilliseconds} ms.");
            _rebuildQueued = false;   // the render above already covers whatever the script just changed

            if (Config.Preferences.LogPageBuilds)
                Core.Log?.Msg($"[Sideload] page built: viewport {cssW:0.#}x{cssH:0.#} css px at {scale:0.###}x, " +
                              $"{_sheet.Rules.Count} rule(s).");
        }

        /// <summary>Elements a page may contain. Generous - the largest app here is under a hundred - but finite.</summary>
        private const int MaxElements = 20000;

        /// <summary>
        /// How deeply a page may nest. Styling, tree building and painting are each RECURSIVE, so nesting is bounded
        /// by the managed stack rather than by memory - and blowing that stack takes the process down before any
        /// error page can be shown. A page has to be refused before it is walked, not while.
        /// </summary>
        private const int MaxDepth = 200;

        /// <summary>Null when the document is fine, otherwise the message to show instead of rendering it.</summary>
        private static string TooLargeToRender(IDocument document)
        {
            IElement root = document?.Body ?? document?.DocumentElement;
            if (root == null) return null;

            int count = 0;

            // Iterative on purpose: a recursive check would hit the very stack limit it exists to protect.
            var stack = new Stack<(IElement Element, int Depth)>();
            stack.Push((root, 1));

            while (stack.Count > 0)
            {
                (IElement element, int depth) = stack.Pop();

                if (++count > MaxElements)
                    return $"the page has more than {MaxElements} elements - refusing to render it";

                if (depth > MaxDepth)
                    return $"the page nests deeper than {MaxDepth} elements - refusing to render it";

                foreach (IElement child in element.Children) stack.Push((child, depth + 1));
            }

            return null;
        }

        /// <summary>
        /// Style, lay out, paint. Split out of <see cref="Build"/> because a script mutation has to redo exactly this
        /// and nothing else - the document, the stylesheet and the engine all survive.
        /// </summary>
        private void Render(float cssW, float cssH, float hostW, float hostH, float scale)
        {
            // Tell the painter what one css pixel is worth in device pixels, so a hairline border can be snapped to
            // a whole one instead of being smeared across two.
            Paint.Painter.CssToDevice = scale;
            Paint.BoxRenderer.ConvertToLinear = WantsLinearColors();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            _interaction.ResetForRender(_document);
            Transitions.Clear();
            _styleWas.Clear();

            Dictionary<IElement, ComputedStyle> styles = StyleResolver.Resolve(_document, _sheet, _context);

            IElement body = _document.Body ?? _document.DocumentElement;
            LayoutNode tree = DomBuilder.Build(body, styles);
            if (tree == null) { ShowError("the document has no renderable content"); return; }

            var measure = new TmpMeasure(_root);
            FlexLayout.Compute(tree, cssW, cssH, measure);

            _painted = Painter.Paint(tree, _root, new Vector2(cssW, cssH),
                                     _inputs, OnInputChanged, OnInputSubmitted, _survivors, _bundle, _appId);
            _survivors = null;

            PublishDeclaredKeys();
            WireInteraction(styles);
            ApplyPin();
            ApplyPendingFocus();

            foreach (KeyValuePair<IElement, Painter.PaintedBox> pair in _painted)
                if (styles.TryGetValue(pair.Key, out ComputedStyle painted)) _styleWas[pair.Key] = painted;

            _tree = tree;
            _renders++;
            _boxes = CountNodes(tree);
            _lastRenderMs = (System.Diagnostics.Stopwatch.GetTimestamp() - started)
                            * 1000f / System.Diagnostics.Stopwatch.Frequency;

#if DEBUG
            Devtools.LayoutOverlay.Dump(tree, cssW, cssH, hostW, hostH, scale);
#endif
        }

        /// <summary>
        /// The script changed the document. Re-rendering is deferred to the next tick rather than done on the spot so
        /// that a handler touching twenty nodes rebuilds the page once, not twenty times.
        /// </summary>
        private void QueueRebuild() => _rebuildQueued = true;

        /// <summary>
        /// Throw the uGUI objects away and build them again from the mutated document. Two things must survive that,
        /// because losing either one is immediately obvious to the player: where each scrollable box was scrolled to,
        /// and which field had the caret.
        /// </summary>
        private void Rebuild()
        {
            if (_root == null || _document == null) return;

            try
            {
                Dictionary<IElement, ScrollMark> scroll = CaptureScroll();
                _survivors = RescueControls();

                for (int i = _root.childCount - 1; i >= 0; i--)
                {
                    GameObject child = _root.GetChild(i).gameObject;
                    if (!IsRescued(child)) Object.Destroy(child);
                }

                // Under the listener as well, not only the first build. A page sets most of its styles from script,
                // and an inline write never passes the stylesheet scan - so without this the most common way to
                // write CSS in this engine would be the one way that never gets checked.
                CollectDiagnostics(() =>
                {
                    float scale = _root.localScale.x;
                    Render(_root.sizeDelta.x, _root.sizeDelta.y, _root.sizeDelta.x * scale, _root.sizeDelta.y * scale, scale);
                });

                RestoreScroll(scroll);
            }
            catch (Exception e)
            {
                Core.Log?.Error("[Sideload] rebuilding the page failed: " + e);
            }
        }

        /// <summary>
        /// Lift every form control out of the doomed hierarchy and park it directly under the view root, so the
        /// wholesale destroy below cannot take it with it. Whichever ones the new tree still wants get slotted back
        /// into place; anything left over is cleaned up afterwards.
        /// </summary>
        private Dictionary<IElement, RectTransform> RescueControls()
        {
            var rescued = new Dictionary<IElement, RectTransform>();
            if (_painted == null) return rescued;

            foreach (KeyValuePair<IElement, Painter.PaintedBox> pair in _painted)
            {
                if (!_inputs.ContainsKey(pair.Key) || pair.Value.Rect == null) continue;

                pair.Value.Rect.SetParent(_root, worldPositionStays: false);
                rescued[pair.Key] = pair.Value.Rect;
            }
            return rescued;
        }

        private bool IsRescued(GameObject candidate)
        {
            if (_survivors == null) return false;

            foreach (KeyValuePair<IElement, RectTransform> pair in _survivors)
                if (pair.Value != null && pair.Value.gameObject == candidate) return true;
            return false;
        }

        /// <summary>Where a scroll area stood, and how tall its content was at the time. The height is what makes the
        /// offset mean anything: the position Unity stores is NORMALISED, so 0.4 of a short list and 0.4 of a long one
        /// are different places entirely.</summary>
        private struct ScrollMark
        {
            internal float Position;
            internal float ContentHeight;
        }

        private Dictionary<IElement, ScrollMark> CaptureScroll()
        {
            var offsets = new Dictionary<IElement, ScrollMark>();
            if (_painted == null) return offsets;

            foreach (KeyValuePair<IElement, Painter.PaintedBox> pair in _painted)
            {
                if (pair.Value.Rect == null) continue;

                var scroll = pair.Value.Rect.GetComponentInChildren<ScrollRect>();
                if (scroll == null) continue;
                offsets[pair.Key] = new ScrollMark
                {
                    Position = scroll.verticalNormalizedPosition,
                    ContentHeight = scroll.content != null ? scroll.content.rect.height : 0f,
                };
            }
            return offsets;
        }

        /// <summary>
        /// Put every scroll area back where the player left it - but only where that still means something.
        ///
        /// Keeping the offset is what stops a page that rebuilds on every DOM write from yanking a reader back to the
        /// top. It stops being a kindness the moment the content is not the same content: a pane that swapped its
        /// whole subtree (an empty state replaced by a full one, a tab switch) opens somewhere in its own middle, and
        /// the player sees a screen that starts halfway through itself. Unity stores the position normalised, so
        /// there is no way to notice that from the number alone - hence the remembered height.
        /// </summary>
        private void RestoreScroll(Dictionary<IElement, ScrollMark> offsets)
        {
            if (offsets == null || _painted == null) return;

            foreach (KeyValuePair<IElement, ScrollMark> pair in offsets)
            {
                if (!_painted.TryGetValue(pair.Key, out Painter.PaintedBox box) || box.Rect == null) continue;

                var scroll = box.Rect.GetComponentInChildren<ScrollRect>();
                if (scroll == null) continue;

                // A box the script pinned to its end wins over where it happened to be scrolled a moment ago.
                if (ReferenceEquals(pair.Key, _pinToEnd)) { scroll.verticalNormalizedPosition = 0f; continue; }

                float was = pair.Value.ContentHeight;
                float now = scroll.content != null ? scroll.content.rect.height : 0f;
                // A quarter of the height, floored at 24px so a short list is not judged by a percentage. Growing by
                // a message or a row stays inside it; replacing the contents does not.
                bool comparable = was > 0f && Mathf.Abs(now - was) <= Mathf.Max(24f, was * 0.25f);
                // 1 is the TOP for a normalised vertical position (0 is the bottom, which is why the pin above uses it).
                scroll.verticalNormalizedPosition = comparable ? pair.Value.Position : 1f;
            }
        }

        /// <summary>A box asked to sit at its end but had no scroll area last time round - a chat that just grew past
        /// its box is exactly that case, so it has to be handled outside the restore path too.</summary>
        private void ApplyPin()
        {
            if (_pinToEnd == null || _painted == null) return;

            if (_painted.TryGetValue(_pinToEnd, out Painter.PaintedBox box) && box.Rect != null)
            {
                var scroll = box.Rect.GetComponentInChildren<ScrollRect>();
                if (scroll != null) scroll.verticalNormalizedPosition = 0f;
            }

            _pinToEnd = null;
        }

        /// <summary>
        /// Pin interaction states on an element, for the Styles pane's `:hov` toggles. Names are the CSS pseudo-class
        /// spellings DevTools sends (`hover`, `active`, `focus`); an empty list clears them.
        /// </summary>
        internal void ForcePseudoState(IElement element, IEnumerable<string> pseudoClasses)
        {
            if (element == null || _interaction == null) return;

            StateFlags flags = StateFlags.None;
            foreach (string name in pseudoClasses ?? Array.Empty<string>())
            {
                switch ((name ?? "").Trim().ToLowerInvariant())
                {
                    case "hover": flags |= StateFlags.Hover; break;
                    case "active": flags |= StateFlags.Active; break;
                    case "focus" or "focus-visible" or "focus-within": flags |= StateFlags.Focus; break;
                }
            }

            _interaction.Force(element, flags);
        }

        /// <summary>Remember that a box wants to sit at its end; applied by the next render, which is when the
        /// ScrollRect that will actually hold the content exists.</summary>
        private void PinToEnd(IElement element) => _pinToEnd = element;

        /// <summary>
        /// Read every painted field's `data-keys` and hand the result to the caret guard, which is reached from a
        /// Harmony prefix that knows only the TMP_InputField and so cannot ask this view anything.
        ///
        /// Parsing here rather than per frame is what keeps a malformed declaration from writing the same warning
        /// sixty times a second, and what keeps the poll allocation-free.
        ///
        /// Fields that survive a rebuild keep their instance id, so re-publishing the same id is a no-op rather than
        /// a leak - which is exactly what should happen to a field the player is currently typing in.
        /// </summary>
        private void PublishDeclaredKeys()
        {
            foreach (int id in _publishedKeys) Input.Keys.Withdraw(id);
            _publishedKeys.Clear();
            _inputKeys.Clear();
            _typingHome = null;

            foreach (KeyValuePair<IElement, Il2CppTMPro.TMP_InputField> pair in _inputs)
            {
                if (pair.Value == null) continue;

                // Recorded here rather than looked up per frame, and only for a field that was actually PAINTED -
                // which is what makes the attribute mean "while this box is on screen". A pane hidden with
                // `display: none` paints nothing, so an app whose compose box is in the hidden half of a portrait
                // layout does not silently hold the keyboard from behind it.
                if (_typingHome == null && pair.Key.HasAttribute("data-typing")) _typingHome = pair.Key;

                Model.KeyDeclarationSet keys = Model.KeyDeclarationSet.Parse(pair.Key.GetAttribute("data-keys"));

                foreach (string refusal in keys.Refused)
                    Core.Log?.Warning($"[Sideload] {_appId}: data-keys ignored {refusal}");

                if (keys.Count == 0) continue;

                int id = pair.Value.GetInstanceID();
                Input.Keys.Bound[] bound = Input.Keys.Publish(id, keys);
                if (bound.Length == 0) continue;

                _inputKeys[pair.Key] = bound;
                _publishedKeys.Add(id);

                // Unity's selection navigation would answer some of these keys before the page ever sees them, and a
                // page built fresh every render has no meaningful tab order anyway. Only touched on fields that
                // declared keys, so no existing page changes behaviour. Reached through the field rather than through
                // a Selectable cast: a managed cast on an interop wrapper is the one that quietly returns null.
                UnityEngine.UI.Navigation navigation = pair.Value.navigation;
                navigation.mode = UnityEngine.UI.Navigation.Mode.None;
                pair.Value.navigation = navigation;
            }
        }

        /// <summary>
        /// One frame of the keyboard for whichever field has the caret. Silent and nearly free when the page declared
        /// no keys, which is the normal case.
        /// </summary>
        private void TickKeyboard()
        {
            if (_focused == null || _script == null || _inputKeys.Count == 0) return;
            if (!_inputKeys.TryGetValue(_focused, out Input.Keys.Bound[] keys)) return;
            if (!_inputs.TryGetValue(_focused, out Il2CppTMPro.TMP_InputField field)) return;

            if (!_keyboard.Tick(field, keys, out Model.KeyDeclaration fired, out bool repeat)) return;

            // The field acted on this press too - Sideload polls the keyboard rather than intercepting it - so the
            // page is told whether there was a selection. Ctrl+C is the case that needs it: TMP has already copied.
            bool selected = field.selectionAnchorPosition != field.selectionFocusPosition;

            _script.Dispatch(_focused, "keydown", field.text ?? "", fired.Name,
                             ctrl: fired.Ctrl, shift: fired.Shift, alt: fired.Alt, repeat: repeat,
                             hasSelection: selected);
        }

        /// <summary>
        /// Keep the caret in the field the page marked <c>data-typing</c>, for as long as that field is on screen.
        ///
        /// The problem it solves is not focus, it is what the other keys do. A chat with the caret NOT in its message
        /// box is a chat where typing "hello" walks the player forward, crouches them, and swaps two inventory slots -
        /// because a field only holds <c>GameInput.IsTyping</c> while it has the caret, and everything else on the
        /// keyboard is a game binding. Somebody looking at a conversation obviously means to write in it, so the box
        /// takes the keyboard and keeps it.
        ///
        /// Three conditions, and each is load-bearing:
        ///
        /// <list type="bullet">
        /// <item><b>Only while the page is really visible.</b> An app stays open on a phone that has gone back in the
        /// player's pocket, and grabbing the keyboard there is the bug that leaves someone unable to move with no way
        /// out - Escape does nothing either, because the game ignores it while typing.</item>
        /// <item><b>Only when nothing at all is selected.</b> A player who clicked the search box is typing in the
        /// search box; taking the caret back would make that box unusable. Clicking something that is NOT a field - a
        /// row, a list, a button - selects nothing, and that is the press that brings the keyboard home again.</item>
        /// <item><b>Only a field that was painted.</b> Hidden panes paint nothing, so the compose box of a portrait
        /// layout showing its thread list does not hold the keyboard from behind the pane the player can see.</item>
        /// </list>
        ///
        /// Per frame rather than per render, because the click that loses the caret often changes nothing in the DOM
        /// and so renders nothing.
        /// </summary>
        private void HoldTyping()
        {
            if (_typingHome == null) return;

            // Gone from in front of the player while still holding the caret - the phone switched to its character
            // tab, which neither closes the app nor releases the keyboard. Left alone, the player is typing into a
            // box that is not on screen: they cannot move, and Escape does nothing because the game stops delivering
            // it while IsTyping. Only ever OUR field, and only one this page put the caret in.
            if (IsVisible != null && !IsVisible())
            {
                if (HoldsKeyboard) Paint.Painter.ReleaseKeyboard();
                return;
            }

            if (!_inputs.TryGetValue(_typingHome, out Il2CppTMPro.TMP_InputField home) || home == null) return;
            if (home.isFocused) return;

            // ONLY when nothing at all is selected. Not "when no field is focused": TextMeshPro activates a field one
            // frame AFTER the EventSystem selects it, so a player clicking the search box spends a frame selected but
            // not yet focused - and a rule that only looked at focus would take the caret back inside that frame and
            // the box would never come alive. Anything else selected keeps the keyboard, whether it is another of
            // this page's controls or a screen of the game's own.
            UnityEngine.EventSystems.EventSystem events = UnityEngine.EventSystems.EventSystem.current;
            GameObject selected = events != null ? events.currentSelectedGameObject : null;
            if (selected != null && selected != home.gameObject) return;

            Focus(_typingHome);
        }

        /// <summary>
        /// Whether a field of this page currently has the caret, and so is the reason the game believes the player is
        /// typing. Read from TextMeshPro rather than from <c>_focused</c>: that one is this view's memory of where it
        /// last put the caret, and TMP is where the caret actually is.
        /// </summary>
        internal bool HoldsKeyboard
        {
            get
            {
                foreach (KeyValuePair<IElement, Il2CppTMPro.TMP_InputField> pair in _inputs)
                    if (pair.Value != null && pair.Value.isFocused) return true;

                return false;
            }
        }

#if DEBUG
        /// <summary>
        /// Which field of this page has the caret, named the way the page names it, or null when none does.
        ///
        /// Only a debug aid, and it earns its place: "is the keyboard where it should be" is otherwise a question
        /// answered by looking for a blinking caret in a screenshot, which is half wrong half the time because the
        /// caret is invisible on the off beat. Reported by the sideloadkeys console command.
        /// </summary>
        internal string FocusedFieldName
        {
            get
            {
                foreach (KeyValuePair<IElement, Il2CppTMPro.TMP_InputField> pair in _inputs)
                {
                    if (pair.Value == null || !pair.Value.isFocused) continue;

                    string id = pair.Key.GetAttribute("id");
                    return string.IsNullOrEmpty(id) ? "<" + pair.Key.LocalName + ">" : "#" + id;
                }

                return null;
            }
        }

        /// <summary>The field this page marked <c>data-typing</c>, or null - so the console command can say whether a
        /// page that looks wrong ever declared a keyboard home in the first place.</summary>
        internal string TypingHomeName
        {
            get
            {
                if (_typingHome == null) return null;

                string id = _typingHome.GetAttribute("id");
                return string.IsNullOrEmpty(id) ? "<" + _typingHome.LocalName + ">" : "#" + id;
            }
        }
#endif

        /// <summary>
        /// Whether this page is the keyboard's owner, or would be the moment nobody else wanted it: it declared a
        /// <c>data-typing</c> field that is painted and on screen, and no control OUTSIDE this page holds the caret.
        ///
        /// The second half is what stops the rule reaching past its own app. The game's console selects its input
        /// field when it opens, and it can be opened over the phone; without this check the compose box would take
        /// the caret straight back off it and the player could not type a command. The same goes for the rename
        /// dialog and every other vanilla screen that selects a field.
        ///
        /// Asked through the EventSystem rather than by counting focused fields, because "nothing at all is selected"
        /// is the case that matters and only the EventSystem can answer it. Clicking a row, a message list or a
        /// button deselects whatever had the caret without selecting anything new, which is exactly when the page
        /// should get it back.
        /// </summary>
        internal bool OwnsTyping
        {
            get
            {
                if (_typingHome == null) return false;
                if (IsVisible != null && !IsVisible()) return false;

                UnityEngine.EventSystems.EventSystem events = UnityEngine.EventSystems.EventSystem.current;
                GameObject selected = events != null ? events.currentSelectedGameObject : null;
                if (selected == null) return true;

                foreach (KeyValuePair<IElement, Il2CppTMPro.TMP_InputField> pair in _inputs)
                    if (pair.Value != null && pair.Value.gameObject == selected) return true;

                return false;
            }
        }

        /// <summary>
        /// Put the caret in a field, from script (`el.focus()`) or after a rebuild.
        ///
        /// A request for a field that has not been painted yet is REMEMBERED rather than dropped. Scripts run before
        /// the first render on purpose - the listeners they register decide which boxes need a hit target - so a page
        /// that focuses its prompt in its startup code is asking for a field that does not exist yet. Silently doing
        /// nothing there is how an app ends up opening with a prompt nobody can type into.
        /// </summary>
        private void Focus(IElement element)
        {
            if (element == null) return;

            if (!_inputs.TryGetValue(element, out Il2CppTMPro.TMP_InputField field) || field == null)
            {
                _focusWanted = element;
                return;
            }

            _focused = element;
            field.ActivateInputField();
        }

        /// <summary>
        /// Let the caret go, from script (<c>el.blur()</c>).
        ///
        /// The counterpart to <see cref="Focus"/>, and not decoration: while a field holds the caret the game's
        /// own exit handling returns on its first line, so a page that takes focus owes the player a way to give
        /// it back. Only ever releases a field THIS page put the caret in.
        /// </summary>
        private void Blur(IElement element)
        {
            _focusWanted = null;
            if (element != null && _focused != null && element != _focused) return;
            if (_focused == null) return;

            _focused = null;
            Paint.Painter.ReleaseKeyboard();
        }

        /// <summary>
        /// Put the caret back where it was when the page was last on screen.
        ///
        /// Closing an app releases the keyboard - it has to, or the game is left with no way to move (see
        /// Painter.ReleaseKeyboard). Reopening does not rebuild the page, so nothing in it runs again to ask for
        /// the focus back, and the app comes up with a prompt that looks ready and swallows nothing: every key
        /// goes to the game instead, which starts opening things. Restoring it here means a page never has to
        /// notice it was hidden, whichever way the player opened it.
        /// </summary>
        internal void RestoreFocus()
        {
            if (_focused == null) return;
            Focus(_focused);
        }

        /// <summary>Grant a focus that was asked for before the field existed. Runs after the render that created
        /// it, which is the first moment it can work.</summary>
        private void ApplyPendingFocus()
        {
            if (_focusWanted == null) return;

            IElement wanted = _focusWanted;
            _focusWanted = null;
            Focus(wanted);
        }

        /// <summary>
        /// Give pointer handling to the elements that can react to it: anything a state rule targets, the controls
        /// that are interactive by nature, and anything the script listens to. Everything else stays inert and lets
        /// the pointer through.
        /// </summary>
        private void WireInteraction(Dictionary<IElement, ComputedStyle> styles)
        {
            HashSet<IElement> stateful = StyleResolver.StatefulElements(_document, _sheet, _context.Orientation);

            foreach (IElement control in _document.QuerySelectorAll("button, a, input, textarea"))
                stateful.Add(control);

            // A top-layer box is modal by nature, so it takes the pointer whether or not anything listens to it.
            //
            // Without this an overlay is only a picture: the engine gives a hit target to elements that can react,
            // and a plain backdrop reacts to nothing, so every click sailed straight through to the page underneath -
            // which is the one thing a modal exists to prevent. The blocking is not a special case in the raycast; it
            // falls out of the painter putting these last under the view root, where uGUI meets them first.
            //
            // Scoped to the fixed box ITSELF, not its subtree. A backdrop stretched over the viewport therefore
            // blocks the viewport, and a small toast blocks only the pixels it covers - which is what it should do,
            // there being no `pointer-events: none` here to say otherwise.
            if (styles != null)
                foreach (KeyValuePair<IElement, ComputedStyle> entry in styles)
                    if (entry.Value != null && entry.Value.Position == PositionKind.Fixed)
                        stateful.Add(entry.Key);

            // A script that listens on a plain div still needs that div to receive the pointer.
            var draggable = new HashSet<IElement>();
            var wheeled = new HashSet<IElement>();

            if (_script != null)
            {
                foreach (IElement clickable in _script.ElementsListeningFor("click"))
                    stateful.Add(clickable);

                // Gestures the page takes over from the scroll area it sits in, so they are collected separately -
                // wiring them on everything would stop most lists scrolling.
                foreach (string type in new[] { "dragstart", "drag", "dragend" })
                    foreach (IElement dragged in _script.ElementsListeningFor(type))
                    {
                        stateful.Add(dragged);
                        draggable.Add(dragged);
                    }

                foreach (IElement scrolled in _script.ElementsListeningFor("wheel"))
                {
                    stateful.Add(scrolled);
                    wheeled.Add(scrolled);
                }
            }

            int wired = 0;
            foreach (IElement element in stateful)
            {
                if (!_painted.TryGetValue(element, out Painter.PaintedBox box)) continue;

                bool disabled = element.HasAttribute("disabled");

                // Form controls already own a Selectable; their handlers have to share its GameObject, otherwise a
                // child hit target eats the click before the field can take focus.
                bool isFormControl = element.LocalName.Equals("input", StringComparison.OrdinalIgnoreCase)
                                     || element.LocalName.Equals("textarea", StringComparison.OrdinalIgnoreCase);

                _interaction.Attach(box.Rect, element, disabled, ownGameObject: isFormControl,
                                    draggable: draggable.Contains(element), wheel: wheeled.Contains(element));
                wired++;
            }

            if (Config.Preferences.LogPageBuilds) Core.Log?.Msg($"[Sideload] interaction wired on {wired} element(s).");
        }

        /// <summary>
        /// An element's hover/press state changed: recompute the cascade and repaint just that box. The layout is
        /// deliberately left alone, which is why state rules are a paint-only feature - a `:hover` that changed the
        /// width would need a full reflow and is not supported.
        /// </summary>
        /// <summary>
        /// A script wrote a paint-only inline style. Same work a hover does - recompute the cascade for that element
        /// and repaint its box - which is why it costs nothing next to a rebuild.
        ///
        /// An element with no painted box falls back to a rebuild rather than being dropped: a node the script just
        /// created, or one inside a `display: none` subtree, has nothing to repaint yet, and silently skipping it
        /// would leave the page showing a style it no longer has.
        /// </summary>
        private void RepaintOnly(IElement element)
        {
            if (element == null || _painted == null || !_painted.ContainsKey(element)) { QueueRebuild(); return; }

            OnStateChanged(element);
        }

        private void OnStateChanged(IElement element)
        {
            try
            {
                Dictionary<IElement, ComputedStyle> styles = StyleResolver.Resolve(_document, _sheet, _context);
                if (!styles.TryGetValue(element, out ComputedStyle style)) return;
                if (!_painted.TryGetValue(element, out Painter.PaintedBox box)) return;

                // Through the transition runner rather than straight to the paint: with no `transition` declared it
                // repaints at once, exactly as before, and with one it animates from the style the box has now.
                _styleWas.TryGetValue(element, out ComputedStyle previous);
                Transitions.To(box, previous, style);
                _styleWas[element] = style;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[Sideload] restyle failed: " + e.Message);
            }
        }

        /// <summary>
        /// The pointer entered or left an element. Raised as `mouseenter` / `mouseleave`, the names a browser uses,
        /// so a page written against one behaves the same here.
        ///
        /// They do NOT bubble, which is also what a browser does with this pair: a tooltip that fired again for every
        /// ancestor would open and close as the pointer crossed each nested box. `:hover` was never enough on its
        /// own - a state rule may repaint a box and may not lay one out, so anything that has to APPEAR on hover
        /// needs the page to build it.
        /// </summary>
        /// <summary>
        /// Where an element ended up, in css pixels from the top left of the viewport.
        ///
        /// Taken from the layout tree rather than from the Unity rect: the layout is already in css pixels and in
        /// exactly the frame `position: fixed` is measured in, so a page can put a floating box against another box
        /// without knowing anything about the panel it is drawn on.
        ///
        /// Zeroes for a node the last render did not lay out - a box the script created a moment ago is not on
        /// screen yet, and a made-up position would be worse than an obviously empty one.
        /// </summary>
        private float[] RectOf(IElement element)
        {
            if (element == null || _painted == null) return new[] { 0f, 0f, 0f, 0f };
            if (!_painted.TryGetValue(element, out Painter.PaintedBox box) || box.Node == null)
                return new[] { 0f, 0f, 0f, 0f };

            // A layout node's X/Y are RELATIVE TO ITS PARENT, so they have to be summed up the tree to mean anything
            // to a page. Without this every rect reported the same y - the offset of a row inside its own container
            // - and a tooltip anchored to it landed in the top left corner whichever icon you pointed at.
            float x = box.Node.X, y = box.Node.Y;
            for (IElement up = element.ParentElement; up != null; up = up.ParentElement)
            {
                if (!_painted.TryGetValue(up, out Painter.PaintedBox parent) || parent.Node == null) continue;
                x += parent.Node.X;
                y += parent.Node.Y;
            }
            return new[] { x, y, box.Node.Width, box.Node.Height };
        }

        private void OnHover(IElement element, bool entered)
        {
            _script?.Dispatch(element, entered ? "mouseenter" : "mouseleave", bubbles: false);
        }

        private void OnClicked(IElement element, Input.PointerSpot spot)
        {
            if (element == null) return;
            if (_inputs.ContainsKey(element)) _focused = element;

            _script?.Dispatch(element, "click", spot: spot);
        }

        /// <summary>
        /// A drag on an element whose page asked for it. The three phases are separate event types rather than one
        /// event with a phase field, so a page can listen for the end of a gesture without also being woken sixty
        /// times a second while it runs.
        /// </summary>
        private void OnDragged(IElement element, string type, Input.PointerSpot spot, UnityEngine.Vector2 delta)
        {
            if (element == null) return;

            _script?.Dispatch(element, type, spot: spot, deltaX: delta.x, deltaY: delta.y);
        }

        private void OnWheel(IElement element, float notches)
        {
            if (element == null) return;

            _script?.Dispatch(element, "wheel", wheelDelta: notches);
        }

        /// <summary>A painted input field reports every keystroke. The value is mirrored onto the element so that
        /// `el.value` reads it and a rebuild does not lose what the player typed.</summary>
        private void OnInputChanged(IElement element, string value)
        {
            if (element == null) return;

            element.SetAttribute("value", value ?? "");
            _focused = element;
            _script?.Dispatch(element, "input", value);
        }

        /// <summary>
        /// Run the page's scripts in document order: `&lt;script src&gt;` resolved from the bundle, inline `&lt;script&gt;`
        /// as written. A page with no script tag still gets `app.js` if the bundle has one, so the simplest possible
        /// app is three files and no boilerplate.
        /// </summary>
        private void RunScripts(IDocument document)
        {
            bool any = false;

            foreach (IElement tag in document.QuerySelectorAll("script"))
            {
                string src = tag.GetAttribute("src");

                if (string.IsNullOrEmpty(src))
                {
                    if (string.IsNullOrWhiteSpace(tag.TextContent)) continue;
                    _script.Run(tag.TextContent, "<inline script>");
                    any = true;
                    continue;
                }

                string code = _bundle?.ReadText(src.TrimStart('/'));
                if (code == null) { Core.Log?.Warning($"[Sideload] script not found: {src}"); continue; }

                _script.Run(code, src);
                any = true;
            }

            if (any) return;

            string fallback = _bundle?.ReadText("app.js");
            if (fallback != null) _script.Run(fallback, "app.js");
        }

#if DEBUG
        /// <summary>The most recently mounted view - what the debug harness pokes at.</summary>
        internal static WebView Newest => _live.Count > 0 ? _live[_live.Count - 1] : null;

        /// <summary>Debug-only: click the first element matching a CSS selector, through the real pointer path rather
        /// than by calling its handler.</summary>
        internal void DebugClick(string selector)
        {
            IElement element = _document?.QuerySelector(selector);
            if (element == null || _painted == null
                || !_painted.TryGetValue(element, out Painter.PaintedBox box) || box.Rect == null)
            {
                Core.Log?.Warning($"[Sideload/probe] '{selector}' is not painted.");
                return;
            }

            Canvas canvas = box.Rect.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            Vector3 world = box.Rect.TransformPoint(box.Rect.rect.center);

            Devtools.ClickProbe.ClickAt(RectTransformUtility.WorldToScreenPoint(camera, world), selector);
        }

        /// <summary>Debug-only: run a snippet against the page's own engine, so the harness can set up a state that
        /// would otherwise need a keyboard.</summary>
        internal void DebugEval(string code) => _script?.Run(code, "<probe>");

        /// <summary>Debug-only: evaluate a snippet and hand back what it produced, so a tool can report the answer
        /// rather than only whether the page survived.</summary>
        internal string DebugEvaluate(string code, out bool failed)
        {
            failed = false;
            return _script == null ? "" : _script.Evaluate(code, out failed);
        }

        /// <summary>Debug-only: rebuild the page from disk right now, as a file change would.</summary>
        internal void DebugReload() => Reload();

        /// <summary>Debug-only: re-render from the document already in memory, keeping the script and its state.</summary>
        internal void DebugRebuild() => Rebuild();

        /// <summary>Debug-only: write the tree that was actually laid out to the log. Rebuilding one here would
        /// report zeroes, because a fresh tree has not been through the layout pass.</summary>
        internal void DebugDumpLayout()
        {
            if (_tree == null) return;

            float scale = _root.localScale.x;
            Devtools.LayoutOverlay.Dump(_tree, _root.sizeDelta.x, _root.sizeDelta.y,
                                        _root.sizeDelta.x * scale, _root.sizeDelta.y * scale, scale);
        }

        /// <summary>The same watcher state without the overlay's colour markup.</summary>
        internal string WatchReportPlain() => _watcher == null
            ? $"not watching - create Mods/{_appId}/ to edit live"
            : $"watching Mods/{_appId}/";

        /// <summary>What the hot-reload watcher is doing, for the dev overlay.</summary>
        internal string WatchReport() => _watcher == null
            ? $"<color=#8A8F9E>not watching - create Mods/{_appId}/ to edit live</color>"
            : $"<color=#7CE08A>watching Mods/{_appId}/</color>";

        /// <summary>
        /// Debug-only: the same numbers <see cref="Stats"/> renders, as a flat map of BCL values. It sits beside the
        /// string version so the two cannot drift, and it exists because an agent driving the MCP bridge needs the
        /// figures as data - reading them back out of the formatted overlay text would be guesswork.
        /// </summary>
        internal Dictionary<string, object> DebugStats() => new Dictionary<string, object>
        {
            ["appId"] = _appId,
            ["built"] = _built,
            ["viewportCssWidth"] = _root == null ? 0f : _root.sizeDelta.x,
            ["viewportCssHeight"] = _root == null ? 0f : _root.sizeDelta.y,
            ["scale"] = _root == null ? 0f : _root.localScale.x,
            ["boxes"] = _boxes,
            ["rules"] = _sheet?.Rules.Count ?? 0,
            ["wiredElements"] = _painted?.Count ?? 0,
            ["renders"] = _renders,
            ["lastRenderMs"] = _lastRenderMs,
            ["reloads"] = _reloads,
            ["scriptStatus"] = _script == null ? "none" : _script.Failed ? "failed" : "ok",
            ["scriptError"] = _script?.LastError ?? "",
            ["watchingOverride"] = _watcher != null,
            ["overrideRoot"] = _bundle?.OverrideRoot ?? "",
        };
#endif

        /// <summary>
        /// Enter was pressed in a field. Delivered as a `keydown` with `key === "Enter"`, the spelling the DOM uses,
        /// so a page written for a browser works unchanged - and so more keys can be added later without changing
        /// what an app already listens for.
        ///
        /// The caret is put back afterwards: a single-line TMP field deactivates itself on Enter, and a chat that
        /// drops focus after every message is unusable. A handler that moves focus elsewhere still wins, because
        /// this runs first.
        /// </summary>
        private void OnInputSubmitted(IElement element, string value)
        {
            if (element == null) return;

            element.SetAttribute("value", value ?? "");
            _script?.Dispatch(element, "keydown", value, "Enter");

            _focused = element;
            Focus(element);
        }

        /// <summary>
        /// Offer the page a back - right-click or Escape, the two the game raises together. Returns true when a
        /// handler called <c>preventDefault()</c>, which means the page navigated somewhere and the app must stay
        /// open; false means nobody wanted it and the host should close.
        ///
        /// Dispatched at &lt;body&gt;, where <c>document.addEventListener</c> binds, so a page listens the same way it
        /// listens for anything else. A page with no handler behaves exactly as it did before this existed.
        /// </summary>
        internal bool DispatchBack(string source)
        {
            IElement body = _document?.Body ?? _document?.DocumentElement;
            if (body == null || _script == null) return false;

            return _script.Dispatch(body, "back", source: source ?? "").DefaultPrevented;
        }

        /// <summary>Stylesheets in document order: every &lt;link&gt; resolved from the bundle, then every inline
        /// &lt;style&gt;, so a page can override what it imports.</summary>
        /// <summary>
        /// Everything the engine threw away that the property scan above cannot see.
        ///
        /// That scan answers one question - is this property NAME implemented - and a page written by hand mostly
        /// only trips over that one. A page out of a build tool trips over the other four the whole time, and all
        /// four used to be silent: a value the parsers cannot read (`padding: 1rem`, `oklch(...)`, `calc(...)`),
        /// a value they read and the layout then ignores (`align-items: baseline`), a selector the DOM library
        /// rejects, and an at-rule block skipped whole (`@media (min-width:)`, `@keyframes`, `@layer`).
        ///
        /// Deduplicated the same way and for the same reason: once per thing, not once per occurrence. A Tailwind
        /// build mentions `rem` several thousand times and the resolver runs per matched element - without this
        /// the log would be the only thing in the log.
        ///
        /// The seen-set lives as long as the VIEW, not as long as one call, and that is the point: a page writes
        /// most of its styles from script, and it writes them again on every rebuild. Remembering per load would
        /// miss `el.style.padding = "1rem"` entirely; remembering per rebuild would print it sixty times a second.
        /// Remembering across the view's whole life names each mistake exactly once.
        /// </summary>
        private readonly HashSet<(Model.DiagnosticKind, string, string)> _reported = new();

        /// <summary>
        /// Run <paramref name="work"/> with the diagnostic listener attached, then say what fell out of it.
        ///
        /// The first call carries the page load and normally has plenty to report, so it goes out as one block - it
        /// is a report about the page and a reader wants it in one place. Later calls are rebuilds, where the
        /// seen-set means almost everything is already known and only a genuinely new mistake gets through.
        /// </summary>
        private void CollectDiagnostics(Action work)
        {
            var fresh = new List<string>();

            Action<Model.Diagnostic> previous = Model.Diagnostics.Sink;
            Model.Diagnostics.Sink = d =>
            {
                if (_reported.Add(d.Identity)) fresh.Add(d.ToString());
            };

            try { work(); }
            finally { Model.Diagnostics.Sink = previous; }

            if (fresh.Count == 0) return;

            // The cap is there because a stylesheet nobody wrote for this engine can produce hundreds, and past the
            // first few dozen the list stops being something anyone acts on.
            const int Cap = 40;
            var sb = new StringBuilder();
            sb.Append($"[Sideload] {_appId}: {fresh.Count} Deklaration(en) wirkungslos - der Browser befolgt sie, diese Engine nicht:");
            foreach (string line in fresh.Take(Cap)) sb.Append("\n    ").Append(line);
            if (fresh.Count > Cap) sb.Append($"\n    ... und {fresh.Count - Cap} weitere.");

            Core.Log?.Warning(sb.ToString());
        }

        /// <summary>
        /// Whether the page asked for the web's defaults with `&lt;meta name="sideload" content="web-defaults"&gt;`.
        ///
        /// The meta element never reaches the box tree - `DomBuilder` drops the whole head - but AngleSharp has
        /// parsed it and the document still carries it, which is exactly what makes a meta tag the right place
        /// for this: it is where the web puts a document-level switch, and it costs the renderer nothing.
        ///
        /// Comma-separated, so later switches can join without a second attribute.
        /// </summary>
        private static bool WantsWebDefaults(IDocument document)
        {
            string content = document?.QuerySelector("meta[name=sideload]")?.GetAttribute("content");
            if (string.IsNullOrWhiteSpace(content)) return false;

            foreach (string flag in content.Split(','))
                if (flag.Trim().Equals("web-defaults", StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        private string CollectCss(IDocument document)
        {
            var sb = new StringBuilder();

            foreach (IElement link in document.QuerySelectorAll("link"))
            {
                string rel = link.GetAttribute("rel");
                if (rel == null || !rel.Contains("stylesheet", StringComparison.OrdinalIgnoreCase)) continue;

                string href = link.GetAttribute("href");
                if (string.IsNullOrEmpty(href)) continue;

                string path = href.TrimStart('/');
                string css = _bundle?.ReadText(path) ?? Framework(path);

                if (css == null) { Core.Log?.Warning($"[Sideload] stylesheet not found: {href}"); continue; }
                sb.AppendLine(css);
            }

            foreach (IElement style in document.QuerySelectorAll("style"))
                sb.AppendLine(style.TextContent);

            return sb.ToString();
        }

        private static int CountNodes(LayoutNode node)
        {
            int n = 1;
            foreach (LayoutNode child in node.Children) n += CountNodes(child);
            return n;
        }

        /// <summary>
        /// A stylesheet the FRAMEWORK ships, reached by name from any app: `s1.css` holds the game's design tokens,
        /// so an app that wants to look like the rest of the menus links it instead of copying a palette that would
        /// then drift. An app's own file of the same name still wins, because the bundle is asked first.
        /// </summary>
        private static string Framework(string path)
        {
            string resource = "Sideload.Assets." + path.Replace('/', '.');

            using Stream stream = typeof(WebView).Assembly.GetManifestResourceStream(resource);
            if (stream == null) return null;

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// The same framework file, for something serving a page outside this process. A companion device rendering
        /// the identical bundle has to resolve `s1.css` the same way the in-game view does, or the two look different
        /// for no reason a page author could see.
        /// </summary>
        internal static string FrameworkAsset(string path) =>
            string.IsNullOrWhiteSpace(path) ? null : Framework(path);

        /// <summary>Fail-soft: a broken page shows what went wrong instead of a black screen.</summary>
        private void ShowError(string message)
        {
            Core.Log?.Warning("[Sideload] " + message);

            for (int i = _root.childCount - 1; i >= 0; i--) Object.Destroy(_root.GetChild(i).gameObject);

            RectTransform panel = UiFactory.Rect("sideload-error", _root);
            UiFactory.Stretch(panel);
            UiFactory.Fill(panel, new Color(0.165f, 0.082f, 0.094f, 1f));   // --danger-subtle

            RectTransform label = UiFactory.Rect("message", panel);
            UiFactory.Stretch(label, top: 24f, right: 24f, bottom: 24f, left: 24f);

            var tmp = label.gameObject.AddComponent<Il2CppTMPro.TextMeshProUGUI>();
            tmp.text = "Sideload\n\n" + message;
            tmp.fontSize = 18f;
            tmp.color = new Color(0.945f, 0.439f, 0.478f, 1f);              // --danger-text
            tmp.raycastTarget = false;
        }
    }
}
