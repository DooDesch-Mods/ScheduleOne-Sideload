using Il2CppTMPro;
using Sideload.Css;
using Sideload.Layout;
using UnityEngine;

namespace Sideload.Paint
{
    /// <summary>
    /// Maps CSS font-family names onto the TMP font assets the game already ships. Measured on the live build: the
    /// house typeface is Open Sans in five weights plus italics, with Unity's LiberationSans as the safety net and a
    /// handful of decorative faces.
    /// </summary>
    internal static class FontRegistry
    {
        private static bool _scanned;
        private static readonly Dictionary<string, TMP_FontAsset> _byName =
            new Dictionary<string, TMP_FontAsset>(StringComparer.OrdinalIgnoreCase);
        private static TMP_FontAsset _fallback;

        internal static TMP_FontAsset Resolve(string family, int weight, FontStyleKind style)
        {
            Scan();

            bool italic = style == FontStyleKind.Italic;
            string wanted = (family ?? "game-ui").Trim().ToLowerInvariant();

            switch (wanted)
            {
                case "game-hand": return Find("Caveat-Regular") ?? Find("Handlee-Regular") ?? _fallback;
                case "game-comic": return Find(italic ? "ComicNeue-BoldItalic" : "ComicNeue-Bold") ?? _fallback;
                case "game-pixel": return Find("VPPixel-Simplified") ?? _fallback;
                case "game-segment": return Find("fs-sevegment") ?? _fallback;
            }

            // Open Sans covers the weight axis, so pick the nearest cut rather than letting TMP fake a bold.
            string cut = weight >= 700 ? "Bold"
                       : weight >= 600 ? "SemiBold"
                       : weight >= 500 ? "Medium"
                       : weight <= 300 ? "Light"
                       : "Regular";

            TMP_FontAsset hit = null;
            if (italic) hit = Find("OpenSans-" + cut + "Italic");
            hit ??= Find("OpenSans-" + cut);
            hit ??= Find("OpenSans-Regular");
            return hit ?? _fallback;
        }

        /// <summary>
        /// Exact name first, prefix only as a last resort. A loose prefix match is actively wrong here: asking for
        /// "OpenSans-SemiBold" also matches "OpenSans-SemiBoldItalic", and dictionary order decides which one wins -
        /// which is how `font-weight: 600` started rendering in italics.
        /// </summary>
        private static TMP_FontAsset Find(string name)
        {
            if (_byName.TryGetValue(name + " SDF", out TMP_FontAsset exact) && exact != null) return exact;
            if (_byName.TryGetValue(name, out exact) && exact != null) return exact;

            foreach (KeyValuePair<string, TMP_FontAsset> pair in _byName)
            {
                if (pair.Value == null) continue;
                if (pair.Key.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            }
            return null;
        }

        private static void Scan()
        {
            if (_scanned) return;
            _scanned = true;

            try
            {
                TMP_FontAsset[] fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                if (fonts == null) return;

                foreach (TMP_FontAsset font in fonts)
                {
                    if (font == null || string.IsNullOrEmpty(font.name)) continue;
                    _byName[font.name] = font;
                    if (_fallback == null && font.name.StartsWith("LiberationSans", StringComparison.OrdinalIgnoreCase))
                        _fallback = font;
                }

                if (_fallback == null && fonts.Length > 0) _fallback = fonts[0];
                Core.Log?.Msg($"[Sideload] font registry: {_byName.Count} asset(s), fallback '{_fallback?.name}'.");
            }
            catch (Exception e) { Core.Log?.Warning("[Sideload] font scan failed: " + e.Message); }
        }
    }

    /// <summary>
    /// Text measurement for the layout engine, backed by a single hidden TextMeshPro instance. One measurer is
    /// enough because measuring is synchronous: configure, ask, discard.
    /// </summary>
    internal sealed class TmpMeasure : IMeasureText
    {
        private const float Unbounded = 100000f;

        private readonly TextMeshProUGUI _probe;

