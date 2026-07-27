using AngleSharp.Dom;
using Sideload.Css;
using Sideload.Host;

namespace Sideload.Devtools.Cdp
{
    /// <summary>
    /// The CSS domain: the Styles and Computed panes.
    ///
    /// Both are answered from the page's real cascade, not from a re-implementation of it. Which rules matched is
    /// asked of the same <see cref="StyleRule"/> list the renderer uses, in the same orientation and the same
    /// interaction state; the computed values come from <see cref="StyleResolver"/> itself. The one thing that has to
    /// be manufactured is stylesheet TEXT, because the parser keeps none - see <see cref="SheetModel"/>.
    ///
    /// Rules are handed over weakest first. The protocol expects cascade order and the frontend reverses it for
    /// display and works out its own strikethrough, so ordering them the way the cascade does is the whole job.
    /// </summary>
    internal static class CssDomain
    {
        internal static string Enable(CdpSession session)
        {
            session.CssEnabled = true;

            // The frontend has to know a stylesheet exists before any rule can name it. A page that has not been
            // built yet simply has none; the reload check announces it when it appears.
            string header = HeaderJson(session);
            if (header != null) session.EmitAfterReply("CSS.styleSheetAdded", header);

            return Json.EmptyObject;
        }

        internal static string GetMatchedStylesForNode(CdpSession session, JsonValue args)
        {
            IElement element = DomDomain.ElementOf(session, args);
            WebView view = DomDomain.ViewOf(session);
            SheetModel model = ModelOf(session);

            return new Json.Obj()
                .Raw("inlineStyle", InlineJson(element))
                .Raw("matchedCSSRules", MatchedJson(model, view, element))
                .Raw("pseudoElements", "[]")
                .Raw("inherited", InheritedJson(model, view, element))
                .Raw("cssKeyframesRules", "[]")
                .Done();
        }

        internal static string GetInlineStylesForNode(CdpSession session, JsonValue args)
        {
            IElement element = DomDomain.ElementOf(session, args);

            return new Json.Obj()
                .Raw("inlineStyle", InlineJson(element))
                .Done();
        }

        /// <summary>
        /// The `:hov` toggles in the Styles pane. Pins a pseudo-class on an element so its `:hover` or `:active`
        /// rules apply without the pointer being there - the only way to style a state you cannot hold still.
        ///
        /// The pin is additive: the pointer keeps working underneath, so clearing a toggle leaves the element in the
        /// state it would have been in anyway.
        /// </summary>
        internal static string ForcePseudoState(CdpSession session, JsonValue args)
        {
            IElement element = DomDomain.ElementOf(session, args);
            WebView view = DomDomain.ViewOf(session);

            var names = new List<string>();
            JsonValue list = args?["forcedPseudoClasses"];
            if (list != null)
                for (int i = 0; i < list.Count; i++) names.Add(list[i]?.AsString() ?? "");

            view.ForcePseudoState(element, names);
            return "{}";
        }

        internal static string GetComputedStyleForNode(CdpSession session, JsonValue args)
        {
            IElement element = DomDomain.ElementOf(session, args);
            WebView view = DomDomain.ViewOf(session);

            // The whole document is resolved, because that is the only way the cascade runs: inherited properties
            // need every ancestor's result. Once per selection in the panel, against a page the renderer resolves
            // every rebuild anyway.
            Dictionary<IElement, ComputedStyle> styles =
                StyleResolver.Resolve(view.Document, view.Sheet, view.StyleContext);

            var rows = new List<string>();
            if (styles.TryGetValue(element, out ComputedStyle style))
                foreach (KeyValuePair<string, string> property in ComputedCss.Describe(style))
                    rows.Add(new Json.Obj().Str("name", property.Key).Str("value", property.Value).Done());

            return new Json.Obj().Raw("computedStyle", Json.Array(rows)).Done();
        }

        internal static string GetStyleSheetText(CdpSession session, JsonValue args)
        {
            SheetModel model = ModelOf(session);
            string id = args["styleSheetId"].AsString();

            if (model == null || !string.Equals(model.Id, id, StringComparison.Ordinal))
                throw new CdpException(CdpException.InvalidParams,
                    "no stylesheet with id " + id + " - the page may have been reloaded");

            return new Json.Obj().Str("text", model.Text).Done();
        }

