using System.Text.Json;

namespace Cyanlabs.Syn3Updater.Core.Drives;

/// <summary>Linux: lists with lsblk, formats with sfdisk + mkfs.exfat through pkexec (graphical sudo prompt).</summary>
public class LinuxDriveService : IDriveService
{
    public string OsDescription => "Linux";

    private const string Columns = "NAME,PATH,TYPE,SIZE,FSTYPE,LABEL,MOUNTPOINT,TRAN,HOTPLUG,RM,MODEL,PTTYPE,FSAVAIL";

    public async Task<IReadOnlyList<DriveInfoModel>> ListAsync(CancellationToken ct = default)
    {
        var r = await ProcessRunner.RunAsync("lsblk", new[] { "-J", "-b", "-o", Columns }, ct: ct);
        if (!r.Ok) throw new IOException("lsblk failed: " + r.StdErr);
        return ParseLsblk(r.StdOut);
    }

    /// <summary>Pure parser, covered by unit tests. Only USB / hotplug disks that do not hold the running system are returned.</summary>
    public static IReadOnlyList<DriveInfoModel> ParseLsblk(string json)
    {
        var list = new List<DriveInfoModel>();
        using var doc = JsonDocument.Parse(json);
        foreach (var dev in doc.RootElement.GetProperty("blockdevices").EnumerateArray())
        {
            if (Str(dev, "type") != "disk") continue;
            bool removable = Str(dev, "tran") == "usb" || Bool(dev, "hotplug") || Bool(dev, "rm");
            long size = Long(dev, "size");
            if (!removable || size == 0) continue;

            var parts = dev.TryGetProperty("children", out var ch) ? Flatten(ch).ToList() : new List<JsonElement>();
            if (parts.Any(p => IsSystemMount(Str(p, "mountpoint")))) continue;   // never offer the system disk
            if (IsSystemMount(Str(dev, "mountpoint"))) continue;

            var first = parts.FirstOrDefault(p => Str(p, "type") is "part" or "disk") ;
            var fs = first.ValueKind == JsonValueKind.Object ? Str(first, "fstype") : Str(dev, "fstype");
            var model = (Str(dev, "model") ?? "").Trim();
            var label = first.ValueKind == JsonValueKind.Object ? Str(first, "label") : Str(dev, "label");
            var mount = first.ValueKind == JsonValueKind.Object ? Str(first, "mountpoint") : Str(dev, "mountpoint");
            var free = first.ValueKind == JsonValueKind.Object ? Long(first, "fsavail") : Long(dev, "fsavail");
            var pttype = Str(dev, "pttype");

            list.Add(new DriveInfoModel
            {
                DeviceId = Str(dev, "path") ?? "/dev/" + Str(dev, "name"),
                Name = string.IsNullOrWhiteSpace(model) ? (Str(dev, "name") ?? "USB drive") : model,
                Model = model,
                SizeBytes = size,
                FileSystem = fs switch { "exfat" => "exFAT", "vfat" => "FAT32", "ntfs" => "NTFS", null => "", _ => fs },
                VolumeName = label ?? "",
                MountPath = mount ?? "",
                FreeBytes = free,
                PartitionType = pttype switch { "dos" => "MBR", "gpt" => "GPT", _ => pttype?.ToUpperInvariant() ?? "" },
                Encrypted = fs == "crypto_LUKS" || fs == "BitLocker",
            });
        }
        return list;
    }

    private static IEnumerable<JsonElement> Flatten(JsonElement children)
    {
        foreach (var c in children.EnumerateArray())
        {
            yield return c;
            if (c.TryGetProperty("children", out var sub)) foreach (var s in Flatten(sub)) yield return s;
        }
    }

    private static bool IsSystemMount(string? m) =>
        m != null && (m == "/" || m == "/boot" || m == "/boot/efi" || m == "/home" || m == "/usr" || m == "/var" || m == "[SWAP]");

