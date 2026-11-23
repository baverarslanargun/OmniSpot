using System.Reflection;
using SmartFileLauncher.Core.Index;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Index;

public sealed class FileIndexNumericOrderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NumericOrdersKeepSignedValuesAndNameOrderTies(int pattern)
    {
        const ulong root = 5;
        var random = new Random(941);
        long[] edges = [long.MinValue, long.MaxValue, -1, 0, 1, 255, 256, 65535, 65536];
        var entries = new List<IndexEntry>
        {
            new(root, root, ".", FileAttributes.Directory, 0, 0),
            new(16, root, "folder-a", FileAttributes.Directory, 0, 0),
            new(17, root, "folder-b", FileAttributes.Directory, 0, 0)
        };
        for (var i = 0; i < 4096; i++)
        {
            var value = pattern == 1 ? -1 : pattern == 2 ? i % 4 :
                i % 3 == 0 ? edges[i / 3 % edges.Length] : random.NextInt64();
            var modified = i % 2 == 0
                ? DateTime.UnixEpoch.Ticks + (long)((ulong)value % 4_000_000_000UL) * TimeSpan.TicksPerSecond + (long)((ulong)value % 9_999_999UL)
                : unchecked(~value);
            entries.Add(new((ulong)i + 18, (ulong)(16 + i % 2), $"name-{i % 37:D2}",
                FileAttributes.Archive, value, modified));
        }
        using var index = FileIndex.Build(@"C:\", root, entries);
        var names = ReadOrder(index, "_byName").Where(row => !entries[(int)row].IsDirectory).ToArray();

        Assert.Equal(names.OrderBy(row => entries[(int)row].SizeBytes), ReadOrder(index, "_bySize"));
        Assert.Equal(names.OrderBy(row => FileIndex.Seconds(entries[(int)row].LastWriteTimeUtc)), ReadOrder(index, "_byModified"));
    }

    private static uint[] ReadOrder(FileIndex index, string field)
    {
        var order = typeof(FileIndex).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(index)!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var rows = (uint[])order.GetType().GetField("Rows", flags)!.GetValue(order)!;
        var count = (int)order.GetType().GetField("Count", flags)!.GetValue(order)!;
        return rows.AsSpan(0, count).ToArray();
    }
}
