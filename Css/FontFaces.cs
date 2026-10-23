using Sideload.Model;

namespace Sideload.Css
{
    /// <summary>
    /// One <c>@font-face</c> block: a family name, the bundle file that draws it, and the weights and style that
    /// file stands for.
    ///
    /// Unity-free on purpose. Parsing the rule and choosing the face for a weight are pure CSS questions and are
    /// tested headlessly; only turning the file into a TMP font asset happens in <c>Paint/BundleFonts.cs</c>.
    /// </summary>
    internal sealed class FontFaceRule
    {
        /// <summary>The family name as written, quotes removed.</summary>
        internal string Family;

        /// <summary>
        /// Bundle-relative path of the file to load, or null when no source in <c>src</c> is one this engine can
        /// read. Resolved against the bundle root, the same root <c>index.html</c> sits in.
        /// </summary>
        internal string Source;

        /// <summary>Why <see cref="Source"/> is null, for the warning the host prints. Null when it is set.</summary>
        internal string Problem;

        /// <summary>The weight range the file covers. A single weight is a range of one.</summary>
        internal int WeightMin = 400, WeightMax = 400;

        internal bool Italic;

        internal string Describe() =>
            $"'{Family}' {(WeightMin == WeightMax ? WeightMin.ToString() : WeightMin + "-" + WeightMax)}"
            + (Italic ? " italic" : "");
    }

    /// <summary>Reading <c>@font-face</c> and picking a face out of a family.</summary>
    internal static class FontFaces
    {
        /// <summary>
        /// Reads the body of one <c>@font-face</c> block. Returns null when the block names no family, because a
        /// face without a family can never be asked for.
        /// </summary>
        internal static FontFaceRule Parse(string body)
        {
            var rule = new FontFaceRule();

            foreach (Declaration declaration in CssParser.ParseDeclarations(body))
            {
                string value = declaration.Value?.Trim() ?? "";
                switch (declaration.Property?.Trim().ToLowerInvariant())
                {
                    case "font-family":
                        rule.Family = Unquote(value);
                        break;

                    case "src":
                        rule.Source = PickSource(value, out rule.Problem);
                        break;

                    case "font-weight":
                        ParseWeightRange(value, rule);
                        break;

                    case "font-style":
                        // `oblique 10deg` and friends: there is no slant axis to drive, and an oblique file is
                        // what a page asking for italic would get from a browser too.
                        rule.Italic = value.StartsWith("italic", StringComparison.OrdinalIgnoreCase)
                                      || value.StartsWith("oblique", StringComparison.OrdinalIgnoreCase);
                        break;

                    default:
                        // font-display, unicode-range, font-stretch, size-adjust, ascent-override... Each one says
                        // something about loading or metrics the TMP asset decides for itself.
                        Diagnostics.Report(DiagnosticKind.ValueIgnored, "@font-face " + declaration.Property,
                                           value);
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(rule.Family)) return null;
            if (rule.Source == null && rule.Problem == null) rule.Problem = "it has no src";
            return rule;
        }

        /// <summary>
        /// The first source in a <c>src</c> list that is a bundle file in a format FreeType reads without help.
        ///
        /// WOFF and WOFF2 are refused rather than tried: both are compressed containers, and nothing in this build
        /// decompresses them before the font engine sees the bytes. <c>local()</c> names a font on the player's
        /// machine, which the game cannot look up (the OS-font API is stripped). A <c>data:</c> URI would work in
        /// principle and is not worth a second decoder - the file next to the stylesheet does the same job.
        /// </summary>
        private static string PickSource(string value, out string problem)
        {
            problem = null;
            var refused = new List<string>();

            foreach (string entry in ValueParser.SplitTopLevel(value, commaSeparated: true))
            {
                string item = entry.Trim();
                if (item.StartsWith("local(", StringComparison.OrdinalIgnoreCase))
                {
                    refused.Add(item + " (system fonts cannot be looked up)");
                    continue;
                }

                if (!item.StartsWith("url(", StringComparison.OrdinalIgnoreCase)) continue;

                int close = item.IndexOf(')');
                if (close < 0) continue;

                string url = Unquote(item.Substring(4, close - 4).Trim());
                string format = FormatOf(item.Substring(close + 1));

                if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    refused.Add("a data: URI (ship the file in the bundle instead)");
                    continue;
                }

                if (url.IndexOf("://", StringComparison.Ordinal) >= 0)
                {
                    refused.Add(url + " (only bundle files are loaded)");
                    continue;
                }

                string extension = Path.GetExtension(StripQuery(url)).ToLowerInvariant();
                bool readable = format != null
                    ? format is "truetype" or "opentype" or "truetype-variations" or "opentype-variations"
                    : extension is ".ttf" or ".otf";

                if (!readable)
                {
                    refused.Add(url + " (" + (format ?? extension) + " is not TTF or OTF)");
                    continue;
                }

                return NormalisePath(StripQuery(url));
            }

            problem = refused.Count == 0
                ? "its src names no url()"
                : "no readable source in src: " + string.Join("; ", refused);
            return null;
        }

