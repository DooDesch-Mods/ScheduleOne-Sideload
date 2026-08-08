using Sideload.Css;

namespace Sideload.Layout
{
    /// <summary>
    /// CSS Grid: track sizing, row-major auto-placement, and alignment inside a cell.
    ///
    /// It sits beside <see cref="FlexLayout"/> rather than inside it because it is a second algorithm, not a mode
    /// of the first one. What the two share is the BOX - sizing, the border-box rule, out-of-flow children - and
    /// that all lives in FlexLayout; a grid container reaches it through <see cref="FlexLayout.LayoutBox"/> for
    /// every item, and hands the shared tail back to <see cref="FlexLayout.FinishContainer"/> so that
    /// `position: absolute` cannot mean two different things depending on the parent's `display`.
    ///
    /// What is implemented is the grid people write:
    ///
    ///   * `grid-template-columns` / `grid-template-rows` with lengths, percentages, `fr`, `auto`, `min-content`,
    ///     `max-content`, `repeat(n, ...)` and `repeat(auto-fit|auto-fill, minmax(&lt;len&gt;, 1fr))`
    ///   * auto-placement in row-major order, sparse, which is `grid-auto-flow: row`
    ///   * `grid-column` / `grid-row` with line numbers (negative ones count from the end) and `span n`
    ///   * implicit rows and columns for anything past the explicit grid, sized by `grid-auto-rows` /
    ///     `grid-auto-columns`
    ///   * `justify-items` / `align-items` and their `-self` overrides
    ///
    /// Deliberate narrowings, each one a promise rather than an oversight. Every one of them is also REPORTED
    /// when a stylesheet asks for it, through <see cref="Model.Diagnostics"/> - a grid that silently comes out as
    /// something else is the failure this whole feature exists to end.
    ///
    ///   * <b>No named lines and no named areas.</b> `grid-template-areas` is a second placement model with its
    ///     own string syntax and its own name table; `[line-name]` groups are stripped so the tracks around them
    ///     still lay out. Neither appears in a Tailwind utility.
    ///   * <b>No subgrid, no masonry, no `grid-auto-flow: dense` and no column flow.</b> Each is its own
    ///     algorithm; dense in particular is a different placement pass, not a flag on this one.
    ///   * <b>The automatic minimum of a grid item is zero along the inline axis.</b> The spec floor is the item's
    ///     min-content width, which costs a second text pass per item. This is the same narrowing FlexLayout
    ///     already makes for a row (see its AutomaticMinimum), and it means `1fr` and `minmax(0, 1fr)` behave
    ///     identically - which is what Tailwind writes anyway, because `grid-cols-3` IS
    ///     `repeat(3, minmax(0, 1fr))`. A track that literally says `min-content` still gets the real answer.
    ///   * <b>Leftover space stays at the end.</b> `justify-content` and `align-content` do not distribute the
    ///     space a grid of fixed tracks leaves over; the tracks start at the content box, which is
    ///     `justify-content: start`, CSS's own default.
    ///   * <b>`auto-fit` drops its empty repetitions instead of collapsing them to zero.</b> CSS keeps the
    ///     collapsed tracks as lines with a gap of nothing; here the repetition is simply not created. For
    ///     `minmax(&lt;len&gt;, 1fr)`, which is the only shape anyone writes auto-fit in, the geometry is the same.
    ///
    /// <b>Where this measures twice, and why.</b> Rows cannot be sized before the items are laid out, and an item
    /// cannot be laid out before its column is sized - so the pass is columns, then items, then rows. That much is
    /// one measurement per item. Two things add a second one, both of them opt-in by the stylesheet:
    ///
    ///   1. A column sized by its content (`auto`, `max-content`, or `min-content`) has to ask each item in it how
    ///      wide it wants to be BEFORE the column width exists, so those items are laid out once to be measured
    ///      and once at their final width. A grid whose columns are all `fr`, a length or a percentage - which is
    ///      every Tailwind grid - never enters that path.
    ///   2. An item shorter than its row and set to stretch is laid out again at the row's height. Flexbox does
    ///      exactly the same in its stretch pass, and for the same reason: the height feeds back into the subtree.
    /// </summary>
    internal static class GridLayout
    {
        /// <summary>
        /// The most tracks one axis may have.
        ///
        /// Not a spec limit - CSS has none - but a stylesheet does: `grid-column: 900000` or
        /// `repeat(auto-fit, minmax(0.02px, 1fr))` asks for a grid of a million tracks, and the placement pass
        /// would allocate one occupancy row per one of them before anything could say the number is a typo.
        /// Browsers clamp for the same reason. A thousand is far past any layout anybody writes.
        /// </summary>
        private const int MaxTracks = 1000;

