using Sideload.Model;

namespace Sideload.Css
{
    /// <summary>
    /// Applies one declaration to a <see cref="ComputedStyle"/>, expanding shorthands on the way. This is where the
    /// engine's supported property set is actually defined: an unknown property, or a value that does not parse, is
    /// dropped - the same "ignore the bad declaration, keep the rest" behaviour a browser has.
    ///
    /// A browser drops it silently because a browser implements the property. This one often does not, and then a
    /// silent drop is a page that comes out wrong with nothing anywhere to say why. So every drop is reported to
    /// <see cref="Diagnostics"/>: the unknown NAME (which the host has warned about since 1.9.0), and now also the
    /// unreadable VALUE - `padding: 1rem`, `color: oklch(...)`, `width: calc(...)` - which is the far more common
    /// case once a page comes from a build tool rather than from hand.
    /// </summary>
    internal static class StyleApplier
    {
        /// <summary>
        /// The property currently being applied, so the parse helpers below can name it without every one of the
        /// fifty call sites having to repeat it.
        ///
        /// A static field rather than a parameter because everything here runs on one thread - Unity's main thread
        /// in the game, the single test thread headless - which the engine relies on throughout. <see cref="Apply"/>
        /// is never re-entered, so there is no nesting to get wrong.
        /// </summary>
        private static string _applying;

        /// <summary>
        /// The font size and viewport a relative length is measured against, published by the cascade before it
        /// applies an element's declarations.
        ///
        /// Static for the same reason as <see cref="_applying"/>: one thread, no re-entry. It starts at the
        /// engine's own defaults so a caller with no element in hand - <see cref="Supports"/>, a test, the sheet
        /// audit - still gets sensible answers instead of dividing by an uninitialised zero.
        /// </summary>
        internal static LengthContext Context = LengthContext.Default;

        /// <summary>The text colour `currentColor` resolves to, set per declaration from the style being built.</summary>
        private static RgbaColor CurrentColor = new RgbaColor(0.925f, 0.929f, 0.945f, 1f);

