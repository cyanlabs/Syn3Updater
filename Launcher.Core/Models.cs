using System.Collections.Generic;

namespace Cyanlabs.Launcher.Updating
{
    /// <summary>Which releases a user follows. Same names the v2 launcher used, so saved preferences keep working.</summary>
    public enum ReleaseChannel
    {
        Stable,
        Beta,
        Alpha
    }

    public sealed class ReleaseAsset
    {
        public string Name { get; set; } = "";
        public string ContentType { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public long Size { get; set; }

        /// <summary>GitHub's own checksum for the asset, e.g. "sha256:ab12…" (present on recent uploads).</summary>
        public string Digest { get; set; }
    }

    public sealed class ReleaseInfo
    {
        public string TagName { get; set; } = "";
        public bool Prerelease { get; set; }
        public bool Draft { get; set; }
        public List<ReleaseAsset> Assets { get; set; } = new List<ReleaseAsset>();

        public ReleaseVersion Version => ReleaseVersion.ParseOrNull(TagName);
    }
}
