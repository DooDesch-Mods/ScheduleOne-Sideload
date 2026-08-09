using Sideload.Model;

namespace Sideload.Css
{
    /// <summary>
    /// Reads a whole stylesheet once and reports every declaration in it this engine cannot use.
    ///
    /// The cascade already reports as it goes, but only for rules that MATCH something. That is the right rule for
    /// "what went wrong on this page" and the wrong one for "what can this engine not read": a stylesheet out of a
    /// build tool is mostly rules that match nothing on any one screen. Drop a Tailwind build into an app and the
    /// cascade would report almost nothing - not because the sheet is fine, but because nobody used those classes
    /// yet. The first author to try it would get silence, which is the failure this whole reporting exists to end.
    ///
    /// So this walks the parsed sheet directly and applies every declaration to a throwaway style. Same parser,
    /// same switch, same diagnostics - just without asking whether anything on screen wanted it. Duplicates with
    /// the cascade's own reports are expected and harmless: the receiver deduplicates.
    ///
    /// Also used by the corpus runner in the headless tests, which is why it lives here rather than in the host:
    /// two implementations of "what would this sheet lose" would be two answers to one question.
    /// </summary>
    internal static class SheetAudit
    {
        internal static void Scan(Stylesheet sheet)
        {
            if (sheet == null || !Diagnostics.Listening) return;

            Dictionary<string, string> variables = CollectVariables(sheet);
            var scratch = new ComputedStyle();

            foreach (StyleRule rule in sheet.Rules)
            {
                foreach (Declaration declaration in rule.Declarations)
                {
                    string property = declaration.Property;
                    if (string.IsNullOrEmpty(property)) continue;

                    // Custom properties are storage, not features - warning about them would shout at every
                    // stylesheet that defines a palette.
                    if (property.StartsWith("--", StringComparison.Ordinal)) continue;

                    string value = declaration.Value;
                    if (value != null && value.IndexOf("var(", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        value = StyleResolver.SubstituteVariables(value, variables, 0);

                        // Unresolvable makes the declaration invalid in CSS too. Not this engine's doing, and
                        // reporting it would blame the wrong thing.
                        if (value == null) continue;
                    }

                    // `inherit` is the cascade's business - it names the parent's value and there is no parent
                    // here. Asking the same question the cascade asks keeps the two answers the same: a property
                    // that can be carried down loses nothing, and one that cannot falls through and is reported.
                    if (StyleApplier.IsInheritKeyword(value) && StyleApplier.Inherit(scratch, null, property)) continue;

                    if (!StyleApplier.Apply(scratch, property, value))
                        Diagnostics.Report(DiagnosticKind.UnknownProperty, property);
                }
            }
        }

        /// <summary>
        /// Every custom property in the sheet in one table, regardless of which selector declared it.
        ///
        /// Coarser than the real cascade, which inherits per element. For the question this asks - can the engine
        /// READ what is in here - that is enough: a variable defined anywhere is not a missing unit. Without the
        /// substitution every `color: var(--ink)` would look like an unreadable value, and the report would be
        /// mostly about its own blind spot.
        /// </summary>
        private static Dictionary<string, string> CollectVariables(Stylesheet sheet)
        {
            var variables = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (StyleRule rule in sheet.Rules)
                foreach (Declaration declaration in rule.Declarations)
                    if (declaration.Property != null &&
                        declaration.Property.StartsWith("--", StringComparison.Ordinal))
                        variables[declaration.Property] = declaration.Value;

            return variables;
        }
    }
}
