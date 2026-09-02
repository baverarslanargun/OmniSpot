using System.ComponentModel;
using System.IO;
using System.Text;
using SmartFileLauncher.Core.ChangeFeed.Store;

namespace SmartFileLauncher.Core.ChangeFeed.Usn;

public enum UsnDrainOutcome
{
    Completed,
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

    public UsnDrainRunner(
        ChangeFeedStoreLayout layout,
        IChangeFeedStore store,
        IUsnJournalReaderFactory readerFactory,
        IUsnIdentityProbe identityProbe,
        IUsnSubtreeReader? subtreeReader = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _readerFactory = readerFactory ?? throw new ArgumentNullException(nameof(readerFactory));
        _identityProbe = identityProbe ?? throw new ArgumentNullException(nameof(identityProbe));
        _subtreeReader = subtreeReader;
    }

    public UsnDrainResult Run(CancellationToken cancellationToken = default)
    {
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
        string? diagnostics = null;

        if (partition.Unsupported.Count > 0)
        {
            AnnounceGap(partition.Unsupported, ChangeFeedGapReason.RootUnavailable);
            entries++;
            gapped += partition.Unsupported.Count;
            diagnostics = $"Desteklenmeyen kök: {partition.Unsupported[0].RootPath}";
        }

        foreach (var group in partition.Groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            VolumeDrain result;
            try
            {
                result = DrainVolume(group.VolumeRoot, group.Roots, cancellationToken);
            }
            catch (Exception failure) when (IsVolumeFailure(failure))
            {
                AnnounceGap(group.Roots, ChangeFeedGapReason.JournalUnavailable);
                faulted++;
                entries++;
                gapped += group.Roots.Count;
                diagnostics ??= $"{group.VolumeRoot}: {failure.Message}";
                continue;
            }

            drained++;
            entries += result.Entries;
            events += result.Events;
            gapped += result.Gapped;
            diagnostics ??= result.Diagnostics;

            if (result.Faulted)
            {
                faulted++;
            }
        }

        var outcome = drained == 0 && faulted > 0
            ? UsnDrainOutcome.Faulted
            : UsnDrainOutcome.Completed;

        return new UsnDrainResult(outcome, drained, faulted, entries, events, gapped, diagnostics);
    }

    private VolumeDrain DrainVolume(
        string volumeRoot,
        IReadOnlyList<ChangeFeedSubscribedRoot> roots,
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

        if (state is not null && state.JournalId == descriptor.JournalId)
        {
            _store.DiscardUncommitted(
                VolumeIdOf(state.Roots),
                state.JournalId,
                state.NextUsn);
        }
        else
        {
            state = null;
        }

        var admission = Admit(state, roots, descriptor, cancellationToken);
        if (admission.States.Count == 0)
        {
            return Announce(admission, descriptor);
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

            deliveries.Add(new ChangeFeedRootDelivery(root.Root.RootPath, root.Batch));
        }

        var entries = 0;
        if (deliveries.Count > 0)
        {
            _store.Enqueue(
                VolumeIdOf(admission.States),
                journalId,
                cursor,
                batch.NextUsn,
                deliveries);
            entries = 1;
        }

        feed.Accept();

        var resynchronized = Resynchronize(feed, batch, descriptor, cancellationToken);
        if (resynchronized.Count == 0)
        {
            stateStore.Delete();
        }
        else
        {
            stateStore.Write(journalId, feed.AcceptedUsn, resynchronized);
        }

        return new VolumeDrain(
            entries,
            deliveries.Sum(delivery => delivery.Batch.Events.Count),
            deliveries.Count(delivery => delivery.Batch.HasGap),
            batch.Roots.Any(root => root.Batch.IsFaulted),
            batch.Roots.FirstOrDefault(root => root.Batch.IsFaulted)?.Batch.Diagnostics);
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
                    ChangeFeedBatch.Gap(ChangeFeedGapReason.RootUnavailable)));
                continue;
            }

            if (fresh.ToChangeFeedRootIdentity() != root.Identity)
            {
                deliveries.Add(new ChangeFeedRootDelivery(
                    root.RootPath,
                    ChangeFeedBatch.Gap(ChangeFeedGapReason.RootIdentityChanged)));
                continue;
            }

            states.Add(fresh);
            deliveries.Add(new ChangeFeedRootDelivery(
                root.RootPath,
                ChangeFeedBatch.Gap(ChangeFeedGapReason.NotYetSynchronized)));
        }

        return new Admission(states, deliveries);
    }

    private IReadOnlyList<UsnChangeFeedState> Resynchronize(
        UsnVolumeChangeFeed feed,
        UsnVolumeBatch batch,
        UsnJournalDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var gapped = batch.Roots
            .Where(root => root.Batch.HasGap)
            .Select(root => root.Root.RootPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var states = new List<UsnChangeFeedState>(feed.Roots.Count);
        foreach (var projection in feed.Roots)
        {
            if (!gapped.Contains(projection.RootPath))
            {
                states.Add(projection.CaptureState(feed.JournalId, feed.AcceptedUsn));
                continue;
            }

            if (TryBootstrap(projection.RootPath, descriptor, cancellationToken, out var rebuilt) &&
                rebuilt.ToChangeFeedRootIdentity() == projection.RootIdentity)
            {
                states.Add(rebuilt);
            }
        }

        return states;
    }

    private VolumeDrain Announce(Admission admission, UsnJournalDescriptor descriptor)
    {
        if (admission.Deliveries.Count == 0)
        {
            return new VolumeDrain(0, 0, 0, false, null);
        }

        _store.Enqueue(string.Empty, descriptor.JournalId, 0, 0, admission.Deliveries);
        return new VolumeDrain(1, 0, admission.Deliveries.Count, false, null);
    }

    private void AnnounceGap(
        IReadOnlyList<ChangeFeedSubscribedRoot> roots,
        ChangeFeedGapReason reason) =>
        _store.Enqueue(
            string.Empty,
            0,
            0,
            0,
            roots
                .Select(root => new ChangeFeedRootDelivery(
                    root.RootPath,
                    ChangeFeedBatch.Gap(reason)))
                .ToArray());

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

    private sealed record VolumeDrain(
        int Entries,
        int Events,
        int Gapped,
        bool Faulted,
        string? Diagnostics);
}
