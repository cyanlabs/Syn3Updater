using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace Cyanlabs.Syn3Updater.Core.Services.Updates.Windows;

/// <summary>Reads and writes .lnk files through the shell's own IShellLink, which is the only supported way to change a shortcut without breaking what Explorer keeps in it.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsShortcutStore : IShortcutStore
{
    private readonly IReadOnlyList<string> _folders;

    /// <param name="folders">Folders to search (recursively, two levels deep); null = the real Desktop, Start Menu and taskbar of everyone and of the current user.</param>
    public WindowsShortcutStore(IReadOnlyList<string>? folders = null) => _folders = folders ?? DefaultFolders();

    public static IReadOnlyList<string> DefaultFolders()
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var roaming = F(Environment.SpecialFolder.ApplicationData);
        return new[]
        {
            F(Environment.SpecialFolder.CommonDesktopDirectory), F(Environment.SpecialFolder.DesktopDirectory),
            F(Environment.SpecialFolder.CommonPrograms), F(Environment.SpecialFolder.Programs),
            Path.Combine(roaming, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),
            Path.Combine(roaming, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\StartMenu")
        }.Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IEnumerable<string> FindShortcuts()
    {
        foreach (var root in _folders)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var f in Walk(root, 2)) yield return f;
        }
    }

    private static IEnumerable<string> Walk(string dir, int depth)
    {
        string[] files = Array.Empty<string>(), subs = Array.Empty<string>();
        try { files = Directory.GetFiles(dir, "*.lnk"); if (depth > 0) subs = Directory.GetDirectories(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        foreach (var f in files) yield return f;
        foreach (var s in subs) foreach (var f in Walk(s, depth - 1)) yield return f;
    }

    public ShortcutInfo? Read(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);                                       // STGM_READ
            var target = new StringBuilder(1024); link.GetPath(target, target.Capacity, IntPtr.Zero, 4 /* SLGP_RAWPATH */);
            var args = new StringBuilder(2048); link.GetArguments(args, args.Capacity);
            var dir = new StringBuilder(1024); link.GetWorkingDirectory(dir, dir.Capacity);
            var desc = new StringBuilder(1024); link.GetDescription(desc, desc.Capacity);
            var icon = new StringBuilder(1024); link.GetIconLocation(icon, icon.Capacity, out var index);
            return new ShortcutInfo(target.ToString(), args.ToString(), dir.ToString(), desc.ToString(), icon.ToString(), index);
        }
        catch (COMException) { return null; }                                          // not a shortcut we can read: leave it alone
        finally { Marshal.FinalReleaseComObject(link); }
    }

    /// <summary>Makes a new shortcut (used by tests and nothing else: the migration only edits shortcuts that already exist).</summary>
    internal void Create(string path, ShortcutInfo info)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(info.Target); link.SetArguments(info.Arguments); link.SetWorkingDirectory(info.WorkingDirectory);
            link.SetDescription(info.Description); if (info.IconPath.Length > 0) link.SetIconLocation(info.IconPath, info.IconIndex);
            ((IPersistFile)link).Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    public void Write(string path, ShortcutInfo info)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 2);                                       // STGM_READWRITE: keep everything we do not set (window state, hotkey, ...)
            link.SetPath(info.Target); link.SetArguments(info.Arguments); link.SetWorkingDirectory(info.WorkingDirectory);
            link.SetDescription(info.Description); link.SetIconLocation(info.IconPath, info.IconIndex);
            ((IPersistFile)link).Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }
}

/// <summary>The machine-wide syn3updater:// handler. The v2 installer wrote it under HKCR, which is HKLM\Software\Classes; both registry views are checked.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProtocolStore : IProtocolStore
{
    private readonly RegistryKey _root; private readonly string _commandPath;

    /// <param name="root">Registry root (default HKEY_LOCAL_MACHINE); tests pass a throw-away key.</param>
    /// <param name="commandPath">Path of the command key below it.</param>
    public WindowsProtocolStore(RegistryKey? root = null, string commandPath = @"Software\Classes\syn3updater\shell\open\command")
    {
        _root = root ?? Registry.LocalMachine; _commandPath = commandPath;
    }

    private IEnumerable<RegistryKey> Bases() =>
        _root == Registry.LocalMachine || _root == Registry.CurrentUser
            ? new[] { RegistryKey.OpenBaseKey(_root == Registry.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64),
                      RegistryKey.OpenBaseKey(_root == Registry.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry32) }
            : new[] { _root };

    public string? GetOpenCommand()
    {
        foreach (var b in Bases()) using (b) using (var k = b.OpenSubKey(_commandPath)) if (k?.GetValue(null) as string is { Length: > 0 } v) return v;
        return null;
    }

    public void SetOpenCommand(string command)
    {
        foreach (var b in Bases())
            using (b)
            {
                using var existing = b.OpenSubKey(_commandPath);
                if (existing == null) continue;                                           // only views that already have the entry
                using var k = b.CreateSubKey(_commandPath, true); k.SetValue(null, command);
            }
    }
}
