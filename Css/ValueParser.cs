using System.Globalization;

namespace Sideload.Css
{
    /// <summary>
    /// Parses single CSS values - lengths, numbers, colours, keywords. Everything here is total: an unparseable value
    /// yields false and the caller leaves the property alone, which is what a browser does with a bad declaration.
    ///
    /// All number parsing uses <see cref="CultureInfo.InvariantCulture"/> on purpose: the mod runtime runs with
    /// invariant globalization, and a locale that reads "0,5" would silently mangle every stylesheet.
    /// </summary>
    internal static class ValueParser
    {
        internal static bool TryNumber(string s, out float value)
        {
            value = 0f;
            if (string.IsNullOrEmpty(s)) return false;
            return float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// A length in any unit the engine understands, resolved to px where it has to be.
        ///
        /// This is the overload the cascade uses. Relative units need a font size and a viewport, and the only
        /// place that knows both is the cascade - see <see cref="LengthContext"/> for why they are not carried
        /// into layout instead.
        /// </summary>
        internal static bool TryLength(string s, in LengthContext ctx, out Len len)
        {
            len = Len.None;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();

            // A math function can contain any of the units below, so it has to be tried before them.
            if (s.IndexOf('(') >= 0 && CalcExpression.TryEvaluate(s, ctx, out len)) return true;

            foreach (var unit in Relative)
            {
                if (!s.EndsWith(unit.Suffix, StringComparison.OrdinalIgnoreCase)) continue;

                // `2vmin` ends in "in" as well. Longest suffix first in the table below keeps that honest, but a
                // number check is what actually decides it.
                if (!TryNumber(s.Substring(0, s.Length - unit.Suffix.Length), out float n)) continue;

                len = Len.Px(n * unit.Basis(ctx));
                return true;
            }

            return TryLength(s, out len);
        }

        /// <summary>
        /// The relative units, longest suffix first so `vmin` is not read as `in` and `rem` is not read as `em`.
        /// </summary>
        private static readonly (string Suffix, Func<LengthContext, float> Basis)[] Relative =
        {
            ("vmin", c => Math.Min(c.ViewportWidth, c.ViewportHeight) * 0.01f),
            ("vmax", c => Math.Max(c.ViewportWidth, c.ViewportHeight) * 0.01f),
            ("rem",  c => c.RootFontSize),
            ("em",   c => c.FontSize),
            // svh/lvh/dvh differ in a browser only because mobile chrome slides in and out. Nothing here does,
            // so all three are vh and a page written for a phone browser lands correctly rather than not at all.
            ("svh",  c => c.ViewportHeight * 0.01f),
            ("lvh",  c => c.ViewportHeight * 0.01f),
            ("dvh",  c => c.ViewportHeight * 0.01f),
            ("svw",  c => c.ViewportWidth * 0.01f),
            ("lvw",  c => c.ViewportWidth * 0.01f),
            ("dvw",  c => c.ViewportWidth * 0.01f),

            ("vh",   c => c.ViewportHeight * 0.01f),
            ("vw",   c => c.ViewportWidth * 0.01f),

            // Physical units, at the CSS reference of 96 dpi. Rare in a mod, but they cost four lines and the
            // alternative is a stylesheet pasted from the web losing every size it names.
            ("pt",   _ => 96f / 72f),
            ("pc",   _ => 16f),
            ("cm",   _ => 96f / 2.54f),
            ("mm",   _ => 9.6f / 2.54f),
            ("in",   _ => 96f),
            ("q",    _ => 2.4f / 2.54f),

            // No glyph metrics at this layer, so `ch` is half an em. That is the usual width of a digit in a
            // proportional face and close enough that a column sized in ch lands in the right place; it is not
            // the advance of a zero, and a terminal that needs the exact number should use -s1-mono-advance.
            ("ch",   c => c.FontSize * 0.5f),
        };

        /// <summary>A length: `12px`, `50%`, `auto`, `none`, or a bare `0`.</summary>
        internal static bool TryLength(string s, out Len len)
        {
            len = Len.None;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();

            if (s.Equals("auto", StringComparison.OrdinalIgnoreCase)) { len = Len.Auto; return true; }
            if (s.Equals("none", StringComparison.OrdinalIgnoreCase)) { len = Len.None; return true; }

            if (s.EndsWith("%", StringComparison.Ordinal))
            {
                if (!TryNumber(s.Substring(0, s.Length - 1), out float p)) return false;
                len = Len.Percent(p);
                return true;
            }

            if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryNumber(s.Substring(0, s.Length - 2), out float v)) return false;
                len = Len.Px(v);
                return true;
            }

