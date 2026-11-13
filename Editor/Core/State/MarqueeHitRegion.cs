using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using System.Collections.Generic;
using System;

namespace Zep.Core.State
{
    // The cells a marquee rectangle covers, in VISIBLE-index space: rows
    // FirstRow..LastRow crossed with columns FirstColumn..LastColumn of a
    // ColumnsPerRow-wide row. List mode is the single-column case.
    //
    // The marquee asks for this region rather than for the paths inside it
    // because two consecutive pointer moves overlap almost completely: the
    // region says which cells ENTERED or LEFT, so a drag that has swept 40,000
    // rows resolves a few hundred paths per move instead of all 40,000.
    public readonly struct MarqueeHitRegion
    {
        public static readonly MarqueeHitRegion Empty = default;

        public MarqueeHitRegion(
            int firstRow,
            int lastRow,
            int firstColumn,
            int lastColumn,
            int columnsPerRow)
        {
            FirstRow = firstRow;
            LastRow = lastRow;
            FirstColumn = firstColumn;
            LastColumn = lastColumn;
            ColumnsPerRow = columnsPerRow;
        }

        internal int FirstRow { get; }
        internal int LastRow { get; }
        internal int FirstColumn { get; }
        internal int LastColumn { get; }
        internal int ColumnsPerRow { get; }

        internal bool IsEmpty =>
            ColumnsPerRow <= 0 || LastRow < FirstRow || LastColumn < FirstColumn;

        public bool Matches(in MarqueeHitRegion other)
        {
            if (IsEmpty || other.IsEmpty)
            {
                return IsEmpty && other.IsEmpty;
            }

            return FirstRow == other.FirstRow
                && LastRow == other.LastRow
                && FirstColumn == other.FirstColumn
                && LastColumn == other.LastColumn
                && ColumnsPerRow == other.ColumnsPerRow;
        }

        // Appends the visible indices this region covers and `other` does not.
        // Cost is the size of that difference, never the size of the region —
        // that is the whole point of tracking a region at all. A column-count
        // change (grid resize mid-drag) makes the two incomparable, so the
        // whole region is reported as new.
        public void AppendCellsMissingFrom(in MarqueeHitRegion other, List<int> appendTo)
        {
            if (IsEmpty)
            {
                return;
            }

            if (other.IsEmpty
                || other.ColumnsPerRow != ColumnsPerRow
                || other.LastRow < FirstRow
                || other.FirstRow > LastRow
                || other.LastColumn < FirstColumn
                || other.FirstColumn > LastColumn)
            {
                AppendBand(FirstRow, LastRow, FirstColumn, LastColumn, appendTo);
                return;
            }

            // Rows above and below the shared band, full width.
            AppendBand(FirstRow, Math.Min(LastRow, other.FirstRow - 1), FirstColumn, LastColumn, appendTo);
            AppendBand(Math.Max(FirstRow, other.LastRow + 1), LastRow, FirstColumn, LastColumn, appendTo);

            // Inside the shared band only the columns outside `other` are new;
            // when the column span did not move, neither band exists and the
            // shared rows cost nothing.
            var sharedFirstRow = Math.Max(FirstRow, other.FirstRow);
            var sharedLastRow = Math.Min(LastRow, other.LastRow);
            AppendBand(
                sharedFirstRow,
                sharedLastRow,
                FirstColumn,
                Math.Min(LastColumn, other.FirstColumn - 1),
                appendTo);
            AppendBand(
                sharedFirstRow,
                sharedLastRow,
                Math.Max(FirstColumn, other.LastColumn + 1),
                LastColumn,
                appendTo);
        }

        private void AppendBand(
            int firstRow,
            int lastRow,
            int firstColumn,
            int lastColumn,
            List<int> appendTo)
        {
            if (firstRow > lastRow || firstColumn > lastColumn)
            {
                return;
            }

            for (var row = firstRow; row <= lastRow; row++)
            {
                var rowOrigin = row * ColumnsPerRow;
                for (var column = firstColumn; column <= lastColumn; column++)
                {
                    appendTo.Add(rowOrigin + column);
                }
            }
        }
    }
}
