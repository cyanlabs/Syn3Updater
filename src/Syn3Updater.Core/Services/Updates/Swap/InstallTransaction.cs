namespace Cyanlabs.Syn3Updater.Core.Services.Updates.Swap;

/// <summary>
/// Applies a staged update to the install folder as one all-or-nothing step, with the same on-disk protocol as the Launcher's engine
/// (.update-staging, .update-backup, a COMPLETE marker), so either can finish or undo the other's interrupted update.
/// Every file that is replaced or removed is first MOVED (renamed, on the same volume) into a backup folder, which also works for a running program.
/// If anything fails, the new files are removed and the originals moved back, so the install is never left half-updated.
/// </summary>
public sealed class InstallTransaction
{
    public const string BackupDirName = ".update-backup";
    public const string StagingDirName = ".update-staging";
    private const string CompleteMarker = "COMPLETE";

    private readonly string _install, _backup;
    private readonly ISwapFileSystem _fs;
    private readonly RetryPolicy _retry;

    public InstallTransaction(string installDir, ISwapFileSystem? fs = null, RetryPolicy? retry = null)
    {
        _install = installDir; _backup = Path.Combine(installDir, BackupDirName);
        _fs = fs ?? new RealSwapFileSystem(); _retry = retry ?? RetryPolicy.Default;
    }

