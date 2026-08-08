using Il2CppTMPro;
using Sideload.Css;
using Sideload.Layout;
using UnityEngine;
using UnityEngine.UI;

namespace Sideload.Paint
{
    /// <summary>
    /// Walks a laid-out tree and produces the uGUI objects for it: one RectTransform per node, a box mesh where the
    /// style paints something, and a TextMeshPro component for every text leaf.
    /// </summary>
    internal static class Painter
    {
        /// <summary>One painted node, kept so a later restyle can repaint it without rebuilding the page.</summary>
        internal readonly struct PaintedBox
        {
            internal readonly LayoutNode Node;
            internal readonly RectTransform Rect;

            /// <summary>The text this box painted for ITSELF, if any. Held rather than searched for: a box's subtree
            /// contains its children's text too, and recolouring the first one found repaints a child that has a
            /// colour of its own - which is how hovering a chat row turned its white avatar letter dark.</summary>
            internal readonly Il2CppTMPro.TextMeshProUGUI Text;

            /// <summary>
            /// The clip this box was FIRST painted under, so a later repaint can put it back.
            ///
            /// This is not bookkeeping for its own sake. Clipping lives on the CanvasRenderer and is set from the
            /// static <see cref="BoxRenderer.ActiveClip"/>, which is only meaningful while the paint walk is inside
            /// a scroll area. A repaint happens long afterwards - a hover, a transition frame - with that static
            /// back at null, so the box was redrawn UNCLIPPED and reappeared at whatever position its rect holds.
            ///
            /// What that looked like: hovering a list row that had scrolled up out of its viewport repainted the
            /// row's background across the sticky bar above the list, hiding it completely - and with no text,
            /// because a TMP component carries its own clip from build time and was never touched.
            /// </summary>
            internal readonly Rect? Clip;

            internal PaintedBox(LayoutNode node, RectTransform rect, Il2CppTMPro.TextMeshProUGUI text = null)
            {
                Node = node;
                Rect = rect;
                Text = text;
                Clip = BoxRenderer.ActiveClip;
            }
        }

        /// <summary>
        /// The form controls of the render in progress, and where keystrokes from them go. Both belong to ONE view:
        /// two apps are mounted at once, so a process-wide map would hand app A's keystrokes to app B's script the
        /// moment B rendered last. The host passes its own map in and keeps it.
        /// </summary>
        private static Dictionary<AngleSharp.Dom.IElement, TMP_InputField> _inputs;
        private static Action<AngleSharp.Dom.IElement, string> _inputChanged;
        private static Action<AngleSharp.Dom.IElement, string> _inputSubmitted;

        private static Vector2 _viewSize;
        private static RectTransform _viewRoot;
        private static Bundle.AppBundle _bundle;   // resolves <img src="..."> against the app's own files
        private static string _appId;
        private static Dictionary<AngleSharp.Dom.IElement, RectTransform> _reuse;
        private static List<LayoutNode> _topLayer;   // `position: fixed` boxes deferred to the end of the pass

        /// <summary><paramref name="reuse"/> maps elements whose GameObject must SURVIVE this pass onto that object.
        /// Only form controls qualify: a TMP_InputField owns the caret, the selection and the half-typed word, and all
        /// three are gone the moment it is recreated - which is what made every keystroke swallow itself.</summary>
        internal static Dictionary<AngleSharp.Dom.IElement, PaintedBox> Paint(
            LayoutNode root, RectTransform host, Vector2 viewSize,
            Dictionary<AngleSharp.Dom.IElement, TMP_InputField> inputs,
            Action<AngleSharp.Dom.IElement, string> inputChanged,
            Action<AngleSharp.Dom.IElement, string> inputSubmitted,
            Dictionary<AngleSharp.Dom.IElement, RectTransform> reuse = null,
            Bundle.AppBundle bundle = null, string appId = null)
        {
            var painted = new Dictionary<AngleSharp.Dom.IElement, PaintedBox>();
            if (root == null || host == null) return painted;

            _inputs = inputs;
            _inputChanged = inputChanged;
            _inputSubmitted = inputSubmitted;
            _inputs?.Clear();
            _placement.Clear();

            _viewSize = viewSize;
            _viewRoot = host;
            _reuse = reuse;
            _bundle = bundle;
            _appId = appId ?? "";
            BoxRenderer.ActiveClip = null;
            BoxRenderer.BeginPass(host.GetInstanceID());

            var topLayer = new List<LayoutNode>();
            _topLayer = topLayer;
            try
            {
                PaintNode(root, host, 0, painted, 0f, 0f);

                // The top layer: every `position: fixed` box, hoisted out of wherever it was written and drawn here
                // instead. Three things follow from doing it at this point, and all three are the reason overlays
                // work at all:
                //
                //   * AFTER the page, so it draws on top. Paint order in this engine is document order and there is
                //     no z-index, so "last" is the only way to be above something.
                //   * UNDER THE VIEW ROOT, so no ancestor's `overflow` crops it and no scroll area carries it off
                //     screen - and, because uGUI raycasts front-most first, so a backdrop over the page actually
                //     swallows the clicks meant for what is behind it.
                //   * With the clip stack back at nothing, which the walk above restores on its way out.
                //
                // The list grows while it is being walked: a fixed box nested inside another one appends to it and is
                // picked up by the same loop, which is why this is an index loop and not a foreach.
                BoxRenderer.ActiveClip = null;
                for (int i = 0; i < topLayer.Count; i++)
                    PaintNode(topLayer[i], host, 0, painted, 0f, 0f, hoisted: true);
            }
            finally
            {
                _topLayer = null;
                BoxRenderer.EndPass();
                _reuse = null;
                _inputs = null;
                _inputChanged = null;
                _inputSubmitted = null;
                _bundle = null;
            }

            return painted;
        }

