using Il2CppTMPro;
using UnityEngine;

namespace Sideload.Devtools
{
    /// <summary>
    /// Debug-only measurements of the host environment. These answer questions the engine's constants depend on and
    /// that cannot be derived from the decompiled source - the real size of the phone's app container, and which TMP
    /// font assets the game actually ships (the pool `font-family` names are mapped onto).
    /// </summary>
    internal static class Probe
    {
        private static bool _fontsLogged;

        /// <summary>Log a RectTransform's laid-out size. Call after the canvas has updated, or the rect reads stale.</summary>
        internal static void LogRect(RectTransform rt, string label)
        {
            if (rt == null) { Core.Log?.Warning($"[Sideload/probe] {label}: rect is null"); return; }

            try
            {
                Canvas.ForceUpdateCanvases();
                Rect r = rt.rect;
                Vector2 scale = rt.lossyScale;
                Core.Log?.Msg($"[Sideload/probe] {label}: {r.width:0.##} x {r.height:0.##} " +
                              $"(pivot {rt.pivot.x:0.##},{rt.pivot.y:0.##}  lossyScale {scale.x:0.###},{scale.y:0.###})");
            }
            catch (Exception e) { Core.Log?.Warning($"[Sideload/probe] {label}: rect read failed: {e.Message}"); }
        }

        /// <summary>
        /// Log the geometry of every app panel in the AppsCanvas. The vanilla apps come in both orientations, so this
        /// is how "what does portrait mean here" gets answered by measurement instead of by assumption.
        /// </summary>
        internal static void LogAppPanels(Transform appsCanvas)
        {
            if (appsCanvas == null) { Core.Log?.Warning("[Sideload/probe] no AppsCanvas to inspect."); return; }

            try
            {
                Canvas.ForceUpdateCanvases();
                Core.Log?.Msg($"[Sideload/probe] AppsCanvas: {appsCanvas.childCount} panel(s)");

                for (int i = 0; i < appsCanvas.childCount; i++)
                {
                    Transform child = appsCanvas.GetChild(i);
                    var rt = child.GetComponent<RectTransform>();
                    if (rt == null) { Core.Log?.Msg($"[Sideload/probe]   {child.name}: no RectTransform"); continue; }

                    Vector3 euler = rt.localEulerAngles;
                    Core.Log?.Msg($"[Sideload/probe]   {child.name}: {rt.rect.width:0.#} x {rt.rect.height:0.#}  " +
                                  $"anchors {rt.anchorMin.x:0.##},{rt.anchorMin.y:0.##}-{rt.anchorMax.x:0.##},{rt.anchorMax.y:0.##}  " +
                                  $"sizeDelta {rt.sizeDelta.x:0.#},{rt.sizeDelta.y:0.#}  rotZ {euler.z:0.#}  active {child.gameObject.activeSelf}");
                }
            }
            catch (Exception e) { Core.Log?.Warning($"[Sideload/probe] AppsCanvas read failed: {e.Message}"); }
        }

        /// <summary>
        /// List every TMP font asset loaded in the build, once per session. The names become the `font-family` values
        /// the stylesheet can ask for.
        /// </summary>
        internal static void LogFonts()
        {
            if (_fontsLogged) return;
            _fontsLogged = true;

            try
            {
                TMP_FontAsset[] fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                if (fonts == null || fonts.Length == 0)
                {
                    Core.Log?.Msg("[Sideload/probe] no TMP font assets found");
                    return;
                }

                Core.Log?.Msg($"[Sideload/probe] {fonts.Length} TMP font asset(s):");
                foreach (TMP_FontAsset font in fonts)
                {
                    if (font == null) continue;
                    string face = "?";
                    try { face = font.faceInfo.familyName + " / " + font.faceInfo.styleName; } catch { }
                    Core.Log?.Msg($"[Sideload/probe]   {font.name}  [{face}]  pointSize={SafePointSize(font)}");
                }
            }
            catch (Exception e) { Core.Log?.Warning($"[Sideload/probe] font scan failed: {e.Message}"); }
        }

        private static string SafePointSize(TMP_FontAsset font)
        {
            try { return font.faceInfo.pointSize.ToString(); } catch { return "?"; }
        }
    }
}
