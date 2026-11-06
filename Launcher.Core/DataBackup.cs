using System;
using System.IO;
using System.Linq;

namespace Cyanlabs.Launcher.Core
{
    /// <summary>Copies the user's settings and profiles aside before a major-version jump (v2 to v3), so a bad upgrade can be undone by hand.</summary>
    public static class DataBackup
    {
        private static readonly string[] Skipped = { "Logs", "Backups" };

        /// <returns>The backup folder, or null when there was nothing to back up.</returns>
        public static string Create(string backupRoot, string label, params string[] sourceDirs)
        {
            var existing = sourceDirs.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).ToList();
            if (existing.Count == 0) return null;
            var safeLabel = new string(label.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_').ToArray());
            var dest = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-before-" + safeLabel);
            for (var i = 0; i < existing.Count; i++)
                Copy(existing[i], Path.Combine(dest, "data" + (i + 1)));
            Prune(backupRoot, 3);
            return dest;
        }

        private static void Copy(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.GetFiles(source))
            {
                try { File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true); } catch { /* a locked file must not stop the update */ }
            }
            foreach (var dir in Directory.GetDirectories(source))
            {
                var name = Path.GetFileName(dir);
                if (Skipped.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                Copy(dir, Path.Combine(dest, name));
            }
        }

        private static void Prune(string root, int keep)
        {
            try
            {
                foreach (var old in Directory.GetDirectories(root).OrderByDescending(d => d, StringComparer.Ordinal).Skip(keep))
                    Directory.Delete(old, true);
            }
            catch { /* housekeeping only */ }
        }
    }
}
