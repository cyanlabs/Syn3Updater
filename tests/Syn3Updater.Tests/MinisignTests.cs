using System.Text;
using Cyanlabs.Syn3Updater.Core.Services.Updates;
using Xunit;

namespace Syn3Updater.Tests;

public class MinisignTests
{
    private static string Fx(string name)
    {
        var relative = Path.Combine("tests", "Syn3Updater.Tests", "Fixtures", "Minisign", name);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        throw new FileNotFoundException(relative);
    }
    private static MinisignKey Key(string name) => MinisignKey.Parse(File.ReadAllText(Fx(name)));
    private static readonly MinisignKey[] Test = { Key("test.pub") };

    [Fact]
    public void Real_minisign_signature_verifies_and_returns_the_trusted_comment()
    {
        var r = Minisign.Verify(File.ReadAllBytes(Fx("message.json")), File.ReadAllText(Fx("message.json.minisig")), Test);
        Assert.True(r.Valid, r.Error);
        Assert.Equal("timestamp:1 file:message.json", r.TrustedComment);
    }

    [Fact]
    public void Legacy_non_prehashed_signature_verifies()
    {
        var r = Minisign.Verify(File.ReadAllBytes(Fx("message-legacy.json")), File.ReadAllText(Fx("message-legacy.json.minisig")), Test);
        Assert.True(r.Valid, r.Error);
    }

    [Fact]
    public void A_changed_file_is_rejected()
    {
        var bytes = File.ReadAllBytes(Fx("message.json")); bytes[5] ^= 1;
        Assert.False(Minisign.Verify(bytes, File.ReadAllText(Fx("message.json.minisig")), Test).Valid);
    }

    [Fact]
    public void A_changed_trusted_comment_is_rejected()
    {
        var sig = File.ReadAllText(Fx("message.json.minisig")).Replace("timestamp:1", "timestamp:2");
        var r = Minisign.Verify(File.ReadAllBytes(Fx("message.json")), sig, Test);
        Assert.False(r.Valid);
        Assert.Contains("trusted comment", r.Error);
    }

    [Fact]
    public void A_key_the_app_does_not_trust_is_rejected()
    {
        var r = Minisign.Verify(File.ReadAllBytes(Fx("message.json")), File.ReadAllText(Fx("message.json.minisig")), new[] { Key("other.pub") });
        Assert.False(r.Valid);
        Assert.Contains("does not trust", r.Error);
    }

    [Fact]
    public void The_real_release_keys_do_not_accept_the_test_signature()
    {
        Assert.False(Minisign.Verify(File.ReadAllBytes(Fx("message.json")), File.ReadAllText(Fx("message.json.minisig")), UpdateKeys.Trusted).Valid);
    }

    [Fact]
    public void Embedded_release_keys_parse_with_the_expected_ids()
    {
        Assert.Equal(2, UpdateKeys.Trusted.Count);
        Assert.Equal("637C5B0ED0BD86F6", UpdateKeys.Trusted[0].KeyIdHex);
        Assert.Equal("0FE81072367DAA78", UpdateKeys.Trusted[1].KeyIdHex);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("untrusted comment: x\n!!!notbase64!!!\ntrusted comment: y\nAAAA")]
    [InlineData("untrusted comment: x\nAAAA\nAAAA\nAAAA")]
    public void Malformed_signatures_fail_without_throwing(string sig)
    {
        Assert.False(Minisign.Verify(Encoding.UTF8.GetBytes("x"), sig, Test).Valid);
    }

    [Fact]
    public void Truncated_signature_fails_without_throwing()
    {
        var lines = File.ReadAllText(Fx("message.json.minisig")).Split('\n');
        Assert.False(Minisign.Verify(File.ReadAllBytes(Fx("message.json")), string.Join('\n', lines.Take(2)), Test).Valid);
    }

    [Fact]
    public void Public_key_parsing_rejects_junk()
    {
        Assert.Throws<FormatException>(() => MinisignKey.Parse("untrusted comment: x\nAAAA"));
        Assert.Throws<FormatException>(() => MinisignKey.Parse(""));
    }
}
