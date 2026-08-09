using Sideload.Css;

namespace Sideload.Layout
{
    /// <summary>
    /// The layout engine: flexbox, CSS grid and absolute positioning, and nothing else.
    ///
    /// This file owns the box - sizing, the box model, out-of-flow children - and the flexbox algorithm.
    /// <see cref="GridLayout"/> owns the grid algorithm and is reached from <see cref="LayoutBox"/> whenever a
    /// box says `display: grid`; the two share this file's sizing pass and nothing else, which is what keeps
    /// either one readable.
    ///
    /// Deliberate simplifications, all of them documented promises rather than accidents:
    ///
    ///   * <b>Border-box sizing throughout.</b> `width` includes padding and border, which is what every app
    ///     stylesheet sets anyway and removes a whole class of "why is my box 24px too wide".
    ///   * <b>Multi-line packing starts at the cross start.</b> `align-content` is not implemented; wrapped lines
    ///     stack tightly with the cross gap between them.
    ///   * <b>Height never feeds back into width.</b> Widths resolve first, text is measured against a known width,
    ///     and heights follow. This is what keeps the pass finite instead of oscillating.
    /// </summary>
    internal static class FlexLayout
    {
        /// <summary>Lay out <paramref name="root"/> into a viewport. The root's width is definite; its height comes
        /// from <paramref name="availableHeight"/> unless the style overrides it.</summary>
        internal static void Compute(LayoutNode root, float availableWidth, float availableHeight, IMeasureText measure)
        {
            if (root == null) return;

            // Fill the viewport horizontally unless the root asks for a width of its own. The height stays content-
            // driven, exactly as `height: auto` means everywhere else - a page that wants to fill the screen says so
            // with `body { height: 100% }`, which is what the shipped base stylesheet does and what every web author
            // already expects. Forcing it here would silently break auto-height for every other box.
            float forcedWidth = root.Style.Width.IsDefinite ? float.NaN : availableWidth;

            LayoutBox(root, availableWidth, availableHeight, measure, forcedWidth);
            root.X = 0f;
            root.Y = 0f;

            LayoutFixed(root, availableWidth, availableHeight, measure);
        }

        /// <summary>
        /// Place every <c>position: fixed</c> box against the VIEWPORT, wherever in the tree it was written.
        ///
        /// This runs after the page is otherwise laid out, and separately from the main pass, for one reason: a
        /// containing block is walked DOWN the tree, and the viewport is not one of those - it is the same rectangle
        /// for a box nested ten deep as for a child of the root. Doing it here also makes the result independent of
        /// how often <see cref="LayoutChildren"/> re-ran for an ancestor (it runs at least twice for any box whose
        /// height gets clamped), which a collect-as-you-go list would not be.
        ///
        /// The resulting X/Y are therefore viewport coordinates, NOT parent-relative like every other node - which is
        /// exactly what the painter needs, because it reparents these to the view root.
        /// </summary>
        private static void LayoutFixed(LayoutNode node, float viewWidth, float viewHeight, IMeasureText measure)
        {
            foreach (LayoutNode child in node.Children)
            {
                if (child.Style.Display == DisplayKind.None) continue;

                if (child.Style.Position == PositionKind.Fixed)
                    LayoutAbsolute(child, viewWidth, viewHeight, measure);

                // Descend regardless: a fixed box may be written inside any subtree, and one nested inside another
                // fixed box is still measured against the viewport - there is only ever the one top layer.
                LayoutFixed(child, viewWidth, viewHeight, measure);
            }
        }

