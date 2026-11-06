using System;
using System.Runtime.InteropServices;

namespace Cyanlabs.Launcher
{
    /// <summary>
    ///     The real Windows build number. <c>Environment.OSVersion</c> lies on .NET Framework without a compatibility manifest
    ///     (it reports 6.2 on Windows 10), so ask the kernel directly.
    /// </summary>
    internal static class OsInfo
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OSVERSIONINFOEX
        {
            public int dwOSVersionInfoSize;
            public int dwMajorVersion;
            public int dwMinorVersion;
            public int dwBuildNumber;
            public int dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion;
            public ushort wServicePackMajor;
            public ushort wServicePackMinor;
            public ushort wSuiteMask;
            public byte wProductType;
            public byte wReserved;
        }

        [DllImport("ntdll.dll")]
        private static extern int RtlGetVersion(ref OSVERSIONINFOEX info);

        /// <returns>The build number (19045 for Windows 10 22H2), or 0 if it could not be read.</returns>
        public static int BuildNumber()
        {
            try
            {
                var info = new OSVERSIONINFOEX { dwOSVersionInfoSize = Marshal.SizeOf(typeof(OSVERSIONINFOEX)) };
                return RtlGetVersion(ref info) == 0 ? info.dwBuildNumber : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
