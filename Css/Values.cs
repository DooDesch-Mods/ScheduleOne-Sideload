using System.Globalization;

namespace Sideload.Css
{
    // This file - like everything under Css/, Dom/ and Layout/ - must stay free of UnityEngine. The headless test
    // project compiles these sources WITHOUT Unity references, so an accidental engine dependency breaks `dotnet run`
    // immediately instead of only showing up in the game.

    internal enum LenUnit
    {
        /// <summary>Property is not set (max-width: none, border-radius absent, ...).</summary>
        None,
        Px,
        Percent,
        /// <summary>Explicit `auto` - size from content / distribute remaining space.</summary>
        Auto,
    }

    /// <summary>
    /// What a relative length needs to become an absolute one.
    ///
    /// `1rem`, `2em` and `50vh` are not lengths until you know a font size and a viewport, and the layout is the
    /// wrong place to learn them: it would mean carrying the context through every box and every measurement pass.
    /// The cascade already knows both, so relative units are resolved to px THERE and <see cref="Len"/> keeps its
    /// four cases. That is why this struct exists and why it is only ever seen by the applier.
    /// </summary>
    internal struct LengthContext
    {
        /// <summary>This element's font size in px - the basis for `em`.</summary>
        internal float FontSize;

        /// <summary>The root's font size in px - the basis for `rem`. A browser's default is 16; this engine's is
        /// 15, and Tailwind's whole spacing scale is written in rem, so the difference is visible everywhere.</summary>
        internal float RootFontSize;

        internal float ViewportWidth;
        internal float ViewportHeight;

        /// <summary>What a percentage resolves against, or NaN when the caller does not know yet. NaN rather than
        /// zero: a percentage against an unknown basis is unresolvable, and zero would be a silent wrong answer.</summary>
        internal float PercentBasis;

        /// <summary>The values the engine starts from, for a caller that has no element in hand.</summary>
        internal static LengthContext Default => new LengthContext
        {
            FontSize = 15f,
            RootFontSize = 15f,
            ViewportWidth = 733.44f,
            ViewportHeight = 400f,
            PercentBasis = float.NaN,
        };
    }

    /// <summary>A CSS length: a number plus how to read it. Percentages resolve against a base during layout.</summary>
    internal readonly struct Len
    {
        internal readonly float Value;
        internal readonly LenUnit Unit;

        internal Len(float value, LenUnit unit) { Value = value; Unit = unit; }

        internal static readonly Len None = new Len(0f, LenUnit.None);
        internal static readonly Len Auto = new Len(0f, LenUnit.Auto);
        internal static readonly Len Zero = new Len(0f, LenUnit.Px);

        internal static Len Px(float v) => new Len(v, LenUnit.Px);
        internal static Len Percent(float v) => new Len(v, LenUnit.Percent);

        internal bool IsNone => Unit == LenUnit.None;
        internal bool IsAuto => Unit == LenUnit.Auto;
        internal bool IsDefinite => Unit == LenUnit.Px || Unit == LenUnit.Percent;

        /// <summary>Absolute pixels, resolving a percentage against <paramref name="basis"/>. None/auto give <paramref name="fallback"/>.</summary>
        internal float Resolve(float basis, float fallback = 0f)
        {
            switch (Unit)
            {
                case LenUnit.Px: return Value;
                case LenUnit.Percent: return basis * Value * 0.01f;
                default: return fallback;
            }
        }

        public override string ToString() => Unit switch
        {
            LenUnit.Px => Value.ToString("0.###", CultureInfo.InvariantCulture) + "px",
            LenUnit.Percent => Value.ToString("0.###", CultureInfo.InvariantCulture) + "%",
            LenUnit.Auto => "auto",
            _ => "none",
        };
    }

