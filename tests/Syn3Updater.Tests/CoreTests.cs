using Cyanlabs.Syn3Updater.Core.Drives;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

public class AutoInstallTests
{
    private static Ivsu I(string type, string file, long size = 1000, string? src = null, string md5 = "x") =>
        new() { Type = type, FileName = file, FileSize = size, Source = src, Md5 = md5 + file, Name = file };

    private static readonly Ivsu Rw = I("TOOL", "rw.tar.gz");

    [Fact]
    public void NoMaps_ListsRwCleanerThenPackages_WithCrlf()
    {
        var tools = new SpecialTools { RwDataCleaner = Rw };
        var q = new List<Ivsu> { Rw, I("APPS", "app.tar.gz"), I("VOICE", "voice.tar.gz") };
        var s = AutoInstallBuilder.CreateAutoInstall(q, tools, UpdateAction.Main, "Sync 3.4.20282", "EU", false, "3.0.0", "Stable");

        Assert.Contains("[SYNCGen3.0_ALL_PRODUCT]\r\n", s);
        Assert.Contains("Item1 = RWDataCleaner TOOL - rw.tar.gz\r\nOpen1= SyncMyRide\\rw.tar.gz\r\n", s);
        Assert.Contains("Item2 = APPS - app.tar.gz\r\nOpen2 = SyncMyRide\\app.tar.gz\r\n", s);
        Assert.Contains("Item3 = VOICE - voice.tar.gz", s);
        Assert.EndsWith("Options = AutoInstall\r\n", s);
        Assert.DoesNotContain("\n", s.Replace("\r\n", ""));
    }

    [Theory]
    [InlineData(UpdateAction.LogUtility)]
    [InlineData(UpdateAction.GracenotesRemoval)]
    [InlineData(UpdateAction.Downgrade)]
    [InlineData(UpdateAction.Upgrade22)]
    public void UtilityActions_OmitRwCleaner(UpdateAction a)
    {
        var s = AutoInstallBuilder.CreateAutoInstall(new[] { Rw, I("APPS", "a") }, new SpecialTools { RwDataCleaner = Rw }, a, "r", "EU", false, "1", "Stable");
        Assert.DoesNotContain("RWDataCleaner", s);
        Assert.Contains("Item1 = APPS - a", s);
    }

    [Fact]
    public void Maps_SplitIntoThreePhases_AllPackagesPresentOnce()
    {
        var tools = new SpecialTools { RwDataCleaner = Rw };
        var q = new List<Ivsu>
        {
            Rw, I("APPS", "app", 500_000_000), I("MAP_LICENSE", "lic", 1000),
            I("MAP", "m1", 3_000_000_000), I("MAP", "m2", 2_000_000_000), I("MAP", "m3", 1_000_000_000), I("MAP", "m4", 2_500_000_000),
        };
        var s = AutoInstallBuilder.CreateAutoInstall(q, tools, UpdateAction.Main, "r", "EU", false, "1", "Stable");
        Assert.Contains("[SYNCGen3.0_3.0.1_PRODUCT]", s);
        Assert.Contains("[SYNCGen3.0_3.0.1]", s);
        Assert.Contains("[SYNCGen3.0_ALL]", s);
        Assert.Contains("Options = Delay, Include, Transaction", s);
        foreach (var f in new[] { "app", "lic", "m1", "m2", "m3", "m4" })
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(s, $@"Open\d+ = SyncMyRide\\{f}\r"));
        // item numbers restart per phase but are unique within a phase header block
        Assert.Contains("Item2 = MAP_LICENSE - lic", s);
    }

    [Fact]
    public void Naviextras_BundleIsNotListed()
    {
        var q = new List<Ivsu> { Rw, I("MAP", "bundle", 10, "naviextras"), I("MAP", "extracted", 10) };
        var s = AutoInstallBuilder.CreateAutoInstall(q, new SpecialTools { RwDataCleaner = Rw }, UpdateAction.Main, "r", "EU", false, "1", "Stable");
        Assert.DoesNotContain("bundle", s);
        Assert.Contains("extracted", s);
    }

    [Fact]
    public void Reformat_WritesBothLists()
    {
        var reformat = I("TOOL", "reformat.tar.gz", md5: "R");
        var tools = new SpecialTools { RwDataCleaner = Rw, ReformatTool = reformat };
        var q = new List<Ivsu> { reformat, Rw, I("APPS", "app"), I("VOICE", "v") };
        var lst = AutoInstallBuilder.CreateReformatList(q, InstallModes.Reformat, tools, "Sync 3.4");
        Assert.Equal("APPS=app\r\nVOICE=v\r\n".TrimEnd(), lst.TrimEnd());
        var auto = AutoInstallBuilder.CreateReformatAutoInstall(InstallModes.Reformat, tools, "Sync 3.4", "NA", true, "1", "Stable");
        Assert.Contains("FORCED", auto);
        Assert.Contains("REFORMAT TOOL ", auto);
    }

    [Fact]
    public void EffPartition_BalancesBuckets()
    {
        var p = new effPartition(new List<uint> { 9, 8, 7, 6, 5, 4 }, 3);
        Assert.Equal(3, p.SubsetCount);
        Assert.Equal(6, p.Subsets.Sum(s => s.NumbIDs.Count));
        Assert.True(p.RelInacc < 20);
    }
}