    /// <param name="stagingDir">Where the new files were unpacked.</param>
    /// <param name="newFiles">Relative paths of the files to install.</param>
    /// <param name="staleFiles">Relative paths of old files to remove (they go to the backup first, so a failure restores them).</param>
    public void Apply(string stagingDir, IReadOnlyList<string> newFiles, IReadOnlyList<string> staleFiles)
    {
        foreach (var f in newFiles) SafePath.Require(f);
        foreach (var f in staleFiles) SafePath.Require(f);
        if (_fs.DirectoryExists(_backup)) _retry.Run(() => _fs.DeleteDirectory(_backup));
        _fs.CreateDirectory(_backup);

        var backedUp = new List<string>(); var placed = new List<string>();
        try
        {
            foreach (var rel in staleFiles) if (_fs.FileExists(Full(_install, rel))) Backup(rel, backedUp);
            foreach (var rel in newFiles)
            {
                var dest = Full(_install, rel);
                if (_fs.FileExists(dest)) Backup(rel, backedUp);
                _fs.CreateDirectory(Path.GetDirectoryName(dest)!);
                var staged = Full(stagingDir, rel);
                _retry.Run(() => _fs.MoveFile(staged, dest, false));
                placed.Add(rel);
            }
        }
        catch (Exception e)
        {
            var restored = Rollback(placed, backedUp);
            throw new InstallException(restored
                ? $"The update could not be applied ({e.Message}). Your previous version was restored."
                : $"The update could not be applied ({e.Message}) and some files could not be restored. Reinstall Syn3 Updater if it does not start.", e);
        }

        foreach (var rel in staleFiles) PruneEmptyParents(rel);       // folders the removed files leave empty

        // everything is in place: the backup is no longer needed. Deleting it can fail while an old copy is still running; Recover() cleans up next time.
        try { _fs.CreateDirectory(_backup); File.WriteAllText(Path.Combine(_backup, CompleteMarker), DateTime.UtcNow.ToString("o")); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        try { if (_fs.DirectoryExists(stagingDir)) _fs.DeleteDirectory(stagingDir); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        TryRemoveBackup(_backup, _fs, new RetryPolicy(1, 0));                  // the old program is usually still running from in there: whatever cannot go yet is left, with the marker, for the next start
    }

    /// <summary>
    /// Removes the backup folder of a COMPLETED update as far as it can go, and NEVER removes the COMPLETE marker until everything else is gone. Deleting the folder in one go (as the Launcher
    /// did) is not safe on Windows: while the old program still runs from the backup, the delete removes the marker first (it sorts first) and then fails on the locked exe, and the next start
    /// takes the marker-less folder for an interrupted update and rolls the NEW version back to the old one.
    /// </summary>
    /// <returns>True when the folder is gone.</returns>
    private static bool TryRemoveBackup(string backup, ISwapFileSystem fs, RetryPolicy retry)
    {
        try
        {
            if (!fs.DirectoryExists(backup)) return true;
            var marker = Path.Combine(backup, CompleteMarker);
            foreach (var f in Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories).ToList())
                if (!string.Equals(f, marker, StringComparison.OrdinalIgnoreCase))
                    try { retry.Run(() => fs.DeleteFile(f)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            foreach (var d in Directory.EnumerateDirectories(backup, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length).ToList())
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) fs.DeleteDirectory(d); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (Directory.EnumerateFileSystemEntries(backup).Any(e => !string.Equals(e, marker, StringComparison.OrdinalIgnoreCase))) return false;      // something is still in use: keep the marker
            if (fs.FileExists(marker)) fs.DeleteFile(marker);
            fs.DeleteDirectory(backup);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Call at every start. A backup folder without the COMPLETE marker means an update was interrupted (power cut, crash): the originals are moved back.
    /// With the marker it is just leftover and is deleted.
    /// </summary>
    /// <returns>True when an interrupted update was rolled back.</returns>
    /// <param name="retry">How long to keep trying to delete the leftover backup: the old program of a finished update is usually still exiting at the first start, and Windows keeps its exe locked a moment longer.</param>
    public static bool Recover(string installDir, ISwapFileSystem? fs = null, RetryPolicy? retry = null)
    {
        fs ??= new RealSwapFileSystem(); retry ??= new RetryPolicy(8, 500);
        var backup = Path.Combine(installDir, BackupDirName); var staging = Path.Combine(installDir, StagingDirName);
        var rolledBack = false;
        if (fs.DirectoryExists(backup))
        {
            if (!File.Exists(Path.Combine(backup, CompleteMarker)))
            {
                foreach (var rel in PackageStager.ListFiles(backup))
                {
                    if (!SafePath.IsSafeRelative(rel)) continue;
                    var dest = Full(installDir, rel);
                    fs.CreateDirectory(Path.GetDirectoryName(dest)!);
                    fs.MoveFile(Full(backup, rel), dest, true);
                }
                rolledBack = true;
                try { retry.Run(() => fs.DeleteDirectory(backup)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            else TryRemoveBackup(backup, fs, retry);                          // a finished update: just the old program's leftovers, never the marker before the rest
        }
        try { if (fs.DirectoryExists(staging)) fs.DeleteDirectory(staging); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return rolledBack;
    }

    private void PruneEmptyParents(string rel)
    {
        try
        {
            var dir = Path.GetDirectoryName(Full(_install, rel));
            var root = Path.GetFullPath(_install).TrimEnd(Path.DirectorySeparatorChar);
            while (!string.IsNullOrEmpty(dir) && Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar).Length > root.Length
                   && _fs.DirectoryExists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                _fs.DeleteDirectory(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* tidying only */ }
    }

    private void Backup(string rel, List<string> backedUp)
    {
        var dst = Full(_backup, rel);
        _fs.CreateDirectory(Path.GetDirectoryName(dst)!);
        var src = Full(_install, rel);
        _retry.Run(() => _fs.MoveFile(src, dst, true));
        backedUp.Add(rel);
    }

    private bool Rollback(List<string> placed, List<string> backedUp)
    {
        var ok = true;
        foreach (var rel in Enumerable.Reverse(placed))
            try { var p = Full(_install, rel); if (_fs.FileExists(p)) _retry.Run(() => _fs.DeleteFile(p)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ok = false; }
        foreach (var rel in backedUp)
            try
            {
                var dest = Full(_install, rel);
                _fs.CreateDirectory(Path.GetDirectoryName(dest)!);
                _retry.Run(() => _fs.MoveFile(Full(_backup, rel), dest, true));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ok = false; }
        if (ok) { try { _fs.DeleteDirectory(_backup); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
        return ok;
    }

    private static string Full(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
}