        /// <summary>The <c>format("...")</c> hint after a url, lower case, or null when there is none.</summary>
        private static string FormatOf(string tail)
        {
            int at = tail.IndexOf("format(", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;

            int close = tail.IndexOf(')', at);
            if (close < 0) return null;

            return Unquote(tail.Substring(at + 7, close - at - 7).Trim()).ToLowerInvariant();
        }

        /// <summary>
        /// `fonts/a.ttf`, `./fonts/a.ttf` and `/fonts/a.ttf` all name the same bundle file, and `../` cannot leave
        /// the bundle: a segment that would climb above the root is dropped, the way a browser treats `/..`.
        /// </summary>
        internal static string NormalisePath(string url)
        {
            var parts = new List<string>();
            foreach (string part in url.Replace('\\', '/').Split('/'))
            {
                if (part.Length == 0 || part == ".") continue;
                if (part == "..")
                {
                    if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                    continue;
                }
                parts.Add(part);
            }
            return string.Join("/", parts);
        }

        private static string StripQuery(string url)
        {
            int cut = url.IndexOfAny(new[] { '?', '#' });
            return cut < 0 ? url : url.Substring(0, cut);
        }

        private static void ParseWeightRange(string value, FontFaceRule rule)
        {
            string[] words = value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            int first = words.Length > 0 ? Weight(words[0]) : -1;
            int second = words.Length > 1 ? Weight(words[1]) : first;

            if (first < 0 || second < 0)
            {
                Diagnostics.Report(DiagnosticKind.ValueRejected, "@font-face font-weight", value);
                return;
            }

            rule.WeightMin = Math.Min(first, second);
            rule.WeightMax = Math.Max(first, second);
        }

        private static int Weight(string word)
        {
            if (word.Equals("normal", StringComparison.OrdinalIgnoreCase)) return 400;
            if (word.Equals("bold", StringComparison.OrdinalIgnoreCase)) return 700;
            if (int.TryParse(word, System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= 1000)
                return n;
            return -1;
        }

        internal static string Unquote(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0])
                s = s.Substring(1, s.Length - 2);
            return s.Trim();
        }

        /// <summary>
        /// Which of a family's faces draws text at this weight and style, as an index into <paramref name="faces"/>,
        /// or -1 when the list is empty.
        ///
        /// The style is settled first: an italic request takes an italic face when the family has one, and an
        /// upright request an upright one; only when the family has none of the wanted style does the other style
        /// stand in (the painter then slants an upright face, as a browser synthesises an oblique). Within the
        /// style the nearest weight wins - a face whose range contains the weight is distance zero. On a tie the
        /// CSS direction decides: below 400 the lighter face, from 400 up the heavier one, which is what puts a
        /// 400 request on the 500 face before the 300 one.
        /// </summary>
        internal static int Pick(IReadOnlyList<FontFaceRule> faces, int weight, bool italic)
        {
            if (faces == null || faces.Count == 0) return -1;

            bool styleExists = false;
            foreach (FontFaceRule face in faces)
                if (face.Italic == italic) { styleExists = true; break; }

            int best = -1, bestDistance = int.MaxValue;
            bool bestHeavier = false;

            for (int i = 0; i < faces.Count; i++)
            {
                FontFaceRule face = faces[i];
                if (styleExists && face.Italic != italic) continue;

                int distance;
                bool heavier;
                if (weight < face.WeightMin) { distance = face.WeightMin - weight; heavier = true; }
                else if (weight > face.WeightMax) { distance = weight - face.WeightMax; heavier = false; }
                else { distance = 0; heavier = false; }

                // An exact tie with the face already held goes to the later one, which is how a sheet replaces a
                // face it declared earlier - the same "last declaration wins" a browser applies to @font-face.
                bool wins = distance < bestDistance
                            || (distance == bestDistance && heavier == bestHeavier)
                            || (distance == bestDistance && heavier != bestHeavier && heavier == (weight >= 400));

                if (best < 0 || wins)
                {
                    best = i;
                    bestDistance = distance;
                    bestHeavier = heavier;
                }
            }

            return best;
        }
    }
}
