using Sideload.Host;

namespace Sideload.Devtools.Cdp
{
    /// <summary>
    /// The Page domain, as far as a page that is not in a browser can mean anything.
    ///
    /// DevTools will not settle until it has been told about a frame: the Elements tree, the execution context and
    /// the Sources tree all hang off one. So a page reports exactly one synthetic frame, whose url names the app it
    /// was built from. `Page.reload` is the one method here that does something real - it is wired to the same
    /// rebuild-from-disk the hot reload watcher performs, which is what a developer means by reloading this page.
    /// </summary>
    internal static class PageDomain
    {
        internal static string Enable(CdpSession session)
        {
            session.PageEnabled = true;
            return Json.EmptyObject;
        }

        internal static string GetResourceTree(CdpSession session) =>
            new Json.Obj()
                .Raw("frameTree", new Json.Obj()
                    .Raw("frame", FrameJson(session))
                    .Raw("resources", "[]")
                    .Done())
                .Done();

        internal static string GetFrameTree(CdpSession session) =>
            new Json.Obj()
                .Raw("frameTree", new Json.Obj()
                    .Raw("frame", FrameJson(session))
                    .Raw("childFrames", "[]")
                    .Done())
                .Done();

        internal static string GetNavigationHistory(CdpSession session)
        {
            WebView view = Targets.Find(session.TargetId);
            string url = Targets.UrlOf(view);

            string entry = new Json.Obj()
                .Num("id", 1)
                .Str("url", url)
                .Str("userTypedURL", url)
                .Str("title", view?.AppId ?? session.TargetId)
                .Str("transitionType", "typed")
                .Done();

            return new Json.Obj()
                .Num("currentIndex", 0)
                .Raw("entries", Json.Array(new[] { entry }))
                .Done();
        }

        /// <summary>The page's viewport in CSS pixels - the same 400px-short-side space a stylesheet is written
        /// against, not the device pixels the panel occupies.</summary>
        internal static string GetLayoutMetrics(CdpSession session)
        {
            float width = 0f, height = 0f;

            try
            {
                WebView view = Targets.Find(session.TargetId);
                if (view != null && view.Root != null)
                {
                    width = view.Root.sizeDelta.x;
                    height = view.Root.sizeDelta.y;
                }
            }
            catch { /* the page was torn down between the lookup and the read */ }

            string viewport = new Json.Obj()
                .Num("pageX", 0).Num("pageY", 0)
                .Num("clientWidth", width).Num("clientHeight", height)
                .Done();

            string visual = new Json.Obj()
                .Num("offsetX", 0).Num("offsetY", 0)
                .Num("pageX", 0).Num("pageY", 0)
                .Num("clientWidth", width).Num("clientHeight", height)
                .Num("scale", 1).Num("zoom", 1)
                .Done();

            string content = new Json.Obj()
                .Num("x", 0).Num("y", 0)
                .Num("width", width).Num("height", height)
                .Done();

            return new Json.Obj()
                .Raw("layoutViewport", viewport)
                .Raw("visualViewport", visual)
                .Raw("contentSize", content)
                .Raw("cssLayoutViewport", viewport)
                .Raw("cssVisualViewport", visual)
                .Raw("cssContentSize", content)
                .Done();
        }

        /// <summary>
        /// Rebuild the page from disk, exactly as saving a file under the override folder does. The document and the
        /// script engine are both replaced, so every node id and object id this session handed out is stale; the
        /// server's per-frame check notices the swap and tells the frontend to start over.
        /// </summary>
        internal static string Reload(CdpSession session)
        {
            WebView view = Targets.Find(session.TargetId)
                ?? throw new CdpException(CdpException.ServerError, $"the page '{session.TargetId}' is no longer mounted");

            view.Reload();
            return Json.EmptyObject;
        }

        private static string FrameJson(CdpSession session)
        {
            WebView view = Targets.Find(session.TargetId);

            return new Json.Obj()
                .Str("id", Targets.FrameOf(session.TargetId))
                .Str("loaderId", Targets.LoaderOf(session.TargetId))
                .Str("url", Targets.UrlOf(view))
                .Str("domainAndRegistry", "")
                .Str("securityOrigin", Targets.OriginOf(view))
                .Str("mimeType", "text/html")
                .Str("secureContextType", "Secure")
                .Str("crossOriginIsolatedContextType", "NotIsolated")
                .Raw("gatedAPIFeatures", "[]")
                .Done();
        }
    }
}
