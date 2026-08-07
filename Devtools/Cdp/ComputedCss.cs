using System.Globalization;
using Sideload.Css;

namespace Sideload.Devtools.Cdp
{
    /// <summary>
    /// The resolved style of an element, in the spelling a developer wrote it in.
    ///
    /// <see cref="ComputedStyle"/> is C# - `BackgroundColor`, an enum, a float - and the Computed pane is read to
    /// compare against a stylesheet, so every value is turned back into CSS here: `background-color`, `rgb(20, 22, 28)`,
    /// `14px`. The mapping is written out property by property rather than reflected over the fields, because the
    /// unit is not derivable from the type: a float is `px` for a font size, unitless for a weight or an opacity, and
    /// degrees for a gradient angle.
    ///
    /// A property the engine gains later is simply missing here until it is added, which is the honest failure mode -
    /// a reflected guess would show it with the wrong unit.
    /// </summary>
    internal static class ComputedCss
    {
        internal static List<KeyValuePair<string, string>> Describe(ComputedStyle style)
        {
            var css = new List<KeyValuePair<string, string>>();
            if (style == null) return css;

            void Put(string name, string value) => css.Add(new KeyValuePair<string, string>(name, value));

            // --- layout ---
            Put("display", style.Display == DisplayKind.None ? "none" : "flex");
            Put("flex-direction", style.FlexDirection switch
            {
                FlexDirection.Row => "row",
                FlexDirection.RowReverse => "row-reverse",
                FlexDirection.ColumnReverse => "column-reverse",
                _ => "column",
            });
            Put("flex-wrap", style.FlexWrap switch
            {
                FlexWrap.Wrap => "wrap",
                FlexWrap.WrapReverse => "wrap-reverse",
                _ => "nowrap",
            });
            Put("flex-grow", Number(style.FlexGrow));
            Put("flex-shrink", Number(style.FlexShrink));
            Put("flex-basis", style.FlexBasis.ToString());
            Put("justify-content", style.JustifyContent switch
            {
                Justify.FlexEnd => "flex-end",
                Justify.Center => "center",
                Justify.SpaceBetween => "space-between",
                Justify.SpaceAround => "space-around",
                Justify.SpaceEvenly => "space-evenly",
                _ => "flex-start",
            });
            Put("align-items", Align(style.AlignItems));
            Put("align-self", Align(style.AlignSelf));
            Put("row-gap", style.RowGap.ToString());
            Put("column-gap", style.ColumnGap.ToString());

            Edges(css, "padding", style.Padding);
            Edges(css, "margin", style.Margin);

            Put("width", style.Width.ToString());
            Put("height", style.Height.ToString());
            Put("min-width", style.MinWidth.ToString());
            Put("min-height", style.MinHeight.ToString());
            Put("max-width", style.MaxWidth.ToString());
            Put("max-height", style.MaxHeight.ToString());

            Put("position", style.Position == PositionKind.Absolute ? "absolute"
                          : style.Position == PositionKind.Fixed ? "fixed"
                          : "static");
            Put("top", style.Inset.Top.ToString());
            Put("right", style.Inset.Right.ToString());
            Put("bottom", style.Inset.Bottom.ToString());
            Put("left", style.Inset.Left.ToString());

            Put("overflow-x", Overflow(style.OverflowX));
            Put("overflow-y", Overflow(style.OverflowY));

            // --- paint ---
            Put("background-color", Color(style.BackgroundColor));
            Put("background-image", style.HasGradient
                ? string.Format(CultureInfo.InvariantCulture, "linear-gradient({0}deg, {1}, {2})",
                    Number(style.GradientAngleDeg), Color(style.GradientFrom), Color(style.GradientTo))
                : "none");

            Put("border-top-width", style.BorderWidth.Top.ToString());
            Put("border-right-width", style.BorderWidth.Right.ToString());
            Put("border-bottom-width", style.BorderWidth.Bottom.ToString());
            Put("border-left-width", style.BorderWidth.Left.ToString());
            Put("border-color", Color(style.BorderColor));
            Put("border-radius", Radius(style.BorderRadius));

            Put("box-shadow", style.HasShadow
                ? string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3}",
                    Pixels(style.ShadowOffsetX), Pixels(style.ShadowOffsetY), Pixels(style.ShadowBlur),
                    Color(style.ShadowColor))
                : "none");

