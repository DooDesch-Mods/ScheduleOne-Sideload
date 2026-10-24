using System.Collections;
using System.Text;
using AngleSharp.Dom;
using MelonLoader;
using Sideload.Host;
using Sideload.Layout;
using UnityEngine;

namespace Sideload.Bridge
{
    /// <summary>
    /// What an editor beside the game needs to see the page the player sees: the laid-out tree with each box's
    /// place, a way to ask the page's own engine a question, and a picture of the app on screen.
    ///
    /// Sideload Studio drives this, and through it the player's own AI agent - which writes an app and then has to
    /// check what actually rendered, the one thing no reading of the source can tell it. The Debug-only McpProbe
    /// does the same for the development harness; this is the smaller Release surface, read-only apart from
    /// <see cref="EvalJson"/>, which runs in the page's sandbox like any script the page already has.
    ///
    /// Main thread only, like everything that touches a WebView.
    /// </summary>
    internal static class StudioAccess
    {
        /// <summary>Enough for any phone app; a page with more boxes is cut off with "truncated": true rather than
        /// handing the caller megabytes.</summary>
        private const int MaxNodes = 1500;

        /// <summary>
        /// The mounted page of an app as JSON: { ok, appId, open, onScreen, phoneRaised, viewport, script, tree }.
        /// Each tree node is { tag, id, class, text, x, y, w, h, children }, positions in CSS pixels from the page's
        /// top left. Not mounted (never opened since the last load) is { ok: false, error }.
        /// </summary>
        internal static string InspectJson(string appId)
        {
            WebView view = WebView.Mounted(appId);
            if (view == null) return Error($"'{appId}' is not mounted - open it on the phone once, then ask again.");

            var sb = new StringBuilder(4096).Append('{')
                .Append("\"ok\":true,")
                .Append("\"appId\":").Append(Quote(view.AppId)).Append(',')
                .Append("\"open\":").Append(Bool(Registry.IsAppOpen(view.AppId))).Append(',')
                .Append("\"onScreen\":").Append(Bool(Registry.IsOnScreen(view.AppId))).Append(',')
                .Append("\"phoneRaised\":").Append(Bool(Registry.IsPhoneRaised())).Append(',');

            RectTransform root = view.RootRect;
            float w = root != null ? root.sizeDelta.x : 0f, h = root != null ? root.sizeDelta.y : 0f;
            sb.Append("\"viewport\":{\"w\":").Append(Num(w)).Append(",\"h\":").Append(Num(h)).Append("},");

            string status = view.Script == null ? "none" : view.Script.Failed ? "failed" : "ok";
            sb.Append("\"script\":{\"status\":").Append(Quote(status))
              .Append(",\"error\":").Append(Quote(view.Script?.LastError ?? "")).Append("},");

            int count = 0;
            sb.Append("\"tree\":");
            if (view.LayoutTree == null) sb.Append("null");
            else WriteNode(sb, view.LayoutTree, 0f, 0f, ref count);
            sb.Append(",\"truncated\":").Append(Bool(count >= MaxNodes));

            return sb.Append('}').ToString();
        }

        private static void WriteNode(StringBuilder sb, LayoutNode node, float parentX, float parentY, ref int count)
        {
            count++;
            float x = parentX + node.X, y = parentY + node.Y;
            var element = node.Tag as IElement;

            sb.Append('{');
            if (element != null)
            {
                sb.Append("\"tag\":").Append(Quote(element.LocalName)).Append(',');
                string id = element.Id;
                if (!string.IsNullOrEmpty(id)) sb.Append("\"id\":").Append(Quote(id)).Append(',');
                string cls = element.GetAttribute("class");
                if (!string.IsNullOrEmpty(cls)) sb.Append("\"class\":").Append(Quote(cls)).Append(',');
            }
            else sb.Append("\"tag\":\"#text\",");

            if (!string.IsNullOrEmpty(node.Text))
            {
                string text = node.Text.Length > 80 ? node.Text.Substring(0, 80) + "..." : node.Text;
                sb.Append("\"text\":").Append(Quote(text)).Append(',');
            }

            sb.Append("\"x\":").Append(Num(x)).Append(",\"y\":").Append(Num(y))
              .Append(",\"w\":").Append(Num(node.Width)).Append(",\"h\":").Append(Num(node.Height));

            if (node.Children.Count > 0 && count < MaxNodes)
            {
                sb.Append(",\"children\":[");
                bool first = true;
                foreach (LayoutNode child in node.Children)
                {
                    if (count >= MaxNodes) break;
                    if (!first) sb.Append(',');
                    first = false;
                    WriteNode(sb, child, x, y, ref count);
                }
                sb.Append(']');
            }

            sb.Append('}');
        }

