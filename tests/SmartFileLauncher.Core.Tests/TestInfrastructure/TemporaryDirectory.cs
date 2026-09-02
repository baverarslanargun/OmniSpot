using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SmartFileLauncher.Core.Tests.TestInfrastructure;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "OmniSpot.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateDirectory(string relativePath)
    {
        var path = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    public string CreateFile(string relativePath, string contents = "test")
    {
        var path = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        if (!Directory.Exists(Path)) return;

        const int maxAttempts = 7;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(50 * (1 << attempt));
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts - 1)
            {
                Unlock(Path);
                Thread.Sleep(50 * (1 << attempt));
            }
        }
    }

    private static void Unlock(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        GrantSelf(path);

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(path);
        }
        catch (Exception failure) when (failure is UnauthorizedAccessException or IOException)
        {
            return;
        }

        foreach (var child in children)
        {
            Unlock(child);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void GrantSelf(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            var security = info.GetAccessControl(AccessControlSections.Access);
            security.SetAccessRuleProtection(false, true);
            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        catch (Exception failure)
            when (failure is UnauthorizedAccessException or PrivilegeNotHeldException or IOException)
        {
        }
    }
}
