using System.Reflection;
using MelonLoader;
using MelonLoader.Utils;

[assembly: MelonInfo(typeof(Sideload.Core), "Sideload", DooDesch.ModVersion.Current, "DooDesch", "https://github.com/DooDesch-Mods/ScheduleOne-Sideload")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace Sideload
{
    /// <summary>
    /// Mod entry point. Sideload is a framework: it renders web bundles that other mods register, and on its own it
    /// only ships the engine plus - in Debug builds - a self-test app so the render path can be exercised without a
    /// second mod installed.
    /// </summary>
    public class Core : MelonMod
    {
        internal static MelonLogger.Instance Log;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;

            // The network allowlist is deliberately free of the loader so it can be tested headlessly; this is where
            // it is given somewhere to write a blocked request to.
            Net.HostAllowlist.Log = line => Log.Warning(line);

            // MelonLoader's own UserLibs probing does not satisfy these bindings (it reports a FileLoadException even
            // with the correct Windows build present), so load them into the default context ourselves before
            // anything can ask for them. AngleSharp registers the code-page encoding provider in a static
            // constructor, and a failure there takes the whole HTML parser down with it.
            // System.Text.Encoding.CodePages is deliberately absent from this list: it is part of
            // Microsoft.NETCore.App 6.0, which is the framework MelonLoader's runtimeconfig asks for, so it never
            // ships next to the mod and the runtime resolves it on its own.
            // The hook goes on FIRST, and it is not optional. Eager loading alone gets each assembly into the
            // process but does not make it findable BY NAME from another one: Jint resolves Esprima in its own
            // constructor, and an assembly that came from a byte array is not in the set that bind probes. Measured
            // outside the game: without this, every Jint.Engine() throws FileNotFoundException for Esprima - which is
            // every app's JavaScript, on every machine.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveRuntimeDependency;

            PreloadRuntimeDependency("AngleSharp.dll");
            PreloadRuntimeDependency("Esprima.dll");
            PreloadRuntimeDependency("Jint.dll");

            WarmUp();

            // Patching at init is safe here because the only patch targets a UI method (HomeScreen.Start). The
            // crash-on-early-patch problem other mods hit comes from touching gameplay/FishNet types before the
            // scene exists, which this does not do.
            try
            {
                HarmonyInstance.PatchAll();
                Log.Msg("patches applied.");
            }
            catch (Exception e)
            {
                Log.Error("PatchAll failed - no app will reach the phone: " + e);
            }

            // Separate from PatchAll on purpose. These targets are TextMeshPro's, not the game's, and their protected
            // overload set has moved between Unity versions - a missing one must cost a page its arrow keys, not cost
            // every app the HomeScreen patch that puts it on the phone in the first place.
            Input.TmpCaretGuard.Apply(HarmonyInstance);

            Config.Preferences.Initialize();

            // Opt-in and off by default: with the preference unset nothing is constructed and no port is opened, in
            // any build configuration.
            if (Config.Preferences.DevTools) Devtools.Cdp.CdpServer.Start(Config.Preferences.DevToolsPort);

#if DEBUG
            Devtools.SelfTestApp.Register();
#endif
        }

        /// <summary>
        /// Run the parsers once on nothing, so the first real page does not pay for them.
        ///
        /// Loading an assembly is not the same as being ready to use it. Measured on the live build, the first
        /// page of a session spent 168 ms inside AngleSharp and 255 ms inside Jint; the second spent 3 ms and
        /// 96 ms for comparable work. The difference is static initialisation and the JIT compiling both engines'
        /// hot paths - a cost that belongs to the first caller by accident.
        ///
        /// Here that caller is the loading screen, where nobody is waiting on a frame, instead of the player who
        /// just opened an app. The documents are the smallest that still walk the whole path: an element with a
        /// stylesheet and a script that touches the DOM.
        /// </summary>
        private static void WarmUp()
        {
            try
            {
                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

                AngleSharp.Html.Dom.IHtmlDocument document =
                    new AngleSharp.Html.Parser.HtmlParser().ParseDocument(
                        "<style>b{color:#fff;padding:1px}</style><div id=w><b>x</b></div>");

                Css.CssParser.Parse("b{color:#fff;padding:1px}");

                var engine = new Jint.Engine();   // defaults are fine: this script cannot loop and is thrown away
                engine.Execute("var a=[1,2,3].map(function(v){return v*2;}).join(',');");

                Log.Msg($"warmed the parsers in {watch.ElapsedMilliseconds} ms"
                        + $" (document has {document.All.Length} node(s), script returned {engine.Evaluate("a")}).");
            }
            catch (Exception e)
            {
                // A warm-up that fails costs nothing but the warmth: the real page builds its own parser anyway.
                Log.Warning("warming the parsers failed, the first page will pay for it: " + e.Message);
            }
        }

        /// <summary>Drives every mounted page: script timers, and the single rebuild that a frame's worth of DOM
        /// changes adds up to.</summary>
        public override void OnUpdate()
        {
            // Every phase below is named for the profiler. Without Snitch compiled in these are empty structs the JIT
            // removes; with it, the overlay names the phase that costs the frame instead of charging all of it to
            // OnUpdate and leaving the reader to guess.
            using (Profiling.Phase.Of("sideload.views")) Host.WebView.TickAll(UnityEngine.Time.deltaTime);

            // The rotate keys, and the "Rotate Phone" line in the game's key strip that explains them.
            using (Profiling.Phase.Of("sideload.turninput")) Phone.TurnInput.Tick();

            // Keys that reach an app with the phone still in the player's pocket. Returns on the first line when no
            // app claimed one, which is every frame unless something asked.
            using (Profiling.Phase.Of("sideload.globalkeys")) Input.GlobalKeys.Tick();

            // Escape and right-click while one of our fields holds the keyboard - the game refuses to raise the exit
            // action at all in that state, and an app that keeps the caret would otherwise have no way out.
            using (Profiling.Phase.Of("sideload.typingexit")) Phone.TypingExit.Tick();

            // The first-open fade. Does nothing on a frame with no fade running.
            using (Profiling.Phase.Of("sideload.appfade")) Phone.AppFade.Tick(UnityEngine.Time.unscaledDeltaTime);

            // Where the devtools protocol crosses onto the main thread. Returns immediately when the server is off.
            using (Profiling.Phase.Of("sideload.cdp")) Devtools.Cdp.CdpServer.Pump();
#if DEBUG
            Devtools.AutoOpen.Tick();
#if !SNITCH
            Devtools.DevOverlay.Tick();
#endif
#endif
        }

        /// <summary>Quitting the game must not leave a stray DevTools window behind - it belongs to the session.</summary>
        /// <summary>A new scene takes the key strip with it, so the turn hint has to be told that what it put up
        /// is gone.</summary>
        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            Phone.TurnPrompt.SceneChanged();
        }

        public override void OnDeinitializeMelon()
        {
            Devtools.Cdp.CdpServer.Stop();
            Devtools.Cdp.ChromeLauncher.Close();
        }

        /// <summary>
        /// Makes one runtime dependency available before anything asks for it.
        ///
        /// These ship inside Sideload.dll as embedded resources, so a normal install has no loose DLLs to lose, to
        /// wonder about, or to leave behind when the mod is removed. Disk is still checked FIRST, and on purpose: an
        /// install that already has these in UserLibs (every version before this one put them there) must keep loading
        /// exactly the file it loaded yesterday, and someone debugging a newer AngleSharp by dropping it next to the
        /// mod must still win over the copy baked in here.
        ///
        /// The two disk locations are both real: UserLibs is the intended home and where the Thunderstore package put
        /// them, but the Nexus/Vortex installer refuses to place a folder there (it redirects a UserLibs entry into
        /// MelonLoader/), so that package shipped them beside Sideload.dll in Mods/ instead.
        ///
        /// Loading it ourselves at all - rather than letting the runtime bind on first use - is unchanged and still
        /// necessary: MelonLoader's own UserLibs probing does not satisfy these bindings and reports a
        /// FileLoadException even with the correct build present.
        /// </summary>
        /// <summary>The three assemblies this mod carries, by simple name. Everything else that fails to bind in this
        /// process is somebody else's business - the resolve hook must stay silent for it.</summary>
        private static readonly string[] RuntimeDependencies = { "AngleSharp", "Esprima", "Jint" };

        /// <summary>What the preload actually got hold of, so a later bind is answered with the SAME assembly rather
        /// than a second copy of it (two copies of Esprima means Jint's types stop matching its own).</summary>
        private static readonly Dictionary<string, Assembly> _resolved =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Answers a failed bind for one of our three dependencies. Fires for every unresolved assembly in the
        /// process, so it returns null immediately for anything not ours.
        /// </summary>
        private static Assembly ResolveRuntimeDependency(object sender, ResolveEventArgs args)
        {
            string simple;
            try { simple = new AssemblyName(args.Name).Name; }
            catch { return null; }

            if (_resolved.TryGetValue(simple, out Assembly already)) return already;
            if (Array.IndexOf(RuntimeDependencies, simple) < 0) return null;

            Assembly loaded = LoadEmbeddedAssembly(simple + ".dll");
            if (loaded == null) return null;
            _resolved[simple] = loaded;
            Log.Msg($"resolved {simple} from the embedded copy (requested by {args.RequestingAssembly?.GetName().Name ?? "the runtime"})");
            return loaded;
        }

        private static void PreloadRuntimeDependency(string fileName)
        {
            try
            {
                string path = Path.Combine(MelonEnvironment.UserLibsDirectory, fileName);
                if (!File.Exists(path))
                {
                    string beside = Path.Combine(
                        Path.GetDirectoryName(typeof(Core).Assembly.Location) ?? MelonEnvironment.ModsDirectory,
                        fileName);
                    if (File.Exists(beside)) path = beside;
                    else path = null;
                }

                Assembly loaded = path != null ? Assembly.LoadFrom(path) : LoadEmbeddedAssembly(fileName);
                if (loaded == null)
                {
                    Log.Error($"runtime dependency {fileName} is neither on disk nor embedded - no app will render.");
                    return;
                }

                AssemblyName name = loaded.GetName();
                _resolved[name.Name] = loaded;   // the hook hands out this one, never a second copy
                Log.Msg($"preloaded {name.Name} {name.Version} {(path != null ? "from disk (" + path + ")" : "(embedded)")}");
            }
            catch (Exception e)
            {
                Log.Warning($"preloading {fileName} failed: {e.Message}");
            }
        }

        /// <summary>
        /// Load one of the embedded dependency images. Read into a byte array rather than handed to the loader as the
        /// manifest stream: the stream is backed by this assembly's image and the loader would keep it open for the
        /// process lifetime.
        /// </summary>
        private static Assembly LoadEmbeddedAssembly(string fileName)
        {
            string resource = "Sideload.Libs." + fileName;
            using Stream stream = typeof(Core).Assembly.GetManifestResourceStream(resource);
            if (stream == null)
            {
                Log.Warning($"embedded dependency '{resource}' is missing from this build.");
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
            if (read != image.Length)
            {
                Log.Warning($"embedded dependency '{resource}' is truncated ({read} of {image.Length} bytes).");
                return null;
            }
            return Assembly.Load(image);
        }
    }
}
