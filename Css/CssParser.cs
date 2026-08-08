using System.Text;

namespace Sideload.Css
{
    /// <summary>Interaction states a rule can require. Sideload tracks these per element instead of asking the DOM,
    /// because a document that is not in a browser has no notion of hover or focus.</summary>
    [Flags]
    internal enum StateFlags
    {
        None = 0,
        Hover = 1,
        Active = 2,
        Focus = 4,
        Disabled = 8,
    }

    internal sealed class Declaration
    {
        internal string Property;
        internal string Value;
        internal bool Important;
    }

    /// <summary>
    /// One selector with its declarations, pre-chewed for the cascade: the dynamic pseudo-classes are split off into
    /// <see cref="RequiredStates"/> so <see cref="BaseSelector"/> is something a DOM library can actually match, and
    /// the specificity is computed once at parse time rather than per element.
    /// </summary>
    internal sealed class StyleRule
    {
        internal string Selector;
        internal string BaseSelector;
        internal StateFlags RequiredStates;
        internal int SpecificityA, SpecificityB, SpecificityC;
        internal int Order;
        internal Orientation? Media;
        internal List<Declaration> Declarations = new List<Declaration>();
    }

    internal sealed class Stylesheet
    {
        internal readonly List<StyleRule> Rules = new List<StyleRule>();
    }

    /// <summary>
    /// Turns stylesheet text into rules. Deliberately a small hand-written scanner rather than a full CSS-OM: the
    /// engine supports a known property subset, so anything it cannot use is better dropped early than modelled.
    ///
    /// Supported at-rule: <c>@media (orientation: portrait|landscape)</c>. Any other at-rule is skipped whole.
    /// </summary>
    internal static class CssParser
    {
        internal static Stylesheet Parse(string css)
        {
            var sheet = new Stylesheet();
            if (string.IsNullOrEmpty(css)) return sheet;

            string src = StripComments(css);
            int order = 0;
            ParseRules(src, 0, src.Length, null, sheet, ref order);
            return sheet;
        }

        private static void ParseRules(string src, int start, int end, Orientation? media, Stylesheet sheet, ref int order)
        {
            int i = start;
            while (i < end)
            {
                while (i < end && char.IsWhiteSpace(src[i])) i++;
                if (i >= end) break;

                if (src[i] == '@')
                {
                    int preludeEnd = i;
                    while (preludeEnd < end && src[preludeEnd] != '{' && src[preludeEnd] != ';') preludeEnd++;

                    if (preludeEnd >= end) break;
                    if (src[preludeEnd] == ';')
                    {
                        // @import, @charset, @namespace, `@layer a, b;`. Nothing is loaded and nothing is ordered.
                        Model.Diagnostics.Report(Model.DiagnosticKind.AtRuleSkipped,
                                                 src.Substring(i, preludeEnd - i).Trim());
                        i = preludeEnd + 1;
                        continue;
                    }

                    int blockEnd = MatchingBrace(src, preludeEnd, end);
                    if (blockEnd < 0) break;

                    string prelude = src.Substring(i, preludeEnd - i);
                    Orientation? nested = ParseMediaOrientation(prelude);

                    // A media block we do not understand is skipped entirely rather than applied unconditionally -
                    // applying it would be worse than ignoring it, because the author gated it for a reason.
                    if (nested.HasValue || IsAlwaysTrueMedia(prelude))
                        ParseRules(src, preludeEnd + 1, blockEnd, nested ?? media, sheet, ref order);
                    else
                        // @keyframes, @layer { }, @property, @supports, @font-face, @container, and every width
                        // breakpoint - the block and everything in it. For a stylesheet out of a build tool this
                        // is where most of the sheet goes, so it is worth a word.
                        Model.Diagnostics.Report(Model.DiagnosticKind.AtRuleSkipped, prelude.Trim());

                    i = blockEnd + 1;
                    continue;
                }

                int braceOpen = i;
                while (braceOpen < end && src[braceOpen] != '{') braceOpen++;
                if (braceOpen >= end) break;

                int braceClose = MatchingBrace(src, braceOpen, end);
                if (braceClose < 0) break;

                string selectorList = src.Substring(i, braceOpen - i).Trim();
                string body = src.Substring(braceOpen + 1, braceClose - braceOpen - 1);

                if (selectorList.Length > 0)
                {
                    List<Declaration> declarations = ParseDeclarations(body);
                    if (declarations.Count > 0)
                    {
                        foreach (string selector in SplitSelectorList(selectorList))
                        {
                            var rule = new StyleRule
                            {
                                Selector = selector,
                                Media = media,
                                Order = order++,
                                Declarations = declarations,
                            };
                            SplitStates(selector, out rule.BaseSelector, out rule.RequiredStates);
                            Specificity(rule.BaseSelector, rule.RequiredStates,
                                        out rule.SpecificityA, out rule.SpecificityB, out rule.SpecificityC);
                            sheet.Rules.Add(rule);
                        }
                    }
                }

                i = braceClose + 1;
            }
        }