        /// <summary>
        /// Size one node and position its children. <paramref name="availWidth"/>/<paramref name="availHeight"/> are
        /// the containing block's content box; a forced size wins over the style (used for the root and for children
        /// whose size a flex line or a grid cell already decided).
        ///
        /// This is also where the two layout algorithms part company: everything above this point is the same for
        /// both, and the only thing `display: grid` changes is which of them places the children. Internal rather
        /// than private because <see cref="GridLayout"/> sizes its items through exactly this entry point.
        /// </summary>
        internal static void LayoutBox(LayoutNode node, float availWidth, float availHeight, IMeasureText measure,
                                       float forcedWidth = float.NaN, float forcedHeight = float.NaN)
        {
            ComputedStyle s = node.Style;

            if (s.Display == DisplayKind.None)
            {
                node.Width = 0f;
                node.Height = 0f;
                node.Baseline = float.NaN;
                return;
            }

            float width = !float.IsNaN(forcedWidth) ? forcedWidth : ResolveOrNaN(s.Width, availWidth);
            float height = !float.IsNaN(forcedHeight) ? forcedHeight : ResolveOrNaN(s.Height, availHeight);

            float padH = Horizontal(s.Padding, availWidth) + Horizontal(s.BorderWidth, availWidth);
            float padV = Vertical(s.Padding, availWidth) + Vertical(s.BorderWidth, availWidth);

            // Width first: text needs a definite width before it can report a height.
            if (float.IsNaN(width))
            {
                float outerAvail = availWidth - Horizontal(s.Margin, availWidth);
                width = node.IsTextLeaf
                    ? Math.Min(measure.Measure(node.Text, s, float.PositiveInfinity).Width + padH, Math.Max(outerAvail, 0f))
                    : IntrinsicWidth(node, measure, availWidth) + padH;
            }
            width = Clamp(width, s.MinWidth, s.MaxWidth, availWidth);
            width = Math.Max(width, 0f);

            float contentW = Math.Max(width - padH, 0f);
            float contentH = float.IsNaN(height) ? float.NaN : Math.Max(height - padV, 0f);

            float borderAndPadTop = s.BorderWidth.Top.Resolve(availWidth) + s.Padding.Top.Resolve(availWidth);

            float usedContentH;
            if (node.IsTextLeaf)
            {
                Size text = measure.Measure(node.Text, s, contentW);
                usedContentH = text.Height;
                node.Baseline = float.IsNaN(text.Baseline) ? float.NaN : borderAndPadTop + text.Baseline;

                // Only when the text needs MORE room than the box has. Anything else is the ordinary case and the
                // painter can size the text to the content box without asking.
                node.TextWrapWidth = text.WrapWidth > contentW + 0.01f ? text.WrapWidth : float.NaN;
            }
            else
            {
                usedContentH = PlaceChildren(node, contentW, contentH, availWidth, measure);
            }

            if (float.IsNaN(height)) height = usedContentH + padV;
            height = Clamp(height, s.MinHeight, s.MaxHeight, availHeight);
            height = Math.Max(height, 0f);

            // A clamped height changes the content box the children were placed in, so stretch them once more.
            float finalContentH = Math.Max(height - padV, 0f);
            if (!node.IsTextLeaf && Math.Abs(finalContentH - usedContentH) > 0.01f)
                PlaceChildren(node, contentW, finalContentH, availWidth, measure);

            // After the LAST placement, so the children's Y values are the ones that survive.
            if (!node.IsTextLeaf) node.Baseline = BaselineFromChildren(node);

            // A text box taller than its text does not draw the text at the top: the painter hands TMP a centred
            // alignment whenever the box says `align-items: center`, and the baseline moves down with it. Reading
            // the baseline off the measurement alone would put it where the text ISN'T, which is worse than having
            // no baseline - the item would be aligned confidently and wrongly.
            else if (!float.IsNaN(node.Baseline) && s.AlignItems == AlignKind.Center)
            {
                float slack = Math.Max(height - padV, 0f) - usedContentH;
                if (slack > 0f) node.Baseline += slack * 0.5f;
            }

            node.Width = width;
            node.Height = height;
        }

        /// <summary>
        /// A container's own baseline: the first in-flow child that has one, in document order, plus wherever that
        /// child ended up. Recursive by construction - the child's baseline was filled in the same way - which is
        /// what lets `align-items: baseline` line up a label that happens to be wrapped in two divs with one that
        /// is bare text.
        ///
        /// Deliberate narrowings, both of them cases where CSS is finer than any app stylesheet needs:
        ///   * <b>Document order, not flex order.</b> `row-reverse` takes its baseline from the item written first
        ///     rather than the one drawn first.
        ///   * <b>A box with no text anywhere has no baseline</b> and is aligned by its bottom margin edge instead
        ///     (see <see cref="PlaceLine"/>), which is what CSS calls a synthesized baseline.
        /// </summary>
        private static float BaselineFromChildren(LayoutNode node)
        {
            foreach (LayoutNode child in node.Children)
            {
                if (child.Style.Display == DisplayKind.None) continue;
                if (child.Style.Position != PositionKind.Static && child.Style.Position != PositionKind.Relative) continue;
                if (float.IsNaN(child.Baseline)) continue;

                return child.Y + child.Baseline;
            }

            return float.NaN;
        }

        /// <summary>
        /// Which algorithm places this box's children. The one dispatch point in the engine, so a box can never
        /// end up laid out by the algorithm its `display` did not ask for.
        /// </summary>
        private static float PlaceChildren(LayoutNode node, float contentW, float contentH, float percentBasis, IMeasureText measure) =>
            node.Style.Display == DisplayKind.Grid
                ? GridLayout.LayoutChildren(node, contentW, contentH, percentBasis, measure)
                : LayoutChildren(node, contentW, contentH, percentBasis, measure);

