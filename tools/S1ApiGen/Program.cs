using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace S1ApiGen;

/// <summary>
/// Writes the generated half of <c>s1api</c> from the S1API DLL.
///
/// What it offers, per public type of S1API:
///   - every public static property with a public getter, as a property: <c>s1api.timeManager.currentTime</c>;
///   - every public static parameterless method whose name says it reads (config readerPrefixes, extraReaders), as a
///     function: <c>s1api.money.getCashBalance()</c>;
///   - every public static event and static delegate field, as a topic: <c>s1api.on('timeManager.onHourPass', fn)</c>;
///   - the methods the config lists under "writes", and nothing else, as functions that change the game when the
///     app's mod allowed the scope.
///
/// Naming: the area is the class name in camelCase (TimeManager -> timeManager). Two classes with the same name in
/// different namespaces both take the last namespace segment in front. A member keeps its S1API name in camelCase.
/// An area named like a curated one (money, player, ...) joins it; a curated key wins every clash.
///
/// Values: scalars, enums (as their name), Unity value structs (as their fields), collections and S1API objects. An
/// S1API object is written with its public instance properties, two levels deep: a value read at the top is level 0,
/// an object inside it is level 1 and carries only scalars, enums, Unity structs and lists of those. Anything typed
/// as a game or Unity object (IL2CPP) is left out - no IL2CPP object reaches a page.
///
/// Every skipped member is written to the report with its reason. A rule this file cannot apply (an ambiguous write,
/// a name clash it cannot settle) stops the build instead of guessing.
/// </summary>
internal static class Program
{
    private const int MaxLevel = 1;

    private static int Main(string[] args)
    {
        try
        {
            var opts = Options.Parse(args);
            var gen = new Generator(opts);
            gen.Run();
            return 0;
        }
        catch (GenException e)
        {
            Console.Error.WriteLine("error S1APIGEN: " + e.Message);
            return 1;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("error S1APIGEN: the generator failed: " + e);
            return 1;
        }
    }

    internal sealed class GenException : Exception
    {
        public GenException(string message) : base(message) { }
    }

    internal sealed class Options
    {
        public string S1Api, Lib, Config, Cs, Surface, Dts, Report;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string value = i + 1 < args.Length ? args[i + 1] : null;
                switch (args[i])
                {
                    case "--s1api": o.S1Api = value; i++; break;
                    case "--lib": o.Lib = value; i++; break;
                    case "--config": o.Config = value; i++; break;
                    case "--cs": o.Cs = value; i++; break;
                    case "--surface": o.Surface = value; i++; break;
                    case "--dts": o.Dts = value; i++; break;
                    case "--report": o.Report = value; i++; break;
                    default: throw new GenException($"unknown argument '{args[i]}'.");
                }
            }

