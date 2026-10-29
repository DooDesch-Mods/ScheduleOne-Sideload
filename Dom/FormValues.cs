using System.Globalization;
using System.Text;
using AngleSharp.Dom;

namespace Sideload.Dom
{
    /// <summary>
    /// The values behind a range, a colour input and a select, read and written the way HTML defines them.
    ///
    /// Unity-free on purpose, for the same reason <see cref="ControlKinds"/> is: the layout places a thumb from the
    /// value, the script hands `el.value` to a page, and the pointer turns a position back into a value. Three callers,
    /// one answer each.
    ///
    /// The value lives in the `value` attribute, like every other control in this engine. A rebuild has to be able to
    /// read it back, and an attribute is the one place that survives one.
    /// </summary>
    internal static class FormValues
    {
        // ------------------------------------------------------------------------------------------- range --

        /// <summary>A range's bounds, step and current value, already sanitised.</summary>
        internal readonly struct RangeSpec
        {
            internal readonly double Min, Max, Step, Value;

            internal RangeSpec(double min, double max, double step, double value)
            {
                Min = min; Max = max; Step = step; Value = value;
            }

            /// <summary>Where the value sits between the ends, 0..1. Zero when there is no distance to cover.</summary>
            internal double Fraction => Max > Min ? (Value - Min) / (Max - Min) : 0d;
        }

        /// <summary>
        /// Read a range as HTML sanitises it: min 0 and max 100 unless they parse, max never below min, step 1
        /// unless it is a positive number or `any`, and a value that is clamped and snapped onto the step grid.
        /// A missing or broken value is the middle, which is what every browser shows for `&lt;input type=range&gt;`.
        /// </summary>
        internal static RangeSpec Range(IElement element)
        {
            double min = Number(element?.GetAttribute("min"), 0d);
            double max = Number(element?.GetAttribute("max"), 100d);
            if (max < min) max = min;

            string rawStep = element?.GetAttribute("step");
            double step = string.Equals(rawStep?.Trim(), "any", StringComparison.OrdinalIgnoreCase)
                ? 0d
                : Number(rawStep, 1d);
            if (step < 0d || double.IsNaN(step)) step = 1d;

            double fallback = min + (max - min) / 2d;
            double value = Number(element?.GetAttribute("value"), fallback);
            return new RangeSpec(min, max, step, Snap(value, min, max, step));
        }

        /// <summary>Clamp into the range and onto the step grid, which starts at min. A snap that lands past max
        /// steps back down, so the largest reachable value is on the grid too.</summary>
        internal static double Snap(double value, double min, double max, double step)
        {
            if (double.IsNaN(value)) value = min + (max - min) / 2d;
            value = Math.Clamp(value, min, max);
            if (step <= 0d) return value;

            double snapped = min + Math.Round((value - min) / step, MidpointRounding.AwayFromZero) * step;
            if (snapped > max + 1e-9) snapped -= step;
            return Math.Clamp(Math.Round(snapped, 10), min, max);
        }

        /// <summary>The value at a fraction of the track, snapped.</summary>
        internal static double ValueAt(RangeSpec spec, double fraction) =>
            Snap(spec.Min + Math.Clamp(fraction, 0d, 1d) * (spec.Max - spec.Min), spec.Min, spec.Max, spec.Step);

        /// <summary>One value as `el.value` reports it: the shortest decimal, no exponent, a dot as separator.</summary>
        internal static string Format(double value)
        {
            double rounded = Math.Round(value, 10);
            if (rounded == 0d) rounded = 0d;   // no "-0"
            return rounded.ToString("0.##########", CultureInfo.InvariantCulture);
        }

        /// <summary>The range's value, as the page reads it.</summary>
        internal static string RangeValue(IElement element) => Format(Range(element).Value);

        /// <summary>
        /// The fraction of the track a pointer at <paramref name="offsetX"/> stands for.
        ///
        /// The thumb's CENTRE travels, not its edge: at 0 the thumb sits flush with the left end, at 1 with the right,
        /// so the distance the pointer can move is the width minus one thumb. Measuring against the whole width put
        /// the value half a thumb off at both ends, and the last step was out of reach.
        /// </summary>
        internal static double FractionAt(float offsetX, float trackLeft, float trackWidth, float thumbWidth)
        {
            float travel = trackWidth - thumbWidth;
            if (travel <= 0f) return 0d;

            return Math.Clamp((offsetX - trackLeft - thumbWidth / 2f) / travel, 0f, 1f);
        }

