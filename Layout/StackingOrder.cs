using Sideload.Css;
using Sideload.Model;

namespace Sideload.Layout
{
    // Unity-free, like everything under Css/, Dom/, Layout/ and Model/ - which is the whole reason this file exists
    // apart from the paint walk. The ORDER is the part with the bugs in it and can be tested headless; hanging
    // GameObjects off a Canvas in that order is the part that cannot.

    /// <summary>
    /// Which of a box's children is painted first, and which last.
    ///
    /// Before this, the answer was always "the order they were written in", and a page could not say otherwise: a
    /// menu that opened upwards was drawn under the row below it, and the only fix was to move the markup. More than
    /// one shipped app orders its DOM around that and explains why in its own source.
    ///
    /// What is implemented is the part of CSS that pays for itself. Appendix E of CSS 2.1 paints a negative stack
    /// level before the in-flow content (step 2), the in-flow content next (steps 3 to 5), and level 0 and upwards
    /// after it (steps 6 and 7). That is the whole of this file.
    ///
    /// What is NOT implemented, deliberately: stacking CONTEXTS. In a browser `opacity`, `transform`, `filter` and a
    /// dozen other properties each start one, which traps every descendant inside it - a child with `z-index: 999`
    /// under a faded parent cannot climb out. Honouring that means painting the tree in a different shape than it is
    /// walked in, and every clip, scroll area and hit target here is built during that walk. No Tailwind utility and
    /// no app in this repo needs it; a future reader looking for it should know it was weighed and left out rather
    /// than forgotten.
    ///
    /// The second deliberate omission is smaller: a positioned box with `z-index: auto` stays in document order
    /// instead of being lifted over its in-flow siblings the way step 6 says. Only a box that ASKED for a level gets
    /// one, so adding this property cannot resort a page that never mentions it.
    /// </summary>
    internal static class StackingOrder
    {
        /// <summary>
        /// The children of one box, in the order they should be painted.
        ///
        /// Returns the very same list when nothing asked to be moved, which is the overwhelming majority of boxes
        /// on any page - no copy, no sort, and nothing for the paint walk to notice.
        /// </summary>
        internal static List<LayoutNode> Of(List<LayoutNode> children)
        {
            if (children == null) return null;

            bool sort = false;
            for (int i = 0; i < children.Count; i++)
            {
                ComputedStyle style = children[i]?.Style;
                if (style == null || !style.ZIndex.HasValue) continue;

                if (style.Position != PositionKind.Static) sort = true;
                else ReportOnAStaticBox(style.ZIndex.Value);
            }

            if (!sort) return children;

            int count = children.Count;
            var levels = new Level[count];
            var order = new int[count];
            for (int i = 0; i < count; i++)
            {
                levels[i] = LevelOf(children[i]);
                order[i] = i;
            }

            Array.Sort(order, (a, b) =>
            {
                int byLevel = levels[a].CompareTo(levels[b]);

                // Document order settles every tie, and it is compared HERE rather than left to the sort to
                // preserve. Array.Sort is an introsort and is not stable: two overlays sharing a z-index would
                // otherwise be free to swap places from one paint to the next, which is a flicker with no cause
                // anywhere in the page.
                return byLevel != 0 ? byLevel : a.CompareTo(b);
            });

            var painted = new List<LayoutNode>(count);
            for (int i = 0; i < count; i++) painted.Add(children[order[i]]);
            return painted;
        }

        /// <summary>
        /// Where one child paints, as the pair CSS sorts by: the stack level, and whether the box actually asked
        /// for one.
        ///
        /// The second half is what separates `z-index: 0` on a positioned box - which paints OVER the in-flow
        /// content, step 6 sitting after steps 3 to 5 - from a box that never mentioned z-index and belongs in it.
        /// One number cannot express that, because both of them are zero.
        /// </summary>
        private readonly struct Level
        {
            internal readonly int Stack;
            internal readonly bool Declared;

            internal Level(int stack, bool declared)
            {
                Stack = stack;
                Declared = declared;
            }

            internal int CompareTo(in Level other)
            {
                if (Stack != other.Stack) return Stack < other.Stack ? -1 : 1;
                if (Declared == other.Declared) return 0;
                return Declared ? 1 : -1;
            }
        }

        /// <summary>
        /// The level a child paints on. Everything that has not earned one - no z-index, or a z-index on a box CSS
        /// gives no stack level to - is in-flow content at level zero.
        /// </summary>
        private static Level LevelOf(LayoutNode node)
        {
            ComputedStyle style = node?.Style;
            return style != null && style.ZIndex.HasValue && style.Position != PositionKind.Static
                ? new Level(style.ZIndex.Value, declared: true)
                : new Level(0, declared: false);
        }

        /// <summary>
        /// `z-index` on a box that is not positioned does nothing in CSS either, so it does nothing here - but it is
        /// said out loud, because this is the silent kind of gap: the rule is valid, a browser agrees that it changes
        /// nothing, and the author is left looking at markup that reads exactly like the working version.
        ///
        /// The message asks for `relative`, which is the cheapest of the three that work: this engine lays a relative
        /// box out exactly as a static one (LAYOUT-018), so adding it switches the ordering on and moves nothing.
        /// `absolute` would work too and would take the box out of the flow with it.
        /// </summary>
        private static void ReportOnAStaticBox(int level)
        {
            if (!Diagnostics.Listening) return;

            Diagnostics.Report(DiagnosticKind.ValueIgnored, "z-index",
                level.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "  (the box is not positioned, so it has no stack level - add `position: relative`)");
        }
    }
}