        /// <summary><paramref name="absX"/>/<paramref name="absY"/> accumulate the node's position from the view root,
        /// in CSS pixels with y growing downwards - the clip rectangle is derived from these instead of from a
        /// RectTransform, which has no usable rect until the canvas has laid it out.</summary>
        /// <param name="hoisted">This node IS the top-layer box being drawn, so it must not defer itself again. Its
        /// X/Y are viewport coordinates rather than parent-relative ones, which is what makes the view root the right
        /// parent for it.</param>
        private static void PaintNode(LayoutNode node, Transform parent, int depth,
                                      Dictionary<AngleSharp.Dom.IElement, PaintedBox> painted,
                                      float absX, float absY, bool hoisted = false)
        {
            if (node.Style.Display == DisplayKind.None) return;

            if (!hoisted && node.Style.Position == PositionKind.Fixed)
            {
                _topLayer?.Add(node);
                return;
            }

            absX += node.X;
            absY += node.Y;

            if (node.Tag is AngleSharp.Dom.IElement surviving && _reuse != null
                && _reuse.TryGetValue(surviving, out RectTransform kept) && kept != null)
            {
                painted[surviving] = new PaintedBox(node, ReuseControl(node, kept, parent, surviving, absX, absY));
                return;
            }

            RectTransform rt = UiFactory.Rect(NameOf(node, depth), parent);
            UiFactory.PlaceFromTopLeft(rt, node.X, node.Y, node.Width, node.Height);
            ApplyTransform(rt, node.Style);

            // A text leaf shares its element with the box around it; the box wins, because that is what gets restyled.
            if (node.Tag is AngleSharp.Dom.IElement owner && !painted.ContainsKey(owner))
                painted[owner] = new PaintedBox(node, rt);

            if (Paints(node.Style))
                BoxRenderer.Paint(rt, ToVisual(node.Style, node.Width, node.Height), node.Width, node.Height);

#if DEBUG
            if (Devtools.LayoutOverlay.Outlines) DrawOutline(rt, node);
#endif

            if (IsFormControl(node, out bool multiline))
            {
                PaintInput(node, rt, multiline, absX, absY);
                return;
            }

            if (IsImage(node))
            {
                PaintImage(node, rt);
                return;
            }

            if (node.IsTextLeaf)
            {
                Il2CppTMPro.TextMeshProUGUI text = PaintText(node, rt);
                if (node.Tag is AngleSharp.Dom.IElement leaf) painted[leaf] = new PaintedBox(node, rt, text);
                return;
            }

            if (!NeedsScrolling(node))
            {
                // `overflow: hidden` clips without scrolling. Until this existed it did nothing at all, so a box
                // meant as a window onto something bigger - a map, a graph - let its contents draw right across the
                // rest of the screen.
                Rect? own = NeedsClipping(node) ? ClipRectOf(rt, absX, absY, node.Width, node.Height) : null;

                if (!own.HasValue)
                {
                    foreach (LayoutNode child in node.Children)
                        PaintNode(child, rt, depth + 1, painted, absX, absY);
                    return;
                }

                Rect? outerClip = BoxRenderer.ActiveClip;
                BoxRenderer.ActiveClip = Narrow(own, outerClip);
                try
                {
                    foreach (LayoutNode child in node.Children)
                        PaintNode(child, rt, depth + 1, painted, absX, absY);
                }
                finally { BoxRenderer.ActiveClip = outerClip; }

                return;
            }

            Transform content = BuildScrollArea(node, rt, absX, absY, out Rect? clip);

            // Everything painted below here clips to this rectangle. A nested scroll area restores the outer one on
            // the way out, so the stack unwinds correctly.
            Rect? previous = BoxRenderer.ActiveClip;
            BoxRenderer.ActiveClip = clip ?? previous;
            try
            {
                foreach (LayoutNode child in node.Children)
                    PaintNode(child, content, depth + 1, painted, absX, absY);
            }
            finally { BoxRenderer.ActiveClip = previous; }
        }

        /// <summary>
        /// Where a box actually ENDS UP, as a clip rectangle - transforms included.
        ///
        /// The layout knows where a box would sit; it does not know that an ancestor was moved or scaled by a
        /// `transform`, because a transform is applied after layout and deliberately changes nothing about it. A
        /// clip derived from the layout alone therefore lands somewhere else than the pixels do, and everything
        /// inside a panned or zoomed window disappears - which is exactly what a map or a graph is.
        ///
        /// The box's own corners carry the whole ancestor chain, so they are the honest source. The layout figure
        /// stays as the fallback: a node the canvas has not measured yet reports a degenerate rectangle, and a
        /// degenerate clip culls everything.
        /// </summary>
        private static Rect? ClipRectOf(RectTransform rt, float absX, float absY, float width, float height)
        {
            Rect? fromLayout = ClipRectInCanvasSpace(absX, absY, width, height);
            if (rt == null || _viewRoot == null) return fromLayout;

            try
            {
                Canvas canvas = _viewRoot.GetComponentInParent<Canvas>();
                if (canvas == null) return fromLayout;

                Transform space = (canvas.rootCanvas != null ? canvas.rootCanvas : canvas).transform;

                // The corners are taken through the transform one at a time rather than read from
                // GetWorldCorners. That call fills a Vector3[] the caller owns, and under Il2CppInterop the array
                // is copied INTO interop memory and never copied back - it returns four zeroes, every time. The
                // rectangle then collapsed, the guard below read that as "not measured yet", and the fallback won
                // silently: this whole method was dead in an IL2CPP build, and a panned or zoomed window clipped
                // where its layout said rather than where its pixels were.
                Rect local = rt.rect;

                var corners = new Vector3[4];
                corners[0] = rt.TransformPoint(new Vector3(local.xMin, local.yMin, 0f));
                corners[1] = rt.TransformPoint(new Vector3(local.xMin, local.yMax, 0f));
                corners[2] = rt.TransformPoint(new Vector3(local.xMax, local.yMax, 0f));
                corners[3] = rt.TransformPoint(new Vector3(local.xMax, local.yMin, 0f));

                float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;

                for (int i = 0; i < 4; i++)
                {
                    // Sorted rather than taken in order: a rotated ancestor hands the corners back in whatever
                    // order the rotation produced, and assuming [0] is the bottom-left gives a negative size.
                    Vector3 p = space.InverseTransformPoint(corners[i]);
                    if (p.x < xMin) xMin = p.x;
                    if (p.y < yMin) yMin = p.y;
                    if (p.x > xMax) xMax = p.x;
                    if (p.y > yMax) yMax = p.y;
                }

                return xMax - xMin > 0.5f && yMax - yMin > 0.5f
                    ? new Rect(xMin, yMin, xMax - xMin, yMax - yMin)
                    : fromLayout;
            }
            catch
            {
                return fromLayout;
            }
        }