        /// <summary>
        /// Apply one declaration. Returns FALSE when this renderer has no case for that property - which is the
        /// only signal that exists, because an unsupported declaration is otherwise dropped without a trace.
        /// <see cref="Supports"/> is the same switch asked the same question, so the two can never drift.
        /// </summary>
        internal static bool Apply(ComputedStyle s, string property, string value)
        {
            if (s == null || string.IsNullOrEmpty(property) || value == null) return true;

            property = property.Trim().ToLowerInvariant();
            value = value.Trim();
            if (value.Length == 0) return true;

            _applying = property;

            // What `currentColor` means right here. Read off the style BEFORE this declaration lands, which is
            // what makes `border-color: currentColor` take the inherited text colour rather than chase itself.
            CurrentColor = s.Color;

            DeadValues.Check(property, value);

            switch (property)
            {
                // ---------------------------------------------------------------- layout --
                case "display":
                    if (Is(value, "none")) s.Display = DisplayKind.None;
                    else if (Is(value, "grid") || Is(value, "inline-grid")) s.Display = DisplayKind.Grid;
                    else if (Is(value, "flex") || Is(value, "block") || Is(value, "inline-block")) s.Display = DisplayKind.Flex;
                    break;

                case "flex-direction": s.FlexDirection = ParseDirection(value, s.FlexDirection); break;
                case "flex-wrap": s.FlexWrap = ParseWrap(value, s.FlexWrap); break;
                case "flex-flow":
                    foreach (string part in ValueParser.SplitTopLevel(value))
                    {
                        s.FlexDirection = ParseDirection(part, s.FlexDirection);
                        s.FlexWrap = ParseWrap(part, s.FlexWrap);
                    }
                    break;

                case "flex": ApplyFlex(s, value); break;
                case "flex-grow": if (Number(value, out float g)) s.FlexGrow = g; break;
                case "flex-shrink": if (Number(value, out float sh)) s.FlexShrink = sh; break;
                case "flex-basis": if (Length(value, out Len fb)) s.FlexBasis = fb; break;

                case "justify-content": s.JustifyContent = ParseJustify(value, s.JustifyContent); break;
                case "align-items": s.AlignItems = ParseAlign(value, s.AlignItems); break;
                case "align-self": s.AlignSelf = ParseAlign(value, s.AlignSelf); break;

                case "gap":
                {
                    string[] p = ValueParser.SplitTopLevel(value);
                    bool tookGap = false;
                    if (p.Length >= 1 && ValueParser.TryLength(p[0], Context, out Len rg)) { s.RowGap = rg; s.ColumnGap = rg; tookGap = true; }
                    if (p.Length >= 2 && ValueParser.TryLength(p[1], Context, out Len cg)) { s.ColumnGap = cg; tookGap = true; }
                    if (!tookGap) Diagnostics.Report(DiagnosticKind.ValueRejected, property, value);
                    break;
                }
                case "row-gap": if (Length(value, out Len rgap)) s.RowGap = rgap; break;
                case "column-gap": if (Length(value, out Len cgap)) s.ColumnGap = cgap; break;

                // ------------------------------------------------------------------ grid --
                //
                // `gap` above is shared with flexbox rather than duplicated - it is one property in CSS and one
                // pair of fields here, and which algorithm reads them is the only difference.

                case "grid-template-columns": if (Template(value, out GridTemplate tc)) s.GridTemplateColumns = tc; break;
                case "grid-template-rows": if (Template(value, out GridTemplate tr)) s.GridTemplateRows = tr; break;

                case "grid-template": ApplyGridTemplate(s, value); break;

                case "grid-auto-columns": if (Track(value, out GridTrack ac)) s.GridAutoColumns = ac; break;
                case "grid-auto-rows": if (Track(value, out GridTrack ar)) s.GridAutoRows = ar; break;

                // Only the default `row` flow is implemented. `column` and `dense` are separate placement
                // algorithms rather than variations on this one, and DeadValues names them.
                case "grid-auto-flow": break;

                // Named areas are the other placement model entirely, built on names this engine does not carry.
                // The case exists so the property is RECOGNISED and can be reported as ignored, the same way
                // `border-style` is - without it the name alone would look unimplemented and the value would
                // never be mentioned.
                case "grid-template-areas": break;

                case "grid-column": if (Placement(value, out GridPlacement gc)) s.GridColumn = gc; break;
                case "grid-row": if (Placement(value, out GridPlacement gr)) s.GridRow = gr; break;

                case "grid-column-start": if (Line(value, out GridLine cs2)) s.GridColumn = new GridPlacement(cs2, s.GridColumn.End); break;
                case "grid-column-end": if (Line(value, out GridLine ce)) s.GridColumn = new GridPlacement(s.GridColumn.Start, ce); break;
                case "grid-row-start": if (Line(value, out GridLine rs)) s.GridRow = new GridPlacement(rs, s.GridRow.End); break;
                case "grid-row-end": if (Line(value, out GridLine re)) s.GridRow = new GridPlacement(s.GridRow.Start, re); break;

                case "grid-area": ApplyGridArea(s, value); break;

                case "justify-items": s.JustifyItems = ParseAlign(value, s.JustifyItems); break;
                case "justify-self": s.JustifySelf = ParseAlign(value, s.JustifySelf); break;

                // `place-items: center` and `place-self: end` are one word for both axes; two words set the
                // block axis first, as CSS orders them.
                case "place-items":
                case "place-self":
                {
                    string[] p = ValueParser.SplitTopLevel(value);
                    if (p.Length == 0 || p.Length > 2) { Diagnostics.Report(DiagnosticKind.ValueRejected, property, value); break; }

                    AlignKind block = ParseAlign(p[0], AlignKind.Auto);
                    AlignKind inline = ParseAlign(p.Length == 2 ? p[1] : p[0], AlignKind.Auto);
                    if (block == AlignKind.Auto || inline == AlignKind.Auto)
                    {
                        // Half a shorthand applied is worse than none of it: the half that landed is invisible.
                        Diagnostics.Report(DiagnosticKind.ValueRejected, property, value);
                        break;
                    }

                    if (property == "place-items") { s.AlignItems = block; s.JustifyItems = inline; }
                    else { s.AlignSelf = block; s.JustifySelf = inline; }
                    break;
                }

                case "padding": if (Edges_(value, out Edges pad)) s.Padding = pad; break;
                case "padding-top": if (Length(value, out Len pt)) s.Padding.Top = pt; break;
                case "padding-right": if (Length(value, out Len pr)) s.Padding.Right = pr; break;
                case "padding-bottom": if (Length(value, out Len pb)) s.Padding.Bottom = pb; break;
                case "padding-left": if (Length(value, out Len pl)) s.Padding.Left = pl; break;

                case "margin": if (Edges_(value, out Edges mar)) s.Margin = mar; break;
                case "margin-top": if (Length(value, out Len mt)) s.Margin.Top = mt; break;
                case "margin-right": if (Length(value, out Len mr)) s.Margin.Right = mr; break;
                case "margin-bottom": if (Length(value, out Len mb)) s.Margin.Bottom = mb; break;
                case "margin-left": if (Length(value, out Len ml)) s.Margin.Left = ml; break;

                case "width": if (Length(value, out Len w)) s.Width = w; break;
                case "height": if (Length(value, out Len h)) s.Height = h; break;
                case "min-width": if (Length(value, out Len mnw)) s.MinWidth = mnw; break;
                case "min-height": if (Length(value, out Len mnh)) s.MinHeight = mnh; break;
                case "max-width": if (Length(value, out Len mxw)) s.MaxWidth = mxw; break;
                case "max-height": if (Length(value, out Len mxh)) s.MaxHeight = mxh; break;

                case "position":
                    if (Is(value, "fixed")) s.Position = PositionKind.Fixed;
                    else if (Is(value, "absolute")) s.Position = PositionKind.Absolute;
                    else if (Is(value, "relative")) s.Position = PositionKind.Relative;
                    else if (Is(value, "static")) s.Position = PositionKind.Static;
                    break;

                case "inset": if (Edges_(value, out Edges inset)) s.Inset = inset; break;
                case "top": if (Length(value, out Len it)) s.Inset.Top = it; break;
                case "right": if (Length(value, out Len ir)) s.Inset.Right = ir; break;
                case "bottom": if (Length(value, out Len ib)) s.Inset.Bottom = ib; break;
                case "left": if (Length(value, out Len il)) s.Inset.Left = il; break;

                // An integer only, as in CSS: `z-index: 1.5` is invalid there and is dropped here with a word about
                // it. Whether the box may use the level is not decided until the whole rule has been applied - a
                // sheet is free to write `z-index` before `position` - so that question belongs to the paint order
                // and is answered in Layout/StackingOrder, which also reports the declarations it has to ignore.
                case "z-index":
                    if (Is(value, "auto")) s.ZIndex = null;
                    else if (Integer(value, out int z)) s.ZIndex = z;
                    break;

                case "overflow": s.OverflowX = s.OverflowY = ParseOverflow(value, s.OverflowX); break;
                case "transform-origin": ApplyTransformOrigin(s, value); break;

                case "object-fit":
                    // `contain` is what this renderer has always done with an <img>, so taking it in silence is
                    // the truth - and all three uses in the shipped apps are exactly that, written to make the
                    // BROWSER match the game. `cover` and `none` crop, and cropping needs a clip rectangle of the
                    // image's own rather than the ancestor's, so those two say so instead of pretending.
                    if (Is(value, "contain")) s.ObjectFit = ObjectFitKind.Contain;
                    else if (Is(value, "fill")) s.ObjectFit = ObjectFitKind.Fill;
                    else if (Is(value, "scale-down")) s.ObjectFit = ObjectFitKind.ScaleDown;
                    else if (Is(value, "cover") || Is(value, "none"))
                        Diagnostics.Report(DiagnosticKind.ValueIgnored, "object-fit", value);
                    else Diagnostics.Report(DiagnosticKind.ValueRejected, "object-fit", value);
                    break;

                case "align-content":
                    // `flex-start` is what this engine already does, so taking it in silence is the truth. An
                    // explicit `stretch` is the one value it cannot honour - growing a line means laying its
                    // items out again - and that one says so.
                    if (Is(value, "flex-start") || Is(value, "start") || Is(value, "normal"))
                        s.AlignContent = AlignContentKind.FlexStart;
                    else if (Is(value, "flex-end") || Is(value, "end")) s.AlignContent = AlignContentKind.FlexEnd;
                    else if (Is(value, "center")) s.AlignContent = AlignContentKind.Center;
                    else if (Is(value, "space-between")) s.AlignContent = AlignContentKind.SpaceBetween;
                    else if (Is(value, "space-around")) s.AlignContent = AlignContentKind.SpaceAround;
                    else if (Is(value, "space-evenly")) s.AlignContent = AlignContentKind.SpaceEvenly;
                    else if (Is(value, "stretch"))
                        Diagnostics.Report(DiagnosticKind.ValueIgnored, "align-content", value);
                    else Diagnostics.Report(DiagnosticKind.ValueRejected, "align-content", value);
                    break;

                // Accepted and deliberately without effect, because they have no effect to have here. Every one
                // of these is a hint to a browser about work this renderer does not do - compositor layers, touch
                // scrolling, tap highlights, subpixel smoothing, a scrollbar that is never drawn. Reporting them
                // as lost would be false: nothing was lost, there was nothing to lose. And a report that names
                // properties which are fine is a report an author learns to skip past.
                case "will-change":
                case "touch-action":
                case "user-select":
                case "-webkit-user-select":
                case "-webkit-tap-highlight-color":
                case "text-size-adjust":
                case "-webkit-text-size-adjust":
                case "print-color-adjust":
                case "-webkit-print-color-adjust":
                case "-webkit-font-smoothing":
                case "-moz-osx-font-smoothing":
                case "text-rendering":
                case "scrollbar-width":
                case "-ms-overflow-style":
                    break;

                case "border-style":
                    // `none` and `hidden` make the used width zero, which is what CSS says and what this engine
                    // can honour exactly. `solid` is what it draws anyway. The rest are drawn solid and say so.
                    if (Is(value, "none") || Is(value, "hidden")) s.BorderWidth = Edges.Zero;
                    else if (!Is(value, "solid"))
                        Diagnostics.Report(DiagnosticKind.ValueIgnored, "border-style", value);
                    break;

                // --------------------------------------------- already what this renderer does --
                //
                // Each of these has exactly one value that describes the engine's own behaviour, and that value is
                // taken in silence; anything else is a real difference and says so. The distinction matters because
                // a preflight sheet - Tailwind's, Normalize's, anyone's - is made almost entirely of the agreeing
                // half. Reported wholesale they were fifty complaints about nothing, and a report that names
                // declarations nothing was lost to is a report an author learns to skip past.

                case "box-sizing":
                    // Every box here measures border-box, always: padding and border come out of the declared size
                    // (FlexLayout). `content-box` would need a second box model and is the only real loss.
                    if (!Is(value, "border-box")) Diagnostics.Report(DiagnosticKind.ValueIgnored, property, value);
                    break;

                case "appearance":
                case "-webkit-appearance":
                    // Only a checkbox and a radio have an appearance to strip - this engine draws theirs, because
                    // there is no user-agent stylesheet to put one in the cascade. Everywhere else `none` asks for
                    // something that already is not there, which is agreement rather than a loss.
                    if (Is(value, "none")) s.Appearance = AppearanceKind.None;
                    else if (Is(value, "auto")) s.Appearance = AppearanceKind.Auto;
                    else Diagnostics.Report(DiagnosticKind.ValueIgnored, property, value);
                    break;

                case "resize":
                    // Nothing has a resize handle to take away.
                    if (!Is(value, "none")) Diagnostics.Report(DiagnosticKind.ValueIgnored, property, value);
                    break;

                case "font-feature-settings":
                case "font-variation-settings":
                case "font-variant-numeric":
                    // `normal` is the initial value, and it is the whole of what a preflight writes. TextMeshPro
                    // exposes no OpenType feature or variation axes, so a real setting is a real loss.
                    if (!Is(value, "normal")) Diagnostics.Report(DiagnosticKind.ValueIgnored, property, value);
                    break;

                case "vertical-align":
                    // There are no inline boxes to align against a baseline: every child is a flex item, and what
                    // moves it up or down is `align-items`. `baseline` is the initial value, so it asks for nothing.
                    if (!Is(value, "baseline")) Diagnostics.Report(DiagnosticKind.ValueIgnored, property, value);
                    break;

                case "list-style":
                case "list-style-type":
                case "list-style-image":
                case "list-style-position":
                    // No marker is drawn, so `none` is agreement and a bullet or a number is not.
                    if (!Is(value, "none")) Diagnostics.Report(DiagnosticKind.ValueIgnored, property, value);
                    break;

                case "outline":
                case "outline-style":
                case "outline-width":
                case "outline-color":
                case "outline-offset":
                    // No outline is drawn. A focus ring written as `outline: none` - which is most of them, right
                    // before a box-shadow ring that this engine does draw - loses nothing.
                    if (!Is(value, "none") && !Is(value, "0") && !Is(value, "0px"))
                        Diagnostics.Report(DiagnosticKind.ValueIgnored, property, value);
                    break;

                case "text-indent":
                    if (!Is(value, "0") && !Is(value, "0px"))
                        Diagnostics.Report(DiagnosticKind.ValueIgnored, property, value);
                    break;

                case "text-decoration":
                case "text-decoration-line":
                case "-webkit-text-decoration":
                    ApplyTextDecoration(s, property, value);
                    break;

                case "text-transform":
                    if (Is(value, "none")) s.TextTransform = TextTransformKind.None;
                    else if (Is(value, "uppercase")) s.TextTransform = TextTransformKind.Uppercase;
                    else if (Is(value, "lowercase")) s.TextTransform = TextTransformKind.Lowercase;
                    else if (Is(value, "capitalize")) s.TextTransform = TextTransformKind.Capitalize;
                    else Diagnostics.Report(DiagnosticKind.ValueRejected, "text-transform", value);
                    break;

                case "pointer-events":
                    // Only the two that mean anything without an SVG. `all`, `stroke`, `fill` and the rest are
                    // painting-order words for shapes this renderer does not draw.
                    if (Is(value, "none")) s.PointerEventsNone = true;
                    else if (Is(value, "auto") || Is(value, "all")) s.PointerEventsNone = false;
                    else Diagnostics.Report(DiagnosticKind.ValueRejected, "pointer-events", value);
                    break;

                case "overflow-x": s.OverflowX = ParseOverflow(value, s.OverflowX); break;
                case "overflow-y": s.OverflowY = ParseOverflow(value, s.OverflowY); break;

                // ----------------------------------------------------------------- paint --
                case "background":
                case "background-color": ApplyBackground(s, value); break;
                case "background-image": ApplyBackground(s, value); break;

                case "border": ApplyBorder(s, value); break;
                case "border-top": ApplyBorder(s, value, Side.Top); break;
                case "border-right": ApplyBorder(s, value, Side.Right); break;
                case "border-bottom": ApplyBorder(s, value, Side.Bottom); break;
                case "border-left": ApplyBorder(s, value, Side.Left); break;
                case "border-width": if (Edges_(value, out Edges bw)) s.BorderWidth = bw; break;
                case "border-color": if (Colour(value, out RgbaColor bc)) s.BorderColor = bc; break;
                case "border-top-width": if (Length(value, out Len btw)) s.BorderWidth.Top = btw; break;
                case "border-right-width": if (Length(value, out Len brw)) s.BorderWidth.Right = brw; break;
                case "border-bottom-width": if (Length(value, out Len bbw)) s.BorderWidth.Bottom = bbw; break;
                case "border-left-width": if (Length(value, out Len blw)) s.BorderWidth.Left = blw; break;

                case "border-radius": ApplyRadius(s, value); break;
                case "border-top-left-radius": if (Px(value, out float r1)) s.BorderRadius.TopLeft = r1; break;
                case "border-top-right-radius": if (Px(value, out float r2)) s.BorderRadius.TopRight = r2; break;
                case "border-bottom-right-radius": if (Px(value, out float r3)) s.BorderRadius.BottomRight = r3; break;
                case "border-bottom-left-radius": if (Px(value, out float r4)) s.BorderRadius.BottomLeft = r4; break;

                case "box-shadow": ApplyShadow(s, value); break;
                case "transform": ApplyTransform(s, value); break;
                case "transition": ApplyTransition(s, value); break;
                case "transition-duration": s.TransitionSeconds = Seconds(value); break;
                case "transition-delay": s.TransitionDelaySeconds = Seconds(value); break;
                case "transition-timing-function": s.TransitionEasing = Easing(value); break;
                case "transition-property": break;   // every animatable property transitions; see ApplyTransition

                case "opacity": if (Number(value, out float op)) s.Opacity = op < 0f ? 0f : (op > 1f ? 1f : op); break;

                // ------------------------------------------------------------------ text --
                case "color": if (Colour(value, out RgbaColor col)) s.Color = col; break;
                case "font-family": s.FontFamily = FontFamilies.Resolve(value); break;
                case "font-size":
                    // A percentage is measured against the PARENT's font size, which is what the length context
                    // already carries - `em` means the same thing and is resolved from the same number.
                    if (Is(value, "smaller")) s.FontSize = Context.FontSize * 0.8333f;
                    else if (Is(value, "larger")) s.FontSize = Context.FontSize * 1.2f;
                    else if (Percent(value, out float fp)) s.FontSize = Context.FontSize * fp;
                    else if (Px(value, out float fs)) s.FontSize = fs;
                    else Diagnostics.Report(DiagnosticKind.ValueRejected, property, value);
                    break;

                case "font": ApplyFontShorthand(s, value); break;
                case "font-weight": s.FontWeight = ParseWeight(value, s.FontWeight); break;
                case "font-style": s.FontStyle = Is(value, "italic") || Is(value, "oblique") ? FontStyleKind.Italic : FontStyleKind.Normal; break;
                case "line-height": ApplyLineHeight(s, value); break;
                case "letter-spacing": if (Is(value, "normal")) s.LetterSpacing = 0f; else if (Px(value, out float ls)) s.LetterSpacing = ls; break;

                // Sideload's own, hence the prefix: the web reaches monospace by naming a family, and there is no
                // monospace family here to name. `normal` turns it back off, so a subtree can opt out of an inherited
                // grid the way `letter-spacing: normal` does.
                case "-s1-scroll":
                    s.SmoothScroll = !Is(value, "instant") && !Is(value, "auto");
                    break;

                case "-s1-mono-advance":
                    if (Is(value, "normal") || Is(value, "none")) s.MonoAdvance = 0f;
                    else if (Px(value, out float adv)) s.MonoAdvance = adv < 0f ? 0f : adv;
                    break;

                // Standard CSS. `auto` hands the caret back to the text colour, which is the default.
                case "caret-color":
                    if (Is(value, "auto")) s.CaretColor = null;
                    else if (Colour(value, out RgbaColor caret)) s.CaretColor = caret;
                    break;

                // Sideload's own: CSS has a caret colour but no caret width, and a block cursor is the difference
                // between a text field and a terminal.
                case "-s1-caret-width":
                    if (Px(value, out float caretWidth)) s.CaretWidth = caretWidth < 0f ? 0f : caretWidth;
                    break;

                // The inline suggestion behind the caret. `auto` is the text colour faded, which is what fish and
                // PSReadLine both draw and what anyone reading it expects.
                case "-s1-ghost-color":
                    if (Is(value, "auto")) s.GhostColor = null;
                    else if (Colour(value, out RgbaColor ghost)) s.GhostColor = ghost;
                    break;
                case "text-align":
                    if (Is(value, "center")) s.TextAlign = TextAlignKind.Center;
                    else if (Is(value, "right") || Is(value, "end")) s.TextAlign = TextAlignKind.Right;
                    else if (Is(value, "left") || Is(value, "start")) s.TextAlign = TextAlignKind.Left;
                    break;
                case "white-space":
                    if (Is(value, "nowrap")) s.WhiteSpace = WhiteSpaceKind.NoWrap;
                    else if (Is(value, "pre")) s.WhiteSpace = WhiteSpaceKind.Pre;
                    else if (Is(value, "pre-wrap") || Is(value, "break-spaces")) s.WhiteSpace = WhiteSpaceKind.PreWrap;
                    else s.WhiteSpace = WhiteSpaceKind.Normal;
                    break;
                case "text-overflow": s.TextOverflowEllipsis = Is(value, "ellipsis"); break;

                // `word-wrap` is the old name for the same property; every browser still takes it and so do the
                // resets that pages ship, so it is an alias rather than a second field.
                case "overflow-wrap":
                case "word-wrap":
                    if (Is(value, "break-word")) s.OverflowWrap = OverflowWrapKind.BreakWord;
                    else if (Is(value, "anywhere")) s.OverflowWrap = OverflowWrapKind.Anywhere;
                    else if (Is(value, "normal")) s.OverflowWrap = OverflowWrapKind.Normal;
                    else Diagnostics.Report(DiagnosticKind.ValueRejected, property, value);
                    break;

                // `word-break: break-word` is deprecated and means `overflow-wrap: break-word` - it is in the CSS
                // that exists, so it is taken, and taken as what it actually does rather than as a word-break value.
                case "word-break":
                    if (Is(value, "break-all")) s.WordBreak = WordBreakKind.BreakAll;
                    else if (Is(value, "keep-all")) s.WordBreak = WordBreakKind.KeepAll;
                    else if (Is(value, "normal")) s.WordBreak = WordBreakKind.Normal;
                    else if (Is(value, "break-word")) s.OverflowWrap = OverflowWrapKind.BreakWord;
                    else Diagnostics.Report(DiagnosticKind.ValueRejected, property, value);
                    break;

                // ----------------------------------------------------- generated content --
                //
                // Only `::before` and `::after` read this: DomBuilder turns a style whose Content is not null into
                // a box, and nothing looks at it on an ordinary element.
                //
                // Read: a quoted string, several of them concatenated, `attr()` (resolved by the cascade, which is
                // the only place that knows the element), and `none`. Deliberately NOT read: counter() and
                // counters(), the quote keywords, url() and the gradient functions, and the `/ "alt text"` suffix.
                // Each of those is its own feature - a counter needs a numbering pass over the document, an image
                // needs the paint layer - and none is reachable from a Tailwind utility. Such a value is refused
                // whole, so it lands in the log as a rejected value rather than as an empty box nobody ordered.
                case "content":
                    if (Is(value, "none") || Is(value, "normal")) { s.Content = null; break; }
                    if (TryContent(value, out string generated)) s.Content = generated;
                    else Diagnostics.Report(DiagnosticKind.ValueRejected, property, value);
                    break;

                default: return false;
            }

            return true;
        }

