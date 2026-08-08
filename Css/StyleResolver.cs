using System.Text;
using AngleSharp.Dom;

namespace Sideload.Css
{
    /// <summary>Everything the cascade needs beyond the document and the stylesheet.</summary>
    internal sealed class StyleContext
    {
        internal Orientation Orientation = Orientation.Landscape;

        /// <summary>Interaction state per element. Sideload tracks hover/active/focus itself, so the cascade asks
        /// rather than inspecting the DOM. Null means "no element has any state".</summary>
        internal Func<IElement, StateFlags> StateOf;

        /// <summary>The CSS viewport, for `vh`, `vw`, `vmin` and `vmax`. Defaults to the phone in landscape so a
        /// caller that forgets to set it is wrong by a rotation rather than by a division by zero.</summary>
        internal float ViewportWidth = 733.44f;

        internal float ViewportHeight = 400f;

        /// <summary>
        /// The root's font size in px, which is what `rem` means.
        ///
        /// Not 16. The engine's own default is 15 (`ComputedStyle.FontSize`), and every Tailwind size is written
        /// in rem - so this one number decides whether a page built for the web comes out 7 percent too large.
        /// A page that wants the browser's scale sets `html { font-size: 16px }` and gets it.
        /// </summary>
        internal float RootFontSize = 15f;
    }

    /// <summary>
    /// Runs the cascade: which declarations win on which element, and what the resulting computed style is.
    ///
    /// Selector matching is delegated to the DOM library's own <c>QuerySelectorAll</c>, once per rule. That buys
    /// correct selector semantics (descendant, child, attribute, :not, ...) for free and inverts cheaply into a
    /// per-element rule list; writing a matcher by hand would be the same work with more bugs.
    /// </summary>
    internal static class StyleResolver
    {
        private const int MaxVarDepth = 8;

        internal static Dictionary<IElement, ComputedStyle> Resolve(IDocument document, Stylesheet sheet, StyleContext context)
        {
            var result = new Dictionary<IElement, ComputedStyle>();
            if (document?.DocumentElement == null) return result;

            context ??= new StyleContext();
            Dictionary<IElement, List<StyleRule>> matches = MatchRules(document, sheet, context.Orientation);

            // `@property` declares what a custom property means when nobody has set it. Tailwind v4 leans on that
            // hard: a utility writes `transform: translate(var(--tw-translate-x), ...)` and expects the registered
            // initial value to stand in for the axis nobody touched. Without this the whole declaration is
            // invalid, so a single translate utility takes the transform with it.
            //
            // Seeded at the ROOT, so it inherits like any other custom property and any real declaration - which
            // is applied later, on the element itself - overrides it.
            var root = ComputedStyle.CreateFrom(null);
            if (sheet?.InitialVariables != null)
                foreach (var initial in sheet.InitialVariables)
                    root.SetVariable(initial.Key, initial.Value);

            Walk(document.DocumentElement, root, matches, context, result);
            return result;
        }

        /// <summary>
        /// Elements that any rule targets through an interaction state. Only these need a hit target and pointer
        /// handlers - wiring up every box would cost a transparent quad each and make the page swallow the pointer
        /// everywhere.
        /// </summary>
        internal static HashSet<IElement> StatefulElements(IDocument document, Stylesheet sheet, Orientation orientation)
        {
            var result = new HashSet<IElement>();
            if (document == null || sheet == null) return result;

            foreach (StyleRule rule in sheet.Rules)
            {
                if (rule.RequiredStates == StateFlags.None) continue;
                if (rule.Media.HasValue && rule.Media.Value != orientation) continue;

                try
                {
                    foreach (IElement element in document.QuerySelectorAll(rule.BaseSelector)) result.Add(element);
                }
                catch (Exception e)
                {
                    Model.Diagnostics.Report(Model.DiagnosticKind.SelectorRejected, rule.BaseSelector, e.Message);
                }
            }
            return result;
        }