        /// <summary>
        /// Place a grid container's children inside its content box and report how tall they ended up.
        ///
        /// Mirrors <c>FlexLayout.LayoutChildren</c>'s contract exactly: children come out positioned relative to
        /// the PARENT's own origin, and the return value is the content height an auto-height box should take.
        /// </summary>
        internal static float LayoutChildren(LayoutNode node, float contentW, float contentH,
                                             float percentBasis, IMeasureText measure)
        {
            ComputedStyle s = node.Style;

            var flow = new List<LayoutNode>();
            var absolute = new List<LayoutNode>();
            Split(node, flow, absolute);

            // Both gaps resolve against the WIDTH, as they do in the flex path - CSS resolves a row gap against
            // the block size, but the block size here is regularly indefinite and a gap that disappears whenever
            // the container is auto-height is the worse of the two answers.
            float columnGap = s.ColumnGap.Resolve(contentW);
            float rowGap = s.RowGap.Resolve(contentW);

            var cells = BuildCells(flow, contentW);

            // --- columns -------------------------------------------------------------------------------------

            int repetitions = AutoRepetitions(s.GridTemplateColumns, contentW, columnGap);
            List<GridTrack> columns = Materialise(s.GridTemplateColumns, repetitions);
            List<GridTrack> rows = Materialise(s.GridTemplateRows, AutoRepetitions(s.GridTemplateRows, contentH, rowGap));

            Place(cells, columns.Count, rows.Count, out int columnCount, out int rowCount);

            // `auto-fit` keeps only as many repetitions as the items actually reach into. The placement is cheap -
            // no measurement happens in it - so running it twice costs less than threading "how many columns will
            // be used" into the sizing of the tracks that decide how many there are.
            if (TrimAutoFit(s.GridTemplateColumns, cells, ref repetitions))
            {
                columns = Materialise(s.GridTemplateColumns, repetitions);
                Place(cells, columns.Count, rows.Count, out columnCount, out rowCount);
            }

            Grow(columns, columnCount, s.GridAutoColumns);
            Grow(rows, rowCount, s.GridAutoRows);

            float[] columnSizes = SizeTracks(columns, cells, true, contentW, columnGap,
                                             (cell, kind) => ColumnContribution(cell, kind, contentW, measure));
            float[] columnStarts = Starts(columnSizes, columnGap);

            // --- items, at their column width ----------------------------------------------------------------

            foreach (Cell cell in cells)
            {
                cell.Width = Extent(columnSizes, cell.Column, cell.ColumnSpan, columnGap);

                float inner = Math.Max(cell.Width - cell.MarginLeft - cell.MarginRight, 0f);
                float definiteHeight = DefiniteExtent(rows, cell.Row, cell.RowSpan, rowGap, contentH);

                // The containing block of a grid item is its grid AREA, not the container's content box - which is
                // what makes `width: 50%` inside a cell mean half the cell.
                FlexLayout.LayoutBox(cell.Node, inner, definiteHeight, measure,
                                     StretchWidth(cell, s) ? inner : float.NaN);
                cell.Height = cell.Node.Height;
            }

            // --- rows ----------------------------------------------------------------------------------------

            float[] rowSizes = SizeTracks(rows, cells, false, contentH, rowGap,
                                          (cell, _) => cell.Height + cell.MarginTop + cell.MarginBottom);
            float[] rowStarts = Starts(rowSizes, rowGap);

            // --- stretch, then place -------------------------------------------------------------------------

            float bottom = 0f;

            foreach (Cell cell in cells)
            {
                float cellHeight = Extent(rowSizes, cell.Row, cell.RowSpan, rowGap);
                float innerW = Math.Max(cell.Width - cell.MarginLeft - cell.MarginRight, 0f);
                float innerH = Math.Max(cellHeight - cell.MarginTop - cell.MarginBottom, 0f);

                if (StretchHeight(cell, s) && Math.Abs(innerH - cell.Node.Height) > 0.01f)
                {
                    FlexLayout.LayoutBox(cell.Node, innerW, innerH, measure,
                                         StretchWidth(cell, s) ? innerW : float.NaN, innerH);
                }

                AlignKind justify = cell.Node.Style.JustifySelf != AlignKind.Auto ? cell.Node.Style.JustifySelf : s.JustifyItems;
                AlignKind align = cell.Node.Style.AlignSelf != AlignKind.Auto ? cell.Node.Style.AlignSelf : s.AlignItems;

                cell.Node.X = columnStarts[cell.Column] + cell.MarginLeft
                              + Offset(justify, innerW - cell.Node.Width);
                cell.Node.Y = rowStarts[cell.Row] + cell.MarginTop
                              + Offset(align, innerH - cell.Node.Height);

                bottom = Math.Max(bottom, cell.Node.Y + cell.Node.Height + cell.MarginBottom);
            }

            // The row track extent counts even where no item reaches the bottom of the last row: an empty
            // `grid-auto-rows: 80px` row is 80px of grid, and an auto-height container has to include it.
            float used = rowSizes.Length == 0 ? 0f : rowStarts[rowSizes.Length - 1] + rowSizes[rowSizes.Length - 1];
            used = Math.Max(used, bottom);

            FlexLayout.FinishContainer(s, flow, absolute, contentW, contentH, percentBasis, measure);
            return used;
        }

