using System.Reflection;
using MelonLoader;
using MelonLoader.Utils;

[assembly: MelonInfo(typeof(Sideload.Core), "Sideload", "1.5.0", "DooDesch", "https://github.com/DooDesch-Mods/ScheduleOne-Sideload")]
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
                Log.Msg("[Sideload] patches applied.");
            }
            catch (Exception e)
            {
                Log.Error("[Sideload] PatchAll failed - no app will reach the phone: " + e);
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

                Log.Msg($"[Sideload] warmed the parsers in {watch.ElapsedMilliseconds} ms"
                        + $" (document has {document.All.Length} node(s), script returned {engine.Evaluate("a")}).");
            }
            catch (Exception e)
            {
                // A warm-up that fails costs nothing but the warmth: the real page builds its own parser anyway.
                Log.Warning("[Sideload] warming the parsers failed, the first page will pay for it: " + e.Message);
            }
        }

        /// <summary>Drives every mounted page: script timers, and the single rebuild that a frame's worth of DOM
        /// changes adds up to.</summary>
        public override void OnUpdate()
        {
            Host.WebView.TickAll(UnityEngine.Time.deltaTime);

            // The rotate keys, and the "Rotate Phone" line in the game's key strip that explains them.
            Phone.TurnInput.Tick();

            // The first-open fade. Does nothing on a frame with no fade running.
            Phone.AppFade.Tick(UnityEngine.Time.unscaledDeltaTime);

            // Where the devtools protocol crosses onto the main thread. Returns immediately when the server is off.
            Devtools.Cdp.CdpServer.Pump();
#if DEBUG
            Devtools.AutoOpen.Tick();
#if !SNITCH
            Devtools.DevOverlay.Tick();
#endif
#endif
        }

        /// <summary>Quitting the game must not leave a stray DevTools window behind - it belongs to the session.</summary>
        public override void OnDeinitializeMelon()
        {
            Devtools.Cdp.CdpServer.Stop();
            Devtools.Cdp.ChromeLauncher.Close();
        }

        /// <summary>
        /// Loads one shipped dependency from disk. UserLibs is the intended home and where the Thunderstore package
        /// puts it, but the Nexus/Vortex installer refuses to place a folder there (it redirects a UserLibs entry into
        /// MelonLoader/), so that package ships the same DLLs beside Sideload.dll in Mods/. Both layouts have to work,
        /// hence the second probe.
        /// </summary>
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
                    if (!File.Exists(beside))
                    {
                        Log.Warning($"[Sideload] runtime dependency not found in UserLibs or next to the mod: {fileName}");
                        return;
                    }

                    path = beside;
                }

                AssemblyName name = Assembly.LoadFrom(path).GetName();
                Log.Msg($"[Sideload] preloaded {name.Name} {name.Version}");
            }
            catch (Exception e)
            {
                Log.Warning($"[Sideload] preloading {fileName} failed: {e.Message}");
            }
        }
    }
}
