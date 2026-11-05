using Cyanlabs.Syn3Updater.Core.Models;

namespace Cyanlabs.Syn3Updater.Core.Services;

public enum MapChoice { MapPackage, NoMaps, NonNavApim, KeepExisting }

public record InstallModeResult(string? Mode, bool Invalid, string? Option);

/// <summary>
/// Picks the install mode from the installed Sync version, MY20 protection state and the
/// selected maps (upstream "Options A-H"). Mode is null when the user forced a mode and it should be kept.
/// </summary>
public static class InstallModeResolver
{
    public static InstallModeResult Resolve(int currentVersion, bool? my20, bool modeForced,
        MapChoice map, bool mapIsEsn, bool releaseIsOnlyMaps)
    {
        bool noMapUpdate = map != MapChoice.MapPackage;
        string? Keep(string m) => modeForced ? null : m;

        if (currentVersion < ApiEndpoints.ReformatVersion)
        {
            if (my20 == true) return new("autoinstall", true, "H");           // MY20 can't be on < 3.2
            return new(Keep(InstallModes.Reformat), false, "A");
        }

        bool newer = currentVersion >= ApiEndpoints.BlacklistedVersion;
        if (noMapUpdate) return new(Keep(InstallModes.Autoinstall), false, newer ? "E" : "B");
        if (my20 == true && mapIsEsn) return new(null, true, newer ? "F-ESN" : "C-ESN");
        if (my20 == true || releaseIsOnlyMaps) return new(InstallModes.Autoinstall, false, newer ? "F" : "C");
        return new(Keep(newer ? InstallModes.Downgrade : InstallModes.Reformat), false, newer ? "G" : "D");
    }
}
