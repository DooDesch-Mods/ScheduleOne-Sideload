namespace Sideload.Css
{
    /// <summary>
    /// Applies one declaration to a <see cref="ComputedStyle"/>, expanding shorthands on the way. This is where the
    /// engine's supported property set is actually defined: an unknown property, or a value that does not parse, is
    /// dropped silently - the same "ignore the bad declaration, keep the rest" behaviour a browser has.
    /// </summary>
    internal static class StyleApplier
    {
        internal static void Apply(ComputedStyle s, string property, string value)
        {
            if (s == null || string.IsNullOrEmpty(property) || value == null) return;

            property = property.Trim().ToLowerInvariant();
            value = value.Trim();
            if (value.Length == 0) return;

            switch (property)
            {
                // ---------------------------------------------------------------- layout --
                case "display":
                    if (Is(value, "none")) s.Display = DisplayKind.None;
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
                case "flex-grow": if (ValueParser.TryNumber(value, out float g)) s.FlexGrow = g; break;
                case "flex-shrink": if (ValueParser.TryNumber(value, out float sh)) s.FlexShrink = sh; break;
                case "flex-basis": if (ValueParser.TryLength(value, out Len fb)) s.FlexBasis = fb; break;

                case "justify-content": s.JustifyContent = ParseJustify(value, s.JustifyContent); break;
                case "align-items": s.AlignItems = ParseAlign(value, s.AlignItems); break;
                case "align-self": s.AlignSelf = ParseAlign(value, s.AlignSelf); break;

                case "gap":
                {
                    string[] p = ValueParser.SplitTopLevel(value);
                    if (p.Length >= 1 && ValueParser.TryLength(p[0], out Len rg)) { s.RowGap = rg; s.ColumnGap = rg; }
                    if (p.Length >= 2 && ValueParser.TryLength(p[1], out Len cg)) s.ColumnGap = cg;
                    break;
                }
                case "row-gap": if (ValueParser.TryLength(value, out Len rgap)) s.RowGap = rgap; break;
                case "column-gap": if (ValueParser.TryLength(value, out Len cgap)) s.ColumnGap = cgap; break;

                case "padding": if (TryEdges(value, out Edges pad)) s.Padding = pad; break;
                case "padding-top": if (ValueParser.TryLength(value, out Len pt)) s.Padding.Top = pt; break;
                case "padding-right": if (ValueParser.TryLength(value, out Len pr)) s.Padding.Right = pr; break;
                case "padding-bottom": if (ValueParser.TryLength(value, out Len pb)) s.Padding.Bottom = pb; break;
                case "padding-left": if (ValueParser.TryLength(value, out Len pl)) s.Padding.Left = pl; break;

                case "margin": if (TryEdges(value, out Edges mar)) s.Margin = mar; break;
                case "margin-top": if (ValueParser.TryLength(value, out Len mt)) s.Margin.Top = mt; break;
                case "margin-right": if (ValueParser.TryLength(value, out Len mr)) s.Margin.Right = mr; break;
                case "margin-bottom": if (ValueParser.TryLength(value, out Len mb)) s.Margin.Bottom = mb; break;
                case "margin-left": if (ValueParser.TryLength(value, out Len ml)) s.Margin.Left = ml; break;

                case "width": if (ValueParser.TryLength(value, out Len w)) s.Width = w; break;
                case "height": if (ValueParser.TryLength(value, out Len h)) s.Height = h; break;
                case "min-width": if (ValueParser.TryLength(value, out Len mnw)) s.MinWidth = mnw; break;
                case "min-height": if (ValueParser.TryLength(value, out Len mnh)) s.MinHeight = mnh; break;
                case "max-width": if (ValueParser.TryLength(value, out Len mxw)) s.MaxWidth = mxw; break;
                case "max-height": if (ValueParser.TryLength(value, out Len mxh)) s.MaxHeight = mxh; break;

                case "position":
                    if (Is(value, "absolute") || Is(value, "fixed")) s.Position = PositionKind.Absolute;
                    else if (Is(value, "static") || Is(value, "relative")) s.Position = PositionKind.Static;
                    break;

                case "inset": if (TryEdges(value, out Edges inset)) s.Inset = inset; break;
                case "top": if (ValueParser.TryLength(value, out Len it)) s.Inset.Top = it; break;
                case "right": if (ValueParser.TryLength(value, out Len ir)) s.Inset.Right = ir; break;
                case "bottom": if (ValueParser.TryLength(value, out Len ib)) s.Inset.Bottom = ib; break;
                case "left": if (ValueParser.TryLength(value, out Len il)) s.Inset.Left = il; break;

                case "overflow": s.OverflowX = s.OverflowY = ParseOverflow(value, s.OverflowX); break;
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
                case "border-width": if (TryEdges(value, out Edges bw)) s.BorderWidth = bw; break;
                case "border-color": if (ValueParser.TryColor(value, out RgbaColor bc)) s.BorderColor = bc; break;
                case "border-style": break;   // only `solid` is drawable; the value carries no other information here
                case "border-top-width": if (ValueParser.TryLength(value, out Len btw)) s.BorderWidth.Top = btw; break;
                case "border-right-width": if (ValueParser.TryLength(value, out Len brw)) s.BorderWidth.Right = brw; break;
                case "border-bottom-width": if (ValueParser.TryLength(value, out Len bbw)) s.BorderWidth.Bottom = bbw; break;
                case "border-left-width": if (ValueParser.TryLength(value, out Len blw)) s.BorderWidth.Left = blw; break;

                case "border-radius": ApplyRadius(s, value); break;
                case "border-top-left-radius": if (TryPx(value, out float r1)) s.BorderRadius.TopLeft = r1; break;
                case "border-top-right-radius": if (TryPx(value, out float r2)) s.BorderRadius.TopRight = r2; break;
                case "border-bottom-right-radius": if (TryPx(value, out float r3)) s.BorderRadius.BottomRight = r3; break;
                case "border-bottom-left-radius": if (TryPx(value, out float r4)) s.BorderRadius.BottomLeft = r4; break;

                case "box-shadow": ApplyShadow(s, value); break;
                case "transform": ApplyTransform(s, value); break;
                case "transition": ApplyTransition(s, value); break;
                case "transition-duration": s.TransitionSeconds = Seconds(value); break;
                case "transition-delay": s.TransitionDelaySeconds = Seconds(value); break;
                case "transition-timing-function": s.TransitionEasing = Easing(value); break;
                case "transition-property": break;   // every animatable property transitions; see ApplyTransition

                case "opacity": if (ValueParser.TryNumber(value, out float op)) s.Opacity = op < 0f ? 0f : (op > 1f ? 1f : op); break;

                // ------------------------------------------------------------------ text --
                case "color": if (ValueParser.TryColor(value, out RgbaColor col)) s.Color = col; break;
                case "font-family": s.FontFamily = FirstFamily(value); break;
                case "font-size": if (TryPx(value, out float fs)) s.FontSize = fs; break;
                case "font-weight": s.FontWeight = ParseWeight(value, s.FontWeight); break;
                case "font-style": s.FontStyle = Is(value, "italic") || Is(value, "oblique") ? FontStyleKind.Italic : FontStyleKind.Normal; break;
                case "line-height": ApplyLineHeight(s, value); break;
                case "letter-spacing": if (Is(value, "normal")) s.LetterSpacing = 0f; else if (TryPx(value, out float ls)) s.LetterSpacing = ls; break;

                // Sideload's own, hence the prefix: the web reaches monospace by naming a family, and there is no
                // monospace family here to name. `normal` turns it back off, so a subtree can opt out of an inherited
                // grid the way `letter-spacing: normal` does.
                case "-s1-mono-advance":
                    if (Is(value, "normal") || Is(value, "none")) s.MonoAdvance = 0f;
                    else if (TryPx(value, out float adv)) s.MonoAdvance = adv < 0f ? 0f : adv;
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
            }
        }

        // ------------------------------------------------------------------ shorthands --

        private static void ApplyFlex(ComputedStyle s, string value)
        {
            if (Is(value, "none")) { s.FlexGrow = 0f; s.FlexShrink = 0f; s.FlexBasis = Len.Auto; return; }
            if (Is(value, "auto")) { s.FlexGrow = 1f; s.FlexShrink = 1f; s.FlexBasis = Len.Auto; return; }

            string[] p = ValueParser.SplitTopLevel(value);
            if (p.Length == 1 && ValueParser.TryNumber(p[0], out float single))
            {
                // `flex: 1` is grow 1 / shrink 1 / basis 0% - the basis is the part people forget, and the reason a
                // single flex:1 child fills its line instead of hugging its content.
                s.FlexGrow = single; s.FlexShrink = 1f; s.FlexBasis = Len.Percent(0f);
                return;
            }

            if (p.Length >= 1 && ValueParser.TryNumber(p[0], out float grow)) s.FlexGrow = grow;
            if (p.Length >= 2 && ValueParser.TryNumber(p[1], out float shrink)) s.FlexShrink = shrink;
            if (p.Length >= 3 && ValueParser.TryLength(p[2], out Len basis)) s.FlexBasis = basis;
            else if (p.Length == 2 && ValueParser.TryLength(p[1], out Len basis2)) { s.FlexBasis = basis2; s.FlexShrink = 1f; }
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
            index < args.Length && ValueParser.TryLength(args[index].Trim(), out Len len) ? len.Resolve(0f) : 0f;

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
                if (ValueParser.TryLength(part, out Len w) && w.IsDefinite) { SetWidth(s, side, w); continue; }
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
                if (!TryPx(p[i], out v[i])) return;
            }

            switch (v.Length)
            {
                case 1: s.BorderRadius = Corners.All(v[0]); break;
                case 2: s.BorderRadius = new Corners { TopLeft = v[0], TopRight = v[1], BottomRight = v[0], BottomLeft = v[1] }; break;
                case 3: s.BorderRadius = new Corners { TopLeft = v[0], TopRight = v[1], BottomRight = v[2], BottomLeft = v[1] }; break;
                case 4: s.BorderRadius = new Corners { TopLeft = v[0], TopRight = v[1], BottomRight = v[2], BottomLeft = v[3] }; break;
            }
        }

        private static void ApplyShadow(ComputedStyle s, string value)
        {
            if (Is(value, "none")) { s.HasShadow = false; return; }

            string[] p = ValueParser.SplitTopLevel(value);
            float x = 0f, y = 0f, blur = 0f;
            var color = new RgbaColor(0f, 0f, 0f, 0.5f);
            int lengths = 0;

            foreach (string part in p)
            {
                if (Is(part, "inset")) return;   // inset shadows are not drawable with the outer-shadow pass
                if (lengths < 3 && ValueParser.TryLength(part, out Len l) && l.Unit == LenUnit.Px)
                {
                    if (lengths == 0) x = l.Value;
                    else if (lengths == 1) y = l.Value;
                    else blur = l.Value;
                    lengths++;
                    continue;
                }
                if (ValueParser.TryColor(part, out RgbaColor c)) color = c;
            }

            if (lengths < 2) return;   // offset-x and offset-y are mandatory in CSS

            s.HasShadow = true;
            s.ShadowOffsetX = x;
            s.ShadowOffsetY = y;
            s.ShadowBlur = blur;
            s.ShadowColor = color;
        }

        private static void ApplyLineHeight(ComputedStyle s, string value)
        {
            if (Is(value, "normal")) { s.LineHeight = Len.None; return; }
            if (ValueParser.TryLength(value, out Len l) && l.IsDefinite) { s.LineHeight = l; return; }

            // A unitless line-height is a multiplier of the font size, which is exactly what a percentage resolves to
            // here - storing it that way keeps it correct when font-size changes later in the cascade.
            if (ValueParser.TryNumber(value, out float n)) s.LineHeight = Len.Percent(n * 100f);
        }

        // --------------------------------------------------------------------- helpers --

        private static bool TryEdges(string value, out Edges edges)
        {
            edges = Edges.Zero;
            string[] p = ValueParser.SplitTopLevel(value);
            var v = new Len[p.Length];
            for (int i = 0; i < p.Length; i++)
            {
                if (!ValueParser.TryLength(p[i], out v[i])) return false;
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
            if (!ValueParser.TryLength(value, out Len l)) return false;
            if (l.Unit != LenUnit.Px) return false;
            px = l.Value;
            return true;
        }

        private static string FirstFamily(string value)
        {
            string[] p = ValueParser.SplitTopLevel(value, commaSeparated: true);
            if (p.Length == 0) return "game-ui";
            return p[0].Trim().Trim('"', '\'');
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

        private static AlignKind ParseAlign(string v, AlignKind fallback) =>
            Is(v, "auto") ? AlignKind.Auto :
            Is(v, "flex-start") || Is(v, "start") ? AlignKind.FlexStart :
            Is(v, "flex-end") || Is(v, "end") ? AlignKind.FlexEnd :
            Is(v, "center") ? AlignKind.Center :
            Is(v, "stretch") ? AlignKind.Stretch :
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
