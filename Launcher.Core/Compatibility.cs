using System.Runtime.InteropServices;

namespace Cyanlabs.Launcher.Updating
{
    /// <summary>Decides which releases a machine can actually run. v3 is .NET 10 and needs Windows 10 1607 or newer; v2 ran on much older systems.</summary>
    public static class Compatibility
    {
        /// <summary>Windows 10 version 1607 (the "Anniversary Update").</summary>
        public const int MinWindowsBuildForV3 = 14393;

        /// <param name="osBuild">The Windows build number, or 0 when it could not be read (then nothing is blocked).</param>
        public static bool IsSupported(ReleaseVersion version, int osBuild)
        {
            if (version == null) return false;
            if (osBuild <= 0) return true;
            return version.Major < 3 || osBuild >= MinWindowsBuildForV3;
        }

        /// <summary>The label used in asset names: "x64", "arm64" or "x86".</summary>
        public static string ArchLabel()
        {
            switch (RuntimeInformation.OSArchitecture)
            {
                case Architecture.Arm64: return "arm64";
                case Architecture.X86: return "x86";
                default: return "x64";
            }
        }
    }
}
