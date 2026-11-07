using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using IDataObject = System.Windows.IDataObject;

namespace SmartFileLauncher.UI.Services;

internal static class WindowsFileTransfer
{
    private const DragDropEffects TransferEffects = DragDropEffects.Copy | DragDropEffects.Move;
    private const string PreferredEffect = "Preferred DropEffect";
    private static readonly Guid FileOperationClass = new("3AD05575-8857-4850-9277-11B85BDB8E09");
    private static readonly Guid ShellItemId = typeof(IShellItem).GUID;

    public static DataObject CreateData(string[] paths, DragDropEffects preferred = DragDropEffects.None)
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths, autoConvert: false);
        if (preferred != DragDropEffects.None)
            data.SetData(PreferredEffect, new MemoryStream(BitConverter.GetBytes((int)preferred)), autoConvert: false);
        return data;
    }

    public static string[] ReadPaths(IDataObject? data)
    {
        return data?.GetData(DataFormats.FileDrop, autoConvert: false) is string[] paths
            ? paths.Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
    }

    public static DragDropEffects ReadPreferredEffect(IDataObject? data)
    {
        if (data is not ComDataObject native)
            return DragDropEffects.None;

        var format = CreateFormat(PreferredEffect);
        if (native.QueryGetData(ref format) != 0)
            return DragDropEffects.None;

        native.GetData(ref format, out var medium);
        try
        {
            if (medium.tymed != TYMED.TYMED_HGLOBAL || GlobalSize(medium.unionmember) < sizeof(int))
                return DragDropEffects.None;

            var address = GlobalLock(medium.unionmember);
            if (address == IntPtr.Zero)
                return DragDropEffects.None;

            try { return (DragDropEffects)Marshal.ReadInt32(address) & TransferEffects; }
            finally { GlobalUnlock(medium.unionmember); }
        }
        finally { ReleaseStgMedium(ref medium); }
    }

    public static DragDropEffects ChooseDropEffect(
        IReadOnlyList<string> paths,
        string destination,
        DragDropKeyStates keys,
        DragDropEffects allowed,
        DragDropEffects preferred)
    {
        allowed &= TransferEffects;
        if (paths.Count == 0 || allowed == DragDropEffects.None || keys.HasFlag(DragDropKeyStates.AltKey) ||
            paths.Any(path => IsSameOrChild(destination, path)))
            return DragDropEffects.None;

        var control = keys.HasFlag(DragDropKeyStates.ControlKey);
        var shift = keys.HasFlag(DragDropKeyStates.ShiftKey);
        if (control && shift)
            return DragDropEffects.None;

        DragDropEffects effect;
        if (control)
            effect = allowed & DragDropEffects.Copy;
        else if (shift)
            effect = allowed & DragDropEffects.Move;
        else if (allowed == DragDropEffects.Copy || allowed == DragDropEffects.Move)
            effect = allowed;
        else if (preferred is DragDropEffects.Copy or DragDropEffects.Move)
            effect = preferred;
        else
        {
            var root = Path.GetPathRoot(destination);
            effect = paths.All(path => string.Equals(Path.GetPathRoot(path), root, StringComparison.OrdinalIgnoreCase))
                ? DragDropEffects.Move
                : DragDropEffects.Copy;
        }

        return effect == DragDropEffects.Move && paths.Any(path => IsInFolder(path, destination))
            ? DragDropEffects.None
            : effect;
    }

    public static bool Transfer(string[] paths, string destination, bool move, IntPtr owner)
    {
        destination = NormalizePath(destination);
        paths = paths.Select(NormalizePath).ToArray();
        if (paths.Length == 0 || !Directory.Exists(destination))
            throw new IOException("Hedef klasör bulunamadı.");

        foreach (var path in paths)
        {
            if (move && IsInFolder(path, destination))
                throw new IOException("Öğe zaten hedef klasörde.");
            if (IsSameOrChild(destination, path))
                throw new IOException("Bir klasör kendi içine veya alt klasörüne aktarılamaz.");
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new FileNotFoundException($"Kaynak öğe bulunamadı: {path}");
        }

        var operation = (IShellFileOperation)Activator.CreateInstance(
            Type.GetTypeFromCLSID(FileOperationClass, throwOnError: true)!)!;
        try
        {
            operation.SetOwnerWindow(owner);
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(
                destination, IntPtr.Zero, ShellItemId, out var folder));
            try
            {
                foreach (var path in paths)
                {
                    Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(
                        path, IntPtr.Zero, ShellItemId, out var item));
                    try
                    {
                        if (move)
                            operation.MoveItem(item, folder, null, IntPtr.Zero);
                        else
                            operation.CopyItem(item, folder, null, IntPtr.Zero);
                    }
                    finally { Marshal.ReleaseComObject(item); }
                }

                var result = operation.PerformOperations();
                operation.GetAnyOperationsAborted(out var aborted);
                if (aborted)
                    return false;
                Marshal.ThrowExceptionForHR(result);
                return true;
            }
            finally { Marshal.ReleaseComObject(folder); }
        }
        finally { Marshal.ReleaseComObject(operation); }
    }

    public static void ReportMove(IDataObject data, bool paste)
    {
        if (data is not ComDataObject native)
            return;

        TryWriteEffect(native, "Performed DropEffect", DragDropEffects.None);
        TryWriteEffect(native, "Logical Performed DropEffect", DragDropEffects.Move);
        if (paste)
            TryWriteEffect(native, "Paste Succeeded", DragDropEffects.Move);
    }

    public static string NormalizePath(string path)
    {
        path = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal) &&
                 path.Length >= 7 && char.IsAsciiLetter(path[4]) && path[5] == ':' && path[6] == '\\')
            path = path[4..];

        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new IOException("Dosya yolu geçersiz.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsSameOrChild(string path, string parent)
    {
        return path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInFolder(string path, string folder) =>
        string.Equals(
            Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)),
            Path.TrimEndingDirectorySeparator(folder),
            StringComparison.OrdinalIgnoreCase);

    private static FORMATETC CreateFormat(string name) => new()
    {
        cfFormat = unchecked((short)DataFormats.GetDataFormat(name).Id),
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        tymed = TYMED.TYMED_HGLOBAL
    };

    private static void TryWriteEffect(ComDataObject data, string name, DragDropEffects effect)
    {
        var format = CreateFormat(name);
        var handle = GlobalAlloc(2, sizeof(int));
        if (handle == IntPtr.Zero)
            return;

        var medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = handle };
        try
        {
            var address = GlobalLock(handle);
            if (address == IntPtr.Zero)
                return;
            try { Marshal.WriteInt32(address, (int)effect); }
            finally { GlobalUnlock(handle); }
            data.SetData(ref format, ref medium, release: true);
            medium = default;
        }
        catch (COMException) { }
        catch (NotImplementedException) { }
        finally
        {
            if (medium.unionmember != IntPtr.Zero)
                ReleaseStgMedium(ref medium);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr bindingContext, in Guid iid, out IShellItem item);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem([MarshalAs(UnmanagedType.Interface)] object item);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void RenameItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? name, IntPtr sink);
        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem folder);
        void CopyItem(IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? name, IntPtr sink);
        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem folder);
        void DeleteItem([MarshalAs(UnmanagedType.Interface)] object item, IntPtr sink);
        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void NewItem([MarshalAs(UnmanagedType.Interface)] object folder, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        [PreserveSig] int PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
