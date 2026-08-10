using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Mori.Controls;

/// <summary>
/// The items panel behind the sidebar's pinned tiles: uniform cells, as many
/// per row as the sidebar is wide enough for, and a row that always ends flush
/// with the sidebar edge.
///
/// <para>
/// This replaces a <see cref="VariableSizedWrapGrid"/> whose cell size was
/// written from the host's <c>SizeChanged</c> handler. That fires *after*
/// layout, so every frame of a sidebar resize drag was laid out with the cell
/// width from the previous frame — while shrinking, the stale width overflowed
/// the row, the wrap grid pushed the last cell of each row down, and the
/// corrected width pulled it back on the following pass. That one-frame lag was
/// the flicker. Computing the cells here, inside measure, uses the width the
/// panel is actually being given, so there is no frame that disagrees with
/// itself and no layout-invalidating event handler.
/// </para>
///
/// <para>
/// A plain <see cref="Panel"/>, so every child is realized: the peek copy of the
/// sidebar is parked off-screen by a render transform, which virtualizing panels
/// read as an empty viewport. Measure constraints, unlike viewports, ignore
/// render transforms, so this lays out correctly in both copies.
/// </para>
/// </summary>
public sealed class PinnedTileGrid : Panel
{
    /// <summary>Upper bound on tiles per row, not a target.</summary>
    private const int MaxColumns = 4;

    /// <summary>Gap between tiles. Drawn inside the tile, so it is part of the cell.</summary>
    private const double Gutter = 6;

    /// <summary>
    /// Narrowest cell that still reads as a favicon target: 56pt of tile plus
    /// the gutter it draws. Below this the row drops a column instead.
    /// </summary>
    private const double MinCellWidth = 56 + Gutter;

    /// <summary>Tile height plus the gutter it draws below itself.</summary>
    private const double CellHeight = 44 + Gutter;

    protected override Size MeasureOverride(Size availableSize)
    {
        int count = Children.Count;
        if (count == 0) return new Size(0, 0);

        double width = availableSize.Width;
        // Unconstrained (a measure pass with infinite width) asks for the
        // widest row we would ever draw rather than collapsing to one column.
        if (double.IsInfinity(width) || double.IsNaN(width))
            width = MinCellWidth * Math.Min(count, MaxColumns);

        (int columns, double cellWidth) = Metrics(width, count);

        var cell = new Size(cellWidth, CellHeight);
        foreach (UIElement child in Children)
            child.Measure(cell);

        return new Size(columns * cellWidth, Rows(count, columns) * CellHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int count = Children.Count;
        if (count == 0) return finalSize;

        (int columns, double cellWidth) = Metrics(finalSize.Width, count);

        for (int i = 0; i < count; i++)
        {
            Children[i].Arrange(new Rect(
                (i % columns) * cellWidth,
                (i / columns) * CellHeight,
                cellWidth,
                CellHeight));
        }

        return new Size(finalSize.Width, Rows(count, columns) * CellHeight);
    }

    /// <summary>
    /// Columns and cell width for a given panel width. Measure and arrange both
    /// go through this, so the two passes can never disagree about the wrap.
    /// </summary>
    private static (int Columns, double CellWidth) Metrics(double width, int count)
    {
        int fits = Math.Max(1, (int)(width / MinCellWidth));
        int columns = Math.Max(1, Math.Min(Math.Min(count, MaxColumns), fits));

        // Floored, so the row can never round its way past the panel edge —
        // overflowing by a fraction of a pixel is what wraps a trailing tile.
        return (columns, Math.Floor(width / columns));
    }

    private static int Rows(int count, int columns) => (count + columns - 1) / columns;
}
