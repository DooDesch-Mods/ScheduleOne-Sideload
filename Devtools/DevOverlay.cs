// Fallback only. When SNITCH is compiled in, Sideload's panel lives in the Snitch overlay instead (Snitch/Probe.cs)
// - one overlay on one master key beats a second HUD painted next to it.
#if DEBUG && !SNITCH
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Sideload.Devtools
{
    /// <summary>
    /// The dev overlay: what the engine is doing, on screen, while the game runs.
    ///
    /// It exists because the two questions that actually come up while building an app - "did my edit reach the game?"
    /// and "is this page rebuilding once per change or once per frame?" - are both invisible from a screenshot and
    /// tedious to answer from a log. Both are one glance here.
    ///
    ///   F9   show/hide
    ///   F10  outline every box and text run, and rebuild so it takes effect
    ///
    /// Debug builds only, and on its own canvas so it survives a page that has failed to build.
    /// </summary>
    internal static class DevOverlay
    {
        private static GameObject _root;
        private static TextMeshProUGUI _text;
        private static bool _shown;
        private static float _next;

        internal static void Tick()
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.F9)) Toggle();

            // Ctrl is required: something else in this game presses F10 roughly once a second, and a bare binding
            // turned the outline toggle into a rebuild loop.
            if (UnityEngine.Input.GetKeyDown(KeyCode.F10) && Held(KeyCode.LeftControl, KeyCode.RightControl))
            {
                LayoutOverlay.Outlines = !LayoutOverlay.Outlines;
                Core.Log?.Msg($"[Sideload/dev] outlines {(LayoutOverlay.Outlines ? "on" : "off")}.");

                // A rebuild, not a reload: the files on disk have not changed, and a reload would throw away the
                // script's state along with the page.
                foreach (Host.WebView view in Host.WebView.Live) view.DebugRebuild();
            }

            if (!_shown || _text == null) return;

            // Four times a second: often enough to watch a rebuild storm, rarely enough to cost nothing.
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.25f;

            _text.text = Report();
        }

        private static bool Held(params KeyCode[] keys)
        {
            foreach (KeyCode key in keys)
                if (UnityEngine.Input.GetKey(key)) return true;
            return false;
        }

        private static void Toggle()
        {
            _shown = !_shown;

            if (_root == null) Build();
            if (_root != null) _root.SetActive(_shown);

            Core.Log?.Msg($"[Sideload/dev] overlay {(_shown ? "on" : "off")}.");
        }

        private static string Report()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("<b>SIDELOAD DEV</b>   F9 hide   Ctrl+F10 outlines ");
            sb.Append(LayoutOverlay.Outlines ? "<color=#7CE08A>on</color>" : "off").Append('\n');
            sb.Append($"<color=#8A8F9E>{Application.targetFrameRate switch { <= 0 => "uncapped", var f => f + " fps cap" }}, "
                      + $"{1f / Mathf.Max(Time.smoothDeltaTime, 0.0001f):0} fps</color>\n\n");

            if (Host.WebView.Live.Count == 0)
            {
                sb.Append("<color=#8A8F9E>no page mounted</color>");
                return sb.ToString();
            }

            foreach (Host.WebView view in Host.WebView.Live)
            {
                sb.Append(view.Stats).Append('\n');
                sb.Append("  ").Append(view.WatchReport()).Append("\n\n");
            }

            return sb.ToString();
        }

        private static void Build()
        {
            try
            {
                _root = new GameObject("sideload-devtools");
                UnityEngine.Object.DontDestroyOnLoad(_root);

                var canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 32000;                       // above the phone, above everything
                _root.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

                RectTransform panel = Paint.UiFactory.Rect("panel", _root.transform);
                panel.anchorMin = new Vector2(0f, 1f);
                panel.anchorMax = new Vector2(0f, 1f);
                panel.pivot = new Vector2(0f, 1f);
                panel.anchoredPosition = new Vector2(12f, -12f);
                panel.sizeDelta = new Vector2(520f, 260f);

                var background = panel.gameObject.AddComponent<Image>();
                background.color = new Color(0.04f, 0.05f, 0.07f, 0.88f);
                background.raycastTarget = false;                   // a debug panel must never eat a click

                RectTransform label = Paint.UiFactory.Rect("text", panel);
                Paint.UiFactory.Stretch(label, top: 10f, right: 12f, bottom: 10f, left: 12f);

                _text = label.gameObject.AddComponent<TextMeshProUGUI>();
                _text.font = Paint.FontRegistry.Resolve("game-ui", 400, Css.FontStyleKind.Normal);
                _text.fontSize = 15f;
                _text.color = new Color(0.93f, 0.94f, 0.96f, 1f);
                _text.raycastTarget = false;
                _text.richText = true;
                _text.alignment = TextAlignmentOptions.TopLeft;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[Sideload/dev] overlay could not be built: " + e.Message);
                _root = null;
            }
        }
    }
}
#endif
