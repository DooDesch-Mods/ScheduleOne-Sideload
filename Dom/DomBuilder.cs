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

            if (IntrinsicControlSize(element, style, generated, out LayoutNode control)) return control;

            if (IsInlineOnly(element, style, styles, out bool inlineRun))
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

            // An inline run that could not fold because one of its pieces is a box: the pieces become real boxes
            // and the element becomes the line they sit on. That is an anonymous inline formatting context, which
            // this engine has no layout mode for - a wrapping row with the items on their baseline is as close as
            // flexbox gets, and it is close enough that a badge in a sentence sits where a badge in a sentence
            // sits. The difference is that it wraps by ITEM rather than by word.
            var node = new LayoutNode(inlineRun ? InlineContext(style) : style) { Tag = element };
            if (before != null) node.Add(before);

            foreach (INode child in element.ChildNodes)
            {
                if (child.NodeType == NodeType.Text)
                {
                    // Text sitting directly among element children becomes its own leaf; it inherits the parent's
                    // style, which is exactly what an anonymous box does in CSS. This one IS a whole block, so the
                    // edge whitespace goes - unless the style preserves it, where the edges are content.
                    //
                    // On a LINE the edges are not the fragment's, they are the line's. Trimming each piece there
                    // deletes exactly the space between a word and the tag after it, and the sentence comes out as
                    // "a paragraph with**bold**,*italic*and a badge" - the same mistake CompileInline was fixed for
                    // once already, one level up. Only the two ends of the line are trimmed, and that happens below.
                    string raw = Preserves(style) ? child.TextContent
                               : inlineRun ? Normalise(child.TextContent)
                               : Normalise(child.TextContent).Trim();

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

            if (inlineRun) TrimLine(node);

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
        /// A form control's own size, which nothing else can supply.
        ///
        /// An <c>&lt;input&gt;</c> has no children, so its box came out as padding around nothing: eight pixels of
        /// `py-2` above and below one pixel of content, with the placeholder drawn outside the bottom edge. Every
        /// text field on every page looked like that, and no stylesheet could fix it - there was no content to give
        /// a height to.
        ///
        /// A browser gives a control an intrinsic size instead of measuring children it does not have: one line of
        /// its own font tall, and as wide as its `size` attribute in characters (twenty by default, which is where
        /// the familiar width of an unstyled text field comes from). This measures exactly that, with a string of
        /// zeroes standing in for the characters - a digit is the widest ordinary glyph in a proportional face, so
        /// it is the same conservative choice a browser's `ch` unit makes.
        ///
        /// The text is never drawn. The painter reaches <see cref="Paint.Painter"/>'s form-control branch before it
        /// looks at whether the node is a text leaf, builds a real TMP_InputField, and never walks the node again.
        /// It exists to be measured and for nothing else, which is why it may be a row of zeroes rather than the
        /// value: the width of a text field must not change while somebody types in it.
        /// </summary>
        private static bool IntrinsicControlSize(IElement element, ComputedStyle style, PseudoStyles generated,
                                                 out LayoutNode node)
        {
            node = null;

            ControlKind kind = ControlKinds.Of(element);
            if (kind == ControlKind.None) return false;

            // `type="hidden"` is data the page carries between renders. A browser gives it no box at all, and this
            // engine used to give it an empty text field - visible, focusable and in the way.
            if (kind == ControlKind.Hidden) return true;

            if (kind == ControlKind.Toggle)
            {
                node = new LayoutNode(Squared(style));
                node.Tag = element;
                return true;
            }

            // A button's label is its `value`, and it is a real run of text: it decides the width, it wraps, and
            // the painter draws it like any other. `<input type="button">` with no value is a blank button, which
            // is also what a browser shows.
            if (kind == ControlKind.Button)
            {
                node = new LayoutNode(style, Escape(element.GetAttribute("value") ?? "")) { Tag = element };
                return true;
            }

            bool textarea = string.Equals(element.LocalName, "textarea", StringComparison.OrdinalIgnoreCase);

            // Anything written between the tags of a textarea is its value, not its layout - the control draws it.
            int columns = Number(element, textarea ? "cols" : "size", 20);
            int rows = textarea ? Number(element, "rows", 2) : 1;

            ComputedStyle measured = style.Clone();

            // A single-line control never wraps, whatever the page said about text. Without this a narrow field
            // would fold its own twenty placeholder characters onto a second line and come out twice as tall.
            measured.WhiteSpace = rows == 1 ? WhiteSpaceKind.NoWrap : style.WhiteSpace;

            var text = new StringBuilder(columns * rows + rows);
            for (int line = 0; line < rows; line++)
            {
                if (line > 0) text.Append('\n');
                text.Append('0', columns);
            }

            node = new LayoutNode(measured, text.ToString())
            {
                Tag = element,
                PlaceholderStyle = generated?.Get(element, PseudoElement.Placeholder),
            };
            return true;
        }

        /// <summary>
        /// A checkbox's own size: a small square, sized off the font so it grows with the text beside it.
        ///
        /// Browsers use 13 CSS pixels flat, from a time when nobody scaled anything. Tying it to the font instead
        /// keeps a checkbox next to 12px help text smaller than one next to a heading, which is what an author who
        /// never styled it expects to see. A declared width or height still wins - `w-4 h-4` is the common way to
        /// say it, and it has to mean what it says.
        /// </summary>
        private static ComputedStyle Squared(ComputedStyle style)
        {
            ComputedStyle box = style.Clone();

            float side = MathF.Round(MathF.Max(11f, style.FontSize * 0.85f));
            if (!box.Width.IsDefinite) box.Width = Len.Px(side);
            if (!box.Height.IsDefinite) box.Height = Len.Px(side);

            // Never squeezed by a row it does not fit into: a checkbox that has given up half its width reads as a
            // rendering fault rather than as a tight layout.
            box.FlexShrink = 0f;
            return box;
        }

        private static int Number(IElement element, string attribute, int fallback)
        {
            string raw = element.GetAttribute(attribute);
            return int.TryParse(raw, out int value) && value > 0 ? value : fallback;
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
        /// <summary><paramref name="inlineRun"/> says every child is inline but one of them is a box, so the run
        /// has to be laid out rather than folded.</summary>
        private static bool IsInlineOnly(IElement element, ComputedStyle style,
                                         Dictionary<IElement, ComputedStyle> styles, out bool inlineRun)
        {
            inlineRun = false;

            bool preserves = Preserves(style);
            bool sawDirectText = false;
            bool sawBox = false;

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
                        var inline = (IElement)child;
                        if (!InlineTags.Contains(inline.LocalName)) return false;

                        // An inline child that is a BOX cannot survive the fold. The fold turns the whole subtree
                        // into one TextMeshPro string, which carries weight, slant and colour and nothing else -
                        // so a `<span class="badge">` inside a sentence lost its background, its padding, its
                        // radius and its hit target, and the page had no way to notice.
                        //
                        // Unconditional, and that is a deliberate behaviour change. Refusing the fold means
                        // laying the run out as boxes on a line, which wraps by item rather than by word - a
                        // worse line break in exchange for a badge that is a badge. The alternative is leaving a
                        // rendering in place that is already wrong, to protect the way it is wrong.
                        if (styles != null && styles.TryGetValue(inline, out ComputedStyle boxed) && IsBox(boxed))
                            sawBox = true;
                        continue;

                    case NodeType.Comment:
                        continue;

                    default:
                        return false;
                }
            }

            if (!sawDirectText && !preserves) return false;

            inlineRun = sawBox;
            return !sawBox;
        }

        /// <summary>
        /// Take the whitespace off the two ENDS of a line, and nowhere else.
        ///
        /// The markup is indented, so the first fragment of an inline run usually begins with the newline and the
        /// spaces the author wrote before the tag - which would open the sentence with a gap - and the last one
        /// ends the same way. Everything between them is the space between two words and stays.
        /// </summary>
        private static readonly char[] Blanks = { ' ', ' ', '\n', '\t' };

        private static void TrimLine(LayoutNode line)
        {
            for (int i = 0; i < line.Children.Count; i++)
            {
                if (!line.Children[i].IsTextLeaf) break;
                line.Children[i].Text = line.Children[i].Text.TrimStart(Blanks);
                if (line.Children[i].Text.Length > 0) break;
            }

            for (int i = line.Children.Count - 1; i >= 0; i--)
            {
                if (!line.Children[i].IsTextLeaf) break;
                line.Children[i].Text = line.Children[i].Text.TrimEnd(Blanks);
                if (line.Children[i].Text.Length > 0) break;
            }

            line.Children.RemoveAll(child => child.IsTextLeaf && child.Text.Length == 0);
        }

        /// <summary>
        /// The style of a line that holds boxes: a wrapping row with its items on the baseline.
        ///
        /// Only the three properties that make it a line, and only where the page said nothing about them - an
        /// author who wrote `flex-direction: column` on a paragraph meant it.
        /// </summary>
        private static ComputedStyle InlineContext(ComputedStyle style)
        {
            ComputedStyle line = style.Clone();

            line.FlexDirection = FlexDirection.Row;
            line.FlexWrap = FlexWrap.Wrap;
            if (line.AlignItems == AlignKind.Stretch) line.AlignItems = AlignKind.Baseline;

            return line;
        }

        /// <summary>
        /// Whether this style paints or measures anything of its own - anything the fold would throw away.
        ///
        /// Deliberately not "did the page write any declaration at all": a colour, a weight or a slant survives
        /// the fold perfectly well, and refusing to fold for those would break every ordinary paragraph with a
        /// bold word in it. What cannot survive is a BOX - something with a surface, an inset or a size.
        /// </summary>
        private static bool IsBox(ComputedStyle s)
        {
            if (s == null) return false;

            return !s.BackgroundColor.IsTransparent
                   || s.HasGradient
                   || s.HasShadow
                   || s.HasTransform
                   || s.Width.IsDefinite || s.Height.IsDefinite
                   || Any(s.Padding) || Any(s.Margin) || Any(s.BorderWidth)
                   || s.BorderRadius.TopLeft > 0f || s.BorderRadius.TopRight > 0f
                   || s.BorderRadius.BottomRight > 0f || s.BorderRadius.BottomLeft > 0f;

            static bool Any(Edges e) =>
                e.Top.Resolve(0f) > 0f || e.Right.Resolve(0f) > 0f
                || e.Bottom.Resolve(0f) > 0f || e.Left.Resolve(0f) > 0f;
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
