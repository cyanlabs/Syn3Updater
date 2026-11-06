using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Cyanlabs.Launcher.Updating
{
    /// <summary>
    ///     A release tag parsed leniently: "2.14.2.0", "v3.0.0", "3.0.0-beta.1" and "3.0" are all understood.
    ///     (The old launcher used <c>Version.Parse</c>, which throws on a leading "v" or any suffix.)
    /// </summary>
    public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
    {
        private static readonly Regex Pattern = new Regex(@"^(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?(?:[-+](.+))?$", RegexOptions.Compiled);

        private ReleaseVersion(int major, int minor, int build, int revision, string suffix)
        {
            Major = major; Minor = minor; Build = build; Revision = revision; Suffix = suffix ?? "";
        }

        public int Major { get; }
        public int Minor { get; }
        public int Build { get; }
        public int Revision { get; }

        /// <summary>The pre-release label ("beta.1"), or empty for a normal release.</summary>
        public string Suffix { get; }

        public bool IsPrerelease => Suffix.Length > 0;

        /// <summary>Always four numeric parts ("3.0.0.0"), the form older launchers and v2 can still parse.</summary>
        public string NumericString => $"{Major}.{Minor}.{Build}.{Revision}";

        public static bool TryParse(string text, out ReleaseVersion version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var t = text.Trim();
            if (t[0] == 'v' || t[0] == 'V') t = t.Substring(1);
            var m = Pattern.Match(t);
            if (!m.Success) return false;
            var parts = new int[4];
            for (var i = 0; i < 4; i++)
            {
                var g = m.Groups[i + 1];
                if (!g.Success) continue;
                if (!int.TryParse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture, out parts[i])) return false;
            }
            version = new ReleaseVersion(parts[0], parts[1], parts[2], parts[3], m.Groups[5].Success ? m.Groups[5].Value : "");
            return true;
        }

        public static ReleaseVersion ParseOrNull(string text) => TryParse(text, out var v) ? v : null;

        public int CompareTo(ReleaseVersion other)
        {
            if (other == null) return 1;
            var c = Major.CompareTo(other.Major);
            if (c != 0) return c;
            c = Minor.CompareTo(other.Minor);
            if (c != 0) return c;
            c = Build.CompareTo(other.Build);
            if (c != 0) return c;
            c = Revision.CompareTo(other.Revision);
            if (c != 0) return c;
            // 3.0.0 is newer than 3.0.0-beta.1
            if (Suffix.Length == 0 && other.Suffix.Length == 0) return 0;
            if (Suffix.Length == 0) return 1;
            if (other.Suffix.Length == 0) return -1;
            return NaturalCompare(Suffix, other.Suffix);
        }

        public bool Equals(ReleaseVersion other) => other != null && CompareTo(other) == 0;
        public override bool Equals(object obj) => Equals(obj as ReleaseVersion);
        public override int GetHashCode() => (Major, Minor, Build, Revision, Suffix.ToLowerInvariant()).GetHashCode();
        public override string ToString() => IsPrerelease ? NumericString + "-" + Suffix : NumericString;

        public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a != null && a.CompareTo(b) > 0;
        public static bool operator <(ReleaseVersion a, ReleaseVersion b) => b != null && b.CompareTo(a) > 0;
        public static bool operator ==(ReleaseVersion a, ReleaseVersion b) => ReferenceEquals(a, b) || (!(a is null) && a.Equals(b));
        public static bool operator !=(ReleaseVersion a, ReleaseVersion b) => !(a == b);

        /// <summary>Compares "test2" and "test10" the way a person would (digit runs numerically).</summary>
        private static int NaturalCompare(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    var si = i; var sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    var na = a.Substring(si, i - si).TrimStart('0'); var nb = b.Substring(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                    var c = string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    var c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}
