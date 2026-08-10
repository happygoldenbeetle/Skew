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

    /// <summary>
    /// Gap between tiles. Laid out *between* them and never on the outside, so a
    /// full row spans the panel exactly and its edges line up with the omnibox
    /// above it. Drawing it inside each tile instead left the row a gutter short
    /// of the sidebar's right edge.
    /// </summary>
    private const double Gutter = 6;

    /// <summary>Below this a tile is too narrow to read as a favicon target.</summary>
    private const double MinTileWidth = 56;

    private const double TileHeight = 44;

    protected override Size MeasureOverride(Size availableSize)
    {
        int count = Children.Count;
        if (count == 0) return new Size(0, 0);

        double width = availableSize.Width;
        // Unconstrained (a measure pass with infinite width) asks for the
        // widest row we would ever draw rather than collapsing to one column.
        if (double.IsInfinity(width) || double.IsNaN(width))
        {
            int widest = Math.Min(count, MaxColumns);
            width = MinTileWidth * widest + Gutter * (widest - 1);
        }

        (int columns, double tileWidth) = Metrics(width, count);

        var cell = new Size(tileWidth, TileHeight);
        foreach (UIElement child in Children)
            child.Measure(cell);

        return new Size(width, Height(count, columns));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int count = Children.Count;
        if (count == 0) return finalSize;

        (int columns, double tileWidth) = Metrics(finalSize.Width, count);

        for (int i = 0; i < count; i++)
        {
            Children[i].Arrange(new Rect(
                (i % columns) * (tileWidth + Gutter),
                (i / columns) * (TileHeight + Gutter),
                tileWidth,
                TileHeight));
        }

        return new Size(finalSize.Width, Height(count, columns));
    }

    /// <summary>
    /// Columns and tile width for a given panel width. Measure and arrange both
    /// go through this, so the two passes can never disagree about the wrap.
    /// </summary>
    private static (int Columns, double TileWidth) Metrics(double width, int count)
    {
        // n tiles carry n-1 gutters, so adding one gutter to both sides of the
        // ratio gives how many fit without over-counting the trailing gap.
        int fits = Math.Max(1, (int)((width + Gutter) / (MinTileWidth + Gutter)));
        int columns = Math.Max(1, Math.Min(Math.Min(count, MaxColumns), fits));

        // Left exact rather than floored: the remainder of the division is what
        // used to leave the row a couple of pixels short of the panel edge, and
        // nothing here wraps on overflow, so fractional widths are safe.
        double tileWidth = (width - Gutter * (columns - 1)) / columns;
        return (columns, Math.Max(0, tileWidth));
    }

    private static double Height(int count, int columns)
    {
        int rows = Rows(count, columns);
        return rows * TileHeight + (rows - 1) * Gutter;
    }

    private static int Rows(int count, int columns) => (count + columns - 1) / columns;
}
