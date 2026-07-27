namespace Sideload.Model
{
    /// <summary>
    /// What an app said about which ways round the phone may hold it.
    ///
    /// Parsing lives here, away from Unity, because it decides two things a player notices: which orientation an app
    /// opens in, and whether the rotate keys do anything at all. Both are worth a test that runs in a second rather
    /// than a game launch.
    /// </summary>
    internal readonly struct OrientationSet
    {
        private OrientationSet(bool declared, bool portrait, bool canTurn, IReadOnlyList<string> ignored)
        {
            Declared = declared;
            Portrait = portrait;
            CanTurn = canTurn;
            Ignored = ignored;
        }

        /// <summary>False when the app named nothing recognisable, in which case its previous setting stands.</summary>
        internal bool Declared { get; }

        /// <summary>Whether the FIRST orientation named was portrait - what the app opens in.</summary>
        internal bool Portrait { get; }

        /// <summary>Whether both were named, which is the only thing that permits turning.</summary>
        internal bool CanTurn { get; }

        /// <summary>Tokens that were neither "portrait" nor "landscape", so the caller can say so out loud.</summary>
        internal IReadOnlyList<string> Ignored { get; }

        /// <summary>
        /// Read a comma-separated list in preference order: "landscape", "portrait", "landscape,portrait". Order
        /// carries the default, repetition is harmless, and anything unrecognised is reported rather than guessed at.
        /// </summary>
        internal static OrientationSet Parse(string list)
        {
            bool sawPortrait = false, sawLandscape = false, firstIsPortrait = false, sawAny = false;
            List<string> ignored = null;

            foreach (string raw in (list ?? "").Split(','))
            {
                string one = raw.Trim();
                if (one.Length == 0) continue;

                bool portrait = string.Equals(one, "portrait", StringComparison.OrdinalIgnoreCase);
                bool landscape = string.Equals(one, "landscape", StringComparison.OrdinalIgnoreCase);

                if (!portrait && !landscape)
                {
                    (ignored ??= new List<string>()).Add(one);
                    continue;
                }

                if (!sawAny) { firstIsPortrait = portrait; sawAny = true; }
                sawPortrait |= portrait;
                sawLandscape |= landscape;
            }

            return new OrientationSet(sawAny, firstIsPortrait, sawPortrait && sawLandscape,
                                      (IReadOnlyList<string>)ignored ?? Array.Empty<string>());
        }
    }
}
