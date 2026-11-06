using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Cyanlabs.Launcher.Core
{
    public static class Checksums
    {
        public static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
            {
                var hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>Accepts "sha256:ab12…" (GitHub's digest field) or a bare hex string.</summary>
        public static string Normalize(string digest)
        {
            if (string.IsNullOrWhiteSpace(digest)) return null;
            var d = digest.Trim();
            var colon = d.IndexOf(':');
            if (colon >= 0)
            {
                if (!d.Substring(0, colon).Equals("sha256", StringComparison.OrdinalIgnoreCase)) return null;
                d = d.Substring(colon + 1);
            }
            d = d.Trim().ToLowerInvariant();
            return d.Length == 64 && d.All(Uri.IsHexDigit) ? d : null;
        }

        /// <summary>Reads a "SHA256SUMS" file ("&lt;hash&gt;  &lt;name&gt;" or "&lt;hash&gt; *&lt;name&gt;" per line) and returns the hash for one file name.</summary>
        public static string FindInSums(string sumsText, string fileName)
        {
            if (string.IsNullOrEmpty(sumsText) || string.IsNullOrEmpty(fileName)) return null;
            foreach (var raw in sumsText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                var space = line.IndexOf(' ');
                if (space < 0) continue;
                var name = line.Substring(space).Trim().TrimStart('*');
                if (!string.Equals(Path.GetFileName(name), fileName, StringComparison.OrdinalIgnoreCase)) continue;
                var hash = Normalize(line.Substring(0, space));
                if (hash != null) return hash;
            }
            return null;
        }

        public static bool Matches(string expected, string actual) =>
            expected != null && actual != null && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }
}
