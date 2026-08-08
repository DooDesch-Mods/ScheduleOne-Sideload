namespace Sideload.Css
{
    // Unity-free, like everything else under Css/ - see the note at the top of Values.cs.

    /// <summary>
    /// One half of a track sizing function: what a column or row is asked to be.
    ///
    /// CSS Grid 7.2 writes a track as `minmax(min, max)`, and every other spelling is shorthand for one:
    /// `200px` is `minmax(200px, 200px)`, `1fr` is `minmax(auto, 1fr)`, `auto` is `minmax(auto, max-content)`.
    /// Splitting it here rather than keeping the surface spelling means the sizing algorithm has one shape to
    /// read instead of six.
    /// </summary>
    internal enum TrackSizeKind
    {
        /// <summary>A definite length or percentage.</summary>
        Length,

        /// <summary>`&lt;n&gt;fr` - a share of the free space. Only legal as the MAXIMUM.</summary>
        Fraction,

        /// <summary>`auto`. As a maximum that is max-content; as a minimum it is the automatic minimum, which is
        /// not the same thing - see <see cref="Layout.GridLayout"/> for what this engine takes it to be.</summary>
        Auto,

        MinContent,
        MaxContent,
    }

    internal readonly struct TrackSize
    {
        internal readonly TrackSizeKind Kind;

        /// <summary>Only meaningful for <see cref="TrackSizeKind.Length"/>.</summary>
        internal readonly Len Length;

        /// <summary>Only meaningful for <see cref="TrackSizeKind.Fraction"/>.</summary>
        internal readonly float Fraction;

        private TrackSize(TrackSizeKind kind, Len length, float fraction)
        {
            Kind = kind;
            Length = length;
            Fraction = fraction;
        }

        internal static readonly TrackSize Auto = new TrackSize(TrackSizeKind.Auto, Len.Auto, 0f);
        internal static readonly TrackSize MinContent = new TrackSize(TrackSizeKind.MinContent, Len.Auto, 0f);
        internal static readonly TrackSize MaxContent = new TrackSize(TrackSizeKind.MaxContent, Len.Auto, 0f);

        internal static TrackSize Fixed(Len length) => new TrackSize(TrackSizeKind.Length, length, 0f);
        internal static TrackSize Fr(float factor) => new TrackSize(TrackSizeKind.Fraction, Len.Auto, factor);

        internal bool IsFraction => Kind == TrackSizeKind.Fraction;

        public override string ToString() => Kind switch
        {
            TrackSizeKind.Length => Length.ToString(),
            TrackSizeKind.Fraction => Fraction.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "fr",
            TrackSizeKind.MinContent => "min-content",
            TrackSizeKind.MaxContent => "max-content",
            _ => "auto",
        };
    }

    /// <summary>One column or row, always in its `minmax` form.</summary>
    internal readonly struct GridTrack
    {
        internal readonly TrackSize Min, Max;

        internal GridTrack(TrackSize min, TrackSize max) { Min = min; Max = max; }

        /// <summary>`auto`: as small as the automatic minimum allows, as large as the content wants.</summary>
        internal static readonly GridTrack Auto = new GridTrack(TrackSize.Auto, TrackSize.MaxContent);

        public override string ToString() => $"minmax({Min}, {Max})";
    }

    /// <summary>
    /// A parsed `grid-template-columns` / `grid-template-rows`.
    ///
    /// <see cref="Tracks"/> is the list as written, with any `repeat(auto-fit|auto-fill, ...)` present exactly
    /// ONCE. How often that one repetition actually appears depends on how much room the container turns out to
    /// have, which the cascade does not know - so the repeat is recorded here and expanded by the layout.
    /// </summary>
    internal sealed class GridTemplate
    {
        internal readonly List<GridTrack> Tracks;

        /// <summary>Index into <see cref="Tracks"/> where the auto repetition starts, or -1 when there is none.</summary>
        internal readonly int AutoRepeatAt;

        /// <summary>How many tracks make up one repetition.</summary>
        internal readonly int AutoRepeatCount;

        /// <summary>True for `auto-fit`, false for `auto-fill`.</summary>
        internal readonly bool AutoFit;

        internal GridTemplate(List<GridTrack> tracks, int autoRepeatAt, int autoRepeatCount, bool autoFit)
        {
            Tracks = tracks;
            AutoRepeatAt = autoRepeatAt;
            AutoRepeatCount = autoRepeatCount;
            AutoFit = autoFit;
        }

        internal bool HasAutoRepeat => AutoRepeatAt >= 0 && AutoRepeatCount > 0;

        public override string ToString() => string.Join(" ", Tracks);
    }

    internal enum GridLineKind
    {
        /// <summary>Nothing was said - the item is placed by the auto-placement pass. Deliberately the default so
        /// an untouched <see cref="GridPlacement"/> means "auto", not "line 0", which is not a line at all.</summary>
        Auto = 0,

        /// <summary>An explicit line number. Negative counts back from the end of the explicit grid.</summary>
        Number,

        /// <summary>`span &lt;n&gt;`.</summary>
        Span,
    }

    internal readonly struct GridLine
    {
        internal readonly GridLineKind Kind;
        internal readonly int Value;

        private GridLine(GridLineKind kind, int value) { Kind = kind; Value = value; }

        internal static readonly GridLine Auto = new GridLine(GridLineKind.Auto, 0);
        internal static GridLine Number(int line) => new GridLine(GridLineKind.Number, line);
        internal static GridLine Span(int tracks) => new GridLine(GridLineKind.Span, tracks < 1 ? 1 : tracks);

        public override string ToString() => Kind switch
        {
            GridLineKind.Number => Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            GridLineKind.Span => "span " + Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => "auto",
        };
    }

    /// <summary>Where an item sits on one axis: `grid-column` or `grid-row`.</summary>
    internal readonly struct GridPlacement
    {
        internal readonly GridLine Start, End;

        internal GridPlacement(GridLine start, GridLine end) { Start = start; End = end; }

        public override string ToString() =>
            End.Kind == GridLineKind.Auto ? Start.ToString() : Start + " / " + End;
    }

    /// <summary>
    /// Reads the grid value syntaxes. Total, like <see cref="ValueParser"/>: anything it cannot make sense of
    /// yields false and the caller leaves the property alone.
    ///
    /// Two pieces of grid syntax are recognised here only so they can be REFUSED with a name rather than parsed
    /// into something wrong: line names in square brackets are stripped (the tracks around them still work, the
    /// names do nothing), and a bare custom-ident where a line number belongs is a named area, which this engine
    /// does not place by. Both are reported through <see cref="DeadValues"/>.
    /// </summary>
    internal static class GridParser
    {
        /// <summary>A track list: `200px 1fr auto`, `repeat(3, minmax(0, 1fr))`, `none`.</summary>
        internal static bool TryTrackList(string value, in LengthContext ctx, out GridTemplate template)
        {
            template = null;
            if (string.IsNullOrEmpty(value)) return false;

            string cleaned = StripLineNames(value);
            if (cleaned.Length == 0) return false;
            if (Is(cleaned, "none")) return true;                 // no explicit tracks; every one is implicit

            var tracks = new List<GridTrack>();
            int autoRepeatAt = -1, autoRepeatCount = 0;
            bool autoFit = false;

            foreach (string token in ValueParser.SplitTopLevel(cleaned))
            {
                if (token.StartsWith("repeat(", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryRepeat(token, ctx, tracks, ref autoRepeatAt, ref autoRepeatCount, ref autoFit))
                        return false;
                    continue;
                }

                if (!TryTrack(token, ctx, out GridTrack track)) return false;
                tracks.Add(track);
            }

            if (tracks.Count == 0) return false;

            template = new GridTemplate(tracks, autoRepeatAt, autoRepeatCount, autoFit);
            return true;
        }

        /// <summary>One track sizing function.</summary>
        internal static bool TryTrack(string token, in LengthContext ctx, out GridTrack track)
        {
            track = GridTrack.Auto;
            if (string.IsNullOrEmpty(token)) return false;
            token = token.Trim();

            if (Is(token, "auto")) { track = GridTrack.Auto; return true; }
            if (Is(token, "min-content")) { track = new GridTrack(TrackSize.MinContent, TrackSize.MinContent); return true; }
            if (Is(token, "max-content")) { track = new GridTrack(TrackSize.MaxContent, TrackSize.MaxContent); return true; }

            if (token.StartsWith("minmax(", StringComparison.OrdinalIgnoreCase))
            {
                string inner = Inside(token);
                if (inner == null) return false;

                string[] args = ValueParser.SplitTopLevel(inner, commaSeparated: true);
                if (args.Length != 2) return false;

                if (!TrySize(args[0], ctx, out TrackSize min) || !TrySize(args[1], ctx, out TrackSize max)) return false;

                // `minmax(1fr, ...)` is invalid CSS: a flexible minimum has nothing to be a fraction OF.
                if (min.IsFraction) return false;

                track = new GridTrack(min, max);
                return true;
            }

            if (!TrySize(token, ctx, out TrackSize size)) return false;

            // A bare `<n>fr` is `minmax(auto, <n>fr)`, and a bare length is `minmax(length, length)`.
            track = size.IsFraction ? new GridTrack(TrackSize.Auto, size) : new GridTrack(size, size);
            return true;
        }

        private static bool TrySize(string token, in LengthContext ctx, out TrackSize size)
        {
            size = TrackSize.Auto;
            if (string.IsNullOrEmpty(token)) return false;
            token = token.Trim();

            if (Is(token, "auto")) { size = TrackSize.Auto; return true; }
            if (Is(token, "min-content")) { size = TrackSize.MinContent; return true; }
            if (Is(token, "max-content")) { size = TrackSize.MaxContent; return true; }

            if (token.EndsWith("fr", StringComparison.OrdinalIgnoreCase)
                && ValueParser.TryNumber(token.Substring(0, token.Length - 2), out float factor))
            {
                size = TrackSize.Fr(factor < 0f ? 0f : factor);
                return true;
            }

            if (ValueParser.TryLength(token, ctx, out Len len) && len.IsDefinite)
            {
                size = TrackSize.Fixed(len);
                return true;
            }

            return false;
        }

        private static bool TryRepeat(string token, in LengthContext ctx, List<GridTrack> into,
                                      ref int autoRepeatAt, ref int autoRepeatCount, ref bool autoFit)
        {
            string inner = Inside(token);
            if (inner == null) return false;

            string[] args = ValueParser.SplitTopLevel(inner, commaSeparated: true);
            if (args.Length < 2) return false;

            string count = args[0].Trim();
            bool auto = Is(count, "auto-fit") || Is(count, "auto-fill");

            // One auto repetition per track list, as CSS requires - two of them have no defined answer, and
            // guessing at one would be worse than refusing the declaration.
            if (auto && autoRepeatAt >= 0) return false;

            int repetitions = 1;
            if (!auto)
            {
                if (!ValueParser.TryNumber(count, out float n) || n < 1f) return false;
                repetitions = (int)n;

                // A repeat count is written by hand and a big one is a typo, not a layout. Ten thousand tracks
                // would be allocated and sized before anything could say so.
                if (repetitions > 1000) return false;
            }

            var body = new List<GridTrack>();
            for (int i = 1; i < args.Length; i++)
            {
                foreach (string part in ValueParser.SplitTopLevel(args[i]))
                {
                    if (!TryTrack(part, ctx, out GridTrack track)) return false;
                    body.Add(track);
                }
            }

            if (body.Count == 0) return false;

            if (auto)
            {
                autoRepeatAt = into.Count;
                autoRepeatCount = body.Count;
                autoFit = Is(count, "auto-fit");
                into.AddRange(body);
                return true;
            }

            for (int i = 0; i < repetitions; i++) into.AddRange(body);
            return true;
        }

        /// <summary>`grid-column` / `grid-row`: `2`, `1 / 3`, `span 2`, `1 / span 2`, `auto`.</summary>
        internal static bool TryPlacement(string value, out GridPlacement placement)
        {
            placement = new GridPlacement(GridLine.Auto, GridLine.Auto);
            if (string.IsNullOrEmpty(value)) return false;

            string[] sides = SplitSlash(value);
            if (sides.Length == 0 || sides.Length > 2) return false;

            if (!TryLine(sides[0], out GridLine start)) return false;
            GridLine end = GridLine.Auto;
            if (sides.Length == 2 && !TryLine(sides[1], out end)) return false;

            placement = new GridPlacement(start, end);
            return true;
        }

        /// <summary>`grid-area` in its line-number form: row-start / column-start / row-end / column-end.</summary>
        internal static bool TryArea(string value, out GridPlacement rows, out GridPlacement columns)
        {
            rows = new GridPlacement(GridLine.Auto, GridLine.Auto);
            columns = rows;
            if (string.IsNullOrEmpty(value)) return false;

            string[] parts = SplitSlash(value);
            if (parts.Length != 1 && parts.Length != 2 && parts.Length != 4) return false;

            var lines = new GridLine[4] { GridLine.Auto, GridLine.Auto, GridLine.Auto, GridLine.Auto };
            for (int i = 0; i < parts.Length; i++)
                if (!TryLine(parts[i], out lines[i])) return false;

            rows = new GridPlacement(lines[0], lines[2]);
            columns = new GridPlacement(lines[1], lines[3]);
            return true;
        }

        internal static bool TryLine(string token, out GridLine line)
        {
            line = GridLine.Auto;
            if (string.IsNullOrEmpty(token)) return false;
            token = token.Trim();

            if (Is(token, "auto")) return true;

            string[] parts = ValueParser.SplitTopLevel(token);
            if (parts.Length == 1 && Is(parts[0], "span")) { line = GridLine.Span(1); return true; }

            if (parts.Length == 2 && Is(parts[0], "span"))
            {
                if (!TryInteger(parts[1], out int span) || span < 1) return false;
                line = GridLine.Span(span);
                return true;
            }

            if (parts.Length != 1) return false;
            if (!TryInteger(parts[0], out int number) || number == 0) return false;   // line 0 does not exist

            line = GridLine.Number(number);
            return true;
        }

        /// <summary>
        /// Whether the value names something rather than numbering it - `grid-area: sidebar`,
        /// `grid-column: content-start / content-end`.
        ///
        /// Named lines and named areas are their own feature (a whole second placement model built on
        /// `grid-template-areas`) and are not implemented. This is how the applier tells "not supported" apart
        /// from "not valid CSS", so the author gets the first message rather than the second.
        /// </summary>
        internal static bool NamesAnArea(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;

            foreach (string side in SplitSlash(value))
            {
                foreach (string word in ValueParser.SplitTopLevel(side))
                {
                    if (Is(word, "span") || Is(word, "auto")) continue;
                    if (TryInteger(word, out _)) continue;
                    if (IsIdentifier(word)) return true;
                }
            }

            return false;
        }

        /// <summary>Whether the track list carries line names, which are read and then dropped.</summary>
        internal static bool NamesLines(string value) =>
            !string.IsNullOrEmpty(value) && value.IndexOf('[') >= 0;

        /// <summary>Splits `a / b` at its one top-level slash. False for none and for more than one.</summary>
        internal static bool TrySplitSlash(string value, out string left, out string right)
        {
            left = right = null;
            string[] parts = SplitSlash(value ?? "");
            if (parts.Length != 2) return false;

            left = parts[0];
            right = parts[1];
            return true;
        }

        // --------------------------------------------------------------------- helpers --

        /// <summary>
        /// Removes `[line-name]` groups from a track list.
        ///
        /// Dropping them rather than refusing the whole declaration is the kinder failure: the tracks around the
        /// names are perfectly ordinary and lay out correctly without them, so a stylesheet that labels its lines
        /// still gets its layout. That the names themselves do nothing is reported separately.
        /// </summary>
        private static string StripLineNames(string value)
        {
            if (value.IndexOf('[') < 0) return value.Trim();

            var sb = new System.Text.StringBuilder(value.Length);
            int depth = 0;
            foreach (char c in value)
            {
                if (c == '[') { depth++; sb.Append(' '); continue; }
                if (c == ']') { if (depth > 0) depth--; sb.Append(' '); continue; }
                if (depth == 0) sb.Append(c);
            }

            return sb.ToString().Trim();
        }

        /// <summary>The argument list of a one-function value, or null when the parentheses do not close.</summary>
        private static string Inside(string token)
        {
            int open = token.IndexOf('(');
            if (open < 0) return null;

            int depth = 0;
            for (int i = open; i < token.Length; i++)
            {
                if (token[i] == '(') depth++;
                else if (token[i] == ')')
                {
                    depth--;
                    if (depth != 0) continue;

                    // Anything after the closing bracket means this was not one function but a run of them.
                    if (i != token.Length - 1) return null;
                    return token.Substring(open + 1, i - open - 1);
                }
            }

            return null;
        }

        private static string[] SplitSlash(string value)
        {
            var parts = new List<string>();
            var current = new System.Text.StringBuilder();
            int depth = 0;

            foreach (char c in value)
            {
                if (c == '(') depth++;
                else if (c == ')') depth--;

                if (c == '/' && depth == 0)
                {
                    parts.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }

                current.Append(c);
            }

            parts.Add(current.ToString().Trim());
            for (int i = parts.Count - 1; i >= 0; i--)
                if (parts[i].Length == 0) parts.RemoveAt(i);

            return parts.ToArray();
        }

        private static bool TryInteger(string s, out int value)
        {
            value = 0;
            if (!ValueParser.TryNumber(s, out float n)) return false;
            if (n != (float)(int)n) return false;
            value = (int)n;
            return true;
        }

        private static bool IsIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (!char.IsLetter(s[0]) && s[0] != '_' && s[0] != '-') return false;

            foreach (char c in s)
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-') return false;

            return true;
        }

        private static bool Is(string value, string keyword) =>
            string.Equals(value, keyword, StringComparison.OrdinalIgnoreCase);
    }
}