        /// <summary>
        /// Whether this renderer implements a property at all. Answered by running the real switch against a
        /// throwaway style, so there is no second list to keep in step - the cases above ARE the list.
        ///
        /// Custom properties are always "supported": they are storage for var(), not something to implement.
        /// </summary>
        /// <summary>
        /// The `inherit` keyword: take this property's value from the parent, whatever it is.
        ///
        /// It cannot live in <see cref="Apply"/>, which sees one style and no ancestry, so the cascade calls this
        /// first and only falls through when the answer is false - and then the ordinary path reports it, which is
        /// what an unhandled property should do.
        ///
        /// The list below is <see cref="ComputedStyle.CreateFrom"/>'s, read the other way round. For an inherited
        /// property `inherit` usually asks for what has already been copied down, and copying it AGAIN is the whole
        /// point: it undoes a declaration that landed earlier in this element's own cascade, which is the one case
        /// where the two differ - `.a { color: red }` followed by `.a { color: inherit }` must end up at the
        /// parent's colour, not at red.
        ///
        /// Properties this engine deliberately ignores answer true as well. `font-feature-settings: inherit` asks
        /// to carry down a setting that was never stored; nothing is lost, so nothing is reported. Tailwind's
        /// preflight writes six of those on every form control.
        /// </summary>
        /// <summary>
        /// The `font` shorthand: `[style] [variant] [weight] [stretch] size[/line-height] family`.
        ///
        /// Read left to right, because the grammar is: everything before the SIZE is optional and unordered, the
        /// size is the first length, and everything after it is the family. That makes the size the anchor - find
        /// it and both halves fall out - which is what makes this eighteen lines instead of a parser.
        ///
        /// A preflight writes it once, on form controls, as `font: inherit`; that never reaches here because the
        /// cascade takes the keyword first. What does reach here is a page setting its whole type in one line, and
        /// before this the entire declaration - family, size and weight together - was dropped as unknown.
        ///
        /// The system keywords (`caption`, `menu`, `status-bar`, ...) name a font the host operating system
        /// chooses. There is no such font here, so they are reported rather than guessed at.
        /// </summary>
        private static void ApplyFontShorthand(ComputedStyle s, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            if (Is(value, "caption") || Is(value, "icon") || Is(value, "menu")
                || Is(value, "message-box") || Is(value, "small-caption") || Is(value, "status-bar"))
            {
                Diagnostics.Report(DiagnosticKind.ValueIgnored, "font", value);
                return;
            }

            string[] words = value.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            int size = -1;
            for (int i = 0; i < words.Length && size < 0; i++)
            {
                string head = words[i].Split('/')[0];
                if (TryPx(head, out _) || head.EndsWith("%", StringComparison.Ordinal)) size = i;
            }

            if (size < 0) { Diagnostics.Report(DiagnosticKind.ValueRejected, "font", value); return; }

            for (int i = 0; i < size; i++)
            {
                if (Is(words[i], "normal")) continue;
                if (Is(words[i], "italic") || Is(words[i], "oblique")) { s.FontStyle = FontStyleKind.Italic; continue; }

                int weight = ParseWeight(words[i], -1);
                if (weight > 0) s.FontWeight = weight;
                // `small-caps`, `condensed` and the rest of the optional slots have no counterpart; the shorthand
                // still carries a size and a family, and losing those to one unreadable word would be the worse
                // reading of it.
                else Diagnostics.Report(DiagnosticKind.ValueIgnored, "font", words[i]);
            }

            string[] sizeAndLine = words[size].Split('/');
            Apply(s, "font-size", sizeAndLine[0]);
            if (sizeAndLine.Length > 1) Apply(s, "line-height", sizeAndLine[1]);

            if (size + 1 < words.Length)
                s.FontFamily = FontFamilies.Resolve(string.Join(" ", words, size + 1, words.Length - size - 1));
        }

