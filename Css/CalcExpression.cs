using System.Globalization;
using Sideload.Model;

namespace Sideload.Css
{
    // Like everything under Css/, this file must stay free of UnityEngine - the headless test project compiles it
    // without an engine reference. That is why the arithmetic below goes through System.Math and never Mathf.

    /// <summary>
    /// Evaluates the CSS math functions - `calc()`, `min()`, `max()` and `clamp()`, nested in any order - down to a
    /// single length in px.
    ///
    /// Everything here is total: an expression this cannot evaluate yields false and the caller leaves the property
    /// alone, which is what a browser does with a bad declaration. The difference to the rest of the value parsing is
    /// that a failure inside a math function is REPORTED (<see cref="DiagnosticKind.ValueRejected"/> under the subject
    /// "calc"), because a dropped `calc()` is the single most common way a Tailwind build renders differently here
    /// than in the browser it was written against.
    ///
    /// Two things it deliberately refuses, both because a browser refuses them too and quietly disagreeing with the
    /// browser is worse than dropping the declaration:
    ///
    ///   - CSS type rules. `calc(10px * 2px)` has no meaning (px squared is not a length) and neither has
    ///     `calc(10px + 2)`; multiplication needs one unitless side, division a unitless divisor.
    ///   - A result that is only a number. `width: calc(2)` is invalid CSS - a unitless value is not a length, not
    ///     even after arithmetic, and reading it as px would silently render what a browser drops.
    ///
    /// All number parsing uses <see cref="CultureInfo.InvariantCulture"/> on purpose: the mod runtime runs with
    /// invariant globalization, and a locale that reads "0,5" would mangle every stylesheet.
    /// </summary>
    internal static class CalcExpression
    {
        /// <summary>
        /// Whether this value even starts a math function. Lets a caller skip <see cref="TryEvaluate"/> for the
        /// ordinary `12px` without it having to decide what counts as one.
        /// </summary>
        internal static bool IsMathFunction(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string s = value.TrimStart();
            return s.StartsWith("calc(", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("min(", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("max(", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("clamp(", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Evaluates a math function to an absolute length. Returns false - and leaves <paramref name="result"/>
        /// at <see cref="Len.None"/> - for anything it cannot work out.
        /// </summary>
        /// <param name="value">The whole value, e.g. `calc(100% - 2rem)`. Any `var()` must already be substituted.</param>
        /// <param name="ctx">What the relative units resolve against.</param>
        /// <param name="result">The length in px on success.</param>
        internal static bool TryEvaluate(string value, in LengthContext ctx, out Len result)
        {
            result = Len.None;
            if (string.IsNullOrEmpty(value)) return false;

            string expr = value.Trim();

            // A value that is not a math function at all is not a failure - it is a plain `12px` on its way to the
            // ordinary parser, and reporting those would bury the real ones.
            if (!IsMathFunction(expr)) return false;

            if (TryRun(expr, ctx, out float px))
            {
                result = Len.Px(px);
                return true;
            }

            // Deliberately silent. The caller reports, because the caller knows the property - `width: calc(...)`
            // tells an author where to look and `calc: calc(...)` does not. Reporting in both places produced two
            // lines for one mistake, which is the fastest way to make a report worth ignoring.
            return false;
        }

        private static bool TryRun(string expr, in LengthContext ctx, out float px)
        {
            px = 0f;

            var tokens = new List<Token>(16);
            if (!Tokenize(expr, tokens)) return false;

            int i = 0;
            if (!ParseValue(tokens, ref i, ctx, out Val v)) return false;

            // The function has to BE the value. `calc(1px) 2px` is two values, and taking the first would render
            // half of what was written.
            if (tokens[i].Kind != TokenKind.End) return false;

            if (v.Dim != 1) return false;
            if (double.IsNaN(v.N) || double.IsInfinity(v.N)) return false;

            px = (float)v.N;
            return true;
        }

        // --- Values ---------------------------------------------------------------------------------------

        /// <summary>
        /// An intermediate result: the number in px, plus how many lengths deep it is. <c>Dim</c> 0 is a plain
        /// number and 1 a length; anything else exists only long enough to be rejected, which is what makes
        /// `calc(10px * 2px)` fail here the way it fails in a browser.
        /// </summary>
        private readonly struct Val
        {
            internal readonly double N;
            internal readonly int Dim;

            internal Val(double n, int dim) { N = n; Dim = dim; }
        }

        private static bool TryUnit(double n, string unit, in LengthContext ctx, out Val v)
        {
            v = default;
            switch (unit)
            {
                case "": v = new Val(n, 0); return true;
                case "px": v = new Val(n, 1); return true;

                case "%":
                    // No basis, no answer. Guessing zero would collapse the box and look like a layout bug rather
                    // than like the missing basis it is.
                    if (float.IsNaN(ctx.PercentBasis)) return false;
                    v = new Val(n * ctx.PercentBasis / 100.0, 1);
                    return true;

                case "rem": v = new Val(n * ctx.RootFontSize, 1); return true;
                case "em": v = new Val(n * ctx.FontSize, 1); return true;
                case "vh": v = new Val(n * ctx.ViewportHeight / 100.0, 1); return true;
                case "vw": v = new Val(n * ctx.ViewportWidth / 100.0, 1); return true;
                case "vmin": v = new Val(n * Math.Min(ctx.ViewportWidth, ctx.ViewportHeight) / 100.0, 1); return true;
                case "vmax": v = new Val(n * Math.Max(ctx.ViewportWidth, ctx.ViewportHeight) / 100.0, 1); return true;

                case "ch":
                    // `ch` is the advance of the "0" glyph, and there are no glyph metrics at this layer - the text
                    // measurer sits above the style code and needs a font asset the cascade never sees. Half the
                    // font size is close for the fonts this UI ships, and far closer than dropping the declaration.
                    v = new Val(n * ctx.FontSize * 0.5, 1);
                    return true;

                default:
                    // Units with no meaning for a length here (`pt`, `cm`, `deg`, `s`, ...). Refused rather than
                    // approximated: a wrong number is harder to find than a missing one.
                    return false;
            }
        }

        // --- Grammar --------------------------------------------------------------------------------------

        private static bool ParseSum(List<Token> t, ref int i, in LengthContext ctx, out Val v)
        {
            if (!ParseProduct(t, ref i, ctx, out v)) return false;

            while (t[i].Kind == TokenKind.Plus || t[i].Kind == TokenKind.Minus)
            {
                // CSS demands whitespace on BOTH sides of + and -, and this is not pedantry: `10px -5px` is a list
                // of two values and `10px - 5px` is a subtraction. Without the check the two spellings would mean
                // the same thing here and different things in every browser.
                if (!t[i].SpaceBefore || !t[i + 1].SpaceBefore) return false;

                bool subtract = t[i].Kind == TokenKind.Minus;
                i++;

                if (!ParseProduct(t, ref i, ctx, out Val rhs)) return false;

                // A length plus a number has no meaning - `calc(100% + 2)` is invalid CSS.
                if (v.Dim != rhs.Dim) return false;

                v = new Val(subtract ? v.N - rhs.N : v.N + rhs.N, v.Dim);
            }

            return true;
        }

        private static bool ParseProduct(List<Token> t, ref int i, in LengthContext ctx, out Val v)
        {
            if (!ParseValue(t, ref i, ctx, out v)) return false;

            while (t[i].Kind == TokenKind.Star || t[i].Kind == TokenKind.Slash)
            {
                bool divide = t[i].Kind == TokenKind.Slash;
                i++;

                if (!ParseValue(t, ref i, ctx, out Val rhs)) return false;

                if (divide)
                {
                    // Only a unitless divisor: `calc(100px / 2px)` would be a bare number, not a length.
                    if (rhs.Dim != 0) return false;
                    if (rhs.N == 0.0) return false;
                    v = new Val(v.N / rhs.N, v.Dim);
                }
                else
                {
                    // One side must be unitless. Two lengths would give px squared, which is not a length.
                    if (v.Dim + rhs.Dim > 1) return false;
                    v = new Val(v.N * rhs.N, v.Dim + rhs.Dim);
                }
            }

            return true;
        }

        private static bool ParseValue(List<Token> t, ref int i, in LengthContext ctx, out Val v)
        {
            v = default;

            bool negate = false;
            while (t[i].Kind == TokenKind.Plus || t[i].Kind == TokenKind.Minus)
            {
                // A sign belongs to the number it precedes, so nothing may come between them. With a space there
                // it is an operator, and an operator in this position means the expression is missing its left
                // operand.
                if (t[i + 1].SpaceBefore) return false;
                if (t[i].Kind == TokenKind.Minus) negate = !negate;
                i++;
            }

            switch (t[i].Kind)
            {
                case TokenKind.Number:
                    if (!TryUnit(t[i].Number, t[i].Text, ctx, out v)) return false;
                    i++;
                    break;

                case TokenKind.Open:
                    i++;
                    if (!ParseSum(t, ref i, ctx, out v)) return false;
                    if (t[i].Kind != TokenKind.Close) return false;
                    i++;
                    break;

                case TokenKind.Function:
                    if (!ParseFunction(t, ref i, ctx, out v)) return false;
                    break;

                default:
                    return false;
            }

            if (negate) v = new Val(-v.N, v.Dim);
            return true;
        }

        private static bool ParseFunction(List<Token> t, ref int i, in LengthContext ctx, out Val v)
        {
            v = default;

            string name = t[i].Text;
            i++;   // the opening parenthesis came with the name

            if (name == "calc")
            {
                if (!ParseSum(t, ref i, ctx, out v)) return false;
                if (t[i].Kind != TokenKind.Close) return false;
                i++;
                return true;
            }

            if (name != "min" && name != "max" && name != "clamp") return false;

            var args = new List<Val>(3);
            if (!ParseArgs(t, ref i, ctx, args)) return false;
            if (args.Count == 0) return false;

            // Comparing a length against a plain number is meaningless, so all arguments must be the same type.
            int dim = args[0].Dim;
            for (int k = 1; k < args.Count; k++)
                if (args[k].Dim != dim) return false;

            if (name == "clamp")
            {
                if (args.Count != 3) return false;

                // CSS defines clamp(MIN, VAL, MAX) as max(MIN, min(VAL, MAX)). The nesting order is the whole
                // behaviour: when MIN is above MAX the MINIMUM wins, and a naive two-sided clamp gets that backwards
                // without ever looking wrong until the ends cross.
                v = new Val(Math.Max(args[0].N, Math.Min(args[1].N, args[2].N)), dim);
                return true;
            }

            double acc = args[0].N;
            for (int k = 1; k < args.Count; k++)
                acc = name == "min" ? Math.Min(acc, args[k].N) : Math.Max(acc, args[k].N);

            v = new Val(acc, dim);
            return true;
        }

        /// <summary>Reads `a, b, c)` including the closing parenthesis. Each argument is a full sum, because
        /// `min(100% - 2rem, 40ch)` needs no inner `calc()` in CSS and authors write it that way.</summary>
        private static bool ParseArgs(List<Token> t, ref int i, in LengthContext ctx, List<Val> args)
        {
            while (true)
            {
                if (!ParseSum(t, ref i, ctx, out Val a)) return false;
                args.Add(a);

                if (t[i].Kind == TokenKind.Comma) { i++; continue; }
                if (t[i].Kind == TokenKind.Close) { i++; return true; }
                return false;
            }
        }

        // --- Tokens ---------------------------------------------------------------------------------------

        private enum TokenKind { Number, Function, Open, Close, Comma, Plus, Minus, Star, Slash, End }

        private readonly struct Token
        {
            internal readonly TokenKind Kind;

            /// <summary>The parsed number, for <see cref="TokenKind.Number"/>.</summary>
            internal readonly double Number;

            /// <summary>The unit of a number (empty when there is none), or the lowercased function name.</summary>
            internal readonly string Text;

            /// <summary>Whether whitespace preceded this token - the whole `10px -5px` versus `10px - 5px`
            /// distinction hangs on it, so it has to survive tokenizing.</summary>
            internal readonly bool SpaceBefore;

            internal Token(TokenKind kind, bool spaceBefore)
            {
                Kind = kind; Number = 0.0; Text = null; SpaceBefore = spaceBefore;
            }

            internal Token(TokenKind kind, string text, bool spaceBefore)
            {
                Kind = kind; Number = 0.0; Text = text; SpaceBefore = spaceBefore;
            }

            internal Token(double number, string unit, bool spaceBefore)
            {
                Kind = TokenKind.Number; Number = number; Text = unit; SpaceBefore = spaceBefore;
            }
        }

        /// <summary>
        /// Splits the expression into tokens, always ending in <see cref="TokenKind.End"/> so the parser can look
        /// one ahead anywhere without a bounds check.
        ///
        /// `+` and `-` become operators here and never part of a number; the parser turns them back into a sign
        /// where one is allowed. Deciding that in the lexer would need the grammar, which the lexer does not have.
        /// </summary>
        private static bool Tokenize(string s, List<Token> tokens)
        {
            bool space = false;
            int i = 0;

            while (i < s.Length)
            {
                char c = s[i];

                if (char.IsWhiteSpace(c)) { space = true; i++; continue; }

                if (TryPunctuation(c, out TokenKind punctuation))
                {
                    tokens.Add(new Token(punctuation, space));
                    space = false;
                    i++;
                    continue;
                }

                if (IsDigit(c) || (c == '.' && i + 1 < s.Length && IsDigit(s[i + 1])))
                {
                    if (!ReadNumber(s, ref i, out double n, out string unit)) return false;
                    tokens.Add(new Token(n, unit, space));
                    space = false;
                    continue;
                }

                if (IsLetter(c))
                {
                    int start = i;
                    while (i < s.Length && (IsLetter(s[i]) || s[i] == '-')) i++;

                    // A bare name means nothing in a length expression, so the only identifier that can appear is a
                    // function - and CSS puts its parenthesis directly against the name.
                    if (i >= s.Length || s[i] != '(') return false;

                    tokens.Add(new Token(TokenKind.Function, s.Substring(start, i - start).ToLowerInvariant(), space));
                    space = false;
                    i++;
                    continue;
                }

                return false;
            }

            tokens.Add(new Token(TokenKind.End, space));
            return true;
        }

        private static bool ReadNumber(string s, ref int i, out double n, out string unit)
        {
            n = 0.0;
            unit = "";

            int start = i;
            while (i < s.Length && IsDigit(s[i])) i++;
            if (i < s.Length && s[i] == '.')
            {
                i++;
                while (i < s.Length && IsDigit(s[i])) i++;
            }

            // An exponent only when digits actually follow it, so the `e` of `1em` stays with the unit.
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                int e = i + 1;
                if (e < s.Length && (s[e] == '+' || s[e] == '-')) e++;
                if (e < s.Length && IsDigit(s[e]))
                {
                    i = e;
                    while (i < s.Length && IsDigit(s[i])) i++;
                }
            }

            if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out n))
                return false;

            if (i < s.Length && s[i] == '%')
            {
                unit = "%";
                i++;
                return true;
            }

            int u = i;
            while (i < s.Length && IsLetter(s[i])) i++;
            if (i > u) unit = s.Substring(u, i - u).ToLowerInvariant();
            return true;
        }

        private static bool TryPunctuation(char c, out TokenKind kind)
        {
            switch (c)
            {
                case '(': kind = TokenKind.Open; return true;
                case ')': kind = TokenKind.Close; return true;
                case ',': kind = TokenKind.Comma; return true;
                case '+': kind = TokenKind.Plus; return true;
                case '-': kind = TokenKind.Minus; return true;
                case '*': kind = TokenKind.Star; return true;
                case '/': kind = TokenKind.Slash; return true;
                default: kind = TokenKind.End; return false;
            }
        }

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        private static bool IsLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
    }
}
