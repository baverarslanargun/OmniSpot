using System.ComponentModel;
using System.IO;
using System.Text;
using SmartFileLauncher.Core.ChangeFeed.Store;

namespace SmartFileLauncher.Core.ChangeFeed.Usn;

public enum UsnDrainOutcome
{
    Completed,
    LeaseHeld,
    LeasePreempted,
    NoSubscription,
    SubscriptionRejected,
    Faulted
}

public sealed record UsnDrainResult(
    UsnDrainOutcome Outcome,
    int VolumesDrained,
    int VolumesFaulted,
    int EntriesWritten,
    int EventsWritten,
    int RootsGapped,
    string? Diagnostics = null);

public sealed class UsnDrainRunner
{
    private readonly ChangeFeedStoreLayout _layout;
    private readonly IChangeFeedStore _store;
    private readonly IUsnJournalReaderFactory _readerFactory;
    private readonly IUsnIdentityProbe _identityProbe;
    private readonly IUsnSubtreeReader? _subtreeReader;
    private readonly Func<DateTime> _utcNow;

    public UsnDrainRunner(
        ChangeFeedStoreLayout layout,
        IChangeFeedStore store,
        IUsnJournalReaderFactory readerFactory,
        IUsnIdentityProbe identityProbe,
        IUsnSubtreeReader? subtreeReader = null,
        Func<DateTime>? utcNow = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _readerFactory = readerFactory ?? throw new ArgumentNullException(nameof(readerFactory));
        _identityProbe = identityProbe ?? throw new ArgumentNullException(nameof(identityProbe));
        _subtreeReader = subtreeReader;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public UsnDrainResult Run(CancellationToken cancellationToken = default)
    {
        var following = _store.ReadLease().IsHeld(_utcNow());

        var subscription = _store.ReadSubscription();
        if (subscription is null)
        {
            return new UsnDrainResult(UsnDrainOutcome.NoSubscription, 0, 0, 0, 0, 0);
        }

        if (!string.Equals(
                subscription.OwnerSid,
                _layout.OwnerSid,
                StringComparison.OrdinalIgnoreCase))
        {
            return new UsnDrainResult(
                UsnDrainOutcome.SubscriptionRejected,
                0,
                0,
                0,
                0,
                0,
                $"Abonelik sahibi {subscription.OwnerSid}, depo sahibi {_layout.OwnerSid}.");
        }

        var partition = PartitionByVolume(subscription.Roots);

        var drained = 0;
        var faulted = 0;
        var entries = 0;
        var events = 0;
        var gapped = 0;
        var leaseArrived = false;
        string? diagnostics = null;

        if (partition.Unsupported.Count > 0 && !following)
        {
            var announced = AnnounceGap(
                partition.Unsupported,
                ChangeFeedGapReason.RootUnavailable,
                cancellationToken);

            if (announced is null)
            {
                return new UsnDrainResult(UsnDrainOutcome.LeasePreempted, 0, 0, 0, 0, 0);
            }

            entries += announced.Value;
            gapped += partition.Unsupported.Count;
            diagnostics = $"Desteklenmeyen kök: {partition.Unsupported[0].RootPath}";
        }

        foreach (var group in partition.Groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            VolumeDrain result;
            try
            {
                result = DrainVolume(
                    group.VolumeRoot,
                    group.Roots,
                    following,
                    cancellationToken);
            }
            catch (Exception failure) when (IsVolumeFailure(failure))
            {
                faulted++;
                gapped += group.Roots.Count;
                diagnostics ??= $"{group.VolumeRoot}: {failure.Message}";

                if (following)
                {
                    continue;
                }

                var announced = AnnounceGap(
                    group.Roots,
                    ChangeFeedGapReason.JournalUnavailable,
                    cancellationToken);

                if (announced is null)
                {
                    leaseArrived = true;
                    break;
                }

                entries += announced.Value;
                continue;
            }

            if (result.LeaseArrived)
            {
                leaseArrived = true;
                break;
            }

            drained++;
            entries += result.Entries;
            events += result.Events;
            gapped += result.Gapped;
            diagnostics = Combine(diagnostics, result.Diagnostics);

            if (result.Faulted)
            {
                faulted++;
            }
        }

        var outcome = leaseArrived
            ? UsnDrainOutcome.LeasePreempted
            : following
                ? UsnDrainOutcome.LeaseHeld
                : drained == 0 && faulted > 0
                    ? UsnDrainOutcome.Faulted
                    : UsnDrainOutcome.Completed;

        return new UsnDrainResult(outcome, drained, faulted, entries, events, gapped, diagnostics);
    }

    private VolumeDrain DrainVolume(
        string volumeRoot,
        IReadOnlyList<ChangeFeedSubscribedRoot> roots,
        bool following,
        CancellationToken cancellationToken)
    {
        var stateStore = new UsnChangeFeedStateStore(StatePath(volumeRoot));

        UsnVolumeFeedState? state;
        try
        {
            state = stateStore.Read();
        }
        catch (InvalidDataException)
        {
            state = null;
        }

        using var reader = _readerFactory.Open(volumeRoot);
        var descriptor = reader.QueryJournal();

        if (state is not null && state.JournalId != descriptor.JournalId)
        {
            state = null;
        }

        var admission = Admit(state, roots, descriptor, cancellationToken);
        if (admission.States.Count == 0)
        {
            return following
                ? new VolumeDrain(0, 0, 0, false, "takip durdu: hiçbir kök kabul edilmedi")
                : Announce(admission, descriptor, state, cancellationToken);
        }

        var journalId = descriptor.JournalId;
        var cursor = admission.States.Min(item => item.NextUsn);

        var projections = admission.States
            .Select(item => new UsnRootProjection(item, _identityProbe, _subtreeReader))
            .ToArray();

        using var feed = new UsnVolumeChangeFeed(reader, journalId, cursor, projections);
        var batch = feed.Read(cancellationToken);

        var deliveries = new List<ChangeFeedRootDelivery>(admission.Deliveries);
        foreach (var root in batch.Roots)
        {
            if (root.Batch.Status == ChangeFeedStatus.Ok && root.Batch.Events.Count == 0)
            {
                continue;
            }

            deliveries.Add(new ChangeFeedRootDelivery(
                root.Root.RootPath,
                root.Batch,
                GenerationOf(roots, root.Root.RootPath)));
        }

        var securityChanged = projections.Any(projection => projection.LastSecurityChanged);
        var rebuilt = RebuildGappedRoots(feed, batch, descriptor, cancellationToken);
        var anchorage = DescribeAnchorage(cursor, descriptor, GappedRoots(batch).Count, rebuilt.Count);

        if (following)
        {
            return Follow(
                feed,
                batch,
                rebuilt,
                stateStore,
                journalId,
                securityChanged || (state?.PendingSecurityChange ?? false),
                anchorage);
        }

        using var commitScope = _store.EnterOwnerScope(cancellationToken);

        if (_store.ReadLease().IsHeld(_utcNow()))
        {
            return VolumeDrain.LeaseTaken;
        }

        Discard(state);

        if (securityChanged || (state?.PendingSecurityChange ?? false))
        {
            _store.NoteSecurityChange();
        }

        var entries = 0;
        if (deliveries.Count > 0)
        {
            entries = _store.Enqueue(
                VolumeIdOf(admission.States),
                journalId,
                cursor,
                batch.NextUsn,
                deliveries).Count;
        }

        feed.Accept();

        var resynchronized = Capture(feed, batch, rebuilt);
        if (resynchronized.Count == 0)
        {
            stateStore.Delete();
        }
        else
        {
            stateStore.Write(journalId, CursorOf(resynchronized), resynchronized);
        }

        return new VolumeDrain(
            entries,
            deliveries.Sum(delivery => delivery.Batch.Events.Count),
            deliveries.Count(delivery => delivery.Batch.HasGap),
            batch.Roots.Any(root => root.Batch.IsFaulted),
            Combine(
                batch.Roots.FirstOrDefault(root => root.Batch.IsFaulted)?.Batch.Diagnostics,
                Combine(DescribeGaps(deliveries), anchorage)));
    }

    private VolumeDrain Follow(
        UsnVolumeChangeFeed feed,
        UsnVolumeBatch batch,
        IReadOnlyDictionary<string, UsnChangeFeedState> rebuilt,
        UsnChangeFeedStateStore stateStore,
        ulong journalId,
        bool securityPending,
        string? anchorage)
    {
        if (!_store.ReadLease().IsHeld(_utcNow()))
        {
            return new VolumeDrain(0, 0, 0, false, Combine("takip durdu: kira düştü", anchorage));
        }

        feed.Accept();

        var followed = Capture(feed, batch, rebuilt);
        if (followed.Count == 0)
        {
            return new VolumeDrain(
                0,
                0,
                0,
                false,
                Combine("takip durdu: kök durumu yakalanamadı", anchorage));
        }

        stateStore.Write(journalId, CursorOf(followed), followed, securityPending);

        return new VolumeDrain(0, 0, 0, false, anchorage);
    }

    private Admission Admit(
        UsnVolumeFeedState? state,
        IReadOnlyList<ChangeFeedSubscribedRoot> roots,
        UsnJournalDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var known = state?.Roots.ToDictionary(
            item => item.RootPath,
            StringComparer.OrdinalIgnoreCase);

        var states = new List<UsnChangeFeedState>();
        var deliveries = new List<ChangeFeedRootDelivery>();

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (known is not null &&
                known.TryGetValue(root.RootPath, out var existing) &&
                existing.ToChangeFeedRootIdentity() == root.Identity &&
                existing.SynchronizedFromUsn <= descriptor.NextUsn)
            {
                states.Add(existing);
                continue;
            }

            if (!TryBootstrap(root.RootPath, descriptor, cancellationToken, out var fresh))
            {
                deliveries.Add(new ChangeFeedRootDelivery(
                    root.RootPath,
                    ChangeFeedBatch.Gap(ChangeFeedGapReason.RootUnavailable),
                    root.Generation));
                continue;
            }

            if (fresh.ToChangeFeedRootIdentity() != root.Identity)
            {
                deliveries.Add(new ChangeFeedRootDelivery(
                    root.RootPath,
                    ChangeFeedBatch.Gap(ChangeFeedGapReason.RootIdentityChanged),
                    root.Generation));
                continue;
            }

            states.Add(fresh);
            deliveries.Add(new ChangeFeedRootDelivery(
                root.RootPath,
                ChangeFeedBatch.Gap(ChangeFeedGapReason.NotYetSynchronized),
                root.Generation));
        }

        return new Admission(states, deliveries);
    }