        internal TmpMeasure(Transform parent)
        {
            // The probe has to stay ACTIVE: TextMeshPro sets itself up in Awake, which never runs on an inactive
            // object, and GetPreferredValues then answers from an uninitialised state - every line came back far too
            // short and the whole page overlapped itself. Parking it far outside the viewport hides it instead.
            RectTransform rt = UiFactory.Rect("sideload-measure", parent);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(10f, 10f);
            rt.anchoredPosition = new Vector2(-100000f, 100000f);

            _probe = rt.gameObject.AddComponent<TextMeshProUGUI>();
            _probe.raycastTarget = false;
        }

        public Size Measure(string text, ComputedStyle style, float availableWidth)
        {
            if (_probe == null || string.IsNullOrEmpty(text))
                return new Size(0f, style?.ResolvedLineHeight ?? 0f);

            try
            {
                Apply(_probe, style);

                float width = float.IsPositiveInfinity(availableWidth) || availableWidth <= 0f ? Unbounded : availableWidth;

                // Both halves matter and each one alone is wrong:
                //   * The rect has to carry the width, or GetPreferredValues answers for the UNWRAPPED line - it
                //     reported 679px of width where only 671 were available, i.e. it never wrapped.
                //   * The rect also needs real vertical room. An earlier attempt used (width, 0); TMP then wrapped but
                //     had nowhere to put the lines and reported a single one - 20.43, 17.74, 16.71 across runs instead
                //     of the correct 40.86.
                // Reading textInfo stays off limits either way; it corrupts the cached generation for later calls.
                _probe.rectTransform.sizeDelta = new Vector2(width, Unbounded);
                _probe.text = text;

                Vector2 size = _probe.GetPreferredValues(text, width, Unbounded);

                float measuredHeight = size.y;
                float measuredWidth = size.x;

                // Never hand back more than we were given: the layout engine treats the result as a fitted box, and a
                // wider answer would push the parent open.
                float w = float.IsPositiveInfinity(availableWidth) ? measuredWidth : Math.Min(measuredWidth, availableWidth);
                return new Size(w, measuredHeight);
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[Sideload] text measure failed: " + e.Message);
                return new Size(0f, style.ResolvedLineHeight);
            }
        }

        /// <summary>Push the CSS text properties onto a TMP component - shared by measuring and rendering so the two
        /// can never disagree.</summary>
        internal static void Apply(TMP_Text tmp, ComputedStyle s)
        {
            if (tmp == null || s == null) return;

            TMP_FontAsset font = FontRegistry.Resolve(s.FontFamily, s.FontWeight, s.FontStyle);
            if (font != null) tmp.font = font;

            tmp.fontSize = s.FontSize;
            tmp.color = new Color(s.Color.R, s.Color.G, s.Color.B, s.Color.A);
            tmp.characterSpacing = s.LetterSpacing;
            tmp.lineSpacing = 0f;
            tmp.richText = true;
            tmp.enableWordWrapping = s.WhiteSpace != WhiteSpaceKind.NoWrap;
            tmp.overflowMode = s.TextOverflowEllipsis ? TextOverflowModes.Ellipsis : TextOverflowModes.Overflow;

            // TMP folds both axes into one enum. Its "Top*" family is top-aligned, the unprefixed family is vertically
            // centred - which is what `align-items: center` means on a box whose only content is text, and the reason
            // a button label sits on the middle of the button instead of clinging to its top edge.
            bool middle = s.AlignItems == AlignKind.Center;

            tmp.alignment = s.TextAlign switch
            {
                TextAlignKind.Center => middle ? TextAlignmentOptions.Center : TextAlignmentOptions.Top,
                TextAlignKind.Right => middle ? TextAlignmentOptions.Right : TextAlignmentOptions.TopRight,
                _ => middle ? TextAlignmentOptions.Left : TextAlignmentOptions.TopLeft,
            };

            // CSS line-height is a box height per line; TMP's lineSpacing is an offset in font units, so the closest
            // honest mapping is to leave spacing alone and let the measured height carry it.
            tmp.fontStyle = FontStyles.Normal;
        }
    }
}