        /// <summary>
        /// Place the children inside a content box and report how tall they ended up. Returns the used cross extent
        /// so the caller can size an auto-height box around them.
        /// </summary>
        private static float LayoutChildren(LayoutNode node, float contentW, float contentH, float percentBasis, IMeasureText measure)
        {
            ComputedStyle s = node.Style;
            bool row = s.FlexDirection == FlexDirection.Row || s.FlexDirection == FlexDirection.RowReverse;
            bool reverse = s.FlexDirection == FlexDirection.RowReverse || s.FlexDirection == FlexDirection.ColumnReverse;

            var flow = new List<LayoutNode>();
            var absolute = new List<LayoutNode>();
            foreach (LayoutNode child in node.Children)
            {
                if (child.Style.Display == DisplayKind.None) { child.Width = 0f; child.Height = 0f; continue; }
                if (child.Style.Position == PositionKind.Absolute) absolute.Add(child);
                // A fixed child is out of the flow AND out of this box entirely - Compute lays it out against the
                // viewport once the page is otherwise finished, so it is skipped rather than collected here.
                else if (child.Style.Position != PositionKind.Fixed) flow.Add(child);
            }

            // row-gap separates lines, column-gap separates items in a line - which of the two is the main axis
            // depends on the direction.
            float mainGap = (row ? s.ColumnGap : s.RowGap).Resolve(contentW);
            float crossGap = (row ? s.RowGap : s.ColumnGap).Resolve(contentW);

            float mainAvail = row ? contentW : contentH;
            float crossAvail = row ? contentH : contentW;

            var items = new List<Item>(flow.Count);
            foreach (LayoutNode child in flow)
                items.Add(BuildItem(child, row, mainAvail, crossAvail, contentW, s.AlignItems, measure));

            // A wrapping container decides where the lines break from the sizes the items are holding, so a floor
            // that has been put off has to be paid before the break - unlike on a single line, where the flex pass
            // is the first thing that can be affected by it.
            if (s.FlexWrap != FlexWrap.NoWrap)
                foreach (Item item in items)
                    item.MainSize = Math.Clamp(item.BaseSize, MinOf(item, measure), item.MaxMain);

            List<List<Item>> lines = BreakIntoLines(items, s.FlexWrap, mainAvail, mainGap);

            float crossCursor = 0f;
            float widest = 0f;

            foreach (List<Item> line in lines)
            {
                ResolveFlexibleLengths(line, mainAvail, mainGap, measure);

                // Give every item its main size, then read back the cross size it needs.
                foreach (Item item in line)
                {
                    float w = row ? item.MainSize : float.NaN;
                    float h = row ? float.NaN : item.MainSize;
                    LayoutBox(item.Node, contentW, contentH, measure, w, h);
                    item.CrossSize = row ? item.Node.Height : item.Node.Width;
                }

                float lineCross = 0f;
                foreach (Item item in line)
                    lineCross = Math.Max(lineCross, item.CrossSize + item.CrossMarginStart + item.CrossMarginEnd);

                // A single line in a container with a known cross size IS that size - not the larger of the two. Taking
                // the maximum would let one oversized item widen the line for all its siblings, so a card whose
                // intrinsic width exceeds the content box would drag every sibling past the padding with it. An item
                // that does not fit has to overflow on its own.
                if (lines.Count == 1 && !float.IsNaN(crossAvail)) lineCross = crossAvail;

                // Stretch pass: an item with no cross size of its own takes the line's.
                foreach (Item item in line)
                {
                    AlignKind align = item.Node.Style.AlignSelf != AlignKind.Auto ? item.Node.Style.AlignSelf : s.AlignItems;
                    if (align != AlignKind.Stretch) continue;

                    // An auto margin on the cross axis absorbs the line's spare room itself, so the item keeps the
                    // size its own content asked for and gets centred or pushed instead of stretched. Flexbox 9.6
                    // stretches only when NEITHER cross margin is auto, which is what makes `margin: 0 auto` centre
                    // a box in a column rather than widening it to the full line and centring nothing.
                    if (item.CrossMarginStartAuto || item.CrossMarginEndAuto) continue;

                    Len crossSizeProperty = row ? item.Node.Style.Height : item.Node.Style.Width;
                    if (crossSizeProperty.IsDefinite) continue;

                    float target = Math.Max(lineCross - item.CrossMarginStart - item.CrossMarginEnd, 0f);
                    if (Math.Abs(target - item.CrossSize) < 0.01f) continue;

                    // Only re-force the main size when the item actually asked for one. Pinning it to the value
                    // measured before the stretch is wrong whenever the cross size feeds back into it: a paragraph
                    // stretched to a narrower width wraps onto another line, and forcing the old height back made the
                    // card size itself one line short and the text spill out.
                    // Force it only when the main size was actually DECIDED: an explicit size, an explicit basis, or a
                    // grow/shrink pass that moved it off the base size. A size that merely came out of the content has
                    // to be measured again at the new cross size. (flex-shrink defaults to 1, so asking "is it
                    // shrinkable" would be true for everything and pin every item.)
                    Len mainSizeProperty = row ? item.Node.Style.Width : item.Node.Style.Height;
                    bool decided = mainSizeProperty.IsDefinite
                                   || item.Node.Style.FlexBasis.IsDefinite
                                   || Math.Abs(item.MainSize - item.BaseSize) > 0.01f;
                    float mainForced = decided ? item.MainSize : float.NaN;

                    LayoutBox(item.Node, contentW, contentH, measure,
                              row ? mainForced : target,
                              row ? target : mainForced);

                    item.CrossSize = row ? item.Node.Height : item.Node.Width;
                    if (float.IsNaN(mainForced)) item.MainSize = row ? item.Node.Width : item.Node.Height;
                }

                // Baseline alignment can need MORE room than the tallest item: two items whose baselines are at
                // different depths push each other apart. Measured after the stretch pass, because an item that
                // was just stretched has a new baseline, and applied to the line size, because that is the only
                // thing a browser grows here - the items themselves keep the size they asked for.
                float ascent = BaselineBand(line, s, row, out float descent);
                if (!float.IsNaN(ascent)) lineCross = Math.Max(lineCross, ascent + descent);

                PlaceLine(line, s, row, reverse, mainAvail, mainGap, crossCursor, lineCross, ascent);

                float lineExtent = 0f;
                foreach (Item item in line)
                    lineExtent = Math.Max(lineExtent, (row ? item.Node.Y + item.Node.Height : item.Node.X + item.Node.Width));

                widest = Math.Max(widest, lineExtent);
                crossCursor += lineCross + crossGap;
            }

            float usedCross = lines.Count == 0 ? 0f : Math.Max(widest, crossCursor - crossGap);

            // Measure the content extent BEFORE shifting anything, otherwise the padding would be counted twice:
            // once here and again when LayoutBox adds it back to reach the border box. Out-of-flow children are
            // excluded on purpose - an absolutely positioned child never stretches its parent.
            float flowBottom = 0f;
            foreach (LayoutNode child in flow) flowBottom = Math.Max(flowBottom, child.Y + child.Height);

            FinishContainer(s, flow, absolute, contentW, contentH, percentBasis, measure);

            // For a row container the used height is the stacked line extent; for a column it is where the main axis
            // ended.
            return row ? usedCross : flowBottom;
        }

