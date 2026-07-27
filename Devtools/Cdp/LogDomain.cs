using Jint;
using Jint.Native;

namespace Sideload.Devtools.Cdp
{
    /// <summary>
    /// The page's console, in DevTools.
    ///
    /// `console.log` and friends already go to the MelonLoader log; this mirrors them onto every attached window as
    /// `Runtime.consoleAPICalled`, with the arguments as real RemoteObjects so an object logged from the page can be
    /// expanded instead of read as text. Uncaught script errors take the other road, `Log.entryAdded`, so they are
    /// not confused with something the page chose to print.
    ///
    /// A short history is kept per page, because DevTools is usually opened after the interesting line was already
    /// logged. Replayed lines carry their text only - the values they referred to are long gone.
    /// </summary>
    internal static class LogDomain
    {
        /// <summary>How many lines are kept for a window that attaches later.</summary>
        private const int HistorySize = 200;

        private static readonly Dictionary<string, List<Entry>> _history = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);

        private readonly struct Entry
        {
            internal Entry(string level, string text, double at) { Level = level; Text = text; At = at; }

            internal string Level { get; }

            internal string Text { get; }

            internal double At { get; }
        }

        internal static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>Keep a line for whoever attaches next.</summary>
        internal static void Record(string targetId, string level, string text)
        {
            if (!_history.TryGetValue(targetId, out List<Entry> lines))
                _history[targetId] = lines = new List<Entry>();

            lines.Add(new Entry(level, text, Now));
            if (lines.Count > HistorySize) lines.RemoveRange(0, lines.Count - HistorySize);
        }

        /// <summary>Everything the page logged before this window attached.</summary>
        internal static void Replay(CdpSession session)
        {
            if (!_history.TryGetValue(session.TargetId, out List<Entry> lines)) return;

            foreach (Entry entry in lines)
            {
                string argument = new Json.Obj().Str("type", "string").Str("value", entry.Text).Done();
                session.EmitAfterReply(entry.Level == "exception" ? "Log.entryAdded" : "Runtime.consoleAPICalled",
                    entry.Level == "exception"
                        ? EntryJson(session, "error", entry.Text, entry.At)
                        : CallJson(TypeOf(entry.Level), new List<string> { argument }, entry.At));
            }
        }

        /// <summary>One live console call, with its arguments intact.</summary>
        internal static void Console(CdpSession session, Engine engine, string level, object[] args, string text)
        {
            if (level == "exception")
            {
                if (session.LogEnabled) session.Emit("Log.entryAdded", EntryJson(session, "error", text, Now));
                return;
            }

            if (!session.RuntimeEnabled) return;

            var encoded = new List<string>();

            if (args != null && args.Length > 0 && engine != null)
            {
                foreach (object argument in args)
                {
                    // Jint hands the console plain CLR values; going back through the engine gives a JavaScript
                    // value again, which is what the console can render and expand.
                    JsValue value;
                    try { value = JsValue.FromObject(engine, argument); }
                    catch { value = new JsString(argument?.ToString() ?? "null"); }

                    encoded.Add(Remote.Describe(value, session.Objects, "console"));
                }
            }
            else
            {
                encoded.Add(new Json.Obj().Str("type", "string").Str("value", text).Done());
            }

            session.Emit("Runtime.consoleAPICalled", CallJson(TypeOf(level), encoded, Now));
        }

        internal static void Forget(string targetId) => _history.Remove(targetId);

        internal static void Clear() => _history.Clear();

        private static string CallJson(string type, List<string> args, double at) =>
            new Json.Obj()
                .Str("type", type)
                .Raw("args", Json.Array(args))
                .Num("executionContextId", RuntimeDomain.ContextId)
                .Num("timestamp", at)
                .Done();

        private static string EntryJson(CdpSession session, string level, string text, double at) =>
            new Json.Obj()
                .Raw("entry", new Json.Obj()
                    .Str("source", "javascript")
                    .Str("level", level)
                    .Str("text", text)
                    .Num("timestamp", at)
                    .Str("url", Targets.UrlOf(Targets.Find(session.TargetId)))
                    .Done())
                .Done();

        /// <summary>The console method name as the protocol spells it.</summary>
        private static string TypeOf(string level) => level switch
        {
            "warn" => "warning",
            "warning" => "warning",
            "error" => "error",
            "info" => "info",
            "debug" => "debug",
            _ => "log",
        };
    }
}