        /// <summary>
        /// How wide a grid container wants to be when nothing has told it - the sum of its columns at their
        /// content sizes.
        ///
        /// The tracks are sized against an INDEFINITE inline size here, which is what turns `1fr` into
        /// "as wide as the content" (CSS Grid 12.7: with no free space to divide, a flexible track sizes to its
        /// content). Percentage tracks contribute nothing, for the same reason a percentage cannot resolve
        /// against a width that does not exist yet.
        ///
        /// This costs a placement and a measurement pass that <see cref="LayoutChildren"/> then repeats. It is the
        /// same shape <c>FlexLayout.IntrinsicWidth</c> has, and it is reached from the same place: only a box whose
        /// width has to come out of its content, which for a grid is the rare case - a dashboard gets its width
        /// from the page.
        /// </summary>
        internal static float IntrinsicWidth(LayoutNode node, IMeasureText measure, float availWidth)
        {
            ComputedStyle s = node.Style;

            var flow = new List<LayoutNode>();
            var absolute = new List<LayoutNode>();
            Split(node, flow, absolute);
            if (flow.Count == 0 && s.GridTemplateColumns == null) return 0f;

            float columnGap = s.ColumnGap.Resolve(availWidth);
            var cells = BuildCells(flow, availWidth);

            int repetitions = AutoRepetitions(s.GridTemplateColumns, availWidth, columnGap);
            List<GridTrack> columns = Materialise(s.GridTemplateColumns, repetitions);
            int explicitRows = s.GridTemplateRows == null ? 0 : s.GridTemplateRows.Tracks.Count;

            Place(cells, columns.Count, explicitRows, out int columnCount, out _);
            Grow(columns, columnCount, s.GridAutoColumns);

            float[] sizes = SizeTracks(columns, cells, true, float.NaN, columnGap,
                                       (cell, kind) => ColumnContribution(cell, kind, availWidth, measure));

            float total = columnGap * Math.Max(sizes.Length - 1, 0);
            foreach (float size in sizes) total += size;
            return total;
        }

        // ------------------------------------------------------------------------------ items --

        /// <summary>One grid item: which cells it occupies, and what came out of laying it out.</summary>
        private sealed class Cell
        {
            internal LayoutNode Node;

            /// <summary>Zero-based track indices. -1 until the placement pass has decided.</summary>
            internal int Row = -1, Column = -1;
            internal int RowSpan = 1, ColumnSpan = 1;

            internal float MarginLeft, MarginRight, MarginTop, MarginBottom;

            /// <summary>The cell's own width and the item's height at it - filled in by the layout pass.</summary>
            internal float Width, Height;

            /// <summary>Measured contributions, kept because a track asks for them once per sizing step and a
            /// measurement is the expensive thing in the whole pass.</summary>
            internal float MinContent = float.NaN, MaxContent = float.NaN;
        }

        private static void Split(LayoutNode node, List<LayoutNode> flow, List<LayoutNode> absolute)
        {
            foreach (LayoutNode child in node.Children)
            {
                if (child.Style.Display == DisplayKind.None) { child.Width = 0f; child.Height = 0f; continue; }
                if (child.Style.Position == PositionKind.Absolute) absolute.Add(child);

                // A fixed child is out of this box entirely - FlexLayout.Compute lays it out against the viewport
                // once the page is otherwise finished.
                else if (child.Style.Position != PositionKind.Fixed) flow.Add(child);
            }
        }

        private static List<Cell> BuildCells(List<LayoutNode> flow, float percentBasis)
        {
            var cells = new List<Cell>(flow.Count);
            foreach (LayoutNode child in flow)
            {
                Edges margin = child.Style.Margin;
                cells.Add(new Cell
                {
                    Node = child,
                    // A grid item's containing block is its area, but its margins resolve against the inline size
                    // of the grid container's content box - which is what percentBasis is here.
                    MarginLeft = margin.Left.Resolve(percentBasis),
                    MarginRight = margin.Right.Resolve(percentBasis),
                    MarginTop = margin.Top.Resolve(percentBasis),
                    MarginBottom = margin.Bottom.Resolve(percentBasis),
                });
            }
            return cells;
        }

        // -------------------------------------------------------------------------- placement --