        /// <summary>
        /// The tail every container shares once its in-flow children are placed: move them out of the content box
        /// into the parent's own coordinate space, then lay out the out-of-flow ones.
        ///
        /// Flow children clear border and padding; absolutely positioned ones only the border, because their
        /// containing block is the padding box. Grid needs both steps exactly as flexbox does, and one copy is what
        /// keeps `position: absolute` from meaning two different things depending on the parent's `display`.
        ///
        /// The caller measures its own content extent BEFORE calling this, otherwise the padding would be counted
        /// twice - once there and again when <see cref="LayoutBox"/> adds it back to reach the border box.
        /// </summary>
        internal static void FinishContainer(ComputedStyle s, List<LayoutNode> flow, List<LayoutNode> absolute,
                                             float contentW, float contentH, float percentBasis, IMeasureText measure)
        {
            float borderLeft = s.BorderWidth.Left.Resolve(percentBasis);
            float borderTop = s.BorderWidth.Top.Resolve(percentBasis);
            float padLeft = s.Padding.Left.Resolve(percentBasis);
            float padTop = s.Padding.Top.Resolve(percentBasis);

            foreach (LayoutNode child in flow)
            {
                child.X += borderLeft + padLeft;
                child.Y += borderTop + padTop;
                if (child.Style.Position == PositionKind.Relative) OffsetRelative(child, contentW, contentH);
            }

            if (absolute.Count == 0) return;

            float paddingBoxW = contentW + padLeft + s.Padding.Right.Resolve(percentBasis);
            float paddingBoxH = float.IsNaN(contentH)
                ? float.NaN
                : contentH + padTop + s.Padding.Bottom.Resolve(percentBasis);

            foreach (LayoutNode child in absolute)
            {
                LayoutAbsolute(child, paddingBoxW, paddingBoxH, measure);
                child.X += borderLeft;
                child.Y += borderTop;
            }
        }

        private sealed class Item
        {
            internal LayoutNode Node;
            internal float BaseSize;      // flex base size, before growing or shrinking
            internal float ContentMain;   // what the item's own content needs - the floor for an auto minimum
            internal float MainSize;      // resolved
            internal float CrossSize;
            internal float MainMarginStart, MainMarginEnd;
            internal float CrossMarginStart, CrossMarginEnd;

            // An auto margin resolves to zero everywhere a length is wanted - sizing, line breaking, the flex pass -
            // and only becomes a number once the line knows how much room is left over. Remembering WHICH margins
            // were written `auto` is the only way to tell that apart from a genuine zero afterwards.
            internal bool MainMarginStartAuto, MainMarginEndAuto;
            internal bool CrossMarginStartAuto, CrossMarginEndAuto;

            internal float MinMain, MaxMain;

            /// <summary>The row minimum has not been measured yet - see <see cref="MinOf"/>.</summary>
            internal bool MinPending;
        }

        private static Item BuildItem(LayoutNode child, bool row, float mainAvail, float crossAvail,
                                      float percentBasis, AlignKind parentAlign, IMeasureText measure)
        {
            ComputedStyle cs = child.Style;

            Len mainMarginStart = row ? cs.Margin.Left : cs.Margin.Top;
            Len mainMarginEnd = row ? cs.Margin.Right : cs.Margin.Bottom;
            Len crossMarginStart = row ? cs.Margin.Top : cs.Margin.Left;
            Len crossMarginEnd = row ? cs.Margin.Bottom : cs.Margin.Right;

            var item = new Item
            {
                Node = child,
                MainMarginStart = mainMarginStart.Resolve(percentBasis),
                MainMarginEnd = mainMarginEnd.Resolve(percentBasis),
                CrossMarginStart = crossMarginStart.Resolve(percentBasis),
                CrossMarginEnd = crossMarginEnd.Resolve(percentBasis),
                MainMarginStartAuto = mainMarginStart.IsAuto,
                MainMarginEndAuto = mainMarginEnd.IsAuto,
                CrossMarginStartAuto = crossMarginStart.IsAuto,
                CrossMarginEndAuto = crossMarginEnd.IsAuto,
            };

            Len mainSizeProperty = row ? cs.Width : cs.Height;
            Len minProperty = row ? cs.MinWidth : cs.MinHeight;
            Len maxProperty = row ? cs.MaxWidth : cs.MaxHeight;

            AlignKind selfAlign = cs.AlignSelf != AlignKind.Auto ? cs.AlignSelf : parentAlign;
            bool stretchedCross = !row && selfAlign == AlignKind.Stretch && !cs.Width.IsDefinite && !float.IsNaN(crossAvail)
                                  && !item.CrossMarginStartAuto && !item.CrossMarginEndAuto;

            float basis = ResolveOrNaN(cs.FlexBasis, mainAvail);

            // Where the basis came from decides whether the content still has to be measured for the automatic
            // minimum below. A basis taken from `flex-basis` says nothing about how big the content is.
            bool basisFromFlexBasis = !float.IsNaN(basis);
            if (float.IsNaN(basis)) basis = ResolveOrNaN(mainSizeProperty, mainAvail);
            bool basisMeasured = false;

            if (float.IsNaN(basis))
            {
                // No size to go on: measure what the item wants. Along a row that is its intrinsic width; down a
                // column it is the height it takes at the container's width.
                //
                // An item that will be stretched has to be measured AT its stretched width, not at the width it would
                // pick for itself. Measuring first and stretching afterwards reports the height of a paragraph that
                // never wrapped, and the flex pass then hands out space that the final, taller box does not fit into.
                LayoutBox(child, row ? mainAvail : crossAvail, row ? crossAvail : mainAvail, measure,
                          stretchedCross ? crossAvail : float.NaN);
                basis = row ? child.Width : child.Height;
                basisMeasured = true;
            }

            item.BaseSize = Math.Max(basis, 0f);

            // The content size the automatic minimum needs. When the basis was measured it already IS the content;
            // when it came from an explicit width or height, that declared size is deliberately the floor (see
            // AutomaticMinimum). Only a `flex-basis` leaves us with a number that says nothing about the content -
            // and `flex: 1` and `flex: 0` both set one, so this is the common case, not the exotic one.
            item.ContentMain = item.BaseSize;

            if (basisFromFlexBasis && !basisMeasured && !row && !minProperty.IsDefinite
                && cs.OverflowX == OverflowKind.Visible && cs.OverflowY == OverflowKind.Visible)
            {
                LayoutBox(child, crossAvail, mainAvail, measure, stretchedCross ? crossAvail : float.NaN);
                item.ContentMain = child.Height;
            }

            item.MinMain = AutomaticMinimum(item, minProperty, row, mainAvail, out bool pending);
            item.MinPending = pending;
            item.MaxMain = maxProperty.IsDefinite ? maxProperty.Resolve(mainAvail) : float.PositiveInfinity;

            // A floor that has not been measured yet does not clamp here. It cannot bind in the ordinary cases -
            // a measured base size IS the max-content width and a declared one is its own floor - and the case
            // where it can (`flex: 1`, whose basis is 0%) is caught before the lines are broken.
            item.MainSize = Math.Clamp(item.BaseSize, pending ? 0f : item.MinMain, item.MaxMain);
            return item;
        }

