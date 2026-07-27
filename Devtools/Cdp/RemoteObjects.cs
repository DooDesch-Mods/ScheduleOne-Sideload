using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;

namespace Sideload.Devtools.Cdp
{
    /// <summary>
    /// The objects a session has handed to DevTools by reference.
    ///
    /// A RemoteObject for anything that is not a primitive carries an `objectId` instead of a value, and the frontend
    /// sends that id back when the developer expands the row or calls a method on it. The values have to stay alive
    /// and stay addressable until the frontend lets them go, which is what this is for. One store per session, so
    /// closing a DevTools window releases everything it was holding.
    /// </summary>
    internal sealed class ObjectStore
    {
        private readonly Dictionary<string, Entry> _byId = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private int _next = 1;

        private readonly struct Entry
        {
            internal Entry(JsValue value, string group) { Value = value; Group = group; }

            internal JsValue Value { get; }

            internal string Group { get; }
        }

        internal string Add(JsValue value, string group)
        {
            string id = "sl:" + _next++;
            _byId[id] = new Entry(value, group ?? "");
            return id;
        }

        internal bool TryGet(string id, out JsValue value)
        {
            value = null;
            if (string.IsNullOrEmpty(id) || !_byId.TryGetValue(id, out Entry entry)) return false;

            value = entry.Value;
            return true;
        }

        internal void Release(string id)
        {
            if (!string.IsNullOrEmpty(id)) _byId.Remove(id);
        }

        internal void ReleaseGroup(string group)
        {
            if (string.IsNullOrEmpty(group)) return;

            var doomed = new List<string>();
            foreach (KeyValuePair<string, Entry> pair in _byId)
                if (string.Equals(pair.Value.Group, group, StringComparison.Ordinal)) doomed.Add(pair.Key);

            foreach (string id in doomed) _byId.Remove(id);
        }

        internal void Clear() => _byId.Clear();
    }

    /// <summary>
    /// Turning a Jint value into what the console renders.
    ///
    /// This is the difference between a console that shows `{count: 2, items: Array(3)}` and one that shows
    /// "[object]": the protocol asks for a tagged shape (type, subtype, className, description) plus, for anything
    /// expandable, an inline preview of its first few members. Everything here reads live JavaScript values, so it
    /// only ever runs on the main thread.
    /// </summary>
    internal static class Remote
    {
        /// <summary>How many members go into an inline preview before it is marked as overflowing. Chrome uses five;
        /// matching it keeps the console line the length a developer expects.</summary>
        private const int PreviewMembers = 5;

        /// <summary>Longest string shown inside a preview before it is cut. The full value is still one click away.</summary>
        private const int PreviewStringLength = 100;

        /// <summary>Cap on a `returnByValue` result, so a page that hands back a huge structure cannot turn one
        /// console line into a multi-megabyte frame.</summary>
        private const int ByValueDepth = 5;

        /// <summary>A RemoteObject for one value. Primitives carry their value; everything else is registered in the
        /// store and carries an id plus, when asked for, a preview.</summary>
        internal static string Describe(JsValue value, ObjectStore store, string group = "console",
                                        bool byValue = false, bool preview = true)
        {
            if (value == null || value.IsUndefined()) return new Json.Obj().Str("type", "undefined").Done();

            if (value.IsNull())
                return new Json.Obj().Str("type", "object").Str("subtype", "null").Raw("value", "null").Done();

            if (value.IsBoolean())
                return new Json.Obj().Str("type", "boolean").Bool("value", value.AsBoolean()).Done();

            if (value.IsNumber())
            {
                double number = value.AsNumber();

                // NaN and the infinities have no JSON spelling, and the protocol has a field for exactly that.
                if (double.IsNaN(number) || double.IsInfinity(number))
                    return new Json.Obj()
                        .Str("type", "number")
                        .Str("unserializableValue", double.IsNaN(number) ? "NaN" : number > 0 ? "Infinity" : "-Infinity")
                        .Str("description", double.IsNaN(number) ? "NaN" : number > 0 ? "Infinity" : "-Infinity")
                        .Done();

                return new Json.Obj()
                    .Str("type", "number")
                    .Num("value", number)
                    .Str("description", Json.Number(number))
                    .Done();
            }

            if (value.IsString())
                return new Json.Obj().Str("type", "string").Str("value", value.AsString()).Done();

            if (value.IsSymbol())
                return new Json.Obj().Str("type", "symbol").Str("description", Text(value)).Str("objectId", store.Add(value, group)).Done();

            if (byValue)
            {
                string plain = ToJson(value, 0);
                return new Json.Obj().Str("type", "object").Raw("value", plain).Done();
            }

            return DescribeObject(value, store, group, preview);
        }

