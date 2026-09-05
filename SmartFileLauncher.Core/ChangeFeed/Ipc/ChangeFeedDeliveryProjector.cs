using SmartFileLauncher.Core.ChangeFeed.Store;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

public sealed class ChangeFeedDeliveryProjector
{
    private readonly Func<string, ChangeFeedPathAuthorizer> _authorizerFactory;
    private readonly IChangeFeedPageMeasure _measure;
    private readonly long _pageBudget;

    public ChangeFeedDeliveryProjector(
        Func<string, ChangeFeedPathAuthorizer> authorizerFactory,
        IChangeFeedPageMeasure measure,
        long pageBudget)
    {
        ArgumentNullException.ThrowIfNull(authorizerFactory);
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageBudget);

        if (measure.Envelope >= pageBudget)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageBudget),
                "Sayfa bütçesi zarf maliyetinden büyük olmalıdır.");
        }

        _authorizerFactory = authorizerFactory;
        _measure = measure;
        _pageBudget = pageBudget;
    }

    public ChangeFeedDeliveryPage Project(
        ChangeFeedSubscription? subscription,
        ChangeFeedQueueSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        var builder = new PageBuilder(_measure, _pageBudget);
        var completed = 0L;
        var truncated = false;

        foreach (var entry in slice.Entries)
        {
            if (truncated)
            {
                break;
            }

            var entryComplete = true;

            foreach (var delivery in ChangeFeedGenerationFilter.Current(subscription, entry))
            {
                if (Project(delivery, builder))
                {
                    continue;
                }

                entryComplete = false;
                truncated = true;
                break;
            }

            if (entryComplete)
            {
                completed = entry.Sequence;
            }
        }

        var hasMore = truncated || slice.HasMore || completed < LastSequence(slice);
        return new ChangeFeedDeliveryPage(builder.Build(), completed, hasMore);
    }

    private bool Project(ChangeFeedRootDelivery delivery, PageBuilder builder)
    {
        var producerGap = delivery.Batch.HasGap
            ? delivery.Batch.GapReason
            : ChangeFeedGapReason.None;

        var producerFault = delivery.Batch.IsFaulted
            ? delivery.Batch.FaultReason
            : ChangeFeedFaultReason.None;

        var projection = _authorizerFactory(delivery.RootPath).Project(delivery.Batch.Events);

        if (projection.Events.Count == 0 &&
            !projection.Withheld &&
            producerGap == ChangeFeedGapReason.None &&
            producerFault == ChangeFeedFaultReason.None)
        {
            return true;
        }

        if (!builder.TryOpen(delivery.RootPath, out var root))
        {
            return false;
        }

        root.Note(producerGap, producerFault, projection.Withheld);

        foreach (var change in projection.Events)
        {
            var cost = _measure.Event(change);

            if (cost > root.Capacity)
            {
                root.NotePayloadTooLarge();
                continue;
            }

            if (!builder.TryAdd(root, change, cost))
            {
                return false;
            }
        }

        return true;
    }

    private static long LastSequence(ChangeFeedQueueSlice slice) =>
        slice.Entries.Count == 0 ? 0 : slice.Entries[^1].Sequence;

    private sealed class PageBuilder
    {
        private readonly IChangeFeedPageMeasure _measure;
        private readonly long _pageBudget;

        private readonly Dictionary<string, RootBuilder> _byPath =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly List<RootBuilder> _order = new();

        private long _remaining;

        public PageBuilder(IChangeFeedPageMeasure measure, long pageBudget)
        {
            _measure = measure;
            _pageBudget = pageBudget;
            _remaining = pageBudget - measure.Envelope;
        }

        public bool TryOpen(string rootPath, out RootBuilder root)
        {
            if (_byPath.TryGetValue(rootPath, out var existing))
            {
                root = existing;
                return true;
            }

            var overhead = _measure.Root(rootPath);
            if (overhead > _remaining)
            {
                root = RootBuilder.None;
                return false;
            }

            _remaining -= overhead;

            root = new RootBuilder(
                rootPath,
                _pageBudget - _measure.Envelope - overhead);

            _byPath.Add(rootPath, root);
            _order.Add(root);
            return true;
        }

        public bool TryAdd(RootBuilder root, ChangeFeedEvent change, long cost)
        {
            if (cost > _remaining)
            {
                return false;
            }

            _remaining -= cost;
            root.Add(change);
            return true;
        }

        public IReadOnlyList<ChangeFeedRootPage> Build() =>
            _order.Where(root => root.HasContent).Select(root => root.Build()).ToArray();
    }

    private sealed class RootBuilder
    {
        public static readonly RootBuilder None = new("?", 0);

        private readonly List<ChangeFeedEvent> _events = new();

        private ChangeFeedGapReason _gap = ChangeFeedGapReason.None;
        private ChangeFeedFaultReason _fault = ChangeFeedFaultReason.None;
        private bool _withheld;
        private bool _oversized;

        public RootBuilder(string rootPath, long capacity)
        {
            RootPath = rootPath;
            Capacity = capacity;
        }

        public string RootPath { get; }

        public long Capacity { get; }

        public void Note(
            ChangeFeedGapReason gap,
            ChangeFeedFaultReason fault,
            bool withheld)
        {
            if (_gap == ChangeFeedGapReason.None)
            {
                _gap = gap;
            }

            if (_fault == ChangeFeedFaultReason.None)
            {
                _fault = fault;
            }

            _withheld |= withheld;
        }

        public bool HasContent =>
            _events.Count > 0 ||
            _gap != ChangeFeedGapReason.None ||
            _fault != ChangeFeedFaultReason.None ||
            _withheld ||
            _oversized;

        public void NotePayloadTooLarge() => _oversized = true;

        public void Add(ChangeFeedEvent change) => _events.Add(change);

        public ChangeFeedRootPage Build() =>
            new(RootPath, _events, _gap, _fault, _withheld, _oversized);
    }
}