        /// <summary>Inverts "rule -> elements" into "element -> rules", skipping rules gated behind the other orientation.</summary>
        private static Dictionary<IElement, List<StyleRule>> MatchRules(IDocument document, Stylesheet sheet, Orientation orientation)
        {
            var map = new Dictionary<IElement, List<StyleRule>>();
            if (sheet == null) return map;

            foreach (StyleRule rule in sheet.Rules)
            {
                if (rule.Media.HasValue && rule.Media.Value != orientation) continue;

                IHtmlCollection<IElement> hits;
                // A selector the DOM library rejects drops out and the rest of the sheet still works - but it
                // drops out in total silence unless it is named here. `.a:not(:hover)` gets here: the state
                // stripper tears the :hover out of the :not() and leaves `.a:not()`, which is not a selector.
                try { hits = document.QuerySelectorAll(rule.BaseSelector); }
                catch (Exception e)
                {
                    Model.Diagnostics.Report(Model.DiagnosticKind.SelectorRejected, rule.Selector, e.Message);
                    continue;
                }

                foreach (IElement element in hits)
                {
                    if (!map.TryGetValue(element, out List<StyleRule> list))
                    {
                        list = new List<StyleRule>();
                        map[element] = list;
                    }
                    list.Add(rule);
                }
            }
            return map;
        }

        private static void Walk(IElement element, ComputedStyle parentStyle,
                                 Dictionary<IElement, List<StyleRule>> matches, StyleContext context,
                                 Dictionary<IElement, ComputedStyle> result)
        {
            ComputedStyle style = ComputeFor(element, parentStyle, matches, context);
            result[element] = style;

            foreach (IElement child in element.Children)
                Walk(child, style, matches, context, result);
        }

