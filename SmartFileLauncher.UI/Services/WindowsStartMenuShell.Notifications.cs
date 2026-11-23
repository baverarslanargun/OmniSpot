using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace SmartFileLauncher.UI.Services;

public sealed partial class WindowsStartMenuShell
{
    private sealed class ChangeSubscription : IStartMenuChangeWatch
    {
        private const int FileSystemEvents = 0x0001;
        private const int ShellEvents = 0x0002;
        private const int RecursiveInterrupt = 0x1000;
        private const int NewDelivery = 0x8000;
        private const int RenameItem = 0x00000001;
        private const int Create = 0x00000002;
        private const int Delete = 0x00000004;
        private const int CreateDirectory = 0x00000008;
        private const int RemoveDirectory = 0x00000010;
        private const int Attributes = 0x00000800;
        private const int UpdateDirectory = 0x00001000;
        private const int UpdateItem = 0x00002000;
        private const int RenameDirectory = 0x00020000;
        private const int AssociationChanged = 0x08000000;
        private const int Interrupt = unchecked((int)0x80000000);
        private const int MaximumPidlSize = 16 * 1024;
        private const int CatalogEvents = RenameItem | Create | Delete | CreateDirectory | RemoveDirectory |
            Attributes | UpdateDirectory | UpdateItem | RenameDirectory | AssociationChanged;

        private static readonly Guid AppsFolder = new("1E87508D-89C2-42F0-8A7E-645A0F50CA58");
        private static readonly Guid StartMenu = new("625B53C3-AB48-4EC1-BA1F-A1EF4146FC19");
        private static readonly Guid CommonStartMenu = new("A4115719-D62E-491D-AA7C-E74B8BE3B067");

        private readonly HwndSource _source;
        private readonly Action<ResolveStartMenuChange?> _changed;
        private readonly bool _captureItems;
        private readonly uint _message;
        private readonly List<uint> _registrations = new(3);
        private bool _disposed;

        public bool IsComplete => !_disposed && _registrations.Count == 3;