        /// <summary>
        /// CSS Grid 8.5, row-major and sparse: definite positions first, then anything pinned to a row, then the
        /// auto-placement cursor over the rest in document order.
        ///
        /// Sparse is what `grid-auto-flow: row` means without `dense`: the cursor never moves backwards, so a
        /// wide item leaves a hole rather than pulling a later item up into it. That is the behaviour people
        /// count on when they write a grid in source order.
        /// </summary>
        private static void Place(List<Cell> cells, int explicitColumns, int explicitRows,
                                  out int columnCount, out int rowCount)
        {
            foreach (Cell cell in cells)
            {
                ResolveAxis(cell.Node.Style.GridColumn, explicitColumns, out cell.Column, out cell.ColumnSpan);
                ResolveAxis(cell.Node.Style.GridRow, explicitRows, out cell.Row, out cell.RowSpan);
                Clamp(ref cell.Column, ref cell.ColumnSpan);
                Clamp(ref cell.Row, ref cell.RowSpan);
            }

            columnCount = Math.Max(explicitColumns, 1);
            foreach (Cell cell in cells)
            {
                // An item wider than the explicit grid widens the grid, whether it was placed by hand or not.
                // The auto-placement loop below relies on this: a span it can never fit would never terminate.
                columnCount = Math.Max(columnCount, cell.ColumnSpan);
                if (cell.Column >= 0) columnCount = Math.Max(columnCount, cell.Column + cell.ColumnSpan);
            }

            var occupied = new List<bool[]>();

            foreach (Cell cell in cells)
                if (cell.Row >= 0 && cell.Column >= 0) Occupy(occupied, cell, columnCount);

            foreach (Cell cell in cells)
            {
                if (cell.Row < 0 || cell.Column >= 0) continue;

                cell.Column = FirstFreeColumn(occupied, cell, columnCount);
                Occupy(occupied, cell, columnCount);
            }

            int cursorRow = 0, cursorColumn = 0;

            foreach (Cell cell in cells)
            {
                if (cell.Row >= 0) continue;

                if (cell.Column >= 0)
                {
                    // Pinned to a column, free to fall down the rows. The cursor moves on rather than back, which
                    // is the sparse rule.
                    if (cell.Column < cursorColumn) cursorRow++;
                    cursorColumn = cell.Column;
                    while (!IsFree(occupied, cursorRow, cell.Column, cell.RowSpan, cell.ColumnSpan)) cursorRow++;
                    cell.Row = cursorRow;
                }
                else
                {
                    while (true)
                    {
                        if (cursorColumn + cell.ColumnSpan > columnCount) { cursorRow++; cursorColumn = 0; continue; }
                        if (IsFree(occupied, cursorRow, cursorColumn, cell.RowSpan, cell.ColumnSpan)) break;
                        cursorColumn++;
                    }

                    cell.Row = cursorRow;
                    cell.Column = cursorColumn;
                    cursorColumn += cell.ColumnSpan;
                }

                Occupy(occupied, cell, columnCount);
            }

            rowCount = Math.Max(explicitRows, 1);
            foreach (Cell cell in cells) rowCount = Math.Max(rowCount, cell.Row + cell.RowSpan);
        }

        /// <summary>
        /// One axis of one item: where it starts and how many tracks it covers.
        ///
        /// A start of -1 means "auto" - the placement pass decides. A negative line number counts back from the
        /// end of the EXPLICIT grid, which is what makes `grid-column: 1 / -1` mean "the whole row".
        /// </summary>
        private static void ResolveAxis(GridPlacement placement, int explicitCount, out int start, out int span)
        {
            start = -1;
            span = 1;

            bool hasStart = placement.Start.Kind == GridLineKind.Number;
            bool hasEnd = placement.End.Kind == GridLineKind.Number;

            int startLine = hasStart ? ToIndex(placement.Start.Value, explicitCount) : 0;
            int endLine = hasEnd ? ToIndex(placement.End.Value, explicitCount) : 0;

            if (hasStart && hasEnd)
            {
                if (endLine < startLine) (startLine, endLine) = (endLine, startLine);
                start = Math.Max(startLine, 0);
                span = Math.Max(endLine - start, 1);
                return;
            }

            if (hasStart)
            {
                start = Math.Max(startLine, 0);
                span = placement.End.Kind == GridLineKind.Span ? placement.End.Value : 1;
                return;
            }

            if (hasEnd)
            {
                span = placement.Start.Kind == GridLineKind.Span ? placement.Start.Value : 1;
                start = Math.Max(endLine - span, 0);
                return;
            }

            if (placement.Start.Kind == GridLineKind.Span) span = placement.Start.Value;
            else if (placement.End.Kind == GridLineKind.Span) span = placement.End.Value;
        }

        /// <summary>Holds a resolved placement inside <see cref="MaxTracks"/>. Called on the way out of
        /// <see cref="ResolveAxis"/>, so nothing downstream ever sees a line number that would size a grid nobody
        /// asked for.</summary>
        private static void Clamp(ref int start, ref int span)
        {
            if (span < 1) span = 1;
            if (span > MaxTracks) span = MaxTracks;
            if (start > MaxTracks - span) start = MaxTracks - span;
            if (start < -1) start = -1;
        }

        /// <summary>Line number to track index. Lines run 1..count+1; -1 is the last one, so -1 lands past the
        /// last track and `1 / -1` spans them all.</summary>
        private static int ToIndex(int line, int explicitCount) =>
            line > 0 ? line - 1 : explicitCount + 1 + line;

        private static void Occupy(List<bool[]> occupied, Cell cell, int columnCount)
        {
            for (int r = cell.Row; r < cell.Row + cell.RowSpan; r++)
            {
                bool[] row = RowAt(occupied, r, columnCount);
                for (int c = cell.Column; c < cell.Column + cell.ColumnSpan && c < row.Length; c++) row[c] = true;
            }
        }

