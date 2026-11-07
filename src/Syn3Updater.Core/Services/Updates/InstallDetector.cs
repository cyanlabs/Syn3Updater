namespace Cyanlabs.Syn3Updater.Core.Services.Updates;

/// <summary>How this copy of the program got onto the machine.</summary>
public enum InstallKind
{
    /// <summary>Not an install at all: run through <c>dotnet</c>, or built from source.</summary>
    Development,
    /// <summary>A Windows folder (the zip, with the Launcher beside the program).</summary>
    WindowsFolder,
    /// <summary>Syn3Updater.exe on its own in some folder (copied to the Desktop or Downloads, say): nothing installed it, so the app must not treat the folder as its own and rewrite it.</summary>
    WindowsPortable,
    /// <summary>Linux tar.gz installed by its install.sh, in the user's own data folder.</summary>
    LinuxTar,
    LinuxDeb,
    LinuxRpm,
    /// <summary>The Arch package or the AUR package: both are pacman packages.</summary>
    LinuxPacman,
    /// <summary>Under /opt or /usr but with no marker saying which package manager owns it.</summary>
    LinuxSystemUnknown,
    /// <summary>Linux: run from a folder nothing installed (an extracted archive, a download folder).</summary>
    LinuxPortable,
    MacApp,
    Unknown
}

/// <summary>How an update reaches this install.</summary>
public enum UpdateRoute
{
    /// <summary>The app replaces its own files.</summary>
    InPlace,
    /// <summary>The user updates it themselves (a package manager command, or a download); the app explains how.</summary>
    Manual,
    /// <summary>Never updated by the app (development builds).</summary>
    None
}

public sealed record InstallInfo(InstallKind Kind, string AppDir, UpdateRoute Route, string Reason)
{
    /// <summary>One English line for logs, the Diagnostics page and bug reports: "LinuxTar: installed by install.sh in your own folder (updates: InPlace)".</summary>
    public string Describe() => $"{Kind}: {Reason} (updates: {Route})";

    public bool CanSelfUpdate => Route == UpdateRoute.InPlace;
}

/// <summary>
/// Works out how the running copy was installed, and therefore whether the app may update it in place. The rule that matters: files owned by a package
/// manager (deb, rpm, pacman) are never rewritten by the app, since that would leave the package database wrong. When in doubt the answer is Manual.
/// </summary>
public static class InstallDetector
{
    /// <summary>The file packages and install.sh drop next to the program naming how it was installed.</summary>
    public const string MarkerFile = ".install-method";

