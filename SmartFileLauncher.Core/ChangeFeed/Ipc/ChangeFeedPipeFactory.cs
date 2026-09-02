using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using SmartFileLauncher.Core.ChangeFeed.Store;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

[SupportedOSPlatform("windows")]
public static class ChangeFeedPipeFactory
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint FileFlagOverlapped = 0x40000000;

    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeReadModeByte = 0x00000000;
    private const uint PipeWait = 0x00000000;
    private const uint PipeRejectRemoteClients = 0x00000008;

    private const uint OpenExisting = 3;
    private const uint SecurityQosPresent = 0x00100000;
    private const uint SecurityAnonymous = 0x00000000;
    private const uint SecurityIdentification = 0x00010000;
    private const uint SecurityImpersonation = 0x00020000;
    private const uint SecurityDelegation = 0x00030000;

    private const int ErrorAccessDenied = 5;
    private const int ErrorPipeBusy = 231;
    private const int ErrorFileNotFound = 2;
    private const int BusyWaitSliceMilliseconds = 250;

    public const PipeAccessRights CallerAccessRights =
        PipeAccessRights.ReadData |
        PipeAccessRights.WriteData |
        PipeAccessRights.ReadPermissions |
        PipeAccessRights.Synchronize;

    public const PipeAccessRights CallerGrantRights =
        CallerAccessRights | PipeAccessRights.ReadAttributes;

    public static NamedPipeServerStream CreateFirstInstance(
        string pipeName = ChangeFeedProtocol.PipeName) =>
        Create(pipeName, firstInstance: true);

    public static NamedPipeServerStream Create(
        string pipeName,
        bool firstInstance,
        SecurityIdentifier? additionalServerPrincipal = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        var security = BuildSecurity(additionalServerPrincipal);
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);

        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = pinned.AddrOfPinnedObject(),
                InheritHandle = false
            };

            var openMode = PipeAccessDuplex | FileFlagOverlapped;
            if (firstInstance)
            {
                openMode |= FileFlagFirstPipeInstance;
            }

            var handle = CreateNamedPipe(
                @"\\.\pipe\" + pipeName,
                openMode,
                PipeTypeByte | PipeReadModeByte | PipeWait | PipeRejectRemoteClients,
                ChangeFeedProtocol.MaximumPipeInstances,
                ChangeFeedProtocol.MaximumMessageBytes,
                ChangeFeedProtocol.MaximumMessageBytes,
                0,
                ref attributes);

            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new ChangeFeedPipeException(
                    DescribeCreateFailure(pipeName, firstInstance, error),
                    new Win32Exception(error));
            }

            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally
        {
            pinned.Free();
        }
    }

    public static NamedPipeClientStream Connect(
        string pipeName,
        TokenImpersonationLevel impersonationLevel,
        string serverName = ".",
        TimeSpan? busyWait = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);

        var path = $@"\\{serverName}\pipe\{pipeName}";
        var budget = busyWait ?? ChangeFeedProtocol.IoTimeout;
        var remaining = (int)Math.Max(0, budget.TotalMilliseconds);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var handle = CreateFile(
                path,
                (uint)CallerAccessRights,
                0,
                IntPtr.Zero,
                OpenExisting,
                SecurityQosPresent | QualityOfService(impersonationLevel),
                IntPtr.Zero);

            if (!handle.IsInvalid)
            {
                return new NamedPipeClientStream(PipeDirection.InOut, false, true, handle);
            }

            var error = Marshal.GetLastWin32Error();
            handle.Dispose();

            if (error != ErrorPipeBusy || remaining <= 0)
            {
                throw new ChangeFeedPipeException(
                    DescribeConnectFailure(pipeName, error),
                    new Win32Exception(error));
            }

            var slice = Math.Min(remaining, BusyWaitSliceMilliseconds);
            WaitNamedPipe(path, slice);
            remaining -= slice;
        }
    }

    private static string DescribeCreateFailure(string pipeName, bool firstInstance, int error) =>
        (firstInstance, error) switch
        {
            (true, ErrorAccessDenied) =>
                $"{pipeName} adlı kanal zaten var; ilk instance alınamadı ({error}).",
            (false, ErrorPipeBusy) =>
                $"{pipeName} adlı kanalın instance sınırı dolu ({error}).",
            _ => $"{pipeName} adlı kanal oluşturulamadı: {error}"
        };

    private static string DescribeConnectFailure(string pipeName, int error) =>
        error switch
        {
            ErrorPipeBusy => $"{pipeName} adlı kanalın tüm instance'ları meşgul ({error}).",
            ErrorFileNotFound => $"{pipeName} adlı kanal dinlenmiyor ({error}).",
            _ => $"{pipeName} adlı kanala bağlanılamadı: {error}"
        };

    private static uint QualityOfService(TokenImpersonationLevel level) =>
        level switch
        {
            TokenImpersonationLevel.Anonymous => SecurityAnonymous,
            TokenImpersonationLevel.Identification => SecurityIdentification,
            TokenImpersonationLevel.Impersonation => SecurityImpersonation,
            TokenImpersonationLevel.Delegation => SecurityDelegation,
            _ => throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                "Desteklenmeyen bürünme seviyesi.")
        };

    public static PipeSecurity BuildSecurity(SecurityIdentifier? additionalServerPrincipal = null)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(CurrentUserSid());

        foreach (var owner in PrivilegedIdentities())
        {
            security.AddAccessRule(new PipeAccessRule(
                owner,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }

        if (additionalServerPrincipal is not null)
        {
            security.AddAccessRule(new PipeAccessRule(
                additionalServerPrincipal,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            CallerGrantRights,
            AccessControlType.Allow));

        return security;
    }

    public static SecurityIdentifier CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User
            ?? throw new ChangeFeedPipeException("Mevcut sürecin kimliği okunamadı.");
    }

    private static IEnumerable<SecurityIdentifier> PrivilegedIdentities()
    {
        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        yield return new SecurityIdentifier(ChangeFeedServiceIdentity.DeriveServiceSid());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafePipeHandle CreateFile(
        string name,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "WaitNamedPipeW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipe(string name, int timeoutMilliseconds);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateNamedPipeW")]
    private static extern SafePipeHandle CreateNamedPipe(
        string name,
        uint openMode,
        uint pipeMode,
        int maxInstances,
        int outBufferSize,
        int inBufferSize,
        int defaultTimeout,
        ref SecurityAttributes securityAttributes);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public bool InheritHandle;
    }
}

public class ChangeFeedPipeException : Exception
{
    public ChangeFeedPipeException(string message)
        : base(message)
    {
    }

    public ChangeFeedPipeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ChangeFeedUntrustedServerException : ChangeFeedPipeException
{
    public ChangeFeedUntrustedServerException(string message)
        : base(message)
    {
    }

    public ChangeFeedUntrustedServerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
