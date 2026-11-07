namespace Cyanlabs.Syn3Updater.Core.Services.Updates.Swap;

/// <summary>The file operations an install performs, behind an interface so tests can make any of them fail and prove the rollback.</summary>
public interface ISwapFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    /// <summary>Moves a file, replacing the destination when <paramref name="overwrite"/> is set.</summary>
    void MoveFile(string source, string destination, bool overwrite);
    void DeleteFile(string path);
    void DeleteDirectory(string path);
    void WriteAllText(string path, string text);
}

public sealed class RealSwapFileSystem : ISwapFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void MoveFile(string source, string destination, bool overwrite)
    {
        if (overwrite && File.Exists(destination))
        {
            try { File.Delete(destination); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a running program cannot be deleted but can be renamed: put it aside (the next start removes it) instead of failing
                File.Move(destination, destination + "." + Guid.NewGuid().ToString("N") + DisplacedSuffix);
            }
        }
        File.Move(source, destination);
    }
    public void DeleteFile(string path) => File.Delete(path);
    public void DeleteDirectory(string path) => Directory.Delete(path, true);
    public void WriteAllText(string path, string text) => File.WriteAllText(path, text);

    /// <summary>Ends the name of a file that was in the way (a running program) and was renamed aside; see <see cref="IsDisplaced"/>.</summary>
    public const string DisplacedSuffix = ".old";
    private static readonly System.Text.RegularExpressions.Regex DisplacedName = new(@"\.[0-9a-f]{32}\.old$", System.Text.RegularExpressions.RegexOptions.Compiled);
    public static bool IsDisplaced(string path) => DisplacedName.IsMatch(path);
}

/// <summary>Files can be briefly locked (antivirus scans, an app still closing). Those are retried before giving up.</summary>
public sealed class RetryPolicy
{
    public static readonly RetryPolicy Default = new(10, 500);
    public int Attempts { get; }
    public int DelayMs { get; }
    public RetryPolicy(int attempts, int delayMs) { Attempts = Math.Max(1, attempts); DelayMs = Math.Max(0, delayMs); }

    public void Run(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < Attempts)
            {
                if (DelayMs > 0) Thread.Sleep(DelayMs);
            }
        }
    }
}

public sealed class InstallException : Exception
{
    public InstallException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class PackageException : Exception
{
    public PackageException(string message) : base(message) { }
}

/// <summary>Relative paths read from a package or from install-manifest.json are never trusted: an elevated updater must not touch anything outside the install folder.</summary>
public static class SafePath
{
    public static bool IsSafeRelative(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return false;
        var p = rel.Replace('\\', '/');
        return !p.StartsWith('/') && !p.Contains(':') && !p.Split('/').Any(s => s == ".." || s.Length == 0 && p != "");
    }

    public static string Require(string rel) => IsSafeRelative(rel) ? rel : throw new InstallException($"unsafe path in the update: {rel}");
}
