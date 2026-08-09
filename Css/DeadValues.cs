using Sideload.Model;

namespace Sideload.Css
{
    /// <summary>
    /// Declarations this renderer reads without complaint and then does nothing with.
    ///
    /// These are the worst kind of gap, and the reason this file exists rather than a line in the register.
    /// An unknown property has been named in the log since 1.9.0; an unreadable value is named since the parse
    /// helpers in <see cref="StyleApplier"/> started reporting. But `position: sticky` passes both of those: the
    /// property is implemented, the value parses into the enum, and the layout then has no branch for it. The
    /// author sees a correct-looking rule, a browser that honours it, and a game that does not - with nothing
    /// anywhere to say which of the three is lying. (`align-items: baseline` was the founding case here and is
    /// implemented as of 1.19; the two-word forms of it are still listed.)
    ///
    /// ENGINE-GAPS.md asked for exactly this: "a value that parses but is not implemented is a second silent
    /// class. Either the layout implements them, or the applier should refuse the value so it falls back visibly."
    /// Refusing would change rendering, so this reports instead - the visible fallback without the behaviour change.
    ///
    /// Not on the list on purpose: `display: block` and `inline-block`. They ARE mapped onto flex rather than
    /// implemented, but a column flex container is the closest thing this engine has and the result is usually what
    /// the author wanted. Reporting them would put a line in every app's log that nobody can act on, and a report
    /// people learn to skip is worse than no report.
    /// </summary>
    internal static class DeadValues
    {
        /// <summary>Called for every declaration before the switch runs. Cheap: one switch on the property name,
        /// and for all but a handful of names that is the whole cost.</summary>
        internal static void Check(string property, string value)
        {
            if (!Diagnostics.Listening) return;

            switch (property)
            {
                case "display":
                    // Everything the engine has no box model for. It keeps whatever display it already had,
                    // which is a column flex container - so `table` quietly becomes a vertical stack.
                    if (IsAny(value, "inline", "inline-flex", "contents",
                                     "table", "table-row", "table-cell", "table-row-group", "list-item", "flow-root"))
                        Report(property, value);
                    break;

                case "position":
                    // `sticky` keeps whatever position it already had - so a header written to stay put scrolls
                    // away with the rest of the list and nothing anywhere says so.
                    if (IsAny(value, "sticky")) Report(property, value);
                    break;

                case "align-items":
                case "align-self":
                    // `baseline` is implemented as of 1.19 and is NOT on this list any more. The two-word forms
                    // still are: `first baseline` is what plain `baseline` already does, and `last baseline`
                    // hangs the line from the LAST baseline in each item, which needs a second metric per box.
                    // Both parse into AlignKind.Baseline and quietly become the first-baseline behaviour.
                    if (IsAny(value, "first baseline", "last baseline")) Report(property, value);
                    break;

                case "transition-property":
                    // Read and discarded: there is one duration for the whole box and every animatable value
                    // rides along, so naming a property changes nothing.
                    ReportProperty(property);
                    break;

                case "overflow-x":
                    // Only overflow-y ever builds a scroll area. On the x axis `auto` and `scroll` clip like
                    // `hidden` and nothing scrolls.
                    if (IsAny(value, "auto", "scroll")) Report(property, value);
                    break;

                case "white-space":
                    // Any unrecognised value falls back to Normal - for `pre-line` that loses exactly the line
                    // breaks it was written to keep.
                    if (IsAny(value, "pre-line")) Report(property, value);
                    break;

                case "text-align":
                    if (IsAny(value, "justify")) Report(property, value);
                    break;


                case "box-shadow":
                    // An `inset` keyword drops the entire declaration; a fourth length (spread) is eaten; a
                    // comma list is merged into one shadow. All three are how focus rings and elevation are
                    // normally written.
                    if (Contains(value, "inset")) Report(property, value + "  (inset drops the whole declaration)");
                    else if (ValueParser.SplitTopLevel(value, commaSeparated: true).Length > 1)
                        Report(property, value + "  (only the first shadow is drawn)");
                    break;

                case "transform":
                    if (ContainsAny(value, "skew", "matrix", "perspective", "rotate3d", "rotatex", "rotatey",
                                           "rotatez", "translate3d", "translatez", "scale3d", "scalez"))
                        Report(property, value + "  (this engine does not know that function)");
                    else if (Contains(value, "translate") && Contains(value, "%"))
                        Report(property, value + "  (a percentage resolves against zero, so translate(-50%) becomes 0)");
                    break;

                case "font-family":
                    // Only the first family is kept; the rest of the stack is never tried.
                    if (Contains(value, ",")) Report(property, value + "  (only the first family counts)");
                    break;

                // --- grid: the parts of it that are their own feature ----------------------------------
                //
                // Grid itself is implemented. These four are not, and each one is a second placement or sizing
                // model rather than a variation on the one that is: named areas come with a name table and a
                // string syntax, subgrid inherits a parent's tracks, dense packing is a different placement
                // algorithm, and masonry is not a grid at all. None appears in a Tailwind utility.

                case "grid-template-areas":
                    // The case in the switch is `break;` - the property is read and nothing is placed by it.
                    ReportProperty(property);
                    break;

                case "grid-auto-flow":
                    // Row-major is the only flow there is here. `column` transposes the placement pass and
                    // `dense` backfills holes; both come out as plain row flow instead.
                    if (ContainsAny(value, "column", "dense")) Report(property, value);
                    break;

                case "grid-template-columns":
                case "grid-template-rows":
                case "grid-template":
                    if (ContainsAny(value, "subgrid", "masonry")) Report(property, value);
                    else if (Contains(value, "\"") || Contains(value, "'"))
                        Report(property, value + "  (named areas are not placed by)");
                    else if (GridParser.NamesLines(value)) Report(property, value + "  (the line names do nothing)");
                    break;

                case "grid-column":
                case "grid-row":
                case "grid-column-start":
                case "grid-column-end":
                case "grid-row-start":
                case "grid-row-end":
                case "grid-area":
                    // A named line or area. Numbers and `span` place fine; a name has nothing to resolve against.
                    if (GridParser.NamesAnArea(value)) Report(property, value);
                    break;
            }
        }

        /// <summary>This VALUE is ignored, and naming it is the point - the property itself is fine.</summary>
        private static void Report(string property, string value) =>
            Diagnostics.Report(DiagnosticKind.ValueIgnored, property, value);

        /// <summary>
        /// The whole property is ignored, whatever it is set to.
        ///
        /// No value, so the receiver deduplicates all of them into one line. Without this a stylesheet that sets
        /// eight different line heights reported eight times, and the report is only read while it is short.
        /// </summary>
        private static void ReportProperty(string property) =>
            Diagnostics.Report(DiagnosticKind.ValueIgnored, property);

        private static bool IsAny(string value, params string[] candidates)
        {
            foreach (string candidate in candidates)
                if (value.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool Contains(string value, string needle) =>
            value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool ContainsAny(string value, params string[] needles)
        {
            foreach (string needle in needles)
                if (Contains(value, needle)) return true;
            return false;
        }
    }
}
