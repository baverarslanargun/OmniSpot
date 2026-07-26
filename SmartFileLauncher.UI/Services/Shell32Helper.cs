using System;
using System.Runtime.InteropServices;

namespace SmartFileLauncher.UI.Services;

/// <summary>
/// Windows Shell32 API helper for file operations
/// </summary>
public static class Shell32Helper
{
    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string lpVerb;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string lpFile;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string? lpParameters;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    private const uint SEE_MASK_INVOKEIDLIST = 12;
    private const int SW_SHOW = 5;

    /// <summary>
    /// Shows the Windows properties dialog for a file or folder
    /// </summary>
    public static void ShowProperties(string path)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO)),
            lpVerb = "properties",
            lpFile = path,
            nShow = SW_SHOW,
            fMask = SEE_MASK_INVOKEIDLIST
        };
        
        ShellExecuteEx(ref info);
    }
}
