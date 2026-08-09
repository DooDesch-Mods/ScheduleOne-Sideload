using UnityEngine;
using UnityEngine.EventSystems;

namespace Sideload.Input
{
    /// <summary>
    /// The pointer position as uGUI reported it, kept in screen coordinates with the camera it came through.
    ///
    /// Unconverted on purpose. The element that CAUGHT a click is not always the element that was clicked - a page
    /// that delegates puts one listener on the root and the root's hit target catches everything - so the view has
    /// to ask other rectangles whether they cover the same point. Under a hierarchy that is scaled, rotated for
    /// portrait and scrolled, the only answer that stays true is the one <see cref="RectTransformUtility"/> gives
    /// for the screen point against each rectangle's live transform. A point converted once, into any one box's
    /// space, would have to be un-converted again for every other box.
    ///
    /// This one carries Unity types and therefore stays out of <see cref="PointerSpot"/>, which crosses into the
    /// engine-free half.
    /// </summary>
    internal readonly struct PointerRay
    {
        internal PointerRay(Vector2 screen, Camera camera)
        {
            Screen = screen;
            Camera = camera;
            Valid = true;
        }

        internal Vector2 Screen { get; }

        /// <summary>Null for a Screen Space Overlay canvas, which is the usual case here and what the utility
        /// methods expect - passing the main camera instead skews every result.</summary>
        internal Camera Camera { get; }

        /// <summary>False for the default value: an event that carried no pointer data at all.</summary>
        internal bool Valid { get; }
    }
}
