using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SmartFileLauncher.UI.Services;

public sealed partial class WindowsStartMenuShell : IStartMenuShell, IStartMenuItemNotifications, IStartMenuApplicationActions
{
    private const string RunAsVerb = "runas";
    private const string OpenFileLocationVerb = "openfilelocation";
    private const string InstalledApplicationsPage = "ms-settings:appsfeatures";
    private const uint ImmersiveHost = 1;
    private const string AppsFolderParsingName = "shell:AppsFolder";
    private const uint ParentRelativeParsing = 0x80018001;
    private const uint Hidden = 0x00080000;
    private const uint Validate = 0x01000000;
    private const uint IconOnly = 0x00000004;
    private const uint InvokeIdList = 0x0000000C;
    private const uint NoAsync = 0x00000100;
    private const uint NoUi = 0x00000400;
    private const int ShowNormal = 1;
    private static readonly Guid ShellItemId = typeof(IShellItem2).GUID;
    private static readonly Guid EnumShellItemsId = typeof(IEnumShellItems).GUID;
    private static readonly Guid EnumItemsHandler = new("94F60519-2850-4924-AA5A-D15E84868039");
    private static readonly PropertyKey LinkTarget = new(new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), 2);
    private static readonly PropertyKey PackageFamily = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 17);
    private static readonly PropertyKey HostEnvironment = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 14);

    public IReadOnlyList<StartMenuApplication> ReadApplications(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folder = OpenAppsFolder();
        try
        {
            var apps = ReadEntries(folder, cancellationToken);
            var repeatedNames = apps.GroupBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

            for (var index = 0; index < apps.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (repeatedNames.Contains(apps[index].Name))
                    apps[index] = apps[index] with { Detail = ReadDetail(folder, apps[index].Id) };
            }

            return apps;
        }
        finally
        {
            Marshal.ReleaseComObject(folder);
        }
    }

    public void Launch(StartMenuApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        Execute(app, null);
    }

    void IStartMenuApplicationActions.RunAsAdministrator(StartMenuApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        Execute(app, RunAsVerb);
    }

    void IStartMenuApplicationActions.OpenFileLocation(StartMenuApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        Execute(app, OpenFileLocationVerb);
    }

    void IStartMenuApplicationActions.OpenInstalledApplications() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(InstalledApplicationsPage) { UseShellExecute = true })?.Dispose();

    private static void Execute(StartMenuApplication app, string? verb)
    {
        var item = Resolve(app.Id);
        try
        {
            ExecuteVerb(item, verb);
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    public ImageSource? LoadIcon(StartMenuApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var item = Resolve(app.Id);
        try
        {
            if (item is not IShellItemImageFactory factory ||
                factory.GetImage(new NativeSize(48, 48), IconOnly, out var bitmap) < 0)
            {
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    public IDisposable Watch(IntPtr window, Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        return new ChangeSubscription(window, _ => changed(), captureItems: false);
    }

    IDisposable IStartMenuItemNotifications.WatchItems(IntPtr window, Action<ResolveStartMenuChange?> changed) =>
        new ChangeSubscription(window, changed, captureItems: true);

    StartMenuApplication? IStartMenuItemNotifications.ReadItem(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var folder = OpenAppsFolder();
        try
        {
            return ReadResolvedItem(folder, id, IntPtr.Zero);
        }
        finally
        {
            Marshal.ReleaseComObject(folder);
        }
    }

    private static void ExecuteVerb(IShellItem2 item, string? verb)
    {
        Marshal.ThrowExceptionForHR(SHGetIDListFromObject(item, out var itemIdList));
        var verbText = verb == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(verb);
        try
        {
            var info = new ShellExecuteInfo
            {
                Size = Marshal.SizeOf<ShellExecuteInfo>(),
                Mask = InvokeIdList | NoAsync | NoUi,
                Verb = verbText,
                Show = ShowNormal,
                ItemIdList = itemIdList
            };
            if (!ShellExecuteExW(ref info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (verbText != IntPtr.Zero) Marshal.FreeHGlobal(verbText);
            ILFree(itemIdList);
        }
    }

    private static List<StartMenuApplication> ReadEntries(IShellItem2 folder, CancellationToken cancellationToken)
    {
        Marshal.ThrowExceptionForHR(folder.BindToHandler(
            IntPtr.Zero, EnumItemsHandler, EnumShellItemsId, out var enumerator));

        try
        {
            var apps = new List<StartMenuApplication>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = enumerator.Next(1, out var item, out var fetched);
                Marshal.ThrowExceptionForHR(result);
                if (fetched == 0)
                    return apps;

                try
                {
                    if (ReadVisibleEntry(item) is { } app && identities.Add(app.Id))
                        apps.Add(app);
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static StartMenuApplication? ReadVisibleEntry(IShellItem2 item, uint attributeMask = Hidden)
    {
        if (item.GetAttributes(attributeMask, out var attributes) < 0 ||
            (attributes & Hidden) != 0 ||
            item.GetDisplayName(0, out var name) < 0 ||
            string.IsNullOrWhiteSpace(name) ||
            item.GetDisplayName(ParentRelativeParsing, out var id) < 0 ||
            string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var (runAs, location) = ReadCapabilities(item, id);
        return new StartMenuApplication(id, name, string.Empty) { CanRunAsAdministrator = runAs, HasFileLocation = location };
    }

    private static (bool RunAs, bool Location) ReadCapabilities(IShellItem2 item, string id)
    {
        if (item.GetString(PackageFamily, out var family) >= 0 && !string.IsNullOrWhiteSpace(family))
            return (item.GetUInt32(HostEnvironment, out var host) >= 0 && host != ImmersiveHost, false);

        var target = item.GetString(LinkTarget, out var path) >= 0 && !string.IsNullOrWhiteSpace(path) ? path.Trim() : null;
        var runnable = target ?? id;
        var elevatable = runnable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            runnable.EndsWith(".msc", StringComparison.OrdinalIgnoreCase);
        return (elevatable, target != null);
    }

    private static StartMenuApplication? ReadResolvedItem(IShellItem2 folder, string id, IntPtr expectedPidl)
    {
        if (SHCreateItemFromRelativeName(folder, id, IntPtr.Zero, ShellItemId, out var item) < 0)
            return null;

        try
        {
            if (expectedPidl != IntPtr.Zero)
            {
                if (SHGetIDListFromObject(item, out var itemIdList) < 0 || itemIdList == IntPtr.Zero)
                    return null;

                try
                {
                    if (!ILIsEqual(expectedPidl, itemIdList))
                        return null;
                }
                finally
                {
                    ILFree(itemIdList);
                }
            }

            var app = ReadVisibleEntry(item, Hidden | Validate);
            return app is not null && StringComparer.Ordinal.Equals(app.Id, id)
                ? app with { Detail = ReadDetail(item) }
                : null;
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    private static string ReadDetail(IShellItem2 folder, string id)
    {
        if (SHCreateItemFromRelativeName(folder, id, IntPtr.Zero, ShellItemId, out var item) < 0)
            return string.Empty;

        try
        {
            return ReadDetail(item);
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    private static string ReadDetail(IShellItem2 item)
    {
        if (item.GetString(LinkTarget, out var path) < 0 || string.IsNullOrWhiteSpace(path))
            return string.Empty;

        return path.Trim();
    }

    private static IShellItem2 OpenAppsFolder()
    {
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(
            AppsFolderParsingName, IntPtr.Zero, ShellItemId, out var folder));
        return folder;
    }

    private static IShellItem2 Resolve(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var folder = OpenAppsFolder();
        try
        {
            Marshal.ThrowExceptionForHR(SHCreateItemFromRelativeName(
                folder, id, IntPtr.Zero, ShellItemId, out var item));
            return item;
        }
        finally
        {
            Marshal.ReleaseComObject(folder);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize(int width, int height)
    {
        public readonly int Width = width;
        public readonly int Height = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Window;
        public IntPtr Verb;
        public IntPtr File;
        public IntPtr Parameters;
        public IntPtr Directory;
        public int Show;
        public IntPtr Instance;
        public IntPtr ItemIdList;
        public IntPtr Class;
        public IntPtr ClassKey;
        public uint Hotkey;
        public IntPtr IconOrMonitor;
        public IntPtr Process;
    }

    [ComImport, Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        [PreserveSig]
        int BindToHandler(IntPtr context, in Guid handlerId, in Guid interfaceId, out IEnumShellItems handler);

        void GetParent();

        [PreserveSig]
        int GetDisplayName(uint format, [MarshalAs(UnmanagedType.LPWStr)] out string? name);

        [PreserveSig]
        int GetAttributes(uint mask, out uint attributes);
        void Compare();
        void GetPropertyStore();
        void GetPropertyStoreWithCreateObject();
        void GetPropertyStoreForKeys();
        void GetPropertyDescriptionList();
        void Update();
        void GetProperty();
        void GetCLSID();
        void GetFileTime();
        void GetInt32();

        [PreserveSig]
        int GetString(in PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] out string? value);

        [PreserveSig]
        int GetUInt32(in PropertyKey key, out uint value);
    }

    [ComImport, Guid("70629033-E363-4A28-A567-0DB78006E6D7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig]
        int Next(uint requested, out IShellItem2 item, out uint fetched);
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, uint flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(
        string name, IntPtr bindContext, in Guid interfaceId, out IShellItem2 item);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromRelativeName(
        IShellItem2 parent, string name, IntPtr bindContext, in Guid interfaceId, out IShellItem2 item);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetIDListFromObject(
        [MarshalAs(UnmanagedType.IUnknown)] object item, out IntPtr idList);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void ILFree(IntPtr idList);

    [DllImport("shell32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ILIsEqual(IntPtr first, IntPtr second);

    [DllImport("shell32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref ShellExecuteInfo info);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);
}
