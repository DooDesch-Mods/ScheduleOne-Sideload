namespace Sideload.Input
{
    /// <summary>
    /// Where inside an element a pointer landed, in that element's own CSS pixels plus the same point as a 0..1
    /// fraction of its size.
    ///
    /// A plain struct with no Unity type in it, because it travels from the pointer layer through the view into the
    /// script engine, and the script side is deliberately buildable without an engine - the headless tests compile
    /// that half in a second and catch an accidental dependency there rather than in a game launch.
    ///
    /// The default value means "no position", which is the honest answer for the events that have none: `back`,
    /// `input`, `keydown`, `orientationchange`.
    /// </summary>
    internal readonly struct PointerSpot
    {
        internal PointerSpot(float offsetX, float offsetY, float width, float height)
        {
            OffsetX = offsetX;
            OffsetY = offsetY;

            // Guard the divide rather than the caller: a zero-sized box is normal (a collapsed row, a node the layout
            // gave nothing), and a NaN reaching JavaScript is far worse than a zero.
            NormX = width > 0f ? offsetX / width : 0f;
            NormY = height > 0f ? offsetY / height : 0f;
        }

        internal float OffsetX { get; }

        internal float OffsetY { get; }

        internal float NormX { get; }

        internal float NormY { get; }
    }
}