        internal static bool IsInheritKeyword(string value) =>
            value != null && value.Trim().Equals("inherit", StringComparison.OrdinalIgnoreCase);

        internal static bool Inherit(ComputedStyle s, ComputedStyle parent, string property)
        {
            if (string.IsNullOrEmpty(property)) return false;

            // What the ROOT inherits is the initial value of everything, which is what a fresh style holds. Standing
            // one in for a missing parent keeps the switch below free of a null check per case, and it is also what
            // lets the sheet audit - which has no element and therefore no ancestry - ask the same question.
            parent ??= new ComputedStyle();

            switch (property.Trim().ToLowerInvariant())
            {
                case "color": s.Color = parent.Color; return true;

                // The shorthand carries all five at once, which is exactly how a preflight hands a form control the
                // page's type: `button, input, select { font: inherit }`.
                case "font":
                    s.FontFamily = parent.FontFamily;
                    s.FontSize = parent.FontSize;
                    s.FontWeight = parent.FontWeight;
                    s.FontStyle = parent.FontStyle;
                    s.LineHeight = parent.LineHeight;
                    return true;

                case "font-family": s.FontFamily = parent.FontFamily; return true;
                case "font-size": s.FontSize = parent.FontSize; return true;
                case "font-weight": s.FontWeight = parent.FontWeight; return true;
                case "font-style": s.FontStyle = parent.FontStyle; return true;
                case "line-height": s.LineHeight = parent.LineHeight; return true;
                case "letter-spacing": s.LetterSpacing = parent.LetterSpacing; return true;
                case "text-align": s.TextAlign = parent.TextAlign; return true;
                case "text-transform": s.TextTransform = parent.TextTransform; return true;
                case "text-decoration":
                case "text-decoration-line":
                case "-webkit-text-decoration": s.TextDecoration = parent.TextDecoration; return true;
                case "white-space": s.WhiteSpace = parent.WhiteSpace; return true;
                case "overflow-wrap":
                case "word-wrap": s.OverflowWrap = parent.OverflowWrap; return true;
                case "word-break": s.WordBreak = parent.WordBreak; return true;
                case "pointer-events": s.PointerEventsNone = parent.PointerEventsNone; return true;
                case "caret-color": s.CaretColor = parent.CaretColor; return true;

                // Not inherited in CSS, but `inherit` names the parent's value all the same, and these are the ones
                // a real sheet asks it of - a border that follows the text colour, a control that takes the box's.
                case "border-color": s.BorderColor = parent.BorderColor; return true;
                case "background-color": s.BackgroundColor = parent.BackgroundColor; return true;
                case "opacity": s.Opacity = parent.Opacity; return true;

                // Read and deliberately without effect, so carrying one down loses nothing either.
                case "font-feature-settings":
                case "font-variation-settings":
                case "font-variant-numeric":
                case "vertical-align":
                case "appearance":
                case "-webkit-appearance":
                case "list-style":
                case "list-style-type":
                case "outline":
                case "outline-color":
                case "text-indent":
                case "tab-size":
                case "cursor": return true;

                default: return false;
            }
        }

