using Sideload.Css;

namespace Sideload.Layout
{
    /// <summary>
    /// The layout engine: flexbox plus absolute positioning, and nothing else.
    ///
    /// Deliberate simplifications, all of them documented promises rather than accidents:
    ///
    ///   * <b>Border-box sizing throughout.</b> `width` includes padding and border, which is what every app
    ///     stylesheet sets anyway and removes a whole class of "why is my box 24px too wide".
    ///   * <b>Auto margins count as zero.</b> Centring is `justify-content` / `align-items`; `margin: 0 auto` does
    ///     not centre. Same trade React Native makes.
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
        /// whose size a flex line already decided).
        /// </summary>
        private static void LayoutBox(LayoutNode node, float availWidth, float availHeight, IMeasureText measure,
                                      float forcedWidth = float.NaN, float forcedHeight = float.NaN)
        {
            ComputedStyle s = node.Style;

            if (s.Display == DisplayKind.None)
            {
                node.Width = 0f;
                node.Height = 0f;
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

            float usedContentH;
            if (node.IsTextLeaf)
            {
                usedContentH = measure.Measure(node.Text, s, contentW).Height;
            }
            else
            {
                usedContentH = LayoutChildren(node, contentW, contentH, availWidth, measure);
            }

            if (float.IsNaN(height)) height = usedContentH + padV;
            height = Clamp(height, s.MinHeight, s.MaxHeight, availHeight);
            height = Math.Max(height, 0f);

            // A clamped height changes the content box the children were placed in, so stretch them once more.
            float finalContentH = Math.Max(height - padV, 0f);
            if (!node.IsTextLeaf && Math.Abs(finalContentH - usedContentH) > 0.01f)
                LayoutChildren(node, contentW, finalContentH, availWidth, measure);

            node.Width = width;
            node.Height = height;
        }

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

            List<List<Item>> lines = BreakIntoLines(items, s.FlexWrap, mainAvail, mainGap);

            float crossCursor = 0f;
            float widest = 0f;

            foreach (List<Item> line in lines)
            {
                ResolveFlexibleLengths(line, mainAvail, mainGap);

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

                PlaceLine(line, s, row, reverse, mainAvail, mainGap, crossCursor, lineCross);

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

            // Everything above placed children relative to the CONTENT box origin. Shift them into the parent's own
            // coordinate space now: flow children clear border and padding, absolutely positioned ones only the
            // border, because their containing block is the padding box.
            float borderLeft = s.BorderWidth.Left.Resolve(percentBasis);
            float borderTop = s.BorderWidth.Top.Resolve(percentBasis);
            float padLeft = s.Padding.Left.Resolve(percentBasis);
            float padTop = s.Padding.Top.Resolve(percentBasis);

            foreach (LayoutNode child in flow)
            {
                child.X += borderLeft + padLeft;
                child.Y += borderTop + padTop;
            }

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

            // For a row container the used height is the stacked line extent; for a column it is where the main axis
            // ended.
            return row ? usedCross : flowBottom;
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
            internal float MinMain, MaxMain;
        }

        private static Item BuildItem(LayoutNode child, bool row, float mainAvail, float crossAvail,
                                      float percentBasis, AlignKind parentAlign, IMeasureText measure)
        {
            ComputedStyle cs = child.Style;

            var item = new Item
            {
                Node = child,
                MainMarginStart = (row ? cs.Margin.Left : cs.Margin.Top).Resolve(percentBasis),
                MainMarginEnd = (row ? cs.Margin.Right : cs.Margin.Bottom).Resolve(percentBasis),
                CrossMarginStart = (row ? cs.Margin.Top : cs.Margin.Left).Resolve(percentBasis),
                CrossMarginEnd = (row ? cs.Margin.Bottom : cs.Margin.Right).Resolve(percentBasis),
            };

            Len mainSizeProperty = row ? cs.Width : cs.Height;
            Len minProperty = row ? cs.MinWidth : cs.MinHeight;
            Len maxProperty = row ? cs.MaxWidth : cs.MaxHeight;

            AlignKind selfAlign = cs.AlignSelf != AlignKind.Auto ? cs.AlignSelf : parentAlign;
            bool stretchedCross = !row && selfAlign == AlignKind.Stretch && !cs.Width.IsDefinite && !float.IsNaN(crossAvail);

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

            item.MinMain = AutomaticMinimum(item, minProperty, row, mainAvail);
            item.MaxMain = maxProperty.IsDefinite ? maxProperty.Resolve(mainAvail) : float.PositiveInfinity;
            item.MainSize = Math.Clamp(item.BaseSize, item.MinMain, item.MaxMain);
            return item;
        }

        /// <summary>
        /// The floor a flex item may be shrunk to. `min-width`/`min-height` default to `auto`, and auto does NOT mean
        /// zero: CSS Flexbox 4.5 gives an item a content-based minimum so a column of text can never be squeezed
        /// smaller than the text inside it. Without this a page whose children add up to more than the viewport
        /// collapses every box a little, and every paragraph spills out of the card that holds it.
        ///
        /// Two deliberate narrowings of the spec:
        ///   * <b>Along a row the automatic minimum is zero.</b> The real rule is the min-content width, i.e. the
        ///     longest unbreakable word, which needs a second text measurement pass per item. Row items in app UIs are
        ///     buttons and icons with `flex: 1` or a fixed width, where the difference does not show.
        ///   * <b>An explicit main size is never shrunk past.</b> The spec would allow it when the content is smaller
        ///     than the declared size; honouring an author's `height: 96px` is the less surprising of the two.
        ///
        /// The floor is <see cref="Item.ContentMain"/>, NOT the flex base size. Those differ exactly when the basis
        /// came from `flex-basis` - which `flex: 1` and `flex: 0` both set - and reading the base size there gave
        /// `flex: 0` a minimum of zero, so a row of sized boxes collapsed into nothing instead of holding at its
        /// content. That was this renderer disagreeing with every browser, not a narrowing of the spec.
        /// </summary>
        private static float AutomaticMinimum(Item item, Len minProperty, bool row, float mainAvail)
        {
            if (minProperty.IsDefinite) return minProperty.Resolve(mainAvail);
            if (row) return 0f;

            // A scroll container has an automatic minimum of zero - it is allowed to be smaller than its content
            // precisely because it can scroll.
            ComputedStyle cs = item.Node.Style;
            if (cs.OverflowY != OverflowKind.Visible || cs.OverflowX != OverflowKind.Visible) return 0f;

            return item.ContentMain;
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
        private static void ResolveFlexibleLengths(List<Item> line, float mainAvail, float mainGap)
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
                item.MainSize = Math.Clamp(item.BaseSize, item.MinMain, item.MaxMain);

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
                    item.MainSize = Math.Clamp(unclamped[i], item.MinMain, item.MaxMain);
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

        private static void PlaceLine(List<Item> line, ComputedStyle parent, bool row, bool reverse,
                                      float mainAvail, float mainGap, float crossOffset, float lineCross)
        {
            float content = 0f;
            foreach (Item item in line) content += item.MainSize + item.MainMarginStart + item.MainMarginEnd;
            content += mainGap * (line.Count - 1);

            float free = float.IsNaN(mainAvail) ? 0f : Math.Max(mainAvail - content, 0f);
            int count = line.Count;

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
                cursor += item.MainMarginStart;

                AlignKind align = item.Node.Style.AlignSelf != AlignKind.Auto ? item.Node.Style.AlignSelf : parent.AlignItems;
                float crossFree = Math.Max(lineCross - item.CrossSize - item.CrossMarginStart - item.CrossMarginEnd, 0f);
                float crossPos = crossOffset + item.CrossMarginStart;

                switch (align)
                {
                    case AlignKind.FlexEnd: crossPos += crossFree; break;
                    case AlignKind.Center: crossPos += crossFree * 0.5f; break;
                }

                if (row) { item.Node.X = cursor; item.Node.Y = crossPos; }
                else { item.Node.Y = cursor; item.Node.X = crossPos; }

                cursor += item.MainSize + item.MainMarginEnd + between;
            }
        }

        /// <summary>
        /// An absolutely positioned child is measured against, and placed inside, its parent's content box. Real CSS
        /// walks up to the nearest positioned ancestor; here every box is a containing block, which is both simpler
        /// and what an app author almost always means inside a component.
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
            bool row = s.FlexDirection == FlexDirection.Row || s.FlexDirection == FlexDirection.RowReverse;

            float total = 0f, widest = 0f;
            int counted = 0;

            foreach (LayoutNode child in node.Children)
            {
                if (child.Style.Display == DisplayKind.None) continue;
                if (child.Style.Position != PositionKind.Static) continue;

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

        private static float ResolveOrNaN(Len len, float basis)
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
        private static float Horizontal(Edges e, float basis) => e.Left.Resolve(basis) + e.Right.Resolve(basis);

        private static float Vertical(Edges e, float basis) => e.Top.Resolve(basis) + e.Bottom.Resolve(basis);
    }
}