            // A unitless number is only a length when it is zero; anything else (line-height: 1.4) is handled by the
            // property that accepts it, not here.
            if (TryNumber(s, out float n) && n == 0f) { len = Len.Zero; return true; }
            return false;
        }

        /// <summary>
        /// A colour, with `currentColor` resolved against the text colour in force.
        ///
        /// The two-argument overload below passes the engine's default text colour, which is right for a caller
        /// with no element in hand and wrong inside the cascade - so the cascade uses this one.
        /// </summary>
        internal static bool TryColor(string s, in RgbaColor currentColor, out RgbaColor color)
        {
            color = RgbaColor.Transparent;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();

            if (s.StartsWith("#", StringComparison.Ordinal)) return TryHex(s.Substring(1), out color);

            // rgb()/rgba() stay here because they are the old path and the cheap one; everything modern -
            // oklch, oklab, lab, lch, hsl, hwb, color-mix, currentColor and the full 148 named colours - lives
            // in ColorParser. It runs BEFORE the 17-entry list below, which it supersedes.
            if (!s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)
                && ColorParser.TryParse(s, currentColor, out color)) return true;

            return TryColorLegacy(s, out color);
        }

        internal static bool TryColor(string s, out RgbaColor color) =>
            TryColor(s, DefaultTextColor, out color);

        /// <summary>The text colour a caller outside the cascade gets for `currentColor`. Matches
        /// <see cref="ComputedStyle"/>'s initial value, so the two cannot disagree.</summary>
        private static readonly RgbaColor DefaultTextColor = new RgbaColor(0.925f, 0.929f, 0.945f, 1f);

        private static bool TryColorLegacy(string s, out RgbaColor color)
        {
            color = RgbaColor.Transparent;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();

            if (s.StartsWith("#", StringComparison.Ordinal)) return TryHex(s.Substring(1), out color);

            if (s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            {
                int open = s.IndexOf('(');
                int close = s.LastIndexOf(')');
                if (open < 0 || close <= open) return false;

                string[] parts = SplitArgs(s.Substring(open + 1, close - open - 1));
                if (parts.Length < 3) return false;

                if (!TryChannel(parts[0], out float r) || !TryChannel(parts[1], out float g) || !TryChannel(parts[2], out float b))
                    return false;

                float a = 1f;
                if (parts.Length >= 4 && !TryAlpha(parts[3], out a)) return false;

                color = new RgbaColor(r, g, b, a);
                return true;
            }

            return TryNamed(s, out color);
        }

        private static bool TryHex(string hex, out RgbaColor color)
        {
            color = RgbaColor.Transparent;
            int Hx(char c) =>
                c >= '0' && c <= '9' ? c - '0' :
                c >= 'a' && c <= 'f' ? c - 'a' + 10 :
                c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;

            foreach (char c in hex) if (Hx(c) < 0) return false;

            switch (hex.Length)
            {
                case 3:
                case 4:
                {
                    float r = Hx(hex[0]) * 17 / 255f, g = Hx(hex[1]) * 17 / 255f, b = Hx(hex[2]) * 17 / 255f;
                    float a = hex.Length == 4 ? Hx(hex[3]) * 17 / 255f : 1f;
                    color = new RgbaColor(r, g, b, a);
                    return true;
                }
                case 6:
                case 8:
                {
                    float r = (Hx(hex[0]) * 16 + Hx(hex[1])) / 255f;
                    float g = (Hx(hex[2]) * 16 + Hx(hex[3])) / 255f;
                    float b = (Hx(hex[4]) * 16 + Hx(hex[5])) / 255f;
                    float a = hex.Length == 8 ? (Hx(hex[6]) * 16 + Hx(hex[7])) / 255f : 1f;
                    color = new RgbaColor(r, g, b, a);
                    return true;
                }
                default:
                    return false;
            }
        }

        private static bool TryChannel(string s, out float v)
        {
            v = 0f;
            s = s.Trim();
            if (s.EndsWith("%", StringComparison.Ordinal))
            {
                if (!TryNumber(s.Substring(0, s.Length - 1), out float p)) return false;
                v = Clamp01(p / 100f);
                return true;
            }
            if (!TryNumber(s, out float n)) return false;
            v = Clamp01(n / 255f);
            return true;
        }

        private static bool TryAlpha(string s, out float v)
        {
            v = 1f;
            s = s.Trim();
            if (s.EndsWith("%", StringComparison.Ordinal))
            {
                if (!TryNumber(s.Substring(0, s.Length - 1), out float p)) return false;
                v = Clamp01(p / 100f);
                return true;
            }
            if (!TryNumber(s, out float n)) return false;
            v = Clamp01(n);
            return true;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        /// <summary>Splits `1, 2, 3` or `1 2 3 / 0.5` into arguments; commas, whitespace and a slash all separate.</summary>
        internal static string[] SplitArgs(string s)
        {
            var parts = new List<string>();
            var current = new System.Text.StringBuilder();
            int depth = 0;

            foreach (char c in s)
            {
                if (c == '(') depth++;
                else if (c == ')') depth--;

                bool separator = depth == 0 && (c == ',' || c == '/' || char.IsWhiteSpace(c));
                if (separator)
                {
                    if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
                    continue;
                }
                current.Append(c);
            }
            if (current.Length > 0) parts.Add(current.ToString());
            return parts.ToArray();
        }

        /// <summary>
        /// Splits a value list on top-level whitespace/commas only, so `linear-gradient(#1, #2) padding-box` and
        /// `0 4px 12px rgba(0,0,0,.5)` keep their functional notation intact.
        /// </summary>
        internal static string[] SplitTopLevel(string s, bool commaSeparated = false)
        {
            var parts = new List<string>();
            var current = new System.Text.StringBuilder();
            int depth = 0;

            foreach (char c in s)
            {
                if (c == '(') depth++;
                else if (c == ')') depth--;

                bool separator = depth == 0 && (commaSeparated ? c == ',' : (char.IsWhiteSpace(c) || c == ','));
                if (separator)
                {
                    if (current.Length > 0) { parts.Add(current.ToString().Trim()); current.Clear(); }
                    continue;
                }
                current.Append(c);
            }
            if (current.Length > 0) parts.Add(current.ToString().Trim());
            return parts.ToArray();
        }

        private static bool TryNamed(string name, out RgbaColor color)
        {
            switch (name.ToLowerInvariant())
            {
                case "transparent": color = RgbaColor.Transparent; return true;
                case "black": color = new RgbaColor(0f, 0f, 0f); return true;
                case "white": color = new RgbaColor(1f, 1f, 1f); return true;
                case "red": color = new RgbaColor(1f, 0f, 0f); return true;
                case "lime": color = new RgbaColor(0f, 1f, 0f); return true;
                case "blue": color = new RgbaColor(0f, 0f, 1f); return true;
                case "yellow": color = new RgbaColor(1f, 1f, 0f); return true;
                case "cyan":
                case "aqua": color = new RgbaColor(0f, 1f, 1f); return true;
                case "magenta":
                case "fuchsia": color = new RgbaColor(1f, 0f, 1f); return true;
                case "gray":
                case "grey": color = new RgbaColor(0.502f, 0.502f, 0.502f); return true;
                case "silver": color = new RgbaColor(0.753f, 0.753f, 0.753f); return true;
                case "maroon": color = new RgbaColor(0.502f, 0f, 0f); return true;
                case "olive": color = new RgbaColor(0.502f, 0.502f, 0f); return true;
                case "green": color = new RgbaColor(0f, 0.502f, 0f); return true;
                case "purple": color = new RgbaColor(0.502f, 0f, 0.502f); return true;
                case "teal": color = new RgbaColor(0f, 0.502f, 0.502f); return true;
                case "navy": color = new RgbaColor(0f, 0f, 0.502f); return true;
                case "orange": color = new RgbaColor(1f, 0.647f, 0f); return true;
                default: color = RgbaColor.Transparent; return false;
            }
        }
    }
}
