namespace Sideload.Css
{
    /// <summary>
    /// Which declarations change only how a box is DRAWN, never where anything sits.
    ///
    /// This is the knowledge that lets an inline style written from script repaint one box instead of rebuilding the
    /// page - a rebuild destroys and recreates every GameObject, measured at roughly half a millisecond per box, so
    /// animating a transform through one costs a page-sized rebuild every frame.
    ///
    /// It lives with the CSS model rather than next to the script API because it is a fact about the properties
    /// themselves: the same split decides what a `:hover` rule may safely change, and stating it twice is how the two
    /// would drift apart.
    ///
    /// The list is deliberately short and closed, and it is governed by two conditions that BOTH have to hold.
    ///
    /// First, the property must not move anything: everything the flex pass reads is out, and so is every shorthand
    /// that can smuggle a length in - `border` carries a width, `font` a size.
    ///
    /// Second - and this is the one that is easy to miss - the property must not affect anything OUTSIDE the box
    /// being repainted. <c>Painter.Repaint</c> redraws exactly one box, so an INHERITED property cannot be on this
    /// list: `color` and `opacity` both cascade into descendants, and repainting only the element that was written
    /// would leave every child drawn in the old colour until something else forced a rebuild. They stay on the
    /// rebuild path, where they are correct. (`transform` is fine despite reaching descendants, because it reaches
    /// them through the Unity transform hierarchy rather than through the cascade - the children move with the
    /// parent for free.)
    ///
    /// When in doubt, leave it out: a missing entry costs a rebuild that used to happen anyway, a wrong one leaves
    /// the page drawing something it no longer says.
    /// </summary>
    internal static class PaintOnlyProperties
    {
        private static readonly HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "transform",
            "background", "background-color", "background-image",
            "border-color",
            "border-radius",
            "border-top-left-radius", "border-top-right-radius",
            "border-bottom-right-radius", "border-bottom-left-radius",
            "box-shadow",

            // An outline is drawn outside the box and takes no room, which is exactly what makes it safe here -
            // and it is the one property a focus ring is written with, so it changes on every tab press.
            "outline", "outline-width", "outline-color", "outline-style", "outline-offset",
        };

        /// <summary>
        /// True when writing this property can be answered with a repaint. A null or unknown name is false, which is
        /// the safe direction - an unrecognised property falls back to the rebuild it would have had.
        /// </summary>
        internal static bool Covers(string property) => property != null && Names.Contains(property);
    }
}
