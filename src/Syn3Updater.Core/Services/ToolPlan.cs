using Cyanlabs.Syn3Updater.Core.Models;

namespace Cyanlabs.Syn3Updater.Core.Services;

/// <summary>
/// The tool packages a normal update needs besides the packages the user ticks, by install mode. They are special packages the app looks up itself: the RW data cleaner starts every autoinstall.lst,
/// Reformat and Downgrade add the reformat tool, and Downgrade adds its own app and tool. Home lists them as locked rows, and Workflow queues exactly these.
/// </summary>
public static class ToolPlan
{
    /// <summary>The special packages (names as the backend knows them) for <paramref name="mode"/>, in the order they are downloaded.</summary>
    public static IReadOnlyList<string> For(string? mode) => mode switch
    {
        InstallModes.Downgrade => new[] { SpecialPackages.DowngradeApp, SpecialPackages.DowngradeTool, SpecialPackages.Reformat, SpecialPackages.RWDataCleaner },
        InstallModes.Reformat => new[] { SpecialPackages.Reformat, SpecialPackages.RWDataCleaner },
        _ => new[] { SpecialPackages.RWDataCleaner },
    };

    /// <summary>
    /// Sorts the special packages in <paramref name="packages"/> into the slots of a <see cref="SpecialTools"/> by their own <see cref="Ivsu.SpecialPackage"/>.
    /// Ordinary packages (no special package) and unknown names are ignored.
    /// </summary>
    public static SpecialTools Collect(IEnumerable<Ivsu> packages)
    {
        var tools = new SpecialTools();
        foreach (var package in packages)
        {
            switch (package.SpecialPackage)
            {
                case SpecialPackages.DowngradeApp: tools.DowngradeApp = package; break;
                case SpecialPackages.DowngradeTool: tools.DowngradeTool = package; break;
                case SpecialPackages.Reformat: tools.ReformatTool = package; break;
                case SpecialPackages.RWDataCleaner: tools.RwDataCleaner = package; break;
            }
        }
        return tools;
    }
}