        private static bool IsFree(List<bool[]> occupied, int rowStart, int columnStart, int rowSpan, int columnSpan)
        {
            for (int r = rowStart; r < rowStart + rowSpan; r++)
            {
                if (r >= occupied.Count) continue;                 // a row nobody has reached yet is all free
                bool[] row = occupied[r];
                for (int c = columnStart; c < columnStart + columnSpan && c < row.Length; c++)
                    if (row[c]) return false;
            }
            return true;
        }

        private static int FirstFreeColumn(List<bool[]> occupied, Cell cell, int columnCount)
        {
            for (int c = 0; c + cell.ColumnSpan <= columnCount; c++)
                if (IsFree(occupied, cell.Row, c, cell.RowSpan, cell.ColumnSpan)) return c;

            // Every column in that row is taken. CSS lets explicitly placed items overlap, so this one does.
            return 0;
        }

        private static bool[] RowAt(List<bool[]> occupied, int index, int columnCount)
        {
            while (occupied.Count <= index) occupied.Add(new bool[Math.Max(columnCount, 1)]);
            return occupied[index];
        }

        // ------------------------------------------------------------------------ track lists --

        /// <summary>
        /// How often a `repeat(auto-fit|auto-fill, ...)` fits into the available size - CSS Grid 7.2.3.1.
        ///
        /// Solved rather than counted up: with `n` repetitions of a body whose tracks have a total minimum of
        /// `per`, the row is `fixed + n*per + gap*(fixedCount + n*body - 1)` wide, so the largest `n` that still
        /// fits comes straight out of a division. One is the floor - a repeat that fits nowhere still produces a
        /// track, which is what a browser does and what keeps a narrow screen showing one column rather than none.
        /// </summary>
        private static int AutoRepetitions(GridTemplate template, float available, float gap)
        {
            if (template == null || !template.HasAutoRepeat) return 1;
            if (float.IsNaN(available) || float.IsInfinity(available)) return 1;

            float per = 0f;
            for (int i = 0; i < template.AutoRepeatCount; i++)
            {
                GridTrack track = template.Tracks[template.AutoRepeatAt + i];
                if (TryDefinite(track.Min, available, out float min)) per += Math.Max(min, 0f);
            }

            // Nothing to divide by: `repeat(auto-fit, minmax(0, 1fr))` would fit any number of tracks, and CSS
            // says one.
            if (per <= 0.01f) return 1;

            float fixedSize = 0f;
            int fixedCount = template.Tracks.Count - template.AutoRepeatCount;

            for (int i = 0; i < template.Tracks.Count; i++)
            {
                if (i >= template.AutoRepeatAt && i < template.AutoRepeatAt + template.AutoRepeatCount) continue;
                if (TryDefinite(template.Tracks[i].Min, available, out float size)) fixedSize += Math.Max(size, 0f);
            }

            float room = available - fixedSize - gap * (fixedCount - 1);
            float step = per + gap * template.AutoRepeatCount;
            if (step <= 0.01f) return 1;

            int repetitions = (int)Math.Floor(room / step);
            if (repetitions < 1) return 1;

            int cap = Math.Max(MaxTracks / template.AutoRepeatCount, 1);
            return repetitions > cap ? cap : repetitions;
        }

        /// <summary>
        /// Drops the repetitions of an `auto-fit` track list that no item reaches into. Returns whether anything
        /// changed, which is the caller's signal to place the items again against the shorter list.
        /// </summary>
        private static bool TrimAutoFit(GridTemplate template, List<Cell> cells, ref int repetitions)
        {
            if (template == null || !template.HasAutoRepeat || !template.AutoFit || repetitions <= 1) return false;

            int used = 0;
            foreach (Cell cell in cells)
                if (cell.Column >= 0) used = Math.Max(used, cell.Column + cell.ColumnSpan);

            int outside = template.Tracks.Count - template.AutoRepeatCount;
            int needed = Math.Max(used - outside, 1);
            int wanted = Math.Max(1, (needed + template.AutoRepeatCount - 1) / template.AutoRepeatCount);

            if (wanted >= repetitions) return false;

            repetitions = wanted;
            return true;
        }

        /// <summary>The explicit track list with the auto repetition written out <paramref name="repetitions"/> times.
        /// Empty for `none`, which is a grid whose every track is implicit.</summary>
        private static List<GridTrack> Materialise(GridTemplate template, int repetitions)
        {
            var tracks = new List<GridTrack>();

            if (template == null) return tracks;

            if (!template.HasAutoRepeat)
            {
                tracks.AddRange(template.Tracks);
                return tracks;
            }

            for (int i = 0; i < template.AutoRepeatAt; i++) tracks.Add(template.Tracks[i]);

            for (int r = 0; r < repetitions; r++)
                for (int i = 0; i < template.AutoRepeatCount; i++)
                    tracks.Add(template.Tracks[template.AutoRepeatAt + i]);

            for (int i = template.AutoRepeatAt + template.AutoRepeatCount; i < template.Tracks.Count; i++)
                tracks.Add(template.Tracks[i]);

            return tracks;
        }

