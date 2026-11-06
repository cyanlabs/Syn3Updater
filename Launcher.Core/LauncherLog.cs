using System;
using System.IO;

namespace Cyanlabs.Launcher.Core
{
    /// <summary>A small append-only log so a failed update leaves something to look at. Never throws.</summary>
    public sealed class LauncherLog
    {
        private static readonly object Gate = new object();
        private readonly string _path;

        public LauncherLog(string path)
        {
            _path = path;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
            }
            catch { /* logging is optional */ }
        }

        public void Write(string message)
        {
            try { lock (Gate) File.AppendAllText(_path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine); }
            catch { /* logging is optional */ }
        }
    }
}