    /// <summary>Straight RGBA in 0..1. Deliberately not UnityEngine.Color - the style layer stays engine-free; the
    /// painter converts at the boundary.</summary>
    internal readonly struct RgbaColor
    {
        internal readonly float R, G, B, A;

        internal RgbaColor(float r, float g, float b, float a = 1f) { R = r; G = g; B = b; A = a; }

        internal static readonly RgbaColor Transparent = new RgbaColor(0f, 0f, 0f, 0f);
        internal static readonly RgbaColor Black = new RgbaColor(0f, 0f, 0f);
        internal static readonly RgbaColor White = new RgbaColor(1f, 1f, 1f);

        internal bool IsTransparent => A <= 0.0001f;

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "rgba({0:0.###}, {1:0.###}, {2:0.###}, {3:0.###})", R, G, B, A);

        public override bool Equals(object obj) =>
            obj is RgbaColor o && Near(R, o.R) && Near(G, o.G) && Near(B, o.B) && Near(A, o.A);

        public override int GetHashCode() => R.GetHashCode() ^ G.GetHashCode() ^ B.GetHashCode() ^ A.GetHashCode();

        private static bool Near(float a, float b) => a - b < 0.002f && b - a < 0.002f;
    }

    /// <summary>Four sides, CSS order (top, right, bottom, left).</summary>
    internal struct Edges
    {
        internal Len Top, Right, Bottom, Left;

        internal static Edges All(Len v) => new Edges { Top = v, Right = v, Bottom = v, Left = v };
        internal static Edges Zero => All(Len.Zero);

        public override string ToString() => $"{Top} {Right} {Bottom} {Left}";
    }

    /// <summary>Four corner radii, CSS order (top-left, top-right, bottom-right, bottom-left).</summary>
    internal struct Corners
    {
        internal float TopLeft, TopRight, BottomRight, BottomLeft;

        internal static Corners All(float v) => new Corners { TopLeft = v, TopRight = v, BottomRight = v, BottomLeft = v };
        internal bool IsZero => TopLeft == 0f && TopRight == 0f && BottomRight == 0f && BottomLeft == 0f;
    }

    internal enum DisplayKind { Flex, None }
    internal enum FlexDirection { Row, RowReverse, Column, ColumnReverse }
    internal enum FlexWrap { NoWrap, Wrap, WrapReverse }
    internal enum Justify { FlexStart, FlexEnd, Center, SpaceBetween, SpaceAround, SpaceEvenly }
    internal enum AlignKind { Auto, FlexStart, FlexEnd, Center, Stretch, Baseline }
    /// <summary>
    /// <c>Fixed</c> is the TOP LAYER: taken out of the flow like <c>Absolute</c>, but positioned against the viewport
    /// instead of the parent, painted after everything else, and clipped by nothing. That last part is the reason it
    /// is a separate kind rather than a flag - an overlay written next to the thing it belongs to sits inside some
    /// scrolling pane, and as an absolute box it would be cropped to that pane and scroll away with it.
    /// </summary>
    internal enum PositionKind { Static, Absolute, Fixed }
    internal enum OverflowKind { Visible, Hidden, Scroll, Auto }

    /// <summary>The easing curves worth having. `linear` for a value that must read as constant, the ease family for
    /// anything a person looks at - a UI that starts and stops abruptly reads as broken rather than as fast.</summary>
    internal enum EasingKind { Linear, EaseIn, EaseOut, EaseInOut }
    internal enum TextAlignKind { Left, Center, Right }
    /// <summary>
    /// How a text run treats the whitespace it was written with, and whether it may wrap.
    ///
    /// The two questions are separate in CSS and the four values are the useful corners of both:
    ///
    ///   Normal   collapse runs of whitespace, wrap        the default, and what prose wants
    ///   NoWrap   collapse runs of whitespace, never wrap  a label that must stay on one line
    ///   Pre      keep every space and newline, never wrap a terminal, a code block, an aligned column
    ///   PreWrap  keep every space and newline, may wrap   preformatted text that still has to fit
    /// </summary>
    internal enum WhiteSpaceKind { Normal, NoWrap, Pre, PreWrap }
    internal enum FontStyleKind { Normal, Italic }

    /// <summary>Which viewport shape a `@media (orientation: ...)` block applies to.</summary>
    internal enum Orientation { Portrait, Landscape }
}
