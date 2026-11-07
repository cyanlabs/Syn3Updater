using System.Net;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;
using Cyanlabs.Syn3Updater.Core.Services.Updates;
using Xunit;
using static Syn3Updater.Tests.UpdateTestSupport;

namespace Syn3Updater.Tests;

public class UpdatePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static UpdatePolicy Policy(params PolicyRow[] rows) => new(Now, rows);
    private static PolicyRow Row(UpdateChannel c = UpdateChannel.Stable, bool on = true, string? all = null, string? win = null, string? lin = null, string? mac = null, string? msg = null) => new(c, on, all, win, lin, mac, msg);

    // ---- reading the backend's answer ----

    [Fact]
    public void The_graphql_answer_becomes_rows_and_unknown_channels_are_ignored()
    {
        var root = new UpdatePolicyRoot { Rows = new()
        {
            new UpdatePolicyDto { Channel = "Stable", Enabled = true, MinVersionAll = "3.0.2", MinVersionWindows = "3.0.4", Message = "Security fix" },
            new UpdatePolicyDto { Channel = "Beta", Enabled = false }, new UpdatePolicyDto { Channel = "Gamma", Enabled = true, MinVersionAll = "9.9.9" }, new UpdatePolicyDto { Channel = null }
        } };
        var p = UpdatePolicy.FromApi(root, Now);
        Assert.Equal(new[] { UpdateChannel.Stable, UpdateChannel.Beta }, p.Rows.Select(r => r.Channel));
        Assert.Equal("Security fix", p.Rows[0].Message);
        Assert.Empty(UpdatePolicy.FromApi(new UpdatePolicyRoot(), Now).Rows);
    }

    [Fact]
    public async Task The_real_query_shape_is_read_from_a_backend_answer()
    {
        const string json = """{"data":{"update_policy":[{"channel":"Stable","enabled":true,"min_version_all":"3.0.2","min_version_windows":null,"min_version_linux":null,"min_version_macos":null,"message":null},{"channel":"Beta","enabled":false,"min_version_all":null,"min_version_windows":null,"min_version_linux":null,"min_version_macos":null,"message":null}]}}""";
        var api = new ApiClient(new HttpClient(new FuncHandler(r =>
        {
            Assert.Equal("https://syn3updater.cyanlabs.net/graphql", r.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        })), () => "token", () => "");
        var p = UpdatePolicy.FromApi(await api.GetUpdatePolicy(), Now);
        Assert.Equal("3.0.2", p.MinimumFor(UpdateChannel.Stable, UpdateOs.Linux)!.Version);
        Assert.Null(p.MinimumFor(UpdateChannel.Beta, UpdateOs.Linux));
    }

    // ---- the minimum ----

    [Fact]
    public void The_higher_of_the_all_OS_value_and_the_OS_value_applies()
    {
        var p = Policy(Row(all: "3.0.2", win: "3.0.5", lin: "3.0.1"));
        Assert.Equal("3.0.5", p.MinimumFor(UpdateChannel.Stable, UpdateOs.Windows)!.Version);
        Assert.Equal("3.0.2", p.MinimumFor(UpdateChannel.Stable, UpdateOs.Linux)!.Version);          // the OS value is lower than the all-OS one
        Assert.Equal("3.0.2", p.MinimumFor(UpdateChannel.Stable, UpdateOs.MacOs)!.Version);
    }

    [Fact]
    public void An_OS_value_alone_applies_only_to_that_OS()
    {
        var p = Policy(Row(mac: "3.0.9"));
        Assert.Equal("3.0.9", p.MinimumFor(UpdateChannel.Stable, UpdateOs.MacOs)!.Version);
        Assert.Null(p.MinimumFor(UpdateChannel.Stable, UpdateOs.Windows)); Assert.Null(p.MinimumFor(UpdateChannel.Stable, UpdateOs.Linux));
    }

    [Fact]
    public void Only_the_row_of_the_channel_applies_and_a_disabled_row_says_nothing()
    {
        var p = Policy(Row(UpdateChannel.Stable, all: "3.0.2"), Row(UpdateChannel.Beta, on: false, all: "3.1.0-beta.5"), Row(UpdateChannel.Nightly, all: "3.1.0-nightly.20261108.3"));
        Assert.Equal("3.0.2", p.MinimumFor(UpdateChannel.Stable, UpdateOs.Linux)!.Version);
        Assert.Null(p.MinimumFor(UpdateChannel.Beta, UpdateOs.Linux));                               // switched off
        Assert.Equal("3.1.0-nightly.20261108.3", p.MinimumFor(UpdateChannel.Nightly, UpdateOs.Linux)!.Version);
    }

    [Theory]
    [InlineData("3,0.0")] [InlineData("abc")] [InlineData("")] [InlineData("   ")] [InlineData(null)]
    public void Values_that_are_not_versions_are_ignored(string? bad) => Assert.Null(Policy(Row(all: bad, win: bad)).MinimumFor(UpdateChannel.Stable, UpdateOs.Windows));

    [Fact]
    public void A_bad_value_does_not_hide_a_good_one_and_the_message_is_trimmed()
    {
        var r = Policy(Row(all: "oops", win: "3.0.4", msg: "  Please update.  ")).MinimumFor(UpdateChannel.Stable, UpdateOs.Windows)!;
        Assert.Equal("3.0.4", r.Version); Assert.Equal("Please update.", r.Message);
        Assert.Null(Policy(Row(all: "3.0.4", msg: "   ")).MinimumFor(UpdateChannel.Stable, UpdateOs.Linux)!.Message);
    }

    [Fact]
    public void Prerelease_minimums_compare_like_versions() =>
        Assert.Equal("3.1.0-beta.10", Policy(Row(UpdateChannel.Beta, all: "3.1.0-beta.2", win: "3.1.0-beta.10")).MinimumFor(UpdateChannel.Beta, UpdateOs.Windows)!.Version);

    // ---- deciding ----

    private static ForceDecision Decide(UpdatePolicy? p, string running = "3.0.0", string? newest = "3.0.4", UpdateChannel ch = UpdateChannel.Stable, UpdateOs os = UpdateOs.Linux) => ForcedUpdate.Evaluate(p, running, ch, os, newest);

    [Fact] public void Below_the_minimum_with_a_release_that_meets_it_is_forced() { var d = Decide(Policy(Row(all: "3.0.2", msg: "Fixes a bug")), "3.0.0", "3.0.4"); Assert.True(d.Required); Assert.Equal("Fixes a bug", d.Requirement!.Message); }
    [Fact] public void Meeting_the_minimum_is_not_forced() { Assert.False(Decide(Policy(Row(all: "3.0.2")), "3.0.2").Required); Assert.False(Decide(Policy(Row(all: "3.0.2")), "3.1.0").Required); }
    [Fact] public void No_policy_or_no_minimum_forces_nothing() { Assert.False(Decide(null).Required); Assert.False(Decide(Policy()).Required); Assert.False(Decide(Policy(Row(on: false, all: "9.9.9"))).Required); }
    [Fact] public void A_minimum_above_the_newest_release_is_ignored_so_a_typo_cannot_lock_everyone_out() { var d = Decide(Policy(Row(all: "99.0.0")), "3.0.0", "3.0.4"); Assert.False(d.Required); Assert.Contains("ignored", d.Reason); }
    [Fact] public void With_no_known_release_nothing_is_forced() { Assert.False(Decide(Policy(Row(all: "3.0.2")), newest: null).Required); Assert.False(Decide(Policy(Row(all: "3.0.2")), newest: "junk").Required); }
    [Fact] public void A_running_version_that_is_not_a_version_is_never_forced() => Assert.False(Decide(Policy(Row(all: "3.0.2")), "dev-build").Required);
    [Fact] public void A_release_exactly_at_the_minimum_is_enough() => Assert.True(Decide(Policy(Row(all: "3.0.2")), "3.0.0", "3.0.2").Required);
    [Fact] public void A_test_build_is_below_the_release_of_the_same_number() => Assert.True(Decide(Policy(Row(all: "3.0.0")), "3.0.0-test9", "3.0.0").Required);
    [Fact] public void Beta_and_nightly_builds_are_forced_by_their_own_rows() { Assert.True(Decide(Policy(Row(UpdateChannel.Beta, all: "3.1.0-beta.3")), "3.1.0-beta.1", "3.1.0-beta.4", UpdateChannel.Beta).Required); Assert.False(Decide(Policy(Row(UpdateChannel.Stable, all: "3.1.0-beta.3")), "3.1.0-beta.1", "3.1.0-beta.4", UpdateChannel.Beta).Required); }
    [Fact] public void The_OS_decides() { var p = Policy(Row(win: "3.0.2")); Assert.True(Decide(p, os: UpdateOs.Windows).Required); Assert.False(Decide(p, os: UpdateOs.Linux).Required); }

    // ---- the live fetch ----

    [Fact]
    public async Task A_live_policy_is_returned()
    {
        var live = Policy(Row(all: "3.0.2"));
        Assert.Same(live, await PolicySource.GetAsync(_ => Task.FromResult(live), TimeSpan.FromSeconds(5)));
    }

    public static IEnumerable<object[]> Failures() => new[]
    {
        new object[] { new Func<Exception>(() => new HttpRequestException("x")) }, new object[] { new Func<Exception>(() => new TaskCanceledException("timeout")) },
        new object[] { new Func<Exception>(() => new ApiException("api")) }, new object[] { new Func<Exception>(() => new System.Text.Json.JsonException("bad")) }
    };

    [Theory, MemberData(nameof(Failures))]
    public async Task Every_kind_of_failure_means_no_policy_and_so_nothing_is_forced(Func<Exception> failure)
    {
        var logs = new List<string>();
        var got = await PolicySource.GetAsync(_ => throw failure(), TimeSpan.FromSeconds(5), logs.Add);
        Assert.Null(got); Assert.Contains(logs, l => l.Contains("nothing is forced"));
        Assert.False(ForcedUpdate.Evaluate(got, "3.0.0", UpdateChannel.Stable, UpdateOs.Linux, "3.0.4").Required);
    }

    [Fact]
    public async Task A_slow_backend_is_given_up_on_after_the_timeout()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await PolicySource.GetAsync(async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return Policy(); }, TimeSpan.FromMilliseconds(150)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task The_caller_giving_up_is_not_hidden_as_a_network_failure()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PolicySource.GetAsync(async ct => { await Task.Delay(1000, ct); return Policy(); }, TimeSpan.FromSeconds(5), null, cts.Token));
    }
}
