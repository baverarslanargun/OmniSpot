using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.ChangeFeed.Usn;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

[SupportedOSPlatform("windows")]
public sealed class ChangeFeedIpcRoundTripTests
{
    [Fact]
    public async Task AddRoot_AdmitsAListableRootAndStoresItInTheTrustedStore()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Projeler");

        var response = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

        Assert.Equal(ChangeFeedResponseStatus.Ok, response.Status);
        Assert.Equal(new[] { root }, response.Roots);
        Assert.Equal(new[] { root }, harness.StoredRoots());
    }

    [Fact]
    public async Task AddRoot_IsIdempotentForTheSameCanonicalPath()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Projeler");

        await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

        var again = await harness.SendAsync(
            new ChangeFeedRequest(
                ChangeFeedProtocol.Version,
                ChangeFeedRequestKind.AddRoot,
                root + Path.DirectorySeparatorChar));

        Assert.Equal(ChangeFeedResponseStatus.Ok, again.Status);
        Assert.Single(harness.StoredRoots());
    }

    [Fact]
    public async Task RemoveRoot_IsIdempotentAndClearsTheSubscriptionWhenEmpty()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Projeler");

        await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

        var removed = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.RemoveRoot, root));
        Assert.Equal(ChangeFeedResponseStatus.Ok, removed.Status);
        Assert.Empty(harness.StoredRoots());

        var again = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.RemoveRoot, root));
        Assert.Equal(ChangeFeedResponseStatus.Ok, again.Status);
        Assert.Empty(again.Roots!);
    }

    [Fact]
    public async Task AddRoot_RefusesARootThatCannotBeUsed()
    {
        using var harness = new Harness();

        var response = await harness.SendAsync(
            new ChangeFeedRequest(
                ChangeFeedProtocol.Version,
                ChangeFeedRequestKind.AddRoot,
                Path.Combine(harness.Workspace.Path, "yok")));

        Assert.Equal(ChangeFeedResponseStatus.RootUnusable, response.Status);
        Assert.Empty(harness.StoredRoots());
    }

    [Fact]
    public async Task AddRoot_RefusesARootTheCallerIsDeniedByAcl()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Kapali");
        Deny(root);

        try
        {
            var response = await harness.SendAsync(
                new ChangeFeedRequest(
                    ChangeFeedProtocol.Version,
                    ChangeFeedRequestKind.AddRoot,
                    root));

            Assert.Equal(ChangeFeedResponseStatus.RootUnauthorized, response.Status);
            Assert.Empty(harness.StoredRoots());
        }
        finally
        {
            Undeny(root);
        }
    }

    [Fact]
    public async Task AddRoot_ReportsUnauthorizedWhenTheCallerCannotReadThePathChain()
    {
        using var harness = new Harness();
        var parent = harness.Workspace.CreateDirectory("KapaliUst");
        var root = Directory.CreateDirectory(Path.Combine(parent, "Icerik")).FullName;
        Deny(parent);

        try
        {
            var response = await harness.SendAsync(
                new ChangeFeedRequest(
                    ChangeFeedProtocol.Version,
                    ChangeFeedRequestKind.AddRoot,
                    root));

            Assert.Equal(ChangeFeedResponseStatus.RootUnauthorized, response.Status);
            Assert.Empty(harness.StoredRoots());
        }
        finally
        {
            Undeny(parent);
        }
    }

    [Fact]
    public async Task AddRoot_AcceptsARootWhoseChildIsClosedToTheCaller()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Acik");
        var child = Directory.CreateDirectory(Path.Combine(root, "Kapali")).FullName;
        Deny(child);

        try
        {
            var response = await harness.SendAsync(
                new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

            Assert.Equal(ChangeFeedResponseStatus.Ok, response.Status);
            Assert.Equal(new[] { root }, harness.StoredRoots());
        }
        finally
        {
            Undeny(child);
        }
    }

    [Fact]
    public async Task ImpersonationFailure_AdmitsNothingAndTouchesNoTrustedStore()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();
        var root = harness.Workspace.CreateDirectory("Projeler");

        using var weak = ChangeFeedPipeFactory.Connect(
            harness.PipeName,
            TokenImpersonationLevel.Identification);

        await ChangeFeedMessageChannel.WriteAsync(
            weak,
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root),
            CancellationToken.None);

        var response = await ChangeFeedMessageChannel.ReadAsync<ChangeFeedResponse>(
            weak,
            CancellationToken.None);

        Assert.Equal(ChangeFeedResponseStatus.RootUnauthorized, response.Status);
        Assert.False(Directory.Exists(harness.OwnerDirectory()));
    }

    [Fact]
    public async Task RejectionText_NamesNothingBeyondThePathTheCallerSent()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Kapali");
        Deny(root);

        try
        {
            var response = await harness.SendAsync(
                new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

            Assert.Equal(ChangeFeedResponseStatus.RootUnauthorized, response.Status);

            var message = response.Message!;
            Assert.Contains(root, message, StringComparison.Ordinal);
            Assert.DoesNotContain(harness.TrustedRoot, message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                ChangeFeedStoreLayout.DefaultTrustedRoot,
                message,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(CurrentSid().Value, message, StringComparison.OrdinalIgnoreCase);

            var extra = message
                .Replace(root, string.Empty, StringComparison.Ordinal)
                .Split(new[] { ' ', '(', ')', ':' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token.Contains(Path.DirectorySeparatorChar))
                .ToArray();

            Assert.Empty(extra);
        }
        finally
        {
            Undeny(root);
        }
    }

    [Fact]
    public async Task Request_WithAnotherVersionIsRefusedWithoutDowngrade()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Projeler");

        var response = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version + 1, ChangeFeedRequestKind.AddRoot, root));

        Assert.Equal(ChangeFeedResponseStatus.VersionMismatch, response.Status);
        Assert.Equal(ChangeFeedProtocol.Version, response.Version);
        Assert.Empty(harness.StoredRoots());
    }

    [Fact]
    public async Task Request_OverTheSizeLimitIsRefusedOnARealConnection()
    {
        using var harness = new Harness();

        var oversized = new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.AddRoot,
            new string('k', ChangeFeedProtocol.MaximumMessageBytes));

        await Assert.ThrowsAsync<ChangeFeedProtocolException>(
            () => harness.Client.SendAsync(oversized, CancellationToken.None));

        Assert.Empty(harness.StoredRoots());

        var afterwards = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.ListRoots));
        Assert.Equal(ChangeFeedResponseStatus.Ok, afterwards.Status);
    }

    [Fact]
    public async Task Server_RefusesAnOversizedFrameAndClosesTheConnection()
    {
        using var harness = new Harness();

        using var raw = ChangeFeedPipeFactory.Connect(
            harness.PipeName,
            TokenImpersonationLevel.Impersonation);

        await raw.WriteAsync(BitConverter.GetBytes(ChangeFeedProtocol.MaximumMessageBytes + 1));
        await raw.FlushAsync();

        var response = await ChangeFeedMessageChannel.ReadAsync<ChangeFeedResponse>(
            raw,
            CancellationToken.None);

        Assert.Equal(ChangeFeedResponseStatus.InvalidRequest, response.Status);

        var trailing = new byte[1];
        Assert.Equal(0, await raw.ReadAsync(trailing));
        Assert.Empty(harness.StoredRoots());
    }

    [Fact]
    public void Request_CarriesNoIdentityFieldTheCallerCouldClaim()
    {
        var members = typeof(ChangeFeedRequest)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(new[] { "Version", "Kind", "RootPath" }, members);
    }

    [Fact]
    public async Task Client_RefusesAServerItDoesNotTrust()
    {
        using var harness = new Harness();
        var client = new ChangeFeedClient(harness.PipeName, ChangeFeedClient.DefaultTrustedOwners());

        var failure = await Assert.ThrowsAsync<ChangeFeedUntrustedServerException>(
            () => client.SendAsync(
                new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.ListRoots),
                CancellationToken.None));

        Assert.Contains("güvenilir değil", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UntrustedServer_IsNeverSoftenedIntoUnavailable()
    {
        using var harness = new Harness();
        var client = new ChangeFeedClient(harness.PipeName, ChangeFeedClient.DefaultTrustedOwners());

        var failure = await Record.ExceptionAsync(
            () => client.SendAsync(
                new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.ListRoots),
                CancellationToken.None));

        Assert.IsType<ChangeFeedUntrustedServerException>(failure);
    }

    [Fact]
    public async Task Client_ReportsUnavailableWhenNobodyIsListening()
    {
        var client = new ChangeFeedClient("OmniSpot.Test." + Guid.NewGuid().ToString("N"));

        var response = await client.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.ListRoots),
            CancellationToken.None);

        Assert.Equal(ChangeFeedResponseStatus.Unavailable, response.Status);
    }

    [Fact]
    public async Task Listener_RefusesToStartWhenThePipeNameIsAlreadyTaken()
    {
        using var harness = new Harness(listen: false);
        using var squatter = ChangeFeedPipeFactory.CreateFirstInstance(harness.PipeName);

        var failure = await Assert.ThrowsAsync<ChangeFeedPipeException>(
            () => harness.Server.ListenAsync(CancellationToken.None));

        Assert.Contains("zaten var", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listener_RefusesTheOverflowClientWhileEverySlotIsHeld()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();

        var silent = new List<System.IO.Pipes.NamedPipeClientStream>();

        try
        {
            for (var i = 0; i < ChangeFeedProtocol.MaximumConcurrentConnections; i++)
            {
                silent.Add(ChangeFeedPipeFactory.Connect(
                    harness.PipeName,
                    TokenImpersonationLevel.Impersonation));
            }

            await WaitUntilAsync(() => harness.Server.AvailableSlots == 0, TimeSpan.FromSeconds(5));

            var overflow = Assert.Throws<ChangeFeedPipeException>(
                () => ChangeFeedPipeFactory.Connect(
                    harness.PipeName,
                    TokenImpersonationLevel.Impersonation,
                    busyWait: TimeSpan.Zero));

            Assert.Equal(231, ((Win32Exception)overflow.InnerException!).NativeErrorCode);
        }
        finally
        {
            foreach (var client in silent)
            {
                client.Dispose();
            }
        }

        await harness.ReadyAsync();
    }

    [Fact]
    public async Task Listener_ReclaimsASlotHeldByASilentClientOnlyAfterTheTimeout()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();

        using var silent = ChangeFeedPipeFactory.Connect(
            harness.PipeName,
            TokenImpersonationLevel.Impersonation);

        await WaitUntilAsync(
            () => harness.Server.AvailableSlots == ChangeFeedProtocol.MaximumConcurrentConnections - 1,
            TimeSpan.FromSeconds(5));

        await Task.Delay(ChangeFeedProtocol.IoTimeout - TimeSpan.FromSeconds(2));
        Assert.Equal(
            ChangeFeedProtocol.MaximumConcurrentConnections - 1,
            harness.Server.AvailableSlots);

        await WaitUntilAsync(
            () => harness.Server.AvailableSlots == ChangeFeedProtocol.MaximumConcurrentConnections,
            TimeSpan.FromSeconds(15));

        var response = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.ListRoots));
        Assert.Equal(ChangeFeedResponseStatus.Ok, response.Status);
    }

    [Fact]
    public async Task Listener_StopsAsAUnitWhenOneInstanceOfThePoolCannotBeCreated()
    {
        using var workspace = new TemporaryDirectory();
        var pipeName = "OmniSpot.Test." + Guid.NewGuid().ToString("N");
        var trustedRoot = Path.Combine(workspace.Path, "Guvenilir");

        var service = new ChangeFeedAdmissionService(
            new ChangeFeedRootAdmission(new UsnFileSystemIdentityProbe()),
            ownerSid => new FileSystemChangeFeedStore(
                ChangeFeedStoreLayout.ForTrustedOwner(trustedRoot, ownerSid, CurrentSid().Value)));

        var created = new List<System.IO.Pipes.NamedPipeServerStream>();
        var server = new ChangeFeedPipeServer(
            service,
            pipeName,
            CurrentSid(),
            null,
            firstInstance =>
            {
                if (created.Count == 2)
                {
                    throw new ChangeFeedPipeException("Havuz instance'ı kurulamadı.");
                }

                var pipe = ChangeFeedPipeFactory.Create(pipeName, firstInstance, CurrentSid());
                created.Add(pipe);
                return pipe;
            });

        var failure = await Assert.ThrowsAsync<ChangeFeedPipeException>(
            () => server.ListenAsync(CancellationToken.None));

        Assert.Contains("Havuz instance'ı kurulamadı", failure.Message, StringComparison.Ordinal);
        Assert.Equal(2, created.Count);
        Assert.Equal(0, server.AvailableSlots);

        var orphan = Assert.Throws<ChangeFeedPipeException>(
            () => ChangeFeedPipeFactory.Connect(
                pipeName,
                TokenImpersonationLevel.Impersonation,
                busyWait: TimeSpan.Zero));

        Assert.Equal(2, ((Win32Exception)orphan.InnerException!).NativeErrorCode);
    }

    [Fact]
    public async Task Listener_StopsAsAUnitWhenAReplacementInstanceCannotBeCreated()
    {
        using var workspace = new TemporaryDirectory();
        var pipeName = "OmniSpot.Test." + Guid.NewGuid().ToString("N");
        var trustedRoot = Path.Combine(workspace.Path, "Guvenilir");

        var service = new ChangeFeedAdmissionService(
            new ChangeFeedRootAdmission(new UsnFileSystemIdentityProbe()),
            ownerSid => new FileSystemChangeFeedStore(
                ChangeFeedStoreLayout.ForTrustedOwner(trustedRoot, ownerSid, CurrentSid().Value)));

        var created = 0;
        var server = new ChangeFeedPipeServer(
            service,
            pipeName,
            CurrentSid(),
            null,
            firstInstance =>
            {
                if (Interlocked.Increment(ref created) > ChangeFeedProtocol.MaximumConcurrentConnections)
                {
                    throw new ChangeFeedPipeException("Yerine geçen instance kurulamadı.");
                }

                return ChangeFeedPipeFactory.Create(pipeName, firstInstance, CurrentSid());
            });

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var listening = server.ListenAsync(stop.Token);

        await WaitUntilAsync(
            () => server.AvailableSlots == ChangeFeedProtocol.MaximumConcurrentConnections,
            TimeSpan.FromSeconds(10));

        var client = new ChangeFeedClient(
            pipeName,
            new HashSet<SecurityIdentifier> { CurrentSid() });

        var response = await client.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.ListRoots),
            CancellationToken.None);
        Assert.Equal(ChangeFeedResponseStatus.Ok, response.Status);

        var failure = await Assert.ThrowsAsync<ChangeFeedPipeException>(() => listening);
        Assert.Contains("Yerine geçen instance kurulamadı", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, server.AvailableSlots);

        var orphan = Assert.Throws<ChangeFeedPipeException>(
            () => ChangeFeedPipeFactory.Connect(
                pipeName,
                TokenImpersonationLevel.Impersonation,
                busyWait: TimeSpan.Zero));

        Assert.Equal(2, ((Win32Exception)orphan.InnerException!).NativeErrorCode);
    }

    [Fact]
    public async Task Listener_NeverLeavesTheNameUnservedWhileEveryWorkerRecyclesAtOnce()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();

        var silent = new List<System.IO.Pipes.NamedPipeClientStream>();
        var unserved = 0;
        var observed = 0;
        var probing = true;
        Task? probe = null;

        try
        {
            for (var i = 0; i < ChangeFeedProtocol.MaximumConcurrentConnections; i++)
            {
                silent.Add(ChangeFeedPipeFactory.Connect(
                    harness.PipeName,
                    TokenImpersonationLevel.Impersonation));
            }

            await WaitUntilAsync(() => harness.Server.AvailableSlots == 0, TimeSpan.FromSeconds(5));

            probe = Task.Run(() =>
            {
                while (Volatile.Read(ref probing))
                {
                    try
                    {
                        using var attempt = ChangeFeedPipeFactory.Connect(
                            harness.PipeName,
                            TokenImpersonationLevel.Impersonation,
                            busyWait: TimeSpan.Zero);
                    }
                    catch (ChangeFeedPipeException failure)
                        when (failure.InnerException is Win32Exception win32)
                    {
                        Interlocked.Increment(ref observed);

                        if (win32.NativeErrorCode == 2)
                        {
                            Interlocked.Increment(ref unserved);
                        }
                    }
                }
            });

            await Task.Delay(ChangeFeedProtocol.IoTimeout + TimeSpan.FromSeconds(2));
        }
        finally
        {
            Volatile.Write(ref probing, false);

            if (probe is not null)
            {
                await probe;
            }

            foreach (var client in silent)
            {
                client.Dispose();
            }
        }

        Assert.Null(harness.ListenerFault);
        Assert.True(observed > 0, "Prob hiç red gözlemedi; ölçüm anlamsız.");
        Assert.Equal(0, unserved);
        await harness.ReadyAsync();
    }

    [Fact]
    public async Task Listener_StopsAsAUnitWhenItCannotAcceptAtAll()
    {
        using var workspace = new TemporaryDirectory();
        var pipeName = "OmniSpot.Test." + Guid.NewGuid().ToString("N");
        var trustedRoot = Path.Combine(workspace.Path, "Guvenilir");

        var service = new ChangeFeedAdmissionService(
            new ChangeFeedRootAdmission(new UsnFileSystemIdentityProbe()),
            ownerSid => new FileSystemChangeFeedStore(
                ChangeFeedStoreLayout.ForTrustedOwner(trustedRoot, ownerSid, CurrentSid().Value)));

        var server = new ChangeFeedPipeServer(
            service,
            pipeName,
            CurrentSid(),
            null,
            firstInstance =>
            {
                var pipe = ChangeFeedPipeFactory.Create(pipeName, firstInstance, CurrentSid());
                pipe.Dispose();
                return pipe;
            });

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => server.ListenAsync(stop.Token));

        Assert.Equal(0, server.AvailableSlots);
    }

    [Fact]
    public async Task Listener_SurvivesAClientThatConnectsAndVanishesRepeatedly()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();

        for (var i = 0; i < 200; i++)
        {
            ChangeFeedPipeFactory.Connect(
                harness.PipeName,
                TokenImpersonationLevel.Impersonation).Dispose();
        }

        Assert.Null(harness.ListenerFault);
        await harness.ReadyAsync();

        var response = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.ListRoots));
        Assert.Equal(ChangeFeedResponseStatus.Ok, response.Status);
    }

    [Fact]
    public async Task Listener_KeepsEveryWorkerWhenTheFaultObserverItselfThrows()
    {
        using var harness = new Harness(faultObserverThrows: true);
        await harness.ReadyAsync();

        using (var abandoned = ChangeFeedPipeFactory.Connect(
            harness.PipeName,
            TokenImpersonationLevel.Impersonation))
        {
            await WaitUntilAsync(
                () => harness.Server.AvailableSlots
                    < ChangeFeedProtocol.MaximumConcurrentConnections,
                TimeSpan.FromSeconds(5));
        }

        for (var i = 0; i < ChangeFeedProtocol.MaximumConcurrentConnections * 4; i++)
        {
            ChangeFeedPipeFactory.Connect(
                harness.PipeName,
                TokenImpersonationLevel.Impersonation).Dispose();
        }

        await WaitUntilAsync(
            () => harness.FaultCount >= ChangeFeedProtocol.MaximumConcurrentConnections,
            TimeSpan.FromSeconds(10));

        Assert.Null(harness.ListenerFault);
        await harness.ReadyAsync();

        var response = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.ListRoots));
        Assert.Equal(ChangeFeedResponseStatus.Ok, response.Status);
    }

    [Fact]
    public async Task Client_IsServedByAReplacementInstanceWithinTheDefaultBudget()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();

        var silent = new List<System.IO.Pipes.NamedPipeClientStream>();

        try
        {
            for (var i = 0; i < ChangeFeedProtocol.MaximumConcurrentConnections; i++)
            {
                silent.Add(ChangeFeedPipeFactory.Connect(
                    harness.PipeName,
                    TokenImpersonationLevel.Impersonation));
            }

            await WaitUntilAsync(() => harness.Server.AvailableSlots == 0, TimeSpan.FromSeconds(5));

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var pending = Task.Run(() => harness.SendAsync(
                new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.ListRoots)));

            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Assert.False(pending.IsCompleted, "İstemci dolu sunucuda beklemeden sonuçlandı.");

            silent[0].Dispose();
            silent.RemoveAt(0);

            var response = await pending;
            clock.Stop();

            Assert.Equal(ChangeFeedResponseStatus.Ok, response.Status);
            Assert.True(
                clock.Elapsed < ChangeFeedProtocol.IoTimeout,
                $"Yanıt bütçe dolduktan sonra geldi: {clock.Elapsed}.");
        }
        finally
        {
            foreach (var client in silent)
            {
                client.Dispose();
            }
        }

        await harness.ReadyAsync();
    }

    [Fact]
    public async Task Client_HonoursCancellationWhileEverySlotIsHeld()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();

        var silent = new List<System.IO.Pipes.NamedPipeClientStream>();

        try
        {
            for (var i = 0; i < ChangeFeedProtocol.MaximumConcurrentConnections; i++)
            {
                silent.Add(ChangeFeedPipeFactory.Connect(
                    harness.PipeName,
                    TokenImpersonationLevel.Impersonation));
            }

            await WaitUntilAsync(() => harness.Server.AvailableSlots == 0, TimeSpan.FromSeconds(5));

            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var started = DateTime.UtcNow;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => harness.Client.SendAsync(
                    new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.ListRoots),
                    cancel.Token));

            var waited = DateTime.UtcNow - started;
            Assert.True(
                waited < ChangeFeedProtocol.IoTimeout,
                $"İptal {waited.TotalMilliseconds:0} ms sürdü, IoTimeout'tan kısa olmalıydı.");

            Assert.Empty(harness.StoredRoots());
        }
        finally
        {
            foreach (var client in silent)
            {
                client.Dispose();
            }
        }
    }

    [Fact]
    public void Client_WaitsExactlyTheProtocolBudgetByDefault()
    {
        Assert.Equal(ChangeFeedProtocol.IoTimeout, ChangeFeedClient.DefaultBusyWait);
        Assert.Equal(
            ChangeFeedProtocol.IoTimeout,
            new ChangeFeedClient(ChangeFeedProtocol.PipeName).BusyWait);
    }

    [Fact]
    public async Task Client_FailsClosedWhenTheWaitBudgetRunsOutWhileEverySlotIsHeld()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();

        var budget = TimeSpan.FromSeconds(1);
        var impatient = new ChangeFeedClient(
            harness.PipeName,
            new HashSet<SecurityIdentifier> { CurrentSid() },
            budget);

        var silent = new List<System.IO.Pipes.NamedPipeClientStream>();

        try
        {
            for (var i = 0; i < ChangeFeedProtocol.MaximumConcurrentConnections; i++)
            {
                silent.Add(ChangeFeedPipeFactory.Connect(
                    harness.PipeName,
                    TokenImpersonationLevel.Impersonation));
            }

            await WaitUntilAsync(() => harness.Server.AvailableSlots == 0, TimeSpan.FromSeconds(5));

            var root = harness.Workspace.CreateDirectory("Projeler");

            var started = DateTime.UtcNow;
            var response = await impatient.SendAsync(
                new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root),
                CancellationToken.None);
            var waited = DateTime.UtcNow - started;

            Assert.Equal(ChangeFeedResponseStatus.Unavailable, response.Status);
            Assert.False(Directory.Exists(harness.OwnerDirectory()));
            Assert.True(
                waited >= budget - TimeSpan.FromMilliseconds(300),
                $"Bütçe dolmadan reddedildi: {waited.TotalMilliseconds:0} ms.");
            Assert.True(
                waited < ChangeFeedProtocol.IoTimeout,
                $"Bütçeden çok sonra reddedildi: {waited.TotalMilliseconds:0} ms.");
            Assert.Equal(0, harness.Server.AvailableSlots);
            Assert.Empty(harness.StoredRoots());
        }
        finally
        {
            foreach (var client in silent)
            {
                client.Dispose();
            }
        }
    }

    [Fact]
    public async Task Subscription_SurvivesConcurrentAddRemoveAndReadsWithoutLoss()
    {
        using var harness = new Harness();
        var kept = harness.Workspace.CreateDirectory("Kalici");
        var churned = harness.Workspace.CreateDirectory("Degisken");

        await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, kept));

        var stop = new CancellationTokenSource();
        var readerFaults = new List<Exception>();

        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var roots = harness.StoredRoots();
                    if (!roots.Contains(kept))
                    {
                        lock (readerFaults)
                        {
                            readerFaults.Add(new InvalidOperationException("Kalıcı kök snapshot'tan düştü."));
                        }
                    }
                }
                catch (Exception failure)
                {
                    lock (readerFaults)
                    {
                        readerFaults.Add(failure);
                    }
                }
            }
        })).ToArray();

        var drainFaults = new List<Exception>();
        var draining = Task.Run(() =>
        {
            var layout = harness.LayoutForCurrentUser();
            var runner = new UsnDrainRunner(
                layout,
                new FileSystemChangeFeedStore(layout),
                new UsnVolumeJournalReaderFactory(),
                new UsnFileSystemIdentityProbe());

            while (!stop.IsCancellationRequested)
            {
                try
                {
                    runner.Run(stop.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception failure)
                {
                    lock (drainFaults)
                    {
                        drainFaults.Add(failure);
                    }
                }
            }
        });

        var clientFaults = new List<string>();

        var mutators = Enumerable.Range(0, 3).Select(lane => Task.Run(async () =>
        {
            var mine = lane == 0 ? churned : harness.Workspace.CreateDirectory("Degisken" + lane);

            for (var i = 0; i < 20; i++)
            {
                foreach (var kind in new[]
                         {
                             ChangeFeedRequestKind.AddRoot,
                             ChangeFeedRequestKind.RemoveRoot
                         })
                {
                    try
                    {
                        var response = await harness.SendAsync(
                            new ChangeFeedRequest(ChangeFeedProtocol.Version, kind, mine));

                        if (response.Status != ChangeFeedResponseStatus.Ok)
                        {
                            lock (clientFaults)
                            {
                                clientFaults.Add($"{kind} -> {response.Status} {response.Message}");
                            }
                        }
                    }
                    catch (Exception failure)
                    {
                        lock (clientFaults)
                        {
                            clientFaults.Add($"{kind} -> {failure.Message}");
                        }
                    }
                }
            }
        })).ToArray();

        await Task.WhenAll(mutators);

        stop.Cancel();
        await Task.WhenAll(readers);
        await draining;

        Assert.Empty(drainFaults.Select(failure => failure.ToString()).ToArray());
        Assert.Empty(harness.Faults.Select(failure => failure.ToString()).ToArray());
        Assert.Empty(clientFaults);
        Assert.Empty(readerFaults);
        Assert.Equal(new[] { kept }, harness.StoredRoots());
    }

    [Fact]
    public async Task CancelledRequests_LeaveNoHalfWrittenAdmissionRecord()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();
        var root = harness.Workspace.CreateDirectory("Projeler");

        await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

        for (var attempt = 0; attempt < 40; attempt++)
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromTicks(attempt * 2000));
            var kind = attempt % 2 == 0
                ? ChangeFeedRequestKind.AddRoot
                : ChangeFeedRequestKind.RemoveRoot;

            try
            {
                await harness.SendAsync(
                    new ChangeFeedRequest(ChangeFeedProtocol.Version, kind, root),
                    cancel.Token);
            }
            catch (OperationCanceledException)
            {
            }

            var store = new FileSystemChangeFeedStore(harness.LayoutForCurrentUser());

            ChangeFeedSubscription? state = null;
            var read = Record.Exception(() => state = store.ReadSubscription());
            Assert.Null(read);

            var roots = state?.Roots.Select(entry => entry.RootPath).ToArray()
                ?? Array.Empty<string>();

            Assert.True(
                roots.Length == 0 || (roots.Length == 1 && roots[0] == root),
                $"Ara durum beklenmedik: [{string.Join(", ", roots)}]");
        }

        Assert.Empty(Directory.GetFiles(
            harness.OwnerDirectory(),
            "*.tmp",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CorruptSubscription_FailsClosedInsteadOfBeingSilentlyReplaced()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Projeler");

        await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

        var subscription = harness.SubscriptionPath();
        File.WriteAllText(subscription, "{ bozuk");

        var response = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

        Assert.Equal(ChangeFeedResponseStatus.Unavailable, response.Status);
        Assert.Equal("{ bozuk", File.ReadAllText(subscription));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(condition(), "Beklenen durum süresi içinde oluşmadı.");
    }

    private static void Deny(string path)
    {
        var directory = new DirectoryInfo(path);
        var access = directory.GetAccessControl();
        access.SetAccessRuleProtection(true, false);
        access.AddAccessRule(new FileSystemAccessRule(
            CurrentSid(),
            FileSystemRights.ListDirectory | FileSystemRights.ReadData,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny));
        directory.SetAccessControl(access);
    }

    private static void Undeny(string path)
    {
        var directory = new DirectoryInfo(path);
        var access = directory.GetAccessControl();
        access.SetAccessRuleProtection(false, true);
        access.RemoveAccessRuleAll(new FileSystemAccessRule(
            CurrentSid(),
            FileSystemRights.ListDirectory | FileSystemRights.ReadData,
            AccessControlType.Deny));
        directory.SetAccessControl(access);
    }

    private static SecurityIdentifier CurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!;
    }

    private sealed class Harness : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _listening;

        public Harness(bool listen = true, bool faultObserverThrows = false)
        {
            Workspace = new TemporaryDirectory();
            TrustedRoot = Path.Combine(Workspace.Path, "Guvenilir");
            PipeName = "OmniSpot.Test." + Guid.NewGuid().ToString("N");

            var service = new ChangeFeedAdmissionService(
                new ChangeFeedRootAdmission(new UsnFileSystemIdentityProbe()),
                ownerSid => new FileSystemChangeFeedStore(Layout(ownerSid)));

            Server = new ChangeFeedPipeServer(
                service,
                PipeName,
                additionalServerPrincipal: CurrentSid(),
                onFault: failure =>
                {
                    lock (Faults)
                    {
                        Faults.Add(failure);
                    }

                    if (faultObserverThrows)
                    {
                        throw new InvalidOperationException("Hata gözlemcisi bilerek patlıyor.");
                    }
                });

            Client = new ChangeFeedClient(
                PipeName,
                new HashSet<SecurityIdentifier> { CurrentSid() });

            _listening = listen
                ? Server.ListenAsync(_cancellation.Token)
                : Task.CompletedTask;
        }

        public List<Exception> Faults { get; } = new();

        public int FaultCount
        {
            get
            {
                lock (Faults)
                {
                    return Faults.Count;
                }
            }
        }

        public TemporaryDirectory Workspace { get; }

        public string TrustedRoot { get; }

        public string PipeName { get; }

        public ChangeFeedPipeServer Server { get; }

        public ChangeFeedClient Client { get; }

        public Task<ChangeFeedResponse> SendAsync(
            ChangeFeedRequest request,
            CancellationToken cancellationToken = default) =>
            Client.SendAsync(request, cancellationToken);

        public ChangeFeedStoreLayout LayoutForCurrentUser() => Layout(CurrentSid().Value);

        public Exception? ListenerFault =>
            _listening.IsFaulted ? _listening.Exception!.GetBaseException() : null;

        public Task ReadyAsync() => WaitUntilAsync(
            () => Server.AvailableSlots == ChangeFeedProtocol.MaximumConcurrentConnections,
            TimeSpan.FromSeconds(10));

        public string OwnerDirectory() => Layout(CurrentSid().Value).OwnerDirectory;

        public string SubscriptionPath() => Layout(CurrentSid().Value).SubscriptionPath;

        public IReadOnlyList<string> StoredRoots()
        {
            var store = new FileSystemChangeFeedStore(Layout(CurrentSid().Value));
            return store.ReadSubscription()?.Roots.Select(root => root.RootPath).ToArray()
                ?? Array.Empty<string>();
        }

        private ChangeFeedStoreLayout Layout(string ownerSid) =>
            ChangeFeedStoreLayout.ForTrustedOwner(TrustedRoot, ownerSid, CurrentSid().Value);

        public void Dispose()
        {
            _cancellation.Cancel();

            try
            {
                _listening.Wait(TimeSpan.FromSeconds(10));
            }
            catch (AggregateException)
            {
            }

            _cancellation.Dispose();
            Workspace.Dispose();
        }
    }
}
