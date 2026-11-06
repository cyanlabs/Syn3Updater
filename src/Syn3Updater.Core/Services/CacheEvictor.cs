using System.Runtime.InteropServices;

namespace Cyanlabs.Syn3Updater.Core.Services;

/// <summary>
/// Best-effort: asks the OS not to serve a file from its page cache, so a checksum taken afterwards reads the bytes that are really on the
/// device. Linux uses posix_fadvise(DONTNEED) (valid once the data has been synced), macOS sets F_NOCACHE. Elsewhere it does nothing.
/// </summary>
public static class CacheEvictor
{
    [DllImport("libc", EntryPoint = "posix_fadvise")] private static extern int PosixFadvise(int fd, long offset, long length, int advice);
    [DllImport("libc", EntryPoint = "fcntl")] private static extern int Fcntl(int fd, int cmd, int arg);
    private const int PosixFadvDontNeed = 4;
    private const int FNoCache = 48;           // macOS <sys/fcntl.h>

    /// <summary>True when the OS accepted the request.</summary>
    public static bool Drop(FileStream file)
    {
        try
        {
            var fd = (int)file.SafeFileHandle.DangerousGetHandle();
            if (OperatingSystem.IsLinux()) return PosixFadvise(fd, 0, 0, PosixFadvDontNeed) == 0;
            if (OperatingSystem.IsMacOS()) return Fcntl(fd, FNoCache, 1) != -1;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or ObjectDisposedException) { }
        return false;
    }
}
