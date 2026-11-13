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
/// <summary>Optional presentation: show the app logo, and render the message as Markdown (bold, bullets, links).</summary>
public record DialogOptions(bool Logo = false, bool Markdown = false);

public interface IDialogBackend
{
    Task<int> Show(string message, string title, string[] buttons, bool warning, bool error, int defaultIndex, DialogOptions? options = null);
    /// <summary>Opens a folder picker. <paramref name="startPath"/> is where it opens (its nearest existing parent when the folder is gone); null leaves the choice to the OS.</summary>
    Task<string?> PickFolder(string title, string? startPath = null);
    /// <summary>Opens a file picker. <paramref name="startPath"/> is where it opens (its nearest existing parent when the folder is gone); null leaves the choice to the OS.</summary>
    Task<string?> PickFile(string title, string filterName, string[] patterns, string? startPath = null);
    /// <summary>Asks for text: one line, or several when <paramref name="multiline"/> (Enter then adds a line and Ctrl+Enter confirms). Returns null when cancelled.</summary>
    Task<string?> Prompt(string title, string message, string initial, bool multiline = false);
    Task SetClipboard(string text);
    Task OpenUrl(string url);
}

public static class Dialogs
{
    public static IDialogBackend Backend { get; set; } = new AvaloniaDialogBackend();
    public static Window? MainWindow => DialogsImpl.MainWindow;

    public static Task Error(string message) => Backend.Show(message, Ctx.T("String.Error"), new[] { Ctx.T("String.OK") }, false, true, 0);
    /// <summary>An error with one extra button next to OK; true when that button was pressed.</summary>
    public static async Task<bool> Error(string message, string actionLabel) =>
        await Backend.Show(message, Ctx.T("String.Error"), new[] { Ctx.T("String.OK"), actionLabel }, false, true, 0) == 1;
    public static Task Info(string message, string? title = null) => Backend.Show(message, title ?? Ctx.T("String.Notice"), new[] { Ctx.T("String.OK") }, false, false, 0);

    /// <summary>Yes/No question. Returns true for yes.</summary>
    public static async Task<bool> Confirm(string message, string? title = null, string? yes = null, string? no = null, bool warning = false) =>
        await Backend.Show(message, title ?? Ctx.T("String.Notice"), new[] { no ?? Ctx.T("String.No"), yes ?? Ctx.T("String.Yes") }, warning, false, 0) == 1;

    public static Task<int> Show(string message, string title, string[] buttons, int defaultIndex = 0, DialogOptions? options = null) => Backend.Show(message, title, buttons, false, false, defaultIndex, options);
    public static Task<string?> PickFolder(string title, string? startPath = null) => Backend.PickFolder(title, startPath);
    public static Task<string?> Prompt(string title, string message, string initial = "", bool multiline = false) => Backend.Prompt(title, message, initial, multiline);
    public static Task<string?> PickFile(string title, string filterName, params string[] patterns) => Backend.PickFile(title, filterName, patterns);
    /// <summary>Like <see cref="PickFile"/>, opening in <paramref name="startPath"/> (its nearest existing parent when it is gone).</summary>
    public static Task<string?> PickFileIn(string? startPath, string title, string filterName, params string[] patterns) => Backend.PickFile(title, filterName, patterns, startPath);
    public static Task SetClipboard(string text) => Backend.SetClipboard(text);
    public static Task OpenUrl(string url) => Backend.OpenUrl(url);
}

internal sealed class AvaloniaDialogBackend : IDialogBackend
{
    public async Task<int> Show(string message, string title, string[] buttons, bool warning, bool error, int defaultIndex, DialogOptions? options = null)
    {
        var host = DialogHostViewModel.Instance;
        if (!host.IsAttached)    // no main window yet (or already closed): a stand-alone window is the only option
            return await DialogsImpl.Show(message, title, buttons, error ? Brushes.Firebrick : warning ? Brushes.DarkOrange : null, defaultIndex);
        return await host.ShowAsync(new DialogRequest
        {
            Title = title, Message = message.Trim(), DefaultIndex = Math.Clamp(defaultIndex, 0, buttons.Length - 1),
            ShowLogo = options?.Logo == true, UseMarkdown = options?.Markdown == true,
            Tone = error ? DialogTone.Error : warning ? DialogTone.Warning : DialogTone.Normal,
            Buttons = buttons.Select((label, i) => new DialogButton(label, i, buttons.Length > 1 && i == buttons.Length - 1)).ToList(),
        });
    }