        /// <summary>
        /// The item's minimum, measuring it first if that was put off.
        ///
        /// Along a row the floor is the min-content width, and finding it means walking the item's subtree and
        /// asking the font about each piece of text in it. That is worth doing once for an item about to be
        /// squeezed and wasted on the many that are not, so <see cref="AutomaticMinimum"/> only marks it and the
        /// answer is worked out at the first read - which for a row that fits never comes at all.
        /// </summary>
        private static float MinOf(Item item, IMeasureText measure)
        {
            if (!item.MinPending) return item.MinMain;

            item.MinPending = false;

            // The content size suggestion, then capped by the declared width. Both halves are CSS Flexbox 4.5:
            // an item may be squeezed down to what is inside it, and never past a width its author wrote down -
            // whichever of the two is the SMALLER floor, so a wide box with narrow content still shrinks.
            float content = GridLayout.MinContentWidth(item.Node, measure, honourDeclaredWidth: false);
            if (item.Node.Style.Width.IsDefinite) content = Math.Min(content, item.ContentMain);

            item.MinMain = content;
            return item.MinMain;
        }

        /// <summary>
        /// The floor a flex item may be shrunk to. `min-width`/`min-height` default to `auto`, and auto does NOT mean
        /// zero: CSS Flexbox 4.5 gives an item a content-based minimum so a column of text can never be squeezed
        /// smaller than the text inside it. Without this a page whose children add up to more than the viewport
        /// collapses every box a little, and every paragraph spills out of the card that holds it.
        ///
        /// The two axes take it from different places, and the difference is the spec's, not a shortcut:
        ///   * <b>Down a COLUMN</b> the floor is <see cref="Item.ContentMain"/> - the height the content came out
        ///     at. Text cannot be made shorter than the lines it wrapped into.
        ///   * <b>Along a ROW</b> it is the MIN-content width: the longest run with no break point in it, capped
        ///     by a declared width where there is one. Not the measured width, which is the MAX-content width and
        ///     would stop a row from ever shrinking. This needs the font, so it is deferred - see
        ///     <see cref="MinOf"/> - and <paramref name="pending"/> says so.
        ///
        /// One deliberate narrowing stays, and only down the column: <b>an explicit height is never shrunk past.</b>
        /// The spec takes the smaller of the declared size and the content, which would let `height: 96px` collapse
        /// to the text inside it; honouring the number the author wrote down is the less surprising of the two.
        /// Along a row the same reading would break `flex-shrink` outright - a declared width would pin the item -
        /// so there the spec is followed exactly.
        ///
        /// The floor is <see cref="Item.ContentMain"/>, NOT the flex base size. Those differ exactly when the basis
        /// came from `flex-basis` - which `flex: 1` and `flex: 0` both set - and reading the base size there gave
        /// `flex: 0` a minimum of zero, so a column of sized boxes collapsed into nothing instead of holding at its
        /// content. That was this renderer disagreeing with every browser, not a narrowing of the spec.
        /// </summary>
        private static float AutomaticMinimum(Item item, Len minProperty, bool row, float mainAvail, out bool pending)
        {
            pending = false;
            if (minProperty.IsDefinite) return minProperty.Resolve(mainAvail);

            // A scroll container has an automatic minimum of zero - it is allowed to be smaller than its content
            // precisely because it can scroll.
            ComputedStyle cs = item.Node.Style;
            if (cs.OverflowY != OverflowKind.Visible || cs.OverflowX != OverflowKind.Visible) return 0f;

            if (!row) return item.ContentMain;

            pending = true;
            return 0f;
        }

