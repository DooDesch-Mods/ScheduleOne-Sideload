using Jint;
using Jint.Native;

namespace Sideload.Script
{
    /// <summary>
    /// Promises the host creates and settles later, from C#, on a frame of its own choosing.
    ///
    /// Two things in Jint 3.1.5 make this less obvious than it looks, both measured against this build:
    ///
    /// 1. <c>Engine.Advanced.RegisterPromise()</c> - the API that appears to be for exactly this - is tied to
    ///    <c>ExecuteWithEventLoop</c>. With one of its promises outstanding, a script containing an <c>await</c>
    ///    never returns from <c>Execute</c>. The promise is therefore built in JavaScript, where it is an ordinary
    ///    <c>new Promise</c> with nothing special about it, and the resolve/reject functions are kept on the C# side.
    /// 2. The engine's time budget is reset only by an entry point (<c>Execute</c>, <c>Evaluate</c>, <c>Invoke</c>),
    ///    at entry AND at exit. <c>ProcessTasks()</c> is not one, so a continuation drained more than 250 ms after the
    ///    last script call dies with a TimeoutException before it runs a single statement. Settling goes through
    ///    <c>Engine.Invoke</c>, and <see cref="Pump"/> resets the budget itself - which is what "250 ms per handler"
    ///    was always meant to say.
    ///
    /// Not covered, and it cannot be: <c>await</c> on a promise the host settles on a LATER frame deadlocks the game.
    /// Jint implements <c>await</c> as <c>UnwrapIfPromise</c>, which blocks the calling thread on a wait handle - the
    /// same thread that would have settled the promise. Pages use <c>.then()</c>; see ARCHITECTURE.md section 5.
    /// </summary>
    internal sealed class Promises
    {
        /// <summary>
        /// Builds one deferred and instruments it so a rejection nobody in the chain took is reported instead of
        /// vanishing. Jint has no unhandled-rejection hook, and a silently swallowed error is the single most
        /// expensive thing an app author can be handed.
        ///
        /// The rule is "nothing is attached below this promise". Each promise remembers whether the page ever chained
        /// off it; the watcher fires only on the one at the END of a chain, so `fetch().then(a).then(b)` with no catch
        /// reports exactly once, at b, and `.catch(c)` anywhere below stays quiet.
        /// </summary>
        private const string Factory = @"(function (report) {
  var settle, fail;
  var root = new Promise(function (ok, no) { settle = ok; fail = no; });

  function watch(p) {
    var chained = false;
    var then = p.then.bind(p);

    p.then    = function (ok, no) { chained = true; return watch(then(ok, no)); };
    p.catch   = function (no)     { chained = true; return watch(then(undefined, no)); };
    p.finally = function (fn)     { chained = true; return watch(then(
                                      function (v) { fn(); return v; },
                                      function (e) { fn(); throw e; })); };

    then(undefined, function (err) {
      if (!chained) report(String(err && err.message ? err.message : err));
    });
    return p;
  }

  return {
    promise: watch(root),
    resolve: settle,
    reject: function (message) { fail(new Error(message)); }
  };
})";

        private readonly Engine _engine;
        private readonly JsValue _factory;
        private readonly JsValue _report;

        internal Promises(Engine engine, Action<string> onUnhandledRejection)
        {
            _engine = engine;
            _factory = engine.Evaluate(Factory);
            _report = JsValue.FromObject(engine, new Action<string>(m => onUnhandledRejection?.Invoke(m)));
        }

        /// <summary>A fresh pending promise plus the two handles that settle it. The page may hold it for as many
        /// frames as it likes.</summary>
        internal Deferred Create()
        {
            Jint.Native.Object.ObjectInstance parts = _engine.Invoke(_factory, _report).AsObject();
            return new Deferred(_engine, parts.Get("promise"), parts.Get("resolve"), parts.Get("reject"));
        }

        /// <summary>
        /// One frame of promise work: give the engine a fresh time budget, then run every continuation that became
        /// runnable since the last frame. Called from <see cref="ScriptHost.Tick"/> and nowhere else.
        /// </summary>
        internal void Pump()
        {
            _engine.Constraints.Reset();
            _engine.Advanced.ProcessTasks();
        }
    }

    /// <summary>A promise handed to the page, with the host's end of it kept here.</summary>
    internal sealed class Deferred
    {
        private readonly Engine _engine;
        private readonly JsValue _resolve;
        private readonly JsValue _reject;

        internal Deferred(Engine engine, JsValue promise, JsValue resolve, JsValue reject)
        {
            _engine = engine;
            Promise = promise;
            _resolve = resolve;
            _reject = reject;
        }

        /// <summary>The value to hand back to the script.</summary>
        internal JsValue Promise { get; }

        /// <summary>Settle it. Must run on the main thread; the continuations it queues run in the same frame's
        /// <see cref="Promises.Pump"/>.</summary>
        internal void Resolve(JsValue value) => _engine.Invoke(_resolve, value ?? JsValue.Undefined);

        /// <summary>Reject with an <c>Error</c> carrying this message, so `catch (e) { e.message }` reads as it does
        /// in a browser.</summary>
        internal void Reject(string message) => _engine.Invoke(_reject, message ?? "");
    }
}
