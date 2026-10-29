using AngleSharp.Dom;
using Sideload.Css;
using Sideload.Layout;

namespace Sideload.Dom
{
    /// <summary>
    /// The boxes a range, a colour input and a select are drawn from.
    ///
    /// None of them has DOM children to lay out - a range is one element, and a select's options are a list nobody
    /// sees until it opens - so the parts are made here, as layout nodes with no Tag. The painter draws them like any
    /// other box, which is what gives a page `border-radius`, `box-shadow` and the rest on a thumb for nothing.
    ///
    /// A page styles them the way it would in a browser: `accent-color` for the filled part and the thumb, and
    /// `::-webkit-slider-thumb` / `::-webkit-slider-runnable-track` (or the `-moz-` spellings) for the parts
    /// themselves. A part the sheet says nothing about gets the engine's own look.
    /// </summary>
    internal static class FormParts
    {
        /// <summary>The thumb a page did not size. 14px is what Chrome draws on a 16px line.</summary>
        internal const float DefaultThumb = 14f;

        internal const float DefaultTrack = 4f;

        // ------------------------------------------------------------------------------------------- range --

        internal static LayoutNode Range(IElement element, ComputedStyle style, PseudoStyles generated)
        {
            ComputedStyle box = style.Clone();
            if (!box.Width.IsDefinite) box.Width = Len.Px(130f);
            if (!box.Height.IsDefinite) box.Height = Len.Px(18f);

            var node = new LayoutNode(box) { Tag = element };

            bool native = style.Appearance != AppearanceKind.None;
            ComputedStyle trackStyle = generated?.Get(element, PseudoElement.SliderTrack);
            ComputedStyle thumbStyle = generated?.Get(element, PseudoElement.SliderThumb);

            ComputedStyle track = Part(trackStyle ?? style, trackStyle != null);
            if (trackStyle == null)
            {
                track.Height = Len.Px(DefaultTrack);
                track.BorderRadius = Corners.All(DefaultTrack / 2f);
                track.BackgroundColor = native ? Faded(style.Color, 0.28f) : RgbaColor.Transparent;
            }
            else if (!track.Height.IsDefinite) track.Height = Len.Px(DefaultTrack);
            track.Width = Len.Auto;

            // The filled part is a browser's own drawing and there is no standard pseudo-element for it; a page that
            // said `appearance: none` asked for none of the native drawing, and gets none.
            ComputedStyle fill = Part(style, false);
            fill.Height = track.Height;
            fill.BorderRadius = track.BorderRadius;
            fill.BackgroundColor = native ? style.Accent : RgbaColor.Transparent;

            ComputedStyle thumb = Part(thumbStyle ?? style, thumbStyle != null);
            if (!thumb.Width.IsDefinite) thumb.Width = Len.Px(DefaultThumb);
            if (!thumb.Height.IsDefinite) thumb.Height = Len.Px(DefaultThumb);
            if (thumbStyle == null)
            {
                thumb.BorderRadius = Corners.All(DefaultThumb / 2f);
                thumb.BackgroundColor = style.Accent;
            }

            node.Add(new LayoutNode(track) { Part = FormPart.RangeTrack });
            node.Add(new LayoutNode(fill) { Part = FormPart.RangeFill });
            node.Add(new LayoutNode(thumb) { Part = FormPart.RangeThumb });

            double fraction = FormValues.Range(element).Fraction;
            node.Arrange = n => ArrangeRange(n, fraction);
            return node;
        }

        /// <summary>
        /// Place the track across the middle, the thumb at its fraction of the travel, and the fill from the start of
        /// the track to the thumb's centre - so the fill always ends under the thumb, at both ends too.
        /// </summary>
        internal static void ArrangeRange(LayoutNode range, double fraction)
        {
            ContentBox(range, out float left, out float top, out float width, out float height);

            LayoutNode track = Find(range, FormPart.RangeTrack);
            LayoutNode fill = Find(range, FormPart.RangeFill);
            LayoutNode thumb = Find(range, FormPart.RangeThumb);
            if (track == null || thumb == null) return;

            track.X = left;
            track.Width = width;
            track.Y = top + (height - track.Height) / 2f;

            thumb.X = left + (float)fraction * Math.Max(width - thumb.Width, 0f);
            thumb.Y = top + (height - thumb.Height) / 2f;

            if (fill == null) return;
            fill.X = left;
            fill.Y = track.Y;
            fill.Height = track.Height;
            fill.Width = Math.Max(thumb.X + thumb.Width / 2f - left, 0f);
        }

        /// <summary>
        /// The fraction a pointer at <paramref name="offsetX"/> (css px from the range's left border edge) stands for,
        /// measured against the laid-out range - its content box and its own thumb.
        /// </summary>
        internal static double RangeFractionAt(LayoutNode range, float offsetX)
        {
            if (range == null) return 0d;

            ContentBox(range, out float left, out _, out float width, out _);
            LayoutNode thumb = Find(range, FormPart.RangeThumb);
            return FormValues.FractionAt(offsetX, left, width, thumb?.Width ?? DefaultThumb);
        }

        // ------------------------------------------------------------------------------------------- colour --

