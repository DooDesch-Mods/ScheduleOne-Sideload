using System.Text;
using Sideload.Host;

namespace Sideload.Devtools.Cdp
{
    /// <summary>
    /// What the protocol calls a "target" is one mounted page here: a live <see cref="WebView"/>.
    ///
    /// The id is derived from the app id rather than handed out as a counter, so the URL a developer bookmarks keeps
    /// working across a restart of the game. Everything in here reads <see cref="WebView.Live"/> and therefore has to
    /// run on the main thread.
    /// </summary>
    internal static class Targets
    {
        /// <summary>The target id for an app. Sanitised because it travels in a URL path.</summary>
        internal static string IdFor(string appId)
        {
            var sb = new StringBuilder(appId?.Length ?? 0);
            foreach (char c in appId ?? "") sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
            return sb.Length == 0 ? "page" : sb.ToString();
        }

        internal static string IdOf(WebView view) => IdFor(view?.AppId);

        /// <summary>The page a session is attached to, or null once it has been unmounted.</summary>
        internal static WebView Find(string targetId)
        {
            if (string.IsNullOrEmpty(targetId)) return null;

            foreach (WebView view in WebView.Live)
                if (string.Equals(IdOf(view), targetId, StringComparison.OrdinalIgnoreCase)) return view;

            return null;
        }

        internal static List<WebView> All()
        {
            var all = new List<WebView>();
            foreach (WebView view in WebView.Live) all.Add(view);
            return all;
        }

        /// <summary>The page's address. Not a real URL - nothing serves it - but DevTools shows it as the page's
        /// identity in the title bar and the Sources tree, so it names the app and the file it was built from.</summary>
        internal static string UrlOf(WebView view) => "sideload://" + (view?.AppId ?? "page") + "/index.html";

        internal static string OriginOf(WebView view) => "sideload://" + (view?.AppId ?? "page");

        /// <summary>The single frame every page has. DevTools keys resources, execution contexts and the DOM against
        /// a frame id, so one is minted per target and never changes.</summary>
        internal static string FrameOf(string targetId) => "frame-" + targetId;

        internal static string LoaderOf(string targetId) => "loader-" + targetId;

        internal static string WebSocketUrl(int port, string targetId) =>
            $"ws://127.0.0.1:{port}/devtools/page/{targetId}";

        /// <summary>
        /// Which build of the DevTools frontend the hosted URL below serves. Any recent revision speaks the protocol
        /// this server implements - the frontend is a protocol client, which is the whole reason this works - so this
        /// only has to be a revision that exists. Read a fresher one out of any Chrome's `/json/version` under
        /// "WebKit-Version" if this ever 404s.
        /// </summary>
        private const string FrontendRevision = "0fcdce5f4fdec8d442d7df760cb541f1ca6e446d";

        /// <summary>
        /// What a developer opens to inspect a page.
        ///
        /// Google's hosted copy of the DevTools frontend rather than the `devtools://` URL built into Chrome, because
        /// Chrome refuses to navigate to `devtools://` from anywhere except its own WebUI pages - not from the
        /// command line, and not from a link on an ordinary page. The hosted copy is an ordinary https page, so it
        /// can be linked and launched; it connects straight back to this server over loopback, and nothing about the
        /// page being inspected leaves the machine.
        ///
        /// The hosted copy is only the fallback: with a frontend on disk this points at that instead and the whole
        /// feature works with no internet. See <see cref="FrontendCache"/> for which one is in use and why.
        /// </summary>
        internal static string FrontendUrl(int port, string targetId)
        {
            string socket = $"ws=127.0.0.1:{port}/devtools/page/{targetId}";

            // A local frontend is served by this server, so the whole thing works with no internet - the same
            // arrangement React Native's Metro uses, which ships its own copy of the frontend. Read per call rather
            // than cached, so a copy that finishes downloading mid-session is picked up by the next link built.
            if (FrontendCache.Root != null)
                return $"http://127.0.0.1:{port}/frontend/inspector.html?{socket}";

            return $"https://chrome-devtools-frontend.appspot.com/serve_rev/@{FrontendRevision}/inspector.html?{socket}";
        }

        /// <summary>One entry of the `/json/list` discovery document, which is what chrome://inspect reads.</summary>
        internal static string DescribeJson(WebView view, int port)
        {
            string id = IdOf(view);

            return new Json.Obj()
                .Str("description", "Sideload page")
                .Str("devtoolsFrontendUrl", FrontendUrl(port, id))
                .Str("id", id)
                .Str("title", view?.AppId ?? "page")
                .Str("type", "page")
                .Str("url", UrlOf(view))
                .Str("webSocketDebuggerUrl", WebSocketUrl(port, id))
                .Done();
        }
    }
}
