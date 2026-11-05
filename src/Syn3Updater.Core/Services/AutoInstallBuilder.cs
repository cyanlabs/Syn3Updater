using System.Text;
using Cyanlabs.Syn3Updater.Core.Models;

namespace Cyanlabs.Syn3Updater.Core.Services;

public enum UpdateAction { Main, LogUtility, LogUtilityMy20, GracenotesRemoval, VoiceShrinker, Downgrade, RwDataCleaner, Upgrade22 }

public class SpecialTools
{
    public Ivsu? ReformatTool { get; set; }
    public Ivsu? RwDataCleaner { get; set; }
    public Ivsu? DowngradeApp { get; set; }
    public Ivsu? DowngradeTool { get; set; }
}

/// <summary>
/// Generates autoinstall.lst / reformat.lst content. The head unit expects CRLF line endings,
/// so these are always written as \r\n regardless of the host OS.
/// </summary>
public static class AutoInstallBuilder
{
    public const string NL = "\r\n";

    private static string Header(string appVersion, string branch, string label, string release, string region) =>
        $"; CyanLabs Syn3Updater {appVersion} {branch} - {label} - {release} {region}{NL}{NL}";

    private static void Entry(StringBuilder sb, int n, string type, string file, bool spaceBeforeEq = true) =>
        sb.Append($"Item{n} = {type} - {file}{NL}Open{n}{(spaceBeforeEq ? " " : "")}= SyncMyRide\\{file}{NL}");

    public static string CreateAutoInstall(IEnumerable<Ivsu> queue, SpecialTools tools, UpdateAction action,
        string release, string region, bool modeForced, string appVersion, string branch)
    {
        var sb = new StringBuilder(Header(appVersion, branch, $"Autoinstall {(modeForced ? "FORCED " : "")}Mode", release, region));
        var rw = tools.RwDataCleaner ?? throw new InvalidOperationException("RWDataCleaner package is required");

        // naviextras packages are not handled here; RWDataCleaner is added explicitly below
        var list = queue.Where(i => i.Source != "naviextras" && !ReferenceEquals(i, rw)).ToList();

        if (list.Any(i => i.Type == "MAP"))
        {
            sb.Append($"[SYNCGen3.0_3.0.1_PRODUCT]{NL}");
            Entry(sb, 1, "RWDataCleaner TOOL", rw.FileName);

            var mapLicense = list.Find(i => i.Type == "MAP_LICENSE");
            if (mapLicense != null)
            {
                Entry(sb, 2, mapLicense.Type, mapLicense.FileName);
                list.Remove(mapLicense);
            }

            // Spread the remaining packages over 3 install phases with roughly equal total size.
            // Sizes are in KiB so multi-GB map packages cannot overflow the partitioner's uint values.
            var vals = list.ConvertAll(i => (uint)Math.Min(uint.MaxValue, Math.Max(1, i.FileSize / 1024)));
            var buckets = new effPartition(vals, 3);
            for (ushort i = 0; i < buckets.SubsetCount; i++)
            {
                for (int j = 0; j < buckets[i].NumbIDs.Count; j++)
                {
                    int subIndex = (int)buckets[i].NumbIDs[j];
                    int partIndex = j + 1;
                    var item = list[subIndex - 1];
                    if (i == 0) partIndex++;                       // RWDataCleaner occupies Item1
                    if (i == 0 && mapLicense != null) partIndex++; // MAP_LICENSE occupies the next slot
                    Entry(sb, partIndex, item.Type, item.FileName);
                }

                if (i == 0) sb.Append($"Options = AutoInstall{NL}{NL}");
                if (i == 0 && list.Count > 1) sb.Append($"[SYNCGen3.0_3.0.1]{NL}");
                if (i == 1 && list.Count > 1) sb.Append($"Options = AutoInstall, Include, Transaction{NL}{NL}");
                if (i == 1 && list.Count > 2) sb.Append($"[SYNCGen3.0_ALL]{NL}");
                if (i == 2 && list.Count > 2) sb.Append($"Options = Delay, Include, Transaction{NL}");
            }
        }
        else
        {
            sb.Append($"[SYNCGen3.0_ALL_PRODUCT]{NL}");
            int current = 1;
            if (action is not (UpdateAction.LogUtility or UpdateAction.LogUtilityMy20 or UpdateAction.GracenotesRemoval
                or UpdateAction.VoiceShrinker or UpdateAction.Downgrade or UpdateAction.Upgrade22))
            {
                Entry(sb, current, "RWDataCleaner TOOL", rw.FileName, spaceBeforeEq: false);
                current = 2;
            }
            foreach (var item in list) Entry(sb, current++, item.Type, item.FileName);
            sb.Append($"Options = AutoInstall{NL}");
        }
        return sb.ToString();
    }

    /// <summary>Content of reformat.lst for reformat/downgrade installs (the packages the reformat tool restores).</summary>
    public static string CreateReformatList(IReadOnlyList<Ivsu> queue, string mode, SpecialTools t, string release)
    {
        var sb = new StringBuilder();
        int i = 0;
        foreach (var item in queue)
        {
            if (item.Source == "naviextras") continue;
            if (mode == InstallModes.Reformat)
            {
                if (item.Md5 == t.ReformatTool?.Md5 || item.Md5 == t.RwDataCleaner?.Md5) continue;
            }
            else if (mode == InstallModes.Downgrade)
            {
                if (item.Md5 == t.ReformatTool?.Md5
                    || (item.Md5 == t.DowngradeApp?.Md5 && release != "Sync 3.3.19052")
                    || item.Md5 == t.DowngradeTool?.Md5 || item.Md5 == t.RwDataCleaner?.Md5) continue;
            }
            i++;
            sb.Append($"{item.Type}={item.FileName}");
            if (i != queue.Count) sb.Append(NL);
        }
        return sb.ToString();
    }

    /// <summary>autoinstall.lst for the reformat / downgrade modes.</summary>
    public static string CreateReformatAutoInstall(string mode, SpecialTools t, string release, string region,
        bool modeForced, string appVersion, string branch)
    {
        var rw = t.RwDataCleaner ?? throw new InvalidOperationException("RWDataCleaner package is required");
        var reformat = t.ReformatTool ?? throw new InvalidOperationException("Reformat tool package is required");
        var sb = new StringBuilder($"; CyanLabs Syn3Updater {appVersion} {branch} - {mode} {(modeForced ? "FORCED " : "")} Mode - {release} {region}{NL}{NL}[SYNCGen3.0_ALL_PRODUCT]{NL}");
        if (mode == InstallModes.Downgrade)
        {
            var dtool = t.DowngradeTool ?? throw new InvalidOperationException("Downgrade tool package is required");
            var dapp = t.DowngradeApp ?? throw new InvalidOperationException("Downgrade app package is required");
            Entry(sb, 1, "RWDataCleaner TOOL", rw.FileName, false);
            Entry(sb, 2, "TOOL", dtool.FileName, false);
            Entry(sb, 3, "APP", dapp.FileName, false);
            sb.Append($"Options = AutoInstall{NL}[SYNCGen3.0_ALL]{NL}");
            Entry(sb, 1, "REFORMAT TOOL", reformat.FileName, false);
            sb.Append($"Options = AutoInstall,Include,Transaction{NL}");
        }
        else if (mode == InstallModes.Reformat)
        {
            Entry(sb, 1, "RWDataCleaner TOOL", rw.FileName, false);
            Entry(sb, 2, "REFORMAT TOOL ", reformat.FileName, false);
            sb.Append("Options = AutoInstall");
        }
        return sb.ToString();
    }
}