        /// <summary>
        /// `text-decoration`, and the shorthand it usually arrives as.
        ///
        /// TextMeshPro draws an underline and a strike as font-style flags, so those two are exact. The rest of the
        /// shorthand - a decoration COLOUR, a style like `dotted`, a thickness - has no equivalent: TMP's line takes
        /// the text's own colour and the font's own thickness. Those parts are reported by the word that was
        /// written, so `underline dotted` says `dotted` rather than pretending the whole declaration was lost.
        /// </summary>
        private static void ApplyTextDecoration(ComputedStyle s, string property, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            var lines = TextDecorationKind.None;
            bool sawLine = false;

            foreach (string word in value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Is(word, "none")) { sawLine = true; continue; }
                if (Is(word, "underline")) { lines |= TextDecorationKind.Underline; sawLine = true; continue; }
                if (Is(word, "line-through")) { lines |= TextDecorationKind.LineThrough; sawLine = true; continue; }

                // `overline` has no TMP flag, and the two decoration keywords that are neither a line nor a style -
                // `blink` and `grammar-error` - have nothing here either.
                if (Is(word, "overline") || Is(word, "blink") || Is(word, "spelling-error")
                    || Is(word, "grammar-error"))
                {
                    Diagnostics.Report(DiagnosticKind.ValueIgnored, property, word);
                    sawLine = true;
                    continue;
                }

                // `solid` is what TMP draws; the other line styles and any colour or thickness are not.
                if (Is(word, "solid") || Is(word, "auto") || Is(word, "from-font")) continue;

                Diagnostics.Report(DiagnosticKind.ValueIgnored, property, word);
            }

            if (sawLine) s.TextDecoration = lines;
        }

        internal static bool Supports(string property)
        {
            if (string.IsNullOrEmpty(property)) return true;
            if (property.StartsWith("--", StringComparison.Ordinal)) return true;

            // Muted, because this runs the real switch to ask about the NAME. Without it every call would file a
            // complaint about a value nobody wrote - "0" is not what the author said, it is what this probe says.
            bool muted = Diagnostics.Muted;
            Diagnostics.Muted = true;
            try { return Apply(new ComputedStyle(), property, "0"); }
            catch { return true; }
            finally { Diagnostics.Muted = muted; }
        }

        // ------------------------------------------------------- parsing, with a paper trail --
        //
        // Thin wrappers around ValueParser used ONLY at the top level of the switch, where a failed parse really
        // does mean the whole declaration was thrown away. The speculative parses inside the shorthand helpers
        // (ApplyFlex, ApplyBorder, ApplyShadow, ApplyBackground) keep calling ValueParser directly: there a failed
        // token is how the grammar is discovered, not a mistake, and reporting it would cry wolf.

        private static bool Length(string value, out Len len)
        {
            if (ValueParser.TryLength(value, Context, out len)) return true;
            Diagnostics.Report(DiagnosticKind.ValueRejected, _applying, value);
            return false;
        }

        private static bool Colour(string value, out RgbaColor color)
        {
            if (ValueParser.TryColor(value, CurrentColor, out color)) return true;
            Diagnostics.Report(DiagnosticKind.ValueRejected, _applying, value);
            return false;
        }

        /// <summary>A whole number. Separate from <see cref="Number"/> because the properties that want one refuse a
        /// fraction rather than rounding it - a rounded `z-index: 1.5` would be a level the author never wrote.</summary>
        private static bool Integer(string value, out int number)
        {
            if (int.TryParse(value, System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture, out number)) return true;

            Diagnostics.Report(DiagnosticKind.ValueRejected, _applying, value);
            return false;
        }

        private static bool Number(string value, out float number)
        {
            if (ValueParser.TryNumber(value, out number)) return true;
            Diagnostics.Report(DiagnosticKind.ValueRejected, _applying, value);
            return false;
        }

        private static bool Edges_(string value, out Edges edges)
        {
            if (TryEdges(value, out edges)) return true;
            Diagnostics.Report(DiagnosticKind.ValueRejected, _applying, value);
            return false;
        }

        /// <summary>A track list. `none` parses to a null template, which is what "no explicit tracks" is.</summary>
        private static bool Template(string value, out GridTemplate template)
        {
            if (GridParser.TryTrackList(value, Context, out template)) return true;
            Diagnostics.Report(DiagnosticKind.ValueRejected, _applying, value);
            return false;
        }

        private static bool Track(string value, out GridTrack track)
        {
            if (GridParser.TryTrack(value, Context, out track)) return true;
            Diagnostics.Report(DiagnosticKind.ValueRejected, _applying, value);
            return false;
        }

        /// <summary>
        /// A `grid-column` / `grid-row` value.
        ///
        /// A NAMED line is not reported here even though it fails to parse: it is a missing feature rather than a
        /// bad value, <see cref="DeadValues"/> already says so in those words, and two reports about one
        /// declaration is how a report gets skipped.
        /// </summary>
        private static bool Placement(string value, out GridPlacement placement)
        {
            if (GridParser.TryPlacement(value, out placement)) return true;
            if (!GridParser.NamesAnArea(value)) Diagnostics.Report(DiagnosticKind.ValueRejected, _applying, value);
            return false;
        }

        private static bool Line(string value, out GridLine line)
        {
            if (GridParser.TryLine(value, out line)) return true;
            if (!GridParser.NamesAnArea(value)) Diagnostics.Report(DiagnosticKind.ValueRejected, _applying, value);
            return false;
        }

        /// <summary>A percentage, as a fraction: `80%` comes back as 0.8. Silent - the caller decides what a
        /// refusal means, and for most properties a percentage is a perfectly good length elsewhere.</summary>
        private static bool Percent(string value, out float fraction)
        {
            fraction = 0f;
            if (string.IsNullOrWhiteSpace(value)) return false;

            string trimmed = value.Trim();
            if (!trimmed.EndsWith("%", StringComparison.Ordinal)) return false;
            if (!float.TryParse(trimmed.Substring(0, trimmed.Length - 1).Trim(),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float percent)) return false;

            fraction = percent / 100f;
            return true;
        }

        /// <summary>A px-only length. A percentage parses fine and is still refused here, so it is reported with
        /// the reason - `border-radius: 50%` is valid CSS that this renderer cannot draw.</summary>
        private static bool Px(string value, out float px)
        {
            if (TryPx(value, out px)) return true;
            Diagnostics.Report(DiagnosticKind.ValueRejected, _applying, value);
            return false;
        }

        // ------------------------------------------------------------------ shorthands --

        /// <summary>
        /// The `flex` shorthand, to the grammar rather than to a guess.
        ///
        ///     none            0 0 auto
        ///     initial         0 1 auto
        ///     auto            1 1 auto
        ///     &lt;number&gt;        &lt;number&gt; 1 0%          - the basis is the part people forget, and the reason a
        ///                                             single `flex: 1` child fills its line instead of hugging
        ///     &lt;width&gt;         1 1 &lt;width&gt;
        ///     &lt;n&gt; &lt;n&gt;         grow shrink, basis 0%
        ///     &lt;n&gt; &lt;width&gt;     grow basis, shrink 1
        ///     &lt;n&gt; &lt;n&gt; &lt;width&gt; all three
        ///
        /// Position two is a shrink factor only when it is a BARE number; anything carrying a unit, a percentage
        /// or `auto` is a basis. `0` is both a valid number and a valid length, so the order of those two tests
        /// is what decides `flex: 1 0` - and reading it as a basis is how shrink used to get quietly reset to 1.
        ///
        /// Nothing is written until the whole value has parsed. A shorthand is one declaration: half of it
        /// applied and half not is worse than none of it, because the half that landed is invisible.
        ///
        /// A value this grammar cannot take is REPORTED rather than dropped. Every other rejection in this file
        /// says so, and a shorthand that silently changes nothing is the hardest kind to find: the property is
        /// implemented, the name raises no warning, and the box just sits at its default.
        /// </summary>
        private static void ApplyFlex(ComputedStyle s, string value)
        {
            if (Is(value, "none")) { s.FlexGrow = 0f; s.FlexShrink = 0f; s.FlexBasis = Len.Auto; return; }
            if (Is(value, "initial")) { s.FlexGrow = 0f; s.FlexShrink = 1f; s.FlexBasis = Len.Auto; return; }
            if (Is(value, "auto")) { s.FlexGrow = 1f; s.FlexShrink = 1f; s.FlexBasis = Len.Auto; return; }

            string[] p = ValueParser.SplitTopLevel(value);
            if (p.Length == 0 || p.Length > 3) { Reject(value); return; }

            float grow = 1f;
            float shrink = 1f;
            Len basis = Len.Percent(0f);
            bool haveGrow = false;
            bool haveBasis = false;
            int at = 0;

            if (ValueParser.TryNumber(p[at], out float g)) { grow = g; haveGrow = true; at++; }

            if (haveGrow && at < p.Length && ValueParser.TryNumber(p[at], out float sh)) { shrink = sh; at++; }

            if (at < p.Length)
            {
                if (Is(p[at], "auto")) basis = Len.Auto;
                else if (ValueParser.TryLength(p[at], Context, out Len b)) basis = b;
                else { Reject(value); return; }                 // not a basis either - the whole value is junk

                haveBasis = true;
                at++;
            }

            if (at != p.Length) { Reject(value); return; }      // something left over: do not guess at it
            if (!haveGrow && !haveBasis) { Reject(value); return; }   // nothing understood at all

            // A basis on its own is `1 1 <width>`: it says how big to start, not that it may not move.
            if (!haveGrow) { grow = 1f; shrink = 1f; }

            s.FlexGrow = grow;
            s.FlexShrink = shrink;
            s.FlexBasis = basis;
        }

