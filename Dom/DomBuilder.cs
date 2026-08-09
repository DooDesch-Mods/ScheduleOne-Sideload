using System.Text;
using AngleSharp.Dom;
using Sideload.Css;
using Sideload.Layout;

namespace Sideload.Dom
{
    /// <summary>
    /// Turns a styled DOM into a layout tree.
    ///
    /// The one interesting decision here is inline content: an element whose children are only text and inline
    /// markup collapses into a SINGLE text leaf carrying TextMeshPro rich text. That is what buys flowing text
    /// ("Du hast &lt;b&gt;500$&lt;/b&gt; verdient" wrapping as one paragraph) inside an engine whose only layout
    /// model is flexbox - without it, every &lt;b&gt; would become its own flex item and break the line.
    ///
    /// The other thing built here is the boxes that have no DOM node at all: <c>::before</c> and <c>::after</c>.
    /// They come out of the cascade as a style each and are hung on the originating element as its first and last
    /// child. In a browser they are INLINE and flow with the text; here they are flex items of the element, so
    /// they stack the way that element stacks its children rather than sitting on the text baseline. The case
    /// this exists for - an empty box with a size and a background: a badge dot, a divider, an overlay - does not
    /// notice, and one that would notice usually says `position: absolute` anyway.
    /// </summary>
    internal static class DomBuilder
    {
        private static readonly HashSet<string> InlineTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "b", "strong", "i", "em", "u", "span", "br", "small", "code", "a",
        };

