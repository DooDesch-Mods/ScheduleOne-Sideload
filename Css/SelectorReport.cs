namespace Sideload.Css
{
    /// <summary>
    /// One place decides whether a refused selector is worth telling anyone about.
    ///
    /// The DOM library refuses a selector for two very different reasons, and only one of them costs the page
    /// anything. A selector it does not understand - `.a:has(.b)`, a nesting form it has not caught up with - is a
    /// real loss: the rule was meant for something on this page and never arrives. A selector that names a
    /// browser's own internals is not: `::-webkit-datetime-edit-minute-field` describes a part of a native date
    /// picker, and there is no native date picker here to lose. Every preflight sheet carries dozens of the second
    /// kind - Tailwind's carries twenty-seven - and reported wholesale they bury the first kind entirely.
    ///
    /// The test is the SHAPE of the name rather than a list of the ones seen so far, for the same reason
    /// <c>StyleApplier.Supports</c> asks the real switch: a list goes stale the moment a browser ships another
    /// shadow part, and nobody notices because the symptom is noise rather than a failure.
    /// </summary>
    internal static class SelectorReport
    {
        /// <summary>Report a refused selector, unless it names something no page here could have had.</summary>
        internal static void Rejected(string selector, string reason)
        {
            if (CannotExistHere(selector)) return;
            Model.Diagnostics.Report(Model.DiagnosticKind.SelectorRejected, selector, reason);
        }

        /// <summary>
        /// Whether the selector reaches for something this renderer has no counterpart to at all.
        ///
        /// Vendor-prefixed pseudo-elements and pseudo-classes are the bulk of it: they are, by definition, one
        /// browser's own construction. The rest are named because they are cheap to be sure about - a shadow tree,
        /// a dialog's backdrop, a file input's button and a video's cue are all things a Sideload page cannot
        /// contain, whatever it does.
        ///
        /// Deliberately NOT here: `::placeholder`, `::marker`, `::selection`, `::first-line`. Each of those names
        /// something this engine does have or could have, so a rule that wanted one really did lose.
        /// </summary>
        internal static bool CannotExistHere(string selector)
        {
            if (string.IsNullOrEmpty(selector)) return false;

            string lower = selector.ToLowerInvariant();

            // A vendor prefix on a pseudo-class or pseudo-element, in either spelling.
            if (lower.Contains(":-webkit-") || lower.Contains(":-moz-") || lower.Contains(":-ms-")
                || lower.Contains(":-o-") || lower.Contains(":-internal-"))
                return true;

            return lower.Contains(":host")
                || lower.Contains("::slotted")
                || lower.Contains("::part(")
                || lower.Contains("::backdrop")
                || lower.Contains("::file-selector-button")
                || lower.Contains("::cue")
                || lower.Contains("::view-transition")
                || lower.Contains("::target-text");
        }
    }
}