public class InstallModeTests
{
    [Theory]
    [InlineData(3100000, false, MapChoice.NoMaps, false, false, "reformat")]       // A
    [InlineData(3300000, false, MapChoice.KeepExisting, false, false, "autoinstall")] // B
    [InlineData(3300000, false, MapChoice.MapPackage, false, false, "reformat")]     // D
    [InlineData(3300000, true, MapChoice.MapPackage, false, false, "autoinstall")]   // C
    [InlineData(3420282, false, MapChoice.NoMaps, false, false, "autoinstall")]      // E
    [InlineData(3420282, true, MapChoice.MapPackage, false, false, "autoinstall")]   // F
    [InlineData(3420282, false, MapChoice.MapPackage, false, false, "downgrade")]    // G
    [InlineData(3420282, false, MapChoice.MapPackage, false, true, "autoinstall")]   // only maps
    public void Resolves(int ver, bool my20, MapChoice map, bool esn, bool onlyMaps, string expected)
    {
        var r = InstallModeResolver.Resolve(ver, my20, false, map, esn, onlyMaps);
        Assert.False(r.Invalid);
        Assert.Equal(expected, r.Mode);
    }

    [Fact] public void My20_Below32_IsInvalid() => Assert.True(InstallModeResolver.Resolve(3100000, true, false, MapChoice.NoMaps, false, false).Invalid);
    [Fact] public void My20_EsnMap_IsInvalid() => Assert.True(InstallModeResolver.Resolve(3420282, true, false, MapChoice.MapPackage, true, false).Invalid);
    [Fact] public void ForcedMode_IsKept() => Assert.Null(InstallModeResolver.Resolve(3420282, false, true, MapChoice.MapPackage, false, false).Mode);
}

public class DriveTests
{
    private const string Lsblk = """
    {"blockdevices":[
     {"name":"nvme0n1","path":"/dev/nvme0n1","type":"disk","size":1000204886016,"tran":"nvme","hotplug":false,"rm":false,"model":"Samsung","pttype":"gpt","children":[
        {"name":"nvme0n1p2","path":"/dev/nvme0n1p2","type":"part","size":1,"fstype":"btrfs","mountpoint":"/"}]},
     {"name":"sda","path":"/dev/sda","type":"disk","size":31914983424,"tran":"usb","hotplug":true,"rm":true,"model":"Ultra Fit  ","pttype":"dos","children":[
        {"name":"sda1","path":"/dev/sda1","type":"part","size":31913934848,"fstype":"exfat","label":"CYANLABS","mountpoint":"/run/media/scott/CYANLABS","fsavail":"31000000000"}]},
     {"name":"sdb","path":"/dev/sdb","type":"disk","size":64000000000,"tran":"usb","hotplug":"1","rm":"1","model":"SSD","pttype":"gpt","children":[
        {"name":"sdb1","path":"/dev/sdb1","type":"part","size":1,"fstype":"ext4","mountpoint":"/home"}]},
     {"name":"sdc","path":"/dev/sdc","type":"disk","size":0,"tran":"usb","hotplug":true,"rm":true}
    ]}
    """;

