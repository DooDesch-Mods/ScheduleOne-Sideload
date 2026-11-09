using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Il2CppScheduleOne.UI.Phone;

namespace Sideload.Phone
{
    /// <summary>
    /// Keeps a scrolling home screen inside the phone display when it is driven with a controller.
    ///
    /// A scroll mod (S1API) wraps the icon grid in a ScrollRect with a mask, but the game knows nothing about it:
    /// the home screen's UIPanel keeps <c>scrollRect</c> empty, so controller navigation moves the selection to an
    /// icon below the mask and the grid never follows. The selected icon still shows its frame, because the frame is
    /// the icon's <c>Outline</c> graphic, tinted to full alpha by the Button's Selected colour, and the prefab marks
    /// that graphic not maskable - the mask cannot hide it, so it hangs over the status bar and the phone frame. With
    /// a mouse nothing is ever selected, which is why only a controller shows it.
    ///
    /// Two repairs, both applied whenever the home screen comes up so icons added later are covered too: the panel is
    /// handed the scroll mod's ScrollRect, which turns on the game's own scroll-to-selection, and every icon frame is
    /// made maskable.
    /// </summary>
    [HarmonyPatch(typeof(HomeScreen), nameof(HomeScreen.SetCanvasActive))]
    internal static class HomeGamepadScroll
    {
        /// <summary>Room above and below the selected icon, so its label is in view too.</summary>
        private static readonly Vector2 Margin = new Vector2(0f, 40f);

        private static int _loggedHome;
        private static bool _loggedNoScroll;

#if DEBUG
        /// <summary>Set by <c>sideloadpad fix off</c> to show the unrepaired home screen next to the repaired one.</summary>
        internal static bool Disabled;

        /// <summary>Undo both repairs on a live home screen, for the before half of a comparison.</summary>
        internal static void Revert(HomeScreen home)
        {
            RectTransform icons = home?.appIconContainer;
            if (icons == null) { Core.Log?.Warning("sideloadpad fix off: no icon container."); return; }

            ScrollRect scroll = icons.GetComponentInParent<ScrollRect>();
            var panel = home.uiPanel;
            if (panel != null && scroll != null && panel.scrollRect == scroll) panel.scrollRect = null;

            int frames = 0;
            for (int i = 0; i < icons.childCount; i++)
            {
                Transform outline = icons.GetChild(i).Find("Outline");
                MaskableGraphic graphic = outline != null ? outline.GetComponent<MaskableGraphic>() : null;
                if (graphic == null || !graphic.maskable) continue;
                graphic.maskable = false;
                frames++;
            }
            Core.Log?.Msg($"sideloadpad fix off: panel scroll cleared, {frames} icon frame(s) unmaskable again.");
        }
#endif

        private static void Postfix(HomeScreen __instance, bool a)
        {
            if (!a || __instance == null) return;
#if DEBUG
            if (Disabled) { Core.Log?.Msg("home screen controller scroll skipped (sideloadpad fix off)."); return; }
#endif

            try { Apply(__instance); }
            catch (Exception e) { Core.Log?.Error("home screen controller scroll could not be set up: " + e); }
        }

        internal static void Apply(HomeScreen home)
        {
            RectTransform icons = home.appIconContainer;
            if (icons == null)
            {
                Core.Log?.Warning("home screen has no icon container - controller scroll not set up.");
                return;
            }

            ScrollRect scroll = icons.GetComponentInParent<ScrollRect>();
            if (scroll == null)
            {
                // Vanilla layout: the grid does not scroll, so nothing can leave the screen.
                if (!_loggedNoScroll) Core.Log?.Msg("home screen does not scroll - no controller scroll needed.");
                _loggedNoScroll = true;
                return;
            }

            var panel = home.uiPanel;
            if (panel == null)
            {
                Core.Log?.Warning("home screen has no UI panel - controller navigation cannot scroll it.");
                return;
            }

            bool wired = false;
            if (panel.scrollRect == null)
            {
                panel.scrollRect = scroll;
                if (panel.scrollMargin == Vector2.zero) panel.scrollMargin = Margin;
                wired = true;
            }
            else if (panel.scrollRect != scroll)
            {
                Core.Log?.Warning($"home panel already scrolls '{panel.scrollRect.name}', not '{scroll.name}' - left as it is.");
            }

            int frames = 0;
            for (int i = 0; i < icons.childCount; i++)
            {
                Transform outline = icons.GetChild(i).Find("Outline");
                MaskableGraphic graphic = outline != null ? outline.GetComponent<MaskableGraphic>() : null;
                if (graphic == null || graphic.maskable) continue;
                graphic.maskable = true;
                frames++;
            }

            int id = home.GetInstanceID();
            if (wired || frames > 0 || _loggedHome != id)
            {
                Core.Log?.Msg($"home screen scrolls with the controller: panel {(wired ? "wired to" : "uses")} '{scroll.name}', "
                              + $"{frames} icon frame(s) made maskable, {icons.childCount} icon(s).");
                _loggedHome = id;
            }
        }
    }
}
