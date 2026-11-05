using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Cyanlabs.Syn3Updater.App.Services;

/// <summary>Modal message boxes plus file/folder pickers and URL launching, all marshalled onto the UI thread.</summary>
/// <summary>Everything the app asks of the UI layer. Tests replace the backend with a scripted fake.</summary>
public interface IDialogBackend
{
    Task<int> Show(string message, string title, string[] buttons, bool warning, bool error, int defaultIndex);
    Task<string?> PickFolder(string title);
    Task<string?> PickFile(string title, string filterName, string[] patterns);
    Task SetClipboard(string text);
    Task OpenUrl(string url);
}

public static class Dialogs
{
    public static IDialogBackend Backend { get; set; } = new AvaloniaDialogBackend();
    public static Window? MainWindow => DialogsImpl.MainWindow;

    public static Task Error(string message) => Backend.Show(message, Ctx.T("String.Error"), new[] { Ctx.T("String.OK") }, false, true, 0);
    public static Task Info(string message, string? title = null) => Backend.Show(message, title ?? Ctx.T("String.Notice"), new[] { Ctx.T("String.OK") }, false, false, 0);

    /// <summary>Yes/No question. Returns true for yes.</summary>
    public static async Task<bool> Confirm(string message, string? title = null, string? yes = null, string? no = null, bool warning = false) =>
        await Backend.Show(message, title ?? Ctx.T("String.Notice"), new[] { no ?? Ctx.T("String.No"), yes ?? Ctx.T("String.Yes") }, warning, false, 0) == 1;

    public static Task<int> Show(string message, string title, string[] buttons, int defaultIndex = 0) => Backend.Show(message, title, buttons, false, false, defaultIndex);
    public static Task<string?> PickFolder(string title) => Backend.PickFolder(title);
    public static Task<string?> PickFile(string title, string filterName, params string[] patterns) => Backend.PickFile(title, filterName, patterns);
    public static Task SetClipboard(string text) => Backend.SetClipboard(text);
    public static Task OpenUrl(string url) => Backend.OpenUrl(url);
}

internal sealed class AvaloniaDialogBackend : IDialogBackend
{
    public Task<int> Show(string message, string title, string[] buttons, bool warning, bool error, int defaultIndex) =>
        DialogsImpl.Show(message, title, buttons, error ? Brushes.Firebrick : warning ? Brushes.DarkOrange : null, defaultIndex);
    public Task<string?> PickFolder(string title) => DialogsImpl.PickFolder(title);
    public Task<string?> PickFile(string title, string filterName, string[] patterns) => DialogsImpl.PickFile(title, filterName, patterns);
    public Task SetClipboard(string text) => DialogsImpl.SetClipboard(text);
    public Task OpenUrl(string url) => DialogsImpl.OpenUrl(url);
}

public static class DialogsImpl
{
    public static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private static Task<T> OnUi<T>(Func<Task<T>> f) =>
        Dispatcher.UIThread.CheckAccess() ? f() : Dispatcher.UIThread.InvokeAsync(f);



    public static Task<int> Show(string message, string title, string[] buttons, IBrush? accent = null, int defaultIndex = 0) => OnUi(async () =>
    {
        var result = defaultIndex;
        var win = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, MaxWidth = 620, MinWidth = 380, MaxHeight = 700,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false,
        };
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 18 };
        if (accent != null)
            panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = accent });
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 480, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new SelectableTextBlock { Text = message.Trim(), TextWrapping = TextWrapping.Wrap },
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < buttons.Length; i++)
        {
            int idx = i;
            var b = new Button { Content = buttons[i], MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (i == buttons.Length - 1 && buttons.Length > 1) b.Classes.Add("accent");
            b.Click += (_, _) => { result = idx; win.Close(); };
            row.Children.Add(b);
        }
        panel.Children.Add(row);
        win.Content = panel;
        if (MainWindow is { } owner) await win.ShowDialog(owner); else { win.Show(); await Task.Delay(Timeout.Infinite); }
        return result;
    });

    public static Task<string?> PickFolder(string title) => OnUi(async () =>
    {
        if (MainWindow?.StorageProvider is not { } sp) return null;
        var r = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return r.Count > 0 ? r[0].TryGetLocalPath() : null;
    });

    public static Task<string?> PickFile(string title, string filterName, params string[] patterns) => OnUi(async () =>
    {
        if (MainWindow?.StorageProvider is not { } sp) return null;
        var r = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(filterName) { Patterns = patterns } },
        });
        return r.Count > 0 ? r[0].TryGetLocalPath() : null;
    });

    public static async Task SetClipboard(string text)
    {
        await OnUi(async () =>
        {
            if (MainWindow?.Clipboard is { } c) await c.SetTextAsync(text);
            return true;
        });
    }

    /// <summary>Opens a web page (or folder) in the OS default handler; copies the link and tells the user if that fails.</summary>
    public static async Task OpenUrl(string url)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo { UseShellExecute = false };
            if (OperatingSystem.IsWindows()) { psi.FileName = url; psi.UseShellExecute = true; }
            else if (OperatingSystem.IsMacOS()) { psi.FileName = "open"; psi.ArgumentList.Add(url); }
            else { psi.FileName = "xdg-open"; psi.ArgumentList.Add(url); }
            using var p = System.Diagnostics.Process.Start(psi);
        }
        catch (Exception)
        {
            await SetClipboard(url);
            await Dialogs.Info(Ctx.T("MessageBox.NoBrowser"));
        }
    }
}
