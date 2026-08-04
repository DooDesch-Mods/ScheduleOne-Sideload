namespace Sideload.Model
{
    /// <summary>
    /// One key an app asked for, spelled the way the DOM spells it: an optional run of modifiers and a name, joined
    /// by <c>+</c> - <c>Tab</c>, <c>ArrowUp</c>, <c>Ctrl+R</c>, <c>Ctrl+Shift+K</c>.
    ///
    /// Modifiers match EXACTLY. <c>Tab</c> fires only for a bare Tab, never for Shift+Tab, so an app that wants both
    /// declares both and can tell them apart. The alternative - a bare name matching any modifier state - reads fine
    /// until the day a page handles Tab and the player presses Ctrl+Tab meaning something else entirely.
    /// </summary>
    internal readonly struct KeyDeclaration
    {
        internal KeyDeclaration(string name, bool ctrl, bool shift, bool alt)
        {
            Name = name;
            Ctrl = ctrl;
            Shift = shift;
            Alt = alt;
        }

        /// <summary>The canonical spelling, which is also what script reads as <c>e.key</c>.</summary>
        internal string Name { get; }

        internal bool Ctrl { get; }

        internal bool Shift { get; }

        internal bool Alt { get; }

        internal bool SameAs(KeyDeclaration other) =>
            Ctrl == other.Ctrl && Shift == other.Shift && Alt == other.Alt
            && string.Equals(Name, other.Name, StringComparison.Ordinal);

        public override string ToString() =>
            (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "") + Name;
    }

    /// <summary>
    /// What one element's <c>data-keys</c> attribute asked for, plus what had to be thrown away.
    ///
    /// Parsed away from Unity because everything interesting about it is string handling - which modifiers were
    /// named, which spellings are the same key, which names are refused and why - and all of that is worth a test
    /// that runs in a second instead of a game launch.
    /// </summary>
    internal sealed class KeyDeclarationSet
    {
        internal static readonly KeyDeclarationSet Empty =
            new KeyDeclarationSet(Array.Empty<KeyDeclaration>(), Array.Empty<string>());

        private readonly KeyDeclaration[] _keys;

        private KeyDeclarationSet(KeyDeclaration[] keys, IReadOnlyList<string> refused)
        {
            _keys = keys;
            Refused = refused;
        }

        internal int Count => _keys.Length;

        internal KeyDeclaration this[int index] => _keys[index];

        /// <summary>Every declaration that was dropped, already phrased as a reason, so the caller can put it in the
        /// log without knowing why any of them failed.</summary>
        internal IReadOnlyList<string> Refused { get; }

        /// <summary>True when any declaration names this key, whatever its modifiers - the question the caret guard
        /// asks, because TextMeshPro moves the caret regardless of what is held alongside.</summary>
        internal bool Uses(string name)
        {
            for (int i = 0; i < _keys.Length; i++)
                if (string.Equals(_keys[i].Name, name, StringComparison.Ordinal)) return true;

            return false;
        }

        /// <summary>
        /// Read a whole attribute: declarations separated by whitespace or commas. A bad one is dropped and reported
        /// rather than failing the set, because a typo in one key must not cost an app the other eleven.
        /// </summary>
        internal static KeyDeclarationSet Parse(string attribute)
        {
            if (string.IsNullOrWhiteSpace(attribute)) return Empty;

            List<KeyDeclaration> keys = null;
            List<string> refused = null;

            foreach (string token in attribute.Split(new[] { ' ', '\t', '\n', '\r', ',' },
                                                     StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryParseOne(token, out KeyDeclaration key, out string why))
                {
                    (refused ??= new List<string>()).Add($"'{token}' - {why}");
                    continue;
                }

                keys ??= new List<KeyDeclaration>();

                bool duplicate = false;
                for (int i = 0; i < keys.Count && !duplicate; i++) duplicate = keys[i].SameAs(key);
                if (!duplicate) keys.Add(key);
            }

            if (keys == null && refused == null) return Empty;

            return new KeyDeclarationSet(keys?.ToArray() ?? Array.Empty<KeyDeclaration>(),
                                         (IReadOnlyList<string>)refused ?? Array.Empty<string>());
        }

        /// <summary>
        /// Parse one declaration. Refuses the two keys that already reach a page by another route: Enter arrives as a
        /// <c>keydown</c> from the field's own submit, and Escape arrives as <c>back</c>. Delivering either twice
        /// would make a page act on one press twice, which is worse than not supporting them at all.
        /// </summary>
        internal static bool TryParseOne(string token, out KeyDeclaration key, out string refusal)
        {
            key = default;
            refusal = null;

            if (string.IsNullOrWhiteSpace(token)) { refusal = "empty"; return false; }

            bool ctrl = false, shift = false, alt = false;
            string name = token.Trim();

            while (true)
            {
                int plus = name.IndexOf('+');
                if (plus <= 0) break;

                string modifier = name.Substring(0, plus).Trim();
                string rest = name.Substring(plus + 1).Trim();
                if (rest.Length == 0) { refusal = "nothing after '+'"; return false; }

                if (Same(modifier, "ctrl") || Same(modifier, "control")) ctrl = true;
                else if (Same(modifier, "shift")) shift = true;
                else if (Same(modifier, "alt")) alt = true;
                else { refusal = $"unknown modifier '{modifier}'"; return false; }

                name = rest;
            }

            if (Same(name, "enter") || Same(name, "return"))
            {
                refusal = "Enter already arrives as a keydown from the field itself";
                return false;
            }

            if (Same(name, "escape") || Same(name, "esc"))
            {
                refusal = "Escape already arrives as the 'back' event";
                return false;
            }

            string canonical = Canonical(name);
            if (canonical == null) { refusal = $"unknown key '{name}'"; return false; }

            key = new KeyDeclaration(canonical, ctrl, shift, alt);
            return true;
        }

        /// <summary>
        /// Every named key an app may declare, in its canonical spelling.
        ///
        /// Deliberately not the whole keyboard: punctuation sits on different physical keys on different layouts, so
        /// a page declaring one would work for its author and silently not for half its players. Letters, digits, the
        /// editing keys and the function row are the same everywhere.
        ///
        /// Public because the Unity side builds its keycode table by walking this list, which is what stops the two
        /// from drifting apart - a name added here and forgotten there reports itself at startup instead of becoming
        /// a key that never fires.
        /// </summary>
        internal static readonly IReadOnlyList<string> Vocabulary = BuildVocabulary();

        private static IReadOnlyList<string> BuildVocabulary()
        {
            var names = new List<string>
            {
                "Tab", "Backspace", "Delete", "Insert", "Home", "End", "PageUp", "PageDown",
                "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Space",
            };

            for (int i = 1; i <= 12; i++) names.Add("F" + i);

            return names;
        }

        /// <summary>The canonical spelling of a name, or null when it is not one Sideload knows. Letters come back
        /// lower-case and digits as themselves, matching what a browser reports for <c>e.key</c>.</summary>
        internal static string Canonical(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            for (int i = 0; i < Vocabulary.Count; i++)
                if (Same(name, Vocabulary[i])) return Vocabulary[i];

            if (name.Length != 1) return null;

            char c = char.ToLowerInvariant(name[0]);
            if (c >= 'a' && c <= 'z') return c.ToString();
            if (c >= '0' && c <= '9') return c.ToString();

            return null;
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
