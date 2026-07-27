using System.Net;
using System.Net.WebSockets;
using System.Text;
using Jint;
using Sideload.Host;
using Sideload.Script;

namespace Sideload.Devtools.Cdp
{
    /// <summary>
    /// A Chrome DevTools Protocol server for the pages this mod renders.
    ///
    /// DevTools is a client of a protocol, not of Chrome's renderer - the same reason `node --inspect` gives you the
    /// full DevTools UI over a socket. So the frontend Chrome already ships is pointed at this server, and it shows
    /// what this server tells it: the page's console, its document, and a prompt wired into the page's own script
    /// engine.
    ///
    /// Shape follows Snitch's data server, which is proven under this runtime: HttpListener on loopback, a background
    /// accept thread, and every piece of work marshalled onto Unity's main thread (see <see cref="MainThread"/>).
    /// Nothing here touches a WebView, a document or the Jint engine from a socket thread.
    ///
    /// Off unless the developer turns it on: <see cref="Config.Preferences.DevTools"/> defaults to false, so a
    /// shipped build opens no port.
    /// </summary>
    internal static class CdpServer
    {
        /// <summary>How many DevTools windows may be attached at once. More than a couple means something is
        /// reconnecting in a loop, not that a developer needs them.</summary>
        private const int MaxSessions = 4;

        private static readonly List<CdpSession> _sessions = new List<CdpSession>();
        private static readonly object _lock = new object();

        /// <summary>The document and script engine each target had last frame, so a reload can be spotted and the
        /// attached windows told to start over.</summary>
        private static readonly Dictionary<string, Snapshot> _seen = new Dictionary<string, Snapshot>(StringComparer.Ordinal);

        private static HttpListener _listener;
        private static Thread _accept;
        private static CancellationTokenSource _cancel;
        private static volatile bool _running;
        private static int _port;
        private static bool _announced;

        private sealed class Snapshot
        {
            internal object Document;
            internal object Script;
        }

        internal static bool Running => _running;

        internal static int Port => _port;

        internal static void Start(int port)
        {
            if (_running) return;

            _port = port;

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();

                _running = true;
                _cancel = new CancellationTokenSource();
                _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "Sideload-DevTools" };
                _accept.Start();

                // The console has to be mirrored from the moment the server is up, not from the moment a window
                // attaches, or everything a page logs while it starts is lost.
                ScriptHost.Diagnostics = OnDiagnostic;

                Core.Log?.Msg($"[Sideload/cdp] devtools protocol server on http://127.0.0.1:{port}/ " +
                              "- open that page and click a target, or add it under chrome://inspect > Configure.");

