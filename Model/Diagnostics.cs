namespace Sideload.Model
{
    // Unity-frei, wie alles unter Css/, Dom/, Layout/ und Model/ - das headless Testprojekt kompiliert
    // diese Ordner ohne Engine-Referenz, und der Messlauf ueber echten Tailwind-Output braucht genau
    // diesen Kanal.

    /// <summary>
    /// Die Arten, auf die eine Deklaration, eine Regel oder ein Listener wirkungslos bleiben kann.
    ///
    /// Bis auf die erste sind das alles Faelle, die BISHER spurlos verschwanden: die Regel ist gueltiges
    /// CSS, ein Browser befolgt sie, und die Seite kommt anders heraus, ohne dass irgendwo etwas steht.
    /// Genau daran ist schon mehr als eine ausgelieferte Mod haengengeblieben.
    /// </summary>
    internal enum DiagnosticKind
    {
        /// <summary>Die Property hat keinen Fall im Switch. Das meldet die Engine seit 1.9.0.</summary>
        UnknownProperty,

        /// <summary>Property bekannt, WERT unlesbar - `padding: 1rem`, `color: oklch(...)`, `width: calc(...)`.</summary>
        ValueRejected,

        /// <summary>Wert gelesen und dann folgenlos - `align-items: baseline`, `position: relative`.</summary>
        ValueIgnored,

        /// <summary>Der Selektor kam bei der DOM-Bibliothek nicht durch; die ganze Regel faellt weg.</summary>
        SelectorRejected,

        /// <summary>Ein At-Rule-Block wurde uebersprungen - `@media (min-width:)`, `@keyframes`, `@layer`.</summary>
        AtRuleSkipped,

        /// <summary>Ein Listener auf einen Ereignistyp, den diese Engine nie zustellt.</summary>
        DeadEventListener,
    }

    /// <summary>Eine Meldung: was fuer eine Art, worueber, und der Wert, der dabei verloren ging.</summary>
    internal readonly struct Diagnostic
    {
        internal readonly DiagnosticKind Kind;

        /// <summary>Property-Name, Selektor, At-Rule-Prelude oder Ereignistyp.</summary>
        internal readonly string Subject;

        /// <summary>Der verworfene Wert, oder warum. Kann null sein.</summary>
        internal readonly string Detail;

        internal Diagnostic(DiagnosticKind kind, string subject, string detail)
        {
            Kind = kind;
            Subject = subject;
            Detail = detail;
        }

        /// <summary>
        /// Woran ein Empfaenger erkennt, dass er das schon kennt - einmal pro Sache, nicht pro Vorkommen.
        ///
        /// Ein Tupel und kein zusammengesetzter String: die Kaskade laeuft pro Regel PRO ELEMENT, also faellt
        /// dieselbe Meldung auf einer Seite mit hundert Zeilen hundertmal an. Ein String hier waere eine
        /// Allokation je Anfall und damit GC-Druck pro Frame - genau in dem Pfad, der ohnehin der teuerste ist.
        /// Das Tupel ist ein Struct und vergleicht seine Strings ordinal.
        /// </summary>
        internal (DiagnosticKind Kind, string Subject, string Detail) Identity => (Kind, Subject, Detail);

        public override string ToString() => Kind switch
        {
            DiagnosticKind.UnknownProperty =>
                $"CSS-Property '{Subject}' ist nicht implementiert, jede Regel damit wird ignoriert.",
            DiagnosticKind.ValueRejected =>
                $"'{Subject}: {Detail}' - der Wert ist fuer diese Engine unlesbar, die Deklaration faellt weg.",
            DiagnosticKind.ValueIgnored =>
                $"'{Subject}: {Detail}' wird gelesen und dann ignoriert - es passiert nichts.",
            DiagnosticKind.SelectorRejected =>
                $"Selektor '{Subject}' wurde abgelehnt ({Detail}), die ganze Regel faellt weg.",
            DiagnosticKind.AtRuleSkipped =>
                $"'{Subject}' wird uebersprungen - der ganze Block darin ist wirkungslos.",
            DiagnosticKind.DeadEventListener =>
                $"Listener auf '{Subject}' - dieses Ereignis stellt Sideload nie zu, der Handler laeuft nie.",
            _ => $"{Kind} {Subject} {Detail}",
        };
    }

    /// <summary>
    /// Wohin die Engine meldet, was sie verworfen hat.
    ///
    /// <see cref="Sink"/> ist null, solange niemand zuhoert - dann kostet eine Meldung einen Nullcheck und
    /// sonst nichts, was auf dem Kaskadenpfad zaehlt: der laeuft pro Regel und pro Element. Der Host haengt
    /// sich beim Laden ein, dedupliziert und loggt; der Messlauf ueber einen Tailwind-Build haengt sich
    /// stattdessen ein und zaehlt.
    ///
    /// Alles laeuft auf einem Thread (Unitys Hauptthread bzw. der Testlauf), deshalb kein Sperren.
    /// </summary>
    internal static class Diagnostics
    {
        internal static Action<Diagnostic> Sink;

        /// <summary>Whether anyone is listening. Lets a caller skip work it would only do to build a message.</summary>
        internal static bool Listening => Sink != null && !Muted;

        /// <summary>
        /// Meldungen voruebergehend verschlucken. <see cref="Css.StyleApplier.Supports"/> laesst den echten
        /// Switch mit einem Platzhalterwert laufen, um nach dem NAMEN zu fragen - dabei darf kein Wort
        /// ueber den Wert fallen, sonst meldet jede Abfrage einen Fehler, den niemand geschrieben hat.
        /// </summary>
        internal static bool Muted;

        internal static void Report(DiagnosticKind kind, string subject, string detail = null)
        {
            if (Muted) return;
            Sink?.Invoke(new Diagnostic(kind, subject, detail));
        }
    }
}