        private static List<List<Item>> BreakIntoLines(List<Item> items, FlexWrap wrap, float mainAvail, float mainGap)
        {
            var lines = new List<List<Item>>();
            if (items.Count == 0) return lines;

            if (wrap == FlexWrap.NoWrap || float.IsNaN(mainAvail))
            {
                lines.Add(items);
                return lines;
            }

            var current = new List<Item>();
            float used = 0f;

            foreach (Item item in items)
            {
                float outer = item.MainSize + item.MainMarginStart + item.MainMarginEnd;
                float withGap = current.Count > 0 ? used + mainGap + outer : outer;

                if (current.Count > 0 && withGap > mainAvail + 0.01f)
                {
                    lines.Add(current);
                    current = new List<Item>();
                    used = outer;
                }
                else used = withGap;

                current.Add(item);
            }

            if (current.Count > 0) lines.Add(current);
            if (wrap == FlexWrap.WrapReverse) lines.Reverse();
            return lines;
        }

        /// <summary>
        /// Grow into the free space or shrink to fit - the heart of flexbox, and the one place where a single pass is
        /// not enough. Clamping an item to its minimum takes space back out of the pool, so the items that still have
        /// room have to absorb it. The loop freezes every item that hit a limit and redistributes what is left over
        /// the rest, which is what makes "one scrollable box soaks up the whole overflow" come out right instead of
        /// shaving a few pixels off every box on the page. Follows CSS Flexbox 9.7.
        /// </summary>
        private static void ResolveFlexibleLengths(List<Item> line, float mainAvail, float mainGap, IMeasureText measure)
        {
            if (line.Count == 0 || float.IsNaN(mainAvail)) return;

            int count = line.Count;

            // Everything that does not flex: gaps and margins are consumed before any item gets a share.
            float outerFixed = mainGap * (count - 1);
            foreach (Item item in line) outerFixed += item.MainMarginStart + item.MainMarginEnd;

            float baseTotal = 0f;
            foreach (Item item in line) baseTotal += item.BaseSize;

            bool growing = baseTotal + outerFixed < mainAvail;
            var frozen = new bool[count];
            var unclamped = new float[count];

            for (int i = 0; i < count; i++)
            {
                Item item = line[i];
                item.MainSize = Math.Clamp(item.BaseSize, MinOf(item, measure), item.MaxMain);

                // An item with no flex factor in the active direction keeps its base size, clamped.
                float factor = growing ? item.Node.Style.FlexGrow : item.Node.Style.FlexShrink;
                if (factor <= 0f) frozen[i] = true;
            }

            // Each pass freezes at least one item, so the line length bounds the iteration count.
            for (int pass = 0; pass <= count; pass++)
            {
                float used = outerFixed;
                float weightTotal = 0f;
                int flexible = 0;

                for (int i = 0; i < count; i++)
                {
                    Item item = line[i];
                    if (frozen[i]) { used += item.MainSize; continue; }

                    used += item.BaseSize;
                    flexible++;
                    weightTotal += Weight(item, growing);
                }

                if (flexible == 0 || weightTotal <= 0f) return;

                float free = mainAvail - used;
                if (Math.Abs(free) < 0.01f) return;

                float violation = 0f;
                for (int i = 0; i < count; i++)
                {
                    if (frozen[i]) continue;
                    Item item = line[i];

                    unclamped[i] = item.BaseSize + Weight(item, growing) / weightTotal * free;
                    item.MainSize = Math.Clamp(unclamped[i], MinOf(item, measure), item.MaxMain);
                    violation += item.MainSize - unclamped[i];
                }

                // No item was clamped: the distribution stands and every item is settled.
                if (Math.Abs(violation) < 0.01f) return;

                // Freeze the items that caused the violation - the ones held back by their minimum when space is
                // short, by their maximum when it is plentiful - and hand the rest another pass.
                for (int i = 0; i < count; i++)
                {
                    if (frozen[i]) continue;
                    if (violation > 0f && line[i].MainSize > unclamped[i] + 0.001f) frozen[i] = true;
                    else if (violation < 0f && line[i].MainSize < unclamped[i] - 0.001f) frozen[i] = true;
                }
            }
        }

        /// <summary>How much of the free space an item claims. Shrinking is weighted by the base size as well as the
        /// factor, so a big item gives up more than a small one with the same flex-shrink; growing is not.</summary>
        private static float Weight(Item item, bool growing) =>
            growing ? item.Node.Style.FlexGrow : item.Node.Style.FlexShrink * item.BaseSize;