        private static void Reject(string value) => Diagnostics.Report(DiagnosticKind.ValueRejected, "flex", value);

        /// <summary>
        /// `grid-template: &lt;rows&gt; / &lt;columns&gt;` - the two-track-list form only.
        ///
        /// The third form of this shorthand takes `grid-template-areas` strings, and that is the named-area model
        /// this engine does not place by. A value carrying a string is left alone here and named by
        /// <see cref="DeadValues"/>, rather than half-applied: taking the track lists out of it and dropping the
        /// areas would lay the tracks out correctly and put every item in the wrong one.
        /// </summary>
        private static void ApplyGridTemplate(ComputedStyle s, string value)
        {
            if (Is(value, "none")) { s.GridTemplateRows = null; s.GridTemplateColumns = null; return; }
            if (value.IndexOf('"') >= 0 || value.IndexOf('\'') >= 0) return;

            if (!GridParser.TrySplitSlash(value, out string rows, out string columns)
                || !GridParser.TryTrackList(rows, Context, out GridTemplate rowTracks)
                || !GridParser.TryTrackList(columns, Context, out GridTemplate columnTracks))
            {
                Diagnostics.Report(DiagnosticKind.ValueRejected, _applying, value);
                return;
            }

            s.GridTemplateRows = rowTracks;
            s.GridTemplateColumns = columnTracks;
        }

        /// <summary>`grid-area: 1 / 2 / 3 / 4` - row-start, column-start, row-end, column-end, as CSS orders them.</summary>
        private static void ApplyGridArea(ComputedStyle s, string value)
        {
            if (GridParser.TryArea(value, out GridPlacement rows, out GridPlacement columns))
            {
                s.GridRow = rows;
                s.GridColumn = columns;
                return;
            }

            // A named area is a missing feature, not a bad value, and DeadValues has already said which.
            if (GridParser.NamesAnArea(value)) return;

            Diagnostics.Report(DiagnosticKind.ValueRejected, _applying, value);
        }

        private static void ApplyBackground(ComputedStyle s, string value)
        {
            if (Is(value, "none")) { s.HasGradient = false; s.BackgroundColor = RgbaColor.Transparent; return; }

            int grad = value.IndexOf("linear-gradient(", StringComparison.OrdinalIgnoreCase);
            if (grad >= 0)
            {
                int open = value.IndexOf('(', grad);
                int close = MatchingParen(value, open);
                if (close > open)
                {
                    string[] args = ValueParser.SplitTopLevel(value.Substring(open + 1, close - open - 1), commaSeparated: true);
                    int i = 0;
                    float angle = 180f;
                    if (args.Length > 0 && args[0].EndsWith("deg", StringComparison.OrdinalIgnoreCase)
                        && ValueParser.TryNumber(args[0].Substring(0, args[0].Length - 3), out float a))
                    {
                        angle = a; i = 1;
                    }

                    if (args.Length >= i + 2
                        && ValueParser.TryColor(FirstToken(args[i]), out RgbaColor from)
                        && ValueParser.TryColor(FirstToken(args[i + 1]), out RgbaColor to))
                    {
                        s.HasGradient = true;
                        s.GradientAngleDeg = angle;
                        s.GradientFrom = from;
                        s.GradientTo = to;
                        // A gradient replaces the flat fill; keeping both would double-paint the box.
                        s.BackgroundColor = RgbaColor.Transparent;
                        return;
                    }
                }
            }

            foreach (string part in ValueParser.SplitTopLevel(value))
            {
                if (ValueParser.TryColor(part, out RgbaColor c)) { s.BackgroundColor = c; s.HasGradient = false; return; }
            }

            // Nothing in the value was a colour this engine can read. That is `url(...)`, which paints nothing,
            // and every modern colour function - the whole of Tailwind v4's palette arrives as oklch().
            Diagnostics.Report(DiagnosticKind.ValueRejected, _applying, value);
        }

        /// <summary>Which edge a border shorthand applies to; All is the plain `border` property.</summary>
        private enum Side { All, Top, Right, Bottom, Left }

        /// <summary>
        /// `border` and its four per-side forms. The colour is shared across all sides - a documented simplification,
        /// because one colour covers every real UI and four would double the vertex payload of every box.
        /// </summary>
        /// <summary>
        /// `transform: translate(8px, -4px) scale(1.05) rotate(3deg)`. Only the three functions that cannot affect
        /// layout - a transform runs after the box has been placed, so it can never move a sibling.
        ///
        /// Not supported and deliberately so: matrix(), skew(), perspective and the 3D family. They buy a game UI
        /// nothing and each one is a separate mapping onto a RectTransform.
        /// </summary>
        private static void ApplyTransform(ComputedStyle s, string value)
        {
            if (Is(value, "none")) { s.TranslateX = s.TranslateY = 0f; s.ScaleX = s.ScaleY = 1f; s.RotateDeg = 0f; return; }

            foreach (string part in ValueParser.SplitTopLevel(value))
            {
                int open = part.IndexOf('(');
                int close = part.LastIndexOf(')');
                if (open < 0 || close <= open) continue;

                string name = part.Substring(0, open).Trim().ToLowerInvariant();
                string[] args = part.Substring(open + 1, close - open - 1).Split(',');

                switch (name)
                {
                    case "translate":
                        s.TranslateX = Px(args, 0);
                        s.TranslateY = args.Length > 1 ? Px(args, 1) : 0f;
                        break;
                    case "translatex": s.TranslateX = Px(args, 0); break;
                    case "translatey": s.TranslateY = Px(args, 0); break;
                    case "scale":
                        s.ScaleX = Number(args, 0, 1f);
                        s.ScaleY = args.Length > 1 ? Number(args, 1, 1f) : s.ScaleX;
                        break;
                    case "scalex": s.ScaleX = Number(args, 0, 1f); break;
                    case "scaley": s.ScaleY = Number(args, 0, 1f); break;
                    case "rotate": s.RotateDeg = Degrees(args, 0); break;
                }
            }
        }

        /// <summary>
        /// `transition: 150ms ease-out 50ms` - or with a property name in front, which is accepted and ignored.
        ///
        /// Ignored on purpose: a state change here repaints one box, and the engine interpolates every animatable
        /// property of it together. Honouring a property list would mean tracking which half of a box is mid-flight,
        /// for a distinction no page has yet needed. Properties that would move other boxes are never animated at
        /// all, whatever is listed.
        /// </summary>
        private static void ApplyTransition(ComputedStyle s, string value)
        {
            if (Is(value, "none")) { s.TransitionSeconds = 0f; return; }

            bool haveDuration = false;
            foreach (string part in ValueParser.SplitTopLevel(value))
            {
                EasingKind easing = Easing(part);
                if (easing != EasingKind.EaseOut || Is(part, "ease-out")) { s.TransitionEasing = easing; continue; }

                float seconds = Seconds(part);
                if (seconds <= 0f) continue;                       // a property name, or something unreadable

                if (!haveDuration) { s.TransitionSeconds = seconds; haveDuration = true; }
                else s.TransitionDelaySeconds = seconds;           // the second time is the delay, as in CSS
            }
        }

        /// <summary>Seconds from `250ms` or `0.25s`. Zero for anything else, which reads as "no transition".</summary>
        private static float Seconds(string value)
        {
            string v = (value ?? "").Trim().ToLowerInvariant();

            if (v.EndsWith("ms") && float.TryParse(v[..^2], System.Globalization.NumberStyles.Float,
                                                   System.Globalization.CultureInfo.InvariantCulture, out float ms))
                return ms / 1000f;

            if (v.EndsWith("s") && float.TryParse(v[..^1], System.Globalization.NumberStyles.Float,
                                                  System.Globalization.CultureInfo.InvariantCulture, out float sec))
                return sec;

            return 0f;
        }