        /// <summary>The `CSS.styleSheetAdded` payload for this page's sheet, or null when it has none yet. Used both
        /// on enable and when a reload replaces the sheet.</summary>
        internal static string HeaderJson(CdpSession session)
        {
            SheetModel model = ModelOf(session);
            if (model == null) return null;

            WebView view = Targets.Find(session.TargetId);

            return new Json.Obj()
                .Raw("header", new Json.Obj()
                    .Str("styleSheetId", model.Id)
                    .Str("frameId", Targets.FrameOf(session.TargetId))
                    // One sheet per page: every <link> and <style> the document pulled in is parsed into a single
                    // rule list, so there is nothing finer to point at than the page itself.
                    .Str("sourceURL", Targets.OriginOf(view) + "/styles.css")
                    .Str("origin", "regular")
                    .Str("title", "")
                    .Bool("disabled", false)
                    .Bool("isInline", false)
                    .Bool("isMutable", true)
                    .Bool("isConstructed", false)
                    .Num("startLine", 0)
                    .Num("startColumn", 0)
                    .Num("length", model.Text.Length)
                    .Num("endLine", model.LineCount)
                    .Num("endColumn", 0)
                    .Done())
                .Done();
        }

        /// <summary>
        /// Edit a rule's declarations from the Styles pane.
        ///
        /// The edit lands on the very declaration objects the cascade reads, so it takes effect for every element the
        /// rule matches - which is what editing a stylesheet means, as opposed to editing one element. The page is
        /// then marked dirty through the same path a script mutation uses, so the change is on screen on the next
        /// frame.
        /// </summary>
        internal static string SetStyleTexts(CdpSession session, JsonValue args)
        {
            SheetModel model = ModelOf(session)
                ?? throw new CdpException(CdpException.ServerError, "the page has no stylesheet yet");

            JsonValue edits = args["edits"];
            var targets = new List<SourceRule>();

            // Applied in two passes: nothing is changed until every edit has been understood, so a bad one in a batch
            // cannot leave half the stylesheet rewritten.
            var parsed = new List<List<Declaration>>();

            for (int i = 0; i < edits.Count; i++)
            {
                JsonValue edit = edits[i];

                if (!string.Equals(edit["styleSheetId"].AsString(), model.Id, StringComparison.Ordinal))
                    throw new CdpException(CdpException.InvalidParams, "that edit is for a stylesheet this page no longer has");

                SourceRule rule = RuleAt(model, edit["range"])
                    ?? throw new CdpException(CdpException.InvalidParams, "no rule at that range - the stylesheet has moved on");

                string text = edit["text"].AsString();
                List<Declaration> declarations = CssParser.ParseDeclarations(text);

                // Text that says something but parses to nothing is a typo, and the frontend wants to hear so: it
                // puts the old value back rather than silently emptying the rule.
                if (declarations.Count == 0 && !string.IsNullOrWhiteSpace(text))
                    throw new CdpException(CdpException.InvalidParams, "that declaration could not be parsed");

                targets.Add(rule);
                parsed.Add(declarations);
            }

            for (int i = 0; i < targets.Count; i++)
            {
                // In place, because every selector the parser split off this block shares this very list.
                targets[i].Declarations.Clear();
                targets[i].Declarations.AddRange(parsed[i]);
            }

            DomDomain.ViewOf(session).MarkDirty();

            // The text is now different, so every range in it moved. Rebuilt under the same id, because it is still
            // the same stylesheet - a new id would invalidate the panel's own references to it.
            SheetModel updated = Rebuild(session, model.Id);
            session.EmitAfterReply("CSS.styleSheetChanged",
                new Json.Obj().Str("styleSheetId", updated.Id).Done());

            var styles = new List<string>();
            foreach (SourceRule rule in targets)
            {
                SourceRule current = updated.Rules.Find(r => ReferenceEquals(r.Declarations, rule.Declarations));
                styles.Add(current == null ? "{}" : StyleJson(updated, current));
            }

            return new Json.Obj().Raw("styles", Json.Array(styles)).Done();
        }

        /// <summary>The rule whose declaration block starts where an edit says it does. The frontend echoes back a
        /// range this server handed it, so matching on the start is exact rather than a search.</summary>
        private static SourceRule RuleAt(SheetModel model, JsonValue range)
        {
            int line = range["startLine"].AsInt(-1);
            int column = range["startColumn"].AsInt(-1);

            foreach (SourceRule rule in model.Rules)
                if (rule.BodySpan.StartLine == line && rule.BodySpan.StartColumn == column) return rule;

            return null;
        }

