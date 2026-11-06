using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Cyanlabs.Syn3Updater.Core.Services;

/// <summary>
/// Keeps one Syn3 Updater running per user and data folder. A second launch (for example clicking a syn3updater:// link while the app is open)
/// hands its message to the first and exits, so two copies never write to the same settings or the same USB stick.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly NamedPipeServerStream _server;
    private readonly FileStream _lock;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(NamedPipeServerStream server, FileStream lockFile) { _server = server; _lock = lockFile; }

    /// <summary>Pipe name unique to this OS user and data folder (so a test or a second profile directory never collides with a real session).</summary>
    public static string NameFor(string dataDir)
    {
        var id = Environment.UserName + "|" + Path.GetFullPath(dataDir);
        return "syn3updater-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Becomes the primary instance, or returns null when another one already owns the name.
    /// Ownership is an exclusive lock on a file: the OS drops it if the app dies, and it works across processes on every OS.
    /// (A pipe name alone is not enough: on Linux .NET lets a second process re-bind the pipe's socket file, so two copies would both think they are first.)
    /// </summary>
    public static SingleInstance? TryBecomePrimary(string name)
    {
        FileStream? lockFile = null;
        try
        {
            var path = Path.Combine(Path.GetTempPath(), name + ".lock");
            var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;   // owner only (Windows has no such mode)
            lockFile = new FileStream(path, options);
            return new SingleInstance(new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly), lockFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            lockFile?.Dispose();
            return null;                                   // the lock is held: another instance is running
        }
    }

    /// <summary>Starts delivering messages from later launches to <paramref name="onMessage"/> (called on a background thread).</summary>
    public void Listen(Action<string> onMessage) => _ = Task.Run(async () =>
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await _server.WaitForConnectionAsync(_cts.Token);
                using var reader = new StreamReader(_server, Encoding.UTF8, false, 1024, leaveOpen: true);
                var message = await reader.ReadToEndAsync(_cts.Token);
                if (message.Length is > 0 and <= 4096) onMessage(message);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { if (_cts.IsCancellationRequested) return; }
            finally
            {
                // Always disconnect, even when IsConnected is already false: once the sender has closed its end, Windows still needs the server end
                // disconnected before it will accept the next client, otherwise a second link a moment later waits out its timeout and a second window opens.
                try { _server.Disconnect(); } catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
            }
        }
    });

    /// <summary>Sends a message to the running instance. False when nobody is listening.</summary>
    public static async Task<bool> TrySendAsync(string name, string message, int timeoutMs = 2000)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeoutMs);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            await writer.WriteAsync(message);
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException) { return false; }
    }

    public void Dispose() { _cts.Cancel(); _server.Dispose(); _lock.Dispose(); _cts.Dispose(); }
}
