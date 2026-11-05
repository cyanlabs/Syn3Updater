using System.Text;
using System.Text.RegularExpressions;

namespace Cyanlabs.Syn3Updater.Core.Services;

public enum MdBlockKind { Paragraph, Heading, Bullet, Quote, Code, Rule }

public abstract record MdInline;
public sealed record MdText(string Text, bool Bold = false, bool Italic = false, bool Code = false) : MdInline;
public sealed record MdLink(string Text, string Url) : MdInline;
public sealed record MdBlock(MdBlockKind Kind, IReadOnlyList<MdInline> Inlines, int Level = 0);

/// <summary>
/// Small Markdown subset used for changelogs, notices and release notes: headings, bullets, quotes, fenced code,
/// **bold**, *italic*, `code`, [links](url) and bare URLs. Single newlines are kept as separate lines
/// (changelogs are written one entry per line), so the output reads the same as the raw text.
/// </summary>
public static class Markdown
{
    private static readonly Regex Heading = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^\s*(?:[-*+]|\d+[.)])\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Rule = new(@"^\s*([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);
    private static readonly Regex BareUrl = new(@"https?://[^\s<>()\[\]]+", RegexOptions.Compiled);

    public static List<MdBlock> Parse(string? markdown)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        bool inCode = false; var code = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```"))
            {
                if (inCode) { blocks.Add(new(MdBlockKind.Code, new MdInline[] { new MdText(code.ToString().TrimEnd('\n'), Code: true) })); code.Clear(); }
                inCode = !inCode; continue;
            }
            if (inCode) { code.Append(raw).Append('\n'); continue; }
            if (line.Length == 0) continue;

            if (Rule.IsMatch(line)) { blocks.Add(new(MdBlockKind.Rule, Array.Empty<MdInline>())); continue; }
            if (Heading.Match(line) is { Success: true } h) { blocks.Add(new(MdBlockKind.Heading, Inline(h.Groups[2].Value), h.Groups[1].Length)); continue; }
            if (line.TrimStart().StartsWith('>')) { blocks.Add(new(MdBlockKind.Quote, Inline(line.TrimStart()[1..].TrimStart()))); continue; }
            if (Bullet.Match(line) is { Success: true } b) { blocks.Add(new(MdBlockKind.Bullet, Inline(b.Groups[1].Value))); continue; }
            blocks.Add(new(MdBlockKind.Paragraph, Inline(line)));
        }
        if (inCode && code.Length > 0) blocks.Add(new(MdBlockKind.Code, new MdInline[] { new MdText(code.ToString().TrimEnd('\n'), Code: true) }));
        return blocks;
    }

    public static List<MdInline> Inline(string s)
    {
        var result = new List<MdInline>();
        var plain = new StringBuilder();
        void Flush() { if (plain.Length > 0) { AddWithUrls(result, plain.ToString()); plain.Clear(); } }

        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length && "\\`*_[]()#>-".Contains(s[i + 1])) { plain.Append(s[i + 1]); i += 2; continue; }

            if (c == '`' && s.IndexOf('`', i + 1) is var end && end > i)
            { Flush(); result.Add(new MdText(s[(i + 1)..end], Code: true)); i = end + 1; continue; }

            if (c == '[' && TryLink(s, i, out var label, out var url, out var next))
            { Flush(); result.Add(new MdLink(label, url)); i = next; continue; }

            if ((c == '*' || c == '_') && i + 1 < s.Length && s[i + 1] == c && Close(s, i + 2, new string(c, 2)) is var e2 && e2 > i + 2)
            { Flush(); result.AddRange(Inline(s[(i + 2)..e2]).Select(x => x is MdText t ? t with { Bold = true } : x)); i = e2 + 2; continue; }

            if ((c == '*' || c == '_') && i + 1 < s.Length && !char.IsWhiteSpace(s[i + 1]) && Close(s, i + 1, c.ToString()) is var e1 && e1 > i + 1
                && (c == '*' || i == 0 || !char.IsLetterOrDigit(s[i - 1])))   // snake_case words are not italic
            { Flush(); result.AddRange(Inline(s[(i + 1)..e1]).Select(x => x is MdText t ? t with { Italic = true } : x)); i = e1 + 1; continue; }

            plain.Append(c); i++;
        }
        Flush();
        return result;
    }

    private static int Close(string s, int from, string marker)
    {
        int idx = s.IndexOf(marker, from, StringComparison.Ordinal);
        return idx;
    }

    private static bool TryLink(string s, int start, out string label, out string url, out int next)
    {
        label = url = ""; next = start;
        int close = s.IndexOf(']', start + 1);
        if (close < 0 || close + 1 >= s.Length || s[close + 1] != '(') return false;
        int paren = s.IndexOf(')', close + 2);
        if (paren < 0) return false;
        label = s[(start + 1)..close]; url = s[(close + 2)..paren].Trim();
        if (!IsSafeUrl(url)) return false;
        next = paren + 1; return true;
    }

    /// <summary>Only web and mail links are ever opened; anything else (file:, javascript:, custom schemes) is left as plain text.</summary>
    public static bool IsSafeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https" or "mailto");

    private static void AddWithUrls(List<MdInline> into, string text)
    {
        int pos = 0;
        foreach (Match m in BareUrl.Matches(text))
        {
            var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', '*', '_');
            if (m.Index > pos) into.Add(new MdText(text[pos..m.Index]));
            into.Add(new MdLink(url, url));
            pos = m.Index + url.Length;
        }
        if (pos < text.Length) into.Add(new MdText(text[pos..]));
    }
}
