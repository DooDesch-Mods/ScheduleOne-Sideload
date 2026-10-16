#if DEBUG
using System;
using System.Globalization;
using UnityEngine;

namespace Sideload.Devtools
{
    /// <summary>
    /// DEBUG-only: <c>sideloadgamma [hex]</c> - for every mounted page, say which canvas carries it and what a
    /// declared colour turns into on the way to the shader.
    ///
    /// Boxes reach the shader as raw mesh vertices and the box shader does no colour conversion of its own, so
    /// <see cref="Paint.BoxRenderer.ToVertex"/> pre-converts wherever the canvas will not. Which canvases those are
    /// is read from <c>vertexColorAlwaysGammaSpace</c> (see <see cref="Host.WebView.WantsLinearColors"/>), and this
    /// prints it next to the render mode and camera so a wrong answer shows which fact it came from.
    /// <c>sideloadgamma force on|off|auto</c> overrides the answer for every page, to compare both on screen.
    ///
    /// The uploaded value is the arithmetic <c>ToVertex</c> performs, not a mesh read-back - the useful unknown
    /// here is which surfaces convert, not whether the conversion works. Ground truth on screen stays the cheap
    /// comparison: one hex as <c>background</c> beside the same hex as <c>color</c>. Text goes through
    /// TextMeshPro's own conversion, so if the two look different, this path is the one that moved.
    /// </summary>
    internal static class GammaConsole
    {
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

        private static bool Dispatch(string[] parts)
        {
            if (parts.Length == 0) return false;
            if (!parts[0].Equals("sideloadgamma", StringComparison.OrdinalIgnoreCase)) return false;

            // Both SubmitCommand overloads are patched and the string one calls the list one, so a single
            // submission arrives twice. Same guard as KeyConsole and WheelConsole, for the same reason.
            string signature = string.Join(" ", parts);
            if (Time.frameCount == _lastFrame && signature == _lastSignature) return true;
            _lastFrame = Time.frameCount;
            _lastSignature = signature;

            try
            {
                if (parts.Length > 2 && parts[1].Equals("force", StringComparison.OrdinalIgnoreCase))
                {
                    string how = parts[2].ToLowerInvariant();
                    Host.WebView.ForceLinear = how == "on" ? true : how == "off" ? (bool?)false : null;
                    Core.Log?.Msg($"sideloadgamma: convertToLinear forced to {(Host.WebView.ForceLinear?.ToString() ?? "auto")} - reload the page to repaint.");
                    return true;
                }

                Color probe = new Color(0.5f, 0.5f, 0.5f, 1f);
                string probeText = "#808080";
                if (parts.Length > 1)
                {
                    if (!TryHex(parts[1], out probe))
                    {
                        Core.Log?.Msg("usage: sideloadgamma [hex] - for example 'sideloadgamma #3a3d42'.");
                        return true;
                    }
                    probeText = parts[1].StartsWith("#") ? parts[1] : "#" + parts[1];
                }

                Core.Log?.Msg($"sideloadgamma: colorSpace={QualitySettings.activeColorSpace}, probe {probeText}");

                var views = Host.WebView.Live;
                if (views == null || views.Count == 0)
                {
                    Core.Log?.Msg("  no page mounted - open the phone app or 'sideloadsurface' first.");
                    return true;
                }

                for (int i = 0; i < views.Count; i++) Report(views[i], probe, probeText);
            }
            catch (Exception e) { Core.Log?.Warning("sideloadgamma failed: " + e.Message); }

            return true;
        }

        private static void Report(Host.WebView view, Color probe, string probeText)
        {
            if (view == null) return;

            Canvas canvas = view.Root != null ? view.Root.GetComponentInParent<Canvas>() : null;
            bool wantsLinear = view.WantsLinearColors();

            string mode = canvas == null ? "NO CANVAS" : canvas.renderMode.ToString();
            Camera cam = canvas != null ? canvas.worldCamera : null;

            RenderTexture target = cam != null ? cam.targetTexture : null;
            string camText = cam == null
                ? "worldCamera=NONE"
                : $"worldCamera='{cam.name}' targetTexture=" +
                  (target == null ? "NONE" : $"'{target.name}' {target.width}x{target.height}");

            // What the painter would upload for this view right now. Not a mesh read-back - see the class remarks.
            Color uploaded = probe;
            if (wantsLinear && QualitySettings.activeColorSpace == ColorSpace.Linear)
            {
                uploaded = probe.linear;
                uploaded.a = probe.a;
            }

            Canvas root = canvas != null ? canvas.rootCanvas : null;
            string gammaText = root == null ? "" : $" vertexGamma={root.vertexColorAlwaysGammaSpace}";
            Core.Log?.Msg($"  [{view.AppId}] canvas='{(canvas == null ? "-" : canvas.name)}' mode={mode} {camText}{gammaText}");
            Core.Log?.Msg($"      wantsLinear={wantsLinear}  {probeText} uploaded as {Hex(uploaded)}"
                        + (wantsLinear ? "  (right only where the canvas does not convert)" : ""));
        }

        private static string Hex(Color c) =>
            "#" + Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255f).ToString("X2")
                + Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255f).ToString("X2")
                + Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255f).ToString("X2");

        private static bool TryHex(string text, out Color color)
        {
            color = Color.magenta;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string hex = text.Trim().TrimStart('#');
            if (hex.Length == 3)
                hex = new string(new[] { hex[0], hex[0], hex[1], hex[1], hex[2], hex[2] });
            if (hex.Length != 6) return false;

            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int packed)) return false;

            color = new Color(((packed >> 16) & 0xFF) / 255f, ((packed >> 8) & 0xFF) / 255f, (packed & 0xFF) / 255f, 1f);
            return true;
        }
    }
}
#endif