        /// <summary>
        /// The clip rectangle CanvasRenderer.EnableRectClipping expects: ROOT CANVAS space, and a Rect of
        /// position-plus-size rather than min/max corners.
        ///
        /// Derived from the layout, not from a RectTransform: a freshly created stretched node still measures 0x0
        /// until the canvas lays it out, which is what made an earlier attempt compute a degenerate rectangle.
        /// </summary>
        private static Rect? ClipRectInCanvasSpace(float absX, float absY, float width, float height)
        {
            if (_viewRoot == null) return null;

            Canvas canvas = _viewRoot.GetComponentInParent<Canvas>();
            if (canvas == null) return null;
            Transform space = (canvas.rootCanvas != null ? canvas.rootCanvas : canvas).transform;

            // View-root local space: origin at the centre, y growing UP. absX/absY come from the top-left, y down.
            var topLeft = new Vector3(absX - _viewSize.x * 0.5f, _viewSize.y * 0.5f - absY, 0f);
            var bottomRight = new Vector3(absX + width - _viewSize.x * 0.5f, _viewSize.y * 0.5f - (absY + height), 0f);

            Vector3 a = space.InverseTransformPoint(_viewRoot.TransformPoint(topLeft));
            Vector3 b = space.InverseTransformPoint(_viewRoot.TransformPoint(bottomRight));

            float xMin = Math.Min(a.x, b.x), xMax = Math.Max(a.x, b.x);
            float yMin = Math.Min(a.y, b.y), yMax = Math.Max(a.y, b.y);
            return new Rect(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        private static bool NeedsScrolling(LayoutNode node)
        {
            OverflowKind overflow = node.Style.OverflowY;
            if (overflow != OverflowKind.Auto && overflow != OverflowKind.Scroll) return false;
            return ContentBottom(node) > node.Height + 0.5f;
        }

        /// <summary>
        /// Whether this box cuts its children off at its own edge.
        ///
        /// Either axis counts: CSS has no way to clip one and not the other, so a box that says hidden on either is
        /// a box whose contents stop at its border. A scrolling box does its own clipping and is handled separately.
        /// </summary>
        private static bool NeedsClipping(LayoutNode node) =>
            node.Style.OverflowX == OverflowKind.Hidden || node.Style.OverflowY == OverflowKind.Hidden;

        /// <summary>The part of an inner clip that survives an outer one. Null outer means nothing narrows it.</summary>
        private static Rect? Narrow(Rect? inner, Rect? outer)
        {
            if (!inner.HasValue) return outer;
            if (!outer.HasValue) return inner;

            Rect a = inner.Value, b = outer.Value;
            float xMin = Math.Max(a.xMin, b.xMin), xMax = Math.Min(a.xMax, b.xMax);
            float yMin = Math.Max(a.yMin, b.yMin), yMax = Math.Min(a.yMax, b.yMax);
            return new Rect(xMin, yMin, Math.Max(0f, xMax - xMin), Math.Max(0f, yMax - yMin));
        }

        /// <summary>Furthest a child reaches down, in the box's own coordinates - the height the scroll content needs.</summary>
        private static float ContentBottom(LayoutNode node)
        {
            float bottom = 0f;
            foreach (LayoutNode child in node.Children)
            {
                if (child.Style.Display == DisplayKind.None) continue;
                bottom = Math.Max(bottom, child.Y + child.Height);
            }
            return bottom + node.Style.Padding.Bottom.Resolve(node.Width);
        }

        /// <summary>
        /// Wrap the children in a ScrollRect so an overflowing box becomes scrollable.
        ///
        /// The clipping mask and the scroll machinery go on their OWN node rather than on the box: a Graphic - and the
        /// transparent one here is needed so the wheel has something to hit - takes over the CanvasRenderer it sits on
        /// and would erase the box's mesh. Children keep the coordinates the layout gave them, because the content
        /// node starts at the box's top-left.
        /// </summary>
        private static Transform BuildScrollArea(LayoutNode node, RectTransform box, float absX, float absY, out Rect? clip)
        {
            RectTransform viewport = UiFactory.Rect("scroll-viewport", box);
            UiFactory.Stretch(viewport);

            var catcher = viewport.gameObject.AddComponent<Image>();
            catcher.color = new Color(0f, 0f, 0f, 0f);
            catcher.raycastTarget = true;

            // Deliberately NO RectMask2D. It derives its clip rectangle from world corners taken in fixed order, so a
            // rotated ancestor hands it a rectangle with negative width and height - and a phone in portrait rotates
            // the whole panel by 90 degrees. The intersection then comes out empty and every masked child is culled:
            // an app with a scrolling list simply loses its text. Clipping runs through ActiveClip instead, which
            // sorts its corners, and which the box meshes already used because RectMask2D never drove them anyway.

            RectTransform content = UiFactory.Rect("scroll-content", viewport);
            UiFactory.PlaceFromTopLeft(content, 0f, 0f, node.Width, ContentBottom(node));

            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.content = content;
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Elastic;
            scroll.elasticity = 0.1f;
            scroll.scrollSensitivity = 24f;
            scroll.inertia = true;
            scroll.decelerationRate = 0.135f;

            // Hand the wheel to Input.SmoothScroll unless this box opted out with `-s1-scroll: instant`. It zeroes
            // the sensitivity above and catches the event on the viewport, because over empty space uGUI dispatches
            // straight to the ScrollRect and never passes through anything Sideload wired.
            if (node.Style.SmoothScroll) Input.SmoothScroll.Install(scroll, viewport, 24f);

            clip = ClipRectInCanvasSpace(absX, absY, node.Width, node.Height);

            // Put the clip back on every child each time the list moves.
            //
            // Scrolling is not a passive act in uGUI: moving the content makes every MaskableGraphic under it
            // recompute which mask it belongs to, and the phone's own panel HAS a RectMask2D that our content sits
            // outside of while scrolled. Whatever we set at paint time is therefore not what survives the first
            // wheel notch - images and text were being culled outright, and the boxes, which are not Graphics at
            // all and so were never re-examined, carried on drawing past the edge of the screen. Reapplying on the
            // scroll event is the only point at which both are true again.
            Rect? settled = clip;
            RectTransform viewportRect = viewport;
            scroll.onValueChanged.AddListener((UnityEngine.Events.UnityAction<Vector2>)(_ =>
            {
                Reclip(content, settled);
                CullHitTargets(content, viewportRect);
            }));

            // Once at build time as well: a list that is already scrolled when it is rebuilt - a rebuild during a
            // scroll, or a page that restores its position - would otherwise wait for the next wheel notch.
            CullHitTargets(content, viewport);

            // One line per scroll area per build, so it follows the same developer switch as the build report itself.
            if (Config.Preferences.LogPageBuilds)
                Core.Log?.Msg($"scroll area at ({absX:0.#},{absY:0.#}) size {node.Width:0.#}x{node.Height:0.#}, " +
                              $"content {ContentBottom(node):0.#}, clip={clip}");

            return content;
        }

        /// <summary>
        /// Repaint one box with a freshly computed style. Used when an interaction state changes: only the box's
        /// appearance is redone, never the layout - which is why state rules are documented as paint-only.
        /// </summary>
        internal static void Repaint(PaintedBox box, ComputedStyle style)
        {
            if (box.Rect == null || style == null) return;

            // Under the clip it was built with - see PaintedBox.Clip. Restored rather than assigned, because a
            // repaint can be triggered from inside a paint walk and must not leave the walk's clip behind.
            Rect? outer = BoxRenderer.ActiveClip;
            BoxRenderer.ActiveClip = box.Clip;
            try
            {
                BoxRenderer.Paint(box.Rect, ToVisual(style, box.Node.Width, box.Node.Height), box.Node.Width, box.Node.Height);
            }
            finally { BoxRenderer.ActiveClip = outer; }

            ApplyTransform(box.Rect, style);

            if (box.Text != null)
                box.Text.color = new Color(style.Color.R, style.Color.G, style.Color.B, style.Color.A);
        }

        /// <summary>
        /// Repaint a box part-way between two styles. This is what a `transition` actually is: the same paint the
        /// state change would have done at once, with every animatable value read from somewhere along the line
        /// between where it was and where it is going.
        ///
        /// Only values that cannot move another box are interpolated - colours, opacity and the transform. A width
        /// or a padding would need the whole page laid out again on every frame of the animation, which is the
        /// promise this engine does not make.
        /// </summary>
        internal static void RepaintBetween(PaintedBox box, ComputedStyle from, ComputedStyle to, float t)
        {
            if (box.Rect == null || from == null || to == null) return;

            float w = box.Node.Width, h = box.Node.Height;

            BoxVisual visual = ToVisual(to, w, h);
            BoxVisual before = ToVisual(from, w, h);

            visual.FillTL = Color.Lerp(before.FillTL, visual.FillTL, t);
            visual.FillTR = Color.Lerp(before.FillTR, visual.FillTR, t);
            visual.FillBR = Color.Lerp(before.FillBR, visual.FillBR, t);
            visual.FillBL = Color.Lerp(before.FillBL, visual.FillBL, t);
            visual.BorderColor = Color.Lerp(before.BorderColor, visual.BorderColor, t);
            visual.ShadowColor = Color.Lerp(before.ShadowColor, visual.ShadowColor, t);

            // Same as Repaint: a transition frame is a repaint, and an unclipped one escapes its scroll area.
            Rect? outer = BoxRenderer.ActiveClip;
            BoxRenderer.ActiveClip = box.Clip;
            try { BoxRenderer.Paint(box.Rect, visual, w, h); }
            finally { BoxRenderer.ActiveClip = outer; }

            ApplyTransform(box.Rect, from, to, t);

            if (box.Text != null)
                box.Text.color = Color.Lerp(new Color(from.Color.R, from.Color.G, from.Color.B, from.Color.A),
                                            new Color(to.Color.R, to.Color.G, to.Color.B, to.Color.A), t);
        }

        /// <summary>
        /// Push a transform onto the placed node. It runs AFTER layout by construction - the anchored position the
        /// layout chose is kept and the translation is added on top - so a transformed box cannot disturb a sibling.
        /// </summary>
        private static void ApplyTransform(RectTransform rt, ComputedStyle style)
        {
            if (rt == null || style == null) return;
            Place(rt, style.TranslateX, style.TranslateY, style.ScaleX, style.ScaleY, style.RotateDeg);
        }

        private static void ApplyTransform(RectTransform rt, ComputedStyle from, ComputedStyle to, float t)
        {
            if (rt == null) return;

            Place(rt,
                  Mathf.Lerp(from.TranslateX, to.TranslateX, t),
                  Mathf.Lerp(from.TranslateY, to.TranslateY, t),
                  Mathf.Lerp(from.ScaleX, to.ScaleX, t),
                  Mathf.Lerp(from.ScaleY, to.ScaleY, t),
                  Mathf.Lerp(from.RotateDeg, to.RotateDeg, t));
        }

        private static void Place(RectTransform rt, float tx, float ty, float sx, float sy, float rotation)
        {
            // The layout's own offset is remembered the first time, so repeated transforms compose against the
            // placement rather than against each other.
            if (!_placement.TryGetValue(rt.GetInstanceID(), out Vector2 anchored))
                _placement[rt.GetInstanceID()] = anchored = rt.anchoredPosition;

            // CSS y grows downwards, Unity's grows up.
            rt.anchoredPosition = anchored + new Vector2(tx, -ty);
            rt.localScale = new Vector3(sx, sy, 1f);
            rt.localRotation = Quaternion.Euler(0f, 0f, -rotation);
        }

        private static readonly Dictionary<int, Vector2> _placement = new();

#if DEBUG
        /// <summary>A hairline around the box itself, drawn on its own node so it cannot disturb the real geometry.</summary>
        private static void DrawOutline(RectTransform box, LayoutNode node)
        {
            // Placed explicitly rather than stretched: a stretched rect still measures 0x0 when the mesh is handed to
            // the CanvasRenderer, and the outline never appeared. Every box that works goes through this same path.
            RectTransform rt = UiFactory.Rect("outline", box);
            UiFactory.PlaceFromTopLeft(rt, 0f, 0f, node.Width, node.Height);

            // Fully opaque and 2px wide: a 1px hairline at 45% is below half a screen pixel once the view is scaled
            // down onto the phone and simply disappears, which makes the overlay useless exactly when it is needed.
            var visual = BoxVisual.Solid(new Color(0f, 0f, 0f, 0f));
            visual.BorderWidth = 2f;
            visual.BorderColor = node.IsTextLeaf
                ? new Color(0.2f, 1f, 1f, 1f)    // text leaves in cyan
                : new Color(1f, 0f, 1f, 1f);     // boxes in magenta

            BoxRenderer.Paint(rt, visual, node.Width, node.Height);
        }
#endif

        private static bool IsFormControl(LayoutNode node, out bool multiline)
        {
            multiline = false;
            if (node.Tag is not AngleSharp.Dom.IElement element) return false;

            string tag = element.LocalName;
            if (string.Equals(tag, "textarea", StringComparison.OrdinalIgnoreCase)) { multiline = true; return true; }
            return string.Equals(tag, "input", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A real TMP_InputField inside our box. Caret, selection, clipboard, umlauts and IME all come from Unity -
        /// the same component the game's own console uses. Writing a text editor by hand would be a project of its own.
        ///
        /// Focus flips GameInput.IsTyping, otherwise typing "w" would walk the player forward while editing.
        /// </summary>
        /// <summary>
        /// Slot a surviving control back into the freshly built tree: move it, resize it, repaint the box around it,
        /// and hand it whatever the DOM now says its value is. Everything inside it - the caret position, the
        /// selection, the field's own text buffer - is deliberately left alone.
        /// </summary>
        private static RectTransform ReuseControl(LayoutNode node, RectTransform kept, Transform parent,
                                                  AngleSharp.Dom.IElement element, float absX, float absY)
        {
            kept.SetParent(parent, worldPositionStays: false);
            UiFactory.PlaceFromTopLeft(kept, node.X, node.Y, node.Width, node.Height);

            if (Paints(node.Style))
                BoxRenderer.Paint(kept, ToVisual(node.Style, node.Width, node.Height), node.Width, node.Height);

            // The interaction pass adds a fresh EventTrigger to every wired element, so the one from the previous
            // build has to go or the control fires every handler twice.
            foreach (UnityEngine.EventSystems.EventTrigger stale in kept.GetComponents<UnityEngine.EventSystems.EventTrigger>())
                UnityEngine.Object.Destroy(stale);

            var field = kept.GetComponent<TMP_InputField>();
            if (field != null)
            {
                if (_inputs != null) _inputs[element] = field;

                // Only when the document disagrees: assigning the same string still moves the caret to the end.
                string wanted = element.GetAttribute("value") ?? "";
                if (!string.Equals(field.text, wanted, StringComparison.Ordinal))
                {
                    field.text = wanted;
                    PutCaretAtEnd(field);
                }

                field.interactable = !element.HasAttribute("disabled");

                // The rest of the control's attributes have to be re-read too. A reused node keeps the TMP children it
                // was built with, so without this a placeholder changed from script - or from the Elements panel -
                // would be the one edit on the page that silently does nothing.
                if (field.placeholder != null)
                {
                    var placeholder = field.placeholder.TryCast<TextMeshProUGUI>();
                    string hint = element.GetAttribute("placeholder") ?? "";
                    if (placeholder != null && !string.Equals(placeholder.text, hint, StringComparison.Ordinal))
                        placeholder.text = hint;
                }

                field.characterLimit = int.TryParse(element.GetAttribute("maxlength"), out int max) && max > 0 ? max : 0;

                // The suggestion changes with every keystroke, and the control survives the rebuild that carries it.
                foreach (TextMeshProUGUI child in kept.GetComponentsInChildren<TextMeshProUGUI>(true))
                    if (child != null && child.gameObject.name == "input-ghost") WriteGhost(child, element, node.Style);

                // The control moved and may have changed size - most visibly when the phone turns - so the rectangle
                // its text clips to has to be recomputed. Nothing else maintains it: the clip lives on the
                // CanvasRenderer, which survives the rebuild along with the control.
                ComputedStyle s = node.Style;
                Rect? clip = InputClip(node, absX, absY,
                                       s.Padding.Left.Resolve(node.Width) + s.BorderWidth.Left.Resolve(node.Width),
                                       s.Padding.Right.Resolve(node.Width) + s.BorderWidth.Right.Resolve(node.Width),
                                       s.Padding.Top.Resolve(node.Width) + s.BorderWidth.Top.Resolve(node.Width),
                                       s.Padding.Bottom.Resolve(node.Width) + s.BorderWidth.Bottom.Resolve(node.Width));

                ClipTo(field.textComponent != null ? field.textComponent.TryCast<TextMeshProUGUI>() : null, clip);
                ClipTo(field.placeholder != null ? field.placeholder.TryCast<TextMeshProUGUI>() : null, clip);
            }

            return kept;
        }

        /// <summary>
        /// Put the inline suggestion in place: the typed text made invisible, then the part that would be added.
        ///
        /// `data-ghost` holds only the REMAINDER - a page that has worked out `give` from `gi` sets "ve". The typed
        /// text is read back from the element so the two cannot disagree about where the visible part starts.
        ///
        /// The typed run goes inside noparse, because a player typing `give &lt;item&gt;` would otherwise watch their
        /// argument disappear into a tag that does not exist.
        /// </summary>
        private static void WriteGhost(TextMeshProUGUI ghost, AngleSharp.Dom.IElement element, ComputedStyle s)
        {
            if (ghost == null) return;

            string rest = element.GetAttribute("data-ghost") ?? "";
            if (rest.Length == 0) { ghost.text = ""; return; }

            string typed = element.GetAttribute("value") ?? "";

            RgbaColor colour = s.GhostColor ?? new RgbaColor(s.Color.R, s.Color.G, s.Color.B, s.Color.A * 0.45f);
            ghost.color = new Color(colour.R, colour.G, colour.B, colour.A);

            ghost.text = typed.Length > 0
                ? "<color=#00000000><noparse>" + typed + "</noparse></color>" + rest
                : rest;
        }

        private static void PaintInput(LayoutNode node, RectTransform box, bool multiline, float absX, float absY)
        {
            ComputedStyle s = node.Style;
            var element = (AngleSharp.Dom.IElement)node.Tag;

            float padLeft = s.Padding.Left.Resolve(node.Width) + s.BorderWidth.Left.Resolve(node.Width);
            float padRight = s.Padding.Right.Resolve(node.Width) + s.BorderWidth.Right.Resolve(node.Width);
            float padTop = s.Padding.Top.Resolve(node.Width) + s.BorderWidth.Top.Resolve(node.Width);
            float padBottom = s.Padding.Bottom.Resolve(node.Width) + s.BorderWidth.Bottom.Resolve(node.Width);

            RectTransform viewport = UiFactory.Rect("input-viewport", box);
            UiFactory.Stretch(viewport, top: padTop, right: padRight, bottom: padBottom, left: padLeft);

            // No RectMask2D here either - see BuildScrollArea for why it cannot survive a rotated panel. The text and
            // the placeholder clip to the control's own content box instead, so overtyping still stops at the edge.
            Rect? clip = InputClip(node, absX, absY, padLeft, padRight, padTop, padBottom);

            RectTransform textRect = UiFactory.Rect("input-text", viewport);
            UiFactory.Stretch(textRect);
            var text = textRect.gameObject.AddComponent<TextMeshProUGUI>();
            TmpMeasure.Apply(text, s);
            text.text = "";
            ClipTo(text, clip);

            RectTransform placeholderRect = UiFactory.Rect("input-placeholder", viewport);
            UiFactory.Stretch(placeholderRect);
            var placeholder = placeholderRect.gameObject.AddComponent<TextMeshProUGUI>();
            TmpMeasure.Apply(placeholder, s);
            placeholder.text = element.GetAttribute("placeholder") ?? "";
            placeholder.color = new Color(s.Color.R, s.Color.G, s.Color.B, s.Color.A * 0.45f);
            ClipTo(placeholder, clip);

            // The inline suggestion, drawn behind the caret in the field's own font.
            //
            // Its own text leaf rather than something written into the field: the field's text is what the player
            // typed and what Enter submits, and a suggestion pushed in there would be both. Laid out by TMP instead
            // of positioned by hand - it holds the typed text in full, made invisible with an alpha-zero colour tag,
            // so the visible part starts exactly where the typing stops. No measuring, and nothing to drift.
            RectTransform ghostRect = UiFactory.Rect("input-ghost", viewport);
            UiFactory.Stretch(ghostRect);
            var ghost = ghostRect.gameObject.AddComponent<TextMeshProUGUI>();
            TmpMeasure.Apply(ghost, s);
            ghost.richText = true;
            ghost.raycastTarget = false;
            ClipTo(ghost, clip);
            WriteGhost(ghost, element, s);

            var field = box.gameObject.AddComponent<TMP_InputField>();
            field.textViewport = viewport;
            field.textComponent = text;
            field.placeholder = placeholder;
            field.lineType = multiline ? TMP_InputField.LineType.MultiLineNewline : TMP_InputField.LineType.SingleLine;
            field.text = element.GetAttribute("value") ?? "";
            field.interactable = !element.HasAttribute("disabled");
            field.richText = false;

            if (int.TryParse(element.GetAttribute("maxlength"), out int max) && max > 0)
                field.characterLimit = max;

            // TMP selects the whole text when a field takes focus, so the next character typed REPLACES everything -
            // which reads as "the field wipes itself every time I click back into it". Web inputs place the caret and
            // keep the text, so do that. resetOnDeActivation goes the same way: clicking a button elsewhere on the page
            // must not roll the field back to what it held when it was last focused.
            field.onFocusSelectAll = false;
            field.resetOnDeActivation = false;

            // The caret has its own colour and is invisible unless told to follow the text. Width 0 would hide it too.
            //
            // Both are stylable. `caret-color` is standard CSS and defaults to the text colour, which is what a form
            // field wants. `-s1-caret-width` has no standard equivalent and is the whole difference between a text
            // cursor and the block a terminal draws: set it to one character cell and the caret covers a glyph.
            RgbaColor caret = s.CaretColor ?? s.Color;

            field.customCaretColor = true;
            field.caretColor = new Color(caret.R, caret.G, caret.B, 1f);
            field.caretWidth = Math.Max(1, (int)Math.Round(s.CaretWidth));
            field.caretBlinkRate = 1.5f;
            field.selectionColor = new Color(0.369f, 0.416f, 0.824f, 0.5f);

            // TMP_InputField builds its caret object in OnEnable, and OnEnable runs the instant AddComponent returns -
            // at which point textComponent is still null, so it silently builds nothing and the field has no caret for
            // the rest of its life. Cycling `enabled` runs OnEnable again, now with everything wired up.
            field.enabled = false;
            field.enabled = true;

            // Captured now, not read at event time: by the time a key is pressed another view may have rendered,
            // and a shared field would send this app's keystrokes to that one's script.
            Action<AngleSharp.Dom.IElement, string> changed = _inputChanged;
            Action<AngleSharp.Dom.IElement, string> submitted = _inputSubmitted;

            // Enter. TMP raises onSubmit for exactly that, which is why the engine does not need a general key
            // listener to make the one keystroke every form actually depends on work. onEndEdit would be wrong here:
            // it also fires when the field merely loses focus, so clicking away would send the message.
            field.onSubmit.AddListener((UnityEngine.Events.UnityAction<string>)(v =>
            {
                try { submitted?.Invoke(element, v); }
                catch (Exception e) { Core.Log?.Warning("submit handler failed: " + e.Message); }
            }));

            field.onSelect.AddListener((UnityEngine.Events.UnityAction<string>)(_ => SetTyping(true)));
            field.onDeselect.AddListener((UnityEngine.Events.UnityAction<string>)(_ => SetTyping(false)));

            RejectLeadingCharacters(field, element.GetAttribute("data-reject-first"));

            // Mirroring every keystroke onto the element is what makes `el.value` work from script AND what lets a
            // rebuild restore the text: the DOM, not the Unity component, is the source of truth for what was typed.
            field.onValueChanged.AddListener((UnityEngine.Events.UnityAction<string>)(v =>
            {
                try { changed?.Invoke(element, v); }
                catch (Exception e) { Core.Log?.Warning("input handler failed: " + e.Message); }
            }));

            if (_inputs != null) _inputs[element] = field;
        }

        /// <summary>
        /// Refuse a set of characters at the very front of a field: <c>data-reject-first="^`´~"</c>.
        ///
        /// This exists for one problem, and it is a problem no page can solve for itself. A key that opens an app is
        /// often a DEAD KEY - `^` on German and Swiss layouts is the usual one - and a dead key emits nothing when
        /// pressed and then delivers its mark a moment later, into the field the app just focused. The player sees
        /// `^help` and never typed the caret.
        ///
        /// REFUSED, NOT DELETED, and that distinction is the whole feature. TMP asks this before inserting and drops
        /// the character when the answer is 0, so nothing is written and no caret needs repairing. Taking the mark
        /// out afterwards cannot be made to work: caret positions are mapped through the RENDERED text, so a field
        /// rewritten behind TMP's back pulls the caret back onto the old string and the next keystroke lands at the
        /// front - which turns `give` into `iveg`.
        ///
        /// Position 0 only. A mark anywhere else is somebody's argument, not the key that opened the app.
        /// </summary>
        private static void RejectLeadingCharacters(TMP_InputField field, string rejected)
        {
            if (field == null || string.IsNullOrEmpty(rejected)) return;

            string marks = rejected;

            try
            {
                // Converted rather than assigned: OnValidateInput is an Il2Cpp delegate and a managed method group
                // cannot be handed to it. Assigning also REPLACES TMP's own validator, which is fine here because
                // the field leaves characterValidation at None.
                field.onValidateInput = Il2CppInterop.Runtime.DelegateSupport
                    .ConvertDelegate<TMP_InputField.OnValidateInput>(
                        (Func<string, int, char, char>)((text, index, added) =>
                            index == 0 && marks.IndexOf(added) >= 0 ? '\0' : added));
            }
            catch (Exception e)
            {
                Core.Log?.Warning("data-reject-first could not be installed: " + e.Message);
            }
        }

        /// <summary>
        /// Put the caret after the text the page just wrote.
        ///
        /// A browser does this when script assigns <c>input.value</c>, and without it a page that completes a word
        /// for the player leaves the caret wherever it was - so typing the arguments after an accepted completion
        /// inserts them into the middle of the command.
        ///
        /// Two things make this fiddly, and both are load-bearing. The label is forced first: both cursors are
        /// mapped through <c>textInfo.characterInfo</c>, which describes the RENDERED text, and right after the
        /// assignment that is still the old, shorter string - so TMP clamps everything set here back onto it. And
        /// all six positions are set, because a TMP_InputField keeps the caret twice: <c>caretPosition</c> is where
        /// the bar is drawn and <c>stringPosition</c> is where the next character goes. Setting only the visible one
        /// leaves the field looking right and typing wrong, which turns `give` into `iveg`.
        /// </summary>
        private static void PutCaretAtEnd(TMP_InputField field)
        {
            try
            {
                int at = field.text?.Length ?? 0;

                field.ForceLabelUpdate();

                field.caretPosition = at;
                field.selectionAnchorPosition = at;
                field.selectionFocusPosition = at;
                field.stringPosition = at;
                field.selectionStringAnchorPosition = at;
                field.selectionStringFocusPosition = at;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("could not move the caret to the end: " + e.Message);
            }
        }

        private static void SetTyping(bool typing)
        {
            try { Il2CppScheduleOne.GameInput.IsTyping = typing; }
            catch (Exception e) { Core.Log?.Warning("IsTyping toggle failed: " + e.Message); }
        }

        /// <summary>
        /// Drop keyboard focus and put the game's typing flag back, whatever is holding it.
        ///
        /// Called when a page leaves the screen. A field only fires onDeselect when something else takes the
        /// selection, and nothing does when the whole container is switched off - so the flag it raised stays
        /// raised, and a raised flag means the player cannot move and no key reaches the game. Deselecting first is
        /// what makes the field's own handler run; clearing the flag afterwards covers the case where it did not.
        /// </summary>
        internal static void ReleaseKeyboard()
        {
            try
            {
                UnityEngine.EventSystems.EventSystem events = UnityEngine.EventSystems.EventSystem.current;
                if (events != null && events.currentSelectedGameObject != null) events.SetSelectedGameObject(null);
            }
            catch (Exception e)
            {
                Core.Log?.Warning("could not drop keyboard focus: " + e.Message);
            }

            SetTyping(false);
        }

        private static bool IsImage(LayoutNode node) =>
            node.Tag is AngleSharp.Dom.IElement element
            && element.LocalName.Equals("img", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Draw an <c>&lt;img&gt;</c>: the file named by <c>src</c>, resolved against the app's own bundle, inside the
        /// node's content box.
        ///
        /// The box is sized by CSS alone. The layout runs without Unity - it cannot open a PNG, so it cannot know a
        /// picture's intrinsic size the way a browser does. An image with no width and height is therefore a box of
        /// nothing, which is why both are required rather than optional.
        ///
        /// The aspect ratio IS preserved inside whatever box you give it, so stating one dimension and a matching
        /// other is enough; a wrong pair letterboxes rather than stretching.
        /// </summary>
        private static void PaintImage(LayoutNode node, RectTransform box)
        {
            var element = (AngleSharp.Dom.IElement)node.Tag;
            string src = element.GetAttribute("src") ?? "";

            Sprite sprite = ImageCache.Get(_bundle, _appId, src);
            if (sprite == null) return;

            ComputedStyle s = node.Style;
            float padLeft = s.Padding.Left.Resolve(node.Width) + s.BorderWidth.Left.Resolve(node.Width);
            float padRight = s.Padding.Right.Resolve(node.Width) + s.BorderWidth.Right.Resolve(node.Width);
            float padTop = s.Padding.Top.Resolve(node.Width) + s.BorderWidth.Top.Resolve(node.Width);
            float padBottom = s.Padding.Bottom.Resolve(node.Width) + s.BorderWidth.Bottom.Resolve(node.Width);

            RectTransform rect = UiFactory.Rect("img", box);
            UiFactory.Stretch(rect, top: padTop, right: padRight, bottom: padBottom, left: padLeft);

            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.preserveAspect = true;
            image.raycastTarget = false;

            // `color` tints the picture, which is how a white glyph becomes any colour the stylesheet asks for -
            // the cheapest way to ship one image and use it on both a light and a dark bar.
            image.color = new Color(s.Color.R, s.Color.G, s.Color.B, s.Color.A * s.Opacity);

            ClipTo(image, BoxRenderer.ActiveClip);
        }

        private static Il2CppTMPro.TextMeshProUGUI PaintText(LayoutNode node, RectTransform box)
        {
            ComputedStyle s = node.Style;

            // The text sits inside the padding box; the box itself already carries background and border.
            float padLeft = s.Padding.Left.Resolve(node.Width) + s.BorderWidth.Left.Resolve(node.Width);
            float padRight = s.Padding.Right.Resolve(node.Width) + s.BorderWidth.Right.Resolve(node.Width);
            float padTop = s.Padding.Top.Resolve(node.Width) + s.BorderWidth.Top.Resolve(node.Width);
            float padBottom = s.Padding.Bottom.Resolve(node.Width) + s.BorderWidth.Bottom.Resolve(node.Width);

            RectTransform rt = UiFactory.Rect("text", box);
            UiFactory.Stretch(rt, top: padTop, right: padRight, bottom: padBottom, left: padLeft);

            var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
            TmpMeasure.Apply(tmp, s);
            tmp.text = TmpMeasure.Content(node.Text, s);
            tmp.raycastTarget = false;
            ClipTo(tmp, BoxRenderer.ActiveClip);
            return tmp;
        }

        /// <summary>
        /// The rectangle a form control's own text may occupy: its content box, further narrowed by whatever scroll
        /// area it happens to sit inside. Intersecting matters - a field in a scrolled list has to disappear with the
        /// row it belongs to, not keep drawing over the container's edge.
        /// </summary>
        private static Rect? InputClip(LayoutNode node, float absX, float absY,
                                       float padLeft, float padRight, float padTop, float padBottom)
        {
            Rect? own = ClipRectInCanvasSpace(absX + padLeft, absY + padTop,
                                              Math.Max(0f, node.Width - padLeft - padRight),
                                              Math.Max(0f, node.Height - padTop - padBottom));

            return Narrow(own, BoxRenderer.ActiveClip);
        }

        /// <summary>
        /// Clip one graphic to a canvas-space rectangle, the same way the box meshes are clipped.
        ///
        /// Safe to set by hand only because nothing else claims it: a MaskableGraphic resets its CanvasRenderer's
        /// clipping when it LOSES an ancestor RectMask2D, and Sideload no longer creates any. Passing null leaves the
        /// graphic unclipped, which is what an element outside every scroll area wants.
        /// </summary>
        private static void ClipTo(Graphic graphic, Rect? clip)
        {
            if (graphic == null) return;

            // maskable = false FIRST, and it is the load-bearing line.
            //
            // A MaskableGraphic recomputes its clipping whenever the hierarchy moves - which a ScrollRect does on
            // every scroll - and it looks for a RectMask2D to obey. Sideload creates none, but the phone's own app
            // container HAS one, and our content deliberately sits outside it while scrolled. The vanilla mask then
            // culled every image and every line of text the moment the list moved: the mugshots vanished and the
            // rows went blank, while the boxes stayed because they are drawn on Sideload's own material.
            //
            // Opting out of masking leaves the rectangle set below as the only clip, which is the one that is
            // actually right for this page.
            if (graphic is MaskableGraphic maskable) maskable.maskable = false;

            CanvasRenderer renderer = graphic.canvasRenderer;
            if (renderer == null) return;

            if (clip.HasValue) renderer.EnableRectClipping(clip.Value);
            else renderer.DisableRectClipping();
        }

        /// <summary>
        /// Reassert one clip rectangle over a whole subtree.
        ///
        /// Cheap enough for a scroll event: it walks the components once and writes two fields per graphic. Doing
        /// it any less often does not work - see the note where this is hooked up.
        /// </summary>
        private static void Reclip(Transform root, Rect? clip)
        {
            if (root == null) return;

            try
            {
                var graphics = root.GetComponentsInChildren<Graphic>(true);
                for (int i = 0; graphics != null && i < graphics.Length; i++) ClipTo(graphics[i], clip);

                // The box meshes are bare CanvasRenderers with no Graphic on them, so they need reaching directly.
                var renderers = root.GetComponentsInChildren<CanvasRenderer>(true);
                for (int i = 0; renderers != null && i < renderers.Length; i++)
                {
                    CanvasRenderer cr = renderers[i];
                    if (cr == null) continue;

                    if (clip.HasValue) cr.EnableRectClipping(clip.Value);
                    else cr.DisableRectClipping();
                }
            }
            catch
            {
                // The page was torn down mid-scroll. Nothing to reassert and nothing worth logging.
            }
        }

        /// <summary>
        /// Shrink every hit target inside a scroll area to the part of it that is actually visible.
        ///
        /// CLIPPING DOES NOT REACH THE POINTER. `CanvasRenderer.EnableRectClipping` is a rendering instruction and
        /// nothing more - uGUI decides what a click hit by raycasting Graphics, and the only thing that filters that
        /// is a component implementing ICanvasRaycastFilter, which is what RectMask2D is. This engine cannot use
        /// RectMask2D (it collapses on a rotated panel, see BuildScrollArea) and cannot implement the interface
        /// either (a Unity interface on a managed type is the unreliable virtual-override path this file already
        /// rules out for Graphic). So a row that scrolled out of view stayed perfectly clickable at wherever its
        /// rect had moved to - and once a page put fixed chrome above its list, "wherever" was on top of that
        /// chrome. The reported symptom: after scrolling, pressing the filters or the red action pressed a list row
        /// instead.
        ///
        /// The fix needs no interface: the hit target is a child stretched over its element, so insetting it is
        /// enough, and the visible band is a plain vertical range because this engine only scrolls vertically.
        /// Working in the content's own local space keeps it correct under rotation for free - the panel's rotation
        /// is above the content, so nothing here has to know about it.
        /// </summary>
        /// <summary>
        /// The name Interaction gives a hit target. Shared because <see cref="CullHitTargets"/> finds them by it -
        /// two spellings of the same string would leave the culling silently doing nothing.
        /// </summary>
        internal const string HitTargetName = "hit";

        private static void CullHitTargets(Transform content, RectTransform viewport)
        {
            if (content == null || viewport == null) return;

            try
            {
                var contentRect = content as RectTransform;
                if (contentRect == null) return;

                // The band of content the viewport is showing, measured down from the content's own top edge.
                float bandTop = contentRect.anchoredPosition.y;
                float bandBottom = bandTop + viewport.rect.height;

                var hits = content.GetComponentsInChildren<Image>(true);
                for (int i = 0; hits != null && i < hits.Length; i++)
                {
                    Image hit = hits[i];
                    if (hit == null || hit.gameObject.name != HitTargetName) continue;

                    RectTransform rt = hit.rectTransform;
                    var owner = rt.parent as RectTransform;
                    if (owner == null) continue;

                    float top = TopWithin(owner, content);
                    float height = owner.rect.height;

                    float visibleTop = Math.Max(top, bandTop);
                    float visibleBottom = Math.Min(top + height, bandBottom);

                    if (visibleBottom <= visibleTop)
                    {
                        // Entirely outside. Left in place with no raycast rather than resized to nothing, because a
                        // zero-height rect still answers a raycast exactly on its own edge.
                        hit.raycastTarget = false;
                        continue;
                    }

                    hit.raycastTarget = true;

                    // Stretched to the owner, so offsetMax.y is the inset from the top and offsetMin.y from the
                    // bottom. Both are zero for a row that is fully in view, which is the overwhelming majority.
                    rt.offsetMax = new Vector2(rt.offsetMax.x, -(visibleTop - top));
                    rt.offsetMin = new Vector2(rt.offsetMin.x, (top + height) - visibleBottom);
                }
            }
            catch
            {
                // Torn down mid-scroll. Nothing to cull and nothing worth logging.
            }
        }

        /// <summary>
        /// How far an element's top edge sits below the scroll content's top, in the content's own units.
        ///
        /// Every box in a painted page is placed with <see cref="UiFactory.PlaceFromTopLeft"/>, which encodes the
        /// CSS y as a negative anchoredPosition, so walking up and summing is exact rather than approximate.
        /// </summary>
        private static float TopWithin(RectTransform node, Transform content)
        {
            float y = 0f;

            for (RectTransform at = node; at != null && at.transform != content; at = at.parent as RectTransform)
            {
                y += -at.anchoredPosition.y;
                if (at.parent == null) break;
            }

            return y;
        }

        /// <summary>Nothing to draw when the box is fully transparent - skip the mesh instead of adding an invisible one.</summary>
        private static bool Paints(ComputedStyle s) =>
            !s.BackgroundColor.IsTransparent
            || s.HasGradient
            || (s.HasShadow && !s.ShadowColor.IsTransparent)
            || (!s.BorderColor.IsTransparent && MaxBorder(s) > 0f);

        private static float MaxBorder(ComputedStyle s) =>
            Math.Max(Math.Max(s.BorderWidth.Top.Resolve(0f), s.BorderWidth.Right.Resolve(0f)),
                     Math.Max(s.BorderWidth.Bottom.Resolve(0f), s.BorderWidth.Left.Resolve(0f)));

        internal static BoxVisual ToVisual(ComputedStyle s, float width, float height)
        {
            Color fill = ToColor(s.BackgroundColor);

            var visual = new BoxVisual
            {
                FillTL = fill, FillTR = fill, FillBR = fill, FillBL = fill,
                RadiusTL = s.BorderRadius.TopLeft,
                RadiusTR = s.BorderRadius.TopRight,
                RadiusBR = s.BorderRadius.BottomRight,
                RadiusBL = s.BorderRadius.BottomLeft,
                BorderColor = ToColor(s.BorderColor),
                HasShadow = s.HasShadow && !s.ShadowColor.IsTransparent,
                ShadowOffsetX = s.ShadowOffsetX,
                ShadowOffsetY = s.ShadowOffsetY,
                ShadowBlur = s.ShadowBlur,
                ShadowColor = ToColor(s.ShadowColor),
            };

            // A border of equal width all round is the shader's rounded ring, which follows the corner radii. Anything
            // else - the single hairline under a list row, say - becomes solid edge quads instead, because the ring is
            // one number in the vertex payload and cannot describe four different widths.
            float top = SnapBorder(s.BorderWidth.Top.Resolve(width)), right = SnapBorder(s.BorderWidth.Right.Resolve(width));
            float bottom = SnapBorder(s.BorderWidth.Bottom.Resolve(width)), left = SnapBorder(s.BorderWidth.Left.Resolve(width));

            bool uniform = top == right && right == bottom && bottom == left;

            /*
              A UNIFORM BORDER OVER A TRANSPARENT FILL DOES NOT DRAW AS A RING, so it is drawn as four strips instead.

              The ring lives in the same quad as the fill and is modulated by that quad's vertex colour, so with no
              background there is nothing for it to modulate and the border disappears entirely. Every border in a
              real page confirmed the rule: single-sided ones drew (they were already strips), bordered inputs and
              buttons drew (they have a fill), and outlined chips with no background drew nothing at all - which is
              how an app shipped with `border: 1px solid` on its state chips and no outline anywhere on screen.

              Only when the corners are square. Four strips cannot follow a radius, and a rounded box with a
              transparent fill is better served by the ring being subtly wrong than by its corners being cut off.
            */
            bool fillCarriesTheRing = !s.BackgroundColor.IsTransparent || s.HasGradient;
            bool squared = s.BorderRadius.TopLeft <= 0f && s.BorderRadius.TopRight <= 0f
                        && s.BorderRadius.BottomRight <= 0f && s.BorderRadius.BottomLeft <= 0f;

            if (uniform && (fillCarriesTheRing || !squared))
            {
                visual.BorderWidth = top;
            }
            else
            {
                visual.EdgeTop = top;
                visual.EdgeRight = right;
                visual.EdgeBottom = bottom;
                visual.EdgeLeft = left;
            }

            if (s.HasGradient) ApplyGradient(ref visual, s, width, height);

            if (s.Opacity < 1f)
            {
                visual.FillTL = Fade(visual.FillTL, s.Opacity);
                visual.FillTR = Fade(visual.FillTR, s.Opacity);
                visual.FillBR = Fade(visual.FillBR, s.Opacity);
                visual.FillBL = Fade(visual.FillBL, s.Opacity);
                visual.BorderColor = Fade(visual.BorderColor, s.Opacity);
                visual.ShadowColor = Fade(visual.ShadowColor, s.Opacity);
            }

            return visual;
        }

        /// <summary>
        /// Evaluates the gradient at the four corners. Because a linear gradient is an affine function of position and
        /// bilinear interpolation reproduces affine functions exactly, the rasteriser then draws the true gradient -
        /// no per-pixel work, no texture.
        /// </summary>
        private static void ApplyGradient(ref BoxVisual visual, ComputedStyle s, float width, float height)
        {
            // CSS angles: 0deg points up, 90deg right, 180deg down. Screen y grows downwards.
            float radians = s.GradientAngleDeg * (float)Math.PI / 180f;
            var dir = new Vector2((float)Math.Sin(radians), -(float)Math.Cos(radians));

            float halfSpan = (Math.Abs(width * dir.x) + Math.Abs(height * dir.y)) * 0.5f;
            if (halfSpan < 0.001f) halfSpan = 0.001f;

            Color from = ToColor(s.GradientFrom);
            Color to = ToColor(s.GradientTo);

            Color At(float x, float y)
            {
                float projection = x * dir.x + y * dir.y;
                float t = Math.Clamp((projection + halfSpan) / (2f * halfSpan), 0f, 1f);
                return Color.Lerp(from, to, t);
            }

            float hw = width * 0.5f, hh = height * 0.5f;
            visual.FillTL = At(-hw, -hh);
            visual.FillTR = At(hw, -hh);
            visual.FillBR = At(hw, hh);
            visual.FillBL = At(-hw, hh);
        }

        private static Color ToColor(RgbaColor c) => new Color(c.R, c.G, c.B, c.A);

        private static Color Fade(Color c, float factor) => new Color(c.r, c.g, c.b, c.a * factor);

        private static string NameOf(LayoutNode node, int depth)
        {
            if (node.Tag is AngleSharp.Dom.IElement element)
            {
                string id = element.GetAttribute("id");
                return string.IsNullOrEmpty(id) ? element.LocalName : element.LocalName + "#" + id;
            }
            return "node" + depth;
        }

        /// <summary>
        /// Round a border width up to a whole number of DEVICE pixels.
        ///
        /// The panel draws css pixels at about 1.64 device pixels, so a 1px border lands on 1.64 of them: it is
        /// antialiased across two rows and each row gets a fraction of the colour. In a browser the same declaration
        /// is one crisp row. The visible result is a hairline that reads on a design and disappears in the game -
        /// an outlined button whose label ends up floating with no button around it, which is exactly how this was
        /// found.
        ///
        /// Snapping the WIDTH rather than the position is enough because every border here is drawn from the box
        /// edge inwards. The floor of one device pixel keeps a sub-pixel border visible instead of letting it round
        /// away to nothing; a zero width still means "no border" and is passed through untouched.
        /// </summary>
        internal static float SnapBorder(float cssWidth)
        {
            if (cssWidth <= 0f) return 0f;
            float scale = CssToDevice;
            if (scale <= 0f) return cssWidth;
            float devicePixels = Mathf.Max(1f, Mathf.Round(cssWidth * scale));
            return devicePixels / scale;
        }

        /// <summary>How many device pixels one css pixel covers, published by the view that is currently painting.
        /// One number for the whole pass - every box in a page is drawn at the same scale.</summary>
        internal static float CssToDevice = 1f;
    }
}
