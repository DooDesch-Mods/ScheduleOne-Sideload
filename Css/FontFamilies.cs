namespace Sideload.Css
{
    /// <summary>
    /// The family names this renderer can actually honour.
    ///
    /// It exists so the cascade can pick a family out of a STACK the way a browser does - first name that resolves
    /// wins - rather than taking the first name written and falling back from there. The difference is the whole of
    /// what a font stack is for: `font-family: Inter, game-comic` should reach the comic face, and taking `Inter`
    /// and failing over to the default reaches the wrong one.
    ///
    /// Kept next to the cascade rather than beside the font loading, because <see cref="Sideload.Paint.TextSupport"/>
    /// is Unity-facing and this has to be answerable without a game. TextSupport's switch is the authority on what
    /// each name LOOKS like; this is the list of names it answers to, and the test project holds the two together.
    /// </summary>
    internal static class FontFamilies
    {
        /// <summary>What an unnamed, unknown or generic family resolves to: the game's own UI face.</summary>
        internal const string Default = "game-ui";

        private static readonly HashSet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "game-ui", "game-hand", "game-comic", "game-pixel", "game-segment", "monospace", "ui-monospace",
        };

        internal static bool Has(string family) =>
            !string.IsNullOrWhiteSpace(family) && Known.Contains(family.Trim().Trim('"', '\''));

        /// <summary>
        /// The first family in the stack this engine has, or the default.
        ///
        /// Nothing is reported when no name in the stack is known: every unknown family lands on the same face, so
        /// a stack of five web fonts and a stack of one lose exactly the same thing - which is the UI font either
        /// way, and that is what a sheet asking for a system sans-serif wanted.
        /// </summary>
        internal static string Resolve(string stack)
        {
            if (string.IsNullOrWhiteSpace(stack)) return Default;

            foreach (string entry in ValueParser.SplitTopLevel(stack, commaSeparated: true))
            {
                string name = entry.Trim().Trim('"', '\'');
                if (Has(name)) return name;
            }

            return Default;
        }
    }
}
