namespace Sideload.Css
{
    /// <summary>
    /// The resolved style of one element: every property the v1 engine understands, already reduced to concrete
    /// values. There is no separate "specified" stage - the cascade mutates this object in ascending priority order,
    /// starting from the inherited/initial baseline, which is exactly what CSS describes and keeps the pipeline short.
    /// </summary>
    internal sealed class ComputedStyle
    {
        // --- layout ---
        internal DisplayKind Display = DisplayKind.Flex;
        internal FlexDirection FlexDirection = DefaultDirection;

        /// <summary>
        /// Which way an undeclared box stacks its children.
        ///
        /// Column, because every box here is a flex container and a block-like stack is what unstyled HTML looks
        /// like. CSS says a flex container defaults to ROW - but CSS also has block layout, and this engine does
        /// not, so copying the row default would make plain markup lay out sideways.
        ///
        /// A page can ask for the web's answer instead with `&lt;meta name="sideload" content="web-defaults"&gt;`,
        /// which is what anything built by a web toolchain wants: Tailwind's `.flex` means a row and says so
        /// nowhere, because in a browser it does not have to. Opt-in rather than a new default, because the
        /// fourteen shipped apps are all written against the column and flipping it would silently reflow every
        /// box they did not think to declare.
        /// </summary>
        internal static FlexDirection DefaultDirection = FlexDirection.Column;
        internal FlexWrap FlexWrap = FlexWrap.NoWrap;
        internal float FlexGrow = 0f;
        internal float FlexShrink = 1f;
        internal Len FlexBasis = Len.Auto;
        internal Justify JustifyContent = Justify.FlexStart;
        internal AlignKind AlignItems = AlignKind.Stretch;
        internal AlignKind AlignSelf = AlignKind.Auto;
        internal Len RowGap = Len.Zero;
        internal Len ColumnGap = Len.Zero;

        // --- grid ---------------------------------------------------------------------------------------------
        // Only read when Display is Grid. They sit next to the flex properties rather than in a side object
        // because ComputedStyle is copied per element per render, and a second allocation per box for fields
        // that are null on almost every one of them costs more than the eight slots do.

        /// <summary>`grid-template-columns`. Null means `none`: no explicit columns, so every column is implicit.</summary>
        internal GridTemplate GridTemplateColumns;

        /// <summary>`grid-template-rows`. Null means `none`.</summary>
        internal GridTemplate GridTemplateRows;

        /// <summary>How a row past the end of the explicit grid is sized. `auto` by default, as in CSS.</summary>
        internal GridTrack GridAutoRows = GridTrack.Auto;

        internal GridTrack GridAutoColumns = GridTrack.Auto;

        internal GridPlacement GridColumn;
        internal GridPlacement GridRow;

        /// <summary>The inline-axis counterpart of <see cref="AlignItems"/> for a grid container.</summary>
        internal AlignKind JustifyItems = AlignKind.Stretch;

        internal AlignKind JustifySelf = AlignKind.Auto;

        internal Edges Padding = Edges.Zero;
        internal Edges Margin = Edges.Zero;

        internal Len Width = Len.Auto;
        internal Len Height = Len.Auto;
        internal Len MinWidth = Len.None;
        internal Len MinHeight = Len.None;
        internal Len MaxWidth = Len.None;
        internal Len MaxHeight = Len.None;

        internal PositionKind Position = PositionKind.Static;
        internal Edges Inset = new Edges { Top = Len.Auto, Right = Len.Auto, Bottom = Len.Auto, Left = Len.Auto };

        /// <summary>
        /// The stack level this box paints on among its siblings. Null is `auto` - the default, and document order.
        ///
        /// Only meaningful on a POSITIONED box: CSS gives a static box no stack level, and
        /// <see cref="Layout.StackingOrder"/> follows that and says so rather than ignoring the declaration quietly.
        ///
        /// Not inherited, and deliberately absent from <see cref="CreateFrom"/>. A z-index that cascaded would put a
        /// whole subtree on the level its container asked for, and the children of a lifted panel could no longer be
        /// ordered against each other at all.
        /// </summary>
        internal int? ZIndex;

        internal OverflowKind OverflowX = OverflowKind.Visible;
        internal OverflowKind OverflowY = OverflowKind.Visible;

        // --- paint ---
        internal RgbaColor BackgroundColor = RgbaColor.Transparent;
        internal bool HasGradient;
        internal RgbaColor GradientFrom = RgbaColor.Transparent;
        internal RgbaColor GradientTo = RgbaColor.Transparent;
        internal float GradientAngleDeg = 180f;   // CSS default: top to bottom

        internal Edges BorderWidth = Edges.Zero;
        internal RgbaColor BorderColor = RgbaColor.Transparent;
        internal Corners BorderRadius;

