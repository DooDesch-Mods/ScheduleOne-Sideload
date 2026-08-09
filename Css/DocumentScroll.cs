using AngleSharp.Dom;

namespace Sideload.Css
{
    /// <summary>
    /// The rule that lets a whole page scroll: overflow propagation from the root element to the viewport.
    ///
    /// In a browser the document's overflow is not honoured on the document - it is taken off the root element and
    /// applied to the VIEWPORT, and a viewport whose overflow computes to <c>visible</c> behaves as <c>auto</c>
    /// (CSS Overflow 3, section 3.3). That single rule is why every long web page scrolls without anyone writing a
    /// scroll container, and it is the reason a page author never has to think about it.
    ///
    /// Here the rendered root is &lt;body&gt;, sized to the viewport. Its overflow started out <c>visible</c>, so a
    /// page that came out taller than the screen had its lower half painted past the bottom of the phone - visible
    /// over the game world, unreachable, and impossible for the page to notice. Anything a framework renders is
    /// taller than a phone eventually.
    ///
    /// A declared <c>hidden</c> on either &lt;html&gt; or &lt;body&gt; wins and is left alone: a page that says its
    /// document does not scroll gets a document that does not scroll. Either way the overflow stays on screen -
    /// the painter clips the whole pass to the viewport.
    /// </summary>
    internal static class DocumentScroll
    {
        /// <summary>Give &lt;body&gt; the viewport's scrollport unless the document asked for something else.</summary>
        internal static void Propagate(IElement body, System.Collections.Generic.Dictionary<IElement, ComputedStyle> styles)
        {
            if (body == null || styles == null) return;
            if (!styles.TryGetValue(body, out ComputedStyle style) || style == null) return;

            // The ROOT element decides, and &lt;body&gt; only gets a say when the root said nothing - the propagation
            // is from html, and from body only as the fallback CSS spells out for exactly this reason.
            ComputedStyle source = Declared(styles, body.Owner?.DocumentElement) ?? Declared(styles, body) ?? style;

            style.OverflowX = Used(source.OverflowX);
            style.OverflowY = Used(source.OverflowY);
        }

        /// <summary>
        /// One axis of the viewport's overflow, given what the root asked for on both.
        ///
        /// Per axis, and that is the whole of this method. `visible` alongside another `visible` is the plain
        /// document scroll. `visible` alongside anything else computes to `auto` - CSS Overflow 3 section 2.1,
        /// because a box cannot overflow visibly on one axis while clipping on the other.
        ///
        /// That second rule is not a corner: `body { overflow-x: hidden }` is one of the most common lines on the
        /// web, and reading it as "the document declared its overflow, leave it alone" left the vertical axis at
        /// `visible`. The painter then clipped the page at the viewport - either axis hidden clips - and never gave
        /// it a scroll area, so everything below the first screenful was drawn nowhere and reachable by nothing.
        /// </summary>
        private static OverflowKind Used(OverflowKind axis) =>
            axis == OverflowKind.Visible ? OverflowKind.Auto : axis;

        /// <summary>This element's overflow, or null when it never mentioned any.</summary>
        private static ComputedStyle Declared(System.Collections.Generic.Dictionary<IElement, ComputedStyle> styles, IElement element) =>
            element != null && styles.TryGetValue(element, out ComputedStyle s) && s != null
            && (s.OverflowX != OverflowKind.Visible || s.OverflowY != OverflowKind.Visible)
                ? s : null;
    }
}