        /// <summary>Adds implicit tracks until there are as many as the placement needs.</summary>
        private static void Grow(List<GridTrack> tracks, int count, GridTrack implicitTrack)
        {
            while (tracks.Count < count) tracks.Add(implicitTrack);
        }

        // ------------------------------------------------------------------------ track sizing --

        /// <summary>
        /// CSS Grid 12.3 to 12.7, in the order the spec runs them: start from the sizing functions, raise the
        /// content-based tracks to what their items need, hand out what is left to the tracks that can still grow,
        /// and divide the remainder among the flexible ones.
        ///
        /// <paramref name="available"/> may be NaN, which is CSS's "indefinite": there is then no free space to
        /// divide, so a flexible track sizes to its content instead - the same rule that makes an auto-height row
        /// of `1fr` behave like `auto`.
        /// </summary>
        private static float[] SizeTracks(List<GridTrack> tracks, List<Cell> cells, bool columns,
                                          float available, float gap,
                                          Func<Cell, TrackSizeKind, float> contribution)
        {
            int n = tracks.Count;
            var basis = new float[n];
            var limit = new float[n];
            if (n == 0) return basis;

            bool definite = !float.IsNaN(available) && !float.IsInfinity(available);

            // With no free space to divide, `1fr` is `max-content` (CSS Grid 12.7). Rewriting it here rather than
            // branching in four later places keeps the rest of this function reading like the spec.
            var sizing = new List<GridTrack>(n);
            foreach (GridTrack track in tracks)
                sizing.Add(!definite && track.Max.IsFraction
                    ? new GridTrack(track.Min, TrackSize.MaxContent)
                    : track);

            for (int i = 0; i < n; i++)
            {
                GridTrack track = sizing[i];
                basis[i] = TryDefinite(track.Min, available, out float min) ? Math.Max(min, 0f) : 0f;
                limit[i] = TryDefinite(track.Max, available, out float max) ? Math.Max(max, 0f) : float.PositiveInfinity;
                if (limit[i] < basis[i]) limit[i] = basis[i];
            }

            foreach (Cell cell in cells)
            {
                int start = columns ? cell.Column : cell.Row;
                int span = columns ? cell.ColumnSpan : cell.RowSpan;
                if (start < 0 || start >= n) continue;
                span = Math.Min(span, n - start);

                if (span == 1)
                {
                    GridTrack track = sizing[start];

                    if (track.Min.Kind != TrackSizeKind.Length)
                        basis[start] = Math.Max(basis[start], contribution(cell, track.Min.Kind));

                    if (track.Max.Kind != TrackSizeKind.Length && !track.Max.IsFraction)
                        limit[start] = Math.Max(Finite(limit[start]), contribution(cell, AsMaximum(track.Max.Kind)));

                    if (limit[start] < basis[start]) limit[start] = basis[start];
                    continue;
                }

                // An item across several tracks: CSS 12.5.1 distributes what it needs beyond what the tracks
                // already have. Equally, and over the content-sized tracks only - a track the author gave a
                // length to is not the one that should absorb somebody else's content.
                float have = gap * (span - 1);
                for (int i = start; i < start + span; i++) have += basis[i];

                float want = contribution(cell, TrackSizeKind.Auto);
                if (want <= have + 0.01f) continue;

                var takers = new List<int>();
                for (int i = start; i < start + span; i++)
                    if (sizing[i].Min.Kind != TrackSizeKind.Length) takers.Add(i);

                if (takers.Count == 0)
                    for (int i = start; i < start + span; i++) takers.Add(i);

                float share = (want - have) / takers.Count;
                foreach (int i in takers)
                {
                    basis[i] += share;
                    if (limit[i] < basis[i]) limit[i] = basis[i];
                }
            }

            if (!definite)
            {
                for (int i = 0; i < n; i++) basis[i] = Settle(basis[i], limit[i]);
                return basis;
            }

            // Maximize tracks (12.6): hand the free space out equally, freezing each track as it reaches its
            // growth limit. Flexible tracks stay out of it - 12.7 below decides those from scratch.
            float free = available - gap * (n - 1);
            for (int i = 0; i < n; i++) free -= basis[i];

            if (free > 0.01f)
            {
                var frozen = new bool[n];
                for (int i = 0; i < n; i++)
                    frozen[i] = sizing[i].Max.IsFraction || limit[i] <= basis[i] + 0.001f;

                // Each pass freezes at least one track, so the track count bounds the iteration.
                for (int pass = 0; pass <= n; pass++)
                {
                    int live = 0;
                    for (int i = 0; i < n; i++) if (!frozen[i]) live++;
                    if (live == 0 || free <= 0.01f) break;

                    float share = free / live;
                    float used = 0f;

                    for (int i = 0; i < n; i++)
                    {
                        if (frozen[i]) continue;

                        float grow = Math.Min(share, limit[i] - basis[i]);
                        basis[i] += grow;
                        used += grow;
                        if (basis[i] >= limit[i] - 0.001f) frozen[i] = true;
                    }

                    free -= used;
                    if (used <= 0.001f) break;
                }
            }

            // Expand flexible tracks (12.7). The leftover is measured against the tracks that are NOT flexible,
            // so the fr share is the same whether this runs before or after the step above - which is why the
            // gaps come off here and not once per track.
            float flexTotal = 0f;
            for (int i = 0; i < n; i++) if (sizing[i].Max.IsFraction) flexTotal += sizing[i].Max.Fraction;

            if (flexTotal > 0f)
            {
                float leftover = available - gap * (n - 1);
                for (int i = 0; i < n; i++) if (!sizing[i].Max.IsFraction) leftover -= basis[i];
                if (leftover < 0f) leftover = 0f;

                // A total below one gives each track its fraction OF the leftover rather than a share of it -
                // `grid-template-columns: 0.5fr` is half the free space, not all of it.
                float unit = leftover / Math.Max(flexTotal, 1f);

                for (int i = 0; i < n; i++)
                    if (sizing[i].Max.IsFraction) basis[i] = Math.Max(basis[i], sizing[i].Max.Fraction * unit);
            }

            for (int i = 0; i < n; i++) basis[i] = Settle(basis[i], float.PositiveInfinity);
            return basis;
        }