        internal static List<Declaration> ParseDeclarations(string body)
        {
            var list = new List<Declaration>();
            if (string.IsNullOrEmpty(body)) return list;

            int depth = 0, start = 0;
            for (int i = 0; i <= body.Length; i++)
            {
                if (i < body.Length)
                {
                    char c = body[i];
                    // Only a top-level ';' ends a declaration. Parentheses adjust the depth and are never separators
                    // themselves, otherwise var(...) and linear-gradient(...) would be cut in half.
                    if (c == '(') { depth++; continue; }
                    if (c == ')') { depth--; continue; }
                    if (c != ';' || depth != 0) continue;
                }

                string chunk = body.Substring(start, i - start).Trim();
                start = i + 1;
                if (chunk.Length == 0) continue;

                int colon = IndexOfTopLevel(chunk, ':');
                if (colon <= 0) continue;

                string property = chunk.Substring(0, colon).Trim();
                string value = chunk.Substring(colon + 1).Trim();
                if (property.Length == 0 || value.Length == 0) continue;

                bool important = false;
                int bang = value.LastIndexOf('!');
                if (bang >= 0 && value.Substring(bang + 1).Trim().Equals("important", StringComparison.OrdinalIgnoreCase))
                {
                    important = true;
                    value = value.Substring(0, bang).Trim();
                }

                if (value.Length > 0)
                    list.Add(new Declaration { Property = property, Value = value, Important = important });
            }
            return list;
        }

        /// <summary>
        /// Splits the dynamic pseudo-classes off the selector. Restriction: they are only honoured on the SUBJECT (the
        /// last compound), so `button:hover` works and `.card:hover .title` does not - the latter needs the state of an
        /// ancestor, which the per-element state model cannot express. Such a rule matches without the state instead of
        /// being dropped.
        /// </summary>
        internal static void SplitStates(string selector, out string baseSelector, out StateFlags states)
        {
            states = StateFlags.None;
            baseSelector = selector;
            if (string.IsNullOrEmpty(selector)) { baseSelector = "*"; return; }

            int subjectStart = LastCombinatorEnd(selector);
            string prefix = selector.Substring(0, subjectStart);
            string subject = selector.Substring(subjectStart);

            var kept = new StringBuilder();
            int i = 0;
            while (i < subject.Length)
            {
                if (subject[i] == ':' && (i + 1 >= subject.Length || subject[i + 1] != ':'))
                {
                    int nameStart = i + 1;
                    int nameEnd = nameStart;
                    while (nameEnd < subject.Length && (char.IsLetterOrDigit(subject[nameEnd]) || subject[nameEnd] == '-')) nameEnd++;
                    string name = subject.Substring(nameStart, nameEnd - nameStart).ToLowerInvariant();

                    StateFlags flag = name switch
                    {
                        "hover" => StateFlags.Hover,
                        "active" => StateFlags.Active,
                        "focus" => StateFlags.Focus,
                        "focus-visible" => StateFlags.Focus,
                        "focus-within" => StateFlags.Focus,
                        "disabled" => StateFlags.Disabled,
                        _ => StateFlags.None,
                    };

                    if (flag != StateFlags.None)
                    {
                        states |= flag;
                        i = nameEnd;
                        continue;
                    }
                }
                kept.Append(subject[i]);
                i++;
            }

            string keptSubject = kept.ToString();
            if (keptSubject.Trim().Length == 0) keptSubject = "*";
            baseSelector = (prefix + keptSubject).Trim();
            if (baseSelector.Length == 0) baseSelector = "*";
        }

