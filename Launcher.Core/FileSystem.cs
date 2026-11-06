using System;
using System.IO;
using System.Threading;

namespace Cyanlabs.Launcher.Core
{
    /// <summary>The file operations the installer performs, behind an interface so tests can make any of them fail.</summary>
    public interface IFileSystem
    {
        bool FileExists(string path);
        bool DirectoryExists(string path);
        void CreateDirectory(string path);
        /// <summary>Moves a file, replacing the destination when <paramref name="overwrite"/> is set.</summary>
        void MoveFile(string source, string destination, bool overwrite);
        void DeleteFile(string path);
        void DeleteDirectory(string path);
    }

    public sealed class RealFileSystem : IFileSystem
    {
        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public void MoveFile(string source, string destination, bool overwrite)
        {
            if (overwrite && File.Exists(destination)) File.Delete(destination);
            File.Move(source, destination);
        }

        public void DeleteFile(string path) => File.Delete(path);
        public void DeleteDirectory(string path) => Directory.Delete(path, true);
    }

    /// <summary>Files can be briefly locked (antivirus scans, an app still closing). Retry those before giving up.</summary>
    public sealed class RetryPolicy
    {
        public static readonly RetryPolicy Default = new RetryPolicy(10, 500);

        public RetryPolicy(int attempts, int delayMs)
        {
            Attempts = Math.Max(1, attempts); DelayMs = Math.Max(0, delayMs);
        }

        public int Attempts { get; }
        public int DelayMs { get; }

        public void Run(Action action)
        {
            for (var attempt = 1; ; attempt++)
            {
                try { action(); return; }
                catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && attempt < Attempts)
                {
                    if (DelayMs > 0) Thread.Sleep(DelayMs);
                }
            }
        }
    }
}