            Put("opacity", Number(style.Opacity));

            // --- text ---
            Put("font-family", style.FontFamily ?? "");
            Put("font-size", Pixels(style.FontSize));
            Put("font-weight", style.FontWeight.ToString(CultureInfo.InvariantCulture));
            Put("font-style", style.FontStyle == FontStyleKind.Italic ? "italic" : "normal");

            // Resolved rather than as written: the engine's own default is 1.2 x font-size, and a Computed pane that
            // said "none" would hide the number the developer is looking for.
            Put("line-height", Pixels(style.ResolvedLineHeight));

            Put("letter-spacing", Pixels(style.LetterSpacing));
            Put("text-align", style.TextAlign switch
            {
                TextAlignKind.Center => "center",
                TextAlignKind.Right => "right",
                _ => "left",
            });
            Put("white-space", style.WhiteSpace switch
            {
                WhiteSpaceKind.NoWrap => "nowrap",
                WhiteSpaceKind.Pre => "pre",
                WhiteSpaceKind.PreWrap => "pre-wrap",
                _ => "normal",
            });
            Put("color", Color(style.Color));
            Put("text-overflow", style.TextOverflowEllipsis ? "ellipsis" : "clip");

            // Custom properties in effect here, inherited ones included - the same set var() resolves against.
            if (style.Variables != null)
                foreach (KeyValuePair<string, string> variable in style.Variables)
                    Put(variable.Key, variable.Value ?? "");

            return css;
        }

        private static void Edges(List<KeyValuePair<string, string>> css, string prefix, Edges edges)
        {
            css.Add(new KeyValuePair<string, string>(prefix + "-top", edges.Top.ToString()));
            css.Add(new KeyValuePair<string, string>(prefix + "-right", edges.Right.ToString()));
            css.Add(new KeyValuePair<string, string>(prefix + "-bottom", edges.Bottom.ToString()));
            css.Add(new KeyValuePair<string, string>(prefix + "-left", edges.Left.ToString()));
        }

        private static string Align(AlignKind align) => align switch
        {
            AlignKind.FlexStart => "flex-start",
            AlignKind.FlexEnd => "flex-end",
            AlignKind.Center => "center",
            AlignKind.Baseline => "baseline",
            AlignKind.Auto => "auto",
            _ => "stretch",
        };

        private static string Overflow(OverflowKind overflow) => overflow switch
        {
            OverflowKind.Hidden => "hidden",
            OverflowKind.Scroll => "scroll",
            OverflowKind.Auto => "auto",
            _ => "visible",
        };

        private static string Radius(Corners corners)
        {
            if (corners.IsZero) return "0px";

            bool uniform = corners.TopLeft == corners.TopRight
                           && corners.TopRight == corners.BottomRight
                           && corners.BottomRight == corners.BottomLeft;

            return uniform
                ? Pixels(corners.TopLeft)
                : $"{Pixels(corners.TopLeft)} {Pixels(corners.TopRight)} {Pixels(corners.BottomRight)} {Pixels(corners.BottomLeft)}";
        }

        /// <summary>CSS colour spelling: channels 0-255, and the alpha only when there is one.</summary>
        internal static string Color(RgbaColor color)
        {
            int r = Channel(color.R), g = Channel(color.G), b = Channel(color.B);

            return color.A >= 0.999f
                ? string.Format(CultureInfo.InvariantCulture, "rgb({0}, {1}, {2})", r, g, b)
                : string.Format(CultureInfo.InvariantCulture, "rgba({0}, {1}, {2}, {3})", r, g, b, Number(color.A));
        }

        private static int Channel(float value) => (int)Math.Round(Math.Clamp(value, 0f, 1f) * 255f);

        private static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Pixels(float value) => Number(value) + "px";
    }
}
