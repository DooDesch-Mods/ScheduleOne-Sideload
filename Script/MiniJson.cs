using System.Text;

namespace Sideload.Script
{
    /// <summary>
    /// A flat string-to-string JSON object, read and written by hand.
    ///
    /// Not a general JSON library and not trying to be: `s1.storage` stores strings, and a page that wants structure
    /// already has JSON.stringify on the script side. Doing it here avoids taking a dependency for sixty lines and
    /// avoids System.Text.Json, which MelonLoader's runtime does not reliably ship.
    /// </summary>
    internal static class MiniJson
    {
        internal static string WriteObject(Dictionary<string, string> values)
        {
            if (values == null || values.Count == 0) return "{}";

            var sb = new StringBuilder("{");
            bool first = true;

            foreach (KeyValuePair<string, string> pair in values)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('\n').Append("  ");
                Escape(sb, pair.Key);
                sb.Append(": ");
                Escape(sb, pair.Value);
            }

            return sb.Append("\n}").ToString();
        }

        internal static Dictionary<string, string> ParseObject(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(json)) return result;

            int i = 0;
            SkipWhitespace(json, ref i);
            if (i >= json.Length || json[i] != '{') return result;
            i++;

            while (true)
            {
                SkipWhitespace(json, ref i);
                if (i >= json.Length || json[i] == '}') break;

                if (json[i] == ',') { i++; continue; }
                if (json[i] != '"') break;

                string key = ReadString(json, ref i);
                SkipWhitespace(json, ref i);
                if (i >= json.Length || json[i] != ':') break;
                i++;

                SkipWhitespace(json, ref i);
                if (i >= json.Length) break;

                // Only strings are written, but tolerate a hand-edited file that puts a bare literal in.
                result[key] = json[i] == '"' ? ReadString(json, ref i) : ReadBareValue(json, ref i);
            }

            return result;
        }

        private static void Escape(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static string ReadString(string json, ref int i)
        {
            var sb = new StringBuilder();
            i++;   // opening quote

            while (i < json.Length)
            {
                char c = json[i++];
                if (c == '"') break;

                if (c != '\\') { sb.Append(c); continue; }
                if (i >= json.Length) break;

                char escape = json[i++];
                switch (escape)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 <= json.Length && int.TryParse(json.Substring(i, 4),
                                System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out int code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(escape); break;
                }
            }

            return sb.ToString();
        }

        private static string ReadBareValue(string json, ref int i)
        {
            int start = i;
            while (i < json.Length && json[i] != ',' && json[i] != '}') i++;
            return json.Substring(start, i - start).Trim();
        }

        private static void SkipWhitespace(string json, ref int i)
        {
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        }
    }
}