        /// <summary>
        /// Put one finished line where it belongs: free space along the main axis, alignment across it.
        ///
        /// Auto margins are resolved HERE, and before <c>justify-content</c> - that order is the whole of CSS
        /// Flexbox 8.1 and the one thing easy to get wrong, because the wrong order looks almost right. An item with
        /// `margin-left: auto` inside a `justify-content: center` container belongs at the END of the line; run
        /// justify first and it lands half way there, which is plausible enough to survive a glance at the screen.
        ///
        /// Only POSITIVE free space is shared out. A line that overflows has none to give, and the spec's answer is
        /// that every auto margin is then simply zero rather than negative - which falls out of the clamp below.
        /// </summary>
        /// <summary>
        /// How much room the baseline-aligned items on a line need above and below their shared baseline.
        ///
        /// Returns <see cref="float.NaN"/> when nothing on the line asks for baseline alignment, which is the
        /// common case and costs one walk of the line. Cross-axis only: down a COLUMN the cross axis is the
        /// horizontal one, there is no baseline to share, and CSS falls back to start alignment - so does this.
        ///
        /// An item that carries no text has no baseline of its own. CSS synthesizes one from its bottom margin
        /// edge, which is what an icon next to a label wants: the icon sits ON the line rather than floating
        /// somewhere inside it.
        /// </summary>
        private static float BaselineBand(List<Item> line, ComputedStyle parent, bool row, out float descent)
        {
            descent = 0f;
            if (!row) return float.NaN;

            float ascent = float.NaN;

            foreach (Item item in line)
            {
                if (AlignOf(item.Node, parent) != AlignKind.Baseline) continue;

                float baseline = float.IsNaN(item.Node.Baseline) ? item.CrossSize : item.Node.Baseline;
                float above = item.CrossMarginStart + baseline;
                float below = item.CrossSize - baseline + item.CrossMarginEnd;

                if (float.IsNaN(ascent) || above > ascent) ascent = above;
                if (below > descent) descent = below;
            }

            return ascent;
        }

        /// <summary>Which alignment an item actually gets: its own <c>align-self</c> when it has one, the
        /// container's <c>align-items</c> otherwise.</summary>
        private static AlignKind AlignOf(LayoutNode node, ComputedStyle parent) =>
            node.Style.AlignSelf != AlignKind.Auto ? node.Style.AlignSelf : parent.AlignItems;

        private static void PlaceLine(List<Item> line, ComputedStyle parent, bool row, bool reverse,
                                      float mainAvail, float mainGap, float crossOffset, float lineCross,
                                      float baselineAscent)
        {
            float content = 0f;
            foreach (Item item in line) content += item.MainSize + item.MainMarginStart + item.MainMarginEnd;
            content += mainGap * (line.Count - 1);

            float free = float.IsNaN(mainAvail) ? 0f : Math.Max(mainAvail - content, 0f);
            int count = line.Count;

            int autoMains = 0;
            foreach (Item item in line)
            {
                if (item.MainMarginStartAuto) autoMains++;
                if (item.MainMarginEndAuto) autoMains++;
            }

            // Every auto margin on the line takes an equal share, and justify-content is then handed a line with
            // nothing left to distribute - so it has no effect, exactly as the spec requires.
            float autoMain = 0f;
            if (autoMains > 0 && free > 0f) { autoMain = free / autoMains; free = 0f; }

            float cursor = 0f;
            float between = mainGap;

            switch (parent.JustifyContent)
            {
                case Justify.FlexEnd: cursor = free; break;
                case Justify.Center: cursor = free * 0.5f; break;
                case Justify.SpaceBetween: if (count > 1) between += free / (count - 1); break;
                case Justify.SpaceAround:
                    if (count > 0) { between += free / count; cursor = free / count * 0.5f; }
                    break;
                case Justify.SpaceEvenly:
                    if (count > 0) { between += free / (count + 1); cursor = free / (count + 1); }
                    break;
            }

            var ordered = new List<Item>(line);
            if (reverse) ordered.Reverse();

            foreach (Item item in ordered)
            {
                cursor += item.MainMarginStart + (item.MainMarginStartAuto ? autoMain : 0f);

                AlignKind align = AlignOf(item.Node, parent);
                float crossFree = Math.Max(lineCross - item.CrossSize - item.CrossMarginStart - item.CrossMarginEnd, 0f);
                float crossPos = crossOffset + item.CrossMarginStart;

                // A cross-axis auto margin replaces align-self rather than adding to it: two of them centre the item
                // in the line, one pushes it away from its own edge. Falling through to the switch as well would
                // apply both and land the item somewhere neither rule asked for.
                if (item.CrossMarginStartAuto && item.CrossMarginEndAuto) crossPos += crossFree * 0.5f;
                else if (item.CrossMarginStartAuto) crossPos += crossFree;
                else if (!item.CrossMarginEndAuto)
                {
                    switch (align)
                    {
                        case AlignKind.FlexEnd: crossPos += crossFree; break;
                        case AlignKind.Center: crossPos += crossFree * 0.5f; break;

                        // Every baseline-aligned item on the line hangs from the SAME depth, so the offset is the
                        // line's, not this item's share of the free space. Centring aligns the boxes; this aligns
                        // the text inside them, which is the difference a 13px name and a 12px amount make visible.
                        case AlignKind.Baseline when !float.IsNaN(baselineAscent):
                            crossPos = crossOffset + baselineAscent
                                     - (float.IsNaN(item.Node.Baseline) ? item.CrossSize : item.Node.Baseline);
                            break;
                    }
                }

                if (row) { item.Node.X = cursor; item.Node.Y = crossPos; }
                else { item.Node.Y = cursor; item.Node.X = crossPos; }

                cursor += item.MainSize + item.MainMarginEnd + (item.MainMarginEndAuto ? autoMain : 0f) + between;
            }
        }