    /// <param name="os">The OS the program runs on.</param>
    /// <param name="appDir">The folder holding the running program (the real one: symlinks already followed).</param>
    /// <param name="processPath">The running executable, used to tell a <c>dotnet</c> development run from an install.</param>
    /// <param name="home">The user's home folder (Linux user-install detection).</param>
    /// <param name="dataHome">XDG data home; null = <c>~/.local/share</c>.</param>
    /// <param name="macInPlaceSupported">macOS in-place updates need a Developer ID signed build; false until releases are signed.</param>
    public static InstallInfo Detect(UpdateOs os, string appDir, string? processPath, string? home, string? dataHome = null, bool macInPlaceSupported = false)
    {
        if (processPath != null && Path.GetFileNameWithoutExtension(processPath.Replace('\\', '/')).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return new(InstallKind.Development, appDir, UpdateRoute.None, "running through the dotnet host, not an installed copy");
        if (IsBuildOutput(appDir)) return new(InstallKind.Development, appDir, UpdateRoute.None, "running from a build output folder (dotnet run, an IDE), not an installed copy");
        return os switch
        {
            UpdateOs.Windows => Windows(appDir),
            UpdateOs.MacOs => Mac(appDir, macInPlaceSupported),
            _ => Linux(appDir, home, dataHome)
        };
    }

    /// <summary>Detects the running copy.</summary>
    public static InstallInfo DetectCurrent()
    {
        var exe = Environment.ProcessPath;
        var dir = exe != null ? Path.GetDirectoryName(ResolveLinks(exe)) ?? AppContext.BaseDirectory : AppContext.BaseDirectory;
        return Detect(ReleaseManifest.CurrentOs(), dir, exe, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable("XDG_DATA_HOME"));
    }

    private static readonly System.Text.RegularExpressions.Regex BuildFolder = new(@"[/\\]bin[/\\](Debug|Release)[/\\]net\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>bin/Debug|Release/net*/ with the program's own .dll beside the apphost: what <c>dotnet run</c> and an IDE start. (A published release is one file, or an app bundle outside bin/.)</summary>
    private static bool IsBuildOutput(string dir) => BuildFolder.IsMatch(dir) && File.Exists(Path.Combine(dir, "Syn3Updater.dll"));

    /// <summary>
    /// The Windows zip, or a folder an older Launcher installed into. Whether the app may rewrite the folder needs EVIDENCE that it is an install: the zip carries a marker, an update leaves
    /// an install-manifest.json, and the old Launcher put Launcher.exe beside the program. A lone Syn3Updater.exe copied somewhere has none, and must never have its folder rewritten.
    /// </summary>
    private static InstallInfo Windows(string dir)
    {
        var evidence = ReadMarker(dir) == "zip" || File.Exists(Path.Combine(dir, "install-manifest.json")) || File.Exists(Path.Combine(dir, "Launcher.exe"));
        if (!evidence) return new(InstallKind.WindowsPortable, dir, UpdateRoute.Manual, "run from a folder that nothing installed (a copied Syn3Updater.exe): download the new version");
        return IsWritable(dir) ? new(InstallKind.WindowsFolder, dir, UpdateRoute.InPlace, "a Windows folder install")
                               : new(InstallKind.WindowsFolder, dir, UpdateRoute.Manual, $"{dir} cannot be written to");
    }

    private static InstallInfo Mac(string dir, bool supported)
    {
        var p = dir.Replace('\\', '/');
        if (!p.Contains(".app/Contents/MacOS", StringComparison.OrdinalIgnoreCase))
            return new(InstallKind.Unknown, dir, UpdateRoute.Manual, "not running from an application bundle");
        if (p.Contains("/AppTranslocation/", StringComparison.OrdinalIgnoreCase))
            return new(InstallKind.MacApp, dir, UpdateRoute.Manual, "macOS is running the app from a temporary protected copy: move it to Applications first");
        if (p.StartsWith("/Volumes/", StringComparison.OrdinalIgnoreCase))
            return new(InstallKind.MacApp, dir, UpdateRoute.Manual, "running from the disk image: drag the app to Applications first");
        if (!supported) return new(InstallKind.MacApp, dir, UpdateRoute.Manual, "in-place updates on macOS need a signed and notarized build");
        var bundle = dir[..dir.LastIndexOf(".app/", StringComparison.OrdinalIgnoreCase)];
        return IsWritable(Path.GetDirectoryName(bundle + ".app") ?? dir) ? new(InstallKind.MacApp, dir, UpdateRoute.InPlace, "an application bundle")
                                                                          : new(InstallKind.MacApp, dir, UpdateRoute.Manual, "the app's folder cannot be written to");
    }

    private static InstallInfo Linux(string dir, string? home, string? dataHome)
    {
        var marker = ReadMarker(dir);
        switch (marker)
        {
            case "deb": return new(InstallKind.LinuxDeb, dir, UpdateRoute.Manual, "installed from a .deb package");
            case "rpm": return new(InstallKind.LinuxRpm, dir, UpdateRoute.Manual, "installed from an .rpm package");
            case "pacman": return new(InstallKind.LinuxPacman, dir, UpdateRoute.Manual, "installed from a pacman (Arch or AUR) package");
            case "tar": return UserInstall(dir);
            case not null: return new(InstallKind.LinuxSystemUnknown, dir, UpdateRoute.Manual, $"unknown install method '{marker}'");
        }
        // no marker: copies installed before markers existed, and plain archives
        var userDir = Path.Combine(dataHome is { Length: > 0 } ? dataHome : Path.Combine(home ?? "", ".local", "share"), "syn3updater");
        if (home != null && SamePath(dir, userDir)) return UserInstall(dir);
        if (Under(dir, "/opt") || Under(dir, "/usr") || Under(dir, "/app") || Under(dir, "/snap"))
            return new(InstallKind.LinuxSystemUnknown, dir, UpdateRoute.Manual, "installed system-wide by something other than install.sh");
        return new(InstallKind.LinuxPortable, dir, UpdateRoute.Manual, "run from a folder that nothing installed: download the new version or use install.sh");
    }

    private static InstallInfo UserInstall(string dir) =>
        IsWritable(dir) ? new(InstallKind.LinuxTar, dir, UpdateRoute.InPlace, "installed by install.sh in your own folder")
                        : new(InstallKind.LinuxTar, dir, UpdateRoute.Manual, $"{dir} cannot be written to");

    private static string? ReadMarker(string dir)
    {
        try
        {
            var f = Path.Combine(dir, MarkerFile);
            return File.Exists(f) ? File.ReadAllText(f).Trim().ToLowerInvariant() is { Length: > 0 } s ? s : null : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>True when a file can be created in <paramref name="dir"/> (a real attempt: permission bits and read-only mounts both lie).</summary>
    public static bool IsWritable(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            var probe = Path.Combine(dir, $".write-test-{Environment.ProcessId}-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool Under(string dir, string root) => dir.Replace('\\', '/').TrimEnd('/') is var d && (d == root || d.StartsWith(root + "/", StringComparison.Ordinal));
    private static bool SamePath(string a, string b) => string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string ResolveLinks(string path)
    {
        try { return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path; }
        catch (IOException) { return path; }
    }

    /// <summary>
    /// What to tell someone whose install the app will not touch: a <c>curl</c> line that downloads the package (when its address is known), then the install command
    /// (one line per distro family when they differ, each with a short "# note"), or, when there is no command, a sentence saying where to download.
    /// The address comes from the signed manifest, which only allows this repository's release downloads, so it is safe to put in a shell command.
    /// </summary>
    public static string ManualInstructions(InstallKind kind, string? fileName, string? url = null)
    {
        var download = url != null && fileName != null ? $"curl -LO {url}\n" : "";
        var file = fileName ?? "<downloaded package>";
        return kind switch
        {
            InstallKind.LinuxDeb => download + $"sudo apt install ./{fileName ?? "<downloaded .deb>"}",
            InstallKind.LinuxRpm => download + $"sudo dnf install ./{fileName ?? "<downloaded .rpm>"}          # Fedora, RHEL\nsudo zypper install ./{fileName ?? "<downloaded .rpm>"}          # openSUSE",
            InstallKind.LinuxPacman => download + $"sudo pacman -U {file}",
            // the disk image mounts as "Syn3 Updater" (scripts/package-macos.sh). curl does not mark the file as downloaded from the internet, so Gatekeeper does not block the copy
            InstallKind.MacApp when download.Length > 0 => download + $"hdiutil attach -nobrowse -quiet {fileName}\nditto \"/Volumes/Syn3 Updater/Syn3 Updater.app\" \"/Applications/Syn3 Updater.app\"\nhdiutil detach -quiet \"/Volumes/Syn3 Updater\"",
            _ => ReleasesPage
        };
    }

    /// <summary>Where new versions can be downloaded: what a copy that no package manager owns is pointed at.</summary>
    public const string ReleasesPage = "https://github.com/" + UpdateChecker.Repo + "/releases";

    /// <summary>
    /// What "Copy command" puts on the clipboard: the commands that must all run, joined with <c>&amp;&amp;</c> so one paste does everything (notes removed). Lines that are alternatives
    /// for different systems carry a "# note": only the first of those is included. Null when the instructions are a sentence or an address, not commands.
    /// </summary>
    public static string? PrimaryCommand(string? instructions)
    {
        var commands = new List<string>();
        foreach (var line in (instructions ?? "").Split('\n'))
        {
            var text = line; var hash = text.IndexOf("  #", StringComparison.Ordinal); var noted = hash >= 0;
            if (noted) text = text[..hash];
            text = text.Trim();
            if (!(text.StartsWith("curl ", StringComparison.Ordinal) || text.StartsWith("sudo ", StringComparison.Ordinal) || text.StartsWith("hdiutil ", StringComparison.Ordinal) || text.StartsWith("ditto ", StringComparison.Ordinal))) continue;
            commands.Add(text);
            if (noted) break;
        }
        return commands.Count == 0 ? null : string.Join(" && ", commands);
    }

    /// <summary>Which wording tells the user how to update a copy the app will not update itself.</summary>
    public enum ManualStyle { PackageManager, Mac, Download }

    public static ManualStyle ManualStyleFor(InstallKind kind, string? instructions) =>
        kind is InstallKind.LinuxDeb or InstallKind.LinuxRpm or InstallKind.LinuxPacman ? ManualStyle.PackageManager
        : kind == InstallKind.MacApp && PrimaryCommand(instructions) != null ? ManualStyle.Mac
        : ManualStyle.Download;
}
