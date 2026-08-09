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

            if (Declares(styles, body.Owner?.DocumentElement) || Declares(styles, body)) return;

            style.OverflowX = OverflowKind.Auto;
            style.OverflowY = OverflowKind.Auto;
        }

        /// <summary>Whether this element said anything about overflow at all - any non-initial value counts.</summary>
        private static bool Declares(System.Collections.Generic.Dictionary<IElement, ComputedStyle> styles, IElement element) =>
            element != null && styles.TryGetValue(element, out ComputedStyle s) && s != null
            && (s.OverflowX != OverflowKind.Visible || s.OverflowY != OverflowKind.Visible);
    }
}
