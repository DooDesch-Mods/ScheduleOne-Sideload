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

        /// <summary>
        /// Whether this page wants the web's defaults rather than the engine's.
        ///
        /// Set by `&lt;meta name="sideload" content="web-defaults"&gt;` in the bundle. Today that means one thing -
        /// an undeclared flex container lays out as a ROW, the way a browser does - and it exists because a page
        /// built by a web toolchain says `.flex` and means a row. See ComputedStyle.DefaultDirection.
        /// </summary>
        internal bool WebDefaults;
    }

    /// <summary>
    /// The finished styles of the boxes CSS generates, kept apart from the elements' own.
    ///
    /// Apart, because one element can carry three styles - its own, its <c>::before</c> and its <c>::after</c> -
    /// and a dictionary keyed by element holds one.
    ///
    /// Only boxes that EXIST are in here. A pseudo-element without <c>content</c> is not a box at all, so it is
    /// never stored, and that one rule is what stops every element a utility sheet touches from growing two empty
    /// children.
    /// </summary>
    internal sealed class PseudoStyles
    {
        private readonly Dictionary<IElement, ComputedStyle> _before = new Dictionary<IElement, ComputedStyle>();
        private readonly Dictionary<IElement, ComputedStyle> _after = new Dictionary<IElement, ComputedStyle>();

        /// <summary>How many generated boxes this page has, across both kinds.</summary>
        internal int Count => _before.Count + _after.Count;

        internal void Set(IElement element, PseudoElement which, ComputedStyle style)
        {
            Dictionary<IElement, ComputedStyle> table = Table(which);
            if (element != null && style != null && table != null) table[element] = style;
        }

        internal ComputedStyle Get(IElement element, PseudoElement which)
        {
            Dictionary<IElement, ComputedStyle> table = Table(which);
            return element != null && table != null && table.TryGetValue(element, out ComputedStyle style)
                ? style : null;
        }

        private Dictionary<IElement, ComputedStyle> Table(PseudoElement which) =>
            which == PseudoElement.Before ? _before : which == PseudoElement.After ? _after : null;
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

        /// <summary>The cascade for a caller that has no use for generated boxes - the inspector, a test.</summary>
        internal static Dictionary<IElement, ComputedStyle> Resolve(IDocument document, Stylesheet sheet,
                                                                    StyleContext context) =>
            Resolve(document, sheet, context, out _);

        internal static Dictionary<IElement, ComputedStyle> Resolve(IDocument document, Stylesheet sheet,
                                                                    StyleContext context, out PseudoStyles generated)
        {
            var result = new Dictionary<IElement, ComputedStyle>();
            generated = new PseudoStyles();
            if (document?.DocumentElement == null) return result;

            context ??= new StyleContext();

            ComputedStyle.DefaultDirection = context.WebDefaults ? FlexDirection.Row : FlexDirection.Column;

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

            Walk(document.DocumentElement, root, matches, context, result, generated);
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
                                 Dictionary<IElement, ComputedStyle> result, PseudoStyles generated)
        {
            ComputedStyle style = ComputeFor(element, parentStyle, matches, context, PseudoElement.None);
            result[element] = style;

            generated?.Set(element, PseudoElement.Before,
                           Generated(element, style, PseudoElement.Before, matches, context));
            generated?.Set(element, PseudoElement.After,
                           Generated(element, style, PseudoElement.After, matches, context));

            foreach (IElement child in element.Children)
                Walk(child, style, matches, context, result, generated);
        }

        /// <summary>
        /// The style of one generated box, or null when this sheet generates none.
        ///
        /// The originating element's FINISHED style is the parent here, because that is what a pseudo-element
        /// inherits from - its own rules then win over what came down, like any other child.
        ///
        /// Without `content` there is no box. That is what CSS says, and it is the line that keeps a stray
        /// `.card::before { position: absolute }` in some utility sheet from hanging an empty child off every card
        /// on the page.
        /// </summary>
        private static ComputedStyle Generated(IElement element, ComputedStyle style, PseudoElement which,
                                               Dictionary<IElement, List<StyleRule>> matches, StyleContext context)
        {
            if (!matches.TryGetValue(element, out List<StyleRule> rules)) return null;

            bool targeted = false;
            foreach (StyleRule rule in rules)
                if (rule.Pseudo == which) { targeted = true; break; }
            if (!targeted) return null;

            ComputedStyle box = ComputeFor(element, style, matches, context, which);
            return box.Content == null ? null : box;
        }

        private static ComputedStyle ComputeFor(IElement element, ComputedStyle parentStyle,
                                                Dictionary<IElement, List<StyleRule>> matches, StyleContext context,
                                                PseudoElement pseudo)
        {
            var style = ComputedStyle.CreateFrom(parentStyle);

            StateFlags state = context.StateOf != null ? context.StateOf(element) : StateFlags.None;
            var winners = new List<Entry>();

            if (matches.TryGetValue(element, out List<StyleRule> rules))
            {
                foreach (StyleRule rule in rules)
                {
                    // `.a::before` matched the element, but it is not ABOUT the element. Without this line its
                    // declarations would land on `.a` itself and colour the card the badge was meant to sit on.
                    if (rule.Pseudo != pseudo) continue;

                    // A rule requiring :hover only applies while the element actually hovers; requiring several
                    // states means all of them, which is how a compound like `button:focus:hover` reads.
                    if ((rule.RequiredStates & state) != rule.RequiredStates) continue;

                    foreach (Declaration declaration in rule.Declarations)
                        winners.Add(new Entry(declaration, rule.SpecificityA, rule.SpecificityB, rule.SpecificityC,
                                              rule.Order, false, rule.LayerRank));
                }
            }

            // The `style` attribute belongs to the element and to nothing it generates - there is no way to write
            // an inline style for a box that is not in the document.
            string inline = pseudo == PseudoElement.None ? element.GetAttribute("style") : null;
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

                if (IsContent(declaration.Property)) value = SubstituteAttributes(value, element);

                StyleApplier.Apply(style, declaration.Property, value);
            }

            return style;
        }

        private static bool IsCustomProperty(string property) =>
            property != null && property.TrimStart().StartsWith("--", StringComparison.Ordinal);

        private static bool IsFontSize(string property) =>
            property != null && property.Trim().Equals("font-size", StringComparison.OrdinalIgnoreCase);

        private static bool IsContent(string property) =>
            property != null && property.Trim().Equals("content", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Replaces every `attr(name)` in a value with that attribute of the element, as a quoted string.
        ///
        /// Here rather than in the applier because the applier has no element: it is handed a property and a value
        /// and nothing else, and `attr()` is the one piece of a value only the cascade can answer. A generated box
        /// reads the attributes of the element it belongs to, which is what `attr()` means on a pseudo-element.
        ///
        /// A missing attribute becomes the fallback, or the empty string when there is none - as in CSS, and it is
        /// what keeps `content: attr(data-label)` from dropping the declaration on the one row without a label.
        /// </summary>
        private static string SubstituteAttributes(string value, IElement element)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (value.IndexOf("attr(", StringComparison.OrdinalIgnoreCase) < 0) return value;

            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];

                // Inside quotes it is text: `content: "attr(x)"` prints those letters, it does not read an
                // attribute.
                if (c == '"' || c == '\'')
                {
                    int end = QuoteEnd(value, i);
                    sb.Append(value, i, end - i + 1);
                    i = end;
                    continue;
                }

                if (c != 'a' && c != 'A') { sb.Append(c); continue; }
                if (string.Compare(value, i, "attr(", 0, 5, StringComparison.OrdinalIgnoreCase) != 0)
                {
                    sb.Append(c);
                    continue;
                }

                int close = MatchingParen(value, i + 4);
                if (close < 0) { sb.Append(c); continue; }

                string args = value.Substring(i + 5, close - i - 5);
                int comma = TopLevelComma(args);
                string name = (comma < 0 ? args : args.Substring(0, comma)).Trim();
                string fallback = comma < 0 ? null : args.Substring(comma + 1).Trim().Trim('"', '\'');

                string read = element?.GetAttribute(name);
                sb.Append(Quote(read ?? fallback ?? ""));
                i = close;
            }
            return sb.ToString();
        }

        /// <summary>An attribute's text as a CSS string, so whatever is in the document cannot end the string
        /// early or start an escape of its own.</summary>
        private static string Quote(string text) =>
            "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static int QuoteEnd(string s, int quote)
        {
            char delimiter = s[quote];
            for (int i = quote + 1; i < s.Length; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                if (s[i] == delimiter) return i;
            }
            return s.Length - 1;
        }

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
