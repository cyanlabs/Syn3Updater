using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Cyanlabs.Syn3Updater.Core.Drives;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

/// <summary>
/// Every install route in docs/install-mode-logic.md ends in the files written to the USB drive, and a wrong list can brick an APIM. <c>InstallModeFlowTests</c> and <c>My20SafetyTests</c> prove the app
/// picks the right mode; these prove each mode then writes the right files:
/// <list type="number">
/// <item>Every route runs through the real <see cref="UpdateRunner"/> (copy, then write the lists) into a real folder, and each written file is compared byte for byte with a golden file in
/// <c>Fixtures/Lists</c> (CRLF, no trailing newline where the builder writes none).</item>
/// <item>Whatever a route writes must be consistent: every file a list names is on the drive, byte-identical to the download, and nothing else is there.</item>
/// <item>The builders are compared with a literal port of v2's list code (<c>DownloadViewModelService.CreateAutoInstallFile</c> and <c>DownloadViewModel.CreateReformat</c> on the <c>v2</c> branch) over
/// many generated queues, as <c>My20SafetyTests</c> does for the decision table.</item>
/// </list>
/// The golden files are written out by hand from the format, not copied from the app's output, except the two map ones where the split into install phases is the partitioner's job (those are pinned
/// and then checked against the v2 port).
/// </summary>
public class InstallRouteListTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("syn3routes").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private const string AppVersion = "3.0.0", Branch = "Stable", Region = "NA";

    // ---------------------------------------------------------------- the packages (their content is their file name, so every file is distinct and checkable)

    private static byte[] Content(string file) => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(file, 50)));
    private static Ivsu P(string type, string file, long? size = null, string? source = null) => new()
    {
        Type = type, Name = file, FileName = file, Url = "http://unused/" + file, Source = source,
        Md5 = Convert.ToHexString(MD5.HashData(Content(file))), FileSize = size ?? Content(file).Length,
    };
    private static Ivsu Rw => P("TOOL", "RWDATACLEANER.TAR.GZ");
    private static Ivsu Reformat => P("TOOL", "REFORMAT.TAR.GZ");
    private static Ivsu DowngradeApp => P("APP", "DOWNGRADEAPP.TAR.GZ");
    private static Ivsu DowngradeTool => P("TOOL", "DOWNGRADETOOL.TAR.GZ");

    private static string Golden(string name)
    {
        var relative = Path.Combine("tests", "Syn3Updater.Tests", "Fixtures", "Lists", name);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative)))
                return File.ReadAllText(Path.Combine(dir.FullName, relative)).Replace("\r\n", "\n").Replace("\n", "\r\n");   // stored as LF so git's line-ending settings cannot change them
        throw new FileNotFoundException(relative);
    }

    /// <summary>A route: what the chart chose, the queue Workflow builds for it (tools first, in <c>ToolPlan</c> order, then the ticked packages) and the golden files it must write.</summary>
    private sealed record Route(string Name, string Mode, UpdateAction Action, string Release, bool Forced, List<Ivsu> Queue, SpecialTools Tools, string[] Lists);

    private static Route Make(string name)
    {
        const string Rel = "Sync 3.4.20282";
        var apps = P("APPS", "APP.TAR.GZ"); var voice = P("VOICE", "VOICE.TAR.GZ");
        var rw = Rw; var re = Reformat; var da = DowngradeApp; var dt = DowngradeTool;
        SpecialTools T(bool reformat, bool downgrade) => new() { RwDataCleaner = rw, ReformatTool = reformat ? re : null, DowngradeApp = downgrade ? da : null, DowngradeTool = downgrade ? dt : null };

        // MAP_LICENSE, voice and four maps, sizes from a real European release. Sizes decide how the maps are spread over the three install phases.
        List<Ivsu> Maps() => new()
        {
            P("MAP_LICENSE", "4U5T-14G424-CD_1616778089000.TAR.GZ", 1734), P("VOICE_NAV", "4U5T-14G421-CAD_1615428059000.TAR.GZ", 90_000_000),
            P("MAP", "4U5T-14G421-CBD_1615428261000.TAR.GZ", 1_185_315_457), P("MAP", "4U5T-14G421-CCD_1615428361000.TAR.GZ", 1_115_467_399),
            P("MAP", "4U5T-14G421-CDD_1615428467000.TAR.GZ", 1_338_129_466), P("MAP", "4U5T-14G421-CFD_1615428639000.TAR.GZ", 1_113_931_334),
        };

        return name switch
        {
            // chart: 3.2+ / keep maps or non nav / Autoinstall (B, E)
            "autoinstall-nomaps" => new(name, InstallModes.Autoinstall, UpdateAction.Main, Rel, false, new() { rw, apps, voice }, T(false, false), new[] { "autoinstall.lst" }),
            // chart: 3.2+ / update maps / protected + ESN unlocked, or apps kept (C, F): maps use the three-phase list, RWDataCleaner first
            "autoinstall-maps" => new(name, InstallModes.Autoinstall, UpdateAction.Main, Rel, false, new List<Ivsu> { rw, apps }.Concat(Maps()).ToList(), T(false, false), new[] { "autoinstall.lst" }),
            "autoinstall-single-map" => new(name, InstallModes.Autoinstall, UpdateAction.Main, Rel, true, new() { rw, P("MAP", "ONLY-MAP.TAR.GZ", 2_000_000_000) }, T(false, false), new[] { "autoinstall.lst" }),
            // chart: below 3.2, or 3.2 up to 3.4.19273, not protected, update apps (A, D)
            "reformat" => new(name, InstallModes.Reformat, UpdateAction.Main, Rel, true, new() { re, rw, apps, voice }, T(true, false), new[] { "autoinstall.lst", "reformat.lst" }),
            // chart: 3.4.19274 or newer, not protected, update apps (G)
            "downgrade" => new(name, InstallModes.Downgrade, UpdateAction.Main, Rel, false, new() { da, dt, re, rw, apps, voice }, T(true, true), new[] { "autoinstall.lst", "reformat.lst" }),
            // the one release whose downgrade app is itself restored by the reformat
            "downgrade-3.3.19052" => new(name, InstallModes.Downgrade, UpdateAction.Main, "Sync 3.3.19052", false, new() { da, dt, re, rw, apps, voice }, T(true, true), new[] { "autoinstall.lst", "reformat.lst" }),
            // the utility actions (log utility and friends): one special package, no RWDataCleaner entry
            "utility-logutility" => new(name, InstallModes.Autoinstall, UpdateAction.LogUtility, Rel, false, new() { P("TOOL", "LOGUTILITY.TAR.GZ") }, T(false, false), new[] { "autoinstall.lst" }),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
    }

    public static IEnumerable<object[]> RouteNames() => new[]
    {
        "autoinstall-nomaps", "autoinstall-maps", "autoinstall-single-map", "reformat", "downgrade", "downgrade-3.3.19052", "utility-logutility",
    }.Select(n => new object[] { n });

    private async Task<(string Usb, UpdateRequest Req)> RunAsync(Route r)
    {
        var dl = Path.Combine(_root, r.Name, "dl"); Directory.CreateDirectory(dl);
        var usb = Path.Combine(_root, r.Name, "usb"); Directory.CreateDirectory(usb);
        foreach (var i in r.Queue) File.WriteAllBytes(Path.Combine(dl, i.FileName), Content(i.FileName));   // already downloaded and verified, so no server is needed
        var req = new UpdateRequest
        {
            Queue = r.Queue, Tools = r.Tools, Action = r.Action, InstallMode = r.Mode, Release = r.Release, Region = Region, ModeForced = r.Forced,
            Drive = new DriveInfoModel { IsFolder = true, MountPath = usb, Name = "folder" }, SkipFormat = true, DownloadDir = dl, AppVersion = AppVersion, Branch = Branch,
        };
        var runner = new UpdateRunner(DriveServiceFactory.Create(), new FileService(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))), new AppLog());
        Assert.True(await runner.RunAsync(req, new Progress<RunProgress>(), default), runner.LastError);
        return (usb, req);
    }

    // ---------------------------------------------------------------- 1. each route writes exactly the golden files

    [Theory]
    [MemberData(nameof(RouteNames))]
    public async Task Each_route_writes_the_golden_lists_to_the_drive(string name)
    {
        var r = Make(name); var (usb, _) = await RunAsync(r);
        foreach (var list in r.Lists)
            Assert.Equal(Golden($"{name}.{list}"), File.ReadAllText(Path.Combine(usb, list)));

        // an Autoinstall route must not leave a reformat.lst behind (the APIM would reformat itself), and every route marks the drive as not to be indexed
        if (!r.Lists.Contains("reformat.lst")) Assert.False(File.Exists(Path.Combine(usb, "reformat.lst")), "an autoinstall must not write reformat.lst");
        Assert.True(File.Exists(Path.Combine(usb, "DONTINDX.MSA")));
    }

    // ---------------------------------------------------------------- 2. what is written is consistent with what is on the drive

    [Theory]
    [MemberData(nameof(RouteNames))]
    public async Task Every_file_a_list_names_is_on_the_drive_unchanged_and_nothing_else_is(string name)
    {
        var r = Make(name); var (usb, _) = await RunAsync(r);
        var syncDir = Path.Combine(usb, "SyncMyRide");
        var onDrive = Directory.GetFiles(syncDir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        // the drive holds exactly the queue, byte for byte
        Assert.Equal(r.Queue.Select(i => i.FileName).OrderBy(n => n, StringComparer.Ordinal), onDrive);
        foreach (var i in r.Queue) Assert.Equal(Content(i.FileName), File.ReadAllBytes(Path.Combine(syncDir, i.FileName)));

        // every files autoinstall.lst opens exists, and the CRLF rule holds (the head unit cannot read bare LF)
        var auto = File.ReadAllText(Path.Combine(usb, "autoinstall.lst"));
        Assert.DoesNotContain("\n", auto.Replace("\r\n", "")); Assert.DoesNotContain("\r", auto.Replace("\r\n", ""));
        var opened = Regex.Matches(auto, @"^Open\d+ ?= SyncMyRide\\(.+)\r$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(opened);
        Assert.All(opened, f => Assert.Contains(f, onDrive));
        Assert.Equal(opened.Count, opened.Distinct().Count());                                              // no package is installed twice
        Assert.Equal(Regex.Matches(auto, @"^Item\d+ ", RegexOptions.Multiline).Count, opened.Count);        // every Item has its Open

        if (File.Exists(Path.Combine(usb, "reformat.lst")))
        {
            var lines = File.ReadAllText(Path.Combine(usb, "reformat.lst")).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.All(lines, l => Assert.Contains(l[(l.IndexOf('=') + 1)..], onDrive));
            // the reformat restores the packages; the tools that do the reformat are never restored, and nothing the autoinstall installs first is listed twice (bar the one 3.3.19052 exception)
            Assert.DoesNotContain(lines, l => l.EndsWith("RWDATACLEANER.TAR.GZ") || l.EndsWith("REFORMAT.TAR.GZ") || l.EndsWith("DOWNGRADETOOL.TAR.GZ"));
            if (r.Release != "Sync 3.3.19052") Assert.DoesNotContain(lines, l => l.EndsWith("DOWNGRADEAPP.TAR.GZ"));
        }
    }

    // ---------------------------------------------------------------- 3. the mode written is the mode that was asked for

    [Theory]
    [InlineData(InstallModes.Autoinstall, false)]
    [InlineData(InstallModes.Reformat, true)]
    [InlineData(InstallModes.Downgrade, true)]
    public async Task Only_reformat_and_downgrade_write_a_reformat_list(string mode, bool writesReformat)
    {
        var name = mode == InstallModes.Autoinstall ? "autoinstall-nomaps" : mode;
        var r = Make(name); var (usb, _) = await RunAsync(r);
        Assert.Equal(writesReformat, File.Exists(Path.Combine(usb, "reformat.lst")));
    }

    [Theory]
    [InlineData(UpdateAction.LogUtility)]
    [InlineData(UpdateAction.LogUtilityMy20)]
    [InlineData(UpdateAction.GracenotesRemoval)]
    [InlineData(UpdateAction.VoiceShrinker)]
    [InlineData(UpdateAction.Downgrade)]
    [InlineData(UpdateAction.Upgrade22)]
    public async Task A_utility_action_never_writes_a_reformat_list_even_if_the_profile_forces_reformat(UpdateAction action)
    {
        // the reformat branch only applies to the Main action: a utility must never reformat the APIM because of a leftover fixed mode
        var tool = P("TOOL", "UTILITY.TAR.GZ");
        var r = new Route("util", InstallModes.Reformat, action, "Sync 3.4.20282", true, new() { tool }, new SpecialTools { RwDataCleaner = Rw, ReformatTool = Reformat }, new[] { "autoinstall.lst" });
        var (usb, _) = await RunAsync(r);
        Assert.False(File.Exists(Path.Combine(usb, "reformat.lst")));
        var auto = File.ReadAllText(Path.Combine(usb, "autoinstall.lst"));
        Assert.DoesNotContain("REFORMAT", auto); Assert.DoesNotContain("RWDataCleaner", auto); Assert.Contains("Item1 = TOOL - UTILITY.TAR.GZ", auto);
    }

    [Fact]
    public async Task The_RW_data_cleaner_action_still_lists_the_cleaner()
    {
        var rw = Rw;
        var r = new Route("rwaction", InstallModes.Autoinstall, UpdateAction.RwDataCleaner, "Sync 3.4.20282", false, new() { rw }, new SpecialTools { RwDataCleaner = rw }, new[] { "autoinstall.lst" });
        var (usb, _) = await RunAsync(r);
        Assert.Equal("Item1 = RWDataCleaner TOOL - RWDATACLEANER.TAR.GZ", File.ReadAllText(Path.Combine(usb, "autoinstall.lst")).Split("\r\n")[3]);
    }

    // ---------------------------------------------------------------- builder edge cases the routes depend on

    [Fact]
    public void A_naviextras_bundle_is_never_listed_in_either_file_for_any_mode()
    {
        var rw = Rw; var re = Reformat; var da = DowngradeApp; var dt = DowngradeTool;
        var tools = new SpecialTools { RwDataCleaner = rw, ReformatTool = re, DowngradeApp = da, DowngradeTool = dt };
        var q = new List<Ivsu> { da, dt, re, rw, P("MAP", "BUNDLE.TAR.GZ", source: "naviextras"), P("APPS", "APP.TAR.GZ") };
        foreach (var mode in new[] { InstallModes.Reformat, InstallModes.Downgrade })
            Assert.DoesNotContain("BUNDLE", AutoInstallBuilder.CreateReformatList(q, mode, tools, "Sync 3.4"));
        Assert.DoesNotContain("BUNDLE", AutoInstallBuilder.CreateAutoInstall(q, tools, UpdateAction.Main, "r", Region, false, AppVersion, Branch));
    }

    [Fact]
    public void A_map_install_with_no_MAP_LICENSE_numbers_the_first_phase_after_the_cleaner_only()
    {
        var rw = Rw;
        var s = AutoInstallBuilder.CreateAutoInstall(new List<Ivsu> { rw, P("MAP", "M1", 3_000_000_000), P("MAP", "M2", 2_000_000_000), P("MAP", "M3", 1_000_000_000) },
            new SpecialTools { RwDataCleaner = rw }, UpdateAction.Main, "r", Region, false, AppVersion, Branch);
        var phase1 = s[s.IndexOf("[SYNCGen3.0_3.0.1_PRODUCT]", StringComparison.Ordinal)..s.IndexOf("[SYNCGen3.0_3.0.1]\r\n", StringComparison.Ordinal)];
        Assert.Contains("Item1 = RWDataCleaner TOOL", phase1); Assert.Contains("Item2 = MAP - ", phase1); Assert.DoesNotContain("MAP_LICENSE", phase1);
    }

    [Theory]
    [InlineData("Sync 3.3.19052", true)]
    [InlineData("Sync 3.4.20282", false)]
    [InlineData("Sync 3.3.19053", false)]
    public void Only_release_3_3_19052_restores_the_downgrade_app_in_reformat_lst(string release, bool listed)
    {
        var rw = Rw; var re = Reformat; var da = DowngradeApp; var dt = DowngradeTool;
        var tools = new SpecialTools { RwDataCleaner = rw, ReformatTool = re, DowngradeApp = da, DowngradeTool = dt };
        var lst = AutoInstallBuilder.CreateReformatList(new List<Ivsu> { da, dt, re, rw, P("APPS", "A") }, InstallModes.Downgrade, tools, release);
        Assert.Equal(listed, lst.Contains("APP=DOWNGRADEAPP.TAR.GZ"));
    }

    [Fact]
    public async Task An_unknown_mode_name_is_written_as_a_plain_autoinstall_never_a_reformat()
    {
        // v2's translated 'Auto-Detect' or a typo must not turn into a reformat of the APIM
        var r = Make("autoinstall-nomaps"); var odd = r with { Name = "odd-mode", Mode = "Auto-Detect" };
        var (usb, _) = await RunAsync(odd);
        Assert.False(File.Exists(Path.Combine(usb, "reformat.lst")));
        Assert.Equal(Golden("autoinstall-nomaps.autoinstall.lst"), File.ReadAllText(Path.Combine(usb, "autoinstall.lst")));
    }

    // ---------------------------------------------------------------- 4. the builders against v2's code

    /// <summary>A literal port of v2 <c>DownloadViewModelService.CreateAutoInstallFile</c>. The only change is the size unit: v2 cast bytes to uint (wrong over 4 GB), v3 passes KiB.</summary>
    private static string V2AutoInstall(List<Ivsu> all, Ivsu rw, UpdateAction action, string release, string region, bool forced)
    {
        var nl = "\r\n";
        var sb = new StringBuilder($"; CyanLabs Syn3Updater {AppVersion} {Branch} - Autoinstall {(forced ? "FORCED " : "")}Mode - {release} {region}{nl}{nl}");
        var ivsuList = all.Where(item => item.Source != "naviextras").ToList();
        ivsuList.Remove(rw);

        if (ivsuList.Any(i => i.Type == "MAP"))
        {
            sb.Append($"[SYNCGen3.0_3.0.1_PRODUCT]{nl}");
            sb.Append($"Item1 = RWDataCleaner TOOL - {rw.FileName}{nl}Open1 = SyncMyRide\\{rw.FileName}{nl}");
            var mapLicense = ivsuList.Find(i => i.Type == "MAP_LICENSE");
            if (mapLicense != null)
            {
                sb.Append($"Item2 = {mapLicense.Type} - {mapLicense.FileName}{nl}Open2 = SyncMyRide\\{mapLicense.FileName}{nl}");
                ivsuList.Remove(mapLicense);
            }
            var vals = ivsuList.ConvertAll(i => (uint)Math.Min(uint.MaxValue, Math.Max(1, i.FileSize / 1024)));
            var buckets = new effPartition(vals, 3);
            for (ushort i = 0; i < buckets.SubsetCount; i++)
            {
                for (int j = 0; j < buckets[i].NumbIDs.Count; j++)
                {
                    int subIndex = (int)buckets[i].NumbIDs[j]; int partIndex = j + 1; var item = ivsuList[subIndex - 1];
                    if (i == 0) partIndex++;
                    if (i == 0 && mapLicense != null) partIndex++;
                    sb.Append($"Item{partIndex} = {item.Type} - {item.FileName}{nl}Open{partIndex} = SyncMyRide\\{item.FileName}{nl}");
                }
                if (i == 0) sb.Append("Options = AutoInstall").Append(nl).Append(nl);
                if (i == 0 && ivsuList.Count > 1) sb.Append($"[SYNCGen3.0_3.0.1]{nl}");
                if (i == 1 && ivsuList.Count > 1) sb.Append("Options = AutoInstall, Include, Transaction").Append(nl).Append(nl);
                if (i == 1 && ivsuList.Count > 2) sb.Append($"[SYNCGen3.0_ALL]{nl}");
                if (i == 2 && ivsuList.Count > 2) sb.Append("Options = Delay, Include, Transaction").Append(nl);
            }
        }
        else
        {
            sb.Append($"[SYNCGen3.0_ALL_PRODUCT]{nl}");
            int current = 1;
            if (action is not (UpdateAction.LogUtility or UpdateAction.LogUtilityMy20 or UpdateAction.GracenotesRemoval or UpdateAction.VoiceShrinker or UpdateAction.Downgrade or UpdateAction.Upgrade22))
            {
                sb.Append($"Item{current} = RWDataCleaner TOOL - {rw.FileName}{nl}Open{current}= SyncMyRide\\{rw.FileName}{nl}");
                current = 2;
            }
            foreach (var item in ivsuList) { sb.Append($"Item{current} = {item.Type} - {item.FileName}{nl}Open{current} = SyncMyRide\\{item.FileName}{nl}"); current++; }
            sb.Append("Options = AutoInstall").Append(nl);
        }
        return sb.ToString();
    }

    /// <summary>A literal port of v2 <c>DownloadViewModel.CreateReformat</c> (both files).</summary>
    private static (string Reformat, string Auto) V2Reformat(List<Ivsu> all, string mode, SpecialTools t, string release, string region, bool forced)
    {
        var nl = "\r\n"; var reformatlst = ""; int i = 0;
        foreach (var item in all)
        {
            if (item.Source == "naviextras") continue;
            if (mode == "reformat") { if (item.Md5 == t.ReformatTool!.Md5 || item.Md5 == t.RwDataCleaner!.Md5) continue; }
            else if (mode == "downgrade")
            {
                if (item.Md5 == t.ReformatTool!.Md5 || item.Md5 == t.DowngradeApp!.Md5 && release != "Sync 3.3.19052" || item.Md5 == t.DowngradeTool!.Md5 || item.Md5 == t.RwDataCleaner!.Md5) continue;
            }
            i++;
            reformatlst += $"{item.Type}={item.FileName}";
            if (i != all.Count) reformatlst += nl;
        }
        var sb = new StringBuilder($"; CyanLabs Syn3Updater {AppVersion} {Branch} - {mode} {(forced ? "FORCED " : "")} Mode - {release} {region}{nl}{nl}[SYNCGen3.0_ALL_PRODUCT]{nl}");
        if (mode == "downgrade")
        {
            sb.Append($"Item1 = RWDataCleaner TOOL - {t.RwDataCleaner!.FileName}{nl}Open1= SyncMyRide\\{t.RwDataCleaner.FileName}{nl}");
            sb.Append($"Item2 = TOOL - {t.DowngradeTool!.FileName}{nl}Open2= SyncMyRide\\{t.DowngradeTool.FileName}{nl}");
            sb.Append($"Item3 = APP - {t.DowngradeApp!.FileName}{nl}Open3= SyncMyRide\\{t.DowngradeApp.FileName}{nl}");
            sb.Append($"Options = AutoInstall{nl}[SYNCGen3.0_ALL]{nl}");
            sb.Append($"Item1 = REFORMAT TOOL - {t.ReformatTool!.FileName}{nl}Open1= SyncMyRide\\{t.ReformatTool.FileName}{nl}");
            sb.Append("Options = AutoInstall,Include,Transaction").Append(nl);
        }
        else if (mode == "reformat")
        {
            sb.Append($"Item1 = RWDataCleaner TOOL - {t.RwDataCleaner!.FileName}{nl}Open1= SyncMyRide\\{t.RwDataCleaner.FileName}{nl}");
            sb.Append($"Item2 = REFORMAT TOOL  - {t.ReformatTool!.FileName}{nl}Open2= SyncMyRide\\{t.ReformatTool.FileName}{nl}");
            sb.Append("Options = AutoInstall");
        }
        return (reformatlst, sb.ToString());
    }

    private static List<Ivsu> RandomPackages(Random rnd, bool maps)
    {
        var list = new List<Ivsu>(); int n = 0;
        string Name(string t) => $"{t}-{++n:D3}-{rnd.Next(1000, 9999)}.TAR.GZ";
        if (rnd.Next(2) == 0) list.Add(P("APPS", Name("APPS"), rnd.NextInt64(1_000_000, 900_000_000)));
        if (rnd.Next(2) == 0) list.Add(P("VOICE", Name("VOICE"), rnd.NextInt64(1_000_000, 90_000_000)));
        if (maps)
        {
            if (rnd.Next(3) > 0) list.Add(P("MAP_LICENSE", Name("LIC"), rnd.NextInt64(1_000, 5_000)));
            if (rnd.Next(2) == 0) list.Add(P("VOICE_NAV", Name("VNAV"), rnd.NextInt64(1_000_000, 120_000_000)));
            for (int m = rnd.Next(1, 10); m > 0; m--) list.Add(P("MAP", Name("MAP"), rnd.NextInt64(50_000_000, 3_500_000_000)));
        }
        if (rnd.Next(4) == 0) list.Add(P("MAP", Name("NAVI"), rnd.NextInt64(1_000_000, 90_000_000), "naviextras"));
        return list.OrderBy(_ => rnd.Next()).ToList();
    }

    [Fact]
    public void Autoinstall_lists_match_v2_for_generated_queues_in_every_action_and_forced_state()
    {
        var rnd = new Random(20260101); int maps = 0, plain = 0;
        foreach (var action in Enum.GetValues<UpdateAction>())
            for (int round = 0; round < 40; round++)
            {
                bool withMaps = round % 2 == 0; var rw = Rw; var pk = RandomPackages(rnd, withMaps);
                var queue = new List<Ivsu> { rw }.Concat(pk).ToList();
                bool forced = round % 3 == 0; var release = round % 5 == 0 ? "Sync 3.3.19052" : "Sync 3.4.20282";
                var v3 = AutoInstallBuilder.CreateAutoInstall(queue, new SpecialTools { RwDataCleaner = rw }, action, release, Region, forced, AppVersion, Branch);
                Assert.Equal(V2AutoInstall(queue, rw, action, release, Region, forced), v3);
                if (queue.Any(i => i.Type == "MAP" && i.Source != "naviextras")) maps++; else plain++;
            }
        Assert.True(maps > 100 && plain > 100, $"both list shapes must be exercised (maps {maps}, plain {plain})");   // guards the generator
    }

    [Theory]
    [InlineData(InstallModes.Reformat, "Sync 3.4.20282", false)]
    [InlineData(InstallModes.Reformat, "Sync 3.3.19052", true)]
    [InlineData(InstallModes.Downgrade, "Sync 3.4.20282", false)]
    [InlineData(InstallModes.Downgrade, "Sync 3.3.19052", true)]
    public void Reformat_and_downgrade_lists_match_v2_for_generated_queues(string mode, string release, bool forced)
    {
        var rnd = new Random(mode.GetHashCode() ^ release.Length);
        for (int round = 0; round < 60; round++)
        {
            var rw = Rw; var re = Reformat; var da = DowngradeApp; var dt = DowngradeTool;
            var tools = new SpecialTools { RwDataCleaner = rw, ReformatTool = re, DowngradeApp = mode == InstallModes.Downgrade ? da : null, DowngradeTool = mode == InstallModes.Downgrade ? dt : null };
            var queue = (mode == InstallModes.Downgrade ? new List<Ivsu> { da, dt, re, rw } : new List<Ivsu> { re, rw }).Concat(RandomPackages(rnd, maps: round % 2 == 0)).ToList();
            var expected = V2Reformat(queue, mode, tools, release, Region, forced);
            Assert.Equal(expected.Reformat, AutoInstallBuilder.CreateReformatList(queue, mode, tools, release));
            Assert.Equal(expected.Auto, AutoInstallBuilder.CreateReformatAutoInstall(mode, tools, release, Region, forced, AppVersion, Branch));
        }
    }
}