        private static EasingKind Easing(string value) => (value ?? "").Trim().ToLowerInvariant() switch
        {
            "linear" => EasingKind.Linear,
            "ease-in" => EasingKind.EaseIn,
            "ease-in-out" or "ease" => EasingKind.EaseInOut,
            _ => EasingKind.EaseOut,
        };

        private static float Px(string[] args, int index) =>
            index < args.Length && ValueParser.TryLength(args[index].Trim(), Context, out Len len) ? len.Resolve(0f) : 0f;

        /// <summary>An angle. `deg` is the only unit worth supporting; `turn` and `rad` are converted because they
        /// cost two lines and a page that uses one would otherwise silently not rotate at all.</summary>
        private static float Degrees(string[] args, int index)
        {
            if (index >= args.Length) return 0f;
            string v = args[index].Trim().ToLowerInvariant();

            float factor = 1f;
            if (v.EndsWith("deg")) v = v[..^3];
            else if (v.EndsWith("turn")) { v = v[..^4]; factor = 360f; }
            else if (v.EndsWith("rad")) { v = v[..^3]; factor = 180f / MathF.PI; }

            return float.TryParse(v, System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out float n) ? n * factor : 0f;
        }

        private static float Number(string[] args, int index, float fallback) =>
            index < args.Length && float.TryParse(args[index].Trim(), System.Globalization.NumberStyles.Float,
                                                  System.Globalization.CultureInfo.InvariantCulture, out float v)
                ? v : fallback;

        private static void ApplyBorder(ComputedStyle s, string value, Side side = Side.All)
        {
            if (Is(value, "none") || Is(value, "0"))
            {
                SetWidth(s, side, Len.Px(0f));
                if (side == Side.All) s.BorderColor = RgbaColor.Transparent;
                return;
            }

            foreach (string part in ValueParser.SplitTopLevel(value))
            {
                // Every line style CSS defines, not just `solid`. The engine draws them all as solid - it has no
                // dash pattern - but a style keyword must still be RECOGNISED, or it falls through to the colour
                // parser below and a stylesheet saying `1px dashed var(--ink-2)` ends up asking for the colour
                // "dashed". Degrading a dash to a solid hairline is honest; silently losing the colour is not.
                if (IsLineStyle(part)) continue;
                if (ValueParser.TryLength(part, Context, out Len w) && w.IsDefinite) { SetWidth(s, side, w); continue; }
                if (ValueParser.TryColor(part, out RgbaColor c)) s.BorderColor = c;
            }
        }

        /// <summary>The CSS border line styles. All of them draw solid here; none of them is a colour.</summary>
        private static bool IsLineStyle(string part) =>
            Is(part, "solid") || Is(part, "none") || Is(part, "hidden") || Is(part, "dashed") || Is(part, "dotted")
            || Is(part, "double") || Is(part, "groove") || Is(part, "ridge") || Is(part, "inset") || Is(part, "outset");

        private static void SetWidth(ComputedStyle s, Side side, Len width)
        {
            switch (side)
            {
                case Side.Top: s.BorderWidth.Top = width; break;
                case Side.Right: s.BorderWidth.Right = width; break;
                case Side.Bottom: s.BorderWidth.Bottom = width; break;
                case Side.Left: s.BorderWidth.Left = width; break;
                default: s.BorderWidth = Edges.All(width); break;
            }
        }

        private static void ApplyRadius(ComputedStyle s, string value)
        {
            string[] p = ValueParser.SplitTopLevel(value);
            float[] v = new float[p.Length];
            for (int i = 0; i < p.Length; i++)
            {
                // px only - a percentage parses as a length and is still refused, which is how `rounded-full`
                // disappears. One bad corner drops the whole declaration, so this is the place to say so.
                if (!TryPx(p[i], out v[i]))
                {
                    Diagnostics.Report(DiagnosticKind.ValueRejected, _applying, value);
                    return;
                }
            }

            switch (v.Length)
            {
                case 1: s.BorderRadius = Corners.All(v[0]); break;
                case 2: s.BorderRadius = new Corners { TopLeft = v[0], TopRight = v[1], BottomRight = v[0], BottomLeft = v[1] }; break;
                case 3: s.BorderRadius = new Corners { TopLeft = v[0], TopRight = v[1], BottomRight = v[2], BottomLeft = v[1] }; break;
                case 4: s.BorderRadius = new Corners { TopLeft = v[0], TopRight = v[1], BottomRight = v[2], BottomLeft = v[3] }; break;
            }
        }

        /// <summary>
        /// The one shadow this engine can draw, picked out of however many the author wrote.
        ///
        /// Only one is drawable - the shader carries a single offset, blur and colour per box - but WHICH one
        /// matters more than it sounds. The layers are read separately and the first VISIBLE one wins, rather
        /// than the first one written. Tailwind composes `box-shadow` out of five slots and the first two are
        /// normally `0 0 #0000`, so taking the first layer meant every `shadow-*` utility painted nothing at all.
        ///
        /// An `inset` layer is skipped rather than fatal, for the same reason: the outer-shadow pass cannot draw
        /// it, but the layers after it may be perfectly drawable and used to be lost with it.
        /// </summary>
        private static void ApplyShadow(ComputedStyle s, string value)
        {
            if (Is(value, "none")) { s.HasShadow = false; return; }

            foreach (string layer in ValueParser.SplitTopLevel(value, commaSeparated: true))
            {
                if (!TryShadowLayer(layer, out float x, out float y, out float blur, out RgbaColor color)) continue;

                // A fully transparent layer is a placeholder, not a shadow. Skipping it is the whole fix.
                if (color.IsTransparent) continue;

                s.HasShadow = true;
                s.ShadowOffsetX = x;
                s.ShadowOffsetY = y;
                s.ShadowBlur = blur;
                s.ShadowColor = color;
                return;
            }
        }

        /// <summary>One comma-separated layer. False for an inset layer or one without the two mandatory offsets.</summary>
        private static bool TryShadowLayer(string layer, out float x, out float y, out float blur, out RgbaColor color)
        {
            x = y = blur = 0f;
            color = new RgbaColor(0f, 0f, 0f, 0.5f);

            int lengths = 0;
            foreach (string part in ValueParser.SplitTopLevel(layer))
            {
                if (Is(part, "inset")) return false;

                // The fourth length is the spread radius, which the shader has no channel for. Reading it as a
                // blur would be worse than dropping it: a focus ring written `0 0 0 3px` would come out as a
                // 3px blur around nothing.
                if (lengths < 3 && ValueParser.TryLength(part, Context, out Len l) && l.Unit == LenUnit.Px)
                {
                    if (lengths == 0) x = l.Value;
                    else if (lengths == 1) y = l.Value;
                    else blur = l.Value;
                    lengths++;
                    continue;
                }

                if (ValueParser.TryColor(part, CurrentColor, out RgbaColor c)) color = c;
            }

            return lengths >= 2;   // offset-x and offset-y are mandatory in CSS
        }

