using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.ChangeFeed.Usn;

namespace OmniSpot.Benchmarking.Ntfs;

internal static class SharedFirstReadyCommand
{
    private const string ServerCommandName = "shared-first-ready-server";
    private const string VolumeRoot = @"C:\";
    internal const string ServiceNamePrefix = "OmniSpotBenchmark-";
    internal const string PipeNamePrefix = "OmniSpot-Benchmark-";

    internal static Command CreateCommand(long processEntryTimestamp)
    {
        var command = new Command("shared-first-ready",
            "İzole production ortak katalog hattında C: ilk hazır süresini ölçer.");
        var output = new Option<string>("--output") { Required = true };
        command.Options.Add(output);
        command.SetAction((result, cancellationToken) =>
            RunDriverAsync(result.GetValue(output)!, processEntryTimestamp, cancellationToken));
        return command;
    }

    internal static Command CreateServiceCommand()
    {
        var command = new Command("shared-first-ready-service");
        command.Hidden = true;
        var root = new Option<string>("--run-root") { Required = true };
        var pipe = new Option<string>("--pipe") { Required = true };
        var serviceName = new Option<string>("--service-name") { Required = true };
        var ownerSid = new Option<string>("--owner-sid") { Required = true };
        command.Options.Add(root);
        command.Options.Add(pipe);
        command.Options.Add(serviceName);
        command.Options.Add(ownerSid);
        command.SetAction((result, cancellationToken) => SharedFirstReadyServiceHost.RunAsync(
            result.GetValue(root)!, result.GetValue(pipe)!, result.GetValue(serviceName)!,
            result.GetValue(ownerSid)!, cancellationToken));
        return command;
    }

    internal static Command CreateClientCommand(long processEntryTimestamp)
    {
        var command = new Command("shared-first-ready-client",
            "Geçici LocalSystem benchmark servisi üzerinden C: ilk hazır süresini ölçer.");
        var output = new Option<string>("--output") { Required = true };
        var pipe = new Option<string>("--pipe") { Required = true };
        var stopAfterBuild = new Option<bool>("--stop-after-build");
        command.Options.Add(output);
        command.Options.Add(pipe);
        command.Options.Add(stopAfterBuild);
        command.SetAction((result, cancellationToken) => RunServiceClientAsync(
            result.GetValue(output)!, result.GetValue(pipe)!, result.GetValue(stopAfterBuild),
            processEntryTimestamp, cancellationToken));
        return command;
    }

    internal static Command CreateServerCommand()
    {
        var command = new Command(ServerCommandName);
        command.Hidden = true;
        var root = new Option<string>("--run-root") { Required = true };
        var pipe = new Option<string>("--pipe") { Required = true };
        command.Options.Add(root);
        command.Options.Add(pipe);
        command.SetAction((result, cancellationToken) =>
            RunServerAsync(result.GetValue(root)!, result.GetValue(pipe)!, cancellationToken));
        return command;
    }

