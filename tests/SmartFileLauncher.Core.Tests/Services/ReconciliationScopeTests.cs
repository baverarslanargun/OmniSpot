using System.Diagnostics;
using System.Security.AccessControl;
using System.Runtime.Versioning;
using System.Security.Principal;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class ReconciliationScopeTests
{
    [Fact]
    public async Task AReparsePointUnderTheRoot_DoesNotFailReconciliation()
    {
        using var world = await World.CreateAsync();
        var target = world.CreateOutsideDirectory("hedef");
        File.WriteAllText(Path.Combine(target, "icerik.txt"), "veri");

        var junction = Path.Combine(world.Root, "baglanti");
        Assert.True(TryCreateJunction(junction, target), "Bağlantı kurulamadı.");

        var reconciled = await world.Manager.ReconcileWithinLifecycleAsync(world.Root);

        Assert.True(
            reconciled,
            "Politika gereği atlanan bir reparse point uzlaştırmayı başarısız saymamalı; " +
            "aksi halde değişiklik akışı devralması hiçbir zaman tamamlanamaz.");
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task AnUnreadableDirectoryUnderTheRoot_FailsReconciliation()
    {
        using var world = await World.CreateAsync();
        var locked = Path.Combine(world.Root, "kilitli");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "icerik.txt"), "veri");

        var identity = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(
            identity,
            FileSystemRights.ListDirectory | FileSystemRights.ReadData,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny);

        var info = new DirectoryInfo(locked);
        var security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);

        try
        {
            var reconciled = await world.Manager.ReconcileWithinLifecycleAsync(world.Root);

            Assert.False(
                reconciled,
                "Gerçekten okunamayan bir dizin uzlaştırmayı başarısız saymalı.");
        }
        finally
        {
            security = info.GetAccessControl();
            security.RemoveAccessRule(rule);
            info.SetAccessControl(security);
        }
    }

    private static bool TryCreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });

        if (process is null)
        {
            return false;
        }

        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    private sealed class World : IDisposable
    {
        private readonly TemporaryDirectory _workspace;
        private readonly FileWatcherService _watcher;

        private World(
            TemporaryDirectory workspace,
            FileWatcherService watcher,
            IndexManager manager)
        {
            _workspace = workspace;
            _watcher = watcher;
            Manager = manager;
            Root = Path.Combine(workspace.Path, "kok");
        }

        public IndexManager Manager { get; }

        public string Root { get; }

        public static async Task<World> CreateAsync()
        {
            var workspace = new TemporaryDirectory();
            var root = workspace.CreateDirectory("kok");
            var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
            var watcher = new FileWatcherService(debounceMs: 1);
            var manager = new IndexManager(database, watcher);

            await manager.InitializeAsync(new[] { root });
            return new World(workspace, watcher, manager);
        }

        public string CreateOutsideDirectory(string name) =>
            _workspace.CreateDirectory(name);

        public void Dispose()
        {
            Manager.Dispose();
            _watcher.Dispose();
            _workspace.Dispose();
        }
    }
}
