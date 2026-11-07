using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cyanlabs.Syn3Updater.Core.Services.Updates.Swap;

namespace Cyanlabs.Syn3Updater.Core.Services.Updates;

/// <summary>What the updater helper is asked to do. Travels on the helper's command line (base64 JSON), never in a file another program could edit.</summary>
public sealed record ApplyRequest(string InstallDir, string ZipPath, string Sha256, long Size, string Tag, string Version, UpdateChannel Channel,
    int ParentPid, string RestartExe, string ResultFile, string? LauncherPrefsPath)
{
    public string Encode()
    {
        var o = new JsonObject
        {
            ["installDir"] = InstallDir, ["zip"] = ZipPath, ["sha256"] = Sha256, ["size"] = Size, ["tag"] = Tag, ["version"] = Version,
            ["channel"] = Channel.ToString(), ["parentPid"] = ParentPid, ["restartExe"] = RestartExe, ["resultFile"] = ResultFile, ["launcherPrefs"] = LauncherPrefsPath
        };
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(o.ToJsonString()));
    }

    /// <summary>Reads and checks a request. Anything odd is refused: this runs elevated, so nothing is taken on trust.</summary>
    public static ApplyRequest Decode(string text)
    {
        try
        {
            if (JsonNode.Parse(Convert.FromBase64String(text)) is not JsonObject o) throw new FormatException("not an object");
            string S(string k) => o[k]?.GetValue<string>() ?? throw new FormatException($"missing {k}");
            var r = new ApplyRequest(S("installDir"), S("zip"), S("sha256").ToLowerInvariant(), o["size"]?.GetValue<long>() ?? throw new FormatException("missing size"), S("tag"), S("version"),
                Enum.TryParse<UpdateChannel>(S("channel"), false, out var ch) && Enum.IsDefined(ch) ? ch : throw new FormatException("bad channel"),
                o["parentPid"]?.GetValue<int>() ?? throw new FormatException("missing parentPid"), S("restartExe"), S("resultFile"), o["launcherPrefs"]?.GetValue<string>());
            if (!Path.IsPathRooted(r.InstallDir) || !Path.IsPathRooted(r.ZipPath) || !Path.IsPathRooted(r.RestartExe) || !Path.IsPathRooted(r.ResultFile)
                || (r.LauncherPrefsPath != null && !Path.IsPathRooted(r.LauncherPrefsPath))) throw new FormatException("paths must be absolute");
            if (r.Sha256.Length != 64 || !r.Sha256.All(Uri.IsHexDigit)) throw new FormatException("bad sha256");
            if (r.Size <= 0 || r.ParentPid < 0 || SemVer.Parse(r.Version) == null) throw new FormatException("bad size, pid or version");
            return r;
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException or ArgumentException) { throw new FormatException("the update request is not valid: " + e.Message, e); }
    }
}

/// <summary>What happened, written for the app to read at its next start (and shown to the user if the update failed).</summary>
public sealed record ApplyResult(bool Success, string Version, string Message, DateTimeOffset At)
{
    public void Write(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, new JsonObject { ["success"] = Success, ["version"] = Version, ["message"] = Message, ["at"] = At.ToString("o") }.ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* nothing more can be done */ }
    }

    public static ApplyResult? Read(string path)
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) return null;
            return new ApplyResult(o["success"]?.GetValue<bool>() ?? false, o["version"]?.GetValue<string>() ?? "", o["message"]?.GetValue<string>() ?? "",
                DateTimeOffset.TryParse(o["at"]?.GetValue<string>(), out var at) ? at : default);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { return null; }
    }
}

/// <summary>
/// The Windows half of an in-place update, run by the helper once the app has closed (a running program cannot replace itself). It re-checks the zip against the hash from the signed
/// manifest while holding the file so nobody can swap it in between, undoes any update that was interrupted earlier, replaces the files all-or-nothing, and records the install.
/// It never throws: the outcome is a result the caller reports and writes for the app.
/// </summary>
public static class WindowsApplier
{
    /// <param name="waitForExit">Waits until the process with this id has ended; false if it is still running after the time given. Default: really waits.</param>
    public static ApplyResult Run(ApplyRequest r, Func<int, TimeSpan, bool>? waitForExit = null, ISwapFileSystem? fs = null, RetryPolicy? retry = null, Action<string>? log = null)
    {
        log ??= _ => { };
        ApplyResult Fail(string why) { log("FAILED: " + why); return new ApplyResult(false, r.Version, why, DateTimeOffset.Now); }
        try
        {
            if (!File.Exists(Path.Combine(r.InstallDir, PackageStager.AppExe))) return Fail($"{r.InstallDir} does not contain {PackageStager.AppExe}");
            if (!File.Exists(r.ZipPath)) return Fail("the downloaded update is missing");

            if (r.ParentPid > 0)
            {
                log($"Waiting for Syn3 Updater (process {r.ParentPid}) to close");
                if (!(waitForExit ?? DefaultWait)(r.ParentPid, TimeSpan.FromSeconds(60))) return Fail("Syn3 Updater did not close, so nothing was changed. Close it and try again.");
            }

            // hold the file with writes denied from the check until the files are out of it, so what was verified is what is installed
            using var hold = new FileStream(r.ZipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (hold.Length != r.Size) return Fail("the downloaded update is not the size the signed manifest promised, so it was not installed");
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(hold)), r.Sha256, StringComparison.OrdinalIgnoreCase))
                return Fail("the downloaded update does not match its signed checksum, so it was not installed");
            log("Checksum verified");

            if (InstallTransaction.Recover(r.InstallDir, fs)) log("Rolled back an earlier interrupted update");
            var staging = Path.Combine(r.InstallDir, InstallTransaction.StagingDirName);
            try
            {
                var files = PackageStager.Extract(r.ZipPath, staging);
                var stale = StalePlanner.Compute(r.InstallDir, InstallManifest.Load(r.InstallDir), files);
                new InstallTransaction(r.InstallDir, fs, retry).Apply(staging, files, stale);
                try { new InstallManifest { Version = LauncherPrefs.NumericTag(r.Tag) ?? r.Version, Files = files.ToList() }.Save(r.InstallDir); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log("Could not save the install manifest: " + e.Message); }
            }
            finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }

            if (LauncherPrefs.WriteInstalled(r.LauncherPrefsPath, r.Tag, r.Channel, r.InstallDir)) log("Recorded the install for the Launcher");
            log($"Installed {r.Version}");
            return new ApplyResult(true, r.Version, "Installed " + r.Version, DateTimeOffset.Now);
        }
        catch (Exception e) when (e is InstallException or PackageException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Fail(e.Message);
        }
    }

    private static bool DefaultWait(int pid, TimeSpan timeout)
    {
        try { using var p = Process.GetProcessById(pid); return p.WaitForExit(timeout); }
        catch (ArgumentException) { return true; }                // no such process: it has already gone
        catch (InvalidOperationException) { return true; }
    }
}
