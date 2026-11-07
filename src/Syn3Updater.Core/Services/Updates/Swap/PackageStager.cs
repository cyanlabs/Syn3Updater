using System.IO.Compression;

namespace Cyanlabs.Syn3Updater.Core.Services.Updates.Swap;

/// <summary>Unpacks a release zip into a scratch folder so it can be checked before anything in the real install is touched.</summary>
public static class PackageStager
{
    public const string AppExe = "Syn3Updater.exe";
    private const long MaxUnpacked = 1L << 30;           // 1 GiB
    private const int MaxEntries = 2000;

    /// <returns>Relative paths (forward slashes) of every file now in the staging folder.</returns>
    public static IReadOnlyList<string> Extract(string zipPath, string stagingDir)
    {
        if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
        Directory.CreateDirectory(stagingDir);
        var root = Path.GetFullPath(stagingDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            if (zip.Entries.Count > MaxEntries) throw new PackageException("The update package has too many files.");
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.Length == 0 || name.EndsWith('/')) continue;
                if (name.StartsWith('/') || name.Split('/').Any(p => p == "..") || name.Contains(':'))
                    throw new PackageException("The update package contains an unsafe path: " + entry.FullName);
                total += entry.Length;
                if (total > MaxUnpacked) throw new PackageException("The update package unpacks to more than expected.");
                var dest = Path.GetFullPath(Path.Combine(stagingDir, name.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(root, cmp)) throw new PackageException("The update package contains an unsafe path: " + entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, true);
            }
        }

        Flatten(stagingDir);
        var files = ListFiles(stagingDir);
        if (!files.Contains(AppExe, StringComparer.OrdinalIgnoreCase)) throw new PackageException("The update package does not contain " + AppExe + ".");
        return files;
    }

    /// <summary>v3 zips put everything in one top-level folder; the install wants the files at the root.</summary>
    private static void Flatten(string stagingDir)
    {
        var entries = Directory.GetFileSystemEntries(stagingDir);
        if (entries.Length != 1 || !Directory.Exists(entries[0])) return;
        var holder = Path.Combine(stagingDir, ".flatten-" + Guid.NewGuid().ToString("N"));
        Directory.Move(entries[0], holder);
        foreach (var child in Directory.GetFileSystemEntries(holder))
        {
            var target = Path.Combine(stagingDir, Path.GetFileName(child));
            if (Directory.Exists(child)) Directory.Move(child, target); else File.Move(child, target);
        }
        Directory.Delete(holder, true);
    }

    public static IReadOnlyList<string> ListFiles(string dir)
    {
        var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetFullPath(f)[root.Length..].Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