                // Only now that the feature is definitely in use: this can download the frontend, and nothing should
                // be fetched for a developer who never turned DevTools on. It returns immediately either way.
                FrontendCache.Resolve();
            }
            catch (Exception e)
            {
                _running = false;
                Core.Log?.Error($"[Sideload/cdp] the devtools server could not start on {port}: {e.Message} " +
                                "(port already in use? change Sideload's DevToolsPort)");
            }
        }

        internal static void Stop()
        {
            if (!_running && _listener == null) return;

            _running = false;
            ScriptHost.Diagnostics = null;

            try { _cancel?.Cancel(); } catch { /* nothing was waiting */ }

            lock (_lock)
            {
                foreach (CdpSession session in _sessions) session.Dispose();
                _sessions.Clear();
            }

            try { _listener?.Stop(); _listener?.Close(); } catch { /* already down */ }
            try { _cancel?.Dispose(); } catch { /* already disposed */ }

            _listener = null;
            _cancel = null;
            _seen.Clear();
            LogDomain.Clear();
            MainThread.Clear();
        }

        /// <summary>
        /// One frame's worth of protocol work, from the mod's update loop: run whatever the socket threads queued,
        /// notice a page that has been rebuilt from disk, and open the developer's browser the first time a page
        /// actually exists.
        /// </summary>
        internal static void Pump()
        {
            if (!_running) return;

            MainThread.Pump();
            Announce();
            NoticeReloads();
        }

        // ------------------------------------------------------------------ sessions --

        private static List<CdpSession> SessionsFor(string targetId)
        {
            var matching = new List<CdpSession>();

            lock (_lock)
                foreach (CdpSession session in _sessions)
                    if (session.Alive && string.Equals(session.TargetId, targetId, StringComparison.Ordinal))
                        matching.Add(session);

            return matching;
        }

        /// <summary>
        /// A page whose document or script engine has been replaced - a reload, from disk or from `Page.reload` - has
        /// invalidated every id an attached window is holding. Telling it so is the whole contract: it asks for the
        /// document again and re-runs whatever it had open.
        /// </summary>
        private static void NoticeReloads()
        {
            lock (_lock) { if (_sessions.Count == 0) return; }

            foreach (WebView view in WebView.Live)
            {
                string targetId = Targets.IdOf(view);
                object document = view.Document;
                object script = view.Script;

                if (!_seen.TryGetValue(targetId, out Snapshot last))
                {
                    _seen[targetId] = new Snapshot { Document = document, Script = script };
                    continue;
                }

                bool newDocument = !ReferenceEquals(last.Document, document);
                bool newScript = !ReferenceEquals(last.Script, script);
                if (!newDocument && !newScript) continue;

                last.Document = document;
                last.Script = script;

                foreach (CdpSession session in SessionsFor(targetId))
                {
                    if (newDocument)
                    {
                        session.Nodes.Clear();
                        if (session.DomEnabled) session.Emit("DOM.documentUpdated", Json.EmptyObject);

                        // The stylesheet is rebuilt together with the document, so the rules the window is holding
                        // belong to a sheet that no longer exists. Announce the new one under a new id.
                        CssDomain.Forget(session);
                        if (session.CssEnabled)
                        {
                            string header = CssDomain.HeaderJson(session);
                            if (header != null) session.Emit("CSS.styleSheetAdded", header);
                        }
                    }

                    if (!newScript || !session.RuntimeEnabled) continue;

                    session.Objects.Clear();
                    session.Emit("Runtime.executionContextsCleared", Json.EmptyObject);
                    session.Emit("Runtime.executionContextCreated", RuntimeDomain.ContextJson(session));
                }
            }
        }

        /// <summary>
        /// Point the developer at the server once there is something to inspect.
        ///
        /// "Something to inspect" means a page that has actually been BUILT, not merely mounted. Every registered app
        /// mounts a view at startup and most are never opened; announcing on the first of those would attach DevTools
        /// to an empty document while the app the developer is looking at goes uninspected.
        /// </summary>
        private static void Announce()
        {
            if (_announced) return;

            WebView target = null;
            foreach (WebView view in WebView.Live)
                if (view.BoxCount > 0) { target = view; break; }

            if (target == null) return;
            _announced = true;

            string targetId = Targets.IdOf(target);
            Core.Log?.Msg($"[Sideload/cdp] open http://127.0.0.1:{_port}/ and click '{targetId}' to inspect it.");
            Core.Log?.Msg($"[Sideload/cdp] direct: {Targets.FrontendUrl(_port, targetId)}");

            // Straight to the inspector, not the target list: with one page mounted there is nothing to choose.
            if (Config.Preferences.DevToolsAutoOpen) ChromeLauncher.Open(_port, targetId);
        }

        /// <summary>
        /// Every console call and every uncaught script error from a page. Called on the main thread by the script
        /// host, which is the only place a Jint value may be read - the RemoteObjects are therefore built here and
        /// the sockets only ever see finished JSON.
        /// </summary>
        private static void OnDiagnostic(string appId, string level, object[] args, string text)
        {
            if (!_running) return;

            string targetId = Targets.IdFor(appId);
            LogDomain.Record(targetId, level, text);

            List<CdpSession> sessions = SessionsFor(targetId);
            if (sessions.Count == 0) return;

            Engine engine = null;
            try { engine = Targets.Find(targetId)?.Script?.Engine; } catch { /* the page is being torn down */ }

            foreach (CdpSession session in sessions)
            {
                try { LogDomain.Console(session, engine, level, args, text); }
                catch (Exception e) { Core.Log?.Warning("[Sideload/cdp] forwarding a console line failed: " + e.Message); }
            }
        }

        // ------------------------------------------------------------------ http --

        private static void AcceptLoop()
        {
            while (_running)
            {
                HttpListenerContext context;
                try { context = _listener.GetContext(); }
                catch { if (!_running) return; continue; }

                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private static async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                HttpListenerRequest request = context.Request;

                // Loopback binding stops anything off-machine, and this stops a page in the developer's browser from
                // reaching the port through a hostname that resolves to 127.0.0.1.
                if (!IsLocalHost(request))
                {
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    return;
                }

                string path = request.Url.AbsolutePath;

                if (request.IsWebSocketRequest)
                {
                    await HandleSocketAsync(context, path).ConfigureAwait(false);
                    return;
                }

                switch (path)
                {
                    case "/json/version":
                        WriteJson(context.Response, VersionJson());
                        return;
                    case "/json":
                    case "/json/list":
                        WriteJson(context.Response, MainThread.Run(ListJson));
                        return;
                    case "/":
                    case "/index.html":
                        WriteHtml(context.Response, MainThread.Run(LandingPage));
                        return;
                    default:
                        if (path.StartsWith("/frontend/", StringComparison.Ordinal)) { ServeFrontend(context.Response, path); return; }
                        if (path.StartsWith("/json/activate/", StringComparison.Ordinal)) { WriteText(context.Response, "Target activated"); return; }
                        if (path.StartsWith("/json/close/", StringComparison.Ordinal)) { WriteText(context.Response, "Target is closed"); return; }

                        context.Response.StatusCode = 404;
                        context.Response.Close();
                        return;
                }
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[Sideload/cdp] request failed: " + e.Message);
                try { context.Response.Abort(); } catch { /* the client is already gone */ }
            }
        }

        private static async Task HandleSocketAsync(HttpListenerContext context, string path)
        {
            const string prefix = "/devtools/page/";

            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            string targetId = path.Substring(prefix.Length).Trim('/');

            lock (_lock)
            {
                if (_sessions.Count >= MaxSessions)
                {
                    try { context.Response.StatusCode = 503; context.Response.Close(); } catch { }
                    return;
                }
            }

            WebSocket socket;
            try { socket = (await context.AcceptWebSocketAsync(null).ConfigureAwait(false)).WebSocket; }
            catch (Exception e)
            {
                Core.Log?.Warning("[Sideload/cdp] the websocket upgrade failed: " + e.Message);
                return;
            }

            var session = new CdpSession(socket, targetId);
            lock (_lock) _sessions.Add(session);
            Core.Log?.Msg($"[Sideload/cdp] devtools attached to '{targetId}'.");

            try { await session.RunAsync(_cancel?.Token ?? CancellationToken.None).ConfigureAwait(false); }
            finally
            {
                lock (_lock) _sessions.Remove(session);
                session.Dispose();
                Core.Log?.Msg($"[Sideload/cdp] devtools detached from '{targetId}'.");
            }
        }

        /// <summary>What chrome://inspect reads first to decide this is something it can talk to.</summary>
        private static string VersionJson() =>
            new Json.Obj()
                .Str("Browser", "Sideload/" + typeof(Core).Assembly.GetName().Version)
                .Str("Protocol-Version", "1.3")
                .Str("User-Agent", "Sideload (Schedule I; MelonLoader; uGUI renderer)")
                .Str("V8-Version", "0.0.0.0")
                .Str("WebKit-Version", "0.0.0.0")
                .Str("webSocketDebuggerUrl", $"ws://127.0.0.1:{_port}/devtools/browser/sideload")
                .Done();

        /// <summary>One entry per mounted page. Reads the live view list, so it runs on the main thread.</summary>
        private static string ListJson()
        {
            var entries = new List<string>();
            foreach (WebView view in Targets.All()) entries.Add(Targets.DescribeJson(view, _port));
            return Json.Array(entries);
        }

        /// <summary>
        /// The page a developer opens.
        ///
        /// It exists because nothing can launch the inspector directly: Chrome only navigates to a `devtools://` URL
        /// from its own WebUI pages, and refuses it both on the command line and from a link on an ordinary page. So
        /// this is a plain http page - which a browser will always open - carrying a link to the hosted DevTools
        /// frontend, which is an ordinary https page and may be linked. It also spells out the offline route, because
        /// the hosted frontend needs internet and chrome://inspect does not.
        /// </summary>
        /// <summary>
        /// Serve a file out of the local DevTools frontend folder, wherever <see cref="FrontendCache"/> found one.
        /// Path traversal is refused by resolving the full path and checking it is still inside that root - this
        /// listens on loopback, but a server that hands out arbitrary files because of a "../" is not one worth
        /// writing either way.
        /// </summary>
        private static void ServeFrontend(HttpListenerResponse response, string path)
        {
            string root = FrontendCache.Root;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                response.StatusCode = 404;
                response.Close();
                return;
            }

            string relative = Uri.UnescapeDataString(path.Substring("/frontend/".Length)).Replace('/', Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(Path.Combine(root, relative));

            if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                response.StatusCode = 404;
                response.Close();
                return;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(full);
                response.ContentType = ContentTypeOf(full);
                response.ContentLength64 = bytes.Length;
                response.OutputStream.Write(bytes, 0, bytes.Length);
                response.Close();
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[Sideload/cdp] serving " + path + " failed: " + e.Message);
                try { response.Abort(); } catch { }
            }
        }

        private static string ContentTypeOf(string file) => Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" or ".map" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            // The frontend package ships its icons as avif and its favicon as ico, and a browser will not decode
            // either one when it arrives as application/octet-stream.
            ".avif" => "image/avif",
            ".ico" => "image/x-icon",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            ".wasm" => "application/wasm",
            _ => "application/octet-stream",
        };

        private static string LandingPage()
        {
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Sideload DevTools</title><style>")
              .Append("body{background:#0b0d12;color:#e7e9ee;font:14px/1.6 system-ui,Segoe UI,sans-serif;margin:0;padding:40px}")
              .Append("h1{font-size:18px;margin:0 0 4px}p{color:#8a8f9e;margin:0 0 24px;max-width:60ch}")
              .Append("a{color:#7cc0ff}li{margin:0 0 16px;list-style:none}")
              .Append("code{color:#8a8f9e;font-size:12px}ul{padding:0;margin:0 0 32px}")
              .Append("h2{font-size:14px;margin:0 0 4px}</style></head><body>")
              .Append("<h1>Sideload DevTools</h1><p>Click a page to inspect it. That opens the real Chrome DevTools, ")
              .Append("attached to the page running in the game - console, evaluate, Elements.</p><ul>");

            List<WebView> views = Targets.All();
            if (views.Count == 0)
                sb.Append("<li>No page is mounted yet. Open a Sideload app on the in-game phone, then reload this page.</li>");

            foreach (WebView view in views)
            {
                string id = Targets.IdOf(view);
                sb.Append("<li><a href=\"").Append(Targets.FrontendUrl(_port, id)).Append("\">")
                  .Append(WebUtility.HtmlEncode(view.AppId)).Append("</a><br><code>")
                  .Append(WebUtility.HtmlEncode(Targets.WebSocketUrl(_port, id))).Append("</code></li>");
            }

            if (FrontendCache.Root != null)
                sb.Append("<h2>Offline</h2><p>Those links load the DevTools frontend from this machine, out of ")
                  .Append("<code>").Append(WebUtility.HtmlEncode(FrontendCache.Root))
                  .Append("</code>. No internet needed.</p>");
            else
                sb.Append("<h2>No internet?</h2><p>Those links load the DevTools frontend from Google's servers. ")
                  .Append("Switch on <code>DevToolsFetchFrontend</code> to have Sideload download its own copy once, ")
                  .Append("or use Chrome's built-in copy right now: open <code>chrome://inspect</code>, click ")
                  .Append("<b>Configure</b> next to \"Discover network targets\", add <code>127.0.0.1:").Append(_port)
                  .Append("</code>, and this page appears in the list with an <b>inspect</b> link. That setting is ")
                  .Append("remembered, so it is a one-time step per machine.</p>");

            return sb.Append("</body></html>").ToString();
        }

        private static bool IsLocalHost(HttpListenerRequest request)
        {
            string host = request.UserHostName ?? "";
            int colon = host.LastIndexOf(':');
            if (colon > 0 && host.IndexOf(']') < colon) host = host.Substring(0, colon);

            return host is "127.0.0.1" or "localhost" or "[::1]" or "::1";
        }

        private static void WriteJson(HttpListenerResponse response, string json) =>
            Write(response, "application/json; charset=utf-8", json);

        private static void WriteHtml(HttpListenerResponse response, string html) =>
            Write(response, "text/html; charset=utf-8", html);

        private static void WriteText(HttpListenerResponse response, string text) =>
            Write(response, "text/plain; charset=utf-8", text);

        private static void Write(HttpListenerResponse response, string contentType, string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body ?? "");

            response.StatusCode = 200;
            response.ContentType = contentType;
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.OutputStream.Close();
        }
    }
}
