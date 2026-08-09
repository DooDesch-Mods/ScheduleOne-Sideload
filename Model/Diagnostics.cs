namespace Sideload.Model
{
    // Unity-free, like everything under Css/, Dom/, Layout/ and Model/ - the headless test project compiles
    // these folders with no engine reference, and the corpus run over real Tailwind output needs exactly
    // this channel.

    /// <summary>
    /// The ways a declaration, a rule or a listener can end up doing nothing.
    ///
    /// All but the first are cases that USED to vanish without trace: the rule is valid CSS, a browser obeys it,
    /// and the page comes out different with nothing said anywhere. More than one shipped mod has been caught
    /// by exactly that.
    /// </summary>
    internal enum DiagnosticKind
    {
        /// <summary>The property has no case in the switch. Reported since 1.9.0.</summary>
        UnknownProperty,

        /// <summary>Property known, VALUE unreadable - `padding: 1rem`, `color: oklch(...)`, `width: calc(...)`.</summary>
        ValueRejected,

        /// <summary>Value read and then acted on by nothing - `align-items: baseline`, `position: relative`.</summary>
        ValueIgnored,

        /// <summary>The selector was refused by the DOM library; the whole rule is dropped.</summary>
        SelectorRejected,

        /// <summary>An at-rule block was skipped - `@media (min-width:)`, `@keyframes`, `@layer`.</summary>
        AtRuleSkipped,

        /// <summary>
        /// A `@media` block this engine read perfectly well and that no screen it draws on can ever satisfy.
        ///
        /// Its own kind rather than <see cref="AtRuleSkipped"/>, because the two ask different things of the
        /// author. "Skipped" says the engine fell short and there is nothing to do; this one says the breakpoint
        /// is wider than the phone will ever be, and the fix is a smaller number.
        /// </summary>
        MediaUnreachable,

        /// <summary>A listener on an event type this engine never delivers.</summary>
        DeadEventListener,
    }

    /// <summary>One report: what kind, about what, and the value that was lost with it.</summary>
    internal readonly struct Diagnostic
    {
        internal readonly DiagnosticKind Kind;

        /// <summary>Property name, selector, at-rule prelude or event type.</summary>
        internal readonly string Subject;

        /// <summary>The value that was dropped, or the reason. May be null.</summary>
        internal readonly string Detail;

        internal Diagnostic(DiagnosticKind kind, string subject, string detail)
        {
            Kind = kind;
            Subject = subject;
            Detail = detail;
        }

        /// <summary>
        /// How a receiver recognises something it has already seen - once per thing, not once per occurrence.
        ///
        /// A tuple rather than one composed string: the cascade runs per rule PER ELEMENT, so the same report
        /// comes up a hundred times on a page with a hundred rows. A string here would be an allocation per
        /// occurrence, and so GC pressure per frame, in the path that is already the most expensive one. The
        /// tuple is a struct and compares its strings ordinally.
        /// </summary>
        internal (DiagnosticKind Kind, string Subject, string Detail) Identity => (Kind, Subject, Detail);

        public override string ToString() => Kind switch
        {
            DiagnosticKind.UnknownProperty =>
                $"the CSS property '{Subject}' is not implemented - every rule using it is ignored.",
            DiagnosticKind.ValueRejected =>
                $"'{Subject}: {Detail}' - this engine cannot read that value, so the declaration is dropped.",
            // With no detail the property is dead whatever it is set to, and naming the value would repeat the
            // same sentence once per spelling. With a detail the value IS the information - `align-items` is
            // fine and `align-items: baseline` is not.
            DiagnosticKind.ValueIgnored when Detail == null =>
                $"'{Subject}' is read and then ignored - nothing happens, whatever you set it to.",
            DiagnosticKind.ValueIgnored =>
                $"'{Subject}: {Detail}' is read and then ignored - nothing happens.",
            DiagnosticKind.SelectorRejected =>
                $"the selector '{Subject}' was refused ({Detail}), so the whole rule is dropped.",
            DiagnosticKind.AtRuleSkipped =>
                $"'{Subject}' is skipped - everything inside that block does nothing.",
            DiagnosticKind.MediaUnreachable =>
                $"'{Subject}' can never match{(Detail == null ? "" : " - the viewport is " + Detail)}, "
                + "so everything inside it does nothing. The breakpoint is outside this screen.",
            DiagnosticKind.DeadEventListener =>
                $"a listener on '{Subject}' - Sideload never delivers that event, so the handler never runs.",
            _ => $"{Kind} {Subject} {Detail}",
        };
    }

    /// <summary>
    /// Where the engine reports what it threw away.
    ///
    /// <see cref="Sink"/> is null while nobody is listening - a report then costs a null check and nothing else
    /// that matters on the cascade path, which runs per rule and per element. The host hooks in at load,
    /// deduplicates and logs; the corpus run over a Tailwind build hooks in instead and counts.
    ///
    /// Everything runs on one thread (Unity's main thread, or the test run), hence no locking.
    /// </summary>
    internal static class Diagnostics
    {
        internal static Action<Diagnostic> Sink;

        /// <summary>Whether anyone is listening. Lets a caller skip work it would only do to build a message.</summary>
        internal static bool Listening => Sink != null && !Muted;

        /// <summary>
        /// Swallow reports for a moment. <see cref="Css.StyleApplier.Supports"/> runs the real switch with a
        /// placeholder value to ask about the NAME, and no word may be said about that value - otherwise every
        /// such question reports a fault nobody wrote.
        /// </summary>
        internal static bool Muted;

        internal static void Report(DiagnosticKind kind, string subject, string detail = null)
        {
            if (Muted) return;
            Sink?.Invoke(new Diagnostic(kind, subject, detail));
        }
    }
}
