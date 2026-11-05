using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;

namespace Cyanlabs.Syn3Updater.App.Services;

public record DialogButton(string Label, int Index, bool IsPrimary);

public enum DialogTone { Normal, Warning, Error }

/// <summary>One message to show. <see cref="Result"/> completes with the index of the chosen button (0 = cancel/first).</summary>
public class DialogRequest
{
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public IReadOnlyList<DialogButton> Buttons { get; init; } = Array.Empty<DialogButton>();
    public DialogTone Tone { get; init; }
    public int DefaultIndex { get; init; }
    public bool IsPrompt { get; init; }
    public string Text { get; set; } = "";
    public bool IsError => Tone == DialogTone.Error;
    public bool IsWarning => Tone == DialogTone.Warning;
    internal TaskCompletionSource<int> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Draws dialogs inside the main window (a dimmed overlay) instead of opening separate OS windows.
/// Separate windows make some compositors (e.g. Hyprland) move focus and the mouse pointer to the new window.
/// Requests are queued so two dialogs never overlap.
/// </summary>
public partial class DialogHostViewModel : ObservableObject
{
    public static DialogHostViewModel Instance { get; } = new();

    private readonly Queue<DialogRequest> _queue = new();
    [ObservableProperty] private DialogRequest? _current;

    /// <summary>True while a main window is on screen that can show the overlay.</summary>
    public bool IsAttached { get; set; }
    public bool HasDialog => Current != null;
    partial void OnCurrentChanged(DialogRequest? value) => OnPropertyChanged(nameof(HasDialog));

    public Task<int> ShowAsync(DialogRequest request)
    {
        void Enqueue()
        {
            if (Current == null) Current = request; else _queue.Enqueue(request);
        }
        if (Dispatcher.UIThread.CheckAccess()) Enqueue(); else Dispatcher.UIThread.Post(Enqueue);
        return request.Completion.Task;
    }

    [RelayCommand]
    private void Choose(int index)
    {
        if (Current is not { } done) return;
        Current = _queue.Count > 0 ? _queue.Dequeue() : null;
        done.Completion.TrySetResult(index);
    }

    /// <summary>Escape: the first button is always the "cancel" / "no" / "OK" answer.</summary>
    public void Cancel() { if (Current != null) Choose(0); }

    /// <summary>Enter: the dialog's default button.</summary>
    public void AcceptDefault() { if (Current is { } c) Choose(c.DefaultIndex); }
}