        private static string DescribeObject(JsValue value, ObjectStore store, string group, bool preview)
        {
            bool callable = IsCallable(value);
            bool array = IsArray(value);
            string className = ClassOf(value, callable, array);
            string description = DescribeText(value, callable, array, className);

            var obj = new Json.Obj()
                .Str("type", callable ? "function" : "object")
                .StrIf("subtype", array ? "array" : IsError(value) ? "error" : null)
                .Str("className", className)
                .Str("description", description)
                .Str("objectId", store.Add(value, group));

            if (preview && !callable) obj.Raw("preview", Preview(value, array, description));
            return obj.Done();
        }

        /// <summary>The inline `{a: 1, b: 2}` the console prints next to an expandable row.</summary>
        private static string Preview(JsValue value, bool array, string description)
        {
            var members = new List<string>();
            bool overflow = false;

            try
            {
                ObjectInstance instance = value.AsObject();
                int seen = 0;

                foreach (KeyValuePair<JsValue, PropertyDescriptor> pair in instance.GetOwnProperties())
                {
                    if (!pair.Key.IsString()) continue;

                    string name = pair.Key.AsString();
                    if (array && name == "length") continue;

                    if (seen++ >= PreviewMembers) { overflow = true; break; }

                    JsValue member = Read(instance, pair.Key);
                    members.Add(new Json.Obj()
                        .Str("name", name)
                        .Str("type", TypeOf(member))
                        .StrIf("subtype", IsArray(member) ? "array" : member != null && member.IsNull() ? "null" : null)
                        .Str("value", Short(member))
                        .Done());
                }
            }
            catch (Exception e)
            {
                // A getter that throws must not take the whole console line down with it.
                members.Add(new Json.Obj().Str("name", "<preview>").Str("type", "string").Str("value", e.Message).Done());
            }

            return new Json.Obj()
                .Str("type", "object")
                .StrIf("subtype", array ? "array" : null)
                .Str("description", description)
                .Bool("overflow", overflow)
                .Raw(array ? "properties" : "properties", Json.Array(members))
                .Done();
        }

        /// <summary>The rows behind an expanded object: `Runtime.getProperties`.</summary>
        internal static string Properties(JsValue value, ObjectStore store, string group)
        {
            var rows = new List<string>();
            if (value == null || !value.IsObject()) return Json.Array(rows);

            ObjectInstance instance = value.AsObject();

            foreach (KeyValuePair<JsValue, PropertyDescriptor> pair in instance.GetOwnProperties())
            {
                if (!pair.Key.IsString()) continue;

                JsValue member = Read(instance, pair.Key);
                rows.Add(new Json.Obj()
                    .Str("name", pair.Key.AsString())
                    .Raw("value", Describe(member, store, group, byValue: false, preview: true))
                    .Bool("writable", true)
                    .Bool("configurable", true)
                    .Bool("enumerable", true)
                    .Bool("isOwn", true)
                    .Done());
            }

            return Json.Array(rows);
        }

