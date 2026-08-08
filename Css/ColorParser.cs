using System.Globalization;

namespace Sideload.Css
{
    /// <summary>
    /// The colour notations <see cref="ValueParser.TryColor"/> does not already handle: the perceptual spaces
    /// (`oklch`, `oklab`, `lab`, `lch`), `hsl`/`hwb`, `color-mix`, `currentColor`, and the full CSS named list.
    ///
    /// This is not a nice-to-have. A Tailwind v4 build writes its ENTIRE default palette as `oklch()` and expresses
    /// every opacity modifier - `bg-slate-900/60` - as `color-mix(in oklab, ..., transparent)`. An engine that reads
    /// only hex and `rgb()` drops every colour such a stylesheet contains, so the page renders unstyled while every
    /// rule in it looks correct to its author.
    ///
    /// The conversions follow the sample code in CSS Color Module Level 4, section "Sample code for color
    /// conversions" (drafts.csswg.org/css-color-4/#color-conversion-code), whose OKLab matrices are Bjorn Ottosson's
    /// (bottosson.github.io/posts/oklab/). Both directions are here because `color-mix` has to interpolate in a space
    /// the operands are not written in. The matrices are spelled out rather than derived: a matrix that is subtly
    /// wrong produces colours that look plausible and are not, which is harder to notice than parsing nothing.
    ///
    /// Two white points are in play and they are NOT interchangeable. `oklab`/`oklch` are defined on D65, the same
    /// white point as sRGB, so that path needs no chromatic adaptation. `lab`/`lch` are CIE Lab on D50 and go through
    /// XYZ with a Bradford adaptation to D65 before they reach sRGB. Sharing one matrix between the two would shift
    /// every lab() colour by a visible amount.
    ///
    /// Like the rest of Css/, this file must stay free of UnityEngine - <see cref="Math"/>, never Mathf - and all
    /// number parsing goes through <see cref="ValueParser.TryNumber"/> so it stays on
    /// <see cref="CultureInfo.InvariantCulture"/>. A locale that reads "0,5" would mangle every stylesheet.
    /// </summary>
    internal static class ColorParser
    {
        /// <summary>
        /// Everything <see cref="ValueParser.TryColor"/> cannot already do. False means "not one of these forms", so
        /// the caller falls through to its own hex/rgb/named paths rather than treating the declaration as broken.
        /// </summary>
        internal static bool TryParse(string value, in RgbaColor currentColor, out RgbaColor color)
        {
            color = RgbaColor.Transparent;
            if (string.IsNullOrEmpty(value)) return false;
            string s = value.Trim();

            // `currentColor` is the one keyword whose answer is context, not a table.
            if (s.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) { color = currentColor; return true; }
            if (s.Equals("transparent", StringComparison.OrdinalIgnoreCase)) { color = RgbaColor.Transparent; return true; }

            int open = s.IndexOf('(');
            if (open < 0) return TryNamed(s, out color);

            int close = s.LastIndexOf(')');
            if (close <= open) return false;

            string fn = s.Substring(0, open).Trim().ToLowerInvariant();
            string args = s.Substring(open + 1, close - open - 1);

            switch (fn)
            {
                // Scales come from the CSS Color 4 grammar for each function: a percentage is always relative to the
                // reference range of that component, which differs per space. 100% chroma is 0.4 in oklch and 150 in
                // lch, so one shared "percent means 1.0" rule would be wrong in five of these six places.
                case "oklab": return TryRectangular(args, 1f, 0.4f, false, out color);
                case "lab": return TryRectangular(args, 100f, 125f, true, out color);
                case "oklch": return TryPolar(args, 1f, 0.4f, false, out color);
                case "lch": return TryPolar(args, 100f, 150f, true, out color);
                case "hsl":
                case "hsla": return TryHsl(args, out color);
                case "hwb": return TryHwb(args, out color);
                case "color-mix": return TryColorMix(args, currentColor, out color);
                default: return false;
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Function forms
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>`oklab(L a b)` / `lab(L a b)`, with an optional `/ alpha`.</summary>
        private static bool TryRectangular(string args, float lightnessScale, float axisScale, bool cieD50,
                                           out RgbaColor color)
        {
            color = RgbaColor.Transparent;

            // SplitArgs treats comma, whitespace and slash alike. That loses the difference between a separator and
            // the alpha slash, which does not matter here: the fourth component of these functions is always alpha.
            string[] parts = ValueParser.SplitArgs(args);
            if (parts.Length < 3) return false;

            if (!TryComponent(parts[0], lightnessScale, out float l)) return false;
            if (!TryComponent(parts[1], axisScale, out float a)) return false;
            if (!TryComponent(parts[2], axisScale, out float b)) return false;

            float alpha = 1f;
            if (parts.Length >= 4 && !TryAlpha(parts[3], out alpha)) return false;

            color = cieD50 ? FromLab(l, a, b, alpha) : FromOklab(l, a, b, alpha);
            return true;
        }

        /// <summary>`oklch(L C H)` / `lch(L C H)`, with an optional `/ alpha`.</summary>
        private static bool TryPolar(string args, float lightnessScale, float chromaScale, bool cieD50,
                                     out RgbaColor color)
        {
            color = RgbaColor.Transparent;

            string[] parts = ValueParser.SplitArgs(args);
            if (parts.Length < 3) return false;

            if (!TryComponent(parts[0], lightnessScale, out float l)) return false;
            if (!TryComponent(parts[1], chromaScale, out float c)) return false;
            if (!TryHue(parts[2], out float h)) return false;

            float alpha = 1f;
            if (parts.Length >= 4 && !TryAlpha(parts[3], out alpha)) return false;

            double rad = h * Math.PI / 180.0;
            float a = (float)(c * Math.Cos(rad));
            float b = (float)(c * Math.Sin(rad));

            color = cieD50 ? FromLab(l, a, b, alpha) : FromOklab(l, a, b, alpha);
            return true;
        }

        /// <summary>`hsl(H S% L%)`, the legacy `hsl(H, S%, L%)` and `hsla(...)`, with an optional alpha either way.</summary>
        private static bool TryHsl(string args, out RgbaColor color)
        {
            color = RgbaColor.Transparent;

            string[] parts = ValueParser.SplitArgs(args);
            if (parts.Length < 3) return false;

            if (!TryHue(parts[0], out float h)) return false;
            if (!TryPercent01(parts[1], out float s)) return false;
            if (!TryPercent01(parts[2], out float l)) return false;

            float alpha = 1f;
            if (parts.Length >= 4 && !TryAlpha(parts[3], out alpha)) return false;

            s = Clamp01(s);
            l = Clamp01(l);
            HslToRgb(h, s, l, out float r, out float g, out float b);
            color = new RgbaColor(r, g, b, alpha);
            return true;
        }

        /// <summary>`hwb(H W% B%)` - a hue diluted with whiteness and blackness.</summary>
        private static bool TryHwb(string args, out RgbaColor color)
        {
            color = RgbaColor.Transparent;

            string[] parts = ValueParser.SplitArgs(args);
            if (parts.Length < 3) return false;

            if (!TryHue(parts[0], out float h)) return false;
            if (!TryPercent01(parts[1], out float w)) return false;
            if (!TryPercent01(parts[2], out float b)) return false;

            float alpha = 1f;
            if (parts.Length >= 4 && !TryAlpha(parts[3], out alpha)) return false;

            w = Clamp01(w);
            b = Clamp01(b);

            // Whiteness and blackness that already fill the colour leave no room for the hue; the spec says the
            // result is the grey they mix to.
            if (w + b >= 1f)
            {
                float grey = w / (w + b);
                color = new RgbaColor(grey, grey, grey, alpha);
                return true;
            }

            HslToRgb(h, 1f, 0.5f, out float hr, out float hg, out float hb);
            float scale = 1f - w - b;
            color = new RgbaColor(hr * scale + w, hg * scale + w, hb * scale + w, alpha);
            return true;
        }

        // ---------------------------------------------------------------------------------------------------------
        // color-mix
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>Which way round a polar space walks the hue circle between two colours.</summary>
        private enum HueArc { Shorter, Longer, Increasing, Decreasing }

        /// <summary>
        /// `color-mix(in SPACE, A p%, B q%)`.
        ///
        /// Tailwind v4 compiles every opacity modifier into this, so it carries as much of a v4 stylesheet as
        /// `oklch()` does. The mix that matters most is `color-mix(in oklab, COLOUR 60%, transparent)`, and getting
        /// it right hinges on interpolating premultiplied: without that, the transparent operand drags the result 40%
        /// of the way towards black and the card renders muddy instead of translucent.
        /// </summary>
        private static bool TryColorMix(string args, in RgbaColor currentColor, out RgbaColor color)
        {
            color = RgbaColor.Transparent;

            string[] parts = ValueParser.SplitTopLevel(args, commaSeparated: true);
            if (parts.Length != 3) return false;

            string[] head = ValueParser.SplitTopLevel(parts[0]);
            if (head.Length < 2 || !head[0].Equals("in", StringComparison.OrdinalIgnoreCase)) return false;
            string space = head[1].ToLowerInvariant();

            HueArc arc = HueArc.Shorter;
            if (head.Length >= 3 && !TryHueArc(head[2], out arc)) return false;

            if (!TryOperand(parts[1], currentColor, out RgbaColor ca, out float pa, out bool hasA)) return false;
            if (!TryOperand(parts[2], currentColor, out RgbaColor cb, out float pb, out bool hasB)) return false;

            // Percentage defaulting, per css-color-5: nothing given means half each, one given implies the
            // complement, and a pair that does not add up to 100% is normalised - with the shortfall taken out of the
            // result's alpha rather than silently ignored.
            if (!hasA && !hasB) { pa = 0.5f; pb = 0.5f; }
            else if (!hasA) pa = 1f - pb;
            else if (!hasB) pb = 1f - pa;

            if (pa < 0f) pa = 0f;
            if (pb < 0f) pb = 0f;

            float sum = pa + pb;
            if (sum <= 0f) return false;

            float alphaScale = sum < 1f ? sum : 1f;
            pa /= sum;
            pb /= sum;

            if (!ToSpace(space, ca, out double a0, out double a1, out double a2)) return false;
            if (!ToSpace(space, cb, out double b0, out double b1, out double b2)) return false;

            bool polar = space == "oklch" || space == "lch";
            double chromaA = a1, chromaB = b1;

            // Premultiplied interpolation (css-color-4, "Interpolating with alpha"). Hue is an angle, so it is the
            // one component that is never weighted by alpha.
            a0 *= ca.A; a1 *= ca.A;
            b0 *= cb.A; b1 *= cb.A;
            if (!polar) { a2 *= ca.A; b2 *= cb.A; }

            double alpha = ca.A * pa + cb.A * pb;
            double m0 = a0 * pa + b0 * pb;
            double m1 = a1 * pa + b1 * pb;
            double m2 = polar
                ? MixHue(a2, b2, chromaA, chromaB, pa, pb, arc)
                : a2 * pa + b2 * pb;

            if (alpha > 0.0)
            {
                m0 /= alpha;
                m1 /= alpha;
                if (!polar) m2 /= alpha;
            }

            color = FromSpace(space, m0, m1, m2, (float)alpha * alphaScale);
            return true;
        }

        /// <summary>
        /// One side of a mix: a colour and, in either order, an optional percentage.
        ///
        /// The colour may be any notation at all, so the extended forms are tried here first and hex/`rgb()` fall back
        /// to <see cref="ValueParser"/>. The mutual recursion that creates is bounded - an operand is always strictly
        /// shorter than the `color-mix` that contains it.
        /// </summary>
        private static bool TryOperand(string s, in RgbaColor currentColor, out RgbaColor color,
                                       out float percent, out bool hasPercent)
        {
            color = RgbaColor.Transparent;
            percent = 0f;
            hasPercent = false;

            string[] tokens = ValueParser.SplitTopLevel(s);
            if (tokens.Length == 0) return false;

            var colorTokens = new List<string>(tokens.Length);
            foreach (string t in tokens)
            {
                // A bare percentage token can only be the weight: a percentage that belongs to the colour itself sits
                // inside its parentheses, and SplitTopLevel does not break those open.
                if (!hasPercent && t.EndsWith("%", StringComparison.Ordinal)
                    && ValueParser.TryNumber(t.Substring(0, t.Length - 1), out float p))
                {
                    percent = p / 100f;
                    hasPercent = true;
                    continue;
                }
                colorTokens.Add(t);
            }
            if (colorTokens.Count == 0) return false;

            string colorText = string.Join(" ", colorTokens);
            return TryParse(colorText, currentColor, out color) || ValueParser.TryColor(colorText, out color);
        }

        private static bool TryHueArc(string s, out HueArc arc)
        {
            switch (s.ToLowerInvariant())
            {
                case "shorter": arc = HueArc.Shorter; return true;
                case "longer": arc = HueArc.Longer; return true;
                case "increasing": arc = HueArc.Increasing; return true;
                case "decreasing": arc = HueArc.Decreasing; return true;
                default: arc = HueArc.Shorter; return false;
            }
        }

        private static double MixHue(double ha, double hb, double chromaA, double chromaB,
                                     float pa, float pb, HueArc arc)
        {
            // A colour with no chroma has no hue to contribute - averaging its placeholder angle in would swing the
            // result to a hue neither operand named.
            if (chromaA < 1e-6) return Norm360(hb);
            if (chromaB < 1e-6) return Norm360(ha);

            ha = Norm360(ha);
            hb = Norm360(hb);
            double d = hb - ha;

            switch (arc)
            {
                case HueArc.Longer:
                    if (d > 0.0 && d < 180.0) hb -= 360.0;
                    else if (d <= 0.0 && d > -180.0) hb += 360.0;
                    break;
                case HueArc.Increasing:
                    if (d < 0.0) hb += 360.0;
                    break;
                case HueArc.Decreasing:
                    if (d > 0.0) hb -= 360.0;
                    break;
                default:
                    if (d > 180.0) hb -= 360.0;
                    else if (d < -180.0) hb += 360.0;
                    break;
            }

            return Norm360(ha * pa + hb * pb);
        }

        /// <summary>The interpolation spaces this engine mixes in. An unknown one is refused rather than approximated,
        /// so a stylesheet that asks for something else loses one declaration instead of gaining a wrong colour.</summary>
        private static bool ToSpace(string space, in RgbaColor c, out double c0, out double c1, out double c2)
        {
            switch (space)
            {
                case "srgb":
                    c0 = c.R; c1 = c.G; c2 = c.B;
                    return true;
                case "srgb-linear":
                    c0 = Degamma(c.R); c1 = Degamma(c.G); c2 = Degamma(c.B);
                    return true;
                case "oklab":
                    ToOklab(c, out c0, out c1, out c2);
                    return true;
                case "lab":
                    ToLab(c, out c0, out c1, out c2);
                    return true;
                case "oklch":
                    ToOklab(c, out double ol, out double oa, out double ob);
                    ToPolar(ol, oa, ob, out c0, out c1, out c2);
                    return true;
                case "lch":
                    ToLab(c, out double ll, out double la, out double lb);
                    ToPolar(ll, la, lb, out c0, out c1, out c2);
                    return true;
                default:
                    c0 = c1 = c2 = 0.0;
                    return false;
            }
        }

        private static RgbaColor FromSpace(string space, double c0, double c1, double c2, float alpha)
        {
            switch (space)
            {
                case "srgb":
                    return new RgbaColor(Clamp01((float)c0), Clamp01((float)c1), Clamp01((float)c2), Clamp01(alpha));
                case "srgb-linear":
                    return new RgbaColor(Clamp01((float)Gamma(c0)), Clamp01((float)Gamma(c1)),
                                         Clamp01((float)Gamma(c2)), Clamp01(alpha));
                case "oklab":
                    return FromOklab((float)c0, (float)c1, (float)c2, alpha);
                case "lab":
                    return FromLab((float)c0, (float)c1, (float)c2, alpha);
                case "oklch":
                    FromPolar(c0, c1, c2, out double ol, out double oa, out double ob);
                    return FromOklab((float)ol, (float)oa, (float)ob, alpha);
                default:
                    FromPolar(c0, c1, c2, out double ll, out double la, out double lb);
                    return FromLab((float)ll, (float)la, (float)lb, alpha);
            }
        }

        private static void ToPolar(double l, double a, double b, out double outL, out double c, out double h)
        {
            outL = l;
            c = Math.Sqrt(a * a + b * b);
            h = Norm360(Math.Atan2(b, a) * 180.0 / Math.PI);
        }

        private static void FromPolar(double l, double c, double h, out double outL, out double a, out double b)
        {
            double rad = h * Math.PI / 180.0;
            outL = l;
            a = c * Math.Cos(rad);
            b = c * Math.Sin(rad);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Colour space conversions
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>OKLab to sRGB. OKLab shares sRGB's D65 white point, so this is cube-root undo, one matrix to
        /// linear sRGB, then the transfer function - no chromatic adaptation anywhere.</summary>
        private static RgbaColor FromOklab(float lightness, float aAxis, float bAxis, float alpha)
        {
            double l = lightness, a = aAxis, b = bAxis;

            // OKLab -> LMS' (Ottosson's inverse of the OKLab matrix).
            double lms0 = l + 0.3963377773761749 * a + 0.2158037573099136 * b;
            double lms1 = l - 0.1055613458156586 * a - 0.0638541728258133 * b;
            double lms2 = l - 0.0894841775298119 * a - 1.2914855480194092 * b;

            // LMS' -> LMS is the cube that undoes the cube root taken on the way in.
            lms0 *= lms0 * lms0;
            lms1 *= lms1 * lms1;
            lms2 *= lms2 * lms2;

            double r = 4.0767416360759583 * lms0 - 3.3077115392580616 * lms1 + 0.2309699031821043 * lms2;
            double g = -1.2684379732850315 * lms0 + 2.6097573492876882 * lms1 - 0.3413193760026570 * lms2;
            double bl = -0.0041960761386756 * lms0 - 0.7034186179359362 * lms1 + 1.7076146940746117 * lms2;

            return new RgbaColor(Clamp01((float)Gamma(r)), Clamp01((float)Gamma(g)), Clamp01((float)Gamma(bl)),
                                 Clamp01(alpha));
        }

        private static void ToOklab(in RgbaColor c, out double lightness, out double aAxis, out double bAxis)
        {
            double r = Degamma(c.R), g = Degamma(c.G), b = Degamma(c.B);

            double lms0 = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b;
            double lms1 = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b;
            double lms2 = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b;

            lms0 = Cbrt(lms0);
            lms1 = Cbrt(lms1);
            lms2 = Cbrt(lms2);

            lightness = 0.2104542553 * lms0 + 0.7936177850 * lms1 - 0.0040720468 * lms2;
            aAxis = 1.9779984951 * lms0 - 2.4285922050 * lms1 + 0.4505937099 * lms2;
            bAxis = 0.0259040371 * lms0 + 0.7827717662 * lms1 - 0.8086757660 * lms2;
        }

        // CIE Lab is defined on D50 while sRGB is D65, so this path carries a Bradford adaptation the OKLab path does
        // not need. The reference white below is the D50 the CSS spec names, derived from its chromaticity rather
        // than quoted, because the two differ in the last digits and the round trip has to close.
        private const double D50X = 0.3457 / 0.3585;
        private const double D50Y = 1.0;
        private const double D50Z = (1.0 - 0.3457 - 0.3585) / 0.3585;
        private const double LabEpsilon = 216.0 / 24389.0;
        private const double LabKappa = 24389.0 / 27.0;

        private static RgbaColor FromLab(float lightness, float aAxis, float bAxis, float alpha)
        {
            double l = lightness, a = aAxis, b = bAxis;

            double fy = (l + 16.0) / 116.0;
            double fx = a / 500.0 + fy;
            double fz = fy - b / 200.0;

            double x = fx * fx * fx > LabEpsilon ? fx * fx * fx : (116.0 * fx - 16.0) / LabKappa;
            double y = l > LabKappa * LabEpsilon ? fy * fy * fy : l / LabKappa;
            double z = fz * fz * fz > LabEpsilon ? fz * fz * fz : (116.0 * fz - 16.0) / LabKappa;

            x *= D50X; y *= D50Y; z *= D50Z;

            // XYZ D50 -> XYZ D65, Bradford.
            double x65 = 0.9554734527042182 * x - 0.023098536874261423 * y + 0.0632593086610217 * z;
            double y65 = -0.028369706963208136 * x + 1.0099954580058226 * y + 0.021041398966943008 * z;
            double z65 = 0.012314001688319899 * x - 0.020507696433477912 * y + 1.3303659366080753 * z;

            double r = 3.2409699419045226 * x65 - 1.537383177570094 * y65 - 0.4986107602930034 * z65;
            double g = -0.9692436362808796 * x65 + 1.8759675015077202 * y65 + 0.04155505740717559 * z65;
            double bl = 0.05563007969699366 * x65 - 0.20397695888897652 * y65 + 1.0569715142428786 * z65;

            return new RgbaColor(Clamp01((float)Gamma(r)), Clamp01((float)Gamma(g)), Clamp01((float)Gamma(bl)),
                                 Clamp01(alpha));
        }

        private static void ToLab(in RgbaColor c, out double lightness, out double aAxis, out double bAxis)
        {
            double r = Degamma(c.R), g = Degamma(c.G), b = Degamma(c.B);

            double x65 = 0.4123907992659595 * r + 0.35758433938387796 * g + 0.1804807884018343 * b;
            double y65 = 0.21263900587151036 * r + 0.7151686787677559 * g + 0.07219231536073371 * b;
            double z65 = 0.019330818715591851 * r + 0.11919477979462599 * g + 0.9505321522496606 * b;

            double x = 1.0479298208405488 * x65 + 0.022946793341019088 * y65 - 0.05019222954313557 * z65;
            double y = 0.029627815688159344 * x65 + 0.990434484573249 * y65 - 0.01707382502938514 * z65;
            double z = -0.009243058152591178 * x65 + 0.015055144896577895 * y65 + 0.7518742899580008 * z65;

            double fx = LabF(x / D50X);
            double fy = LabF(y / D50Y);
            double fz = LabF(z / D50Z);

            lightness = 116.0 * fy - 16.0;
            aAxis = 500.0 * (fx - fy);
            bAxis = 200.0 * (fy - fz);
        }

        private static double LabF(double t) => t > LabEpsilon ? Cbrt(t) : (LabKappa * t + 16.0) / 116.0;

        /// <summary>The sRGB transfer function. Signed, because an out-of-gamut mix can land on a negative linear
        /// component and clamping it before the curve would bend the hue instead of just the brightness.</summary>
        private static double Gamma(double c)
        {
            double a = Math.Abs(c);
            double v = a <= 0.0031308 ? 12.92 * a : 1.055 * Math.Pow(a, 1.0 / 2.4) - 0.055;
            return c < 0.0 ? -v : v;
        }

        private static double Degamma(double c)
        {
            double a = Math.Abs(c);
            double v = a <= 0.04045 ? a / 12.92 : Math.Pow((a + 0.055) / 1.055, 2.4);
            return c < 0.0 ? -v : v;
        }

        /// <summary>Cube root that keeps the sign. Math.Pow gives NaN for a negative base, and the a/b axes of both
        /// Lab spaces are routinely negative.</summary>
        private static double Cbrt(double v) => v < 0.0 ? -Math.Pow(-v, 1.0 / 3.0) : Math.Pow(v, 1.0 / 3.0);

        private static void HslToRgb(float hueDegrees, float s, float l, out float r, out float g, out float b)
        {
            // The modulo formulation from CSS Color 4, which needs no case analysis on the sextant.
            double h = Norm360(hueDegrees) / 30.0;
            double a = s * Math.Min(l, 1.0 - l);

            r = (float)HslChannel(0.0, h, l, a);
            g = (float)HslChannel(8.0, h, l, a);
            b = (float)HslChannel(4.0, h, l, a);
        }

        private static double HslChannel(double n, double h, double l, double a)
        {
            double k = (n + h) % 12.0;
            return l - a * Math.Max(-1.0, Math.Min(Math.Min(k - 3.0, 9.0 - k), 1.0));
        }

        // ---------------------------------------------------------------------------------------------------------
        // Component parsing
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>A component that is either a plain number or a percentage of that component's reference range.</summary>
        private static bool TryComponent(string s, float fullScale, out float v)
        {
            v = 0f;
            s = s.Trim();

            // `none` is a real value in these functions and means "no contribution". Zero is what it resolves to
            // outside interpolation, which is the only place this parser is used.
            if (s.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;

            if (s.EndsWith("%", StringComparison.Ordinal))
            {
                if (!ValueParser.TryNumber(s.Substring(0, s.Length - 1), out float p)) return false;
                v = p / 100f * fullScale;
                return true;
            }
            return ValueParser.TryNumber(s, out v);
        }

        /// <summary>A saturation, lightness, whiteness or blackness. The modern syntax allows the percent sign to be
        /// dropped, and `hsl(0 100 50)` means the same as `hsl(0 100% 50%)` - so a bare number is still hundredths,
        /// not a raw fraction.</summary>
        private static bool TryPercent01(string s, out float v)
        {
            v = 0f;
            s = s.Trim();
            if (s.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;

            if (s.EndsWith("%", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);
            if (!ValueParser.TryNumber(s, out float n)) return false;
            v = n / 100f;
            return true;
        }

        /// <summary>An angle in deg, rad, grad or turn - or, as CSS allows for hue, a bare number of degrees.</summary>
        private static bool TryHue(string s, out float degrees)
        {
            degrees = 0f;
            s = s.Trim();
            if (s.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;

            float scale = 1f;
            // `grad` is checked before `rad` because it ends in it.
            if (s.EndsWith("grad", StringComparison.OrdinalIgnoreCase)) { s = s.Substring(0, s.Length - 4); scale = 360f / 400f; }
            else if (s.EndsWith("turn", StringComparison.OrdinalIgnoreCase)) { s = s.Substring(0, s.Length - 4); scale = 360f; }
            else if (s.EndsWith("rad", StringComparison.OrdinalIgnoreCase)) { s = s.Substring(0, s.Length - 3); scale = (float)(180.0 / Math.PI); }
            else if (s.EndsWith("deg", StringComparison.OrdinalIgnoreCase)) { s = s.Substring(0, s.Length - 3); }

            if (!ValueParser.TryNumber(s, out float n)) return false;
            degrees = n * scale;
            return true;
        }

        private static bool TryAlpha(string s, out float v)
        {
            v = 1f;
            s = s.Trim();
            if (s.Equals("none", StringComparison.OrdinalIgnoreCase)) { v = 0f; return true; }

            if (s.EndsWith("%", StringComparison.Ordinal))
            {
                if (!ValueParser.TryNumber(s.Substring(0, s.Length - 1), out float p)) return false;
                v = Clamp01(p / 100f);
                return true;
            }
            if (!ValueParser.TryNumber(s, out float n)) return false;
            v = Clamp01(n);
            return true;
        }

        /// <summary>Out-of-gamut results clamp rather than wrap: a wrapped channel turns a slightly too saturated
        /// colour into its opposite, which reads as a rendering fault rather than as a rounding one.</summary>
        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static double Norm360(double d)
        {
            d %= 360.0;
            return d < 0.0 ? d + 360.0 : d;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Named colours
        // ---------------------------------------------------------------------------------------------------------

        private static bool TryNamed(string name, out RgbaColor color)
        {
            if (Names.TryGetValue(name, out uint packed))
            {
                color = new RgbaColor(((packed >> 16) & 0xFF) / 255f, ((packed >> 8) & 0xFF) / 255f, (packed & 0xFF) / 255f);
                return true;
            }
            color = RgbaColor.Transparent;
            return false;
        }

        /// <summary>
        /// The complete CSS named-colour list: 148 keywords packed as 0xRRGGBB. `transparent` is not in here because
        /// it is the one name with an alpha, and it is answered before the lookup.
        ///
        /// The engine used to carry seventeen of these inline. That covers the CSS 1 set, which no stylesheet written
        /// this decade limits itself to - `slate`, `gainsboro`, `rebeccapurple` and the rest were all silently
        /// dropped. A table is also the cheaper shape: one dictionary probe against a switch over 148 string cases.
        /// </summary>
        private static readonly Dictionary<string, uint> Names = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            { "aliceblue", 0xF0F8FF }, { "antiquewhite", 0xFAEBD7 }, { "aqua", 0x00FFFF },
            { "aquamarine", 0x7FFFD4 }, { "azure", 0xF0FFFF }, { "beige", 0xF5F5DC },
            { "bisque", 0xFFE4C4 }, { "black", 0x000000 }, { "blanchedalmond", 0xFFEBCD },
            { "blue", 0x0000FF }, { "blueviolet", 0x8A2BE2 }, { "brown", 0xA52A2A },
            { "burlywood", 0xDEB887 }, { "cadetblue", 0x5F9EA0 }, { "chartreuse", 0x7FFF00 },
            { "chocolate", 0xD2691E }, { "coral", 0xFF7F50 }, { "cornflowerblue", 0x6495ED },
            { "cornsilk", 0xFFF8DC }, { "crimson", 0xDC143C }, { "cyan", 0x00FFFF },
            { "darkblue", 0x00008B }, { "darkcyan", 0x008B8B }, { "darkgoldenrod", 0xB8860B },
            { "darkgray", 0xA9A9A9 }, { "darkgreen", 0x006400 }, { "darkgrey", 0xA9A9A9 },
            { "darkkhaki", 0xBDB76B }, { "darkmagenta", 0x8B008B }, { "darkolivegreen", 0x556B2F },
            { "darkorange", 0xFF8C00 }, { "darkorchid", 0x9932CC }, { "darkred", 0x8B0000 },
            { "darksalmon", 0xE9967A }, { "darkseagreen", 0x8FBC8F }, { "darkslateblue", 0x483D8B },
            { "darkslategray", 0x2F4F4F }, { "darkslategrey", 0x2F4F4F }, { "darkturquoise", 0x00CED1 },
            { "darkviolet", 0x9400D3 }, { "deeppink", 0xFF1493 }, { "deepskyblue", 0x00BFFF },
            { "dimgray", 0x696969 }, { "dimgrey", 0x696969 }, { "dodgerblue", 0x1E90FF },
            { "firebrick", 0xB22222 }, { "floralwhite", 0xFFFAF0 }, { "forestgreen", 0x228B22 },
            { "fuchsia", 0xFF00FF }, { "gainsboro", 0xDCDCDC }, { "ghostwhite", 0xF8F8FF },
            { "gold", 0xFFD700 }, { "goldenrod", 0xDAA520 }, { "gray", 0x808080 },
            { "green", 0x008000 }, { "greenyellow", 0xADFF2F }, { "grey", 0x808080 },
            { "honeydew", 0xF0FFF0 }, { "hotpink", 0xFF69B4 }, { "indianred", 0xCD5C5C },
            { "indigo", 0x4B0082 }, { "ivory", 0xFFFFF0 }, { "khaki", 0xF0E68C },
            { "lavender", 0xE6E6FA }, { "lavenderblush", 0xFFF0F5 }, { "lawngreen", 0x7CFC00 },
            { "lemonchiffon", 0xFFFACD }, { "lightblue", 0xADD8E6 }, { "lightcoral", 0xF08080 },
            { "lightcyan", 0xE0FFFF }, { "lightgoldenrodyellow", 0xFAFAD2 }, { "lightgray", 0xD3D3D3 },
            { "lightgreen", 0x90EE90 }, { "lightgrey", 0xD3D3D3 }, { "lightpink", 0xFFB6C1 },
            { "lightsalmon", 0xFFA07A }, { "lightseagreen", 0x20B2AA }, { "lightskyblue", 0x87CEFA },
            { "lightslategray", 0x778899 }, { "lightslategrey", 0x778899 }, { "lightsteelblue", 0xB0C4DE },
            { "lightyellow", 0xFFFFE0 }, { "lime", 0x00FF00 }, { "limegreen", 0x32CD32 },
            { "linen", 0xFAF0E6 }, { "magenta", 0xFF00FF }, { "maroon", 0x800000 },
            { "mediumaquamarine", 0x66CDAA }, { "mediumblue", 0x0000CD }, { "mediumorchid", 0xBA55D3 },
            { "mediumpurple", 0x9370DB }, { "mediumseagreen", 0x3CB371 }, { "mediumslateblue", 0x7B68EE },
            { "mediumspringgreen", 0x00FA9A }, { "mediumturquoise", 0x48D1CC }, { "mediumvioletred", 0xC71585 },
            { "midnightblue", 0x191970 }, { "mintcream", 0xF5FFFA }, { "mistyrose", 0xFFE4E1 },
            { "moccasin", 0xFFE4B5 }, { "navajowhite", 0xFFDEAD }, { "navy", 0x000080 },
            { "oldlace", 0xFDF5E6 }, { "olive", 0x808000 }, { "olivedrab", 0x6B8E23 },
            { "orange", 0xFFA500 }, { "orangered", 0xFF4500 }, { "orchid", 0xDA70D6 },
            { "palegoldenrod", 0xEEE8AA }, { "palegreen", 0x98FB98 }, { "paleturquoise", 0xAFEEEE },
            { "palevioletred", 0xDB7093 }, { "papayawhip", 0xFFEFD5 }, { "peachpuff", 0xFFDAB9 },
            { "peru", 0xCD853F }, { "pink", 0xFFC0CB }, { "plum", 0xDDA0DD },
            { "powderblue", 0xB0E0E6 }, { "purple", 0x800080 }, { "rebeccapurple", 0x663399 },
            { "red", 0xFF0000 }, { "rosybrown", 0xBC8F8F }, { "royalblue", 0x4169E1 },
            { "saddlebrown", 0x8B4513 }, { "salmon", 0xFA8072 }, { "sandybrown", 0xF4A460 },
            { "seagreen", 0x2E8B57 }, { "seashell", 0xFFF5EE }, { "sienna", 0xA0522D },
            { "silver", 0xC0C0C0 }, { "skyblue", 0x87CEEB }, { "slateblue", 0x6A5ACD },
            { "slategray", 0x708090 }, { "slategrey", 0x708090 }, { "snow", 0xFFFAFA },
            { "springgreen", 0x00FF7F }, { "steelblue", 0x4682B4 }, { "tan", 0xD2B48C },
            { "teal", 0x008080 }, { "thistle", 0xD8BFD8 }, { "tomato", 0xFF6347 },
            { "turquoise", 0x40E0D0 }, { "violet", 0xEE82EE }, { "wheat", 0xF5DEB3 },
            { "white", 0xFFFFFF }, { "whitesmoke", 0xF5F5F5 }, { "yellow", 0xFFFF00 },
            { "yellowgreen", 0x9ACD32 },
        };
    }
}
