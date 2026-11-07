using Avalonia;
using Avalonia.Controls;

namespace Cyanlabs.Syn3Updater.App.Controls;

/// <summary>
/// Lays its children out in as many equal columns as fit (each at least <see cref="MinItemWidth"/> wide) and stretches them to the full width, so the rows always
/// end at the right edge: 3 tiles of 240 in a 1000 px column become 3 tiles of 326, and a narrow window gets one tile per row at the full width.
/// A row is as tall as its tallest tile.
/// </summary>
public class AdaptiveColumns : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty = AvaloniaProperty.Register<AdaptiveColumns, double>(nameof(MinItemWidth), 240);
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<AdaptiveColumns, double>(nameof(Spacing), 10);

    static AdaptiveColumns() => AffectsMeasure<AdaptiveColumns>(MinItemWidthProperty, SpacingProperty);

    public double MinItemWidth { get => GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    /// <summary>How many columns fit in <paramref name="width"/> for <paramref name="count"/> tiles, and how wide each one is.</summary>
    public static (int Columns, double ItemWidth) Plan(double width, int count, double minItemWidth, double spacing)
    {
        if (count <= 0) return (0, 0);
        var columns = (int)Math.Floor((width + spacing) / (Math.Max(minItemWidth, 1) + spacing));
        columns = Math.Clamp(columns, 1, count);
        return (columns, Math.Max(0, (width - (columns - 1) * spacing) / columns));
    }

    private List<Control> Tiles() => Children.Where(c => c.IsVisible).ToList();

    protected override Size MeasureOverride(Size availableSize)
    {
        var tiles = Tiles();
        if (tiles.Count == 0) return default;
        // an unbounded width (inside a horizontal scroller) has no edge to stretch to: use the widest row that would fit three tiles
        var width = double.IsInfinity(availableSize.Width) ? MinItemWidth * Math.Min(3, tiles.Count) + Spacing * (Math.Min(3, tiles.Count) - 1) : availableSize.Width;
        var (columns, itemWidth) = Plan(width, tiles.Count, MinItemWidth, Spacing);
        double height = 0;
        for (var row = 0; row * columns < tiles.Count; row++)
        {
            double rowHeight = 0;
            for (var i = row * columns; i < Math.Min(tiles.Count, (row + 1) * columns); i++)
            {
                tiles[i].Measure(new Size(itemWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, tiles[i].DesiredSize.Height);
            }
            height += rowHeight + (row > 0 ? Spacing : 0);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var tiles = Tiles();
        if (tiles.Count == 0) return finalSize;
        var (columns, itemWidth) = Plan(finalSize.Width, tiles.Count, MinItemWidth, Spacing);
        double y = 0;
        for (var row = 0; row * columns < tiles.Count; row++)
        {
            double rowHeight = 0;
            for (var i = row * columns; i < Math.Min(tiles.Count, (row + 1) * columns); i++)
            {
                tiles[i].Measure(new Size(itemWidth, double.PositiveInfinity));     // the final width can differ from the one that was measured
                rowHeight = Math.Max(rowHeight, tiles[i].DesiredSize.Height);
            }
            for (var i = row * columns; i < Math.Min(tiles.Count, (row + 1) * columns); i++)
                tiles[i].Arrange(new Rect((i - row * columns) * (itemWidth + Spacing), y, itemWidth, rowHeight));
            y += rowHeight + Spacing;
        }
        return finalSize;
    }
}
