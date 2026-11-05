using System.Text;

namespace Cyanlabs.Syn3Updater.Core.Services;

public class AppLog
{
    private readonly List<string> _lines = new();
    private readonly object _lock = new();
    public event Action<string>? LineAdded;

    public void Info(string message) => Add("INFO", message);
    public void Debug(string message) => Add("DEBUG", message);
    public void Error(string message) => Add("ERROR", message);

    private void Add(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level}: {message}";
        lock (_lock) _lines.Add(line);
        LineAdded?.Invoke(line);
    }

    public IReadOnlyList<string> Lines { get { lock (_lock) return _lines.ToArray(); } }
    public string Text { get { lock (_lock) return string.Join(Environment.NewLine, _lines); } }

    public void Save(string file)
    {
        try { File.WriteAllText(file, Text, Encoding.UTF8); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