        /// <summary>Evaluate a snippet in the page's own engine: { ok, value, failed }. The value is JSON for anything
        /// structured and the plain text for the rest - the same rendering the dev console uses.</summary>
        internal static string EvalJson(string appId, string code)
        {
            WebView view = WebView.Mounted(appId);
            if (view == null) return Error($"'{appId}' is not mounted - open it on the phone once, then ask again.");
            if (view.Script == null) return Error($"'{appId}' has no script yet.");

            string value = view.Script.Evaluate(code ?? "", out bool failed);
            return new StringBuilder().Append('{')
                .Append("\"ok\":true,\"failed\":").Append(Bool(failed))
                .Append(",\"value\":").Append(Quote(value))
                .Append('}').ToString();
        }

        /// <summary>
        /// A PNG of the app as it is on screen, handed to <paramref name="done"/> on the main thread after the frame
        /// has rendered - or null when there is nothing to take (not on screen, capture failed). An empty
        /// <paramref name="appId"/> takes the whole screen.
        ///
        /// End of frame, because a capture taken during Update reads a back buffer that is still being drawn.
        /// </summary>
        internal static void Capture(string appId, Action<byte[]> done)
        {
            if (done == null)
            {
                Core.Log?.Warning("[Sideload/studio] capture asked for without a callback - nothing to do.");
                return;
            }

            MelonCoroutines.Start(CaptureAtEndOfFrame(appId, done));
        }

        private static IEnumerator CaptureAtEndOfFrame(string appId, Action<byte[]> done)
        {
            yield return new WaitForEndOfFrame();

            byte[] png = null;
            Texture2D full = null, cut = null;
            try
            {
                full = ScreenCapture.CaptureScreenshotAsTexture();
                if (string.IsNullOrEmpty(appId))
                {
                    png = ImageConversion.EncodeToPNG(full);
                }
                else if (ScreenRectOf(appId, full.width, full.height, out RectInt area))
                {
                    cut = new Texture2D(area.width, area.height, TextureFormat.RGBA32, false);
                    cut.SetPixels(full.GetPixels(area.x, area.y, area.width, area.height));
                    cut.Apply();
                    png = ImageConversion.EncodeToPNG(cut);
                }
                else
                {
                    Core.Log?.Msg($"[Sideload/studio] capture of '{appId}' skipped: the app is not on screen.");
                }
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[Sideload/studio] capture failed: " + e.Message);
                png = null;
            }
            finally
            {
                if (full != null) UnityEngine.Object.Destroy(full);
                if (cut != null) UnityEngine.Object.Destroy(cut);
            }

            if (png != null) Core.Log?.Msg($"[Sideload/studio] captured {(string.IsNullOrEmpty(appId) ? "the screen" : "'" + appId + "'")}: {png.Length} bytes.");

            try { done(png); }
            catch (Exception e) { Core.Log?.Warning("[Sideload/studio] capture consumer threw: " + e.Message); }
        }

        /// <summary>Where the app's root rect lands on screen, in texture pixels (origin bottom left), clamped to the
        /// screen. False when the app is not on screen or the rect is empty.</summary>
        private static bool ScreenRectOf(string appId, int screenW, int screenH, out RectInt area)
        {
            area = default;
            if (!Registry.IsOnScreen(appId)) return false;

            RectTransform root = WebView.Mounted(appId)?.RootRect;
            if (root == null) return false;

            Canvas canvas = root.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;

            var corners = new Vector3[4];
            root.GetWorldCorners(corners);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (Vector3 corner in corners)
            {
                Vector2 p = RectTransformUtility.WorldToScreenPoint(camera, corner);
                minX = Math.Min(minX, p.x); maxX = Math.Max(maxX, p.x);
                minY = Math.Min(minY, p.y); maxY = Math.Max(maxY, p.y);
            }

            int x0 = Mathf.Clamp(Mathf.FloorToInt(minX), 0, screenW), y0 = Mathf.Clamp(Mathf.FloorToInt(minY), 0, screenH);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(maxX), 0, screenW), y1 = Mathf.Clamp(Mathf.CeilToInt(maxY), 0, screenH);
            if (x1 - x0 < 2 || y1 - y0 < 2) return false;

            area = new RectInt(x0, y0, x1 - x0, y1 - y0);
            return true;
        }

        private static string Quote(string s) => CompanionAccess.Quote(s);

        private static string Error(string message) => "{\"ok\":false,\"error\":" + Quote(message) + "}";

        private static string Bool(bool b) => b ? "true" : "false";

        private static string Num(float f) =>
            float.IsNaN(f) || float.IsInfinity(f) ? "0" : Math.Round(f, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