        /// <summary>
        /// Shift a <c>position: relative</c> box by its insets, after everything else on the page has been placed.
        ///
        /// The point of relative is that the box keeps the room it took in the flow: siblings do not close the gap
        /// behind it and the parent does not shrink around it. That is why this runs at the very end of
        /// <see cref="LayoutChildren"/> - the line extents and the parent's used height were already read off the
        /// UNSHIFTED rectangles, and moving the box any earlier would feed the offset straight back into both.
        ///
        /// `left` wins over `right` and `top` over `bottom` when a box gives all four. CSS resolves that
        /// over-constrained case in the writing direction, and this engine's is always left-to-right, top-to-bottom.
        /// </summary>
        private static void OffsetRelative(LayoutNode child, float contentW, float contentH)
        {
            Edges inset = child.Style.Inset;

            float left = ResolveOrNaN(inset.Left, contentW);
            float right = ResolveOrNaN(inset.Right, contentW);
            if (!float.IsNaN(left)) child.X += left;
            else if (!float.IsNaN(right)) child.X -= right;

            // A percentage inset needs a definite containing size. Without one it resolves to NaN and is dropped -
            // the same answer a browser gives, and the reason a px offset still works in an auto-height parent.
            float top = ResolveOrNaN(inset.Top, contentH);
            float bottom = ResolveOrNaN(inset.Bottom, contentH);
            if (!float.IsNaN(top)) child.Y += top;
            else if (!float.IsNaN(bottom)) child.Y -= bottom;
        }

        /// <summary>
        /// An absolutely positioned child is measured against, and placed inside, its parent's content box. Real CSS
        /// walks up to the nearest positioned ancestor; here every box is a containing block, which is both simpler
        /// and what an app author almost always means inside a component.
        ///
        /// Insets and size are read; margins are not, auto ones included. Out here `inset` says everything a margin
        /// would, so the two ways of saying it would only be a second thing to keep in step.
        /// </summary>
        private static void LayoutAbsolute(LayoutNode child, float contentW, float contentH, IMeasureText measure)
        {
            ComputedStyle cs = child.Style;

            float left = ResolveOrNaN(cs.Inset.Left, contentW);
            float right = ResolveOrNaN(cs.Inset.Right, contentW);
            float top = ResolveOrNaN(cs.Inset.Top, contentH);
            float bottom = ResolveOrNaN(cs.Inset.Bottom, contentH);

            float width = ResolveOrNaN(cs.Width, contentW);
            float height = ResolveOrNaN(cs.Height, contentH);

            // Opposite insets without a size mean "span the gap between them".
            if (float.IsNaN(width) && !float.IsNaN(left) && !float.IsNaN(right))
                width = Math.Max(contentW - left - right, 0f);
            if (float.IsNaN(height) && !float.IsNaN(top) && !float.IsNaN(bottom))
                height = Math.Max(contentH - top - bottom, 0f);

            LayoutBox(child, contentW, contentH, measure, width, height);

            child.X = !float.IsNaN(left) ? left
                    : !float.IsNaN(right) ? contentW - right - child.Width
                    : 0f;

            child.Y = !float.IsNaN(top) ? top
                    : !float.IsNaN(bottom) && !float.IsNaN(contentH) ? contentH - bottom - child.Height
                    : 0f;
        }

        /// <summary>Widest the children want to be - used to size an auto-width box around them.</summary>
        private static float IntrinsicWidth(LayoutNode node, IMeasureText measure, float availWidth)
        {
            ComputedStyle s = node.Style;
            if (s.Display == DisplayKind.Grid) return GridLayout.IntrinsicWidth(node, measure, availWidth);

            bool row = s.FlexDirection == FlexDirection.Row || s.FlexDirection == FlexDirection.RowReverse;

            float total = 0f, widest = 0f;
            int counted = 0;

            foreach (LayoutNode child in node.Children)
            {
                if (child.Style.Display == DisplayKind.None) continue;

                // Only the out-of-flow kinds are skipped. A relative child still occupies its normal room, so it
                // sizes the box around it exactly like a static one - the offset moves the paint, not the space.
                if (child.Style.Position == PositionKind.Absolute || child.Style.Position == PositionKind.Fixed) continue;

                LayoutBox(child, availWidth, float.NaN, measure);
                float outer = child.Width + Horizontal(child.Style.Margin, availWidth);

                widest = Math.Max(widest, outer);
                total += outer;
                counted++;
            }

            if (counted == 0) return 0f;

            if (!row) return widest;

            float gap = s.ColumnGap.Resolve(availWidth) * (counted - 1);
            return total + gap;
        }

        // --------------------------------------------------------------------- helpers --

        internal static float ResolveOrNaN(Len len, float basis)
        {
            if (!len.IsDefinite) return float.NaN;
            if (len.Unit == LenUnit.Percent && float.IsNaN(basis)) return float.NaN;
            return len.Resolve(basis);
        }

        private static float Clamp(float value, Len min, Len max, float basis)
        {
            if (min.IsDefinite)
            {
                float m = min.Resolve(basis);
                if (!float.IsNaN(m)) value = Math.Max(value, m);
            }
            if (max.IsDefinite)
            {
                float m = max.Resolve(basis);
                if (!float.IsNaN(m)) value = Math.Min(value, m);
            }
            return value;
        }

        /// <summary>Left plus right. Percentages of both axes resolve against the WIDTH, as CSS specifies.</summary>
        internal static float Horizontal(Edges e, float basis) => e.Left.Resolve(basis) + e.Right.Resolve(basis);

        private static float Vertical(Edges e, float basis) => e.Top.Resolve(basis) + e.Bottom.Resolve(basis);
    }
}
