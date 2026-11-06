using Avalonia.Controls;

namespace Cyanlabs.Syn3Updater.App.Views;

public partial class HomeView : UserControl
{
    /// <summary>Below this width the bottom bar stacks: what will happen on its own row, MY20 and the buttons under it.</summary>
    private const double CompactBelow = 880;

    public HomeView()
    {
        InitializeComponent();
        var bar = this.FindControl<Grid>("BarGrid")!;
        bar.SizeChanged += (_, e) => bar.Classes.Set("compact", e.NewSize.Width < CompactBelow);
    }
}
