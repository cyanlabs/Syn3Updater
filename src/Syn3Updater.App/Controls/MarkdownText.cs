using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Services;

namespace Cyanlabs.Syn3Updater.App.Controls;

/// <summary>Renders the Markdown subset from <see cref="Markdown"/>: &lt;ctl:MarkdownText Text="{Binding Notes}" /&gt;</summary>
public class MarkdownText : StackPanel
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<MarkdownText, string?>(nameof(Text));
    public static readonly StyledProperty<double> FontSizeProperty = AvaloniaProperty.Register<MarkdownText, double>(nameof(FontSize), 14);
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    public MarkdownText() { Spacing = 4; ActualThemeVariantChanged += (_, _) => Rebuild(); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TextProperty || e.Property == FontSizeProperty) Rebuild();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); Rebuild(); }

    private IBrush Res(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;

    private void Rebuild()
    {
        Children.Clear();
        var brand = Res("Brand", Brushes.SteelBlue);
        var chip = Res("ChipBg", Brushes.DimGray);
        var muted = Res("Muted", Brushes.Gray);

        foreach (var block in Markdown.Parse(Text))
        {
            switch (block.Kind)
            {
                case MdBlockKind.Rule:
                    Children.Add(new Border { Height = 1, Background = Res("CardBorder", Brushes.Gray), Margin = new Thickness(0, 6) });
                    break;
                case MdBlockKind.Code:
                    Children.Add(new Border
                    {
                        Background = chip, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8),
                        Child = new SelectableTextBlock { Text = ((MdText)block.Inlines[0]).Text, FontFamily = Mono, FontSize = FontSize - 1, TextWrapping = TextWrapping.Wrap },
                    });
                    break;
                default:
                    Children.Add(BuildText(block, brand, chip, muted));
                    break;
            }
        }
    }

    private static readonly FontFamily Mono = new("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace");

    private Control BuildText(MdBlock block, IBrush brand, IBrush chip, IBrush muted)
    {
        var tb = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = FontSize };
        if (block.Kind == MdBlockKind.Heading)
        { tb.FontWeight = FontWeight.SemiBold; tb.FontSize = FontSize + Math.Max(0, 4 - block.Level) * 1.5; tb.Margin = new Thickness(0, 6, 0, 0); }

        if (block.Kind == MdBlockKind.Bullet) tb.Inlines!.Add(new Run("•  ") { Foreground = brand });
        foreach (var inline in block.Inlines)
        {
            switch (inline)
            {
                case MdText t:
                    var run = new Run(t.Text);
                    if (t.Bold) run.FontWeight = FontWeight.Bold;
                    if (t.Italic) run.FontStyle = FontStyle.Italic;
                    if (t.Code) { run.FontFamily = Mono; run.Foreground = brand; }
                    tb.Inlines!.Add(run);
                    break;
                case MdLink l:
                    var link = new TextBlock { Text = l.Text, Foreground = brand, TextDecorations = TextDecorations.Underline, Cursor = new Cursor(StandardCursorType.Hand), FontSize = tb.FontSize };
                    ToolTip.SetTip(link, l.Url);
                    var url = l.Url;
                    link.PointerPressed += (_, _) => _ = Dialogs.OpenUrl(url);
                    tb.Inlines!.Add(new InlineUIContainer(link) { BaselineAlignment = BaselineAlignment.Baseline });
                    break;
            }
        }
        if (block.Kind == MdBlockKind.Quote)
            return new Border { BorderBrush = brand, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 2, 0, 2), Child = tb };
        if (block.Kind == MdBlockKind.Bullet) tb.Margin = new Thickness(8, 0, 0, 0);
        return tb;
    }
}
