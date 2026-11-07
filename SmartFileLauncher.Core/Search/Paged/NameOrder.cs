using System.Buffers.Binary;

namespace SmartFileLauncher.Core.Search.Paged;

internal sealed record NameOrderPlan(string Directory, ulong Root, long NameBytes, int[] Offsets, int[] Ranks, uint[] Order);

internal sealed partial class Catalog
{
    internal NameOrderPlan CreateNameOrder(CancellationToken ct, SmartFileLauncher.Core.Application.Settings.IndexBuildOptions? options = null)
    {
        var names = new int[Data.NameUsed];
        var used = 0;
        for (var i = 0; i < Data.NameHashSize; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            var offset = Data.Read<int>(Image.NameHash, i);
            if (offset != 0) names[used++] = offset;
        }
        IndexSorter.Sort(names, (a, b) => CompareNameAt(a, b), ct, options);
        var ranks = new int[names.Length];
        var rankByOffset = new Dictionary<int, int>(names.Length);
        var rank = 0;
        var prior = 0;
        for (var i = 0; i < names.Length; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            var offset = names[i];
            if (prior == 0 || CompareNameAt(prior, offset) != 0) rank++;
            ranks[i] = rank;
            rankByOffset.Add(offset, rank);
            prior = offset;
        }
        var counts = new int[rank + 1];
        foreach (var id in ActiveIds())
        {
            if ((id & 4095) == 0) ct.ThrowIfCancellationRequested();
            counts[rankByOffset[Row(id).Name & NameMask]]++;
        }
        var total = 0;
        for (var i = 0; i < counts.Length; i++) { var count = counts[i]; counts[i] = total; total += count; }
        var order = new uint[total];
        foreach (var id in ActiveIds())
        {
            if ((id & 4095) == 0) ct.ThrowIfCancellationRequested();
            order[counts[rankByOffset[Row(id).Name & NameMask]]++] = id;
        }
        return new(Data.Directory, Data.RootFrn, Data.Count(Image.Names), names, ranks, order);
    }

    internal void ApplyNameOrder(NameOrderPlan plan, CancellationToken ct)
    {
        if (!Data.Mutable || plan.Directory != Data.Directory || plan.Root != Data.RootFrn ||
            Data.Count(Image.Names) < plan.NameBytes) throw new InvalidOperationException("Sıralama planı katalogla eşleşmiyor.");
        for (var i = 0; i < plan.Offsets.Length; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteInt32LittleEndian(Data.MutableBytes(Image.Names, plan.Offsets[i] - 4, 4), plan.Ranks[i]);
        }
        Data.ZeroRows(Image.NameOrder, plan.Order.Length);
        for (var i = 0; i < plan.Order.Length; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            Data.Write(Image.NameOrder, i, plan.Order[i]);
        }
        Data.SortedCount = plan.Order.Length;
    }
}
