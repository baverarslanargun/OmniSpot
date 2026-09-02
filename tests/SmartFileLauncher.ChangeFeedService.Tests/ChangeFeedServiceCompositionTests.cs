using System.Reflection;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.ChangeFeed.Usn;
using Xunit;

namespace SmartFileLauncher.ChangeFeedService.Tests;

public sealed class ChangeFeedServiceCompositionTests
{
    [Fact]
    public void HostRegistration_StartsBothAdmissionAndDrain()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChangeFeedService();

        var hosted = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType)
            .ToArray();

        Assert.Contains(typeof(ChangeFeedAdmissionWorker), hosted);
        Assert.Contains(typeof(ChangeFeedDrainWorker), hosted);
    }

    [Fact]
    public void ProductionStoreFactory_OnlyOpensTheTrustedStore()
    {
        Assert.False(RunningAsLocalSystem());

        var factory = StoreFactoryOf(ChangeFeedAdmissionWorker.CreateAdmissionService());

        var failure = Assert.Throws<ChangeFeedStoreSecurityException>(
            () => factory(CurrentSid()));

        Assert.Contains("LocalSystem", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionAdmissionService_ProbesTheRealFileSystem()
    {
        var service = ChangeFeedAdmissionWorker.CreateAdmissionService();

        var admission = FieldValue<ChangeFeedRootAdmission>(
            typeof(ChangeFeedAdmissionService),
            "_admission",
            service);

        var probe = FieldValue<object>(
            typeof(ChangeFeedRootAdmission),
            "_identityProbe",
            admission);

        Assert.IsType<UsnFileSystemIdentityProbe>(probe);
    }

    [Fact]
    public void DrainRunner_ReadsTheSubscriptionFromTheSameLayoutItWasGiven()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "OmniSpot.Test." + Guid.NewGuid().ToString("N"));

        try
        {
            var layout = TrustedLayout(root);
            var (runner, store) = ChangeFeedDrainWorker.CreateRunner(layout);

            Assert.Equal(UsnDrainOutcome.NoSubscription, runner.Run().Outcome);

            store.WriteSubscription(new ChangeFeedSubscription(
                CurrentSid(),
                new[]
                {
                    new ChangeFeedSubscribedRoot(
                        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
                        new ChangeFeedRootIdentity("vol-test", "node-test"))
                }));

            Assert.True(File.Exists(layout.SubscriptionPath));
            Assert.NotEqual(UsnDrainOutcome.NoSubscription, runner.Run().Outcome);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ChangeFeedStoreLayout TrustedLayout(string trustedRoot)
    {
        var method = typeof(ChangeFeedStoreLayout).GetMethod(
            "ForTrustedOwner",
            BindingFlags.Static | BindingFlags.NonPublic,
            new[] { typeof(string), typeof(string), typeof(string) });

        Assert.NotNull(method);

        return (ChangeFeedStoreLayout)method!.Invoke(
            null,
            new object[] { trustedRoot, CurrentSid(), CurrentSid() })!;
    }

    private static Func<string, IChangeFeedStore> StoreFactoryOf(ChangeFeedAdmissionService service) =>
        FieldValue<Func<string, IChangeFeedStore>>(
            typeof(ChangeFeedAdmissionService),
            "_storeFactory",
            service);

    private static TValue FieldValue<TValue>(Type owner, string name, object instance)
    {
        var field = owner.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (TValue)field!.GetValue(instance)!;
    }

    private static string CurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }

    private static bool RunningAsLocalSystem()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User is { } user && user.IsWellKnown(WellKnownSidType.LocalSystemSid);
    }
}
