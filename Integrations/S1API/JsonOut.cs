using System.Globalization;
using System.Text;

namespace Sideload.Integrations.S1Api
{
    /// <summary>
    /// The JSON writer the generated readers write through (Generated.cs, from tools/S1ApiGen).
    ///
    /// Commas are decided by the last character written, not by state: a value never follows '{' or '[' with one. That
    /// is what lets <see cref="Prop{T}"/> cut a half-written property back off when its read throws and write null in
    /// its place, so one member a different S1API lacks costs that member and not the object around it.
    ///
    /// Numbers are written in the invariant culture and rounded to four places, the same as Entry's curated areas: a
    /// German Windows would otherwise write 12,5, and a float read as double would carry digits nobody asked for.
    /// </summary>
    internal sealed class JsonOut
    {
        /// <summary>A list longer than this is cut, with one warning per list kind. Large enough for every list the
        /// game holds today (items, NPCs, products); small enough that a runaway read cannot stall a frame.</summary>
        internal const int MaxItems = 1000;

        private readonly StringBuilder _sb = new StringBuilder();

        public override string ToString() => _sb.ToString();

        internal void Null() => _sb.Append("null");
        internal void Open() => _sb.Append('{');
        internal void Close() => _sb.Append('}');
        internal void OpenArray() => _sb.Append('[');
        internal void CloseArray() => _sb.Append(']');

        /// <summary>A comma, unless this is the first entry of the object or array just opened.</summary>
        internal void Sep()
        {
            if (_sb.Length == 0) return;
            char last = _sb[_sb.Length - 1];
            if (last != '{' && last != '[') _sb.Append(',');
        }

        internal void Key(string name)
        {
            Sep();
            _sb.Append(Quote(name)).Append(':');
        }

        /// <summary>One property of an object. A read that throws leaves "name":null and one warning.</summary>
        internal void Prop<T>(string name, T owner, Action<JsonOut, T> write, string what)
        {
            int mark = _sb.Length;
            Key(name);
            try
            {
                write(this, owner);
            }
            catch (Exception e)
            {
                _sb.Length = mark;
                Key(name);
                Null();
                Entry.Report(what, e);
            }
        }

        /// <summary>One member of a whole-area read, through the member's own guarded handler.</summary>
        internal void Member(string name, string what, Func<string> read)
        {
            Key(name);
            _sb.Append(Entry.Guard(what, read));
        }

        internal void List<T>(IEnumerable<T> items, Action<JsonOut, T> write, string what)
        {
            if (items == null) { Null(); return; }

            OpenArray();
            int n = 0;
            foreach (T item in items)
            {
                if (n++ == MaxItems)
                {
                    Entry.Report(what, new InvalidOperationException($"more than {MaxItems} entries - the rest is left out."));
                    break;
                }
                Sep();
                write(this, item);
            }
            CloseArray();
        }

        internal void Dict<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>> items, Action<JsonOut, TValue> write, string what)
        {
            if (items == null) { Null(); return; }

            Open();
            int n = 0;
            foreach (KeyValuePair<TKey, TValue> item in items)
            {
                if (item.Key == null) continue;
                if (n++ == MaxItems)
                {
                    Entry.Report(what, new InvalidOperationException($"more than {MaxItems} entries - the rest is left out."));
                    break;
                }
                Key(Convert.ToString(item.Key, CultureInfo.InvariantCulture) ?? "");
                write(this, item.Value);
            }
            Close();
        }

        // ------------------------------------------------------------------ scalars --
        // One method per CLR type, named W + the type's name, so the generator can name a method group for any of them.

        internal static void WString(JsonOut w, string v) { if (v == null) w.Null(); else w._sb.Append(Quote(v)); }
        internal static void WChar(JsonOut w, char v) => w._sb.Append(Quote(v.ToString()));
        internal static void WBoolean(JsonOut w, bool v) => w._sb.Append(v ? "true" : "false");
        internal static void WSByte(JsonOut w, sbyte v) => w._sb.Append(v.ToString(CultureInfo.InvariantCulture));
        internal static void WByte(JsonOut w, byte v) => w._sb.Append(v.ToString(CultureInfo.InvariantCulture));
        internal static void WInt16(JsonOut w, short v) => w._sb.Append(v.ToString(CultureInfo.InvariantCulture));
        internal static void WUInt16(JsonOut w, ushort v) => w._sb.Append(v.ToString(CultureInfo.InvariantCulture));
        internal static void WInt32(JsonOut w, int v) => w._sb.Append(v.ToString(CultureInfo.InvariantCulture));
        internal static void WUInt32(JsonOut w, uint v) => w._sb.Append(v.ToString(CultureInfo.InvariantCulture));
        internal static void WInt64(JsonOut w, long v) => w._sb.Append(v.ToString(CultureInfo.InvariantCulture));
        internal static void WUInt64(JsonOut w, ulong v) => w._sb.Append(v.ToString(CultureInfo.InvariantCulture));
        internal static void WSingle(JsonOut w, float v) => WDouble(w, v);
        internal static void WDecimal(JsonOut w, decimal v) => WDouble(w, (double)v);
        internal static void WGuid(JsonOut w, Guid v) => w._sb.Append(Quote(v.ToString()));

        internal static void WDouble(JsonOut w, double v) =>
            w._sb.Append(double.IsNaN(v) || double.IsInfinity(v)
                ? "null"
                : Math.Round(v, 4).ToString("R", CultureInfo.InvariantCulture));

        /// <summary>An enum as its name, the way the curated areas write Day and Rank.</summary>
        internal static void WEnum<T>(JsonOut w, T v) where T : struct, Enum => w._sb.Append(Quote(v.ToString()));

        internal static string Quote(string value)
        {
            var sb = new StringBuilder(value.Length + 2).Append('"');
            foreach (char c in value)
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
            return sb.Append('"').ToString();
        }
    }
}
