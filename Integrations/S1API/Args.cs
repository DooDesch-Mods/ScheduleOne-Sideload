using System.Globalization;
using System.Text;

namespace Sideload.Integrations.S1Api
{
    /// <summary>
    /// The arguments of a generated write. The page sends them as one JSON array of scalars
    /// (<c>s1api.money.changeCashBalance(50, false)</c> -> <c>[50,false]</c>); this parses it and hands each parameter
    /// its value. A missing argument takes the method's own default, a missing one without a default, a wrong type or
    /// a number that is not finite throws ArgumentException - Entry's guard reports it and the page gets false.
    /// </summary>
    internal static class Args
    {
        internal static IReadOnlyList<object> Parse(string json)
        {
            var list = new List<object>();
            if (string.IsNullOrWhiteSpace(json)) return list;

            int i = 0;
            Space(json, ref i);
            Expect(json, ref i, '[');
            Space(json, ref i);
            if (i < json.Length && json[i] == ']') return list;

            while (true)
            {
                Space(json, ref i);
                list.Add(Value(json, ref i));
                Space(json, ref i);
                if (i >= json.Length) throw new ArgumentException("the argument list ends early.");
                if (json[i] == ',') { i++; continue; }
                if (json[i] == ']') { i++; break; }
                throw new ArgumentException($"unexpected '{json[i]}' in the argument list.");
            }

            Space(json, ref i);
            if (i != json.Length) throw new ArgumentException("text after the argument list.");
            return list;
        }

        internal static void Count(IReadOnlyList<object> a, int max)
        {
            if (a.Count > max) throw new ArgumentException($"{a.Count} arguments, the method takes {max}.");
        }

        internal static double Number(IReadOnlyList<object> a, int i, string name) => Number(Get(a, i, name), name);
        internal static double Number(IReadOnlyList<object> a, int i, string name, double fallback) => Given(a, i) ? Number(a[i], name) : fallback;

        internal static long Integer(IReadOnlyList<object> a, int i, string name) => Integer(Get(a, i, name), name);
        internal static long Integer(IReadOnlyList<object> a, int i, string name, long fallback) => Given(a, i) ? Integer(a[i], name) : fallback;

        internal static bool Bool(IReadOnlyList<object> a, int i, string name) => Bool(Get(a, i, name), name);
        internal static bool Bool(IReadOnlyList<object> a, int i, string name, bool fallback) => Given(a, i) ? Bool(a[i], name) : fallback;

        internal static string Text(IReadOnlyList<object> a, int i, string name) => Text(Get(a, i, name), name);
        internal static string Text(IReadOnlyList<object> a, int i, string name, string fallback) => Given(a, i) ? Text(a[i], name) : fallback;

        internal static T Enum<T>(IReadOnlyList<object> a, int i, string name) where T : struct, System.Enum => Enum<T>(Get(a, i, name), name);
        internal static T Enum<T>(IReadOnlyList<object> a, int i, string name, T fallback) where T : struct, System.Enum => Given(a, i) ? Enum<T>(a[i], name) : fallback;

        // ------------------------------------------------------------------ conversion --

        private static bool Given(IReadOnlyList<object> a, int i) => i < a.Count && a[i] != null;

        private static object Get(IReadOnlyList<object> a, int i, string name) =>
            Given(a, i) ? a[i] : throw new ArgumentException($"'{name}' is missing.");

        private static double Number(object v, string name)
        {
            if (v is double d && !double.IsNaN(d) && !double.IsInfinity(d)) return d;
            throw new ArgumentException($"'{name}' is not a finite number.");
        }

        private static long Integer(object v, string name)
        {
            double d = Number(v, name);
            if (d != Math.Floor(d) || d < long.MinValue || d > long.MaxValue) throw new ArgumentException($"'{name}' is not a whole number.");
            return (long)d;
        }

        private static bool Bool(object v, string name) =>
            v is bool b ? b : throw new ArgumentException($"'{name}' is not true or false.");

        private static string Text(object v, string name) =>
            v is string s ? s : throw new ArgumentException($"'{name}' is not a string.");

        private static T Enum<T>(object v, string name) where T : struct, System.Enum
        {
            if (v is string s && System.Enum.TryParse(s, true, out T named) && System.Enum.IsDefined(typeof(T), named)) return named;
            if (v is double d && d == Math.Floor(d))
            {
                object raw = Convert.ChangeType((long)d, System.Enum.GetUnderlyingType(typeof(T)), CultureInfo.InvariantCulture);
                if (System.Enum.IsDefined(typeof(T), raw)) return (T)System.Enum.ToObject(typeof(T), raw);
            }
            throw new ArgumentException($"'{name}' is not one of {string.Join(", ", System.Enum.GetNames(typeof(T)))}.");
        }

        // ------------------------------------------------------------------ parsing --

        private static void Space(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c) throw new ArgumentException($"expected '{c}' in the argument list.");
            i++;
        }

        private static object Value(string s, ref int i)
        {
            if (i >= s.Length) throw new ArgumentException("the argument list ends early.");
            char c = s[i];
            if (c == '"') return Str(s, ref i);
            if (Word(s, ref i, "true")) return true;
            if (Word(s, ref i, "false")) return false;
            if (Word(s, ref i, "null")) return null;

            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i > start && double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
            throw new ArgumentException("only numbers, strings, true, false and null can be passed.");
        }

        private static bool Word(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        private static string Str(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length || !int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                            throw new ArgumentException("a broken \\u escape in a string argument.");
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new ArgumentException("a string argument is not closed.");
        }
    }
}