    [Fact]
    public void Linux_OnlyRemovableNonSystemDisks()
    {
        var d = Assert.Single(LinuxDriveService.ParseLsblk(Lsblk));
        Assert.Equal("/dev/sda", d.DeviceId);
        Assert.Equal("Ultra Fit", d.Name);
        Assert.Equal("exFAT", d.FileSystem);
        Assert.Equal("MBR", d.PartitionType);
        Assert.Equal("/run/media/scott/CYANLABS", d.MountPath);
        Assert.True(d.CanSkipFormat);
    }

    [Fact]
    public void Linux_UnmountedDrive_HasNoMountPath()
    {
        var json = Lsblk.Replace("\"mountpoint\":\"/run/media/scott/CYANLABS\",", "");
        Assert.Equal("", LinuxDriveService.ParseLsblk(json).Single().MountPath);
    }

    [Fact]
    public void Windows_ParsesPowerShellJson()
    {
        var d = Assert.Single(WindowsDriveService.Parse("""[{"Number":2,"Name":"SanDisk","Size":32000000000,"Style":"MBR","Letter":"E","Fs":"exFAT","Label":"CYANLABS","Free":1,"Encrypted":false}]"""));
        Assert.Equal(@"E:\", d.MountPath); Assert.Equal("2", d.DeviceId); Assert.True(d.CanSkipFormat);
    }

    [Fact]
    public void Plist_Parses()
    {
        var p = (Dictionary<string, object?>)Plist.Parse("""<?xml version="1.0"?><plist version="1.0"><dict><key>A</key><string>x</string><key>B</key><array><integer>3</integer><true/></array></dict></plist>""")!;
        Assert.Equal("x", p["A"]); Assert.Equal(3L, ((List<object?>)p["B"]!)[0]);
    }

    [Fact] public void BytesFormat() { Assert.Equal("0B", Format.Bytes(0)); Assert.Equal("1KB", Format.Bytes(1024)); Assert.Equal("29.7GB", Format.Bytes(31914983424)); }
}

public class MiscTests
{
    [Fact]
    public void Localizer_LoadsAllEmbeddedLanguages_AndFallsBack()
    {
        Assert.True(Localizer.AvailableCodes.Count >= 20);
        var l = new Localizer();
        Assert.Equal("Home", l["Main.Home"]);
        l.SetLanguage("de");
        Assert.NotEqual("Main.Home", l["Main.Home"]);
        Assert.Equal("missing.key", l["missing.key"]);
        Assert.Equal("pt-br", Localizer.Resolve("pt-BR"));
        Assert.Equal("en-US", Localizer.Resolve("xx"));
        foreach (var c in Localizer.AvailableCodes) new Localizer().SetLanguage(c); // every file parses
    }

    [Fact]
    public void Api_EscapesQuotes()
    {
        Assert.Equal("a\\\"b\\\\c", ApiClient.Esc("a\"b\\c"));
    }

    [Fact]
    public async Task Api_QueriesAreWellFormed()
    {
        var calls = new List<string>();
        var http = new HttpClient(new Capture(calls));
        var api = new ApiClient(http, () => "tok", () => "");
        await api.GetReleases("EU");
        await api.GetReleaseIvsus("Sync \"3\"", "nav");
        await Assert.ThrowsAsync<ApiException>(() => api.GetSpecialPackage("reformat")); // empty mock data => not found
        await api.GetMapReleases("EU", "3.4");
        Assert.Equal(4, calls.Count);
        foreach (var c in calls)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(c);
            var q = doc.RootElement.GetProperty("query").GetString()!;
            Assert.Equal(q.Count(x => x == '{'), q.Count(x => x == '}'));   // balanced braces
            Assert.DoesNotContain("{{", q.Replace("{ {", ""));              // no leftover interpolation
        }
        string Decode(string body) { using var d = System.Text.Json.JsonDocument.Parse(body); return d.RootElement.GetProperty("query").GetString()!; }
        Assert.Contains("name: {_eq: \"Sync \\\"3\\\"\"}", Decode(calls[1]));   // embedded quotes are escaped, not injected
        Assert.Contains("regions: { _contains: \"EU\" }", Decode(calls[0]));
    }

