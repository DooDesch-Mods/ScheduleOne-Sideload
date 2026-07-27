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

            internal PaintedBox(LayoutNode node, RectTransform rect, Il2CppTMPro.TextMeshProUGUI text = null)
            {
                Node = node;
                Rect = rect;
                Text = text;
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
        private static Dictionary<AngleSharp.Dom.IElement, RectTransform> _reuse;

        /// <summary><paramref name="reuse"/> maps elements whose GameObject must SURVIVE this pass onto that object.
        /// Only form controls qualify: a TMP_InputField owns the caret, the selection and the half-typed word, and all
        /// three are gone the moment it is recreated - which is what made every keystroke swallow itself.</summary>
        internal static Dictionary<AngleSharp.Dom.IElement, PaintedBox> Paint(
            LayoutNode root, RectTransform host, Vector2 viewSize,
            Dictionary<AngleSharp.Dom.IElement, TMP_InputField> inputs,
            Action<AngleSharp.Dom.IElement, string> inputChanged,
            Action<AngleSharp.Dom.IElement, string> inputSubmitted,
            Dictionary<AngleSharp.Dom.IElement, RectTransform> reuse = null)
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
            BoxRenderer.ActiveClip = null;
            BoxRenderer.BeginPass(host.GetInstanceID());
            try { PaintNode(root, host, 0, painted, 0f, 0f); }
            finally
            {
                BoxRenderer.EndPass();
                _reuse = null;
                _inputs = null;
                _inputChanged = null;
                _inputSubmitted = null;
            }

            return painted;
        }

        /// <summary><paramref name="absX"/>/<paramref name="absY"/> accumulate the node's position from the view root,
        /// in CSS pixels with y growing downwards - the clip rectangle is derived from these instead of from a
        /// RectTransform, which has no usable rect until the canvas has laid it out.</summary>
        private static void PaintNode(LayoutNode node, Transform parent, int depth,
                                      Dictionary<AngleSharp.Dom.IElement, PaintedBox> painted,
                                      float absX, float absY)
        {
            if (node.Style.Display == DisplayKind.None) return;

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

            if (node.IsTextLeaf)
            {
                Il2CppTMPro.TextMeshProUGUI text = PaintText(node, rt);
                if (node.Tag is AngleSharp.Dom.IElement leaf) painted[leaf] = new PaintedBox(node, rt, text);
                return;
            }

            if (!NeedsScrolling(node))
            {
                foreach (LayoutNode child in node.Children)
                    PaintNode(child, rt, depth + 1, painted, absX, absY);
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

            clip = ClipRectInCanvasSpace(absX, absY, node.Width, node.Height);

            Core.Log?.Msg($"[Sideload] scroll area at ({absX:0.#},{absY:0.#}) size {node.Width:0.#}x{node.Height:0.#}, " +
                          $"content {ContentBottom(node):0.#}, clip={clip}");

            // Devtools.GraphicProbe.Spawn(viewport) went here for the injected-Graphic experiment. Result is recorded
            // in GraphicProbe itself; leaving it enabled paints a magenta quad over the page.
            return content;
        }

        /// <summary>
        /// Repaint one box with a freshly computed style. Used when an interaction state changes: only the box's
        /// appearance is redone, never the layout - which is why state rules are documented as paint-only.
        /// </summary>
        internal static void Repaint(PaintedBox box, ComputedStyle style)
        {
            if (box.Rect == null || style == null) return;

            BoxRenderer.Paint(box.Rect, ToVisual(style, box.Node.Width, box.Node.Height), box.Node.Width, box.Node.Height);
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

            BoxRenderer.Paint(box.Rect, visual, w, h);

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
                if (!string.Equals(field.text, wanted, StringComparison.Ordinal)) field.text = wanted;

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
            field.customCaretColor = true;
            field.caretColor = new Color(s.Color.R, s.Color.G, s.Color.B, 1f);
            field.caretWidth = 2;
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
                catch (Exception e) { Core.Log?.Warning("[Sideload] submit handler failed: " + e.Message); }
            }));

            field.onSelect.AddListener((UnityEngine.Events.UnityAction<string>)(_ => SetTyping(true)));
            field.onDeselect.AddListener((UnityEngine.Events.UnityAction<string>)(_ => SetTyping(false)));

            // Mirroring every keystroke onto the element is what makes `el.value` work from script AND what lets a
            // rebuild restore the text: the DOM, not the Unity component, is the source of truth for what was typed.
            field.onValueChanged.AddListener((UnityEngine.Events.UnityAction<string>)(v =>
            {
                try { changed?.Invoke(element, v); }
                catch (Exception e) { Core.Log?.Warning("[Sideload] input handler failed: " + e.Message); }
            }));

            if (_inputs != null) _inputs[element] = field;
        }

        private static void SetTyping(bool typing)
        {
            try { Il2CppScheduleOne.GameInput.IsTyping = typing; }
            catch (Exception e) { Core.Log?.Warning("[Sideload] IsTyping toggle failed: " + e.Message); }
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
            tmp.text = node.Text;
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
            Rect? outer = BoxRenderer.ActiveClip;
            if (!own.HasValue) return outer;
            if (!outer.HasValue) return own;

            Rect a = own.Value, b = outer.Value;
            float xMin = Math.Max(a.xMin, b.xMin), xMax = Math.Min(a.xMax, b.xMax);
            float yMin = Math.Max(a.yMin, b.yMin), yMax = Math.Min(a.yMax, b.yMax);
            return new Rect(xMin, yMin, Math.Max(0f, xMax - xMin), Math.Max(0f, yMax - yMin));
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

            CanvasRenderer renderer = graphic.canvasRenderer;
            if (renderer == null) return;

            if (clip.HasValue) renderer.EnableRectClipping(clip.Value);
            else renderer.DisableRectClipping();
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
            float top = s.BorderWidth.Top.Resolve(width), right = s.BorderWidth.Right.Resolve(width);
            float bottom = s.BorderWidth.Bottom.Resolve(width), left = s.BorderWidth.Left.Resolve(width);

            if (top == right && right == bottom && bottom == left)
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
    }
}
