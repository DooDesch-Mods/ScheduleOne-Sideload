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

        internal static LayoutNode Build(IElement element, Dictionary<IElement, ComputedStyle> styles)
        {
            if (element == null) return null;
            if (SkippedTags.Contains(element.LocalName)) return null;

            ComputedStyle style = Style(element, styles);
            if (style.Display == DisplayKind.None) return null;

            if (IsInlineOnly(element))
            {
                string text = CompileInline(element, styles, style);
                return string.IsNullOrEmpty(text) ? null : new LayoutNode(style, text) { Tag = element };
            }

            var node = new LayoutNode(style) { Tag = element };

            foreach (INode child in element.ChildNodes)
            {
                if (child.NodeType == NodeType.Text)
                {
                    // Text sitting directly among element children becomes its own leaf; it inherits the parent's
                    // style, which is exactly what an anonymous box does in CSS. This one IS a whole block, so the
                    // edge whitespace goes.
                    string raw = Normalise(child.TextContent).Trim();
                    if (raw.Length == 0) continue;
                    node.Add(new LayoutNode(style, raw) { Tag = element });
                    continue;
                }

                if (child is IElement childElement)
                {
                    LayoutNode built = Build(childElement, styles);
                    if (built != null) node.Add(built);
                }
            }

            return node;
        }

        /// <summary>
        /// True when this element is a run of text rather than a container.
        ///
        /// It must contain DIRECT text of its own - inline children alone are not enough. Every element is a flex
        /// container in this engine, so a bar holding two spans genuinely has two flex items to lay out; collapsing it
        /// into one string would silently discard justify-content and glue the two labels together.
        /// </summary>
        private static bool IsInlineOnly(IElement element)
        {
            bool sawDirectText = false;

            foreach (INode child in element.ChildNodes)
            {
                switch (child.NodeType)
                {
                    case NodeType.Text:
                        if (Normalise(child.TextContent).Trim().Length > 0) sawDirectText = true;
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

            return sawDirectText;
        }

        private static string CompileInline(IElement element, Dictionary<IElement, ComputedStyle> styles, ComputedStyle inherited)
        {
            var sb = new StringBuilder();
            AppendInline(element, styles, inherited, sb);
            return sb.ToString().Trim();
        }

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
                    sb.Append(Escape(Normalise(child.TextContent)));
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
