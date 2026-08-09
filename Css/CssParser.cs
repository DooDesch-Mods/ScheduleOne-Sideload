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
    /// A parsed <c>@media</c> condition, kept as a tree rather than reduced to a yes/no at parse time.
    ///
    /// The screen is fixed - one long edge and one short one - so a width query has exactly two answers, one per
    /// orientation, and both are computable from here. Keeping the tree is still what makes a combined condition
    /// honest: <c>(max-width:400px) and (orientation:portrait)</c> has to be read as one thing, because honouring
    /// whichever half the parser recognised applies the block in every portrait, which is worse than dropping it.
    /// </summary>
    internal sealed class MediaCondition
    {
        /// <summary>The phone's screen in CSS pixels. Landscape is long by short, portrait is the other way round.</summary>
        internal const float LongEdgePx = 733.44f;
        internal const float ShortEdgePx = 400f;

        internal enum Axis { Width, Height }

        internal enum Compare { AtLeast, AtMost, Above, Below, Exact }

        private enum Node { Constant, Orientation, Length, And, Or, Not }

        private readonly Node _node;
        private readonly bool _constant;
        private readonly Orientation _orientation;
        private readonly Axis _axis;
        private readonly Compare _compare;
        private readonly float _px;
        private readonly MediaCondition _a, _b;

        private MediaCondition(Node node, bool constant, Orientation orientation,
                               Axis axis, Compare compare, float px, MediaCondition a, MediaCondition b)
        {
            _node = node; _constant = constant; _orientation = orientation;
            _axis = axis; _compare = compare; _px = px; _a = a; _b = b;
        }

        internal static readonly MediaCondition Always =
            new MediaCondition(Node.Constant, true, default, default, default, 0f, null, null);

        internal static readonly MediaCondition Never =
            new MediaCondition(Node.Constant, false, default, default, default, 0f, null, null);

        internal static MediaCondition Constant(bool value) => value ? Always : Never;

        internal static MediaCondition IsOrientation(Orientation orientation) =>
            new MediaCondition(Node.Orientation, false, orientation, default, default, 0f, null, null);

        internal static MediaCondition Length(Axis axis, Compare compare, float px) =>
            new MediaCondition(Node.Length, false, default, axis, compare, px, null, null);

        /// <summary>Null stands for "no condition", so nesting a block inside another one is a plain And.</summary>
        internal static MediaCondition And(MediaCondition a, MediaCondition b)
        {
            if (a == null) return b;
            if (b == null) return a;
            return new MediaCondition(Node.And, false, default, default, default, 0f, a, b);
        }

        internal static MediaCondition Or(MediaCondition a, MediaCondition b)
        {
            if (a == null) return b;
            if (b == null) return a;
            return new MediaCondition(Node.Or, false, default, default, default, 0f, a, b);
        }

        internal static MediaCondition Not(MediaCondition a) =>
            a == null ? null : new MediaCondition(Node.Not, false, default, default, default, 0f, a, null);

        internal bool Matches(Orientation orientation)
        {
            switch (_node)
            {
                case Node.Constant: return _constant;
                case Node.Orientation: return _orientation == orientation;
                case Node.Length: return Test(_axis == Axis.Width ? WidthPx(orientation) : HeightPx(orientation));
                case Node.And: return _a.Matches(orientation) && _b.Matches(orientation);
                case Node.Or: return _a.Matches(orientation) || _b.Matches(orientation);
                case Node.Not: return !_a.Matches(orientation);
                default: return true;
            }
        }

        /// <summary>The two numbers a breakpoint is measured against, for a report that has to name them. There is
        /// one screen here and it turns, so "the viewport" is a pair rather than a value.</summary>
        internal static string ViewportDescription =>
            $"{LongEdgePx:0}x{ShortEdgePx:0} css px, or {ShortEdgePx:0}x{LongEdgePx:0} turned";

        internal static float WidthPx(Orientation orientation) =>
            orientation == Orientation.Landscape ? LongEdgePx : ShortEdgePx;

        internal static float HeightPx(Orientation orientation) =>
            orientation == Orientation.Landscape ? ShortEdgePx : LongEdgePx;

        /// <summary>True when no orientation satisfies this - the block can be dropped rather than carried around.</summary>
        internal bool Impossible => !Matches(Orientation.Portrait) && !Matches(Orientation.Landscape);

        /// <summary>
        /// The one orientation this condition allows, or null when it allows both.
        ///
        /// This is what the cascade already gates on, so a width breakpoint arrives there as the orientation it
        /// really means: on this screen <c>(min-width: 640px)</c> IS landscape. Cached because the inspector asks
        /// it once per rule per element.
        /// </summary>
        internal Orientation? OnlyOrientation
        {
            get
            {
                if (!_resolved)
                {
                    bool portrait = Matches(Orientation.Portrait);
                    bool landscape = Matches(Orientation.Landscape);
                    _only = portrait == landscape ? (Orientation?)null
                          : portrait ? Orientation.Portrait : Orientation.Landscape;
                    _resolved = true;
                }
                return _only;
            }
        }

        private bool _resolved;
        private Orientation? _only;

        private bool Test(float actual)
        {
            switch (_compare)
            {
                case Compare.AtLeast: return actual >= _px;
                case Compare.AtMost: return actual <= _px;
                case Compare.Above: return actual > _px;
                case Compare.Below: return actual < _px;
                default: return Math.Abs(actual - _px) < 0.01f;
            }
        }
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

        /// <summary>
        /// Which generated box this rule is about, or <see cref="PseudoElement.None"/> for the element itself.
        ///
        /// Split off the selector for the same reason the states are: what is left has to be something the DOM
        /// library can match, and no DOM library can hand back a box that is not in the document.
        /// </summary>
        internal PseudoElement Pseudo;
        internal int SpecificityA, SpecificityB, SpecificityC;
        internal int Order;

        /// <summary>The <c>@media</c> gate this rule sits behind, or null when it sits behind none.</summary>
        internal MediaCondition Condition;

        /// <summary>
        /// The orientation this rule is limited to, or null for both. Derived from <see cref="Condition"/> so there
        /// is one source of truth: a width breakpoint and an orientation query answer the same question here.
        /// </summary>
        internal Orientation? Media => Condition?.OnlyOrientation;

        /// <summary>The cascade layer this rule was declared in, or null for none. Qualified with a dot for a
        /// layer inside a layer, as CSS writes it.</summary>
        internal string Layer;

        /// <summary>
        /// Where this rule's layer sits in the cascade. Zero means unlayered, which is where the default has to be:
        /// a sheet without a single <c>@layer</c> must sort exactly as it did before layers existed.
        ///
        /// Layered rules are NEGATIVE, and the earlier the layer was declared the lower it goes. That is the whole
        /// cascade-layer rule in one number - unlayered beats every layer, and layers beat each other in declaration
        /// order - so a consumer only has to compare this before specificity.
        /// </summary>
        internal int LayerRank;

        internal List<Declaration> Declarations = new List<Declaration>();
    }

    internal sealed class Stylesheet
    {
        internal readonly List<StyleRule> Rules = new List<StyleRule>();

        /// <summary>
        /// Every cascade layer the sheet named, in the order it declared them - the order a bare
        /// <c>@layer theme, base, components, utilities;</c> exists to fix. First mention wins, so a layer that is
        /// only ever opened as a block still lands where it first appeared.
        /// </summary>
        internal readonly List<string> LayerOrder = new List<string>();

        /// <summary>
        /// The <c>initial-value</c> of every custom property registered with <c>@property</c>.
        ///
        /// Without these a <c>var(--tw-translate-x)</c> that nobody assigned resolves to nothing and takes its whole
        /// declaration down with it, which in a Tailwind build is most of the transform and shadow utilities.
        /// </summary>
        internal readonly Dictionary<string, string> InitialVariables =
            new Dictionary<string, string>(StringComparer.Ordinal);

        internal void RegisterLayer(string name)
        {
            if (!string.IsNullOrEmpty(name) && !LayerOrder.Contains(name)) LayerOrder.Add(name);
        }
    }

    /// <summary>
    /// Turns stylesheet text into rules. Deliberately a small hand-written scanner rather than a full CSS-OM: the
    /// engine supports a known property subset, so anything it cannot use is better dropped early than modelled.
    ///
    /// Handled at-rules: <c>@media</c> (orientation, width and height conditions including the range form),
    /// <c>@layer</c> (unwrapped, order recorded), <c>@supports</c> (evaluated against what the engine really does),
    /// <c>@property</c> (initial values kept), and CSS nesting inside any rule body. Everything else - and every
    /// condition this cannot read - is skipped whole and reported, because applying a block the author gated is
    /// worse than ignoring it.
    /// </summary>
    internal static class CssParser
    {
        /// <summary>A stylesheet nested this deep is a runaway, not a document. Cheaper than a stack overflow.</summary>
        private const int MaxNesting = 32;

        internal static Stylesheet Parse(string css)
        {
            var sheet = new Stylesheet();
            if (string.IsNullOrEmpty(css)) return sheet;

            string src = StripComments(css);
            var state = new ParseState { Sheet = sheet };
            ParseRules(src, 0, src.Length, default, state);
            AssignLayerRanks(sheet);
            return sheet;
        }

        /// <summary>Everything a block inherits from the block around it.</summary>
        private readonly struct Scope
        {
            /// <summary>The media gate in force, or null outside any <c>@media</c>.</summary>
            internal readonly MediaCondition Media;

            /// <summary>The cascade layer in force, or null outside any <c>@layer</c>.</summary>
            internal readonly string Layer;

            /// <summary>The selectors a nested block belongs to, already expanded. Null at the top level, where
            /// a stray declaration belongs to nobody.</summary>
            internal readonly List<string> Selectors;

            private Scope(MediaCondition media, string layer, List<string> selectors)
            {
                Media = media; Layer = layer; Selectors = selectors;
            }

            internal Scope WithMedia(MediaCondition media) => new Scope(media, Layer, Selectors);
            internal Scope WithLayer(string layer) => new Scope(Media, layer, Selectors);
            internal Scope WithSelectors(List<string> selectors) => new Scope(Media, Layer, selectors);
        }

        private sealed class ParseState
        {
            internal Stylesheet Sheet;
            internal int Order;
            internal int Depth;
            internal int AnonymousLayers;
        }

        /// <summary>A sequence of qualified rules and at-rules, as found at the top level or inside an at-rule block.</summary>
        private static void ParseRules(string src, int start, int end, Scope scope, ParseState state)
        {
            int i = start;
            while (i < end)
            {
                while (i < end && char.IsWhiteSpace(src[i])) i++;
                if (i >= end) break;

                if (src[i] == '@')
                {
                    int next = ParseAtRule(src, i, end, scope, state);
                    if (next < 0) break;
                    i = next;
                    continue;
                }

                int braceOpen = i;
                while (braceOpen < end && src[braceOpen] != '{') braceOpen++;
                if (braceOpen >= end) break;

                int braceClose = MatchingBrace(src, braceOpen, end);
                if (braceClose < 0) break;

                string selectorList = src.Substring(i, braceOpen - i).Trim();
                if (selectorList.Length > 0)
                {
                    Scope inner = scope.WithSelectors(Expand(scope.Selectors, selectorList));
                    ParseBody(src.Substring(braceOpen + 1, braceClose - braceOpen - 1), inner, state);
                }

                i = braceClose + 1;
            }
        }

        /// <summary>
        /// The contents of an at-rule block: rules at the top level, but declarations for the rule around it when
        /// the at-rule sits inside one. Tailwind writes both, sometimes in the same file.
        /// </summary>
        private static void ParseContents(string src, int start, int end, Scope scope, ParseState state)
        {
            if (state.Depth >= MaxNesting) return;

            state.Depth++;
            try
            {
                if (scope.Selectors == null) ParseRules(src, start, end, scope, state);
                else ParseBody(src.Substring(start, end - start), scope, state);
            }
            finally { state.Depth--; }
        }

        /// <summary>
        /// A rule body: its own declarations, plus whatever is nested inside it.
        ///
        /// The declarations are emitted first so the parent keeps document order against its children, which is the
        /// order a browser resolves them in when both touch the same property.
        /// </summary>
        private static void ParseBody(string body, Scope scope, ParseState state)
        {
            if (state.Depth >= MaxNesting) return;

            SplitBody(body, out string declarationText, out string nested);
            EmitRules(declarationText, scope, state);

            if (nested == null) return;
            state.Depth++;
            try { ParseRules(nested, 0, nested.Length, scope, state); }
            finally { state.Depth--; }
        }

        private static void EmitRules(string declarationText, Scope scope, ParseState state)
        {
            if (scope.Selectors == null || scope.Selectors.Count == 0) return;

            List<Declaration> declarations = ParseDeclarations(declarationText);
            if (declarations.Count == 0) return;

            foreach (string selector in scope.Selectors)
            {
                var rule = new StyleRule
                {
                    Selector = selector,
                    Condition = scope.Media,
                    Layer = scope.Layer,
                    Order = state.Order++,
                    Declarations = declarations,
                };
                SplitStates(selector, out string stateless, out rule.RequiredStates);
                SplitPseudoElement(stateless, out rule.BaseSelector, out rule.Pseudo);

                // Measured on the selector BEFORE the pseudo-element comes off it, because `::before` counts as a
                // type and stripping it first would let `.a` and `.a::before` tie.
                Specificity(stateless, rule.RequiredStates,
                            out rule.SpecificityA, out rule.SpecificityB, out rule.SpecificityC);
                state.Sheet.Rules.Add(rule);
            }
        }

        // ------------------------------------------------------------------- at-rules --

        /// <summary>Handles one at-rule and returns the index just past it, or -1 when the text runs out.</summary>
        private static int ParseAtRule(string src, int at, int end, Scope scope, ParseState state)
        {
            int preludeEnd = at;
            while (preludeEnd < end && src[preludeEnd] != '{' && src[preludeEnd] != ';') preludeEnd++;
            if (preludeEnd >= end) return -1;

            string prelude = src.Substring(at, preludeEnd - at);
            string name = AtRuleName(prelude);

            if (src[preludeEnd] == ';')
            {
                // `@layer a, b, c;` declares nothing but the ORDER, which is the whole point of writing it: every
                // rule that lands in one of those layers later sorts by where the name stands here.
                if (name == "layer") DeclareLayers(prelude, scope, state);
                else
                    // @import, @charset, @namespace. Nothing is fetched, so the sheet the author expected is not
                    // the sheet the engine got, and that has to be said out loud.
                    Model.Diagnostics.Report(Model.DiagnosticKind.AtRuleSkipped, prelude.Trim());

                return preludeEnd + 1;
            }

            int blockEnd = MatchingBrace(src, preludeEnd, end);
            if (blockEnd < 0) return -1;

            int bodyStart = preludeEnd + 1;
            switch (name)
            {
                case "media":
                {
                    MediaCondition condition = ParseMediaPrelude(prelude);
                    MediaCondition combined = MediaCondition.And(scope.Media, condition);

                    // Two different failures, and they read as one only if you already know the engine. A prelude
                    // this parser cannot follow is a gap; a breakpoint wider than the phone will ever be is a
                    // number the author should change. Reporting both as "skipped" sent people looking for a
                    // missing feature when the answer was 1024 on a 733px screen.
                    if (condition == null)
                        Model.Diagnostics.Report(Model.DiagnosticKind.AtRuleSkipped, prelude.Trim());
                    else if (combined.Impossible)
                        Model.Diagnostics.Report(Model.DiagnosticKind.MediaUnreachable, prelude.Trim(),
                                                 MediaCondition.ViewportDescription);
                    else
                        ParseContents(src, bodyStart, blockEnd, scope.WithMedia(combined), state);
                    break;
                }

                case "layer":
                {
                    ParseContents(src, bodyStart, blockEnd, scope.WithLayer(EnterLayer(prelude, scope, state)), state);
                    break;
                }

                case "supports":
                {
                    MediaCondition condition = ParseSupportsPrelude(prelude);

                    // Every leaf of a `@supports` condition is a constant, so which orientation it is asked in
                    // makes no difference - it is the same tree only because the two grammars are the same shape.
                    if (condition != null && condition.Matches(Orientation.Landscape))
                        ParseContents(src, bodyStart, blockEnd, scope, state);
                    else
                        // The author wrote a fallback for exactly this case, so skipping is what lets it through.
                        Model.Diagnostics.Report(Model.DiagnosticKind.AtRuleSkipped, prelude.Trim());
                    break;
                }

                case "property":
                {
                    RegisterProperty(prelude, src.Substring(bodyStart, blockEnd - bodyStart), state);
                    break;
                }

                default:
                    // @keyframes, @font-face, @container, @page and anything newer. The block and everything in it.
                    Model.Diagnostics.Report(Model.DiagnosticKind.AtRuleSkipped, prelude.Trim());
                    break;
            }

            return blockEnd + 1;
        }

        /// <summary>The at-rule keyword in lower case, without the '@'.</summary>
        private static string AtRuleName(string prelude)
        {
            int i = 1;
            while (i < prelude.Length && (char.IsLetterOrDigit(prelude[i]) || prelude[i] == '-')) i++;
            return prelude.Substring(1, i - 1).ToLowerInvariant();
        }

        // ---------------------------------------------------------------------- layers --

        private static void DeclareLayers(string prelude, Scope scope, ParseState state)
        {
            foreach (string name in SplitCommas(prelude.Substring("@layer".Length)))
                state.Sheet.RegisterLayer(Qualify(scope.Layer, name));
        }

        /// <summary>Opens `@layer name { ... }`, registering the name if this is where it first appears.</summary>
        private static string EnterLayer(string prelude, Scope scope, ParseState state)
        {
            string name = prelude.Substring("@layer".Length).Trim();

            // An anonymous layer is its own layer at its own position, so it needs a name nobody can write.
            if (name.Length == 0) name = "#anon-" + state.AnonymousLayers++;

            string qualified = Qualify(scope.Layer, name);
            state.Sheet.RegisterLayer(qualified);
            return qualified;
        }

        private static string Qualify(string parent, string name) =>
            string.IsNullOrEmpty(parent) ? name : parent + "." + name;

        /// <summary>
        /// Turns each rule's layer NAME into its rank, once the sheet has been read and the layer count is known.
        ///
        /// It cannot happen while parsing: a layer opened at the bottom of the file still sorts where its name was
        /// first declared at the top, and until the last line nobody knows how many layers there are.
        /// </summary>
        private static void AssignLayerRanks(Stylesheet sheet)
        {
            if (sheet.LayerOrder.Count == 0) return;

            var rank = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < sheet.LayerOrder.Count; i++)
                rank[sheet.LayerOrder[i]] = i - sheet.LayerOrder.Count;

            foreach (StyleRule rule in sheet.Rules)
                if (rule.Layer != null && rank.TryGetValue(rule.Layer, out int value))
                    rule.LayerRank = value;
        }

        // -------------------------------------------------------------------- @property --

        private static void RegisterProperty(string prelude, string body, ParseState state)
        {
            string name = prelude.Substring("@property".Length).Trim();
            if (!name.StartsWith("--", StringComparison.Ordinal)) return;

            foreach (Declaration declaration in ParseDeclarations(body))
                if (string.Equals(declaration.Property, "initial-value", StringComparison.OrdinalIgnoreCase))
                    state.Sheet.InitialVariables[name] = declaration.Value;
        }

        // ---------------------------------------------------------------------- nesting --

        /// <summary>
        /// Splits a rule body into the declarations that belong to the rule and the blocks nested inside it.
        ///
        /// The nested blocks come back as one run of text rather than a list of ranges because the rule parser
        /// already reads a sequence of blocks, and the parser keeps no source positions to invalidate.
        /// </summary>
        private static void SplitBody(string body, out string declarations, out string nested)
        {
            nested = null;
            if (string.IsNullOrEmpty(body)) { declarations = ""; return; }

            // A body with no block in it is the common case by far, and copying it would allocate for nothing.
            if (body.IndexOf('{') < 0) { declarations = body; return; }

            var declarationText = new StringBuilder(body.Length);
            StringBuilder blocks = null;

            int depth = 0, cut = 0;
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (c == '"' || c == '\'') { i = StringEnd(body, i); continue; }
                if (c == '(') depth++;
                else if (c == ')') { if (depth > 0) depth--; }
                else if (depth != 0) continue;
                else if (c == ';')
                {
                    declarationText.Append(body, cut, i - cut + 1);
                    cut = i + 1;
                }
                else if (c == '{')
                {
                    int close = MatchingBrace(body, i, body.Length);
                    if (close < 0) close = body.Length - 1;

                    // From the last separator, so the nested rule keeps its selector or at-rule prelude.
                    (blocks ??= new StringBuilder()).Append(body, cut, close - cut + 1).Append('\n');
                    i = close;
                    cut = close + 1;
                }
            }

            if (cut < body.Length) declarationText.Append(body, cut, body.Length - cut);

            declarations = declarationText.ToString();
            nested = blocks?.ToString();
        }

        /// <summary>
        /// Resolves a nested selector list against the selectors it is nested in.
        ///
        /// <c>&amp;</c> stands for the parent wherever it appears, and a selector without one is a descendant, which
        /// is the two forms a build tool emits. Nesting under a selector LIST expands to one selector per parent
        /// rather than an <c>:is()</c>, because the DOM library matches the plain form and the cascade wants one
        /// entry per selector anyway.
        /// </summary>
        private static List<string> Expand(List<string> parents, string selectorList)
        {
            List<string> selectors = SplitSelectorList(selectorList);
            if (parents == null || parents.Count == 0) return selectors;

            var expanded = new List<string>(parents.Count * selectors.Count);
            foreach (string selector in selectors)
                foreach (string parent in parents)
                    expanded.Add(selector.IndexOf('&') >= 0
                        ? selector.Replace("&", parent)
                        : parent + " " + selector);

            return expanded;
        }

        // ----------------------------------------------------------------- declarations --

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

                    // A quoted string is text, not structure: `content: "{"` says nothing about nesting.
                    if (c == '"' || c == '\'') { i = StringEnd(body, i); continue; }

                    // Only a top-level ';' ends a declaration. Parentheses adjust the depth and are never separators
                    // themselves, otherwise var(...) and linear-gradient(...) would be cut in half.
                    if (c == '(') { depth++; continue; }
                    if (c == ')') { depth--; continue; }

                    // A nested rule is not a declaration and neither is the selector in front of it. Callers that
                    // split the body first never get here; an inline style handed straight in still cannot be
                    // derailed by a stray block.
                    if (c == '{' && depth == 0)
                    {
                        int close = MatchingBrace(body, i, body.Length);
                        i = close < 0 ? body.Length : close;
                        start = i + 1;
                        continue;
                    }

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

        /// <summary>
        /// Splits a trailing <c>::before</c> or <c>::after</c> off the selector, leaving something the DOM library
        /// can match and naming the generated box separately.
        ///
        /// It has to come off. AngleSharp accepts either spelling and then matches NOTHING with it - a pseudo-
        /// element is not a node in the document - so a rule handed over intact applies to nobody and says nothing
        /// about it. That is the whole of CSS-034: the rules were parsed, matched against zero elements, and
        /// disappeared.
        ///
        /// Only the two-colon spelling. The one-colon <c>:before</c> is the legacy form that a browser still reads
        /// as a pseudo-element, and this engine does not - a deliberate line, not an oversight. A single colon is
        /// a pseudo-CLASS everywhere else in this parser; telling `:before` from a pseudo-class the engine has not
        /// heard of would take a list of every name CSS will ever have. Left alone it reaches AngleSharp, matches
        /// nothing, and generates nothing, which is where it already stood.
        ///
        /// Only when it is the SUBJECT, i.e. at the very end. <c>.a::before .b</c> matches nothing in a browser
        /// either, and stripping the pseudo out of the middle would turn it into a selector that does.
        ///
        /// The other pseudo-elements go to the DOM library untouched: <c>::marker</c>, <c>::selection</c> and
        /// <c>::backdrop</c> are refused by it and reported, <c>::first-line</c> and <c>::first-letter</c> are
        /// matched AS the element and so style the whole of it, which is a gap of its own.
        /// </summary>
        internal static void SplitPseudoElement(string selector, out string baseSelector, out PseudoElement pseudo)
        {
            baseSelector = selector;
            pseudo = PseudoElement.None;
            if (string.IsNullOrEmpty(selector)) return;

            int at = selector.LastIndexOf("::", StringComparison.Ordinal);
            if (at < 0) return;

            switch (selector.Substring(at + 2).Trim().ToLowerInvariant())
            {
                case "before": pseudo = PseudoElement.Before; break;
                case "after": pseudo = PseudoElement.After; break;

                // Both spellings: `::placeholder` is the standard and `::-webkit-input-placeholder` is what a
                // sheet written before 2017 says. Neither generates a box - see PseudoStyles.
                case "placeholder":
                case "-webkit-input-placeholder":
                case "-moz-placeholder":
                case "-ms-input-placeholder": pseudo = PseudoElement.Placeholder; break;
                default: return;
            }

            baseSelector = selector.Substring(0, at).Trim();

            // `::before { }` on its own belongs to every element, the same way a bare `:hover` does.
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

        // ----------------------------------------------------------------------- @media --

        /// <summary>
        /// Reads a whole `@media` prelude. Null means the engine could not read it, which is the signal to skip the
        /// block and say so - one unreadable query poisons the list rather than letting the rest apply on its own,
        /// because a half-honoured condition applies where the author never asked for it.
        /// </summary>
        private static MediaCondition ParseMediaPrelude(string prelude)
        {
            string rest = prelude.Trim();
            if (!rest.StartsWith("@media", StringComparison.OrdinalIgnoreCase)) return null;

            rest = rest.Substring("@media".Length).Trim();
            if (rest.Length == 0) return MediaCondition.Always;   // bare `@media { }` gates nothing

            MediaCondition any = null;
            foreach (string query in SplitCommas(rest))
            {
                MediaCondition one = ParseMediaQuery(query);
                if (one == null) return null;
                any = MediaCondition.Or(any, one);
            }
            return any;
        }

        private static MediaCondition ParseMediaQuery(string query)
        {
            string text = query.Trim();

            // `only screen` exists to hide a query from browsers that predate media queries, and says nothing else.
            if (StartsWithWord(text, "only")) text = text.Substring(4).Trim();

            int i = 0;
            MediaCondition condition = ParseCondition(text, ref i, MediaFeature, MediaType);
            if (condition == null) return null;

            SkipWhitespace(text, ref i);
            return i >= text.Length ? condition : null;
        }

        /// <summary>A media TYPE rather than a feature. This engine is a screen, and it is never any of the others.</summary>
        private static MediaCondition MediaType(string word) => word.ToLowerInvariant() switch
        {
            "screen" or "all" => MediaCondition.Always,
            "print" or "speech" or "tty" or "tv" or "projection" or "handheld" or "braille" or "embossed" or "aural"
                => MediaCondition.Never,
            _ => null,
        };

        /// <summary>The inside of one `( ... )` in a media query.</summary>
        private static MediaCondition MediaFeature(string inside)
        {
            int colon = IndexOfTopLevel(inside, ':');
            if (colon > 0)
                return MediaFeatureValue(inside.Substring(0, colon).Trim().ToLowerInvariant(),
                                         inside.Substring(colon + 1).Trim().ToLowerInvariant());

            if (inside.IndexOf('<') >= 0 || inside.IndexOf('>') >= 0 || inside.IndexOf('=') >= 0)
                return MediaRange(inside);

            // Boolean form: `(hover)` asks whether the feature has any value at all.
            return inside.Trim().ToLowerInvariant() switch
            {
                "hover" or "any-hover" or "pointer" or "any-pointer" or "width" or "height" => MediaCondition.Always,
                _ => null,
            };
        }

        private static MediaCondition MediaFeatureValue(string name, string value)
        {
            switch (name)
            {
                case "orientation":
                    if (value == "portrait") return MediaCondition.IsOrientation(Orientation.Portrait);
                    if (value == "landscape") return MediaCondition.IsOrientation(Orientation.Landscape);
                    return null;

                // The pointer that drives the phone is a mouse: it hovers, and it is not a fingertip. Answering
                // these is what lets a sheet's `hover:` utilities through instead of dropping every one of them.
                case "hover":
                case "any-hover":
                    return value == "hover" ? MediaCondition.Always : value == "none" ? MediaCondition.Never : null;

                case "pointer":
                case "any-pointer":
                    return value == "fine" ? MediaCondition.Always
                         : value == "coarse" || value == "none" ? MediaCondition.Never : null;
            }

            // The device IS the viewport here, so the device-* variants ask the same question.
            string axis = name;
            MediaCondition.Compare compare = MediaCondition.Compare.Exact;
            if (axis.StartsWith("min-", StringComparison.Ordinal)) { compare = MediaCondition.Compare.AtLeast; axis = axis.Substring(4); }
            else if (axis.StartsWith("max-", StringComparison.Ordinal)) { compare = MediaCondition.Compare.AtMost; axis = axis.Substring(4); }
            if (axis.StartsWith("device-", StringComparison.Ordinal)) axis = axis.Substring(7);

            if (!TryAxis(axis, out MediaCondition.Axis which)) return null;
            if (!TryMediaLength(value, out float px)) return null;
            return MediaCondition.Length(which, compare, px);
        }

        /// <summary>
        /// The range form: `(width >= 640px)`, `(width &lt; 400px)` and the double-ended `(400px &lt;= width &lt;= 800px)`.
        /// Tailwind v4 emits every breakpoint this way, so not reading it drops the whole responsive half of a build.
        /// </summary>
        private static MediaCondition MediaRange(string inside)
        {
            var parts = new List<string>();
            var operators = new List<string>();

            int start = 0;
            for (int i = 0; i < inside.Length; i++)
            {
                string op = null;
                if (i + 1 < inside.Length && (inside[i] == '<' || inside[i] == '>') && inside[i + 1] == '=')
                    op = inside.Substring(i, 2);
                else if (inside[i] == '<' || inside[i] == '>' || inside[i] == '=')
                    op = inside[i].ToString();
                if (op == null) continue;

                parts.Add(inside.Substring(start, i - start).Trim());
                operators.Add(op);
                i += op.Length - 1;
                start = i + 1;
            }
            parts.Add(inside.Substring(start).Trim());

            if (operators.Count == 1)
            {
                if (TryAxis(parts[0], out MediaCondition.Axis left))
                    return Bound(left, operators[0], parts[1]);
                if (TryAxis(parts[1], out MediaCondition.Axis right))
                    return Bound(right, Flip(operators[0]), parts[0]);
                return null;
            }

            if (operators.Count == 2 && TryAxis(parts[1], out MediaCondition.Axis axis))
                return MediaCondition.And(Bound(axis, Flip(operators[0]), parts[0]),
                                          Bound(axis, operators[1], parts[2]));

            return null;
        }

        private static MediaCondition Bound(MediaCondition.Axis axis, string op, string length)
        {
            if (!TryMediaLength(length, out float px)) return null;

            MediaCondition.Compare compare = op switch
            {
                ">=" => MediaCondition.Compare.AtLeast,
                "<=" => MediaCondition.Compare.AtMost,
                ">" => MediaCondition.Compare.Above,
                "<" => MediaCondition.Compare.Below,
                _ => MediaCondition.Compare.Exact,
            };
            return MediaCondition.Length(axis, compare, px);
        }

        private static string Flip(string op) => op switch
        {
            ">=" => "<=",
            "<=" => ">=",
            ">" => "<",
            "<" => ">",
            _ => op,
        };

        private static bool TryAxis(string name, out MediaCondition.Axis axis)
        {
            switch (name.Trim().ToLowerInvariant())
            {
                case "width": axis = MediaCondition.Axis.Width; return true;
                case "height": axis = MediaCondition.Axis.Height; return true;
                default: axis = default; return false;
            }
        }

        /// <summary>
        /// A length in a media query. `rem` and `em` both resolve against 16px here and not against the page's font
        /// size, which is what the spec says for media queries and what a build tool assumes when it writes `40rem`
        /// for a 640px breakpoint.
        /// </summary>
        private static bool TryMediaLength(string text, out float px)
        {
            px = 0f;
            if (string.IsNullOrEmpty(text)) return false;
            text = text.Trim().ToLowerInvariant();

            foreach ((string suffix, float basis) in MediaUnits)
            {
                if (!text.EndsWith(suffix, StringComparison.Ordinal)) continue;
                if (!ValueParser.TryNumber(text.Substring(0, text.Length - suffix.Length), out float n)) continue;
                px = n * basis;
                return true;
            }

            // A bare number is only a length when it is zero, exactly as in a declaration.
            if (!ValueParser.TryNumber(text, out float bare) || bare != 0f) return false;
            return true;
        }

        /// <summary>Longest suffix first, so `rem` is not read as `em`.</summary>
        private static readonly (string Suffix, float Basis)[] MediaUnits =
        {
            ("rem", 16f),
            ("em", 16f),
            ("px", 1f),
            ("pt", 4f / 3f),
        };

        // -------------------------------------------------------------------- @supports --

        /// <summary>
        /// Reads a `@supports` prelude into a constant condition. Null means unreadable, which is skipped and
        /// reported like any other condition the engine cannot answer.
        /// </summary>
        private static MediaCondition ParseSupportsPrelude(string prelude)
        {
            string rest = prelude.Trim();
            if (!rest.StartsWith("@supports", StringComparison.OrdinalIgnoreCase)) return null;

            rest = rest.Substring("@supports".Length).Trim();
            if (rest.Length == 0) return null;

            int i = 0;
            MediaCondition condition = ParseCondition(rest, ref i, SupportsTest, _ => null);
            if (condition == null) return null;

            SkipWhitespace(rest, ref i);
            return i >= rest.Length ? condition : null;
        }

        private static MediaCondition SupportsTest(string inside)
        {
            int colon = IndexOfTopLevel(inside, ':');
            if (colon <= 0) return null;

            string property = inside.Substring(0, colon).Trim();
            string value = inside.Substring(colon + 1).Trim();
            if (property.Length == 0 || value.Length == 0) return null;

            return MediaCondition.Constant(DeclarationWorks(property, value));
        }

        /// <summary>
        /// Whether this engine would actually honour `property: value`.
        ///
        /// <see cref="StyleApplier.Supports"/> answers for the NAME alone, and a name is not the question
        /// `@supports` asks: the engine reads `display` and then does nothing with `grid`, so a sheet that gates its
        /// grid layout on it has to land in the fallback. So the real applier runs on a throwaway style and its own
        /// complaints are the answer - no second table of what works to keep in step.
        /// </summary>
        private static bool DeclarationWorks(string property, string value)
        {
            // Custom properties are storage. Any of them "works", which is also what a browser reports.
            if (property.StartsWith("--", StringComparison.Ordinal)) return true;

            Action<Model.Diagnostic> sink = Model.Diagnostics.Sink;
            bool muted = Model.Diagnostics.Muted;
            bool complained = false;

            // The probe's own complaints belong to nobody: the author did not write this declaration to be applied,
            // they wrote it to be asked about.
            Model.Diagnostics.Sink = d => complained |= d.Kind == Model.DiagnosticKind.ValueRejected
                                                     || d.Kind == Model.DiagnosticKind.ValueIgnored;
            Model.Diagnostics.Muted = false;

            try { return StyleApplier.Apply(new ComputedStyle(), property, value) && !complained; }
            catch { return false; }
            finally { Model.Diagnostics.Sink = sink; Model.Diagnostics.Muted = muted; }
        }

        // ------------------------------------------------------ shared condition grammar --

        /// <summary>
        /// `not X`, `X and Y`, `X or Y` and parenthesised groups - the grammar `@media` and `@supports` share.
        /// <paramref name="test"/> reads the inside of a plain `( ... )`, <paramref name="identifier"/> a bare word
        /// such as a media type. Either returning null makes the whole condition unreadable.
        /// </summary>
        private static MediaCondition ParseCondition(string s, ref int i,
                                                     Func<string, MediaCondition> test,
                                                     Func<string, MediaCondition> identifier)
        {
            MediaCondition left = ParsePrimary(s, ref i, test, identifier);
            if (left == null) return null;

            while (true)
            {
                int save = i;
                SkipWhitespace(s, ref i);
                string word = ReadWord(s, ref i);

                bool and = string.Equals(word, "and", StringComparison.OrdinalIgnoreCase);
                bool or = string.Equals(word, "or", StringComparison.OrdinalIgnoreCase);
                if (!and && !or) { i = save; return left; }

                MediaCondition right = ParsePrimary(s, ref i, test, identifier);
                if (right == null) return null;

                left = and ? MediaCondition.And(left, right) : MediaCondition.Or(left, right);
            }
        }

        private static MediaCondition ParsePrimary(string s, ref int i,
                                                   Func<string, MediaCondition> test,
                                                   Func<string, MediaCondition> identifier)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) return null;

            if (s[i] != '(')
            {
                int save = i;
                string word = ReadWord(s, ref i);
                if (word == null) return null;

                if (word.Equals("not", StringComparison.OrdinalIgnoreCase))
                    return MediaCondition.Not(ParsePrimary(s, ref i, test, identifier));

                // `selector(...)`, `font-tech(...)`: a function, not an identifier, and none of them is answerable.
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == '(') { i = save; return null; }

                return identifier(word);
            }

            int close = MatchingParen(s, i);
            if (close < 0) return null;

            string inside = s.Substring(i + 1, close - i - 1).Trim();
            i = close + 1;

            // `( ... )` may hold a condition rather than a test: `(not (a))`, `((a) and (b))`.
            if (inside.StartsWith("(", StringComparison.Ordinal) || StartsWithWord(inside, "not"))
            {
                int j = 0;
                MediaCondition nested = ParseCondition(inside, ref j, test, identifier);
                SkipWhitespace(inside, ref j);
                return j >= inside.Length ? nested : null;
            }

            return test(inside);
        }

        // --------------------------------------------------------------------- helpers --

        private static int SkipIdent(string s, int i)
        {
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '-' || s[i] == '_')) i++;
            return i;
        }

        /// <summary>Index of the closing quote of the string that opens at <paramref name="quote"/>, or the last
        /// character when it is never closed.</summary>
        private static int StringEnd(string s, int quote)
        {
            char delimiter = s[quote];
            for (int i = quote + 1; i < s.Length; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                if (s[i] == delimiter) return i;
            }
            return s.Length - 1;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        /// <summary>The identifier at <paramref name="i"/>, or null when there is none.</summary>
        private static string ReadWord(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            int start = i;
            while (i < s.Length && (char.IsLetter(s[i]) || s[i] == '-')) i++;
            return i > start ? s.Substring(start, i - start) : null;
        }

        private static bool StartsWithWord(string s, string word) =>
            s.StartsWith(word, StringComparison.OrdinalIgnoreCase)
            && (s.Length == word.Length || !char.IsLetterOrDigit(s[word.Length]));

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

                    // The `continue` on the bracket cases is the whole point. Without it a bracket adjusted the
                    // depth and then fell through to the split below, so EVERY parenthesis cut the list: the
                    // perfectly ordinary `.a:not(.b), .c` came out as three fragments, two of which are not
                    // selectors, and the DOM library then rejected the rule whole. Two shipped apps lost a rule
                    // to this and nobody noticed until the engine started naming what it rejects.
                    if (c == '(' || c == '[') { depth++; continue; }
                    if (c == ')' || c == ']') { depth--; continue; }
                    if (c != ',' || depth != 0) continue;
                }
                string s = list.Substring(start, i - start).Trim();
                start = i + 1;
                if (s.Length > 0) result.Add(s);
            }
            return result;
        }

        /// <summary>
        /// Comma-separated parts, with parentheses holding a part together.
        ///
        /// An at-rule prelude needs its own splitter rather than <see cref="SplitSelectorList"/>: a media query
        /// list nests whole conditions in parentheses, and everything between them belongs to one query.
        /// </summary>
        private static List<string> SplitCommas(string text)
        {
            var result = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i < text.Length)
                {
                    char c = text[i];
                    if (c == '(') { depth++; continue; }
                    if (c == ')') { depth--; continue; }
                    if (c != ',' || depth != 0) continue;
                }
                string part = text.Substring(start, i - start).Trim();
                start = i + 1;
                if (part.Length > 0) result.Add(part);
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
