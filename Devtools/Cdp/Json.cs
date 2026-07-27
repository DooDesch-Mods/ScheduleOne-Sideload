using System.Globalization;
using System.Text;

namespace Sideload.Devtools.Cdp
{
    /// <summary>What a parsed JSON node holds.</summary>
    internal enum JsonKind { Null, Bool, Number, String, Array, Object }

    /// <summary>
    /// One parsed JSON node.
    ///
    /// A real recursive parser rather than the flat one in Script/MiniJson, because a protocol message nests:
    /// `Runtime.callFunctionOn` carries an array of argument objects, `DOM.setChildNodes` carries a tree. Reading a
    /// missing key yields <see cref="Nothing"/> instead of null, so a handler can walk a path it is not sure about
    /// without a null check at every step.
    ///
    /// System.Text.Json is avoided for the same reason Script/MiniJson avoids it: MelonLoader's runtime does not
    /// reliably ship it.
    /// </summary>
    internal sealed class JsonValue
    {
        /// <summary>The answer to every lookup that found nothing. Reads as an empty string, a zero and a false.</summary>
        internal static readonly JsonValue Nothing = new JsonValue { Kind = JsonKind.Null };

        /// <summary>How deep a message may nest. A protocol message is a handful of levels; anything beyond this is a
        /// malformed or hostile payload and must not be able to blow the parser's stack.</summary>
        private const int MaxDepth = 64;

        internal JsonKind Kind { get; private set; } = JsonKind.Null;

        internal string Text { get; private set; } = "";

        internal double Number { get; private set; }

        internal bool Flag { get; private set; }

        internal List<JsonValue> Items { get; private set; }

        internal Dictionary<string, JsonValue> Members { get; private set; }

        /// <summary>True when this is the placeholder for a key that was not in the message at all, which is not the
        /// same as a key whose value is JSON null.</summary>
        internal bool IsMissing => ReferenceEquals(this, Nothing);

        internal int Count => Items?.Count ?? 0;

        internal JsonValue this[string name] =>
            Members != null && name != null && Members.TryGetValue(name, out JsonValue value) ? value : Nothing;

        internal JsonValue this[int index] =>
            Items != null && index >= 0 && index < Items.Count ? Items[index] : Nothing;

        internal string AsString(string fallback = "") => Kind switch
        {
            JsonKind.String => Text,
            JsonKind.Number => Number.ToString(CultureInfo.InvariantCulture),
            JsonKind.Bool => Flag ? "true" : "false",
            _ => fallback,
        };

        internal double AsNumber(double fallback = 0) => Kind == JsonKind.Number ? Number : fallback;

        internal int AsInt(int fallback = 0) => Kind == JsonKind.Number ? (int)Number : fallback;

        internal bool AsBool(bool fallback = false) => Kind == JsonKind.Bool ? Flag : fallback;

        /// <summary>Parse a whole message. Never throws: a malformed frame yields <see cref="Nothing"/> and the
        /// caller answers with a protocol error rather than dropping the connection.</summary>
        internal static JsonValue Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Nothing;

            try
            {
                int i = 0;
                JsonValue value = ParseValue(text, ref i, 0);
                return value ?? Nothing;
            }
            catch
            {
                return Nothing;
            }
        }

        private static JsonValue ParseValue(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("json nested too deeply");

            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("json ended early");

            switch (s[i])
            {
                case '{': return ParseObject(s, ref i, depth);
                case '[': return ParseArray(s, ref i, depth);
                case '"': return new JsonValue { Kind = JsonKind.String, Text = ParseString(s, ref i) };
                case 't': Expect(s, ref i, "true"); return new JsonValue { Kind = JsonKind.Bool, Flag = true };
                case 'f': Expect(s, ref i, "false"); return new JsonValue { Kind = JsonKind.Bool, Flag = false };
                case 'n': Expect(s, ref i, "null"); return new JsonValue { Kind = JsonKind.Null };
                default: return ParseNumber(s, ref i);
            }
        }

        private static JsonValue ParseObject(string s, ref int i, int depth)
        {
            var result = new JsonValue { Kind = JsonKind.Object, Members = new Dictionary<string, JsonValue>(StringComparer.Ordinal) };
            i++;   // '{'

            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated object");
                if (s[i] == '}') { i++; return result; }
                if (s[i] == ',') { i++; continue; }
                if (s[i] != '"') throw new FormatException("expected a key");

                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':'");
                i++;

                result.Members[key] = ParseValue(s, ref i, depth + 1);
            }
        }

        private static JsonValue ParseArray(string s, ref int i, int depth)
        {
            var result = new JsonValue { Kind = JsonKind.Array, Items = new List<JsonValue>() };
            i++;   // '['

            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated array");
                if (s[i] == ']') { i++; return result; }
                if (s[i] == ',') { i++; continue; }

                result.Items.Add(ParseValue(s, ref i, depth + 1));
            }
        }

        private static JsonValue ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E')) i++;

            string raw = s.Substring(start, i - start);
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                throw new FormatException("bad number '" + raw + "'");

            return new JsonValue { Kind = JsonKind.Number, Number = number };
        }

        private static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;   // opening quote

            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();

                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;

                char escape = s[i++];
                switch (escape)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("truncated \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(escape); break;
                }
            }

            throw new FormatException("unterminated string");
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
                throw new FormatException("expected '" + literal + "'");
            i += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }
    }

    /// <summary>
    /// Writing JSON out.
    ///
    /// Every message shape the protocol uses is fixed, so the encoder appends already-encoded fragments instead of
    /// walking a model: fewer moving parts than a serializer, and a nested RemoteObject preview is built by the code
    /// that knows its shape rather than by a generic writer.
    /// </summary>
    internal static class Json
    {
        internal const string EmptyObject = "{}";

        internal static string Quote(string value)
        {
            var sb = new StringBuilder(value == null ? 2 : value.Length + 2);
            Quote(sb, value);
            return sb.ToString();
        }

        internal static void Quote(StringBuilder sb, string value)
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
                        // Anything below space, plus the two line separators, has to be escaped for a strict parser.
                        if (c < ' ' || c == '\u2028' || c == '\u2029')
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>A number the protocol can read back. Round-trippable and never in scientific notation for the
        /// small integers that ids and counts are.</summary>
        internal static string Number(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "null";
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>An array of fragments that are already JSON.</summary>
        internal static string Array(IEnumerable<string> encoded)
        {
            var sb = new StringBuilder("[");
            bool first = true;

            foreach (string item in encoded)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(item);
            }

            return sb.Append(']').ToString();
        }

        /// <summary>An object built member by member. Values go in already encoded, which is what lets a domain hand
        /// over a nested fragment it built itself.</summary>
        internal sealed class Obj
        {
            private readonly StringBuilder _sb = new StringBuilder("{");
            private bool _any;

            internal Obj Raw(string name, string encoded)
            {
                if (encoded == null) return this;

                if (_any) _sb.Append(',');
                _any = true;
                Json.Quote(_sb, name);
                _sb.Append(':').Append(encoded);
                return this;
            }

            internal Obj Str(string name, string value) => Raw(name, Json.Quote(value));

            internal Obj Num(string name, double value) => Raw(name, Json.Number(value));

            internal Obj Bool(string name, bool value) => Raw(name, value ? "true" : "false");

            /// <summary>Only writes the member when there is something to write - the protocol treats an absent
            /// optional and an empty one differently in a few places.</summary>
            internal Obj StrIf(string name, string value) => string.IsNullOrEmpty(value) ? this : Str(name, value);

            internal string Done() => _sb.Append('}').ToString();

            public override string ToString() => Done();
        }
    }
}