            if (o.S1Api == null || o.Lib == null || o.Config == null || o.Cs == null || o.Surface == null || o.Dts == null)
                throw new GenException("usage: --s1api <dll> --lib <dir> --config <json> --cs <out> --surface <out> --dts <out> [--report <out>]");
            if (!File.Exists(o.S1Api)) throw new GenException($"no S1API at '{o.S1Api}'.");
            if (!File.Exists(o.Config)) throw new GenException($"no config at '{o.Config}'.");
            return o;
        }
    }

    // ------------------------------------------------------------------ the model --

    /// <summary>How one value type is written: the C# method group that writes it, and its TypeScript type.</summary>
    internal sealed class Writer
    {
        public string Cs;
        public string Ts;
    }

    internal enum MemberKind { Property, Reader, Write }

    internal sealed class Member
    {
        public MemberKind Kind;
        public string Key;          // page-facing name
        public string Source;       // S1API full name, for comments and the report
        public string Expr;         // C# expression that reads it (Property, Reader)
        public Writer Writer;       // how the value is written (Property, Reader)
        public string Scope;        // AllowGameWrites scope (Write)
        public MethodInfo Method;   // the write target (Write)
        public string Method_;      // generated method name
    }

    internal sealed class Topic
    {
        public string Name;
        public string Source;
        public string Subscribe;    // C# target of +=
        public List<Writer> Args = new List<Writer>();
        public string Method_;
    }

    internal sealed class Area
    {
        public string Name;
        public Type Type;
        public bool Curated;
        public List<Member> Members = new List<Member>();
        public List<Topic> Topics = new List<Topic>();
        public readonly HashSet<string> Keys = new HashSet<string>(StringComparer.Ordinal);
        public string Method_;
    }

    internal sealed class Generator
    {
        private readonly Options _o;
        private readonly JsonElement _config;
        private readonly StringBuilder _methods = new StringBuilder();
        private readonly List<string> _report = new List<string>();
        private readonly Dictionary<(Type, int), Writer> _writers = new Dictionary<(Type, int), Writer>();
        private readonly Dictionary<(Type, int), string> _unsupported = new Dictionary<(Type, int), string>();
        private readonly HashSet<(Type, int)> _building = new HashSet<(Type, int)>();
        private readonly SortedDictionary<string, string> _tsTypes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, string> _schemaTypes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<Type, string> _typeNames = new Dictionary<Type, string>();
        private Assembly _s1api;
        private int _id;

        private HashSet<string> _excludeNamespaces, _exclude, _extraReaders;
        private string[] _readerPrefixes;
        private Dictionary<string, string> _writes;
        private Dictionary<string, HashSet<string>> _curatedKeys;
        private HashSet<string> _reserved;

        public Generator(Options o)
        {
            _o = o;
            using var doc = JsonDocument.Parse(File.ReadAllText(o.Config), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            _config = doc.RootElement.Clone();
        }

        public void Run()
        {
            ReadConfig();

            var paths = new List<string>();
            paths.AddRange(Directory.GetFiles(_o.Lib, "*.dll", SearchOption.AllDirectories));
            paths.AddRange(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            byName[Path.GetFileNameWithoutExtension(_o.S1Api)] = Path.GetFullPath(_o.S1Api);
            foreach (string p in paths) byName.TryAdd(Path.GetFileNameWithoutExtension(p), p);

            using var mlc = new MetadataLoadContext(new PathAssemblyResolver(byName.Values), "System.Private.CoreLib");
            _s1api = mlc.LoadFromAssemblyPath(Path.GetFullPath(_o.S1Api));

            Type[] exported = _s1api.GetExportedTypes();
            NameTypes(exported);

            List<Area> areas = CollectAreas(exported);

            string version = _s1api.GetName().Version?.ToString(3) ?? "?";
            Write(_o.Cs, EmitCs(areas, version));
            Write(_o.Surface, EmitSurface(areas));
            Write(_o.Dts, EmitDts(areas, version));
            if (_o.Report != null) Write(_o.Report, string.Join("\n", _report) + "\n");

            int members = areas.Sum(a => a.Members.Count);
            int topics = areas.Sum(a => a.Topics.Count);
            Console.WriteLine($"S1ApiGen: S1API {version} -> {areas.Count} areas, {members} members, {topics} topics, " +
                              $"{areas.Sum(a => a.Members.Count(m => m.Kind == MemberKind.Write))} writes; {_report.Count} skipped" +
                              (_o.Report != null ? $" (see {_o.Report})" : "") + ".");
        }

        private static void Write(string path, string text)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text.Replace("\r\n", "\n"), new UTF8Encoding(false));
        }

        private void ReadConfig()
        {
            static IEnumerable<string> Strings(JsonElement e, string name) =>
                e.TryGetProperty(name, out JsonElement a) ? a.EnumerateArray().Select(x => x.GetString()) : Array.Empty<string>();

            _excludeNamespaces = new HashSet<string>(Strings(_config, "excludeNamespaces"), StringComparer.Ordinal);
            _exclude = new HashSet<string>(Strings(_config, "exclude"), StringComparer.Ordinal);
            _extraReaders = new HashSet<string>(Strings(_config, "extraReaders"), StringComparer.Ordinal);
            _readerPrefixes = Strings(_config, "readerPrefixes").ToArray();

            _writes = new Dictionary<string, string>(StringComparer.Ordinal);
            if (_config.TryGetProperty("writes", out JsonElement writes))
                foreach (JsonProperty w in writes.EnumerateObject())
                    if (!w.Name.StartsWith("//", StringComparison.Ordinal)) _writes[w.Name] = w.Value.GetString();

            JsonElement curated = _config.GetProperty("curated");
            _curatedKeys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (JsonProperty area in curated.GetProperty("areas").EnumerateObject())
                _curatedKeys[area.Name] = new HashSet<string>(area.Value.EnumerateObject().Select(p => p.Name), StringComparer.Ordinal);
            foreach (JsonProperty area in curated.GetProperty("functions").EnumerateObject())
            {
                if (!_curatedKeys.TryGetValue(area.Name, out HashSet<string> keys))
                    throw new GenException($"curated.functions names '{area.Name}', which curated.areas does not have.");
                foreach (JsonElement k in area.Value.EnumerateArray()) keys.Add(k.GetString());
            }

            _reserved = new HashSet<string>(Strings(curated, "reserved"), StringComparer.Ordinal);
        }

        private void Skip(string member, string reason) => _report.Add($"skip {member}: {reason}");

        // ------------------------------------------------------------------ names --

        internal static string Camel(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int n = 0;
            while (n < s.Length && char.IsUpper(s[n])) n++;
            if (n == 0) return s;
            if (n == s.Length) return s.ToLowerInvariant();
            if (n > 1 && char.IsLower(s[n])) n--;
            return s.Substring(0, n).ToLowerInvariant() + s.Substring(n);
        }

        internal static string Pascal(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private static string LastSegment(string ns) => string.IsNullOrEmpty(ns) ? "" : ns.Substring(ns.LastIndexOf('.') + 1);

        private static string NestedName(Type t) => t.DeclaringType == null ? t.Name : NestedName(t.DeclaringType) + t.Name;

        /// <summary>TypeScript names for every S1API type: the simple name, or namespace segment + name where two
        /// types share one.</summary>
        private void NameTypes(Type[] exported)
        {
            foreach (IGrouping<string, Type> group in exported.Where(t => !t.IsGenericTypeDefinition).GroupBy(NestedName))
            {
                bool clash = group.Count() > 1;
                foreach (Type t in group)
                    _typeNames[t] = clash ? Pascal(LastSegment(t.Namespace)) + NestedName(t) : NestedName(t);
            }

            foreach (IGrouping<string, KeyValuePair<Type, string>> dup in _typeNames.GroupBy(p => p.Value).Where(g => g.Count() > 1))
                throw new GenException($"two S1API types would both be named '{dup.Key}' in TypeScript: " +
                                       string.Join(", ", dup.Select(p => p.Key.FullName)));
        }

        private string TsName(Type t) => _typeNames.TryGetValue(t, out string name) ? name : NestedName(t);

        internal static string CsName(Type t)
        {
            if (t.IsArray) return CsName(t.GetElementType()) + "[]";
            if (t.IsGenericType)
            {
                Type def = t.GetGenericTypeDefinition();
                string raw = def.FullName ?? def.Name;
                int tick = raw.IndexOf('`');
                if (tick >= 0) raw = raw.Substring(0, tick);
                return "global::" + raw.Replace('+', '.') + "<" + string.Join(", ", t.GetGenericArguments().Select(CsName)) + ">";
            }
            return "global::" + (t.FullName ?? t.Name).Replace('+', '.');
        }

        private static string CsString(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static string TsString(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

        // ------------------------------------------------------------------ type rules --

        private static bool IsObsolete(MemberInfo m) =>
            m.CustomAttributes.Any(a => a.AttributeType.FullName == "System.ObsoleteAttribute");

        private static bool IsBcl(Assembly a)
        {
            string name = a.GetName().Name ?? "";
            return name == "System.Private.CoreLib" || name == "mscorlib" || name == "netstandard" || name == "System.Runtime"
                   || name.StartsWith("System.", StringComparison.Ordinal);
        }

        private bool IsS1Api(Type t) => t.Assembly == _s1api;

        private static readonly HashSet<string> Scalars = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.String", "System.Boolean", "System.Char", "System.SByte", "System.Byte", "System.Int16",
            "System.UInt16", "System.Int32", "System.UInt32", "System.Int64", "System.UInt64", "System.Single",
            "System.Double", "System.Decimal", "System.Guid",
        };

        private static bool IsDelegate(Type t)
        {
            for (Type b = t.BaseType; b != null; b = b.BaseType)
                if (b.FullName == "System.MulticastDelegate") return true;
            return false;
        }

        /// <summary>The writer for a value of type <paramref name="t"/> read at <paramref name="level"/>, or null with the
        /// reason recorded in <see cref="_unsupported"/>.</summary>
        private Writer GetWriter(Type t, int level)
        {
            if (_writers.TryGetValue((t, level), out Writer known)) return known;
            if (_unsupported.ContainsKey((t, level))) return null;
            if (_building.Contains((t, level))) return Fail(t, level, "refers to itself");

            _building.Add((t, level));
            try
            {
                Writer w = BuildWriter(t, level);
                if (w != null) _writers[(t, level)] = w;
                return w;
            }
            finally
            {
                _building.Remove((t, level));
            }
        }

        private Writer Fail(Type t, int level, string reason)
        {
            _unsupported[(t, level)] = reason;
            return null;
        }

        internal string Why(Type t, int level) =>
            _unsupported.TryGetValue((t, level), out string why) ? $"{t.FullName}: {why}" : t.FullName;

        private Writer BuildWriter(Type t, int level)
        {
            if (t.IsByRef || t.IsPointer || t.IsGenericParameter) return Fail(t, level, "not a value");

            if (t.FullName != null && Scalars.Contains(t.FullName))
                return new Writer
                {
                    Cs = "global::Sideload.Integrations.S1Api.JsonOut.W" + t.Name,
                    Ts = t.FullName == "System.String" || t.FullName == "System.Char" || t.FullName == "System.Guid" ? "string"
                       : t.FullName == "System.Boolean" ? "boolean" : "number",
                };

            if (t.IsEnum) return EnumWriter(t);

            if (t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Nullable`1")
            {
                Type inner = t.GetGenericArguments()[0];
                Writer iw = GetWriter(inner, level);
                if (iw == null) return Fail(t, level, Why(inner, level));
                string name = "N_" + (++_id);
                _methods.Append($"        private static void {name}(JsonOut w, {CsName(t)} v)\n        {{\n" +
                                $"            if (v.HasValue) {iw.Cs}(w, v.Value); else w.Null();\n        }}\n\n");
                return new Writer { Cs = name, Ts = iw.Ts };
            }

            if (t.IsArray || IsBcl(t.Assembly)) return CollectionWriter(t, level);

            if (IsUnityStruct(t)) return UnityStructWriter(t);

            if (IsS1Api(t)) return ObjectWriter(t, level);

            return Fail(t, level, $"a game or Unity object ({t.Assembly.GetName().Name})");
        }

        private Writer EnumWriter(Type t)
        {
            bool flags = t.CustomAttributes.Any(a => a.AttributeType.FullName == "System.FlagsAttribute");
            string ts = "string";
            if (!flags && IsS1Api(t))
            {
                string name = TsName(t);
                string union = string.Join(" | ", t.GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => TsString(f.Name)));
                _tsTypes[name] = $"  type {name} = {(union.Length == 0 ? "string" : union)};\n";
                _schemaTypes[name] = JsonSerializer.Serialize(union.Length == 0 ? "string" : union);
                ts = "S1ApiGen." + name;
            }
            return new Writer { Cs = $"global::Sideload.Integrations.S1Api.JsonOut.WEnum<{CsName(t)}>", Ts = ts };
        }

        private Writer CollectionWriter(Type t, int level)
        {
            Type element = null;
            if (t.IsArray)
            {
                if (t.GetArrayRank() != 1) return Fail(t, level, "a multi-dimensional array");
                element = t.GetElementType();
            }
            else
            {
                IEnumerable<Type> candidates = new[] { t }.Concat(t.GetInterfaces());
                Type enumerable = candidates.FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition().FullName == "System.Collections.Generic.IEnumerable`1");
                if (enumerable == null) return Fail(t, level, "not a value the page can read");
                element = enumerable.GetGenericArguments()[0];
            }

            if (element.IsGenericType && element.GetGenericTypeDefinition().FullName == "System.Collections.Generic.KeyValuePair`2")
            {
                Type[] kv = element.GetGenericArguments();
                Type key = kv[0];
                bool keyOk = key.IsEnum || (key.FullName != null && Scalars.Contains(key.FullName) && key.FullName != "System.Boolean");
                if (!keyOk) return Fail(t, level, $"a dictionary keyed by {key.FullName}");
                Writer vw = GetWriter(kv[1], level);
                if (vw == null) return Fail(t, level, Why(kv[1], level));

                string name = "D_" + (++_id);
                _methods.Append($"        private static void {name}(JsonOut w, global::System.Collections.Generic.IEnumerable<{CsName(element)}> v) =>\n" +
                                $"            w.Dict(v, {vw.Cs}, {CsString("map of " + TsName(kv[1]))});\n\n");
                return new Writer { Cs = name, Ts = "{ [key: string]: " + vw.Ts + " }" };
            }

            Writer ew = GetWriter(element, level);
            if (ew == null) return Fail(t, level, "list of " + Why(element, level));

            string list = "L_" + (++_id);
            _methods.Append($"        private static void {list}(JsonOut w, global::System.Collections.Generic.IEnumerable<{CsName(element)}> v) =>\n" +
                            $"            w.List(v, {ew.Cs}, {CsString("list of " + TsName(element))});\n\n");
            string ts = ew.Ts.Contains(' ') && !ew.Ts.StartsWith("{", StringComparison.Ordinal) ? "(" + ew.Ts + ")[]" : ew.Ts + "[]";
            return new Writer { Cs = list, Ts = ts };
        }

        private static bool IsUnityStruct(Type t)
        {
            if (!t.IsValueType || t.IsEnum || t.IsPrimitive) return false;
            if (t.Namespace == null || !t.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal)) return false;
            FieldInfo[] fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
            return fields.Length > 0 && fields.Length <= 16 && fields.All(f => f.FieldType.IsPrimitive);
        }

        private Writer UnityStructWriter(Type t)
        {
            FieldInfo[] fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
            string name = "U_" + (++_id);
            var sb = new StringBuilder($"        private static void {name}(JsonOut w, {CsName(t)} v)\n        {{\n            w.Open();\n");
            foreach (FieldInfo f in fields)
                sb.Append($"            w.Key({CsString(f.Name)}); global::Sideload.Integrations.S1Api.JsonOut.W{f.FieldType.Name}(w, v.{f.Name});\n");
            sb.Append("            w.Close();\n        }\n\n");
            _methods.Append(sb);
            string ts = "{ " + string.Join("; ", fields.Select(f => f.Name + ": " + (f.FieldType.FullName == "System.Boolean" ? "boolean" : "number"))) + " }";
            return new Writer { Cs = name, Ts = ts };
        }

        private Writer ObjectWriter(Type t, int level)
        {
            if (level > MaxLevel) return Fail(t, level, "deeper than two levels");
            if (t.IsGenericType) return Fail(t, level, "a generic type");
            if (IsDelegate(t)) return Fail(t, level, "a delegate");
            if (t.IsAbstract && t.IsSealed) return Fail(t, level, "a static class");

            for (Type b = t.BaseType; b != null; b = b.BaseType)
                if (!IsS1Api(b) && !IsBcl(b.Assembly))
                    return Fail(t, level, $"derives from {b.FullName} (a game or Unity object)");

            // Properties declared by S1API itself, most derived first; a base's property of the same name is hidden.
            var props = new List<PropertyInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            IEnumerable<Type> chain = t.IsInterface
                ? new[] { t }.Concat(t.GetInterfaces().Where(IsS1Api))
                : Chain(t).Where(IsS1Api);
            foreach (Type owner in chain)
                foreach (PropertyInfo p in owner.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (seen.Add(Camel(p.Name))) props.Add(p);

            string tsName = TsName(t) + (level == 0 ? "" : "Brief");
            string name = "S_" + (++_id);
            string owner_ = CsName(t);
            var body = new StringBuilder();
            var ts = new StringBuilder();
            var schema = new List<string>();
            int count = 0;

            // Registered before the properties are walked so a property typed as its own owner finds a writer at
            // the next level rather than recursing.
            var writer = new Writer { Cs = name, Ts = "S1ApiGen." + tsName };
            _writers[(t, level)] = writer;

            foreach (PropertyInfo p in props)
            {
                string where = $"{t.FullName}.{p.Name}";
                MethodInfo getter = p.GetMethod;
                if (getter == null || !getter.IsPublic || p.GetIndexParameters().Length > 0) continue;
                if (IsObsolete(p)) { if (level == 0) Skip(where, "obsolete"); continue; }

                Writer pw = GetWriter(p.PropertyType, level + 1);
                if (pw == null)
                {
                    if (level == 0) Skip(where, Why(p.PropertyType, level + 1));
                    continue;
                }

                string key = Camel(p.Name);
                string pm = name + "_" + (++count);
                body.Append($"            w.Prop({CsString(key)}, v, {pm}, {CsString(TsName(t) + "." + key)});\n");
                _methods.Append($"        private static void {pm}(JsonOut w, {owner_} v) => {pw.Cs}(w, v.{p.Name});\n\n");
                ts.Append($"    {key}: {pw.Ts} | null;\n");
                schema.Add(JsonSerializer.Serialize(key) + ":" + JsonSerializer.Serialize(pw.Ts.Replace("S1ApiGen.", "")));
            }

            if (count == 0)
            {
                _writers.Remove((t, level));
                return Fail(t, level, "no property a page can read");
            }

            string nullCheck = t.IsValueType ? "" : "            if (v is null) { w.Null(); return; }\n";
            _methods.Append($"        private static void {name}(JsonOut w, {owner_} v)\n        {{\n{nullCheck}            w.Open();\n{body}            w.Close();\n        }}\n\n");

            _tsTypes[tsName] = $"  /** {t.FullName}{(level == 0 ? "" : ", one level down: scalars only")} */\n  interface {tsName} {{\n{ts}  }}\n";
            _schemaTypes[tsName] = "{" + string.Join(",", schema) + "}";
            return writer;
        }

        private static IEnumerable<Type> Chain(Type t)
        {
            for (Type b = t; b != null; b = b.BaseType) yield return b;
        }

        // ------------------------------------------------------------------ areas --

        private List<Area> CollectAreas(Type[] exported)
        {
            var candidates = new List<Area>();

            foreach (Type t in exported.OrderBy(x => x.FullName, StringComparer.Ordinal))
            {
                if (t.IsGenericTypeDefinition || t.Name.Contains('<') || IsDelegate(t) || t.IsEnum || t.IsInterface) continue;
                if (_excludeNamespaces.Any(ns => t.Namespace != null && (t.Namespace == ns || t.Namespace.StartsWith(ns + ".", StringComparison.Ordinal)))) continue;
                if (IsObsolete(t)) continue;

                var area = new Area { Type = t };
                CollectMembers(t, area);
                if (area.Members.Count + area.Topics.Count > 0) candidates.Add(area);
            }

            // Area names: the class name, or namespace segment + class name where two classes share one.
            foreach (IGrouping<string, Area> group in candidates.GroupBy(a => Camel(NestedName(a.Type))))
            {
                bool clash = group.Count() > 1;
                foreach (Area a in group)
                    a.Name = clash ? Camel(LastSegment(a.Type.Namespace) + NestedName(a.Type)) : group.Key;
            }

            foreach (IGrouping<string, Area> dup in candidates.GroupBy(a => a.Name).Where(g => g.Count() > 1))
                throw new GenException($"two S1API classes would both be the area '{dup.Key}': " + string.Join(", ", dup.Select(a => a.Type.FullName)));

            var areas = new List<Area>();
            foreach (Area a in candidates)
            {
                if (_reserved.Contains(a.Name))
                {
                    Skip(a.Type.FullName, $"its area name '{a.Name}' is a reserved s1api key");
                    continue;
                }

                a.Curated = _curatedKeys.ContainsKey(a.Name);
                if (a.Curated) a.Keys.UnionWith(_curatedKeys[a.Name]);
                else a.Keys.Add("get");

                var kept = new List<Member>();
                foreach (Member m in a.Members)
                {
                    if (a.Keys.Add(m.Key)) kept.Add(m);
                    else Skip(m.Source, $"'{a.Name}.{m.Key}' is already taken" + (a.Curated ? " by the curated area" : ""));
                }
                a.Members = kept;

                var topics = new List<Topic>();
                var topicNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Topic tp in a.Topics)
                {
                    tp.Name = a.Name + "." + tp.Name;
                    if (topicNames.Add(tp.Name)) topics.Add(tp);
                    else Skip(tp.Source, $"topic '{tp.Name}' is already taken");
                }
                a.Topics = topics;

                if (a.Members.Count + a.Topics.Count > 0) areas.Add(a);
            }

            return areas.OrderBy(a => a.Name, StringComparer.Ordinal).ToList();
        }

        private void CollectMembers(Type t, Area area)
        {
            const BindingFlags Static = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
            string owner = CsName(t);

            foreach (PropertyInfo p in t.GetProperties(Static))
            {
                string full = $"{t.FullName}.{p.Name}";
                MethodInfo getter = p.GetMethod;
                if (getter == null || !getter.IsPublic || !getter.IsStatic || p.GetIndexParameters().Length > 0) continue;
                if (_exclude.Contains(full)) { Skip(full, "excluded in the config"); continue; }
                if (IsObsolete(p)) { Skip(full, "obsolete"); continue; }

                Writer w = GetWriter(p.PropertyType, 0);
                if (w == null) { Skip(full, Why(p.PropertyType, 0)); continue; }

                area.Members.Add(new Member { Kind = MemberKind.Property, Key = Camel(p.Name), Source = full, Expr = $"{owner}.{p.Name}", Writer = w });
            }

            foreach (MethodInfo m in t.GetMethods(Static))
            {
                if (m.IsSpecialName) continue;
                string full = $"{t.FullName}.{m.Name}";

                if (_writes.TryGetValue(full, out _)) continue;   // handled below, once per name
                if (m.GetParameters().Length > 0 || m.IsGenericMethodDefinition || m.ReturnType.FullName == "System.Void") continue;

                bool reads = _extraReaders.Contains(full)
                             || _readerPrefixes.Any(pre => m.Name.Length > pre.Length && m.Name.StartsWith(pre, StringComparison.Ordinal) && char.IsUpper(m.Name[pre.Length]));
                if (!reads) { Skip(full + "()", "its name does not say it only reads (not a reader prefix, not in extraReaders)"); continue; }
                if (_exclude.Contains(full)) { Skip(full + "()", "excluded in the config"); continue; }
                if (IsObsolete(m)) { Skip(full + "()", "obsolete"); continue; }

                Writer w = GetWriter(m.ReturnType, 0);
                if (w == null) { Skip(full + "()", Why(m.ReturnType, 0)); continue; }

                area.Members.Add(new Member { Kind = MemberKind.Reader, Key = Camel(m.Name), Source = full + "()", Expr = $"{owner}.{m.Name}()", Writer = w });
            }

            foreach (KeyValuePair<string, string> write in _writes)
            {
                int dot = write.Key.LastIndexOf('.');
                if (dot < 0 || write.Key.Substring(0, dot) != t.FullName) continue;
                string name = write.Key.Substring(dot + 1);

                MethodInfo[] overloads = t.GetMethods(Static).Where(m => m.Name == name && !m.IsGenericMethodDefinition).ToArray();
                if (overloads.Length == 0) throw new GenException($"the write '{write.Key}' is not a public static method of S1API {_s1api.GetName().Version}.");

                MethodInfo[] usable = overloads.Where(m => m.GetParameters().All(p => ArgReader(p) != null)).ToArray();
                if (usable.Length == 0) throw new GenException($"the write '{write.Key}' takes parameters a page cannot pass (only numbers, booleans, strings and enums).");
                if (usable.Length > 1) throw new GenException($"the write '{write.Key}' has {usable.Length} overloads a page could call, and a page argument list cannot say which one it means.");

                area.Members.Add(new Member { Kind = MemberKind.Write, Key = Camel(name), Source = write.Key, Scope = write.Value, Method = usable[0] });
            }

            foreach (EventInfo e in t.GetEvents(Static))
            {
                string full = $"{t.FullName}.{e.Name}";
                if (e.AddMethod == null || !e.AddMethod.IsPublic) continue;
                AddTopic(area, full, e.Name, e.EventHandlerType, $"{owner}.{e.Name}");
            }

            foreach (FieldInfo f in t.GetFields(Static))
            {
                if (f.IsLiteral || !IsDelegate(f.FieldType)) continue;
                string full = $"{t.FullName}.{f.Name}";
                if (f.IsInitOnly) { Skip(full, "a read-only delegate field cannot be subscribed to"); continue; }
                AddTopic(area, full, f.Name, f.FieldType, $"{owner}.{f.Name}");
            }
        }

        private void AddTopic(Area area, string full, string name, Type handler, string target)
        {
            if (_exclude.Contains(full)) { Skip(full, "excluded in the config"); return; }

            MethodInfo invoke = handler.GetMethod("Invoke");
            if (invoke == null) { Skip(full, "its delegate has no Invoke"); return; }
            if (invoke.ReturnType.FullName != "System.Void") { Skip(full, "its handler returns a value"); return; }

            var topic = new Topic { Name = Camel(name), Source = full, Subscribe = target };
            foreach (ParameterInfo p in invoke.GetParameters())
            {
                if (p.ParameterType.IsByRef) { Skip(full, "its handler takes a ref or out parameter"); return; }
                Writer w = GetWriter(p.ParameterType, 0);
                if (w == null) Skip(full + " argument " + p.Name, Why(p.ParameterType, 0) + " - sent as null");
                topic.Args.Add(w);
            }
            area.Topics.Add(topic);
        }

        /// <summary>The Args call that turns a page argument into this parameter, or null when a page cannot pass it.</summary>
        private static string ArgReader(ParameterInfo p)
        {
            Type t = p.ParameterType;
            string name = CsString(Camel(p.Name ?? "arg"));
            string fallback = p.HasDefaultValue ? ", " + Literal(t, p.RawDefaultValue) : "";

            if (t.IsEnum) return $"global::Sideload.Integrations.S1Api.Args.Enum<{CsName(t)}>(a, {{0}}, {name}{fallback})";

            return t.FullName switch
            {
                "System.Boolean" => $"global::Sideload.Integrations.S1Api.Args.Bool(a, {{0}}, {name}{fallback})",
                "System.String" => $"global::Sideload.Integrations.S1Api.Args.Text(a, {{0}}, {name}{fallback})",
                "System.Single" => $"(float)global::Sideload.Integrations.S1Api.Args.Number(a, {{0}}, {name}{fallback})",
                "System.Double" => $"global::Sideload.Integrations.S1Api.Args.Number(a, {{0}}, {name}{fallback})",
                "System.Int32" => $"checked((int)global::Sideload.Integrations.S1Api.Args.Integer(a, {{0}}, {name}{fallback}))",
                "System.Int64" => $"global::Sideload.Integrations.S1Api.Args.Integer(a, {{0}}, {name}{fallback})",
                "System.Int16" => $"checked((short)global::Sideload.Integrations.S1Api.Args.Integer(a, {{0}}, {name}{fallback}))",
                "System.Byte" => $"checked((byte)global::Sideload.Integrations.S1Api.Args.Integer(a, {{0}}, {name}{fallback}))",
                _ => null,
            };
        }

        private static string Literal(Type t, object value)
        {
            if (value == null) return t.IsValueType ? "default" : "null";
            if (t.IsEnum) return $"({CsName(t)}){Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)}";
            return value switch
            {
                bool b => b ? "true" : "false",
                string s => CsString(s),
                float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d",
                double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d",
                IConvertible c => Convert.ToInt64(c, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => throw new GenException($"cannot write the default value '{value}' of type {t.FullName}."),
            };
        }

        private static string TsParam(ParameterInfo p)
        {
            Type t = p.ParameterType;
            string type = t.IsEnum ? "string" : t.FullName switch
            {
                "System.Boolean" => "boolean",
                "System.String" => "string",
                _ => "number",
            };
            return Camel(p.Name ?? "arg") + (p.HasDefaultValue ? "?" : "") + ": " + type;
        }

        // ------------------------------------------------------------------ C# --

        private string EmitCs(List<Area> areas, string version)
        {
            var register = new StringBuilder();
            int handlers = 0;

            foreach (Area a in areas)
            {
                foreach (Member m in a.Members)
                {
                    m.Method_ = (m.Kind == MemberKind.Write ? "W_" : "R_") + (++_id);
                    string path = a.Name + "." + m.Key;

                    if (m.Kind == MemberKind.Write)
                    {
                        ParameterInfo[] ps = m.Method.GetParameters();
                        string call = $"{CsName(a.Type)}.{m.Method.Name}(" +
                                      string.Join(", ", ps.Select((p, i) => string.Format(ArgReader(p), i))) + ")";
                        _methods.Append($"        /// <summary>{m.Source}, scope \"{m.Scope}\".</summary>\n" +
                                        $"        private static string {m.Method_}(global::System.Collections.Generic.IReadOnlyList<object> a)\n        {{\n" +
                                        $"            global::Sideload.Integrations.S1Api.Args.Count(a, {ps.Length});\n" +
                                        $"            {call};\n            return \"ok\";\n        }}\n\n");
                        register.Append($"            write({CsString(path)}, {CsString(m.Scope)}, {m.Method_});\n");
                    }
                    else
                    {
                        _methods.Append($"        /// <summary>{m.Source}</summary>\n" +
                                        $"        private static string {m.Method_}()\n        {{\n" +
                                        $"            var w = new JsonOut();\n            {m.Writer.Cs}(w, {m.Expr});\n            return w.ToString();\n        }}\n\n");
                        register.Append($"            read({CsString(path)}, {m.Method_});\n");
                    }
                    handlers++;
                }

                if (!a.Curated)
                {
                    a.Method_ = "A_" + (++_id);
                    var body = new StringBuilder();
                    foreach (Member m in a.Members.Where(x => x.Kind == MemberKind.Property))
                        body.Append($"            w.Member({CsString(m.Key)}, {CsString(a.Name + "." + m.Key)}, {m.Method_});\n");
                    _methods.Append($"        /// <summary>Every property of {a.Type.FullName} in one read.</summary>\n" +
                                    $"        private static string {a.Method_}()\n        {{\n            var w = new JsonOut();\n            w.Open();\n{body}            w.Close();\n            return w.ToString();\n        }}\n\n");
                    register.Append($"            read({CsString(a.Name)}, {a.Method_});\n");
                    handlers++;
                }

                foreach (Topic tp in a.Topics)
                {
                    tp.Method_ = "E_" + (++_id);
                    string ps = string.Join(", ", tp.Args.Select((_, i) => "a" + i));
                    string payload;
                    if (tp.Args.Count == 0) payload = "() => \"null\"";
                    else if (tp.Args.Count == 1)
                        payload = tp.Args[0] == null ? "() => \"null\"" : $"() => {{ var w = new JsonOut(); {tp.Args[0].Cs}(w, a0); return w.ToString(); }}";
                    else
                    {
                        var sb = new StringBuilder("() => { var w = new JsonOut(); w.OpenArray(); ");
                        for (int i = 0; i < tp.Args.Count; i++)
                            sb.Append(tp.Args[i] == null ? "w.Sep(); w.Null(); " : $"w.Sep(); {tp.Args[i].Cs}(w, a{i}); ");
                        payload = sb.Append("w.CloseArray(); return w.ToString(); }").ToString();
                    }

                    _methods.Append($"        /// <summary>{tp.Source}</summary>\n" +
                                    $"        private static void {tp.Method_}()\n        {{\n" +
                                    $"            {tp.Subscribe} += ({ps}) => Entry.Push({CsString(tp.Name)}, {payload});\n        }}\n\n");
                    register.Append($"            topic({CsString(tp.Name)}, {tp.Method_});\n");
                }
            }

            string schema = EmitSchema(areas, version);

            return
$@"// <auto-generated>
// Written by tools/S1ApiGen from S1API {version} on every build whose S1API or rules changed. Do not edit: change
// tools/S1ApiGen/s1api-gen.json or the generator instead.
// </auto-generated>
#pragma warning disable CS0612, CS0618

namespace Sideload.Integrations.S1Api
{{
    /// <summary>
    /// The generated half of s1api. <see cref=""Register""/> only creates delegates: no method below is compiled until
    /// a page reads it, so a member a newer or older S1API lacks fails alone, inside Entry's guard, and costs that
    /// member.
    /// </summary>
    internal static class Generated
    {{
        internal const string S1ApiVersion = {CsString(version)};

        internal const string Schema = {CsString(schema)};

        /// <summary>Hands every generated handler to Entry. Returns how many there are.</summary>
        internal static int Register(global::System.Action<string, global::System.Func<string>> read,
                                     global::System.Action<string, string, global::System.Func<global::System.Collections.Generic.IReadOnlyList<object>, string>> write,
                                     global::System.Action<string, global::System.Action> topic)
        {{
{register}            return {handlers};
        }}

{_methods.ToString().TrimEnd()}
    }}
}}
";
        }

        // ------------------------------------------------------------------ schema --

        private string EmitSchema(List<Area> areas, string version)
        {
            JsonElement curated = _config.GetProperty("curated");
            var sb = new StringBuilder();
            sb.Append("{\"version\":2,\"s1api\":").Append(JsonSerializer.Serialize(version)).Append(",\"areas\":{");

            var byName = areas.ToDictionary(a => a.Name, StringComparer.Ordinal);
            bool firstArea = true;

            void Members(Area a, bool any)
            {
                foreach (Member m in a.Members)
                {
                    if (any) sb.Append(',');
                    any = true;
                    string key = m.Kind == MemberKind.Property ? m.Key
                        : m.Kind == MemberKind.Reader ? m.Key + "()"
                        : m.Key + "(" + string.Join(", ", m.Method.GetParameters().Select(TsParam)) + ")";
                    string type = m.Kind == MemberKind.Write ? "boolean" : m.Writer.Ts.Replace("S1ApiGen.", "");
                    sb.Append(JsonSerializer.Serialize(key)).Append(':').Append(JsonSerializer.Serialize(type));
                }
            }

            foreach (JsonProperty area in curated.GetProperty("areas").EnumerateObject())
            {
                if (!firstArea) sb.Append(',');
                firstArea = false;
                sb.Append(JsonSerializer.Serialize(area.Name)).Append(":{");
                bool any = false;
                foreach (JsonProperty p in area.Value.EnumerateObject())
                {
                    if (any) sb.Append(',');
                    any = true;
                    sb.Append(JsonSerializer.Serialize(p.Name)).Append(':').Append(p.Value.GetRawText());
                }
                if (byName.TryGetValue(area.Name, out Area merged)) Members(merged, any);
                sb.Append('}');
            }

            foreach (Area a in areas.Where(x => !x.Curated))
            {
                if (!firstArea) sb.Append(',');
                firstArea = false;
                sb.Append(JsonSerializer.Serialize(a.Name)).Append(":{");
                Members(a, false);
                sb.Append('}');
            }

            sb.Append("},\"types\":{");
            sb.Append(string.Join(",", _schemaTypes.Select(p => JsonSerializer.Serialize(p.Key) + ":" + p.Value)));
            sb.Append("},\"topics\":[");
            var topics = curated.GetProperty("topics").EnumerateArray().Select(x => x.GetString())
                .Concat(areas.SelectMany(a => a.Topics).Select(tp => tp.Name));
            sb.Append(string.Join(",", topics.Select(x => JsonSerializer.Serialize(x))));
            sb.Append("],\"payloads\":{");
            sb.Append(string.Join(",", areas.SelectMany(a => a.Topics).Select(tp => JsonSerializer.Serialize(tp.Name) + ":" + JsonSerializer.Serialize(PayloadTs(tp).Replace("S1ApiGen.", "")))));
            sb.Append("},\"writes\":{");
            var writes = curated.GetProperty("writes").EnumerateObject().Select(p => JsonSerializer.Serialize(p.Name) + ":" + p.Value.GetRawText())
                .Concat(areas.SelectMany(a => a.Members.Where(m => m.Kind == MemberKind.Write).Select(m => JsonSerializer.Serialize(a.Name + "." + m.Key) + ":" + JsonSerializer.Serialize(m.Scope))));
            sb.Append(string.Join(",", writes));
            sb.Append("}}");

            using (JsonDocument.Parse(sb.ToString())) { }   // throws here, at build time, rather than in a page
            return sb.ToString();
        }

        private static string PayloadTs(Topic tp)
        {
            if (tp.Args.Count == 0) return "null";
            if (tp.Args.Count == 1) return tp.Args[0]?.Ts ?? "null";
            return "[" + string.Join(", ", tp.Args.Select(w => w == null ? "null" : w.Ts + " | null")) + "]";
        }

        // ------------------------------------------------------------------ surface --

        /// <summary>What the page prelude builds the generated areas from: per area, its properties (p), reading
        /// functions (m) and writes (w). Embedded in Sideload.dll so a page without S1API has the same objects.</summary>
        private static string EmitSurface(List<Area> areas)
        {
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (Area a in areas.Where(x => x.Members.Count > 0 || !x.Curated))
            {
                if (!first) sb.Append(',');
                first = false;
                string Keys(MemberKind kind) => "[" + string.Join(",", a.Members.Where(m => m.Kind == kind).Select(m => JsonSerializer.Serialize(m.Key))) + "]";
                sb.Append(JsonSerializer.Serialize(a.Name))
                  .Append(":{\"p\":").Append(Keys(MemberKind.Property))
                  .Append(",\"m\":").Append(Keys(MemberKind.Reader))
                  .Append(",\"w\":").Append(Keys(MemberKind.Write))
                  .Append('}');
            }
            return sb.Append("}\n").ToString();
        }

        // ------------------------------------------------------------------ TypeScript --

        private string EmitDts(List<Area> areas, string version)
        {
            var ns = new StringBuilder();
            var merged = new StringBuilder();
            var top = new StringBuilder();
            var topics = new StringBuilder();

            static string Doc(Member m, string indent) =>
                $"{indent}/** {m.Source}{(m.Kind == MemberKind.Write ? $" - true only when the app's mod called `AllowGameWrites(\"{m.Scope}\")`" : "")} */\n";

            static string Fn(Member m, string indent) => m.Kind == MemberKind.Reader
                ? $"{indent}{m.Key}(): {m.Writer.Ts} | null;\n"
                : $"{indent}{m.Key}({string.Join(", ", m.Method.GetParameters().Select(TsParam))}): boolean;\n";

            foreach (Area a in areas)
            {
                foreach (Topic tp in a.Topics)
                    topics.Append($"  /** {tp.Source} */\n  {TsString(tp.Name)}: {PayloadTs(tp)};\n");

                if (a.Curated)
                {
                    if (a.Members.Count == 0) continue;
                    merged.Append($"interface S1Api{Pascal(a.Name)}Area {{\n");
                    foreach (Member m in a.Members)
                    {
                        merged.Append(Doc(m, "  "));
                        merged.Append(m.Kind == MemberKind.Property ? $"  readonly {m.Key}: {m.Writer.Ts} | null;\n" : Fn(m, "  "));
                    }
                    merged.Append("}\n\n");
                    continue;
                }

                var data = new StringBuilder();
                var fns = new StringBuilder();
                foreach (Member m in a.Members)
                {
                    if (m.Kind == MemberKind.Property) data.Append(Doc(m, "    ")).Append($"    {m.Key}: {m.Writer.Ts} | null;\n");
                    else fns.Append(Doc(m, "    ")).Append(Fn(m, "    "));
                }

                string pascal = Pascal(a.Name);
                foreach (string clash in new[] { pascal + "Data", pascal + "Area" })
                    if (_tsTypes.ContainsKey(clash)) throw new GenException($"the TypeScript name '{clash}' is both an area and an S1API type.");

                ns.Append($"  /** Every property of {a.Type.FullName}, as `s1api.{a.Name}.get()` returns it. */\n")
                  .Append($"  interface {pascal}Data {{\n{data}  }}\n\n")
                  .Append($"  /** s1api.{a.Name}: {a.Type.FullName} */\n")
                  .Append($"  interface {pascal}Area extends Readonly<{pascal}Data> {{\n")
                  .Append($"    /** Every property in one read, or null. */\n    get(): {pascal}Data | null;\n")
                  .Append(fns)
                  .Append("  }\n\n");
                top.Append($"  readonly {a.Name}: S1ApiGen.{pascal}Area;\n");
            }

            return
$@"// `s1api`, the part generated from S1API {version}. Do not edit: a Sideload build rewrites this file from the S1API it
// is built against (tools/S1ApiGen). The hand-written part is s1api.d.ts, which references this file.
//
// Every static property, reading method and event of S1API is here under the class name in camelCase:
// S1API.GameTime.TimeManager.CurrentTime is `s1api.timeManager.currentTime`. Objects come two levels deep; a type
// ending in Brief is the second level and carries scalars only. Every value can be null: before a save has loaded,
// without S1API, or when the member is missing from the S1API the player has.

declare namespace S1ApiGen {{
{string.Concat(_tsTypes.Values.Select(v => v + "\n"))}{ns.ToString().TrimEnd()}
}}

{merged}interface S1Api {{
{top}}}

interface S1ApiTopics {{
{topics}}}
";
        }
    }
}