    private static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Bool(JsonElement e, string p) => e.TryGetProperty(p, out var v) &&
        (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() == "1"));
    private static long Long(JsonElement e, string p)
    {
        if (!e.TryGetProperty(p, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetInt64();
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var l) ? l : 0;
    }

    public async Task<string> FormatAsync(DriveInfoModel drive, IProgress<string> log, CancellationToken ct = default)
    {
        // Re-validate right before the destructive step: the device must still be a removable disk we would have listed.
        var current = await ListAsync(ct);
        if (!current.Any(d => d.DeviceId == drive.DeviceId))
            throw new Drives.FormatException($"{drive.DeviceId} is no longer a removable USB drive; refusing to format it.");
        if (!ProcessRunner.Exists("mkfs.exfat"))
            throw new Drives.FormatException("mkfs.exfat was not found. Install exFAT tools (Arch: sudo pacman -S exfatprogs).");
        if (!ProcessRunner.Exists("pkexec"))
            throw new Drives.FormatException("pkexec (polkit) was not found; run the app as root or install polkit to format drives.");

        log.Report($"Re-creating partition table as MBR and formatting {drive.DeviceId} as exFAT (authentication required)");
        // Arguments are passed positionally so nothing from the device list is ever interpolated into the script.
        const string script = """
            set -e
            dev="$1"
            for p in $(lsblk -nrpo PATH "$dev" | tail -n +2); do umount "$p" 2>/dev/null || true; done
            wipefs -a "$dev" >/dev/null
            printf 'label: dos\n,,7\n' | sfdisk --quiet "$dev"
            udevadm settle || true
            sleep 1
            part=$(lsblk -nrpo PATH "$dev" | sed -n 2p)
            [ -n "$part" ] || { echo "partition not created" >&2; exit 1; }
            mkfs.exfat -n CYANLABS "$part" >/dev/null
            echo "$part"
            """;
        var r = await ProcessRunner.RunAsync("pkexec", new[] { "/bin/sh", "-c", script, "format-usb", drive.DeviceId }, ct: ct, timeoutMs: 600_000);
        if (!r.Ok)
            throw new Drives.FormatException(r.ExitCode is 126 or 127 && string.IsNullOrWhiteSpace(r.StdErr)
                ? "Authentication was cancelled." : $"Formatting failed: {r.StdErr.Trim()}");

        var part = r.StdOut.Trim().Split('\n').Last().Trim();
        log.Report($"Created {part}");
        return await MountPartitionAsync(part, ct);
    }

    public async Task<string> EnsureMountedAsync(DriveInfoModel drive, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(drive.MountPath)) return drive.MountPath;
        var r = await ProcessRunner.RunAsync("lsblk", new[] { "-nrpo", "PATH,TYPE", drive.DeviceId }, ct: ct);
        var part = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ')).FirstOrDefault(p => p.Length > 1 && p[1] == "part")?[0] ?? drive.DeviceId;
        return await MountPartitionAsync(part, ct);
    }

    private static async Task<string> MountPartitionAsync(string part, CancellationToken ct)
    {
        // The desktop may already have auto-mounted it.
        await Task.Delay(500, ct);
        var existing = await ProcessRunner.RunAsync("findmnt", new[] { "-nro", "TARGET", "-S", part }, ct: ct);
        if (existing.Ok && !string.IsNullOrWhiteSpace(existing.StdOut)) return existing.StdOut.Trim().Split('\n')[0];

        if (!ProcessRunner.Exists("udisksctl"))
            throw new Drives.FormatException($"{part} is not mounted and udisksctl (udisks2) is not installed. Mount it manually and press Refresh.");
        var m = await ProcessRunner.RunAsync("udisksctl", new[] { "mount", "-b", part }, ct: ct);
        if (!m.Ok) throw new Drives.FormatException("Could not mount the drive: " + m.StdErr.Trim());
        // "Mounted /dev/sdb1 at /run/media/user/CYANLABS"
        var at = m.StdOut.LastIndexOf(" at ", StringComparison.Ordinal);
        return at < 0 ? throw new Drives.FormatException("Unexpected udisksctl output: " + m.StdOut) : m.StdOut[(at + 4)..].Trim().TrimEnd('.');
    }

    public async Task FlushAsync(DriveInfoModel drive, CancellationToken ct = default) =>
        await ProcessRunner.RunAsync("sync", Array.Empty<string>(), ct: ct);
}
