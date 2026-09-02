using System.Security.Principal;

namespace SmartFileLauncher.Core.Tests.TestInfrastructure;

internal static class TestStoreOwner
{
    public static string Sid { get; } = Resolve();

    private static string Resolve()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "S-1-5-21-1-2-3-1001";
        }

        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }
}