        /// <summary>Specificity as (ids, classes+attributes+pseudo-classes, types). The stripped state pseudo-classes
        /// still count - dropping them would let `button` beat `button:hover`.</summary>
        internal static void Specificity(string selector, StateFlags states, out int a, out int b, out int c)
        {
            a = 0; b = 0; c = 0;
            if (string.IsNullOrEmpty(selector)) return;

            for (int i = 0; i < 4; i++)
                if (((int)states & (1 << i)) != 0) b++;

            int p = 0;
            while (p < selector.Length)
            {
                char ch = selector[p];
                if (ch == '#') { a++; p = SkipIdent(selector, p + 1); }
                else if (ch == '.') { b++; p = SkipIdent(selector, p + 1); }
                else if (ch == '[') { b++; while (p < selector.Length && selector[p] != ']') p++; p++; }
                else if (ch == ':')
                {
                    if (p + 1 < selector.Length && selector[p + 1] == ':') { c++; p = SkipIdent(selector, p + 2); }
                    else { b++; p = SkipIdent(selector, p + 1); }
                }
                else if (char.IsLetter(ch)) { c++; p = SkipIdent(selector, p); }
                else p++;
            }
        }

        // --------------------------------------------------------------------- helpers --

        private static int SkipIdent(string s, int i)
        {
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '-' || s[i] == '_')) i++;
            return i;
        }

        /// <summary>Index just after the last top-level combinator, i.e. where the subject compound begins.</summary>
        private static int LastCombinatorEnd(string selector)
        {
            int depth = 0, last = 0;
            for (int i = 0; i < selector.Length; i++)
            {
                char c = selector[i];
                if (c == '[' || c == '(') depth++;
                else if (c == ']' || c == ')') depth--;
                else if (depth == 0 && (c == ' ' || c == '>' || c == '+' || c == '~' || c == '\t' || c == '\n' || c == '\r'))
                    last = i + 1;
            }
            return last;
        }

        internal static List<string> SplitSelectorList(string list)
        {
            var result = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i <= list.Length; i++)
            {
                if (i < list.Length)
                {
                    char c = list[i];
                    if (c == '(' || c == '[') depth++;
                    else if (c == ')' || c == ']') depth--;
                    else if (c != ',' || depth != 0) continue;
                }
                string s = list.Substring(start, i - start).Trim();
                start = i + 1;
                if (s.Length > 0) result.Add(s);
            }
            return result;
        }

        private static int IndexOfTopLevel(string s, char target)
        {
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == target && depth == 0) return i;
            }
            return -1;
        }

        private static int MatchingBrace(string s, int open, int end)
        {
            int depth = 0;
            for (int i = open; i < end; i++)
            {
                if (s[i] == '{') depth++;
                else if (s[i] == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static Orientation? ParseMediaOrientation(string prelude)
        {
            if (prelude.IndexOf("orientation", StringComparison.OrdinalIgnoreCase) < 0) return null;
            if (prelude.IndexOf("portrait", StringComparison.OrdinalIgnoreCase) >= 0) return Orientation.Portrait;
            if (prelude.IndexOf("landscape", StringComparison.OrdinalIgnoreCase) >= 0) return Orientation.Landscape;
            return null;
        }

        /// <summary>`@media screen`, `@media all` and bare `@media` carry no condition the engine needs to honour.</summary>
        private static bool IsAlwaysTrueMedia(string prelude)
        {
            string p = prelude.Trim();
            if (!p.StartsWith("@media", StringComparison.OrdinalIgnoreCase)) return false;
            string rest = p.Substring(6).Trim().ToLowerInvariant();
            return rest.Length == 0 || rest == "all" || rest == "screen";
        }

        internal static string StripComments(string css)
        {
            var sb = new StringBuilder(css.Length);
            for (int i = 0; i < css.Length; i++)
            {
                if (css[i] == '/' && i + 1 < css.Length && css[i + 1] == '*')
                {
                    int close = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (close < 0) break;
                    i = close + 1;
                    continue;
                }
                sb.Append(css[i]);
            }
            return sb.ToString();
        }
    }
}