    private class Capture(List<string> calls) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            calls.Add(await r.Content!.ReadAsStringAsync(ct));
            Assert.Equal("Bearer tok", r.Headers.Authorization!.ToString());
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"data\":{}}") };
        }
    }

    [Fact]
    public void Ivsu_FileNameFromUrl()
    {
        Assert.Equal("a.tar.gz", ApiIvsu.UrlToFileName("https://x/y/a.tar.gz?dl=1"));
        Assert.Equal("3.4.20282", ProfileSettings.FormatVersion(3420282));
        Assert.Equal("0.0.00000", ProfileSettings.FormatVersion(0));
    }

    [Fact]
    public void HexPackages()
    {
        // "AB" 00 "CD" -> AB, CD
        Assert.Equal(new[] { "AB", "CD" }, HexToAscii.ConvertPackages("4142004344"));
    }

    private const string Xml = """
    <?xml version="1.0" encoding="utf-8"?>
    <p:OTAModuleSnapShot xmlns:p="urn:p" xmlns:d2p1="urn:d">
      <p:VIN>1FAHP3F20CL000000</p:VIN>
      <p:Node><d2p1:ECUAcronym name="APIM"><d2p1:State><d2p1:Gateway>
        <d2p1:DID didType="Embedded Consumer Operating System Part Number" didValue="D133"><d2p1:Response>GB5T-14G381-AB</d2p1:Response></d2p1:DID>
        <d2p1:DID didType="ECU Delivery Assembly Number" didValue="F111"><d2p1:Response>JB5T-14G371-AF</d2p1:Response></d2p1:DID>
        <d2p1:DID didType="x" didValue="8060"><d2p1:Response>4142004344</d2p1:Response></d2p1:DID>
        <d2p1:DID didType="Direct Configuraation DID DE00" didValue="DE00"><d2p1:Response>aabb</d2p1:Response></d2p1:DID>
      </d2p1:Gateway></d2p1:State></d2p1:ECUAcronym>
      <d2p1:AdditionalAttributes logGeneratedDateTime="2024-01-02T03:04:05+00:00">
        <d2p1:PartitionHealth type="/fs/images/" total="28.5G" available="12.0G"/>
        <d2p1:PartitionHealth type="/fs/rwdata/" total="1G" available="1G"/>
      </d2p1:AdditionalAttributes></p:Node>
    </p:OTAModuleSnapShot>
    """;

    [Fact]
    public void Interrogator_ParsesLog()
    {
        var r = Interrogator.Parse(Xml);
        Assert.Equal("1FAHP3F20CL000000", r.Vin);
        Assert.Equal("JB5T-14G371-AF", r.ApimModel);
        Assert.Equal("GB5T-14G381-AB", r.SoftwareName);
        Assert.True(r.Nav); Assert.Equal(32, r.ApimSizeGb);
        Assert.Equal("12.0G", r.ApimFree);
        Assert.Equal(new[] { "AB", "CD" }, r.Packages);
        Assert.Equal("AABB", r.AsBuilt.Single().Response.ToUpperInvariant());
        Assert.Contains("AABB", r.AsBuiltXml!.ToString());
    }

    [Fact]
    public void Interrogator_RejectsGarbage()
    {
        Assert.Throws<InterrogatorException>(() => Interrogator.Parse("<nope/>"));
        Assert.Throws<InterrogatorException>(() => Interrogator.Parse("not xml"));
    }
}

public class FileServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("syn3test").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public async Task Md5_AndValidate_AndCopy()
    {
        var f = Path.Combine(_dir, "a.bin"); File.WriteAllBytes(f, new byte[] { 1, 2, 3, 4, 5 });
        var md5 = await FileService.Md5Async(f, null, default);
        Assert.Equal("7CFDD07889B3295D6A550914AB35E068", md5);
        var svc = new FileService(new HttpClient());
        Assert.True((await svc.ValidateAsync("", f, md5!.ToLowerInvariant(), false, null, default)).Ok);
        Assert.False((await svc.ValidateAsync("", f, "deadbeef", false, null, default)).Ok);
        var g = Path.Combine(_dir, "b.bin");
        Assert.True(await FileService.CopyAsync(f, g, new Progress<int>(), default));
        Assert.True((await svc.ValidateAsync(f, g, null, true, null, default)).Ok);
    }
}
