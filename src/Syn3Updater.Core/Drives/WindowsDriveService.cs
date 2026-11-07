using System.Text.Json;

namespace Cyanlabs.Syn3Updater.Core.Drives;

/// <summary>Windows: PowerShell storage cmdlets to list and format (requires an elevated process); diskpart only if the cmdlets are missing.</summary>
public class WindowsDriveService : IDriveService
{
    private readonly CommandRunner _run;
    private readonly Func<string, bool> _exists;
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly Func<string, bool> _ready;

    public WindowsDriveService(CommandRunner? run = null, Func<string, bool>? exists = null, Func<int, CancellationToken, Task>? delay = null, Func<string, bool>? ready = null)
    {
        _run = run ?? ((file, args, stdin, ct, timeout) => ProcessRunner.RunAsync(file, args, stdin, ct, timeout));
        _exists = exists ?? ProcessRunner.Exists;
        _delay = delay ?? Task.Delay;
        _ready = ready ?? (root => { try { return Directory.Exists(root); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; } });
    }

    private Task<ProcessResult> Run(string file, IEnumerable<string> args, string? stdin = null, CancellationToken ct = default, int timeoutMs = 120_000) =>
        _run(file, args, stdin, ct, timeoutMs);

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
        var r = await Run("powershell", new[] { "-NoProfile", "-NonInteractive", "-Command", ListScript }, ct: ct);
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

    /// <summary>
    /// Formats with the PowerShell storage cmdlets: structured errors, no text script piped into diskpart and no dependence on its
    /// localized output. <c>{0}</c> is the validated disk number and <c>{1}</c> the drive letter to keep ('' = any). <c>{2}</c> and <c>{3}</c> are the size and
    /// name of the drive the user confirmed (0 / '' = unknown): the script refuses if disk <c>{0}</c> is a different device by now, so that check happens at the
    /// moment of the format and not in a separate listing beforehand. Exit code 3 means the cmdlets do not exist on this system, which is the only case that
    /// falls back to diskpart.
    /// </summary>
    public const string FormatScript = """
        $ErrorActionPreference = 'Stop'
        if (-not (Get-Command Clear-Disk -ErrorAction SilentlyContinue)) { [Console]::Error.WriteLine('The Windows storage cmdlets are not available'); exit 3 }
        try {
          $n = {0}; $keep = '{1}'; $size = {2}; $name = '{3}'
          $d = Get-Disk -Number $n
          if ($d.BusType -ne 'USB' -or $d.IsSystem -or $d.IsBoot) { throw ('Disk ' + $n + ' is not a removable USB disk; refusing to format it.') }
          if (($size -gt 0 -and [int64]$d.Size -ne $size) -or ($name -ne '' -and $d.FriendlyName -ne $name)) { throw ('Disk ' + $n + ' is no longer the drive you selected (it is now ' + $d.FriendlyName + '); refusing to format it.') }
          if ($d.IsOffline) { Set-Disk -Number $n -IsOffline $false }
          if ($d.IsReadOnly) { Set-Disk -Number $n -IsReadOnly $false }
          Clear-Disk -Number $n -RemoveData -RemoveOEM -Confirm:$false
          # Clear-Disk normally makes the disk RAW, but some USB controllers leave the
          # existing partition style in place. Initialize-Disk only accepts RAW disks.
          # Keep MBR as-is in that case; any other initialized style has no partitions
          # after Clear-Disk and can safely be converted to MBR.
          $style = [string](Get-Disk -Number $n).PartitionStyle
          if ($style -eq 'RAW') { Initialize-Disk -Number $n -PartitionStyle MBR }
          elseif ($style -ne 'MBR') { Set-Disk -Number $n -PartitionStyle MBR -Confirm:$false }
          if ($keep -ne '' -and -not (Get-Volume -DriveLetter $keep -ErrorAction SilentlyContinue)) { $p = New-Partition -DiskNumber $n -UseMaximumSize -DriveLetter $keep }
          else { $p = New-Partition -DiskNumber $n -UseMaximumSize -AssignDriveLetter }
          Format-Volume -Partition $p -FileSystem exFAT -NewFileSystemLabel 'CYANLABS' -Confirm:$false -Force | Out-Null
          $letter = (Get-Partition -DiskNumber $n | Where-Object { $_.DriveLetter } | Select-Object -First 1).DriveLetter
          Write-Output ('LETTER=' + $letter)
        } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
        """;