        /// <summary>
        /// `transform-origin`, the one- or two-value form. A third value is a Z origin, which is meaningless on a
        /// flat UI, so it is read and dropped rather than refused - a page written for the web often carries one.
        /// </summary>
        private static void ApplyTransformOrigin(ComputedStyle s, string value)
        {
            string[] parts = (value ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            // One value sets the horizontal origin and centres the vertical, which is what CSS says - not "both
            // axes get the same number", which is the reading that looks right and puts `transform-origin: 0`
            // in the top-left instead of the left edge.
            if (!TryOrigin(parts[0], horizontal: true, out Len x)) { Reject(s, value); return; }

            Len y = Len.Percent(50f);
            if (parts.Length > 1 && !TryOrigin(parts[1], horizontal: false, out y)) { Reject(s, value); return; }

            s.TransformOriginX = x;
            s.TransformOriginY = y;
        }

        private static void Reject(ComputedStyle s, string value) =>
            Diagnostics.Report(DiagnosticKind.ValueRejected, "transform-origin", value);

        private static bool TryOrigin(string part, bool horizontal, out Len len)
        {
            len = Len.Percent(50f);

            if (Is(part, "center")) return true;
            if (Is(part, horizontal ? "left" : "top")) { len = Len.Percent(0f); return true; }
            if (Is(part, horizontal ? "right" : "bottom")) { len = Len.Percent(100f); return true; }

            return ValueParser.TryLength(part, Context, out len) && len.IsDefinite;
        }

        private static void ApplyLineHeight(ComputedStyle s, string value)
        {
            if (Is(value, "normal")) { s.LineHeight = Len.None; return; }
            if (ValueParser.TryLength(value, Context, out Len l) && l.IsDefinite) { s.LineHeight = l; return; }

            // A unitless line-height is a multiplier of the font size, which is exactly what a percentage resolves to
            // here - storing it that way keeps it correct when font-size changes later in the cascade.
            if (ValueParser.TryNumber(value, out float n)) s.LineHeight = Len.Percent(n * 100f);
        }

        // ----------------------------------------------------------- generated content --

        /// <summary>
        /// The text `content` produces, or false when the value says something this engine cannot make text out of.
        ///
        /// A value is a LIST whose parts concatenate, which is what makes <c>content: "(" attr(id) ")"</c> one
        /// string rather than three.
        ///
        /// An `attr()` still standing here was never resolved, because only the cascade knows which element it
        /// reads - see <see cref="StyleResolver"/>. It contributes nothing instead of failing the value, so the
        /// sheet audit, which reads a stylesheet with no document behind it at all, does not report every icon
        /// utility in a build as unreadable.
        /// </summary>
        private static bool TryContent(string value, out string text)
        {
            text = null;
            var sb = new System.Text.StringBuilder();

            foreach (string part in SplitOutsideStrings(value))
            {
                if (part.Length >= 2 && (part[0] == '"' || part[0] == '\'') && part[part.Length - 1] == part[0])
                {
                    Unescape(part.Substring(1, part.Length - 2), sb);
                    continue;
                }

                if (part.StartsWith("attr(", StringComparison.OrdinalIgnoreCase)
                    && part.EndsWith(")", StringComparison.Ordinal)) continue;

                return false;
            }

            text = sb.ToString();
            return true;
        }

        /// <summary>
        /// The space-separated parts of a value, with a quoted string kept whole.
        ///
        /// <see cref="ValueParser.SplitTopLevel"/> cannot be used for this: it cuts at every space, and the space
        /// in <c>content: "a b"</c> is text rather than a separator.
        /// </summary>
        private static List<string> SplitOutsideStrings(string value)
        {
            var parts = new List<string>();
            var current = new System.Text.StringBuilder();
            int depth = 0;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];

                if (c == '"' || c == '\'')
                {
                    int end = StringEnd(value, i);
                    current.Append(value, i, end - i + 1);
                    i = end;
                    continue;
                }

                if (c == '(') depth++;
                else if (c == ')') depth--;

                if (depth == 0 && char.IsWhiteSpace(c))
                {
                    if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
                    continue;
                }

                current.Append(c);
            }

            if (current.Length > 0) parts.Add(current.ToString());
            return parts;
        }

        /// <summary>Index of the closing quote of the string opening at <paramref name="quote"/>, or the last
        /// character when it is never closed.</summary>
        private static int StringEnd(string s, int quote)
        {
            char delimiter = s[quote];
            for (int i = quote + 1; i < s.Length; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                if (s[i] == delimiter) return i;
            }
            return s.Length - 1;
        }

        /// <summary>
        /// CSS string escapes. A backslash before a character means that character; a backslash before up to six
        /// hex digits means that code point, and one space after the digits belongs to the escape rather than to
        /// the text.
        ///
        /// The hex form is not decoration: an icon font is addressed by code point and nothing else -
        /// <c>content: "\f00c"</c> - and it is how Tailwind spells anything unusual in <c>content-['\2014']</c>.
        /// </summary>
        private static void Unescape(string raw, System.Text.StringBuilder sb)
        {
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] != '\\') { sb.Append(raw[i]); continue; }
                if (i + 1 >= raw.Length) return;                 // a trailing backslash escapes nothing

                int digits = 0;
                int code = 0;
                while (digits < 6 && i + 1 + digits < raw.Length && Hex(raw[i + 1 + digits], out int nibble))
                {
                    code = code * 16 + nibble;
                    digits++;
                }

                if (digits == 0) { sb.Append(raw[i + 1]); i++; continue; }

                i += digits;
                if (i + 1 < raw.Length && raw[i + 1] == ' ') i++;

                // Beyond the last code point, or in the surrogate range where there is no character to make.
                if (code == 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF)) sb.Append('�');
                else sb.Append(char.ConvertFromUtf32(code));
            }
        }

        private static bool Hex(char c, out int value)
        {
            if (c >= '0' && c <= '9') { value = c - '0'; return true; }
            if (c >= 'a' && c <= 'f') { value = c - 'a' + 10; return true; }
            if (c >= 'A' && c <= 'F') { value = c - 'A' + 10; return true; }
            value = 0;
            return false;
        }

        // --------------------------------------------------------------------- helpers --

        private static bool TryEdges(string value, out Edges edges)
        {
            edges = Edges.Zero;
            string[] p = ValueParser.SplitTopLevel(value);
            var v = new Len[p.Length];
            for (int i = 0; i < p.Length; i++)
            {
                if (!ValueParser.TryLength(p[i], Context, out v[i])) return false;
            }

            switch (v.Length)
            {
                case 1: edges = Edges.All(v[0]); return true;
                case 2: edges = new Edges { Top = v[0], Right = v[1], Bottom = v[0], Left = v[1] }; return true;
                case 3: edges = new Edges { Top = v[0], Right = v[1], Bottom = v[2], Left = v[1] }; return true;
                case 4: edges = new Edges { Top = v[0], Right = v[1], Bottom = v[2], Left = v[3] }; return true;
                default: return false;
            }
        }

        private static bool TryPx(string value, out float px)
        {
            px = 0f;
            if (!ValueParser.TryLength(value, Context, out Len l)) return false;
            if (l.Unit != LenUnit.Px) return false;
            px = l.Value;
            return true;
        }

        private static string FirstToken(string s)
        {
            string[] p = ValueParser.SplitTopLevel(s);
            return p.Length > 0 ? p[0] : s;
        }

        private static int MatchingParen(string s, int open)
        {
            if (open < 0 || open >= s.Length) return -1;
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static bool Is(string value, string keyword) => string.Equals(value, keyword, StringComparison.OrdinalIgnoreCase);

        private static FlexDirection ParseDirection(string v, FlexDirection fallback) =>
            Is(v, "row") ? FlexDirection.Row :
            Is(v, "row-reverse") ? FlexDirection.RowReverse :
            Is(v, "column") ? FlexDirection.Column :
            Is(v, "column-reverse") ? FlexDirection.ColumnReverse : fallback;

        private static FlexWrap ParseWrap(string v, FlexWrap fallback) =>
            Is(v, "nowrap") ? FlexWrap.NoWrap :
            Is(v, "wrap") ? FlexWrap.Wrap :
            Is(v, "wrap-reverse") ? FlexWrap.WrapReverse : fallback;

        private static Justify ParseJustify(string v, Justify fallback) =>
            Is(v, "flex-start") || Is(v, "start") ? Justify.FlexStart :
            Is(v, "flex-end") || Is(v, "end") ? Justify.FlexEnd :
            Is(v, "center") ? Justify.Center :
            Is(v, "space-between") ? Justify.SpaceBetween :
            Is(v, "space-around") ? Justify.SpaceAround :
            Is(v, "space-evenly") ? Justify.SpaceEvenly : fallback;

        /// <summary>
        /// One alignment keyword, for all four of `align-items`, `align-self`, `justify-items` and `justify-self`.
        ///
        /// `normal` is the initial value of every one of them and behaves as `stretch` in both flexbox and grid,
        /// which is why it maps there rather than being refused - a grid that says it explicitly must not fall
        /// back to whatever the previous declaration left behind.
        /// </summary>
        private static AlignKind ParseAlign(string v, AlignKind fallback) =>
            Is(v, "auto") ? AlignKind.Auto :
            Is(v, "flex-start") || Is(v, "start") || Is(v, "self-start") ? AlignKind.FlexStart :
            Is(v, "flex-end") || Is(v, "end") || Is(v, "self-end") ? AlignKind.FlexEnd :
            Is(v, "center") ? AlignKind.Center :
            Is(v, "stretch") || Is(v, "normal") ? AlignKind.Stretch :
            Is(v, "baseline") ? AlignKind.Baseline : fallback;

        private static OverflowKind ParseOverflow(string v, OverflowKind fallback) =>
            Is(v, "visible") ? OverflowKind.Visible :
            Is(v, "hidden") || Is(v, "clip") ? OverflowKind.Hidden :
            Is(v, "scroll") ? OverflowKind.Scroll :
            Is(v, "auto") ? OverflowKind.Auto : fallback;

        private static int ParseWeight(string v, int fallback)
        {
            if (Is(v, "normal")) return 400;
            if (Is(v, "bold")) return 700;
            if (Is(v, "lighter")) return 300;
            if (Is(v, "bolder")) return 700;
            return ValueParser.TryNumber(v, out float n) ? (int)n : fallback;
        }
    }
}