        // ------------------------------------------------------------------------------------------- colour --

        /// <summary>
        /// A colour input's value, as HTML sanitises it: `#rrggbb` in lower case, or black when the attribute is
        /// anything else. Named colours and the three-digit form are not valid values for this control in a browser
        /// either - they are what the picker's own field accepts, see <see cref="ParseHex"/>.
        /// </summary>
        internal static string ColorValue(IElement element) => NormaliseColor(element?.GetAttribute("value"));

        internal static string NormaliseColor(string raw)
        {
            string v = raw?.Trim() ?? "";
            if (v.Length == 7 && v[0] == '#' && IsHex(v, 1, 6)) return v.ToLowerInvariant();
            return "#000000";
        }

        /// <summary>What the picker's hex field accepts: `#rgb`, `#rrggbb`, with or without the `#`.</summary>
        internal static bool ParseHex(string raw, out string normalised)
        {
            normalised = null;
            string v = (raw ?? "").Trim();
            if (v.StartsWith("#", StringComparison.Ordinal)) v = v.Substring(1);

            if (v.Length == 3 && IsHex(v, 0, 3))
                v = new string(new[] { v[0], v[0], v[1], v[1], v[2], v[2] });

            if (v.Length != 6 || !IsHex(v, 0, 6)) return false;

            normalised = "#" + v.ToLowerInvariant();
            return true;
        }

        private static bool IsHex(string s, int from, int count)
        {
            for (int i = from; i < from + count; i++)
                if (!Uri.IsHexDigit(s[i])) return false;
            return true;
        }

        /// <summary>`#rrggbb` to 0..1 channels. The caller has normalised it.</summary>
        internal static void HexToRgb(string hex, out double r, out double g, out double b)
        {
            string v = NormaliseColor(hex);
            r = int.Parse(v.Substring(1, 2), NumberStyles.HexNumber) / 255d;
            g = int.Parse(v.Substring(3, 2), NumberStyles.HexNumber) / 255d;
            b = int.Parse(v.Substring(5, 2), NumberStyles.HexNumber) / 255d;
        }

        internal static string RgbToHex(double r, double g, double b) =>
            "#" + Byte(r).ToString("x2") + Byte(g).ToString("x2") + Byte(b).ToString("x2");

        private static int Byte(double channel) => (int)Math.Round(Math.Clamp(channel, 0d, 1d) * 255d);

        /// <summary>Hue in degrees 0..360, saturation and value 0..1 - the three numbers a picker's field and
        /// slider stand for.</summary>
        internal static void RgbToHsv(double r, double g, double b, out double h, out double s, out double v)
        {
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;

            v = max;
            s = max <= 0d ? 0d : d / max;

            if (d <= 0d) { h = 0d; return; }

            if (max == r) h = 60d * (((g - b) / d) % 6d);
            else if (max == g) h = 60d * ((b - r) / d + 2d);
            else h = 60d * ((r - g) / d + 4d);

            if (h < 0d) h += 360d;
        }

        internal static void HsvToRgb(double h, double s, double v, out double r, out double g, out double b)
        {
            h = ((h % 360d) + 360d) % 360d;
            double c = v * s;
            double x = c * (1d - Math.Abs((h / 60d) % 2d - 1d));
            double m = v - c;

            (double r1, double g1, double b1) = (int)(h / 60d) switch
            {
                0 => (c, x, 0d),
                1 => (x, c, 0d),
                2 => (0d, c, x),
                3 => (0d, x, c),
                4 => (x, 0d, c),
                _ => (c, 0d, x),
            };

            r = r1 + m; g = g1 + m; b = b1 + m;
        }

        internal static string HsvToHex(double h, double s, double v)
        {
            HsvToRgb(h, s, v, out double r, out double g, out double b);
            return RgbToHex(r, g, b);
        }

        // ------------------------------------------------------------------------------------------- select --