        internal bool HasShadow;
        internal float ShadowOffsetX, ShadowOffsetY, ShadowBlur;
        internal RgbaColor ShadowColor = RgbaColor.Transparent;

        internal float Opacity = 1f;

        // --- transform ------------------------------------------------------------------------------------------
        // Applied to the RectTransform after layout, never to it. That is the whole reason transforms are safe to
        // animate: moving, scaling or rotating a box cannot change where any other box sits.
        internal float TranslateX, TranslateY;
        internal float ScaleX = 1f, ScaleY = 1f;
        internal float RotateDeg;

        internal bool HasTransform =>
            TranslateX != 0f || TranslateY != 0f || ScaleX != 1f || ScaleY != 1f || RotateDeg != 0f;

        // --- transition -----------------------------------------------------------------------------------------
        /// <summary>Seconds a state change takes to arrive. Zero means instantly, which is the default and what
        /// every page that says nothing gets.</summary>
        internal float TransitionSeconds;

        internal float TransitionDelaySeconds;

        internal EasingKind TransitionEasing = EasingKind.EaseOut;

        // --- text (all inherited) ---
        internal string FontFamily = "game-ui";
        internal float FontSize = 15f;
        internal int FontWeight = 400;
        internal FontStyleKind FontStyle = FontStyleKind.Normal;
        internal Len LineHeight = Len.None;        // none = 1.2 x font-size, the engine default

        /// <summary>`pointer-events: none` - the box is there to be looked at, not touched. Inherited, as CSS has
        /// it, so a whole overlay opts out with one rule on its root.</summary>
        internal bool PointerEventsNone;

        internal TextTransformKind TextTransform = TextTransformKind.None;

        /// <summary>
        /// `text-decoration-line` - the underline or strike on a run of text.
        ///
        /// Inherited here, which CSS does NOT say: there the property sits on one box and PROPAGATES to the inline
        /// boxes inside it, and the difference shows only when a descendant tries to take the line off again, which
        /// CSS forbids. Inheriting gets the propagation for nothing, and this renderer has no inline boxes for the
        /// spec's version to act on anyway - a decorated run is one TextMeshPro component with a style flag.
        /// </summary>
        internal TextDecorationKind TextDecoration = TextDecorationKind.None;

        /// <summary>Where the wrapped lines sit. Not inherited, like every other flex property.</summary>
        internal AlignContentKind AlignContent = AlignContentKind.FlexStart;

        /// <summary>How an `img` fills its box. `contain` is what this renderer has always done, so it is the
        /// default here rather than CSS's `fill` - changing that would restretch every picture in every app.</summary>
        internal ObjectFitKind ObjectFit = ObjectFitKind.Contain;

        /// <summary>
        /// The point `scale` and `rotate` turn around, as a length or a percentage of the box.
        ///
        /// 50% 50% is the CSS default, and getting it wrong is not subtle: this renderer places every box from its
        /// top-left corner, so before this existed every scale grew out of the corner and every rotation swung
        /// around it. NOT inherited - a transform belongs to one box.
        /// </summary>
        internal Len TransformOriginX = Len.Percent(50f);

        internal Len TransformOriginY = Len.Percent(50f);
        internal float LetterSpacing = 0f;

        /// <summary>
        /// Advance every glyph gets, in CSS pixels - what the web calls a monospace font and what none of the game's
        /// fonts are. Zero, the default, leaves the font's own metrics alone.
        ///
        /// There is no monospace family to switch to: `font-family` resolves against the five TextMeshPro assets the
        /// game ships, and every one of them is proportional. Forcing the advance is the only way a column of values
        /// can line up under a heading, which is the difference between a terminal and a list of words.
        /// </summary>
        internal float MonoAdvance = 0f;

        /// <summary>False for `-s1-scroll: instant`. A wheel notch is eased by default; a box that follows the
        /// pointer - a map, a canvas - wants the jump, because there smoothing is lag.</summary>
        internal bool SmoothScroll = true;

        // --- generated content ---

        /// <summary>
        /// What a generated box carries: the text `content` resolved to, or null when it was never set or set to
        /// `none`.
        ///
        /// Null against empty is the whole distinction CSS rests a pseudo-element on. Without `content` there is no
        /// box at all; with `content: ""` there is a box carrying nothing, which is the badge dot, the divider and
        /// the overlay - so the empty string is a value here and not the absence of one.
        ///
        /// Stored on every style and read only for `::before` and `::after`. On an ordinary element `content` does
        /// nothing, which is also what it does in a browser.
        /// </summary>
        internal string Content;

        internal TextAlignKind TextAlign = TextAlignKind.Left;
        internal WhiteSpaceKind WhiteSpace = WhiteSpaceKind.Normal;