    private IReadOnlyDictionary<string, UsnChangeFeedState> RebuildGappedRoots(
        UsnVolumeChangeFeed feed,
        UsnVolumeBatch batch,
        UsnJournalDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var gapped = GappedRoots(batch);
        var rebuilt = new Dictionary<string, UsnChangeFeedState>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var projection in feed.Roots)
        {
            if (!gapped.Contains(projection.RootPath))
            {
                continue;
            }

            if (TryBootstrap(projection.RootPath, descriptor, cancellationToken, out var state) &&
                state.ToChangeFeedRootIdentity() == projection.RootIdentity)
            {
                rebuilt[projection.RootPath] = state;
            }
        }

        return rebuilt;
    }

    private static IReadOnlyList<UsnChangeFeedState> Capture(
        UsnVolumeChangeFeed feed,
        UsnVolumeBatch batch,
        IReadOnlyDictionary<string, UsnChangeFeedState> rebuilt)
    {
        var gapped = GappedRoots(batch);
        var states = new List<UsnChangeFeedState>(feed.Roots.Count);

        foreach (var projection in feed.Roots)
        {
            if (!gapped.Contains(projection.RootPath))
            {
                states.Add(projection.CaptureState(feed.JournalId, feed.AcceptedUsn));
                continue;
            }

            if (rebuilt.TryGetValue(projection.RootPath, out var state))
            {
                states.Add(state);
            }
        }

        return states;
    }

    private static HashSet<string> GappedRoots(UsnVolumeBatch batch) =>
        batch.Roots
            .Where(root => root.Batch.HasGap)
            .Select(root => root.Root.RootPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void Discard(UsnVolumeFeedState? state)
    {
        if (state is null)
        {
            return;
        }

        _store.DiscardUncommitted(
            VolumeIdOf(state.Roots),
            state.JournalId,
            state.NextUsn);
    }

    private VolumeDrain Announce(
        Admission admission,
        UsnJournalDescriptor descriptor,
        UsnVolumeFeedState? state,
        CancellationToken cancellationToken)
    {
        using var commitScope = _store.EnterOwnerScope(cancellationToken);

        if (_store.ReadLease().IsHeld(_utcNow()))
        {
            return VolumeDrain.LeaseTaken;
        }

        Discard(state);

        if (admission.Deliveries.Count == 0)
        {
            return new VolumeDrain(0, 0, 0, false, null);
        }

        var entries = _store
            .Enqueue(string.Empty, descriptor.JournalId, 0, 0, admission.Deliveries)
            .Count;

        return new VolumeDrain(
            entries,
            0,
            admission.Deliveries.Count,
            false,
            DescribeGaps(admission.Deliveries));
    }

    private int? AnnounceGap(
        IReadOnlyList<ChangeFeedSubscribedRoot> roots,
        ChangeFeedGapReason reason,
        CancellationToken cancellationToken)
    {
        using var commitScope = _store.EnterOwnerScope(cancellationToken);

        if (_store.ReadLease().IsHeld(_utcNow()))
        {
            return null;
        }

        return _store.Enqueue(
            string.Empty,
            0,
            0,
            0,
            roots
                .Select(root => new ChangeFeedRootDelivery(
                    root.RootPath,
                    ChangeFeedBatch.Gap(reason),
                    root.Generation))
                .ToArray()).Count;
    }

    private static ChangeFeedRootGeneration GenerationOf(
        IReadOnlyList<ChangeFeedSubscribedRoot> roots,
        string rootPath) =>
        roots
            .FirstOrDefault(root => string.Equals(
                root.RootPath,
                rootPath,
                StringComparison.OrdinalIgnoreCase))
            ?.Generation ?? ChangeFeedRootGeneration.Unknown;

    private static string VolumeIdOf(IReadOnlyList<UsnChangeFeedState> states) =>
        states.Count == 0
            ? string.Empty
            : states[0].RootIdentity.ToChangeFeedRootIdentity().VolumeId;

    private bool TryBootstrap(
        string rootPath,
        UsnJournalDescriptor descriptor,
        CancellationToken cancellationToken,
        out UsnChangeFeedState state)
    {
        try
        {
            var built = UsnDirectoryMapBuilder.Build(rootPath, _identityProbe, cancellationToken);
            state = new UsnChangeFeedState(
                rootPath,
                built.RootIdentity,
                descriptor.JournalId,
                descriptor.NextUsn,
                built.Directories,
                descriptor.NextUsn);
            return true;
        }
        catch (Exception failure) when (IsRootFailure(failure))
        {
            state = null!;
            return false;
        }
    }

    private string StatePath(string volumeRoot) =>
        Path.Combine(_layout.StateDirectory, VolumeKey(volumeRoot) + ".json");

    private static string VolumeKey(string volumeRoot)
    {
        var trimmed = volumeRoot.TrimEnd(Path.DirectorySeparatorChar);
        var builder = new StringBuilder(trimmed.Length);
        foreach (var character in trimmed)
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_');
        }

        return builder.ToString();
    }

    private static VolumePartition PartitionByVolume(
        IReadOnlyList<ChangeFeedSubscribedRoot> roots)
    {
        var groups = new Dictionary<string, List<ChangeFeedSubscribedRoot>>(
            StringComparer.OrdinalIgnoreCase);
        var unsupported = new List<ChangeFeedSubscribedRoot>();

        foreach (var root in roots)
        {
            string volumeRoot;
            try
            {
                volumeRoot = UsnVolumeJournalReader.ResolveVolumeRoot(root.RootPath);
            }
            catch (Exception failure) when (failure is NotSupportedException or ArgumentException)
            {
                unsupported.Add(root);
                continue;
            }

            if (!groups.TryGetValue(volumeRoot, out var members))
            {
                members = new List<ChangeFeedSubscribedRoot>();
                groups[volumeRoot] = members;
            }

            members.Add(root);
        }

        return new VolumePartition(
            groups.Select(pair => new VolumeGroup(pair.Key, pair.Value)).ToArray(),
            unsupported);
    }

    private static bool IsVolumeFailure(Exception failure) =>
        failure is UnauthorizedAccessException
            or Win32Exception
            or UsnJournalUnavailableException
            or UsnProtocolRejectedException
            or NotSupportedException
            or IOException;

    private static bool IsRootFailure(Exception failure) =>
        failure is DirectoryNotFoundException
            or NotSupportedException
            or UnauthorizedAccessException
            or IOException;

    private sealed record VolumeGroup(
        string VolumeRoot,
        IReadOnlyList<ChangeFeedSubscribedRoot> Roots);

    private sealed record VolumePartition(
        IReadOnlyList<VolumeGroup> Groups,
        IReadOnlyList<ChangeFeedSubscribedRoot> Unsupported);

    private sealed record Admission(
        IReadOnlyList<UsnChangeFeedState> States,
        IReadOnlyList<ChangeFeedRootDelivery> Deliveries);

    internal static string? DescribeGaps(IReadOnlyList<ChangeFeedRootDelivery> deliveries)
    {
        ArgumentNullException.ThrowIfNull(deliveries);

        var groups = deliveries
            .Where(delivery => delivery.Batch.HasGap)
            .GroupBy(delivery => delivery.Batch.GapReason)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .ToArray();

        if (groups.Length == 0)
        {
            return null;
        }

        return "boşluk: " + string.Join(
            ", ",
            groups.Select(group =>
                $"{group.Key}x{group.Count()} ({group.First().RootPath})"));
    }

    internal static long CursorOf(IReadOnlyList<UsnChangeFeedState> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        if (states.Count == 0)
        {
            throw new ArgumentException("En az bir kök durumu gerekiyor.", nameof(states));
        }

        return states.Min(state => state.NextUsn);
    }

    internal static string? DescribeAnchorage(
        long cursor,
        UsnJournalDescriptor descriptor,
        int gappedRoots,
        int reanchoredRoots)
    {
        if (gappedRoots == 0)
        {
            return null;
        }

        return $"imleç={cursor} pencere=[{descriptor.FirstUsn}..{descriptor.NextUsn}] " +
            $"yeniden-çapa={reanchoredRoots}/{gappedRoots}";
    }

    internal static string? Combine(string? primary, string? secondary)
    {
        if (string.IsNullOrWhiteSpace(primary))
        {
            return string.IsNullOrWhiteSpace(secondary) ? null : secondary;
        }

        return string.IsNullOrWhiteSpace(secondary)
            ? primary
            : $"{primary} | {secondary}";
    }

    private sealed record VolumeDrain(
        int Entries,
        int Events,
        int Gapped,
        bool Faulted,
        string? Diagnostics,
        bool LeaseArrived = false)
    {
        public static VolumeDrain LeaseTaken { get; } =
            new(0, 0, 0, false, null, true);
    }
}