    public async Task<string?> Prompt(string title, string message, string initial, bool multiline = false)
    {
        var host = DialogHostViewModel.Instance;
        if (!host.IsAttached) return await DialogsImpl.Prompt(title, message, initial, multiline);
        var request = new DialogRequest
        {
            Title = title, Message = message, IsPrompt = true, Multiline = multiline, Text = initial, DefaultIndex = 1,
            Buttons = new[] { new DialogButton(Ctx.T("Download.CancelButton"), 0, false), new DialogButton(Ctx.T("String.OK"), 1, true) },
        };
        return await host.ShowAsync(request) == 1 ? request.Text : null;
    }

    public Task<string?> PickFolder(string title, string? startPath = null) => DialogsImpl.PickFolder(title, startPath);
    public Task<string?> PickFile(string title, string filterName, string[] patterns, string? startPath = null) => DialogsImpl.PickFile(title, filterName, patterns, startPath);
    public Task SetClipboard(string text) => DialogsImpl.SetClipboard(text);
    public Task OpenUrl(string url) => DialogsImpl.OpenUrl(url);
}

public static class DialogsImpl
{
    public static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private static Task<T> OnUi<T>(Func<Task<T>> f) =>
        Dispatcher.UIThread.CheckAccess() ? f() : Dispatcher.UIThread.InvokeAsync(f);

    /// <summary>
    /// Shows <paramref name="win"/> and completes when it is closed. Modal over <paramref name="owner"/> when that window is on screen; otherwise a stand-alone window in the middle of the screen
    /// and in the taskbar, because Avalonia refuses to show a dialog over an owner that is not visible yet (the main window is created before it appears, and the Home page can report an
    /// error that early) and a stand-alone window without a taskbar entry could not be found again.
    /// </summary>
    internal static async Task ShowAndWaitAsync(Window win, Window? owner)
    {
        if (owner is { IsVisible: true }) { await win.ShowDialog(owner); return; }
        var closed = new TaskCompletionSource();
        win.Closed += (_, _) => closed.TrySetResult();
        win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        win.ShowInTaskbar = true;
        win.Show();
        await closed.Task;
    }


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
        await ShowAndWaitAsync(win, MainWindow);
        return result;
    });

    public static Task<string?> Prompt(string title, string message, string initial, bool multiline = false) => OnUi(async () =>
    {
        string? result = null;
        var win = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, MinWidth = 380, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false };
        var box = new TextBox { Text = initial, MinWidth = 320 };
        if (multiline) { box.AcceptsReturn = true; box.TextWrapping = TextWrapping.Wrap; box.MinHeight = 120; box.MaxHeight = 220; }
        var ok = new Button { Content = Ctx.T("String.OK"), MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("accent");
        var cancel = new Button { Content = Ctx.T("Download.CancelButton"), MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        void Accept() { result = box.Text; win.Close(); }
        ok.Click += (_, _) => Accept(); cancel.Click += (_, _) => win.Close();
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter && (!multiline || e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))) Accept(); };
        win.Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 14,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
        win.Opened += (_, _) => box.Focus();
        await ShowAndWaitAsync(win, MainWindow);
        return result;
    });

    public static Task<string?> PickFolder(string title, string? startPath = null) => OnUi(async () =>
    {
        if (MainWindow?.StorageProvider is not { } sp) return null;
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (ExistingFolder(startPath) is { } start)
        {
            try { options.SuggestedStartLocation = await sp.TryGetFolderFromPathAsync(start); }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }     // the OS decides where to open
        }
        var r = await sp.OpenFolderPickerAsync(options);
        return r.Count > 0 ? r[0].TryGetLocalPath() : null;
    });

    /// <summary>The folder itself when it exists, otherwise its nearest existing parent (the saved download folder may be on a drive that is not plugged in), otherwise null.</summary>
    public static string? ExistingFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            for (var dir = path.Trim(); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                if (Directory.Exists(dir)) return dir;
        }
        catch (ArgumentException) { }
        catch (PathTooLongException) { }
        catch (NotSupportedException) { }
        return null;
    }

    public static Task<string?> PickFile(string title, string filterName, string[] patterns, string? startPath = null) => OnUi(async () =>
    {
        if (MainWindow?.StorageProvider is not { } sp) return null;
        var options = new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(filterName) { Patterns = patterns } },
        };
        if (ExistingFolder(startPath) is { } start)
        {
            try { options.SuggestedStartLocation = await sp.TryGetFolderFromPathAsync(start); }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }     // the OS decides where to open
        }
        var r = await sp.OpenFilePickerAsync(options);
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
