using System.Globalization;
using System.Text.RegularExpressions;

namespace Cyanlabs.Syn3Updater.Core.Services;

public enum LogLevel { Info, Debug, Success, Warning, Error }
public enum LogKind { Session, UpdateRun, Interrogator, Other }

public record LogFileInfo(LogKind Kind, string Title);

/// <summary>Classifies log lines (for colouring/filtering) and turns log file names into friendly titles.</summary>
public static class LogParser
{
    private static readonly Regex RunName = new(@"^log-(\d{4})-(\d{2})-(\d{2})-(\d{2})-(\d{2})-(\d{2})\.txt$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static LogLevel Classify(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return LogLevel.Info;
        if (line.Contains("ERROR", StringComparison.Ordinal) || line.Contains("ABORTING", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed", StringComparison.OrdinalIgnoreCase)) return LogLevel.Error;
        if (line.Contains("WARN", StringComparison.Ordinal) || line.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Unable", StringComparison.OrdinalIgnoreCase) || line.Contains("attempt #", StringComparison.OrdinalIgnoreCase)) return LogLevel.Warning;
        if (line.Contains("SUCCESSFULLY", StringComparison.Ordinal) || line.Contains("Completed", StringComparison.Ordinal)
            || line.Contains("Validated:", StringComparison.Ordinal) || line.Contains("Downloaded:", StringComparison.Ordinal)
            || line.Contains("Copied:", StringComparison.Ordinal)) return LogLevel.Success;
        if (line.Contains(" DEBUG:", StringComparison.Ordinal)) return LogLevel.Debug;
        return LogLevel.Info;
    }

    public static LogFileInfo Describe(string fileName)
    {
        if (RunName.Match(fileName) is { Success: true } m &&
            DateTime.TryParse($"{m.Groups[1]}-{m.Groups[2]}-{m.Groups[3]} {m.Groups[4]}:{m.Groups[5]}:{m.Groups[6]}", CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
            return new(LogKind.UpdateRun, $"Update run - {when:d MMM, HH:mm}");
        if (fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return new(LogKind.Interrogator, $"Interrogator - {fileName}");
        return new(LogKind.Other, fileName);
    }

    public static string FileSize(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / 1024.0 / 1024.0:0.#} MB";
}
