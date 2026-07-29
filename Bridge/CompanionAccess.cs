using System.Text;

namespace Sideload.Bridge
{
    /// <summary>
    /// What a companion server needs to serve the SAME page to a device outside the game: the app list, the bundle
    /// files, the framework stylesheet, runtime pictures, a way to run a handler, and a tap on everything the host
    /// pushes at pages.
    ///
    /// It lives here rather than in the server that uses it because the pieces are internal to Sideload and should
    /// stay that way - this is the one seam, and it is read-only apart from Invoke, which is the same call a page
    /// makes. Nothing here grants a capability a page does not already have; it moves the same surface to a second
    /// screen.
    ///
    /// Every method MUST be called on the Unity main thread. The server is free to accept connections on its own
    /// thread, but the moment it touches this it has to marshal.
    /// </summary>
    internal static class CompanionAccess
    {
        /// <summary>
        /// Every registered app as JSON, so the companion can draw a home screen without a second source of truth.
        /// Hand-rolled rather than serialized: it is one shape, it is written once, and the alternative is a
        /// reflection-based serializer under IL2CPP for no gain.
        /// </summary>
        internal static string ListAppsJson()
        {
            var sb = new StringBuilder("[");

            IReadOnlyList<AppRegistration> apps = Registry.Apps;
            for (int i = 0; i < apps.Count; i++)
            {
                AppRegistration a = apps[i];
                if (i > 0) sb.Append(',');
                sb.Append('{')
                  .Append("\"id\":").Append(Quote(a.Id)).Append(',')
                  .Append("\"title\":").Append(Quote(a.Title)).Append(',')
                  .Append("\"iconLabel\":").Append(Quote(a.IconLabel)).Append(',')
                  .Append("\"portrait\":").Append(a.Portrait ? "true" : "false").Append(',')
                  .Append("\"declaredPortrait\":").Append(a.DeclaredPortrait ? "true" : "false").Append(',')
                  .Append("\"canTurn\":").Append(a.CanTurn ? "true" : "false").Append(',')
                  .Append("\"iconless\":").Append(a.Iconless ? "true" : "false").Append(',')
                  .Append("\"badge\":").Append(a.Badge)
                  .Append('}');
            }

            return sb.Append(']').ToString();
        }

        /// <summary>
        /// One file from an app's bundle, resolved exactly as the in-game view resolves it - the folder under Mods
        /// wins over the embedded copy. Null when the app or the file is unknown.
        ///
        /// The path never reaches the filesystem directly: AppBundle does the resolving against a fixed root. The
        /// caller should still reject traversal before asking, so a request that means nothing is refused rather
        /// than quietly missing.
        /// </summary>
        internal static byte[] ReadBundleFile(string appId, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            AppRegistration reg = Registry.Find(appId);
            return reg?.Bundle?.ReadBytes(path);
        }

        /// <summary>A file the framework itself ships - `s1.css` and nothing else so far. Same resolution as a page
        /// linking it, so both screens get the identical design tokens.</summary>
        internal static byte[] ReadFrameworkAsset(string path)
        {
            string text = Host.WebView.FrameworkAsset(path);
            return text == null ? null : Encoding.UTF8.GetBytes(text);
        }

        /// <summary>PNG bytes behind an <c>s1://</c> name, for the companion to serve as an ordinary image.</summary>
        internal static byte[] ReadRuntimeImage(string appId, string name) => Paint.ImageCache.Supplied(appId, name);

        /// <summary>
        /// Run an app's `s1.call` handler. The same lookup a page uses, so a companion call and an in-game call are
        /// indistinguishable to the mod - which is the point: the companion adds a second input channel into the
        /// same door, never a new door.
        /// </summary>
        internal static string Invoke(string appId, string name, string argument) =>
            Script.Bridge.Invoke(appId, name, argument);

        /// <summary>
        /// Listen to everything the host pushes at pages: <c>s1.on</c> events, badge changes and notifications.
        /// Passing null for a tap clears it.
        ///
        /// One consumer, deliberately. The events fire on the main thread inside whatever caused them, so a tap must
        /// return promptly - queue and get out, never do work here.
        /// </summary>
        internal static void SetTaps(Action<string, string, string> onEmit,
                                     Action<string, int> onBadge,
                                     Action<string, string, string> onNotify)
        {
            Script.Bridge.Tap = onEmit;
            Registry.BadgeTap = onBadge;
            Registry.NotifyTap = onNotify;
        }

        private static string Quote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";

            var sb = new StringBuilder(s.Length + 8).Append('"');
            foreach (char c in s)
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