        /// <summary>`auto` as a MAXIMUM is max-content; as a minimum it is the automatic minimum, which is not the
        /// same question. This is where the one becomes the other.</summary>
        private static TrackSizeKind AsMaximum(TrackSizeKind kind) =>
            kind == TrackSizeKind.Auto ? TrackSizeKind.MaxContent : kind;

        private static float Finite(float value) => float.IsInfinity(value) ? 0f : value;

        private static float Settle(float value, float limit)
        {
            if (float.IsNaN(value) || value < 0f) return 0f;
            if (!float.IsInfinity(limit) && value > limit) return limit;
            return float.IsInfinity(value) ? 0f : value;
        }

        private static bool TryDefinite(TrackSize size, float available, out float px)
        {
            px = 0f;
            if (size.Kind != TrackSizeKind.Length) return false;

            if (size.Length.Unit == LenUnit.Percent)
            {
                if (float.IsNaN(available) || float.IsInfinity(available)) return false;
                px = size.Length.Resolve(available);
                return true;
            }

            px = size.Length.Resolve(0f);
            return true;
        }

        // ------------------------------------------------------------------------ measurement --

        /// <summary>
        /// What one item asks a COLUMN for. The row axis has no counterpart because a row's answer is simply the
        /// height the item came out at, whichever of the three kinds asked.
        /// </summary>
        private static float ColumnContribution(Cell cell, TrackSizeKind kind, float fallbackWidth, IMeasureText measure)
        {
            float margins = cell.MarginLeft + cell.MarginRight;

            switch (kind)
            {
                // The automatic minimum, narrowed to zero - see the class comment for why, and for what it costs.
                case TrackSizeKind.Auto:
                    return 0f;

                case TrackSizeKind.MinContent:
                    if (float.IsNaN(cell.MinContent)) cell.MinContent = MinContentWidth(cell.Node, measure);
                    return cell.MinContent + margins;

                default:
                    if (float.IsNaN(cell.MaxContent))
                        cell.MaxContent = MaxContentWidth(cell.Node, fallbackWidth, measure);
                    return cell.MaxContent + margins;
            }
        }

        /// <summary>
        /// The widest the item wants to be with no limit on the room - CSS's max-content contribution.
        ///
        /// Measured by laying the box out against an unbounded width, which is exactly what the sizing pass in
        /// <see cref="FlexLayout"/> already does for a text leaf. A percentage size cannot answer that question
        /// (there is nothing to be a percentage of), and comes back as infinity rather than as a number, so the
        /// second attempt asks at the container's own width - which is what CSS means when it says a percentage
        /// behaves as `auto` during intrinsic sizing.
        /// </summary>
        private static float MaxContentWidth(LayoutNode node, float fallbackWidth, IMeasureText measure)
        {
            FlexLayout.LayoutBox(node, float.PositiveInfinity, float.NaN, measure);
            float width = node.Width;
            if (IsUsable(width)) return width;

            FlexLayout.LayoutBox(node, fallbackWidth, float.NaN, measure);
            return IsUsable(node.Width) ? node.Width : 0f;
        }