        private static ComputedStyle ComputeFor(IElement element, ComputedStyle parentStyle,
                                                Dictionary<IElement, List<StyleRule>> matches, StyleContext context)
        {
            var style = ComputedStyle.CreateFrom(parentStyle);

            StateFlags state = context.StateOf != null ? context.StateOf(element) : StateFlags.None;
            var winners = new List<Entry>();

            if (matches.TryGetValue(element, out List<StyleRule> rules))
            {
                foreach (StyleRule rule in rules)
                {
                    // A rule requiring :hover only applies while the element actually hovers; requiring several
                    // states means all of them, which is how a compound like `button:focus:hover` reads.
                    if ((rule.RequiredStates & state) != rule.RequiredStates) continue;

                    foreach (Declaration declaration in rule.Declarations)
                        winners.Add(new Entry(declaration, rule.SpecificityA, rule.SpecificityB, rule.SpecificityC,
                                              rule.Order, false, rule.LayerRank));
                }
            }

            string inline = element.GetAttribute("style");
            if (!string.IsNullOrWhiteSpace(inline))
            {
                foreach (Declaration declaration in CssParser.ParseDeclarations(inline))
                    winners.Add(new Entry(declaration, 0, 0, 0, int.MaxValue, true));
            }

            winners.Sort(Compare);

            // Custom properties first: a later declaration may reference one through var(), and CSS resolves those
            // against the element's final variable set, not against whatever happened to be declared above it.
            foreach (Entry entry in winners)
            {
                if (IsCustomProperty(entry.Declaration.Property))
                    style.SetVariable(entry.Declaration.Property.Trim(), entry.Declaration.Value);
            }

            // The context every relative length is measured against. Set before the first declaration lands,
            // because the first thing applied below is font-size and `em` on everything after it reads FontSize
            // back off the style.
            StyleApplier.Context = new LengthContext
            {
                FontSize = style.FontSize,
                RootFontSize = context?.RootFontSize ?? 15f,
                ViewportWidth = context?.ViewportWidth ?? 733.44f,
                ViewportHeight = context?.ViewportHeight ?? 400f,
                PercentBasis = float.NaN,
            };

            // font-size before anything else, and it is not a preference: `padding: 2em` means twice THIS
            // element's font size, so applying padding first would measure it against the inherited one. CSS
            // says the same, it just gets to say it as a computed-value dependency rather than an ordering.
            //
            // font-size's own `em` still refers to the parent's, which falls out for free: the context is built
            // from the inherited FontSize above, and only updated once font-size itself has landed.
            foreach (Entry entry in winners)
            {
                if (!IsFontSize(entry.Declaration.Property)) continue;

                string sized = Substituted(entry.Declaration.Value, style);
                if (sized == null) continue;
                StyleApplier.Apply(style, entry.Declaration.Property, sized);
            }

            StyleApplier.Context = new LengthContext
            {
                FontSize = style.FontSize,
                RootFontSize = context?.RootFontSize ?? 15f,
                ViewportWidth = context?.ViewportWidth ?? 733.44f,
                ViewportHeight = context?.ViewportHeight ?? 400f,
                PercentBasis = float.NaN,
            };

            foreach (Entry entry in winners)
            {
                Declaration declaration = entry.Declaration;
                if (IsCustomProperty(declaration.Property)) continue;
                if (IsFontSize(declaration.Property)) continue;   // already applied, above

                string value = declaration.Value;
                if (value.IndexOf("var(", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    value = SubstituteVariables(value, style.Variables, 0);
                    if (value == null) continue;   // unresolvable var() makes the declaration invalid, as in CSS
                }

                StyleApplier.Apply(style, declaration.Property, value);
            }

            return style;
        }

        private static bool IsCustomProperty(string property) =>
            property != null && property.TrimStart().StartsWith("--", StringComparison.Ordinal);

        private static bool IsFontSize(string property) =>
            property != null && property.Trim().Equals("font-size", StringComparison.OrdinalIgnoreCase);

        /// <summary>A declaration value with its `var()` references resolved, or null when one cannot be.</summary>
        private static string Substituted(string value, ComputedStyle style)
        {
            if (value == null) return null;
            if (value.IndexOf("var(", StringComparison.OrdinalIgnoreCase) < 0) return value;
            return SubstituteVariables(value, style.Variables, 0);
        }

        /// <summary>
        /// Replaces every `var(--name, fallback)` with its value. Returns null when a reference cannot be resolved and
        /// has no fallback, which invalidates the whole declaration.
        /// </summary>
        internal static string SubstituteVariables(string value, Dictionary<string, string> variables, int depth)
        {
            if (depth > MaxVarDepth || string.IsNullOrEmpty(value)) return null;

            var sb = new StringBuilder(value.Length);
            int i = 0;
            while (i < value.Length)
            {
                int start = value.IndexOf("var(", i, StringComparison.OrdinalIgnoreCase);
                if (start < 0) { sb.Append(value, i, value.Length - i); break; }

                sb.Append(value, i, start - i);

                int open = start + 3;
                int close = MatchingParen(value, open);
                if (close < 0) return null;

                string args = value.Substring(open + 1, close - open - 1);
                int comma = TopLevelComma(args);
                string name = (comma < 0 ? args : args.Substring(0, comma)).Trim();
                string fallback = comma < 0 ? null : args.Substring(comma + 1).Trim();

                string resolved = null;
                if (variables != null && name.StartsWith("--", StringComparison.Ordinal))
                    variables.TryGetValue(name, out resolved);

                if (resolved == null) resolved = fallback;
                if (resolved == null) return null;

                if (resolved.IndexOf("var(", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    resolved = SubstituteVariables(resolved, variables, depth + 1);
                    if (resolved == null) return null;
                }

                sb.Append(resolved);
                i = close + 1;
            }
            return sb.ToString();
        }

        private static int MatchingParen(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static int TopLevelComma(string s)
        {
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')') depth--;
                else if (s[i] == ',' && depth == 0) return i;
            }
            return -1;
        }

        private readonly struct Entry
        {
            internal readonly Declaration Declaration;
            internal readonly int A, B, C, Order, Layer;
            internal readonly bool Inline;

            internal Entry(Declaration declaration, int a, int b, int c, int order, bool inline, int layer = 0)
            {
                Declaration = declaration; A = a; B = b; C = c; Order = order; Inline = inline; Layer = layer;
            }
        }

        /// <summary>
        /// Ascending cascade order - the last entry applied wins. Importance beats everything, then inline style,
        /// then the CASCADE LAYER, then specificity, then document order.
        ///
        /// The layer sits above specificity and that is the whole point of layers: a one-class utility has to be
        /// able to beat a three-class component rule, which specificity alone can never let it do. It is also the
        /// only reason Tailwind's base/components/utilities ordering works at all. `LayerRank` is 0 for unlayered
        /// and negative for layered, earliest layer most negative, so ascending sort puts unlayered on top - which
        /// is what CSS says: an author's unlayered styles win over anything they put in a layer.
        /// </summary>
        private static int Compare(Entry x, Entry y)
        {
            int xi = x.Declaration.Important ? 1 : 0, yi = y.Declaration.Important ? 1 : 0;
            if (xi != yi) return xi - yi;

            int xin = x.Inline ? 1 : 0, yin = y.Inline ? 1 : 0;
            if (xin != yin) return xin - yin;

            if (x.Layer != y.Layer) return x.Layer - y.Layer;

            if (x.A != y.A) return x.A - y.A;
            if (x.B != y.B) return x.B - y.B;
            if (x.C != y.C) return x.C - y.C;
            return x.Order.CompareTo(y.Order);
        }
    }
}