    public async Task<string> FormatAsync(DriveInfoModel drive, IProgress<string> log, CancellationToken ct = default)
    {
        if (!int.TryParse(drive.DeviceId, out var number) || number < 0) throw new Drives.FormatException("Invalid disk number.");
        // No separate "is it still listed?" pass first: it was a second PowerShell run (seconds), and one bad listing made it refuse a perfectly good drive
        // ("Disk 1 is no longer a USB drive"). The script checks the disk itself, atomically with the format.
        var letter = string.IsNullOrEmpty(drive.MountPath) ? "" : drive.MountPath[..1];
        if (letter != "" && !System.Text.RegularExpressions.Regex.IsMatch(letter, "^[A-Za-z]$")) letter = "";        // only ever a single letter reaches the script
        log.Report("Re-creating partition table as MBR and formatting as exFAT on the selected USB drive");

        var size = Math.Max(0, drive.SizeBytes).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var script = FormatScript.Replace("{0}", number.ToString(System.Globalization.CultureInfo.InvariantCulture)).Replace("{1}", letter.ToUpperInvariant())
            .Replace("{2}", size).Replace("{3}", ScriptSafeName(drive.Name));
        var r = await Run("powershell", new[] { "-NoProfile", "-NonInteractive", "-Command", script }, ct: ct, timeoutMs: 600_000);
        string? assigned = null;
        if (r.ExitCode == 3)
        {
            log.Report("The PowerShell storage cmdlets are not available, falling back to diskpart");
            await DiskpartFormatAsync(number, letter, ct);
        }
        else if (!r.Ok)
        {
            var why = (r.StdErr + " " + r.StdOut).Trim();
            throw new Drives.FormatException("Formatting failed: " + why + (why.Contains("denied", StringComparison.OrdinalIgnoreCase) || why.Contains("administrator", StringComparison.OrdinalIgnoreCase)
                ? " (run Syn3 Updater as Administrator)" : ""));
        }
        else
        {
            var m = System.Text.RegularExpressions.Regex.Match(r.StdOut, @"LETTER=([A-Za-z])");
            if (m.Success) assigned = m.Groups[1].Value.ToUpperInvariant();
        }

        // The script reports the letter it ended up with: wait (up to ~10 s) for Windows to finish mounting it instead of a fixed 5 s pause and another full listing.
        if (assigned != null) { await WaitUntilReadyAsync(assigned + @":\", ct); return assigned + @":\"; }
        await _delay(3000, ct);                                                          // diskpart gave no letter: look at the drives once
        var after = (await ListAsync(ct)).FirstOrDefault(d => d.DeviceId == drive.DeviceId);
        if (after?.MountPath is { Length: > 0 } mount) return mount;
        return letter != "" ? letter.ToUpperInvariant() + @":\" : throw new Drives.FormatException("Formatted, but no drive letter was assigned.");
    }

    private async Task WaitUntilReadyAsync(string root, CancellationToken ct)
    {
        for (var i = 0; i < 20 && !_ready(root); i++) await _delay(500, ct);
    }

    /// <summary>A name that is safe inside a single-quoted PowerShell string on a command line, or '' (skip the name check) when it holds anything odd.</summary>
    private static string ScriptSafeName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Any(c => c < ' ' || c == '"' || c == '`')) return "";
        return name.Replace("'", "''");
    }

    /// <summary>Last resort for Windows installs that lack the storage module (e.g. stripped-down editions).</summary>
    private async Task DiskpartFormatAsync(int number, string letter, CancellationToken ct)
    {
        var script = $"SELECT DISK={number}\nCLEAN\nCONVERT MBR\nCREATE PARTITION PRIMARY\nFORMAT FS=EXFAT LABEL=\"CYANLABS\" QUICK\n" +
                     (letter != "" ? $"ASSIGN LETTER={letter}\n" : "ASSIGN\n") + "EXIT\n";
        var r = await Run("diskpart", Array.Empty<string>(), stdin: script, ct: ct, timeoutMs: 600_000);
        if (!r.Ok) throw new Drives.FormatException("diskpart failed (run Syn3 Updater as Administrator): " + (r.StdErr + r.StdOut).Trim());
    }

    public Task<string> EnsureMountedAsync(DriveInfoModel drive, CancellationToken ct = default) =>
        Task.FromResult(string.IsNullOrEmpty(drive.MountPath) ? throw new Drives.FormatException("The drive has no drive letter.") : drive.MountPath);

    public Task FlushAsync(DriveInfoModel drive, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Flushes the volume cache, then uses the shell's Eject verb (the same as "Eject" in Explorer).</summary>
    public async Task<EjectResult> EjectAsync(DriveInfoModel drive, string? mountPath, CancellationToken ct = default)
    {
        var letter = System.Text.RegularExpressions.Regex.Match(mountPath ?? drive.MountPath ?? "", @"^([A-Za-z]):");
        if (!letter.Success) return new(false, "The drive has no drive letter.");
        var l = char.ToUpperInvariant(letter.Groups[1].Value[0]);
        if (!await IsUsbVolumeAsync(l, ct))
            return new(false, $"{l}: is not a removable USB drive; refusing to eject it.");     // never touch a system or fixed disk
        var script = $"$l='{l}:'; try {{ Write-VolumeCache -DriveLetter {l} -ErrorAction Stop }} catch {{ }}; " +
                     "$i = (New-Object -ComObject Shell.Application).Namespace(17).ParseName($l); if ($null -eq $i) { exit 3 }; $i.InvokeVerb('Eject')";
        // Windows often refuses the first try because something (the indexer, antivirus, a thumbnail cache) still has the volume open for a moment, so ask again
        // a couple of times before giving up; a real error from PowerShell is reported straight away.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var r = await Run("powershell", new[] { "-NoProfile", "-NonInteractive", "-Command", script }, ct: ct);
            if (r.ExitCode == 3) return new(false, $"Windows does not list drive {l}: any more.");
            if (!r.Ok) return new(false, "Could not eject the drive: " + r.StdErr.Trim());
            for (var look = 0; look < 4; look++)                           // the verb returns before Windows has finished
            {
                await _delay(1000, ct);
                if (!await StillMountedAsync(l, ct)) return new(true, "The drive was ejected. You can remove it now.");
            }
            if (attempt < 3) await _delay(2000, ct);
        }
        return new(false, "Windows could not eject the drive. Close anything that is using it (such as Explorer windows) and try again.");
    }

    private async Task<bool> StillMountedAsync(char l, CancellationToken ct) =>
        (await ListAsync(ct)).Any(d => d.MountPath.StartsWith(l + ":", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the letter belongs to a USB drive. One odd listing does not decide it: look twice before refusing.</summary>
    private async Task<bool> IsUsbVolumeAsync(char l, CancellationToken ct)
    {
        for (var i = 0; i < 2; i++)
        {
            if (await StillMountedAsync(l, ct)) return true;
            if (i == 0) await _delay(1000, ct);
        }
        return false;
    }
}