        /// <summary>What DevTools shows in red when an evaluation threw.</summary>
        internal static string ExceptionDetails(Exception error, ObjectStore store, string group)
        {
            string text = "Uncaught";
            string encoded;

            if (error is JavaScriptException js)
            {
                // The thrown value itself, so the console can expand a real Error object.
                encoded = Describe(js.Error, store, group);
                text = "Uncaught " + ErrorHead(js.Error, js.Message);
            }
            else
            {
                encoded = new Json.Obj()
                    .Str("type", "object")
                    .Str("subtype", "error")
                    .Str("className", error.GetType().Name)
                    .Str("description", error.Message)
                    .Done();
                text = "Uncaught " + error.Message;
            }

            return new Json.Obj()
                .Num("exceptionId", 1)
                .Str("text", text)
                .Num("lineNumber", LineOf(error))
                .Num("columnNumber", 0)
                .Raw("exception", encoded)
                .Done();
        }

        private static int LineOf(Exception error) =>
            error is JavaScriptException js ? Math.Max(js.Location.Start.Line - 1, 0) : 0;

        /// <summary>A JavaScript value as plain JSON, for `returnByValue`. Cycles are cut at the depth limit rather
        /// than tracked, which is enough for the console and cannot loop.</summary>
        internal static string ToJson(JsValue value, int depth)
        {
            if (value == null || value.IsUndefined() || value.IsNull()) return "null";
            if (value.IsBoolean()) return value.AsBoolean() ? "true" : "false";
            if (value.IsNumber()) return Json.Number(value.AsNumber());
            if (value.IsString()) return Json.Quote(value.AsString());
            if (depth >= ByValueDepth || !value.IsObject()) return Json.Quote(Text(value));

            try
            {
                ObjectInstance instance = value.AsObject();

                if (IsArray(value))
                {
                    var items = new List<string>();
                    int length = (int)Math.Min(instance.Get("length").AsNumber(), 1000);
                    for (int i = 0; i < length; i++) items.Add(ToJson(instance.Get(i.ToString()), depth + 1));
                    return Json.Array(items);
                }

                var obj = new Json.Obj();
                foreach (KeyValuePair<JsValue, PropertyDescriptor> pair in instance.GetOwnProperties())
                {
                    if (!pair.Key.IsString()) continue;
                    obj.Raw(pair.Key.AsString(), ToJson(Read(instance, pair.Key), depth + 1));
                }
                return obj.Done();
            }
            catch (Exception e)
            {
                return Json.Quote("<" + e.Message + ">");
            }
        }

        /// <summary>The other direction: a `CallArgument` from the frontend into a value the engine can take.</summary>
        internal static JsValue FromJson(Engine engine, JsonValue json)
        {
            switch (json.Kind)
            {
                case JsonKind.Bool: return json.Flag ? JsBoolean.True : JsBoolean.False;
                case JsonKind.Number: return JsNumber.Create(json.Number);
                case JsonKind.String: return new JsString(json.Text);
                case JsonKind.Array:
                {
                    var items = new List<JsValue>();
                    for (int i = 0; i < json.Count; i++) items.Add(FromJson(engine, json[i]));
                    return new JsArray(engine, items.ToArray());
                }
                case JsonKind.Object:
                {
                    ObjectInstance instance = new JsObject(engine);
                    if (json.Members != null)
                        foreach (KeyValuePair<string, JsonValue> pair in json.Members)
                            instance.Set(pair.Key, FromJson(engine, pair.Value));
                    return instance;
                }
                default: return JsValue.Null;
            }
        }

        // ------------------------------------------------------------------ shapes --

        private static JsValue Read(ObjectInstance instance, JsValue key)
        {
            // Through Get rather than the descriptor's Value, so an accessor reports what it actually returns. A
            // getter that throws is reported as its message instead of failing the whole response.
            try { return instance.Get(key); }
            catch (Exception e) { return new JsString("<" + e.Message + ">"); }
        }

