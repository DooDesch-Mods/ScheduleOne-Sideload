using Sideload.Css;

namespace Sideload.Layout
{
    // Unity-free, like everything under Css/, Dom/ and Layout/ - the headless tests compile this without any engine
    // reference, which is what keeps the layout algorithm testable at all.

    internal readonly struct Size
    {
        internal readonly float Width, Height;

        /// <summary>
        /// Where the FIRST baseline sits, measured down from the top of this box. <see cref="float.NaN"/> when the
        /// measurer cannot say.
        ///
        /// A third number rather than a second call because it comes free with the measurement and would otherwise
        /// have to be asked for again per item, per pass - and because a baseline that could drift out of the height
        /// it belongs to is worse than no baseline at all.
        /// </summary>
        internal readonly float Baseline;

        /// <summary>
        /// The width the text was actually wrapped into, which can be WIDER than what was available.
        ///
        /// That happens for the browser's default wrapping: a word too long for the box is not cut, it hangs out
        /// of it. The measurer is the only thing that can work out how far - it is the one holding the font - and
        /// the painter has to give the text the same room or the two would disagree about the line count.
        /// </summary>
        internal readonly float WrapWidth;

        internal Size(float width, float height, float baseline = float.NaN, float wrapWidth = float.NaN)
        {
            Width = width;
            Height = height;
            Baseline = baseline;
            WrapWidth = wrapWidth;
        }

        public override string ToString() => $"{Width:0.##} x {Height:0.##}";
    }

    /// <summary>
    /// How the layout engine asks how big a piece of text is. The real implementation measures with TextMeshPro; the
    /// tests use a deterministic stand-in, which is the only reason ~60 layout cases can run in milliseconds.
    /// </summary>
    internal interface IMeasureText
    {
        /// <summary>
        /// Size of <paramref name="text"/> when wrapped into <paramref name="availableWidth"/>. The width may be
        /// <see cref="float.PositiveInfinity"/> for "do not wrap".
        /// </summary>
        Size Measure(string text, ComputedStyle style, float availableWidth);
    }

    /// <summary>
    /// One box in the layout tree. Input is <see cref="Style"/> plus either children or text; output are the four
    /// result fields, filled in by <see cref="FlexLayout"/>.
    /// </summary>
    internal sealed class LayoutNode
    {
        internal ComputedStyle Style = new ComputedStyle();

        /// <summary>Text content when this is a leaf. Mutually exclusive with <see cref="Children"/> in practice.</summary>
        internal string Text;

        internal readonly List<LayoutNode> Children = new List<LayoutNode>();

        /// <summary>Whatever the caller wants to associate - the DOM element for the painter, nothing for tests.</summary>
        internal object Tag;

        /// <summary>
        /// `::placeholder` on a form control, or null when the page never styled one.
        ///
        /// Carried on the node rather than looked up later because the painter has no cascade to ask: the hint
        /// text is not a box, so it never became a LayoutNode, and this is the only way its style reaches paint.
        /// </summary>
        internal Css.ComputedStyle PlaceholderStyle;

        // --- results, relative to the PARENT's padding box, top-left origin, y growing downwards ---
        internal float X, Y, Width, Height;

        /// <summary>
        /// This box's first baseline, measured down from its own TOP BORDER EDGE. <see cref="float.NaN"/> when the
        /// box has no text to take one from - an icon, a spacer, a container of empty boxes.
        ///
        /// Filled in by <see cref="FlexLayout.LayoutBox"/> alongside the width and height: a text leaf reads it off
        /// the measurement, a container inherits it from its first in-flow child that has one. That recursion is
        /// what lets `align-items: baseline` line up a heading with a note that is wrapped in two divs.
        /// </summary>
        internal float Baseline = float.NaN;

        /// <summary>
        /// How wide the text inside this leaf was laid out, when that is WIDER than the box itself -
        /// <see cref="float.NaN"/> whenever it fits, which is nearly always.
        ///
        /// A word longer than its box overflows in a browser rather than being cut in half, and the painter has to
        /// hand TextMeshPro the same width the measurement used or the drawn line count would not match the
        /// measured height. Whatever clips the box clips the overhang, exactly as elsewhere.
        /// </summary>
        internal float TextWrapWidth = float.NaN;

        internal LayoutNode() { }

        internal LayoutNode(ComputedStyle style, string text = null)
        {
            Style = style ?? new ComputedStyle();
            Text = text;
        }

        internal LayoutNode Add(LayoutNode child)
        {
            Children.Add(child);
            return this;
        }

        internal bool IsTextLeaf => Text != null && Children.Count == 0;

        public override string ToString() => $"({X:0.##},{Y:0.##}) {Width:0.##}x{Height:0.##}";
    }
}
