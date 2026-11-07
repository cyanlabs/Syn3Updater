using Cyanlabs.Syn3Updater.Core.Services.Updates;

namespace Cyanlabs.Syn3Updater.App.Services;

/// <summary>How an install is described to the user, in their language. Every key is spelled out so the localisation checks can see it is used.</summary>
public static class InstallText
{
    public static string Kind(InstallKind kind) => Ctx.T(kind switch
    {
        InstallKind.WindowsFolder => "Install.Kind.WindowsFolder",
        InstallKind.WindowsPortable => "Install.Kind.WindowsPortable",
        InstallKind.LinuxTar => "Install.Kind.LinuxTar",
        InstallKind.LinuxDeb => "Install.Kind.LinuxDeb",
        InstallKind.LinuxRpm => "Install.Kind.LinuxRpm",
        InstallKind.LinuxPacman => "Install.Kind.LinuxPacman",
        InstallKind.LinuxSystemUnknown => "Install.Kind.LinuxSystemUnknown",
        InstallKind.LinuxPortable => "Install.Kind.LinuxPortable",
        InstallKind.MacApp => "Install.Kind.MacApp",
        InstallKind.Development => "Install.Kind.Development",
        _ => "Install.Kind.Unknown"
    });

    public static string Route(UpdateRoute route) => Ctx.T(route switch
    {
        UpdateRoute.InPlace => "Install.Route.InPlace",
        UpdateRoute.Manual => "Install.Route.Manual",
        _ => "Install.Route.None"
    });
}