        /// <summary>Drop the cached sheet so the next request rebuilds it. Called when the page is reloaded and the
        /// rule list it was built from is gone.</summary>
        internal static void Forget(CdpSession session)
        {
            session.Sheet = null;
        }

        private static SheetModel Rebuild(CdpSession session, string id)
        {
            session.Sheet = SheetModel.Build(Targets.Find(session.TargetId)?.Sheet, id);
            return session.Sheet;
        }

        // ------------------------------------------------------------------ matching --

        private sealed class Match
        {
            internal SourceRule Rule;
            internal List<int> Selectors;
            internal int A, B, C;
            internal int Order = int.MaxValue;
        }

        private static string MatchedJson(SheetModel model, WebView view, IElement element)
        {
            var matches = new List<Match>();
            if (model == null || element == null) return Json.Array(new List<string>());

            StyleContext context = view.StyleContext;
            Orientation orientation = context?.Orientation ?? Orientation.Landscape;
            StateFlags state = context?.StateOf != null ? context.StateOf(element) : StateFlags.None;

            foreach (SourceRule rule in model.Rules)
            {
                Match match = null;

                for (int i = 0; i < rule.Variants.Count; i++)
                {
                    StyleRule variant = rule.Variants[i];
                    if (!Applies(element, variant, orientation, state)) continue;

                    match ??= new Match { Rule = rule, Selectors = new List<int>() };
                    match.Selectors.Add(i);

                    // The strongest matching selector decides where the rule sits, which is how a selector list
                    // behaves: `.a, #b` is as strong as whichever half did the matching.
                    if (Weaker(match.A, match.B, match.C, variant))
                    {
                        match.A = variant.SpecificityA;
                        match.B = variant.SpecificityB;
                        match.C = variant.SpecificityC;
                    }

                    if (variant.Order < match.Order) match.Order = variant.Order;
                }

                if (match != null) matches.Add(match);
            }

            matches.Sort(Compare);

            var encoded = new List<string>();
            foreach (Match match in matches)
            {
                var selectors = new List<string>();
                foreach (int index in match.Selectors) selectors.Add(Json.Number(index));

                encoded.Add(new Json.Obj()
                    .Raw("rule", RuleJson(model, match.Rule))
                    .Raw("matchingSelectors", Json.Array(selectors))
                    .Done());
            }

            return Json.Array(encoded);
        }

        /// <summary>
        /// Everything the element inherits, nearest ancestor first.
        ///
        /// Worth the extra walk here because in this engine every text property is inherited: without it, a font size
        /// coming from a rule three levels up looks like it comes from nowhere.
        /// </summary>
        private static string InheritedJson(SheetModel model, WebView view, IElement element)
        {
            var entries = new List<string>();

            for (IElement ancestor = element?.ParentElement; ancestor != null; ancestor = ancestor.ParentElement)
            {
                entries.Add(new Json.Obj()
                    .Raw("inlineStyle", InlineJson(ancestor))
                    .Raw("matchedCSSRules", MatchedJson(model, view, ancestor))
                    .Done());
            }

            return Json.Array(entries);
        }

        /// <summary>
        /// Does this rule apply to this element right now.
        ///
        /// State is the interesting part: a `:hover` rule is reported only while the element actually hovers, because
        /// that is what the page is wearing. The frontend's force-state toggles cannot change that - they would need
        /// </summary>
        private static bool Applies(IElement element, StyleRule rule, Orientation orientation, StateFlags state)
        {
            if (rule.Media.HasValue && rule.Media.Value != orientation) return false;
            if ((rule.RequiredStates & state) != rule.RequiredStates) return false;

            try { return element.Matches(rule.BaseSelector); }
            catch { return false; }   // a selector the DOM library rejects never matched during rendering either
        }

        private static bool Weaker(int a, int b, int c, StyleRule candidate)
        {
            if (candidate.SpecificityA != a) return candidate.SpecificityA > a;
            if (candidate.SpecificityB != b) return candidate.SpecificityB > b;
            return candidate.SpecificityC > c;
        }

