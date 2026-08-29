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
    /// Boxes reach the shader as raw mesh vertices, which skips the sRGB-to-linear step uGUI does for its own
    /// meshes, so <see cref="Paint.BoxRenderer.ToVertex"/> does it instead. Whether that is right depends on
    /// whether something downstream converts back, and the only thing deciding that today is
    /// "is this canvas ScreenSpaceOverlay" - justified in a comment by the phone drawing into a render texture.
    /// That justification has never been measured, and a world-space clipboard gets the same answer as the phone
    /// while rendering a step dark, so this prints the facts the condition should have been written from:
    /// the render mode, the camera, and whether that camera has a target texture at all.
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

            // The claim the discriminator rests on. A camera with no target texture renders straight into the
            // frame, which is the same path an overlay canvas takes and the opposite of what the comment assumes.
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

            Core.Log?.Msg($"  [{view.AppId}] canvas='{(canvas == null ? "-" : canvas.name)}' mode={mode} {camText}");
            Core.Log?.Msg($"      wantsLinear={wantsLinear}  {probeText} uploaded as {Hex(uploaded)}"
                        + (wantsLinear ? "  (a step dark unless something downstream converts back)" : ""));
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