        /// <summary>
        /// A select's options in the order a list shows them: direct children, and the options inside each
        /// `&lt;optgroup&gt;`. Anything else in a select is ignored, as HTML does.
        /// </summary>
        internal static List<IElement> Options(IElement select)
        {
            var options = new List<IElement>();
            if (select == null) return options;

            foreach (IElement child in select.Children)
            {
                if (Is(child, "option")) options.Add(child);
                else if (Is(child, "optgroup"))
                    foreach (IElement inner in child.Children)
                        if (Is(inner, "option")) options.Add(inner);
            }

            return options;
        }

        /// <summary>
        /// Which option is chosen. The last one carrying `selected` wins, which is what HTML's selectedness rule
        /// comes to; with none, the first option that can be chosen. -1 for a select with nothing to choose.
        /// </summary>
        internal static int SelectedIndex(IElement select)
        {
            List<IElement> options = Options(select);

            for (int i = options.Count - 1; i >= 0; i--)
                if (options[i].HasAttribute("selected")) return i;

            for (int i = 0; i < options.Count; i++)
                if (!IsDisabled(options[i])) return i;

            return -1;
        }

        /// <summary>Choose one option and drop the mark from every other. False when the index names none.</summary>
        internal static bool Select(IElement select, int index)
        {
            List<IElement> options = Options(select);
            if (index < 0 || index >= options.Count) return false;

            for (int i = 0; i < options.Count; i++)
            {
                if (i == index) { if (!options[i].HasAttribute("selected")) options[i].SetAttribute("selected", ""); }
                else if (options[i].HasAttribute("selected")) options[i].RemoveAttribute("selected");
            }

            return true;
        }

        /// <summary>`select.value`: the chosen option's value, or "" when there is none.</summary>
        internal static string SelectValue(IElement select)
        {
            int index = SelectedIndex(select);
            return index < 0 ? "" : OptionValue(Options(select)[index]);
        }

        /// <summary>`select.value = v`: choose the first option with that value. False when none has it, and the
        /// selection is left alone - a controlled component that names a value the list does not have is a mistake
        /// in the page, and clearing the box would hide it.</summary>
        internal static bool SetSelectValue(IElement select, string value)
        {
            List<IElement> options = Options(select);
            for (int i = 0; i < options.Count; i++)
                if (string.Equals(OptionValue(options[i]), value ?? "", StringComparison.Ordinal))
                    return Select(select, i);

            return false;
        }

        /// <summary>An option's value: its `value` attribute, or its text when it has none.</summary>
        internal static string OptionValue(IElement option) =>
            option?.GetAttribute("value") ?? OptionText(option);

        /// <summary>An option's text with whitespace collapsed, which is what `option.text` returns.</summary>
        internal static string OptionText(IElement option) => Collapse(option?.TextContent);

        /// <summary>What the list shows for an option: its `label`, or its text.</summary>
        internal static string OptionLabel(IElement option)
        {
            string label = option?.GetAttribute("label");
            return string.IsNullOrEmpty(label) ? OptionText(option) : label;
        }

        /// <summary>An option that cannot be chosen: disabled itself, or inside a disabled group.</summary>
        internal static bool IsDisabled(IElement option)
        {
            if (option == null) return true;
            if (option.HasAttribute("disabled")) return true;

            IElement parent = option.ParentElement;
            return parent != null && Is(parent, "optgroup") && parent.HasAttribute("disabled");
        }

        /// <summary>The next option that can be chosen from <paramref name="from"/> in direction
        /// <paramref name="step"/>, or <paramref name="from"/> itself when there is none - an arrow key at the end of
        /// the list stays where it is.</summary>
        internal static int NextEnabled(IElement select, int from, int step)
        {
            List<IElement> options = Options(select);
            for (int i = from + step; i >= 0 && i < options.Count; i += step)
                if (!IsDisabled(options[i])) return i;

            return from;
        }

        private static bool Is(IElement element, string tag) =>
            string.Equals(element?.LocalName, tag, StringComparison.OrdinalIgnoreCase);

        private static string Collapse(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var sb = new StringBuilder(text.Length);
            bool space = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static double Number(string raw, double fallback) =>
            double.TryParse(raw?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && !double.IsNaN(value) && !double.IsInfinity(value)
                ? value
                : fallback;
    }
}
