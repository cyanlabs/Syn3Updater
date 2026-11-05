using System.Diagnostics;

namespace Cyanlabs.Syn3Updater.Core.Drives;

public record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

public static class ProcessRunner
{
    /// <summary>Runs a program without a shell (arguments are passed as a list, never interpolated).</summary>
    public static async Task<ProcessResult> RunAsync(string file, IEnumerable<string> args, string? stdin = null, CancellationToken ct = default, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = stdin != null, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi) ?? throw new IOException($"Could not start {file}");
            if (stdin != null) { await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close(); }
            var o = p.StandardOutput.ReadToEndAsync(ct);
            var e = p.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } throw; }
            return new(p.ExitCode, await o, await e);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new(127, "", $"{file}: {ex.Message}");
        }
    }

    public static bool Exists(string program)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
        return path.Split(Path.PathSeparator).Any(d => exts.Any(e => File.Exists(Path.Combine(d, program + e))));
    }
}
