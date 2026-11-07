using System.Diagnostics;
using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;

namespace Cyanlabs.Syn3Updater.Core.Services.Updates;

public sealed class UpdateApplyException : Exception
{
    public UpdateApplyException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Updates a user-level Linux install (the tar.gz installed by its install.sh) in place. The archive has already been checked against the signed manifest. It is unpacked
/// safely, its own install.sh is run (so the launcher entry, icon and link handler are refreshed exactly as a fresh install would), and the result is checked.
/// If anything goes wrong the previous program is put back.
/// </summary>
public static class LinuxTarApplier
{
    public const string ProgramName = "Syn3Updater";
    public const string PreviousSuffix = ".previous";
    private const long MaxUnpacked = 1L << 30;           // 1 GiB
    private const int MaxEntries = 200;

    /// <param name="archive">The verified Syn3Updater-&lt;version&gt;-linux-&lt;arch&gt;.tar.gz.</param>
    /// <param name="install">The detected install: only a <see cref="InstallKind.LinuxTar"/> one that can update in place is accepted.</param>
    /// <param name="workDir">A scratch folder (created, and removed again).</param>
    /// <param name="dataHome">What install.sh will use as XDG_DATA_HOME (null = the environment's, else ~/.local/share).</param>
    /// <param name="extraEnv">Environment variables for install.sh on top of this process's (tests use it to keep a run inside a temporary home).</param>
    /// <returns>The path of the program that was installed.</returns>
    public static async Task<string> ApplyAsync(string archive, InstallInfo install, string workDir, string? dataHome = null, TimeSpan? timeout = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        if (install is not { Kind: InstallKind.LinuxTar, Route: UpdateRoute.InPlace }) throw new UpdateApplyException("this copy is not an install.sh install that can update itself");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = dataHome ?? Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dest = Path.Combine(xdg is { Length: > 0 } ? xdg : Path.Combine(home, ".local", "share"), "syn3updater");
        // install.sh decides for itself where to install; only run it if that is exactly where this copy lives
        if (!string.Equals(Path.GetFullPath(dest).TrimEnd('/'), Path.GetFullPath(install.AppDir).TrimEnd('/'), StringComparison.Ordinal))
            throw new UpdateApplyException($"install.sh would install to {dest}, not to where this copy runs ({install.AppDir}); not touching either");

        var stage = Path.Combine(workDir, "unpacked");
        if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
        Directory.CreateDirectory(stage);
        var exe = Path.Combine(dest, ProgramName); var backup = exe + PreviousSuffix;
        var haveBackup = false;
        try
        {
            var root = Unpack(archive, stage);
            var script = Path.Combine(root, "install.sh"); var program = Path.Combine(root, ProgramName);
            if (!File.Exists(script) || !File.Exists(program)) throw new UpdateApplyException("the archive does not contain install.sh and the program");
            var expected = await HashAsync(program, ct);

            if (File.Exists(exe)) { File.Copy(exe, backup, overwrite: true); haveBackup = true; }
            var (code, output) = await RunAsync(script, root, xdg, extraEnv, timeout ?? TimeSpan.FromSeconds(120), ct);
            if (code != 0) throw new UpdateApplyException($"install.sh failed ({code}): {Tail(output)}");
            if (!File.Exists(exe) || await HashAsync(exe, ct) != expected) throw new UpdateApplyException("the installed program is not the one that was downloaded");
            return exe;
        }
        catch
        {
            if (haveBackup && File.Exists(backup)) { try { File.Move(backup, exe, overwrite: true); } catch (IOException) { } }
            throw;
        }
        finally { try { Directory.Delete(workDir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    /// <summary>Removes the copy kept for rollback, once the new version has started fine.</summary>
    public static void CleanUpPrevious(string appDir)
    {
        try { File.Delete(Path.Combine(appDir, ProgramName + PreviousSuffix)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Unpacks the archive into <paramref name="stage"/>; returns the folder holding install.sh. Refuses anything but plain files and folders that stay inside.</summary>
    internal static string Unpack(string archive, string stage)
    {
        var full = Path.GetFullPath(stage) + Path.DirectorySeparatorChar;
        long total = 0; var count = 0;
        using var gz = new GZipInputStream(File.OpenRead(archive));
        using var tar = new TarInputStream(gz, System.Text.Encoding.UTF8);
        for (var e = tar.GetNextEntry(); e != null; e = tar.GetNextEntry())
        {
            if (++count > MaxEntries) throw new UpdateApplyException("the archive has too many entries");
            var name = e.Name.Replace('\\', '/');
            if (name.StartsWith('/') || name.Split('/').Contains("..")) throw new UpdateApplyException($"unsafe path in the archive: {e.Name}");
            var target = Path.GetFullPath(Path.Combine(stage, name));
            if (!(target + Path.DirectorySeparatorChar).StartsWith(full, StringComparison.Ordinal)) throw new UpdateApplyException($"unsafe path in the archive: {e.Name}");
            if (e.IsDirectory) { Directory.CreateDirectory(target); continue; }
            if (e.TarHeader.TypeFlag is not (TarHeader.LF_NORMAL or TarHeader.LF_OLDNORM)) throw new UpdateApplyException($"the archive contains something other than files and folders: {e.Name}");
            total += e.Size;
            if (total > MaxUnpacked) throw new UpdateApplyException("the archive unpacks to more than expected");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var o = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) tar.CopyEntryContents(o);
            // keep only "executable or not": never setuid/setgid or group/other write
            File.SetUnixFileMode(target, (e.TarHeader.Mode & 0x40) != 0 ? (UnixFileMode)0b111_101_101 : (UnixFileMode)0b110_100_100);
        }
        var script = Directory.EnumerateFiles(stage, "install.sh", SearchOption.AllDirectories).OrderBy(p => p.Length).FirstOrDefault();
        return script == null ? stage : Path.GetDirectoryName(script)!;
    }

    private static async Task<(int Code, string Output)> RunAsync(string script, string cwd, string? xdg, IReadOnlyDictionary<string, string>? extraEnv, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("bash") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        psi.ArgumentList.Add(script);
        if (!string.IsNullOrEmpty(xdg)) psi.Environment["XDG_DATA_HOME"] = xdg;
        if (extraEnv != null) foreach (var kv in extraEnv) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi) ?? throw new UpdateApplyException("could not start bash");
        p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync(ct); var errTask = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } if (ct.IsCancellationRequested) throw; throw new UpdateApplyException("install.sh took too long"); }
        return (p.ExitCode, await outTask + await errTask);
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
    }

    private static string Tail(string s) => (s.Length > 400 ? s[^400..] : s).Trim();
}