        private static string TypeOf(JsValue value)
        {
            if (value == null || value.IsUndefined()) return "undefined";
            if (value.IsNull()) return "object";
            if (value.IsBoolean()) return "boolean";
            if (value.IsNumber()) return "number";
            if (value.IsString()) return "string";
            if (value.IsSymbol()) return "symbol";
            return IsCallable(value) ? "function" : "object";
        }

        /// <summary>The one-line form a preview row shows.</summary>
        private static string Short(JsValue value)
        {
            if (value == null || value.IsUndefined()) return "undefined";
            if (value.IsNull()) return "null";
            if (value.IsNumber()) return Json.Number(value.AsNumber());
            if (value.IsBoolean()) return value.AsBoolean() ? "true" : "false";

            if (value.IsString())
            {
                string text = value.AsString();
                return text.Length > PreviewStringLength ? text.Substring(0, PreviewStringLength) + "..." : text;
            }

            bool callable = IsCallable(value);
            bool array = IsArray(value);
            return DescribeText(value, callable, array, ClassOf(value, callable, array));
        }

        private static string DescribeText(JsValue value, bool callable, bool array, string className)
        {
            if (array)
            {
                try { return "Array(" + (int)value.AsObject().Get("length").AsNumber() + ")"; }
                catch { return "Array"; }
            }

            if (callable)
            {
                string source = Text(value);
                return string.IsNullOrEmpty(source) ? "function" : source.Length > 120 ? source.Substring(0, 120) + "..." : source;
            }

            if (IsError(value))
            {
                string head = ErrorHead(value, className);
                string stack = Stack(value);

                // Jint's `stack` is the frames only, where Chrome's begins with "TypeError: message" - and that first
                // line is exactly what the console shows on the collapsed row. Put it back.
                return string.IsNullOrEmpty(stack) ? head : head + "\n" + stack;
            }

            return className;
        }

        /// <summary>The first line of an error the way a console prints it: "TypeError: nope".</summary>
        private static string ErrorHead(JsValue error, string fallback)
        {
            try
            {
                ObjectInstance instance = error.AsObject();
                string name = Text(instance.Get("name"));
                string message = Text(instance.Get("message"));

                if (string.IsNullOrEmpty(name) || name == "undefined") return string.IsNullOrEmpty(message) ? fallback : message;
                return string.IsNullOrEmpty(message) || message == "undefined" ? name : name + ": " + message;
            }
            catch { return fallback; }
        }

        private static string Stack(JsValue error)
        {
            try
            {
                string stack = Text(error.AsObject().Get("stack"));
                return string.IsNullOrEmpty(stack) || stack == "undefined" ? "" : stack.TrimEnd();
            }
            catch { return ""; }
        }

        private static string ClassOf(JsValue value, bool callable, bool array)
        {
            if (array) return "Array";
            if (callable) return "Function";

            try
            {
                JsValue constructor = value.AsObject().Get("constructor");
                if (constructor != null && constructor.IsObject())
                {
                    string name = Text(constructor.AsObject().Get("name"));
                    if (!string.IsNullOrEmpty(name) && name != "undefined") return name;
                }
            }
            catch { /* an object with no prototype has no constructor to ask */ }

            return "Object";
        }

        private static bool IsError(JsValue value)
        {
            if (value == null || !value.IsObject()) return false;

            try
            {
                ObjectInstance instance = value.AsObject();
                return !instance.Get("stack").IsUndefined() && !instance.Get("message").IsUndefined();
            }
            catch { return false; }
        }

        private static bool IsCallable(JsValue value)
        {
            try { return value != null && value.IsObject() && value is Jint.Native.Function.Function; }
            catch { return false; }
        }

        private static bool IsArray(JsValue value)
        {
            try { return value != null && value.IsArray(); }
            catch { return false; }
        }

        private static string Text(JsValue value)
        {
            try { return value == null ? "" : value.ToString(); }
            catch { return ""; }
        }
    }
}
