using System.Collections.Concurrent;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Sideload.Net;

namespace Sideload.Script
{
    /// <summary>
    /// The `fetch` global: the page's only way off the machine, and the only asynchronous thing it can do.
    ///
    /// The request runs on a thread-pool thread and the promise settles on the main thread in the next
    /// <see cref="ScriptHost.Tick"/>. That split is the whole point - a frame is never spent waiting on a socket, and
    /// no continuation ever runs while the DOM is half-rebuilt.
    ///
    /// Which hosts a page may reach is decided by the mod that registered it (<see cref="HostAllowlist"/>), never by
    /// the page. Default is deny.
    /// </summary>
    internal sealed class FetchApi
    {
        /// <summary>Requests in flight per page. A page that calls fetch from a render loop would otherwise queue one
        /// per frame; the cap turns that mistake into a readable rejection instead of a thread-pool full of sockets.</summary>
        private const int MaxInFlight = 8;

        /// <summary>Builds the Response the page sees. Written in JavaScript because `headers.get()` and the promises
        /// from `text()`/`json()` are script objects - a CLR wrapper would expose CLR shapes instead.</summary>
        private const string ResponseFactory = @"(function (status, statusText, url, headerLines, body, redirected) {
  var map = {};
  var lines = headerLines ? headerLines.split('\n') : [];
  for (var i = 0; i < lines.length; i++) {
    var at = lines[i].indexOf(':');
    if (at > 0) map[lines[i].substring(0, at).trim().toLowerCase()] = lines[i].substring(at + 1).trim();
  }

  return {
    ok: status >= 200 && status < 300,
    status: status,
    statusText: statusText,
    url: url,
    redirected: redirected,
    headers: {
      get: function (name) { var k = String(name).toLowerCase(); return k in map ? map[k] : null; },
      has: function (name) { return String(name).toLowerCase() in map; }
    },
    // Already-settled promises: the body was read before the page was handed the response, so these match the web's
    // shape without ever being pending.
    text: function () { return Promise.resolve(body); },
    json: function () {
      try { return Promise.resolve(JSON.parse(body)); }
      catch (e) { return Promise.reject(e); }
    }
  };
})";

        /// <summary>Gives `fetch` its web signature - one required argument, an optional init - without relying on how
        /// a CLR delegate behaves when the page passes fewer arguments than it declares.</summary>
        private const string Wrapper = @"(function (call) {
  return function fetch(url, init) {
    return call(url === undefined || url === null ? '' : String(url), init);
  };
})";

        /// <summary>Finished requests waiting for the main thread. Written from the thread pool, drained in
        /// <see cref="Settle"/>.</summary>
        private readonly ConcurrentQueue<Action> _finished = new ConcurrentQueue<Action>();

        private readonly Engine _engine;
        private readonly Promises _promises;
        private readonly Action<string> _onError;
        private readonly string _appId;

        private JsValue _responseFactory;
        private int _inFlight;

        internal FetchApi(Engine engine, string appId, Promises promises, Action<string> onError)
        {
            _engine = engine;
            _appId = appId ?? "";
            _promises = promises;
            _onError = onError;
        }

        /// <summary>Define `fetch` on the page's global object.</summary>
        internal void Install()
        {
            _responseFactory = _engine.Evaluate(ResponseFactory);

            JsValue host = JsValue.FromObject(_engine, new Func<string, JsValue, JsValue>(Call));
            _engine.SetValue("fetch", _engine.Invoke(_engine.Evaluate(Wrapper), host));
        }

        /// <summary>Requests whose answer has arrived but has not been handed to the script yet.</summary>
        internal int Ready => _finished.Count;

        /// <summary>
        /// Hand every finished request to its promise. Main thread, once per frame, before the continuations are
        /// drained - so a response that landed while the game was rendering settles in this frame rather than the next.
        /// </summary>
        internal void Settle()
        {
            while (_finished.TryDequeue(out Action settle))
            {
                try { settle(); }
                catch (Exception e) { _onError?.Invoke("settling a fetch failed: " + e.Message); }
            }
        }

        private JsValue Call(string url, JsValue init)
        {
            Deferred deferred = _promises.Create();

            var call = new FetchCall { AppId = _appId };

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri parsed))
            {
                // A page has no document URL, so there is nothing for a relative path to be relative to.
                Later(() => deferred.Reject($"fetch needs an absolute http(s) URL - got '{Trim(url)}'."));
                return deferred.Promise;
            }

            call.Url = parsed;

            try { ReadInit(init, call); }
            catch (Exception e)
            {
                Later(() => deferred.Reject("the second argument to fetch is not usable: " + e.Message));
                return deferred.Promise;
            }

            // Checked here as well as in Fetcher so a blocked call never reaches the thread pool at all, and so the
            // page gets the same message whether the block is on the first hop or a later one.
            if (!HostAllowlist.Allows(_appId, parsed, out string reason))
            {
                HostAllowlist.ReportOnce(_appId, parsed, reason);
                Later(() => deferred.Reject(reason));
                return deferred.Promise;
            }

            if (_inFlight >= MaxInFlight)
            {
                Later(() => deferred.Reject($"too many requests at once - this app already has {MaxInFlight} " +
                                            "fetches in flight. Wait for one to settle before starting another."));
                return deferred.Promise;
            }

            _inFlight++;
            Fetcher.Send(call, outcome => _finished.Enqueue(() =>
            {
                _inFlight--;
                if (outcome.Failed) deferred.Reject(outcome.Error);
                else deferred.Resolve(Response(outcome));
            }));

            return deferred.Promise;
        }

        /// <summary>Settle on the next frame rather than inside the fetch call. A promise that is already rejected
        /// when the page gets it behaves differently from one that rejects a moment later, and only the second shape
        /// is worth writing an app against.</summary>
        private void Later(Action settle) => _finished.Enqueue(settle);

        private JsValue Response(FetchOutcome outcome) => _engine.Invoke(_responseFactory,
            outcome.Status, outcome.StatusText, outcome.Url, outcome.HeaderLines, outcome.Body, outcome.Redirected);

        /// <summary>Read the `init` object: method, headers, body, and a per-call timeout. Anything else a browser
        /// accepts (mode, credentials, cache, signal) has no meaning here and is ignored rather than rejected.</summary>
        private static void ReadInit(JsValue init, FetchCall call)
        {
            if (init == null || init.IsUndefined() || init.IsNull() || !init.IsObject()) return;

            ObjectInstance options = init.AsObject();

            JsValue method = options.Get("method");
            if (!method.IsUndefined() && !method.IsNull()) call.Method = method.ToString();

            JsValue body = options.Get("body");
            if (!body.IsUndefined() && !body.IsNull()) call.Body = body.ToString();

            JsValue timeout = options.Get("timeout");
            if (timeout.IsNumber()) call.TimeoutMs = (int)timeout.AsNumber();

            JsValue headers = options.Get("headers");
            if (headers.IsObject())
            {
                ObjectInstance map = headers.AsObject();
                foreach (KeyValuePair<JsValue, Jint.Runtime.Descriptors.PropertyDescriptor> pair in map.GetOwnProperties())
                {
                    JsValue value = map.Get(pair.Key);
                    if (value.IsUndefined() || value.IsNull()) continue;

                    call.Headers.Add(new KeyValuePair<string, string>(pair.Key.ToString(), value.ToString()));
                }
            }
        }

        private static string Trim(string url) =>
            url != null && url.Length > 80 ? url.Substring(0, 80) + "..." : url ?? "";
    }
}
