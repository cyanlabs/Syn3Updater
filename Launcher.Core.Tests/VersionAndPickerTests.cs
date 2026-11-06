using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Cyanlabs.Launcher.Core.Tests
{
    public class VersionTests
    {
        [Theory]
        [InlineData("2.14.2.0", "2.14.2.0")] [InlineData("v3.0.0", "3.0.0.0")] [InlineData("V3.0", "3.0.0.0")] [InlineData("3", "3.0.0.0")]
        [InlineData("3.0.0-test2", "3.0.0.0-test2")] [InlineData(" 3.1.0+build5 ", "3.1.0.0-build5")]
        public void Parses_WhatTheOldLauncherCouldNot(string tag, string expected) => Assert.Equal(expected, ReleaseVersion.ParseOrNull(tag).ToString());

        [Theory]
        [InlineData("")] [InlineData("latest")] [InlineData("v")] [InlineData("1.2.3.4.5")] [InlineData("99999999999.0")]
        public void RejectsNonVersions(string tag) => Assert.False(ReleaseVersion.TryParse(tag, out _));

        [Fact]
        public void NumericString_IsAlwaysFourParts_SoOlderLaunchersCanReadIt() => Assert.Equal("3.0.0.0", ReleaseVersion.ParseOrNull("v3.0.0-beta.1").NumericString);

        [Theory]
        [InlineData("2.14.2.0", "2.14.10.0", -1)] [InlineData("3.0.0", "3.0.0.0", 0)] [InlineData("v3.0.0", "3.0.0", 0)]
        [InlineData("3.0.0", "3.0.0-beta.1", 1)] [InlineData("3.0.0-test2", "3.0.0-test10", -1)] [InlineData("3.0.0-beta", "3.0.0-alpha", 1)]
        [InlineData("3.0.0-test1", "2.99.99", 1)]
        public void Orders(string a, string b, int expected) =>
            Assert.Equal(expected, System.Math.Sign(ReleaseVersion.ParseOrNull(a).CompareTo(ReleaseVersion.ParseOrNull(b))));
    }

    public class PickerTests
    {
        private static ReleaseInfo R(string tag, bool pre = false, bool draft = false) => new ReleaseInfo { TagName = tag, Prerelease = pre, Draft = draft };

        [Fact]
        public void Stable_IgnoresPrereleases_Drafts_AndJunkTags()
        {
            var releases = new[] { R("2.14.2.0"), R("3.0.0.0", pre: true), R("v3.1.0-beta.1"), R("nightly"), R("2.15.0.0", draft: true) };
            Assert.Equal("2.14.2.0", ReleasePicker.Pick(releases, ReleaseChannel.Stable).TagName);
        }

        [Fact]
        public void Beta_TakesTheNewestIncludingPrereleases() =>
            Assert.Equal("3.0.0.0", ReleasePicker.Pick(new[] { R("2.14.2.0"), R("3.0.0.0", pre: true), R("2.14.1.0", pre: true) }, ReleaseChannel.Beta).TagName);

        [Fact]
        public void PicksByVersion_NotByUploadOrder() =>
            Assert.Equal("3.0.0.0", ReleasePicker.Pick(new[] { R("2.14.9.0"), R("3.0.0.0"), R("2.14.2.0") }, ReleaseChannel.Stable).TagName);

        [Fact]
        public void AMachineThatCannotRunV3_IsKeptOnTheNewestV2()
        {
            var releases = new[] { R("2.14.2.0"), R("3.0.0.0") };
            Assert.Equal("2.14.2.0", ReleasePicker.Pick(releases, ReleaseChannel.Stable, v => Compatibility.IsSupported(v, 9600)).TagName);
            Assert.Equal("3.0.0.0", ReleasePicker.Pick(releases, ReleaseChannel.Stable, v => Compatibility.IsSupported(v, 19045)).TagName);
        }

        [Fact]
        public void NoReleases_ReturnsNull() { Assert.Null(ReleasePicker.Pick(new ReleaseInfo[0], ReleaseChannel.Stable)); Assert.Null(ReleasePicker.Pick(null, ReleaseChannel.Beta)); }

        [Theory]
        [InlineData(14393, true)] [InlineData(22631, true)] [InlineData(9600, false)] [InlineData(7601, false)] [InlineData(0, true)]
        public void V3NeedsWindows10_1607(int build, bool ok) => Assert.Equal(ok, Compatibility.IsSupported(ReleaseVersion.ParseOrNull("3.0.0"), build));

        [Fact]
        public void V2_RunsEverywhere() => Assert.True(Compatibility.IsSupported(ReleaseVersion.ParseOrNull("2.14.2.0"), 7601));
    }

    public class RuleTests
    {
        private static ReleaseVersion V(string s) => ReleaseVersion.ParseOrNull(s);

        [Fact] public void MissingApp_AlwaysInstalls() => Assert.Equal(UpdateDecision.Install, UpdateRules.Decide("3.0.0.0", ReleaseChannel.Stable, ReleaseChannel.Stable, V("3.0.0"), false));
        [Fact] public void Newer_Installs() => Assert.Equal(UpdateDecision.Install, UpdateRules.Decide("2.14.2.0", ReleaseChannel.Stable, ReleaseChannel.Stable, V("3.0.0"), true));
        [Fact] public void Same_IsUpToDate() => Assert.Equal(UpdateDecision.UpToDate, UpdateRules.Decide("3.0.0.0", ReleaseChannel.Stable, ReleaseChannel.Stable, V("v3.0.0"), true));
        [Fact] public void Older_IsNotInstalled_TheOldLauncherWould() => Assert.Equal(UpdateDecision.UpToDate, UpdateRules.Decide("3.0.0.0", ReleaseChannel.Stable, ReleaseChannel.Stable, V("2.14.2.0"), true));
        [Fact] public void SwitchingChannel_InstallsThatChannelsRelease_EvenIfOlder() => Assert.Equal(UpdateDecision.Install, UpdateRules.Decide("3.0.0.0", ReleaseChannel.Beta, ReleaseChannel.Stable, V("2.14.2.0"), true));
        [Fact] public void SwitchingChannel_ToTheSameVersion_DoesNothing() => Assert.Equal(UpdateDecision.UpToDate, UpdateRules.Decide("2.14.2.0", ReleaseChannel.Beta, ReleaseChannel.Stable, V("2.14.2.0"), true));
        [Fact] public void UnreadableInstalledTag_Reinstalls() => Assert.Equal(UpdateDecision.Install, UpdateRules.Decide("0.0.0.0-garbage?", null, ReleaseChannel.Stable, V("2.14.2.0"), true));
    }

    public class AssetTests
    {
        private static ReleaseAsset A(string name, string type = "application/zip") => new ReleaseAsset { Name = name, ContentType = type };

        [Fact]
        public void V2Packaging_SingleZipWithTheOldContentType() =>
            Assert.Equal("2.14.2.0.zip", AssetPicker.Pick(new[] { A("2.14.2.0.zip", "application/x-zip-compressed") }, "x64").Name);

        [Fact]
        public void V3Packaging_PicksTheMatchingArchitecture()
        {
            var assets = new[] { A("SHA256SUMS", "text/plain"), A("Syn3Updater-3.0.0-linux-x64.tar.gz"), A("Syn3Updater-3.0.0-win-arm64.zip"), A("Syn3Updater-3.0.0-win-x64.zip"), A("Syn3Updater-3.0.0-macos-arm64.dmg") };
            Assert.Equal("Syn3Updater-3.0.0-win-x64.zip", AssetPicker.Pick(assets, "x64").Name);
            Assert.Equal("Syn3Updater-3.0.0-win-arm64.zip", AssetPicker.Pick(assets, "arm64").Name);
        }

        [Fact] public void NeverPicksAnotherArchitecture() => Assert.Null(AssetPicker.Pick(new[] { A("Syn3Updater-3.0.0-win-arm64.zip") }, "x86"));
        [Fact] public void NeverPicksOtherPlatformsOrChecksums() => Assert.Null(AssetPicker.Pick(new[] { A("SHA256SUMS", "text/plain"), A("Syn3Updater-3.0.0-linux-x64.tar.gz"), A("app-macos.zip") }, "x64"));
        [Fact] public void NoAssets_ReturnsNull() { Assert.Null(AssetPicker.Pick(new ReleaseAsset[0], "x64")); Assert.Null(AssetPicker.Pick(null, "x64")); }
    }
}
