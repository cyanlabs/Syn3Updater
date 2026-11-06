using System;
using System.Collections.Generic;
using System.Linq;

namespace Cyanlabs.Launcher.Updating
{
    public static class ReleasePicker
    {
        /// <summary>
        ///     The newest release (by version, not by upload order) that this channel follows and the machine can run.
        ///     Stable = full releases only; Beta/Alpha = full and pre-releases. Drafts and tags that are not versions are ignored.
        /// </summary>
        public static ReleaseInfo Pick(IEnumerable<ReleaseInfo> releases, ReleaseChannel channel, Func<ReleaseVersion, bool> isSupported = null)
        {
            return (releases ?? Enumerable.Empty<ReleaseInfo>())
                .Where(r => r != null && !r.Draft && r.Version != null)
                .Where(r => channel != ReleaseChannel.Stable || (!r.Prerelease && !r.Version.IsPrerelease))
                .Where(r => isSupported == null || isSupported(r.Version))
                .OrderByDescending(r => r.Version)
                .FirstOrDefault();
        }
    }

    public enum UpdateDecision
    {
        UpToDate,
        Install
    }

    public static class UpdateRules
    {
        /// <summary>
        ///     Installs when the app is missing, when the release is newer, or when the user switched channel and the release differs.
        ///     The old launcher installed anything that merely differed from the installed tag, so a stale or older release could replace a newer install.
        /// </summary>
        public static UpdateDecision Decide(string installedTag, ReleaseChannel? installedChannel, ReleaseChannel channel, ReleaseVersion candidate, bool appExists)
        {
            if (!appExists) return UpdateDecision.Install;
            var installed = ReleaseVersion.ParseOrNull(installedTag);
            if (installed == null) return UpdateDecision.Install;
            if (installedChannel.HasValue && installedChannel.Value != channel && candidate != installed) return UpdateDecision.Install;
            return candidate > installed ? UpdateDecision.Install : UpdateDecision.UpToDate;
        }
    }

    public static class AssetPicker
    {
        private static readonly string[] OtherPlatforms = { "linux", "macos", "osx", "darwin" };

        /// <summary>
        ///     Picks the Windows zip. Understands both packagings:
        ///     the v2 style (a single flat "&lt;version&gt;.zip" served as application/x-zip-compressed) and the v3 style
        ///     ("Syn3Updater-&lt;version&gt;-win-&lt;arch&gt;.zip"). Checksum files and other platforms are never chosen.
        /// </summary>
        public static ReleaseAsset Pick(IEnumerable<ReleaseAsset> assets, string arch)
        {
            var zips = (assets ?? Enumerable.Empty<ReleaseAsset>())
                .Where(a => a != null && a.Name != null && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .Where(a => !OtherPlatforms.Any(p => a.Name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();

            // 1. built for this architecture
            var exact = zips.FirstOrDefault(a => a.Name.IndexOf("win-" + arch, StringComparison.OrdinalIgnoreCase) >= 0);
            if (exact != null) return exact;

            // 2. the v2 packaging: one generic zip
            var generic = zips.Where(a => a.Name.IndexOf("win-", StringComparison.OrdinalIgnoreCase) < 0).ToList();
            var legacy = generic.FirstOrDefault(a => string.Equals(a.ContentType, "application/x-zip-compressed", StringComparison.OrdinalIgnoreCase));
            return legacy ?? generic.FirstOrDefault();
            // (a zip built only for another Windows architecture is deliberately not used)
        }
    }
}