        internal static LayoutNode Color(IElement element, ComputedStyle style)
        {
            ComputedStyle box = style.Clone();
            if (!box.Width.IsDefinite) box.Width = Len.Px(44f);
            if (!box.Height.IsDefinite) box.Height = Len.Px(24f);
            box.FlexShrink = 0f;

            var node = new LayoutNode(box) { Tag = element };

            ComputedStyle swatch = Part(style, false);
            swatch.Inset = Edges.All(Len.Zero);
            swatch.BorderRadius = Corners.All(2f);
            swatch.BackgroundColor = ToRgba(FormValues.ColorValue(element));
            node.Add(new LayoutNode(swatch) { Part = FormPart.ColorSwatch });
            return node;
        }

        internal static RgbaColor ToRgba(string hex)
        {
            FormValues.HexToRgb(hex, out double r, out double g, out double b);
            return new RgbaColor((float)r, (float)g, (float)b, 1f);
        }

        // ------------------------------------------------------------------------------------------- select --

        /// <summary>
        /// A closed select: the chosen option's label and an arrow, in a row. The width follows the label unless the
        /// page sets one - a browser sizes the box to the widest option, which this engine cannot measure before the
        /// layout runs.
        /// </summary>
        internal static LayoutNode Select(IElement element, ComputedStyle style)
        {
            ComputedStyle box = style.Clone();
            box.FlexDirection = FlexDirection.Row;
            box.AlignItems = AlignKind.Center;
            box.FlexWrap = FlexWrap.NoWrap;

            var node = new LayoutNode(box) { Tag = element };

            int index = FormValues.SelectedIndex(element);
            string label = index < 0 ? "" : FormValues.OptionLabel(FormValues.Options(element)[index]);

            ComputedStyle text = DomBuilder.Contents(style);
            text.Opacity = 1f;
            text.FlexGrow = 1f;
            text.FlexShrink = 1f;
            text.MinWidth = Len.Zero;
            text.WhiteSpace = WhiteSpaceKind.NoWrap;
            text.TextOverflowEllipsis = true;
            text.OverflowX = OverflowKind.Hidden;

            // A non-breaking space keeps an empty select one line tall instead of collapsing to its padding.
            string shown = string.IsNullOrEmpty(label) ? "\u00A0" : DomBuilder.Escape(label);
            node.Add(new LayoutNode(text, shown) { Part = FormPart.SelectLabel });

            // A chevron from two borders of a small square turned by 45 degrees: no glyph needed, and it takes the
            // text colour like the label beside it.
            ComputedStyle arrow = Part(style, false);
            arrow.Position = PositionKind.Static;
            arrow.Width = Len.Px(6f);
            arrow.Height = Len.Px(6f);
            arrow.FlexShrink = 0f;
            arrow.Margin = new Edges { Top = Len.Zero, Right = Len.Px(2f), Bottom = Len.Px(3f), Left = Len.Px(8f) };
            arrow.BorderWidth = new Edges { Top = Len.Zero, Right = Len.Px(1.5f), Bottom = Len.Px(1.5f), Left = Len.Zero };
            arrow.BorderColor = Faded(style.Color, 0.8f);
            arrow.RotateDeg = 45f;
            node.Add(new LayoutNode(arrow) { Part = FormPart.SelectArrow });

            return node;
        }

        // ------------------------------------------------------------------------------------------- helpers --

        /// <summary>
        /// A part's style. From the page's own pseudo-element rule when there is one - everything it said is kept,
        /// only the placement is taken over - and from a clean box otherwise, so nothing the element itself declares
        /// (its padding, its border, its background) is drawn a second time on the part.
        /// </summary>
        private static ComputedStyle Part(ComputedStyle source, bool fromSheet)
        {
            ComputedStyle part = fromSheet ? source.Clone() : DomBuilder.Contents(source);
            part.Position = PositionKind.Absolute;
            part.Inset = new Edges { Top = Len.Zero, Right = Len.Auto, Bottom = Len.Auto, Left = Len.Zero };
            part.Margin = Edges.Zero;
            part.Opacity = fromSheet ? part.Opacity : 1f;
            part.PointerEventsNone = true;
            if (!fromSheet) { part.Width = Len.Auto; part.Height = Len.Auto; }
            return part;
        }

        private static RgbaColor Faded(RgbaColor c, float alpha) => new RgbaColor(c.R, c.G, c.B, c.A * alpha);

        private static LayoutNode Find(LayoutNode parent, FormPart part)
        {
            foreach (LayoutNode child in parent.Children)
                if (child.Part == part) return child;
            return null;
        }

        /// <summary>The content box, in the node's own coordinates - inside the border and the padding.</summary>
        internal static void ContentBox(LayoutNode node, out float left, out float top, out float width, out float height)
        {
            ComputedStyle s = node.Style;
            float w = node.Width;

            left = s.BorderWidth.Left.Resolve(w) + s.Padding.Left.Resolve(w);
            top = s.BorderWidth.Top.Resolve(w) + s.Padding.Top.Resolve(w);
            float right = s.BorderWidth.Right.Resolve(w) + s.Padding.Right.Resolve(w);
            float bottom = s.BorderWidth.Bottom.Resolve(w) + s.Padding.Bottom.Resolve(w);

            width = Math.Max(node.Width - left - right, 0f);
            height = Math.Max(node.Height - top - bottom, 0f);
        }
    }
}
