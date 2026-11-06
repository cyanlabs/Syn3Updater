using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace Cyanlabs.Launcher.Core
{
    /// <summary>Remembers which files the launcher installed, so the next update can remove exactly the ones the new version no longer ships.</summary>
    public sealed class InstallManifest
    {
        public const string FileName = "install-manifest.json";

        public string Version { get; set; } = "";
        public List<string> Files { get; set; } = new List<string>();

        public static InstallManifest Load(string installDir)
        {
            try
            {
                var path = Path.Combine(installDir, FileName);
                return File.Exists(path) ? JsonConvert.DeserializeObject<InstallManifest>(File.ReadAllText(path)) : null;
            }
            catch { return null; }
        }

        public void Save(string installDir) => File.WriteAllText(Path.Combine(installDir, FileName), JsonConvert.SerializeObject(this, Formatting.Indented));
    }

    public static class StalePlanner
    {
        /// <summary>
        ///     Files in the install folder that the new version does not ship and that should therefore go.
        ///     With a manifest: exactly the previously installed files that are no longer part of the package.
        ///     Without one (an install made by the old v2 launcher): the old launcher's own rule, every .dll and the Languages folder.
        ///     Files the launcher never installed (user files, the app's own data) are never listed.
        /// </summary>
        public static IReadOnlyList<string> Compute(string installDir, InstallManifest previous, IEnumerable<string> newFiles)
        {
            var keep = new HashSet<string>(newFiles, StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> candidates;
            if (previous != null && previous.Files.Count > 0)
            {
                candidates = previous.Files;
            }
            else
            {
                candidates = PackageStager.ListFiles(installDir)
                    .Where(f => !f.StartsWith(InstallTransaction.BackupDirName + "/", StringComparison.OrdinalIgnoreCase)
                                && !f.StartsWith(InstallTransaction.StagingDirName + "/", StringComparison.OrdinalIgnoreCase))
                    .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.StartsWith("Languages/", StringComparison.OrdinalIgnoreCase));
            }
            return candidates.Where(f => !keep.Contains(f) && File.Exists(Path.Combine(installDir, f.Replace('/', Path.DirectorySeparatorChar)))).ToList();
        }
    }
}
