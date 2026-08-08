#if DEBUG
using System;
using UnityEngine;

namespace Sideload.Devtools
{
    /// <summary>
    /// DEBUG-only console commands that put a surface on screen. Compiled out of Release entirely.
    ///
    /// A surface is a bundle rendered into somebody else's panel rather than onto the phone, and until now the only
    /// way to see one was to install a mod that mounts one - so the whole path could not be exercised on its own.
    /// That matters more than it sounds: the phone and a surface differ in exactly the places that go wrong. The
    /// phone releases the keyboard on the way down (Phone/PhoneScreen.Lower) and a surface has to do it in Dispose;
    /// the phone has one fixed panel size and a surface takes whatever rect it is given.
    ///
    /// <list type="bullet">
    /// <item><c>sideloadsurface</c> - mount the selftest bundle in a panel over the middle of the screen.</item>
    /// <item><c>sideloadsurface off</c> - take it down, which is the half that used to leave the keyboard behind.
    /// </item>
    /// <item><c>sideloadsurface &lt;width&gt; &lt;height&gt;</c> - the same, at a size of your choosing. A surface has
    /// no agreed panel the way the phone does, so the shape is the variable a layout has to survive.</item>
    /// </list>
    /// </summary>
    internal static class SurfaceConsole
    {
        internal const string Id = "surfacetest";

        private static GameObject _panel;
        private static int _lastFrame = -1;
        private static string _lastSignature = "";

        internal static bool TryHandle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return Dispatch(raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        internal static bool TryHandle(Il2CppSystem.Collections.Generic.List<string> args)
        {
            if (args == null || args.Count == 0) return false;

            var parts = new string[args.Count];
            for (int i = 0; i < args.Count; i++) parts[i] = args[i];
            return Dispatch(parts);
        }

        /// <summary>True when the line was ours and the game must not also run it.</summary>
        private static bool Dispatch(string[] parts)
        {
            if (parts.Length == 0) return false;
            if (!parts[0].Equals("sideloadsurface", StringComparison.OrdinalIgnoreCase)) return false;

            // Both SubmitCommand overloads are patched and the string body calls the list body, so one submission
            // arrives twice. Same guard as KeyConsole, for the same reason.
            string signature = string.Join(" ", parts);
            if (Time.frameCount == _lastFrame && signature == _lastSignature) return true;
            _lastFrame = Time.frameCount;
            _lastSignature = signature;

            try
            {
                if (parts.Length > 1 && parts[1].Equals("off", StringComparison.OrdinalIgnoreCase)) Down();
                else Up(Size(parts));
            }
            catch (Exception e) { Core.Log?.Warning($"sideloadsurface failed: {e.Message}"); }

            return true;
        }

        private static Vector2 Size(string[] parts)
        {
            var size = new Vector2(760f, 460f);
            if (parts.Length >= 3
                && float.TryParse(parts[1], out float w) && float.TryParse(parts[2], out float h)
                && w > 40f && h > 40f)
                size = new Vector2(w, h);
            return size;
        }

        private static void Up(Vector2 size)
        {
            Down();

            // Its own canvas rather than one borrowed from the scene, the same way DevOverlay does it. Hunting for a
            // canvas means deciding which of the several the scene holds is the one being drawn, and a panel
            // parented to a switched-off one mounts, renders and is invisible - which reads exactly like the surface
            // being broken. A canvas of its own is also the honest test: a surface is supposed to work in a panel
            // the engine knows nothing about.
            _panel = new GameObject("SideloadSurfaceTest");
            UnityEngine.Object.DontDestroyOnLoad(_panel);

            var canvas = _panel.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 31000;                        // above the phone, below the dev overlay
            _panel.AddComponent<UnityEngine.UI.CanvasScaler>().uiScaleMode =
                UnityEngine.UI.CanvasScaler.ScaleMode.ConstantPixelSize;
            _panel.AddComponent<UnityEngine.UI.GraphicRaycaster>();

            RectTransform rect = Paint.UiFactory.Rect("panel", _panel.transform);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;

            // No trailing dot: AppBundle.ResourceNameOf joins the prefix and the path with one itself, and a spare
            // one asks for `Sideload.Assets.selftest..index.html`. That resolves to nothing, and nothing is not an
            // error here - the page builds empty in three milliseconds and looks like a surface that mounted fine.
            // The bundle's own handlers, under this id. They are registered per app id, so the surface has none of
            // SelfTestApp's - and the page asks for the clock once a second, which without this is a warning per
            // second for as long as the surface is up.
            Script.Bridge.Handle(Id, "host.clock", (app, arg) =>
                DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));

            Script.Bridge.Handle(Id, "host.info", (app, arg) =>
                $"{app} on a surface, {size.x:0}x{size.y:0}, frame {Time.frameCount}");

            if (Host.Surfaces.Mount(rect, Id, "Sideload.Assets.selftest", typeof(SurfaceConsole).Assembly, 400f))
                Core.Log?.Msg($"sideloadsurface: '{Id}' is up at {size.x:0}x{size.y:0}. "
                              + "Take it down with 'sideloadsurface off'.");
            else
                Down();
        }

        private static void Down()
        {
            if (Host.Surfaces.IsMounted(Id))
            {
                Host.Surfaces.Unmount(Id);
                Core.Log?.Msg($"sideloadsurface: '{Id}' is down.");
            }

            if (_panel == null) return;
            UnityEngine.Object.Destroy(_panel);
            _panel = null;
        }
    }
}
#endif
