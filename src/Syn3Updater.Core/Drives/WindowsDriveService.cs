using System.Text.Json;

namespace Cyanlabs.Syn3Updater.Core.Drives;

/// <summary>Windows: PowerShell storage cmdlets to list, diskpart to format (requires an elevated process).</summary>
public class WindowsDriveService : IDriveService
{
    public string OsDescription => "Windows";

    private const string ListScript = """
        $out = foreach ($d in Get-Disk | Where-Object { $_.BusType -eq 'USB' -and $_.Size -gt 0 }) {
          $p = Get-Partition -DiskNumber $d.Number -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter } | Select-Object -First 1
          $v = if ($p) { Get-Volume -DriveLetter $p.DriveLetter -ErrorAction SilentlyContinue } else { $null }
          $bl = $null
          if ($p) { try { $bl = (Get-BitLockerVolume -MountPoint ($p.DriveLetter + ':') -ErrorAction Stop).ProtectionStatus } catch {} }
          [pscustomobject]@{
            Number = $d.Number; Name = $d.FriendlyName; Size = [int64]$d.Size; Style = [string]$d.PartitionStyle
            Letter = if ($p) { [string]$p.DriveLetter } else { '' }
            Fs = if ($v) { [string]$v.FileSystem } else { '' }
            Label = if ($v) { [string]$v.FileSystemLabel } else { '' }
            Free = if ($v) { [int64]$v.SizeRemaining } else { 0 }
            Encrypted = ($bl -ne $null -and [string]$bl -ne 'Off' -and [string]$bl -ne '0')
          }
        }
        ConvertTo-Json -InputObject @($out) -Compress
        """;

    public async Task<IReadOnlyList<DriveInfoModel>> ListAsync(CancellationToken ct = default)
    {
        var r = await ProcessRunner.RunAsync("powershell", new[] { "-NoProfile", "-NonInteractive", "-Command", ListScript }, ct: ct);
        if (!r.Ok) throw new IOException("PowerShell failed: " + r.StdErr);
        return Parse(r.StdOut);
    }

    public static IReadOnlyList<DriveInfoModel> Parse(string json)
    {
        var list = new List<DriveInfoModel>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        using var doc = JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var letter = e.GetProperty("Letter").GetString() ?? "";
            list.Add(new DriveInfoModel
            {
                DeviceId = e.GetProperty("Number").GetInt32().ToString(),
                Name = e.GetProperty("Name").GetString() ?? "USB drive",
                Model = e.GetProperty("Name").GetString() ?? "",
                SizeBytes = e.GetProperty("Size").GetInt64(),
                PartitionType = e.GetProperty("Style").GetString() switch { "MBR" => "MBR", "GPT" => "GPT", var s => s ?? "" },
                MountPath = letter == "" ? "" : letter + @":\",
                FileSystem = e.GetProperty("Fs").GetString() ?? "",
                VolumeName = e.GetProperty("Label").GetString() ?? "",
                FreeBytes = e.GetProperty("Free").GetInt64(),
                Encrypted = e.GetProperty("Encrypted").GetBoolean(),
            });
        }
        return list;
    }

    public async Task<string> FormatAsync(DriveInfoModel drive, IProgress<string> log, CancellationToken ct = default)
    {
        if (!int.TryParse(drive.DeviceId, out var number)) throw new Drives.FormatException("Invalid disk number.");
        if (!(await ListAsync(ct)).Any(d => d.DeviceId == drive.DeviceId))
            throw new Drives.FormatException($"Disk {number} is no longer a USB drive; refusing to format it.");
        var letter = string.IsNullOrEmpty(drive.MountPath) ? "" : drive.MountPath[..1];
        log.Report("Re-creating partition table as MBR and formatting as exFAT on the selected USB drive");
        var script = $"SELECT DISK={number}\nCLEAN\nCONVERT MBR\nCREATE PARTITION PRIMARY\nFORMAT FS=EXFAT LABEL=\"CYANLABS\" QUICK\n" +
                     (letter != "" ? $"ASSIGN LETTER={letter}\n" : "ASSIGN\n") + "EXIT\n";
        var r = await ProcessRunner.RunAsync("diskpart", Array.Empty<string>(), stdin: script, ct: ct, timeoutMs: 600_000);
        if (!r.Ok) throw new Drives.FormatException("diskpart failed (run Syn3 Updater as Administrator): " + (r.StdErr + r.StdOut).Trim());
        await Task.Delay(5000, ct);
        var after = (await ListAsync(ct)).FirstOrDefault(d => d.DeviceId == drive.DeviceId);
        return after?.MountPath is { Length: > 0 } m ? m : (letter != "" ? letter + @":\" : throw new Drives.FormatException("Formatted, but no drive letter was assigned."));
    }

    public Task<string> EnsureMountedAsync(DriveInfoModel drive, CancellationToken ct = default) =>
        Task.FromResult(string.IsNullOrEmpty(drive.MountPath) ? throw new Drives.FormatException("The drive has no drive letter.") : drive.MountPath);

    public Task FlushAsync(DriveInfoModel drive, CancellationToken ct = default) => Task.CompletedTask;
}