        /// <summary>
        /// Whether a word that does not fit may be cut. Together with <see cref="WordBreak"/> this is the third
        /// wrapping state: TextMeshPro offers "wrap anywhere" and "never wrap", and a browser's DEFAULT is neither
        /// of them - break at word boundaries, and when a single word is wider than the box, let it overflow.
        /// See <see cref="WrapsWholeWords"/> for how the three fold back into TMP's one boolean.
        /// </summary>
        internal OverflowWrapKind OverflowWrap = OverflowWrapKind.Normal;

        internal WordBreakKind WordBreak = WordBreakKind.Normal;

        /// <summary>
        /// Whether this text keeps its words whole - the browser default, and false as soon as the stylesheet
        /// asks for the opposite in either of the two properties that can.
        ///
        /// `overflow-wrap: break-word` is in the false group deliberately: it says a word MAY be cut when it does
        /// not fit on a line of its own, which is exactly what TextMeshPro already does, so it needs nothing added.
        /// `keep-all` is about CJK, where this engine has no line-breaking rules to keep, and behaves as normal.
        /// </summary>
        internal bool WrapsWholeWords =>
            OverflowWrap == OverflowWrapKind.Normal && WordBreak != WordBreakKind.BreakAll;
        internal RgbaColor Color = new RgbaColor(0.925f, 0.929f, 0.945f, 1f);   // --text
        internal bool TextOverflowEllipsis;

        // --- caret (inherited, so a terminal sets it once on the shell and every field inside obeys) ---

        /// <summary>The colour of the insertion point. Unset means it follows <see cref="Color"/>, which is what a
        /// field wants when nobody has said otherwise.</summary>
        internal RgbaColor? CaretColor;

        /// <summary>
        /// How wide the insertion point is drawn, in CSS pixels.
        ///
        /// Two is a text cursor. Set it to one character cell and it is a block, which is what a terminal wants and
        /// what nothing else in CSS can express - there is a standard `caret-color` but no standard caret width.
        /// </summary>
        internal float CaretWidth = 2f;

        /// <summary>
        /// The colour of the inline suggestion drawn behind the caret - what `data-ghost` puts there.
        ///
        /// Unset means the text colour at 45% alpha, which is the muted grey every shell uses for this. Inherited
        /// like the caret properties, so a terminal sets it once on the shell.
        /// </summary>
        internal RgbaColor? GhostColor;

        /// <summary>Custom properties in effect on this element. Inherited, so `var()` works on descendants.</summary>
        internal Dictionary<string, string> Variables;

        /// <summary>Line height in pixels, applying the engine default when the property is unset.</summary>
        /// <summary>A shallow copy. Every field is a value type or a string, so this is a complete copy - used by the
        /// transition runner to hold a style that is part-way between two others.</summary>
        internal ComputedStyle Clone() => (ComputedStyle)MemberwiseClone();

        internal float ResolvedLineHeight => LineHeight.IsDefinite ? LineHeight.Resolve(FontSize) : FontSize * 1.2f;

        /// <summary>The style an element starts from: initial values, plus the inherited ones taken from its parent.</summary>
        internal static ComputedStyle CreateFrom(ComputedStyle parent)
        {
            var s = new ComputedStyle();
            if (parent == null)
            {
                s.Variables = new Dictionary<string, string>(StringComparer.Ordinal);
                s._sharedVariables = false;   // the root owns its table outright
                return s;
            }

            s.FlexDirection = DefaultDirection;
            s.FontFamily = parent.FontFamily;
            s.FontSize = parent.FontSize;
            s.FontWeight = parent.FontWeight;
            s.FontStyle = parent.FontStyle;
            s.LineHeight = parent.LineHeight;
            s.PointerEventsNone = parent.PointerEventsNone;
            s.TextTransform = parent.TextTransform;
            s.TextDecoration = parent.TextDecoration;
            s.LetterSpacing = parent.LetterSpacing;
            s.MonoAdvance = parent.MonoAdvance;
            s.SmoothScroll = parent.SmoothScroll;
            s.CaretColor = parent.CaretColor;
            s.GhostColor = parent.GhostColor;
            s.CaretWidth = parent.CaretWidth;
            s.TextAlign = parent.TextAlign;
            s.WhiteSpace = parent.WhiteSpace;
            s.OverflowWrap = parent.OverflowWrap;
            s.WordBreak = parent.WordBreak;
            s.Color = parent.Color;

            // Copy-on-write: children share the parent's table until one of them declares a variable of its own.
            s.Variables = parent.Variables;
            return s;
        }

        /// <summary>Declare a custom property, detaching this element's variable table from the parent's on first write.</summary>
        internal void SetVariable(string name, string value)
        {
            if (Variables == null) Variables = new Dictionary<string, string>(StringComparer.Ordinal);
            else if (_sharedVariables) { Variables = new Dictionary<string, string>(Variables, StringComparer.Ordinal); }
            _sharedVariables = false;
            Variables[name] = value;
        }

        private bool _sharedVariables = true;
    }
}
