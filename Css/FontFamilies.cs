using Sideload.Model;

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
    /// Two kinds of name resolve: the built-ins below, and every family the page itself declared with
    /// <c>@font-face</c>. A page family resolves to a <see cref="FaceKey"/> that carries the page's font scope, so
    /// two apps that both call their face "Display" never draw each other's file.
    ///
    /// Kept next to the cascade rather than beside the font loading, because <see cref="Sideload.Paint.TextSupport"/>
    /// is Unity-facing and this has to be answerable without a game. TextSupport's switch is the authority on what
    /// each name LOOKS like; this is the list of names it answers to, and the test project holds the two together.
    /// </summary>
    internal static class FontFamilies
    {
        /// <summary>What an unnamed, unknown or generic family resolves to: the game's own UI face.</summary>
        internal const string Default = "game-ui";

        /// <summary>The prefix that marks a resolved family as a page's own <c>@font-face</c>.</summary>
        internal const string FacePrefix = "face:";

        private static readonly HashSet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "game-ui", "game-hand", "game-comic", "game-pixel", "game-segment", "monospace", "ui-monospace",
        };

        /// <summary>
        /// Generic and system names a web stylesheet writes as the tail of a stack. Each lands on the UI face, which
        /// is what the author meant by them, so falling back from one says nothing worth a warning.
        /// </summary>
        private static readonly HashSet<string> Generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sans-serif", "serif", "system-ui", "ui-sans-serif", "ui-serif", "ui-rounded", "cursive", "fantasy",
            "emoji", "math", "fangsong", "-apple-system", "BlinkMacSystemFont", "inherit", "initial", "unset",
            "revert", "revert-layer", "Apple Color Emoji", "Segoe UI Emoji", "Segoe UI Symbol", "Noto Color Emoji",
        };

        /// <summary>Families already reported as undrawable. Once per family for the session: the cascade asks per
        /// element and per rebuild, and a warning per ask would be a warning per frame.</summary>
        private static readonly HashSet<string> _warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The faces of the page being styled right now, or null outside a page.
        ///
        /// Static for the same reason <see cref="StyleApplier.Context"/> is: the cascade runs on one thread and
        /// never re-enters. <see cref="Enter"/> sets it for the length of one resolve and hands back what was there.
        /// </summary>
        internal static PageFonts Page;

        internal static PageFonts Enter(Stylesheet sheet, string scope)
        {
            PageFonts previous = Page;
            Page = sheet != null && sheet.FontFaces.Count > 0 ? new PageFonts(scope, sheet.FontFaces) : null;
            return previous;
        }

        internal static bool Has(string family) =>
            !string.IsNullOrWhiteSpace(family) && Known.Contains(family.Trim().Trim('"', '\''));

        /// <summary>The resolved name of a page family: prefix, scope, then the family in lower case.</summary>
        internal static string FaceKey(string scope, string family) =>
            FacePrefix + (scope ?? "") + ":" + family.Trim().ToLowerInvariant();

        /// <summary>Splits a <see cref="FaceKey"/> back into its scope and family. False for a built-in name.</summary>
        internal static bool TryParseFace(string resolved, out string scope, out string family)
        {
            scope = family = null;
            if (resolved == null || !resolved.StartsWith(FacePrefix, StringComparison.Ordinal)) return false;

            int split = resolved.IndexOf(':', FacePrefix.Length);
            if (split < 0) return false;

            scope = resolved.Substring(FacePrefix.Length, split - FacePrefix.Length);
            family = resolved.Substring(split + 1);
            return true;
        }

        /// <summary>
        /// The first family in the stack this engine has, or the default.
        ///
        /// A page's own <c>@font-face</c> families are checked before the built-ins, so a page that declares a face
        /// called <c>game-ui</c> gets its file, as a browser lets a declared face shadow any other of that name.
        /// Every non-generic name passed over on the way is reported once per session: it is a font the author
        /// asked for and the player will not see.
        /// </summary>
        internal static string Resolve(string stack)
        {
            if (string.IsNullOrWhiteSpace(stack)) return Default;

            List<string> skipped = null;
            string resolved = null;

            foreach (string entry in ValueParser.SplitTopLevel(stack, commaSeparated: true))
            {
                string name = entry.Trim().Trim('"', '\'').Trim();
                if (name.Length == 0) continue;

                if (Page != null && Page.Declares(name)) { resolved = FaceKey(Page.Scope, name); break; }
                if (Has(name)) { resolved = name; break; }

                if (!Generic.Contains(name)) (skipped ??= new List<string>()).Add(name);
            }

            resolved ??= Default;
            if (skipped != null) WarnOnce(skipped, resolved);
            return resolved;
        }

        private static void WarnOnce(List<string> skipped, string resolved)
        {
            // Muted while StyleApplier.Supports asks about a property NAME with a placeholder value - that value
            // is not a family anybody wrote.
            if (Diagnostics.Muted) return;

            foreach (string name in skipped)
            {
                if (!_warned.Add(name)) continue;

                string drawn = TryParseFace(resolved, out _, out string family) ? "'" + family + "'" : "'" + resolved + "'";
                Platform.Warning($"font-family '{name}' is neither a Sideload family nor declared with @font-face "
                                 + $"in this app - text falls back to {drawn}.");
            }
        }
    }

    /// <summary>The <c>@font-face</c> families of one page, and the scope that keeps them apart from other apps'.</summary>
    internal sealed class PageFonts
    {
        internal readonly string Scope;
        private readonly HashSet<string> _families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal PageFonts(string scope, IEnumerable<FontFaceRule> faces)
        {
            Scope = scope ?? "";
            foreach (FontFaceRule face in faces)
                if (!string.IsNullOrWhiteSpace(face.Family)) _families.Add(face.Family.Trim());
        }

        internal bool Declares(string family) => _families.Contains(family);
    }
}
