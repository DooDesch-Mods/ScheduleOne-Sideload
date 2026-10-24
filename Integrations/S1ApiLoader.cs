using System.Reflection;

namespace Sideload.Integrations
{
    /// <summary>
    /// Loads the S1API half of <c>s1api</c> when, and only when, S1API is in the process.
    ///
    /// The integration is its own assembly (Integrations/S1API), compiled against S1API and embedded in Sideload.dll as
    /// a resource. Sideload itself references nothing of S1API, so a player without it loads nothing extra and sees
    /// <c>s1api.available === false</c>; a player with it gets every <c>s1api.*</c> handler registered for every app.
    ///
    /// The handshake is the one Sideload.Api uses with this mod, turned around: find a public static method by name,
    /// hand it BCL delegates, never share a type.
    /// </summary>
    internal static class S1ApiLoader
    {
        private const string Resource = "Sideload.Integrations.S1API.dll";
        private const string EntryType = "Sideload.Integrations.S1Api.Entry";

        private static bool _tried;
        private static Assembly _s1api;

        /// <summary>Called once from OnInitializeMelon, after every mod assembly is loaded and before any page exists.
        /// S1API may load after us, so this looks for it rather than expecting it in the reference list.</summary>
        internal static void TryInstall()
        {
            if (_tried) return;
            _tried = true;

            _s1api = FindS1Api();
            if (_s1api == null)
            {
                Core.Log?.Msg("S1API is not installed - s1api answers 'not available' in every page.");
                return;
            }

            try
            {
                // The integration was compiled against one S1API version and the player may have another. Its
                // reference is by simple name, so a newer S1API answers it - member by member, which is what the
                // per-area guards in the integration are for.
                AppDomain.CurrentDomain.AssemblyResolve += ResolveS1Api;

                Assembly integration = LoadEmbedded();
                if (integration == null) return;

                MethodInfo install = integration.GetType(EntryType, false)?.GetMethod("Install", BindingFlags.Public | BindingFlags.Static);
                if (install == null)
                {
                    Core.Log?.Warning("the s1api integration has no entry point - s1api stays unavailable.");
                    return;
                }

                Action<string, Func<string, string, string>> handle = (name, handler) => Script.Bridge.Handle(null, name, handler);
                Action<string, string, string> emit = Script.Bridge.Emit;
                Func<string, string, bool> mayWrite = Script.GameWrites.IsAllowed;
                Action<string> warn = line => Core.Log?.Warning(line);

                object count = install.Invoke(null, new object[] { handle, emit, mayWrite, warn });

                Script.S1Api.Available = true;
                Core.Log?.Msg($"s1api is ready: {count} handler(s) on S1API {_s1api.GetName().Version}.");
            }
            catch (Exception e)
            {
                Script.S1Api.Available = false;
                Core.Log?.Warning("the s1api integration failed to start - s1api stays unavailable: "
                                  + (e is TargetInvocationException t && t.InnerException != null ? t.InnerException.Message : e.Message));
            }
        }

        private static Assembly FindS1Api()
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                try { name = asm.GetName().Name ?? ""; }
                catch { continue; }

                // S1API.Il2Cpp.MelonLoader is the assembly; S1APILoader is the plugin that picks the build and is not it.
                if (name.StartsWith("S1API.", StringComparison.OrdinalIgnoreCase)) return asm;
            }

            return null;
        }

        private static Assembly ResolveS1Api(object sender, ResolveEventArgs args)
        {
            string simple;
            try { simple = new AssemblyName(args.Name).Name ?? ""; }
            catch { return null; }

            return simple.StartsWith("S1API.", StringComparison.OrdinalIgnoreCase) ? _s1api : null;
        }

        private static Assembly LoadEmbedded()
        {
            using Stream stream = typeof(S1ApiLoader).Assembly.GetManifestResourceStream(Resource);
            if (stream == null)
            {
                Core.Log?.Warning("this Sideload build carries no s1api integration - s1api stays unavailable.");
                return null;
            }

            byte[] image = new byte[stream.Length];
            int read = 0;
            while (read < image.Length)
            {
                int n = stream.Read(image, read, image.Length - read);
                if (n <= 0) break;
                read += n;
            }

            return read == image.Length ? Assembly.Load(image) : null;
        }
    }
}