        /// <summary>Ascending cascade order, the same comparison the resolver makes: specificity, then document
        /// order. The frontend reverses this to put the winner on top.</summary>
        private static int Compare(Match x, Match y)
        {
            if (x.A != y.A) return x.A - y.A;
            if (x.B != y.B) return x.B - y.B;
            if (x.C != y.C) return x.C - y.C;
            return x.Order.CompareTo(y.Order);
        }

        // ------------------------------------------------------------------ shapes --

        private static string RuleJson(SheetModel model, SourceRule rule)
        {
            var selectors = new List<string>();
            for (int i = 0; i < rule.Variants.Count; i++)
            {
                selectors.Add(new Json.Obj()
                    .Str("text", rule.Variants[i].Selector ?? "")
                    .Raw("range", i < rule.SelectorSpans.Count ? rule.SelectorSpans[i].ToJson() : null)
                    .Done());
            }

            var encoded = new Json.Obj()
                .Str("styleSheetId", model.Id)
                .Raw("selectorList", new Json.Obj()
                    .Raw("selectors", Json.Array(selectors))
                    .Str("text", rule.SelectorText)
                    .Done())
                .Str("origin", "regular")
                .Raw("style", StyleJson(model, rule));

            if (rule.Media.HasValue)
            {
                string media = new Json.Obj()
                    .Str("text", "(orientation: " + (rule.Media.Value == Orientation.Portrait ? "portrait" : "landscape") + ")")
                    .Str("source", "mediaRule")
                    .Done();
                encoded.Raw("media", Json.Array(new[] { media }));
            }

            return encoded.Done();
        }

        private static string StyleJson(SheetModel model, SourceRule rule)
        {
            var properties = new List<string>();
            List<Declaration> declarations = rule.Declarations ?? new List<Declaration>();

            for (int i = 0; i < declarations.Count; i++)
            {
                SourceSpan? span = i < rule.DeclarationSpans.Count ? rule.DeclarationSpans[i] : (SourceSpan?)null;
                properties.Add(PropertyJson(declarations[i], span));
            }

            return new Json.Obj()
                .Str("styleSheetId", model.Id)
                .Raw("cssProperties", Json.Array(properties))
                .Raw("shorthandEntries", "[]")
                .Str("cssText", model.Slice(rule.BodySpan))
                .Raw("range", rule.BodySpan.ToJson())
                .Done();
        }

        private static string PropertyJson(Declaration declaration, SourceSpan? span)
        {
            var encoded = new Json.Obj()
                .Str("name", declaration.Property)
                .Str("value", declaration.Value)
                .Bool("implicit", false)
                .Str("text", SheetModel.DeclarationText(declaration))
                .Bool("parsedOk", true)
                .Bool("disabled", false);

            if (declaration.Important) encoded.Bool("important", true);
            if (span.HasValue) encoded.Raw("range", span.Value.ToJson());

            return encoded.Done();
        }

        /// <summary>
        /// The element's `style` attribute, which the cascade already treats as the highest-priority source.
        ///
        /// No styleSheetId and no range: there is no stylesheet these live in, so the frontend shows them read-only.
        /// Editing an inline style is what the console is for here (`el.style.color = ...`), and that path already
        /// marks the page dirty.
        /// </summary>
        private static string InlineJson(IElement element)
        {
            string inline = element?.GetAttribute("style");
            var properties = new List<string>();

            if (!string.IsNullOrWhiteSpace(inline))
                foreach (Declaration declaration in CssParser.ParseDeclarations(inline))
                    properties.Add(PropertyJson(declaration, null));

            return new Json.Obj()
                .Raw("cssProperties", Json.Array(properties))
                .Raw("shorthandEntries", "[]")
                .Str("cssText", inline ?? "")
                .Done();
        }

        /// <summary>
        /// The page's sheet, serialised and cached for this session. Rebuilt when the page hands out a different
        /// rule list, which is what a reload from disk does; the id changes with it, so a stale id from the previous
        /// document is rejected rather than silently answered with the new text.
        /// </summary>
        private static SheetModel ModelOf(CdpSession session)
        {
            Stylesheet sheet = Targets.Find(session.TargetId)?.Sheet;
            if (sheet == null) return null;

            if (session.Sheet == null || !ReferenceEquals(session.Sheet.Sheet, sheet))
                session.Sheet = SheetModel.Build(sheet, session.TargetId + "-sheet-" + ++session.SheetGeneration);

            return session.Sheet;
        }
    }
}