        /// <summary>
        /// The narrowest the box can be made without its content spilling out of it.
        ///
        /// Only ever reached from a track that literally says `min-content`, and that is what lets it walk the
        /// subtree a second time: no page that does not write the keyword pays for it. The definition is CSS's
        /// own - the longest run of text that cannot be broken - and it is reachable through the existing
        /// measurer because a single word measured unbounded IS that run. Percentage padding resolves against
        /// zero here, as CSS specifies for an intrinsic pass.
        ///
        /// Coarser than the spec for containers: a nowrap row is the sum of its children and everything else is
        /// the widest of them. Wrapping, which could pack them tighter, is not tried.
        /// </summary>
        private static float MinContentWidth(LayoutNode node, IMeasureText measure)
        {
            ComputedStyle s = node.Style;
            if (s.Display == DisplayKind.None) return 0f;

            // Border-box sizing, so a declared width IS the answer rather than a starting point.
            if (s.Width.Unit == LenUnit.Px) return Math.Max(s.Width.Value, 0f);

            float frame = FlexLayout.Horizontal(s.Padding, 0f) + FlexLayout.Horizontal(s.BorderWidth, 0f);

            if (node.IsTextLeaf) return LongestUnbreakableRun(node.Text, s, measure) + frame;

            bool sideBySide = s.Display == DisplayKind.Flex
                              && (s.FlexDirection == FlexDirection.Row || s.FlexDirection == FlexDirection.RowReverse)
                              && s.FlexWrap == FlexWrap.NoWrap;

            float total = 0f, widest = 0f;
            int counted = 0;

            foreach (LayoutNode child in node.Children)
            {
                if (child.Style.Display == DisplayKind.None) continue;
                if (child.Style.Position != PositionKind.Static) continue;

                float width = MinContentWidth(child, measure) + FlexLayout.Horizontal(child.Style.Margin, 0f);
                widest = Math.Max(widest, width);
                total += width;
                counted++;
            }

            if (counted == 0) return frame;
            if (!sideBySide) return widest + frame;
            return total + s.ColumnGap.Resolve(0f) * (counted - 1) + frame;
        }

        private static float LongestUnbreakableRun(string text, ComputedStyle style, IMeasureText measure)
        {
            if (string.IsNullOrEmpty(text)) return 0f;

            // Text that may not wrap has no break point at all, so the whole run is the answer.
            if (style.WhiteSpace == WhiteSpaceKind.NoWrap || style.WhiteSpace == WhiteSpaceKind.Pre)
                return measure.Measure(text, style, float.PositiveInfinity).Width;

            float widest = 0f;
            foreach (string word in text.Split(' ', '\t', '\n', '\r'))
            {
                if (word.Length == 0) continue;
                widest = Math.Max(widest, measure.Measure(word, style, float.PositiveInfinity).Width);
            }

            return widest;
        }

        private static bool IsUsable(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        // --------------------------------------------------------------------------- geometry --

        private static float[] Starts(float[] sizes, float gap)
        {
            var starts = new float[sizes.Length];
            float cursor = 0f;

            for (int i = 0; i < sizes.Length; i++)
            {
                starts[i] = cursor;
                cursor += sizes[i] + gap;
            }

            return starts;
        }

        /// <summary>How far a span reaches: the tracks it covers plus the gaps BETWEEN them - never one before the
        /// first or after the last, which is the whole difference between a gap and a margin.</summary>
        private static float Extent(float[] sizes, int start, int span, float gap)
        {
            if (start < 0 || start >= sizes.Length) return 0f;

            int end = Math.Min(start + span, sizes.Length);
            float total = gap * Math.Max(end - start - 1, 0);
            for (int i = start; i < end; i++) total += sizes[i];
            return total;
        }

        /// <summary>
        /// The height of an item's area when - and only when - every row it covers was given a definite length.
        /// NaN otherwise, which is what an item with `height: 50%` needs to see so it falls back to its content
        /// rather than resolving against a number nobody has worked out yet.
        /// </summary>
        private static float DefiniteExtent(List<GridTrack> rows, int start, int span, float gap, float available)
        {
            if (start < 0) return float.NaN;

            float total = gap * Math.Max(span - 1, 0);
            for (int i = start; i < start + span; i++)
            {
                if (i >= rows.Count) return float.NaN;
                if (!TryDefinite(rows[i].Min, available, out float min)) return float.NaN;
                if (!TryDefinite(rows[i].Max, available, out float max)) return float.NaN;
                if (Math.Abs(min - max) > 0.01f) return float.NaN;
                total += min;
            }

            return total;
        }

        /// <summary>Whether the item fills its cell along an axis, which is what `stretch` means and what an item
        /// that declared its own size along that axis is exempt from.</summary>
        private static bool StretchWidth(Cell cell, ComputedStyle parent)
        {
            AlignKind justify = cell.Node.Style.JustifySelf != AlignKind.Auto ? cell.Node.Style.JustifySelf : parent.JustifyItems;
            return justify == AlignKind.Stretch && !cell.Node.Style.Width.IsDefinite;
        }

        private static bool StretchHeight(Cell cell, ComputedStyle parent)
        {
            AlignKind align = cell.Node.Style.AlignSelf != AlignKind.Auto ? cell.Node.Style.AlignSelf : parent.AlignItems;
            return align == AlignKind.Stretch && !cell.Node.Style.Height.IsDefinite;
        }

        /// <summary>Where in the free space of a cell the item sits. Stretch and start are both zero: an item that
        /// could not be stretched - it has a size of its own - sits at the start, as CSS says.</summary>
        private static float Offset(AlignKind align, float free)
        {
            if (free <= 0f) return 0f;

            switch (align)
            {
                case AlignKind.FlexEnd: return free;
                case AlignKind.Center: return free * 0.5f;
                default: return 0f;
            }
        }
    }
}
