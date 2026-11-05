using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Cyanlabs.Syn3Updater.Core.Services;

/// <summary>Loads the language JSON files embedded in the assembly (English fallback) and looks up keys.</summary>
public class Localizer
{
    private Dictionary<string, string> _current = new();
    private readonly Dictionary<string, string> _english;
    public string Code { get; private set; } = "en-US";
    public event Action? LanguageChanged;

    public Localizer()
    {
        _english = Load("en-US");
        _current = _english;
    }

    public static IReadOnlyList<string> AvailableCodes { get; } = typeof(Localizer).Assembly
        .GetManifestResourceNames()
        .Where(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && n.Contains(".Languages."))
        .Select(n => n[(n.IndexOf(".Languages.", StringComparison.Ordinal) + ".Languages.".Length)..^".json".Length])
        .Order().ToList();

    public static string? NativeName(string code)
    {
        try { return new CultureInfo(code).NativeName; } catch (CultureNotFoundException) { return code; }
    }

    /// <summary>Picks the best match for a code like "de", "de-DE" or "pt-br"; falls back to English.</summary>
    public static string Resolve(string? wanted)
    {
        if (string.IsNullOrWhiteSpace(wanted)) wanted = CultureInfo.CurrentUICulture.Name;
        var exact = AvailableCodes.FirstOrDefault(c => c.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        var two = wanted.Split('-')[0];
        return AvailableCodes.FirstOrDefault(c => c.Equals(two, StringComparison.OrdinalIgnoreCase))
               ?? AvailableCodes.FirstOrDefault(c => c.StartsWith(two + "-", StringComparison.OrdinalIgnoreCase))
               ?? "en-US";
    }

    public void SetLanguage(string? code)
    {
        Code = Resolve(code);
        _current = Code == "en-US" ? _english : Load(Code);
        LanguageChanged?.Invoke();
    }

    public string this[string key] => _current.TryGetValue(key, out var v) && v != ""
        ? v
        : _english.TryGetValue(key, out var e) ? e : key;

    public string Format(string key, params object[] args) => string.Format(this[key], args);

    private static Dictionary<string, string> Load(string code)
    {
        var asm = Assembly.GetExecutingAssembly();
        var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith($".Languages.{code}.json", StringComparison.OrdinalIgnoreCase));
        if (res == null) return new();
        using var s = asm.GetManifestResourceStream(res)!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
    }
}
