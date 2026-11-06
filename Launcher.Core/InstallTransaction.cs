using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Cyanlabs.Launcher.Updating
{
    public sealed class InstallException : Exception
    {
        public InstallException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    ///     Applies a staged update to the install folder as one all-or-nothing step.
    ///     Every file that is replaced or removed is first <em>moved</em> (renamed, on the same volume) into a backup folder, which also works for
    ///     the running launcher itself. If anything fails, the new files are removed and the originals moved back, so the install is never left half-updated.
    ///     The old launcher swallowed copy errors and recorded the update as installed anyway.
    /// </summary>
    public sealed class InstallTransaction
    {
        public const string BackupDirName = ".update-backup";
        public const string StagingDirName = ".update-staging";
        private const string CompleteMarker = "COMPLETE";

        private readonly string _install;
        private readonly string _backup;
        private readonly IFileSystem _fs;
        private readonly RetryPolicy _retry;

        public InstallTransaction(string installDir, IFileSystem fs = null, RetryPolicy retry = null)
        {
            _install = installDir;
            _backup = Path.Combine(installDir, BackupDirName);
            _fs = fs ?? new RealFileSystem();
            _retry = retry ?? RetryPolicy.Default;
        }

        /// <param name="stagingDir">Where the new files were unpacked.</param>
        /// <param name="newFiles">Relative paths of the files to install.</param>
        /// <param name="staleFiles">Relative paths of old files that should be removed (they go to the backup first, so a failure restores them).</param>
        public void Apply(string stagingDir, IReadOnlyList<string> newFiles, IReadOnlyList<string> staleFiles)
        {
            if (_fs.DirectoryExists(_backup)) _retry.Run(() => _fs.DeleteDirectory(_backup));
            _fs.CreateDirectory(_backup);

            var backedUp = new List<string>();
            var placed = new List<string>();
            try
            {
                foreach (var rel in staleFiles)
                    if (_fs.FileExists(Full(_install, rel))) Backup(rel, backedUp);

                foreach (var rel in newFiles)
                {
                    var dest = Full(_install, rel);
                    if (_fs.FileExists(dest)) Backup(rel, backedUp);
                    _fs.CreateDirectory(Path.GetDirectoryName(dest));
                    var staged = Full(stagingDir, rel);
                    _retry.Run(() => _fs.MoveFile(staged, dest, false));
                    placed.Add(rel);
                }
            }
            catch (Exception e)
            {
                var restored = Rollback(placed, backedUp);
                throw new InstallException(restored
                    ? "The update could not be applied (" + e.Message + "). Your previous version was restored."
                    : "The update could not be applied (" + e.Message + ") and some files could not be restored. Reinstall Syn3 Updater if it does not start.", e);
            }

            // Remove folders the removed files leave empty (the old launcher deleted the whole Languages folder, for instance).
            foreach (var rel in staleFiles) PruneEmptyParents(rel);

            // Everything is in place: mark the backup as no longer needed. Deleting it may fail while the old launcher is still running; that is cleaned up on the next start.
            try { _fs.CreateDirectory(_backup); File.WriteAllText(Path.Combine(_backup, CompleteMarker), DateTime.UtcNow.ToString("o")); } catch { /* best effort */ }
            try { if (_fs.DirectoryExists(stagingDir)) _fs.DeleteDirectory(stagingDir); } catch { /* best effort */ }
            try { _fs.DeleteDirectory(_backup); } catch { /* the running launcher is in there; removed by Recover() next time */ }
        }

        /// <summary>
        ///     Call at every start. A backup folder without the COMPLETE marker means an update was interrupted (power cut, crash):
        ///     the originals are moved back. With the marker it is just leftover and is deleted.
        /// </summary>
        /// <returns>True when an interrupted update was rolled back.</returns>
        public static bool Recover(string installDir, IFileSystem fs = null)
        {
            fs = fs ?? new RealFileSystem();
            var backup = Path.Combine(installDir, BackupDirName);
            var staging = Path.Combine(installDir, StagingDirName);
            var rolledBack = false;
            if (fs.DirectoryExists(backup))
            {
                if (!File.Exists(Path.Combine(backup, CompleteMarker)))
                {
                    foreach (var rel in PackageStager.ListFiles(backup))
                    {
                        var dest = Full(installDir, rel);
                        fs.CreateDirectory(Path.GetDirectoryName(dest));
                        fs.MoveFile(Full(backup, rel), dest, true);
                    }
                    rolledBack = true;
                }
                try { fs.DeleteDirectory(backup); } catch { /* try again next start */ }
            }
            try { if (fs.DirectoryExists(staging)) fs.DeleteDirectory(staging); } catch { /* ignore */ }
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
            catch { /* tidying only */ }
        }

        private void Backup(string rel, List<string> backedUp)
        {
            var dst = Full(_backup, rel);
            _fs.CreateDirectory(Path.GetDirectoryName(dst));
            var src = Full(_install, rel);
            _retry.Run(() => _fs.MoveFile(src, dst, true));
            backedUp.Add(rel);
        }

        private bool Rollback(List<string> placed, List<string> backedUp)
        {
            var ok = true;
            foreach (var rel in Enumerable.Reverse(placed))
            {
                try { var p = Full(_install, rel); if (_fs.FileExists(p)) _retry.Run(() => _fs.DeleteFile(p)); }
                catch { ok = false; }
            }
            foreach (var rel in backedUp)
            {
                try
                {
                    var dest = Full(_install, rel);
                    _fs.CreateDirectory(Path.GetDirectoryName(dest));
                    _retry.Run(() => _fs.MoveFile(Full(_backup, rel), dest, true));
                }
                catch { ok = false; }
            }
            if (ok) { try { _fs.DeleteDirectory(_backup); } catch { /* ignore */ } }
            return ok;
        }

        private static string Full(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
    }
}
