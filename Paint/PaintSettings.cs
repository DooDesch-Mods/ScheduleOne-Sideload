namespace Sideload.Paint
{
    /// <summary>
    /// The two globals the painter reads while it draws, carried with the box that is about to be painted rather
    /// than left standing wherever the last view put them.
    ///
    /// Both are documented as "published by the view that is currently painting", and that is true exactly once per
    /// view per frame - inside <see cref="Host.WebView"/>'s render. Painting also happens OUTSIDE it: a transition
    /// repaints from <see cref="Transitions.Tick"/>, and an immediate state change repaints from
    /// <see cref="Transitions.To"/>. Those run after every mounted view has published in turn, so with two pages on
    /// different canvas kinds whichever rendered last decided how the other one's fade was coloured, and with two
    /// pages at different scales the same for its hairlines.
    ///
    /// Neither value belongs to the last view built. They belong to the box under the brush, which is what this
    /// carries.
    /// </summary>
    internal readonly struct PaintSettings
    {
        /// <summary>See <see cref="BoxRenderer.ConvertToLinear"/>.</summary>
        internal readonly bool ConvertToLinear;

        /// <summary>See <see cref="Painter.CssToDevice"/>.</summary>
        internal readonly float CssToDevice;

        internal PaintSettings(bool convertToLinear, float cssToDevice)
        {
            ConvertToLinear = convertToLinear;
            CssToDevice = cssToDevice;
        }

        /// <summary>Put them in force for the paint that follows.</summary>
        internal void Apply()
        {
            BoxRenderer.ConvertToLinear = ConvertToLinear;
            Painter.CssToDevice = CssToDevice;
        }
    }
}
