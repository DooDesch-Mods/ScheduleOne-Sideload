namespace Sideload.Model
{
    /// <summary>
    /// A key an app asked for OUTSIDE its own page - one that reaches it with the phone still in the player's pocket.
    ///
    /// Spelled exactly like the <c>data-keys</c> declarations in <see cref="KeyDeclarationSet"/>, and parsed through
    /// the same modifier splitter, because a player who learns one spelling has learnt both. The two differ only in
    /// which NAMES they accept, and only in the two places where the scope changes the answer:
    ///
    /// <list type="bullet">
    /// <item><c>Enter</c> is allowed here and refused there. A focused field already delivers Enter as its own submit,
    /// so a page declaring it would act on one press twice; with no field in the picture there is nothing to collide
    /// with, and Enter is the obvious key for "say something".</item>
    /// <item><c>Escape</c> is refused in both, and here for a stronger reason: it is the game's exit action - it
    /// closes the pause menu, dialogue, shops and the phone itself. An app that could take it globally could take it
    /// while the player is trying to leave something else.</item>
    /// </list>
    /// </summary>
    internal static class GlobalKey
    {
        /// <summary>Names a global key may use that a field-scoped one may not.</summary>
        private static readonly string[] Extra = { "Enter" };

        /// <summary>
        /// Read one declaration - <c>Enter</c>, <c>F8</c>, <c>Ctrl+Shift+K</c>. A refusal comes back already phrased
        /// as a reason, so the caller can log it without knowing the rules.
        /// </summary>
        internal static bool TryParse(string token, out KeyDeclaration key, out string refusal)
        {
            key = default;

            if (!KeyDeclarationSet.SplitModifiers(token, out string name,
                                                  out bool ctrl, out bool shift, out bool alt, out refusal))
                return false;

            if (KeyDeclarationSet.Same(name, "escape") || KeyDeclarationSet.Same(name, "esc"))
            {
                refusal = "Escape is the game's own exit action and cannot be taken";
                return false;
            }

            string canonical = Canonical(name);
            if (canonical == null) { refusal = $"unknown key '{name}'"; return false; }

            key = new KeyDeclaration(canonical, ctrl, shift, alt);
            return true;
        }

        /// <summary>Read a whole list: declarations separated by whitespace or commas. A bad one is dropped and
        /// reported rather than failing the rest, exactly as a <c>data-keys</c> attribute behaves.</summary>
        internal static IReadOnlyList<KeyDeclaration> Parse(string declaration, out IReadOnlyList<string> refused)
        {
            List<KeyDeclaration> keys = null;
            List<string> bad = null;

            foreach (string token in (declaration ?? "").Split(new[] { ' ', '\t', '\n', '\r', ',' },
                                                               StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryParse(token, out KeyDeclaration key, out string why))
                {
                    (bad ??= new List<string>()).Add($"'{token}' - {why}");
                    continue;
                }

                keys ??= new List<KeyDeclaration>();

                bool duplicate = false;
                for (int i = 0; i < keys.Count && !duplicate; i++) duplicate = keys[i].SameAs(key);
                if (!duplicate) keys.Add(key);
            }

            refused = (IReadOnlyList<string>)bad ?? Array.Empty<string>();
            return (IReadOnlyList<KeyDeclaration>)keys ?? Array.Empty<KeyDeclaration>();
        }

        /// <summary>The canonical spelling of a global key name, or null when it is not one Sideload knows.</summary>
        internal static string Canonical(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            for (int i = 0; i < Extra.Length; i++)
                if (KeyDeclarationSet.Same(name, Extra[i])) return Extra[i];

            // "Return" is what a keyboard's key is called and what half the world types; both mean Enter.
            if (KeyDeclarationSet.Same(name, "return")) return "Enter";

            return KeyDeclarationSet.Canonical(name);
        }

        /// <summary>Every name a global declaration may use, for the keycode table to walk. The field vocabulary plus
        /// the handful only this scope allows.</summary>
        internal static IReadOnlyList<string> Vocabulary
        {
            get
            {
                var names = new List<string>(Extra);
                names.AddRange(KeyDeclarationSet.Vocabulary);
                return names;
            }
        }
    }

    /// <summary>
    /// Who gets a key when more than one app wants it.
    ///
    /// The rule is "whoever spoke to you last": among the apps claiming a key, the one whose notification is the most
    /// recent wins. Two messengers installed side by side then behave the way a player already expects a phone to -
    /// the key answers the conversation that is actually waiting, not whichever mod happened to load first.
    ///
    /// <para>An app that has never notified is not excluded, it just sorts last, in the order the claims arrived. That
    /// is what makes the ordinary case work: with ONE app claiming Enter it wins from the first press, with no
    /// notification needed to earn a key nobody is competing for.</para>
    ///
    /// <para>Attention is a counter rather than a clock. Nothing here needs to know how long ago something happened,
    /// only what came after what - and a counter makes this whole file Unity-free and testable in a second.</para>
    ///
    /// <para>A handler returns whether it TOOK the key. Returning false passes the press to the next claimant, which
    /// is how an app declines a key it cannot use right now - a chat with no lobby behind it should not open, and
    /// should not swallow the key on the way.</para>
    /// </summary>
    internal sealed class KeyClaims
    {
        private sealed class Entry
        {
            internal string AppId;
            internal KeyDeclaration Key;
            internal Func<string, string, bool> Handler;

            /// <summary>Position in the order claims arrived - the tie-break, and the whole order before anyone has
            /// been notified.</summary>
            internal int Placed;
        }

        private readonly List<Entry> _claims = new List<Entry>();

        private readonly Dictionary<string, int> _attention =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private int _placed;
        private int _attended;

        /// <summary>The distinct key list, rebuilt only when a claim changes. <see cref="Keys"/> is read once a frame
        /// forever, and allocating a list sixty times a second to answer a question whose answer almost never changes
        /// is the kind of thing that only shows up on somebody else's machine.</summary>
        private KeyDeclaration[] _keys = Array.Empty<KeyDeclaration>();

        /// <summary>Whether anything at all is claimed - what the per-frame poll asks before doing any work.</summary>
        internal bool Any => _claims.Count > 0;

        /// <summary>
        /// Record that an app wants a key. A second claim of the same key by the same app REPLACES the handler rather
        /// than adding a second one, so a mod that re-registers (a scene change, a reload) does not end up with the
        /// press running twice.
        /// </summary>
        internal void Claim(string appId, KeyDeclaration key, Func<string, string, bool> handler)
        {
            if (string.IsNullOrWhiteSpace(appId) || handler == null) return;

            for (int i = 0; i < _claims.Count; i++)
            {
                if (!_claims[i].Key.SameAs(key)) continue;
                if (!string.Equals(_claims[i].AppId, appId, StringComparison.OrdinalIgnoreCase)) continue;

                _claims[i].Handler = handler;
                return;
            }

            _claims.Add(new Entry { AppId = appId, Key = key, Handler = handler, Placed = _placed++ });
            RebuildKeys();
        }

        /// <summary>Drop every claim an app holds. For a mod that wants its keys back out of the way while it is
        /// switched off.</summary>
        internal void Release(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId)) return;

            if (_claims.RemoveAll(c => string.Equals(c.AppId, appId, StringComparison.OrdinalIgnoreCase)) > 0)
                RebuildKeys();
        }

        /// <summary>An app just interrupted the player, so it is the one they are most likely to mean. Recorded for
        /// every app, claim or no claim - a mod may notify long before it ever asks for a key.</summary>
        internal void Attend(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId)) return;
            _attention[appId] = ++_attended;
        }

        /// <summary>Every distinct key somebody claims, so the frame poll reads each keyboard key once however many
        /// apps are behind it.</summary>
        internal IReadOnlyList<KeyDeclaration> Keys => _keys;

        private void RebuildKeys()
        {
            var keys = new List<KeyDeclaration>(_claims.Count);

            foreach (Entry claim in _claims)
            {
                bool seen = false;
                for (int i = 0; i < keys.Count && !seen; i++) seen = keys[i].SameAs(claim.Key);
                if (!seen) keys.Add(claim.Key);
            }

            _keys = keys.ToArray();
        }

        /// <summary>The apps claiming a key, best first. Exposed for its own sake because it is the whole rule, and a
        /// rule worth asserting on directly rather than through what the handlers happened to do.</summary>
        internal IReadOnlyList<string> Ranked(KeyDeclaration key)
        {
            var order = new List<Entry>();
            foreach (Entry claim in _claims) if (claim.Key.SameAs(key)) order.Add(claim);

            order.Sort(Compare);

            var ids = new List<string>(order.Count);
            foreach (Entry claim in order) ids.Add(claim.AppId);
            return ids;
        }

        /// <summary>
        /// Deliver one press. Walks the claimants best-first and stops at the first handler that takes it; returns
        /// that app's id, or null when nobody would have it.
        /// </summary>
        /// <param name="eligible">
        /// An optional veto the caller applies before a handler is even asked - what the host uses to say "the app in
        /// front of the player owns this key". Null lets every claimant through.
        /// </param>
        /// <param name="failed">
        /// Called with (appId, exception) when a handler throws. The throwing app is skipped and the press moves on,
        /// because a mod that breaks must not take a key away from the one behind it.
        /// </param>
        internal string Dispatch(KeyDeclaration key, Predicate<string> eligible = null,
                                 Action<string, Exception> failed = null)
        {
            var order = new List<Entry>();
            foreach (Entry claim in _claims) if (claim.Key.SameAs(key)) order.Add(claim);

            order.Sort(Compare);

            foreach (Entry claim in order)
            {
                if (eligible != null && !eligible(claim.AppId)) continue;

                try
                {
                    if (claim.Handler(claim.AppId, key.ToString())) return claim.AppId;
                }
                catch (Exception e) { failed?.Invoke(claim.AppId, e); }
            }

            return null;
        }

        /// <summary>Most recently attended first; never-attended last, in the order they claimed.</summary>
        private int Compare(Entry a, Entry b)
        {
            int mine = _attention.TryGetValue(a.AppId, out int x) ? x : 0;
            int theirs = _attention.TryGetValue(b.AppId, out int y) ? y : 0;

            return mine != theirs ? theirs.CompareTo(mine) : a.Placed.CompareTo(b.Placed);
        }
    }
}
