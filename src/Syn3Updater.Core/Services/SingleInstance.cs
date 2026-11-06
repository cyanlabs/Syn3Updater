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
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(NamedPipeServerStream server) { _server = server; }

    /// <summary>Pipe name unique to this OS user and data folder (so a test or a second profile directory never collides with a real session).</summary>
    public static string NameFor(string dataDir)
    {
        var id = Environment.UserName + "|" + Path.GetFullPath(dataDir);
        return "syn3updater-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16].ToLowerInvariant();
    }

    /// <summary>Becomes the primary instance, or returns null when another one already owns the name.</summary>
    public static SingleInstance? TryBecomePrimary(string name)
    {
        try
        {
            return new SingleInstance(new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly));
        }
        catch (IOException) { return null; }               // the name is taken: another instance is running
        catch (UnauthorizedAccessException) { return null; }
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
            finally { try { if (_server.IsConnected) _server.Disconnect(); } catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { } }
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

    public void Dispose() { _cts.Cancel(); _server.Dispose(); _cts.Dispose(); }
}