        private static readonly HashSet<string> SkippedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "head", "script", "style", "title", "meta", "link",
        };

        internal static LayoutNode Build(IElement element, Dictionary<IElement, ComputedStyle> styles,
                                         PseudoStyles generated = null)
        {
            if (element == null) return null;
            if (SkippedTags.Contains(element.LocalName)) return null;

            ComputedStyle style = Style(element, styles);
            if (style.Display == DisplayKind.None) return null;

            LayoutNode before = Generated(element, generated, PseudoElement.Before);
            LayoutNode after = Generated(element, generated, PseudoElement.After);

            if (IsInlineOnly(element, style))
            {
                string text = CompileInline(element, styles, style);
                if (before == null && after == null)
                    return string.IsNullOrEmpty(text) ? null : new LayoutNode(style, text) { Tag = element };

                // A node carries text OR children, so an element that folded into a text leaf cannot hold a
                // generated box as well. It becomes a container and the WHOLE run moves inside it as one leaf:
                // splitting the run to make room would undo the fold, and the fold is the only reason a sentence
                // with a &lt;b&gt; in it wraps as one paragraph instead of breaking into a flex item per tag.
                var folded = new LayoutNode(style) { Tag = element };
                if (before != null) folded.Add(before);
                if (!string.IsNullOrEmpty(text)) folded.Add(new LayoutNode(Contents(style), text) { Tag = element });
                if (after != null) folded.Add(after);
                return folded;
            }

            var node = new LayoutNode(style) { Tag = element };
            if (before != null) node.Add(before);

            foreach (INode child in element.ChildNodes)
            {
                if (child.NodeType == NodeType.Text)
                {
                    // Text sitting directly among element children becomes its own leaf; it inherits the parent's
                    // style, which is exactly what an anonymous box does in CSS. This one IS a whole block, so the
                    // edge whitespace goes - unless the style preserves it, where the edges are content.
                    string raw = Preserves(style) ? child.TextContent : Normalise(child.TextContent).Trim();
                    if (raw.Length == 0) continue;
                    node.Add(new LayoutNode(style, Transform(raw, style)) { Tag = element });
                    continue;
                }

                if (child is IElement childElement)
                {
                    LayoutNode built = Build(childElement, styles, generated);
                    if (built != null) node.Add(built);
                }
            }

            if (after != null) node.Add(after);
            return node;
        }

        /// <summary>
        /// The box a <c>::before</c> or <c>::after</c> rule asked for, or null when the sheet asked for none.
        ///
        /// The cascade only hands one over once <c>content</c> has been set, so there is nothing left to test here
        /// beyond <c>display: none</c>. A string becomes a text leaf; an empty string becomes an empty box, which
        /// is the badge dot and the divider and why <c>content: ""</c> has to mean something.
        ///
        /// No Tag, on purpose. A generated box has no DOM node, and handing it the originating element would make
        /// the painter treat it AS that element: reuse the element's input field, paint its &lt;img&gt; a second
        /// time, and take over its entry in the painted map, which is what hover restyling looks the box up in.
        ///
        /// On an &lt;input&gt;, a &lt;textarea&gt; or an &lt;img&gt; the box is built and then never drawn, because
        /// the painter stops at those without walking their children. A browser generates nothing there at all;
        /// the difference is that the box still takes up room here.
        /// </summary>
        private static LayoutNode Generated(IElement element, PseudoStyles generated, PseudoElement which)
        {
            ComputedStyle style = generated?.Get(element, which);
            if (style == null || style.Display == DisplayKind.None) return null;

            return style.Content.Length == 0
                ? new LayoutNode(style)
                : new LayoutNode(style, Escape(style.Content));
        }

        /// <summary>
        /// The element's style with everything belonging to its BOX taken back out.
        ///
        /// For the run of text that moves inside an element which grew a generated box. The element's own node is
        /// now the container around it, so leaving the padding, border, background, size and transform on the text
        /// as well would draw each of them twice and inset the words by double the padding. What stays is what a
        /// run of text is made of: the font, the colour, the alignment and the whitespace handling.
        /// </summary>
        private static ComputedStyle Contents(ComputedStyle style)
        {
            ComputedStyle inner = style.Clone();

            inner.Padding = Edges.Zero;
            inner.Margin = Edges.Zero;
            inner.BorderWidth = Edges.Zero;
            inner.BorderColor = RgbaColor.Transparent;
            inner.BorderRadius = default;
            inner.BackgroundColor = RgbaColor.Transparent;
            inner.HasGradient = false;
            inner.HasShadow = false;

            inner.Width = Len.Auto;
            inner.Height = Len.Auto;
            inner.MinWidth = Len.None;
            inner.MinHeight = Len.None;
            inner.MaxWidth = Len.None;
            inner.MaxHeight = Len.None;

            inner.Position = PositionKind.Static;
            inner.Inset = new Edges { Top = Len.Auto, Right = Len.Auto, Bottom = Len.Auto, Left = Len.Auto };
            inner.OverflowX = OverflowKind.Visible;
            inner.OverflowY = OverflowKind.Visible;

            inner.FlexGrow = 0f;
            inner.FlexShrink = 1f;
            inner.FlexBasis = Len.Auto;
            inner.AlignSelf = AlignKind.Auto;

            inner.TranslateX = 0f;
            inner.TranslateY = 0f;
            inner.ScaleX = 1f;
            inner.ScaleY = 1f;
            inner.RotateDeg = 0f;

            inner.Content = null;
            return inner;
        }

        /// <summary>
        /// True when this element is a run of text rather than a container.
        ///
        /// Normally it must contain DIRECT text of its own - inline children alone are not enough. Every element is a
        /// flex container in this engine, so a bar holding two spans genuinely has two flex items to lay out;
        /// collapsing it into one string would silently discard justify-content and glue the two labels together.
        ///
        /// Preserved whitespace overrides that, because it says the opposite. An author who writes
        /// <c>white-space: pre</c> has declared that the spaces between these children are content, and spaces are
        /// only content inside a run of text. Without this a block of nothing but coloured spans - a syntax-
        /// highlighted line, a terminal row where every part is a colour - came out as one full-width box per span,
        /// stacked down the screen instead of laid along the line.
        /// </summary>
        private static bool IsInlineOnly(IElement element, ComputedStyle style)
        {
            bool preserves = Preserves(style);
            bool sawDirectText = false;

            foreach (INode child in element.ChildNodes)
            {
                switch (child.NodeType)
                {
                    case NodeType.Text:
                        // A run of nothing but spaces IS content when whitespace is preserved - an indented blank
                        // line in a transcript is a line, and dropping it shortens the block by a row.
                        if (preserves
                            ? child.TextContent.Length > 0
                            : Normalise(child.TextContent).Trim().Length > 0) sawDirectText = true;
                        continue;

                    case NodeType.Element:
                        if (!InlineTags.Contains(((IElement)child).LocalName)) return false;
                        continue;

                    case NodeType.Comment:
                        continue;

                    default:
                        return false;
                }
            }

            return sawDirectText || preserves;
        }

        private static string CompileInline(IElement element, Dictionary<IElement, ComputedStyle> styles, ComputedStyle inherited)
        {
            var sb = new StringBuilder();
            AppendInline(element, styles, inherited, sb);

            // Preformatted text is not trimmed either. Leading spaces are the first column of an indented line, and a
            // trailing newline is a blank last row - both are content, and a terminal notices immediately when they
            // are quietly removed.
            return Preserves(inherited) ? sb.ToString() : sb.ToString().Trim();
        }

        /// <summary>Whether this style keeps the whitespace it was written with, rather than collapsing runs of it
        /// into a single space the way prose does.</summary>
        private static bool Preserves(ComputedStyle style) =>
            style != null && (style.WhiteSpace == WhiteSpaceKind.Pre || style.WhiteSpace == WhiteSpaceKind.PreWrap);

        // Spaces next to rich-text tags: FIXED, and verified in the game - "keeps <b>bold</b>, <i>italic</i> and
        // <span>coloured</span> runs" renders with every space intact. The cause was here, not in TextMeshPro: each
        // text fragment was trimmed on its own, which deleted exactly the space separating a word from a following
        // tag. Normalise now collapses without trimming and CompileInline trims the assembled block once.
        //
        // Replacing those spaces with U+00A0 was tried and REVERTED, and must not come back: a non-breaking space is
        // not a line-break opportunity, so a sentence with several inline runs became one unbreakable word - TMP then
        // reported a single line for a paragraph that renders on two and every card sized itself one line short.

        private static void AppendInline(IElement element, Dictionary<IElement, ComputedStyle> styles,
                                         ComputedStyle inherited, StringBuilder sb)
        {
            foreach (INode child in element.ChildNodes)
            {
                if (child.NodeType == NodeType.Text)
                {
                    string text = child.TextContent;
                    sb.Append(Escape(Transform(Preserves(inherited) ? text : Normalise(text), inherited)));
                    continue;
                }

                if (child is not IElement el) continue;

                string tag = el.LocalName.ToLowerInvariant();
                if (tag == "br") { sb.Append('\n'); continue; }

                ComputedStyle style = Style(el, styles);

                string open = "", close = "";
                if (tag == "b" || tag == "strong" || style.FontWeight >= 600) { open += "<b>"; close = "</b>" + close; }
                if (tag == "i" || tag == "em" || style.FontStyle == FontStyleKind.Italic) { open += "<i>"; close = "</i>" + close; }
                if (tag == "u") { open += "<u>"; close = "</u>" + close; }

                // Only emit a colour tag when it actually differs, otherwise every span would bloat the string.
                if (!style.Color.Equals(inherited.Color))
                {
                    open += "<color=#" + Hex(style.Color) + ">";
                    close = "</color>" + close;
                }

                sb.Append(open);
                AppendInline(el, styles, style, sb);
                sb.Append(close);
            }
        }

        private static ComputedStyle Style(IElement element, Dictionary<IElement, ComputedStyle> styles) =>
            styles != null && styles.TryGetValue(element, out ComputedStyle s) ? s : new ComputedStyle();

        /// <summary>
        /// Collapse whitespace runs into single spaces the way `white-space: normal` does - and keep the ones at the
        /// edges. A text run is only ever a FRAGMENT of its paragraph: trimming here deletes exactly the space that
        /// separates "dabei" from a following &lt;b&gt;, and the words end up glued together. Trimming belongs to the
        /// assembled block, which <see cref="CompileInline"/> does once at the end.
        /// </summary>
        internal static string Normalise(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var sb = new StringBuilder(text.Length);
            bool lastWasSpace = false;

            foreach (char c in text)
            {
                bool space = c == ' ' || c == '\t' || c == '\n' || c == '\r';
                if (space)
                {
                    if (!lastWasSpace) sb.Append(' ');
                    lastWasSpace = true;
                    continue;
                }
                sb.Append(c);
                lastWasSpace = false;
            }

            return sb.ToString();
        }

        /// <summary>
        /// `text-transform`, applied to a FRAGMENT rather than to the compiled line.
        ///
        /// That distinction is the whole of it: an inline run is compiled into one TextMeshPro string carrying
        /// rich-text tags, and upper-casing that string would upper-case `&lt;color=#7ee787&gt;` along with the
        /// words. Done per fragment it also gets the CSS meaning right, where each element transforms its own
        /// text and a nested span may ask for something else.
        /// </summary>
        internal static string Transform(string text, ComputedStyle style)
        {
            if (string.IsNullOrEmpty(text) || style == null) return text;

            switch (style.TextTransform)
            {
                case TextTransformKind.Uppercase: return text.ToUpperInvariant();
                case TextTransformKind.Lowercase: return text.ToLowerInvariant();

                case TextTransformKind.Capitalize:
                {
                    var sb = new StringBuilder(text.Length);
                    bool startOfWord = true;

                    foreach (char c in text)
                    {
                        sb.Append(startOfWord ? char.ToUpperInvariant(c) : c);
                        startOfWord = !char.IsLetterOrDigit(c);
                    }
                    return sb.ToString();
                }

                default: return text;
            }
        }

        /// <summary>A literal '&lt;' in page text must not be read as a rich-text tag.</summary>
        private static string Escape(string text) => text.Replace("<", "<noparse><</noparse>");

        private static string Hex(RgbaColor c)
        {
            int r = (int)Math.Round(Math.Clamp(c.R, 0f, 1f) * 255f);
            int g = (int)Math.Round(Math.Clamp(c.G, 0f, 1f) * 255f);
            int b = (int)Math.Round(Math.Clamp(c.B, 0f, 1f) * 255f);
            return r.ToString("X2") + g.ToString("X2") + b.ToString("X2");
        }
    }
}
