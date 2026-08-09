using AngleSharp.Dom;

namespace Sideload.Dom
{
    /// <summary>
    /// Which element a pointer is actually on, given a way to ask whether a box covers it.
    ///
    /// Split out of the view and free of Unity for the usual reason: the rule is worth testing, and the thing that
    /// makes it hard to test - asking a live RectTransform whether it contains a screen point - is not the rule. The
    /// caller supplies that as a predicate; everything decided here is the walk.
    /// </summary>
    internal static class HitPath
    {
        /// <summary>
        /// The deepest descendant of <paramref name="root"/> that covers the point, or null when nothing below it
        /// does.
        ///
        /// Two rules, both taken from what a browser does.
        ///
        /// The LAST covering child at each level wins, because that is the one painted over its siblings. Taking the
        /// first would report the box underneath, which on a page where cards overlap is a different card.
        ///
        /// And the walk only descends into a child that covers the point, which is what keeps a clipped subtree out
        /// of the answer without knowing anything about clipping: a row scrolled out of its list has moved away from
        /// the point, and so has everything inside it.
        ///
        /// A box the caller cannot answer for - an element with no painted box, because it folded into its parent's
        /// text run or was never drawn - ends the descent down that branch, which is right: it has no area of its
        /// own to be clicked on.
        /// </summary>
        internal static IElement Deepest(IElement root, Func<IElement, bool> covers)
        {
            if (root == null || covers == null) return null;

            IElement found = null;

            for (IElement level = root; level != null;)
            {
                IElement next = null;

                foreach (IElement child in level.Children)
                    if (covers(child)) next = child;

                if (next == null) break;

                found = next;
                level = next;
            }

            return found;
        }
    }
}
