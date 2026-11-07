using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Cyanlabs.Syn3Updater.Core.Services.Updates;

/// <summary>A minisign public key: the 8-byte key id and the 32-byte Ed25519 key.</summary>
public sealed class MinisignKey
{
    public byte[] KeyId { get; }
    public byte[] PublicKey { get; }
    private MinisignKey(byte[] id, byte[] pk) { KeyId = id; PublicKey = pk; }

    /// <summary>Parses the text of a .pub file ("untrusted comment: ..." then the base64 line) or just the base64 line.</summary>
    public static MinisignKey Parse(string text)
    {
        var line = Minisign.LastNonCommentLine(text) ?? throw new FormatException("no public key found");
        var raw = Minisign.Base64(line);
        if (raw.Length != 42 || raw[0] != (byte)'E' || raw[1] != (byte)'d') throw new FormatException("not a minisign Ed25519 public key");
        return new MinisignKey(raw[2..10], raw[10..42]);
    }

    public string KeyIdHex => Convert.ToHexString(KeyId.Reverse().ToArray());     // minisign prints the id little-endian
}

public sealed record MinisignResult(bool Valid, string? TrustedComment, string? Error)
{
    public static MinisignResult Fail(string why) => new(false, null, why);
}

/// <summary>
/// Verifies minisign signatures (https://jedisct1.github.io/minisign/): the signature over the file, and the second signature that
/// covers the "trusted comment", so the comment cannot be altered either. Both the prehashed ("ED") and legacy ("Ed") forms are accepted.
/// </summary>
public static class Minisign
{
    /// <summary>True when <paramref name="signature"/> is a valid signature of <paramref name="message"/> by one of <paramref name="trusted"/>.</summary>
    public static MinisignResult Verify(byte[] message, string signature, IEnumerable<MinisignKey> trusted)
    {
        try
        {
            var lines = signature.Replace("\r", "").Split('\n');
            // line 0 untrusted comment, 1 signature, 2 "trusted comment: ...", 3 global signature
            if (lines.Length < 4 || !lines[0].StartsWith("untrusted comment:", StringComparison.Ordinal)) return MinisignResult.Fail("not a minisign signature");
            const string tc = "trusted comment: ";
            if (!lines[2].StartsWith(tc, StringComparison.Ordinal)) return MinisignResult.Fail("signature has no trusted comment");
            var sigBlock = Base64(lines[1]);
            var globalSig = Base64(lines[3]);
            if (sigBlock.Length != 74 || globalSig.Length != 64) return MinisignResult.Fail("malformed signature");

            var prehashed = sigBlock[0] == (byte)'E' && sigBlock[1] == (byte)'D';
            var legacy = sigBlock[0] == (byte)'E' && sigBlock[1] == (byte)'d';
            if (!prehashed && !legacy) return MinisignResult.Fail("unsupported signature algorithm");
            var keyId = sigBlock[2..10];
            var sig = sigBlock[10..74];
            var comment = Encoding.UTF8.GetBytes(lines[2][tc.Length..]);

            var key = trusted.FirstOrDefault(k => k.KeyId.AsSpan().SequenceEqual(keyId));
            if (key == null) return MinisignResult.Fail("signed by a key this app does not trust");

            var signed = prehashed ? Blake2b512(message) : message;
            if (!Ed25519.Verify(sig, 0, key.PublicKey, 0, signed, 0, signed.Length)) return MinisignResult.Fail("signature does not match the file");

            var global = new byte[sig.Length + comment.Length];
            sig.CopyTo(global, 0); comment.CopyTo(global, sig.Length);
            if (!Ed25519.Verify(globalSig, 0, key.PublicKey, 0, global, 0, global.Length)) return MinisignResult.Fail("trusted comment does not match the signature");
            return new MinisignResult(true, lines[2][tc.Length..], null);
        }
        catch (FormatException) { return MinisignResult.Fail("malformed signature"); }
    }

    private static byte[] Blake2b512(byte[] data)
    {
        var d = new Blake2bDigest(512);
        d.BlockUpdate(data, 0, data.Length);
        var o = new byte[64];
        d.DoFinal(o, 0);
        return o;
    }

    internal static byte[] Base64(string s) => Convert.FromBase64String(s.Trim());

    internal static string? LastNonCommentLine(string text) =>
        text.Replace("\r", "").Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && !l.StartsWith("untrusted comment:", StringComparison.Ordinal));
}

/// <summary>
/// The public keys releases are signed with. Two are trusted: the primary (its private half is a CI secret) and a backup whose private half is kept offline,
/// so the primary can be replaced by signing one release with the backup, without stranding installed copies.
/// </summary>
public static class UpdateKeys
{
    public const string Primary = "RWT2hr3QDlt8Y3G8EseipNEgC32eufOEz7XueQVyn6zn1MgJaYdo4kMN";      // key id 637C5B0ED0BD86F6
    public const string Backup = "RWR4qn02chDoD46rqERHlExaWKBvyz6zBMXymyHCCxG349dDjEkkvtfJ";        // key id 0FE81072367DAA78

    public static IReadOnlyList<MinisignKey> Trusted { get; } = new[] { MinisignKey.Parse(Primary), MinisignKey.Parse(Backup) };
}
