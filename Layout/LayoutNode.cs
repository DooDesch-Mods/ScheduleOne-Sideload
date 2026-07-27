using Sideload.Css;

namespace Sideload.Layout
{
    // Unity-free, like everything under Css/, Dom/ and Layout/ - the headless tests compile this without any engine
    // reference, which is what keeps the layout algorithm testable at all.

    internal readonly struct Size
    {
        internal readonly float Width, Height;
        internal Size(float width, float height) { Width = width; Height = height; }
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

        // --- results, relative to the PARENT's padding box, top-left origin, y growing downwards ---
        internal float X, Y, Width, Height;

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