    private static async Task<int> RunDriverAsync(string output, long processTimestamp,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string? runRoot = null;
        Process? server = null;
        Task<string>? serverErrors = null;
        var timeline = new List<TimingPoint>
        {
            new("process-entry", processTimestamp, 0),
            Point("handler-start", processTimestamp)
        };
        SharedCatalogStatus? catalog = null;

        try
        {
            RequireWindowsAdministrator();
            runRoot = RequireNewAbsoluteRoot(output);
            var storeRoot = ChildPath(runRoot, "trusted-store");
            var controlPath = ChildPath(runRoot, "client", "catalog.control");
            var resultPath = ChildPath(runRoot, "result.json");
            Directory.CreateDirectory(runRoot);

            var pipeName = PipeNamePrefix + Guid.NewGuid().ToString("N");
            var startInfo = CreateServerStartInfo(runRoot, pipeName);
            server = Process.Start(startInfo) ?? throw new InvalidOperationException("ServerStartFailed");
            timeline.Add(Point("server-process-start", processTimestamp));
            serverErrors = server.StandardError.ReadToEndAsync(cancellationToken);

            var owner = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("CurrentSidUnavailable");
            var client = new ChangeFeedClient(pipeName,
                new HashSet<SecurityIdentifier> { owner }, TimeSpan.FromMilliseconds(250));
            await WaitForServerAsync(client, server, cancellationToken).ConfigureAwait(false);
            timeline.Add(Point("server-ready", processTimestamp));

            catalog = await OpenClientAsync(client, controlPath, timeline, processTimestamp,
                cancellationToken).ConfigureAwait(false);

            var report = new
            {
                Classification = "elevated-admin exploratory",
                Completed = true,
                Source = new { Kind = "live-ntfs-volume", Root = VolumeRoot },
                Options = new { ScanMethod = IndexScanMethod.Mft.ToString(), Isolated = true },
                StopwatchFrequency = Stopwatch.Frequency,
                Timeline = timeline,
                Catalog = new
                {
                    catalog.Phase,
                    catalog.Items,
                    catalog.ElapsedMilliseconds,
                    catalog.Files,
                    catalog.Directories,
                    catalog.Tokens,
                    PartCount = catalog.Parts.Count,
                    Parts = catalog.Parts.Select(part => new
                    {
                        part.Items,
                        part.ExcludeRoot,
                        part.Generation
                    }).ToArray(),
                    Build = catalog.Build is null ? null : new
                    {
                        catalog.Build.ReadAndIngestMilliseconds,
                        catalog.Build.PrepareMilliseconds,
                        catalog.Build.CommitMilliseconds,
                        catalog.Build.LinkedFoldersMilliseconds
                    }
                },
                FailureType = (string?)null
            };
            await WriteAtomicAsync(resultPath, report, cancellationToken).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                report.Completed,
                ReadyMilliseconds = timeline[^1].ElapsedMilliseconds,
                catalog.Items,
                PartCount = catalog.Parts.Count
            }));
            return 0;
        }
        catch (Exception failure) when (runRoot is not null)
        {
            var resultPath = ChildPath(runRoot, "result.json");
            var report = new
            {
                Classification = "elevated-admin exploratory",
                Completed = false,
                Source = new { Kind = "live-ntfs-volume", Root = VolumeRoot },
                Options = new { ScanMethod = IndexScanMethod.Mft.ToString(), Isolated = true },
                StopwatchFrequency = Stopwatch.Frequency,
                Timeline = timeline,
                FailureType = failure.GetType().Name
            };
            try { await WriteAtomicAsync(resultPath, report, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { }
            Console.Error.WriteLine(failure.GetType().Name);
            return 1;
        }
        finally
        {
            if (server is not null)
            {
                try { await server.StandardInput.WriteLineAsync("stop").ConfigureAwait(false); }
                catch (Exception) { }
                try
                {
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await server.WaitForExitAsync(stop.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    if (!server.HasExited) server.Kill(entireProcessTree: true);
                }
                if (serverErrors is not null)
                {
                    try { _ = await serverErrors.ConfigureAwait(false); }
                    catch (Exception) { }
                }
                server.Dispose();
            }
        }
    }

    private static async Task<int> RunServiceClientAsync(string output, string pipeName, bool stopAfterBuild,
        long processTimestamp, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string? runRoot = null;
        SharedCatalogStatus? lastStatus = null;
        var phases = new List<PhasePoint>();
        var timeline = new List<TimingPoint>
        {
            new("process-entry", processTimestamp, 0),
            Point("handler-start", processTimestamp)
        };
        try
        {
            RequireNormalUser();
            ValidatePipeName(pipeName);
            runRoot = RequireExistingAbsoluteRoot(output);
            var controlPath = ChildPath(runRoot, "client", "catalog.control");
            var resultPath = ChildPath(runRoot, "result.json");
            RequireAbsent(controlPath);
            RequireAbsent(resultPath);
            var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var client = new ChangeFeedClient(pipeName,
                new HashSet<SecurityIdentifier> { localSystem }, TimeSpan.FromSeconds(2));
            if (stopAfterBuild)
            {
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var recording = new RecordingChannel(new ChangeFeedClientChannel(client), status =>
                {
                    lastStatus = status;
                    if (phases.Count == 0 || phases[^1].Phase != status.Phase)
                        phases.Add(new(status.Phase, status.Items, status.ElapsedMilliseconds,
                            Stopwatch.GetElapsedTime(processTimestamp).TotalMilliseconds));
                    if (status.Phase == "Son dosya olayları uygulanıyor") stop.Cancel();
                });
                using var lifecycle = new SharedIndexLifecycleService(
                    recording, new FixedLocations(), IndexScanMethod.Mft, controlPath);
                timeline.Add(Point("client-start", processTimestamp));
                try { await lifecycle.InitializeAsync(stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested) { }
                if (!stop.IsCancellationRequested)
                    throw new InvalidDataException("PostBuildPhaseUnavailable");
                timeline.Add(Point("post-build", processTimestamp));
                await WriteAtomicAsync(resultPath,
                    CreateReport("localsystem-service phase-only exploratory", true, timeline,
                        lastStatus, null, observation: "post-build", phases: phases),
                    cancellationToken).ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    Completed = true,
                    Observation = "post-build",
                    Ready = false,
                    ElapsedMilliseconds = timeline[^1].ElapsedMilliseconds,
                    lastStatus?.Items
                }));
                return 0;
            }
            var catalog = await OpenClientAsync(client, controlPath, timeline, processTimestamp,
                cancellationToken, status => lastStatus = status).ConfigureAwait(false);
            var report = CreateReport("localsystem-service exploratory", true, timeline, catalog, null);
            await WriteAtomicAsync(resultPath, report, cancellationToken).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Completed = true,
                ReadyMilliseconds = timeline[^1].ElapsedMilliseconds,
                catalog.Items,
                PartCount = catalog.Parts.Count
            }));
            return 0;
        }
        catch (Exception failure) when (runRoot is not null)
        {
            var resultPath = ChildPath(runRoot, "result.json");
            try
            {
                await WriteAtomicAsync(resultPath,
                    CreateReport("localsystem-service exploratory", false, timeline, lastStatus,
                        failure.GetType().Name, failure.Message, failure.HResult,
                        phases: phases),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception) { }
            Console.Error.WriteLine(failure.GetType().Name);
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<SharedCatalogStatus> OpenClientAsync(ChangeFeedClient client,
        string controlPath, List<TimingPoint> timeline, long processTimestamp,
        CancellationToken cancellationToken, Action<SharedCatalogStatus>? onStatus = null)
    {
        var recording = new RecordingChannel(new ChangeFeedClientChannel(client), onStatus);
        using var lifecycle = new SharedIndexLifecycleService(
            recording, new FixedLocations(), IndexScanMethod.Mft, controlPath);
        timeline.Add(Point("client-start", processTimestamp));
        var startup = await lifecycle.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var catalog = recording.LatestCatalog
            ?? throw new InvalidOperationException("CatalogStatusUnavailable");
        var state = lifecycle.CreateSearchState(cancellationToken);
        using var lease = state as IDisposable;
        if (!lifecycle.IsInitialized || !catalog.Ready || startup.RootPaths.Count != 1 ||
            state.ItemCount <= 0 || !state.ContainsPath(VolumeRoot))
            throw new InvalidDataException("ReadyInvariantFailed");
        timeline.Add(Point("client-ready", processTimestamp));
        return catalog;
    }

    private static object CreateReport(string classification, bool completed,
        IReadOnlyList<TimingPoint> timeline, SharedCatalogStatus? catalog, string? failureType,
        string? failureMessage = null, int? failureHResult = null,
        string? observation = null, IReadOnlyList<PhasePoint>? phases = null) => new
    {
        Classification = classification,
        Completed = completed,
        Ready = catalog?.Ready ?? false,
        Observation = observation,
        Source = new { Kind = "live-ntfs-volume", Root = VolumeRoot },
        Options = new
        {
            ScanMethod = IndexScanMethod.Mft.ToString(),
            Isolated = true,
            ServerMode = "LocalSystemWindowsService"
        },
        StopwatchFrequency = Stopwatch.Frequency,
        Timeline = timeline,
        Phases = phases,
        Catalog = catalog is null ? null : new
        {
            catalog.Phase,
            catalog.Items,
            catalog.ElapsedMilliseconds,
            catalog.Files,
            catalog.Directories,
            catalog.Tokens,
            PartCount = catalog.Parts.Count,
            PartItems = catalog.Parts.Sum(part => part.Items),
            ExcludedRootParts = catalog.Parts.Count(part => part.ExcludeRoot),
            Build = catalog.Build is null ? null : new
            {
                catalog.Build.ReadAndIngestMilliseconds,
                catalog.Build.PrepareMilliseconds,
                catalog.Build.CommitMilliseconds,
                catalog.Build.LinkedFoldersMilliseconds
            }
        },
        FailureType = failureType,
        FailureMessage = failureMessage,
        FailureHResult = failureHResult
    };

    private static async Task<int> RunServerAsync(
        string runRoot,
        string pipeName,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        try
        {
            RequireWindowsAdministrator();
            runRoot = RequireExistingAbsoluteRoot(runRoot);
            var storeRoot = ChildPath(runRoot, "trusted-store");
            var sid = WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("CurrentSidUnavailable");
            var layout = ChangeFeedStoreLayout.ForTrustedOwner(storeRoot, sid, sid);
            var runnerStore = new FileSystemChangeFeedStore(layout);
            var runner = new UsnDrainRunner(layout, runnerStore,
                new UsnVolumeJournalReaderFactory(), new UsnFileSystemIdentityProbe(), cacheState: true);
            ChangeFeedStoreLayout LayoutFor(string owner) =>
                string.Equals(owner, sid, StringComparison.OrdinalIgnoreCase)
                    ? layout
                    : throw new UnauthorizedAccessException("OwnerMismatch");
            IChangeFeedStore StoreFor(string owner) =>
                string.Equals(owner, sid, StringComparison.OrdinalIgnoreCase)
                    ? new FileSystemChangeFeedStore(layout)
                    : throw new UnauthorizedAccessException("OwnerMismatch");

            using var admission = new ChangeFeedAdmissionService(
                new ChangeFeedRootAdmission(new UsnFileSystemIdentityProbe()),
                StoreFor,
                handoffDrainer: (_, ct) =>
                {
                    var result = runner.Run(ct);
                    return result.Outcome == UsnDrainOutcome.Completed && result.VolumesFaulted == 0;
                },
                inventory: new ChangeFeedInventorySessions(),
                sharedCatalogLayout: LayoutFor,
                catalogBootstrap: (_, ct) => runner.BeginBootstrap(ct));
            var server = new ChangeFeedPipeServer(admission, pipeName, additionalServerPrincipal: null);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var listening = server.ListenAsync(stop.Token);
            while (server.AvailableSlots == 0 && !listening.IsCompleted)
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            if (listening.IsCompleted) await listening.ConfigureAwait(false);
            Console.WriteLine("READY");
            Console.Out.Flush();
            _ = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            stop.Cancel();
            try { await listening.ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            return 0;
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine(failure.GetType().Name);
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task WaitForServerAsync(
        ChangeFeedClient client,
        Process server,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (server.HasExited) throw new InvalidOperationException("ServerExitedBeforeReady");
            var response = await client.SendAsync(new(
                ChangeFeedProtocol.Version,
                ChangeFeedRequestKind.GetCapabilities), cancellationToken).ConfigureAwait(false);
            if (response.Status == ChangeFeedResponseStatus.Ok) return;
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ProcessStartInfo CreateServerStartInfo(string runRoot, string pipeName)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("ProcessPathUnavailable");
        var entryPath = Assembly.GetEntryAssembly()?.Location
            ?? throw new InvalidOperationException("EntryAssemblyUnavailable");
        var start = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(entryPath);
        start.ArgumentList.Add(ServerCommandName);
        start.ArgumentList.Add("--run-root");
        start.ArgumentList.Add(runRoot);
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(pipeName);
        return start;
    }

    private static void RequireWindowsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("AdministratorTokenRequired");
    }

    private static void RequireNormalUser()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is null || identity.IsSystem ||
            new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("NormalUserTokenRequired");
    }

    private static string RequireNewAbsoluteRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("AbsoluteOutputRequired", nameof(path));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Directory.Exists(full) || File.Exists(full))
            throw new IOException("OutputAlreadyExists");
        _ = ChildPath(full, "trusted-store");
        _ = ChildPath(full, "client", "catalog.control");
        _ = ChildPath(full, "result.json");
        return full;
    }

    private static string RequireExistingAbsoluteRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("AbsoluteRunRootRequired", nameof(path));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("RunRootUnavailable");
        return full;
    }

    internal static void ValidateServiceIdentity(string serviceName, string pipeName)
    {
        var serviceId = ParseGuidSuffix(serviceName, ServiceNamePrefix, "ServiceNameInvalid");
        var pipeId = ParseGuidSuffix(pipeName, PipeNamePrefix, "PipeNameInvalid");
        if (serviceId != pipeId) throw new ArgumentException("ServicePipeIdentityMismatch");
    }

    internal static void ValidatePipeName(string pipeName) =>
        _ = ParseGuidSuffix(pipeName, PipeNamePrefix, "PipeNameInvalid");

    private static Guid ParseGuidSuffix(string value, string prefix, string failure)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(value[prefix.Length..], "N", out var id))
            throw new ArgumentException(failure);
        return id;
    }

    internal static string RequireServiceRunRoot(string path)
    {
        var root = RequireExistingAbsoluteRoot(path);
        RequireAbsent(ChildPath(root, "trusted-store"));
        RequireAbsent(ChildPath(root, "client", "catalog.control"));
        RequireAbsent(ChildPath(root, "result.json"));
        return root;
    }

    private static void RequireAbsent(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("OutputAlreadyExists");
    }

    internal static string ChildPath(string root, params string[] parts)
    {
        var candidate = Path.GetFullPath(parts.Aggregate(root, Path.Combine));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GeneratedPathEscapedRunRoot");
        return candidate;
    }

    private static TimingPoint Point(string name, long processTimestamp)
    {
        var timestamp = Stopwatch.GetTimestamp();
        return new(name, timestamp, Stopwatch.GetElapsedTime(processTimestamp, timestamp).TotalMilliseconds);
    }

    private static async Task WriteAtomicAsync(string path, object value, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, value,
                    new JsonSerializerOptions { WriteIndented = true }, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record TimingPoint(string Name, long Timestamp, double ElapsedMilliseconds);
    private sealed record PhasePoint(string Phase, int Items, double ServiceElapsedMilliseconds,
        double ClientElapsedMilliseconds);

    private sealed class FixedLocations : IIndexedLocationProvider
    {
        public IndexLocations Resolve() => new(VolumeRoot, new[] { VolumeRoot });
    }

    private sealed class RecordingChannel(IChangeFeedRequestChannel inner,
        Action<SharedCatalogStatus>? onStatus = null) : IChangeFeedRequestChannel
    {
        internal SharedCatalogStatus? LatestCatalog { get; private set; }

        public async Task<ChangeFeedResponse> SendAsync(
            ChangeFeedRequest request,
            CancellationToken cancellationToken)
        {
            var response = await inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.Catalog is { } catalog)
            {
                LatestCatalog = catalog;
                onStatus?.Invoke(catalog);
            }
            return response;
        }
    }
}