        public ChangeSubscription(IntPtr window, Action<ResolveStartMenuChange?> changed, bool captureItems)
        {
            ArgumentNullException.ThrowIfNull(changed);
            _source = HwndSource.FromHwnd(window)
                ?? throw new ArgumentException("Başlat menüsü izleyicisi için pencere bulunamadı.", nameof(window));
            _source.VerifyAccess();
            _changed = changed;
            _captureItems = captureItems;
            _message = RegisterWindowMessageW("OmniSpot.StartMenuCatalogChanged.996C5C36-AA2D-4678-BBA4-1AC29F291CE2");
            if (_message == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            _source.Disposed += OnSourceDisposed;
            _source.AddHook(OnWindowMessage);
            try
            {
                Register(window, AppsFolder, ShellEvents);
                Register(window, StartMenu, ShellEvents | FileSystemEvents | RecursiveInterrupt);
                Register(window, CommonStartMenu, ShellEvents | FileSystemEvents | RecursiveInterrupt);
                if (_registrations.Count == 0)
                    throw new InvalidOperationException("Windows Başlat menüsü değişiklikleri izlenemedi.");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _source.VerifyAccess();
            _disposed = true;
            foreach (var registration in _registrations)
                SHChangeNotifyDeregister(registration);

            _registrations.Clear();
            _source.Disposed -= OnSourceDisposed;
            if (!_source.IsDisposed)
                _source.RemoveHook(OnWindowMessage);
        }

        private void Register(IntPtr window, Guid folder, int sources)
        {
            if (SHGetKnownFolderIDList(folder, 0, IntPtr.Zero, out var itemIdList) < 0)
                return;

            try
            {
                var entry = new ChangeNotifyEntry { ItemIdList = itemIdList, Recursive = true };
                var registration = SHChangeNotifyRegister(
                    window, sources | NewDelivery, CatalogEvents, _message, 1, in entry);
                if (registration != 0)
                    _registrations.Add(registration);
            }
            finally
            {
                ILFree(itemIdList);
            }
        }

        private IntPtr OnWindowMessage(
            IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_disposed || message != _message)
                return IntPtr.Zero;

            handled = true;
            var notification = SHChangeNotification_Lock(
                wParam, unchecked((uint)lParam.ToInt64()), out var itemIdLists, out var eventId);
            ResolveStartMenuChange? resolveChange = null;
            if (notification != IntPtr.Zero)
            {
                try
                {
                    if (_captureItems)
                        resolveChange = CaptureChange(eventId & ~Interrupt, itemIdLists);
                }
                finally
                {
                    SHChangeNotification_Unlock(notification);
                }
            }

            if (_captureItems)
            {
                try
                {
                    _changed(resolveChange);
                }
                catch (Exception error)
                {
                    Debug.WriteLine(error);
                }
            }
            else if (notification != IntPtr.Zero)
            {
                _changed(null);
            }

            return IntPtr.Zero;
        }

        private void OnSourceDisposed(object? sender, EventArgs args) => Dispose();

        private static ResolveStartMenuChange? CaptureChange(int eventId, IntPtr itemIdLists)
        {
            if (itemIdLists == IntPtr.Zero || eventId is not (RenameItem or Create or Delete or Attributes or UpdateItem))
                return null;

            var first = CopyPidl(Marshal.ReadIntPtr(itemIdLists));
            if (first is null)
                return null;

            byte[]? second = null;
            var secondPidl = Marshal.ReadIntPtr(itemIdLists, IntPtr.Size);
            if (eventId == RenameItem)
            {
                second = CopyPidl(secondPidl);
                if (second is null)
                    return null;
            }
            else if (secondPidl != IntPtr.Zero)
            {
                return null;
            }

            return () => ResolveItemChange(eventId, first, second);
        }

        private static byte[]? CopyPidl(IntPtr itemIdList)
        {
            if (itemIdList == IntPtr.Zero)
                return null;

            var size = ILGetSize(itemIdList);
            if (size <= sizeof(ushort) || size > MaximumPidlSize)
                return null;

            var copy = new byte[(int)size];
            Marshal.Copy(itemIdList, copy, 0, copy.Length);
            return copy;
        }

        private static StartMenuItemChange? ResolveItemChange(int eventId, byte[] first, byte[]? second)
        {
            var folder = OpenAppsFolder();
            try
            {
                if (SHGetIDListFromObject(folder, out var folderPidl) < 0 || folderPidl == IntPtr.Zero)
                    return null;

                try
                {
                    return ResolveItemChange(folder, folderPidl, eventId, first, second);
                }
                finally
                {
                    ILFree(folderPidl);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(folder);
            }
        }

        private static StartMenuItemChange? ResolveItemChange(
            IShellItem2 folder, IntPtr folderPidl, int eventId, byte[] first, byte[]? second)
        {
            var firstHandle = GCHandle.Alloc(first, GCHandleType.Pinned);
            try
            {
                var firstPidl = firstHandle.AddrOfPinnedObject();
                var firstId = ReadItemId(folderPidl, firstPidl);
                if (firstId is null)
                    return null;

                if (eventId == Delete)
                    return CanRemoveItem(folder, firstId) ? new StartMenuItemChange(firstId, null) : null;

                if (eventId == RenameItem)
                    return second is null ? null : ResolveRename(folder, folderPidl, firstId, second);

                var item = ReadResolvedItem(folder, firstId, firstPidl);
                return item is null ? null : new StartMenuItemChange(null, item);
            }
            finally
            {
                firstHandle.Free();
            }
        }

        private static StartMenuItemChange? ResolveRename(
            IShellItem2 folder, IntPtr folderPidl, string previousId, byte[] second)
        {
            var secondHandle = GCHandle.Alloc(second, GCHandleType.Pinned);
            try
            {
                var secondPidl = secondHandle.AddrOfPinnedObject();
                var newId = ReadItemId(folderPidl, secondPidl);
                if (newId is null || (!StringComparer.Ordinal.Equals(previousId, newId) && !CanRemoveItem(folder, previousId)))
                    return null;

                var item = ReadResolvedItem(folder, newId, secondPidl);
                return item is null ? null : new StartMenuItemChange(previousId, item);
            }
            finally
            {
                secondHandle.Free();
            }
        }

        private static string? ReadItemId(IntPtr folderPidl, IntPtr itemPidl) =>
            ILIsParent(folderPidl, itemPidl, immediate: true) &&
            SHGetNameFromIDList(itemPidl, ParentRelativeParsing, out var id) >= 0 &&
            !string.IsNullOrWhiteSpace(id)
                ? id
                : null;

        private static bool CanRemoveItem(IShellItem2 folder, string id)
        {
            var result = SHCreateItemFromRelativeName(folder, id, IntPtr.Zero, ShellItemId, out var item);
            if (result < 0)
                return IsItemNotFound(result);

            try
            {
                result = item.GetAttributes(Validate | Hidden, out var attributes);
                return result < 0 ? IsItemNotFound(result) : (attributes & Hidden) != 0;
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }

        private static bool IsItemNotFound(int result) =>
            result is unchecked((int)0x80070002) or unchecked((int)0x80070003) or unchecked((int)0x80070490);

        [StructLayout(LayoutKind.Sequential)]
        private struct ChangeNotifyEntry
        {
            public IntPtr ItemIdList;
            [MarshalAs(UnmanagedType.Bool)] public bool Recursive;
        }

        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterWindowMessageW(string message);

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern int SHGetKnownFolderIDList(
            in Guid folderId, uint flags, IntPtr userToken, out IntPtr itemIdList);

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern uint SHChangeNotifyRegister(
            IntPtr window, int sources, int events, uint message, int entryCount, in ChangeNotifyEntry entry);

        [DllImport("shell32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SHChangeNotifyDeregister(uint registration);

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern IntPtr SHChangeNotification_Lock(
            IntPtr notification, uint processId, out IntPtr itemIdLists, out int eventId);

        [DllImport("shell32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SHChangeNotification_Unlock(IntPtr notification);

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern uint ILGetSize(IntPtr itemIdList);

        [DllImport("shell32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ILIsParent(IntPtr parent, IntPtr child, [MarshalAs(UnmanagedType.Bool)] bool immediate);

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern int SHGetNameFromIDList(
            IntPtr itemIdList, uint format, [MarshalAs(UnmanagedType.LPWStr)] out string? name);
    }
}
