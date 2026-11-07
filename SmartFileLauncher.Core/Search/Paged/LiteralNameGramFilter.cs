namespace SmartFileLauncher.Core.Search.Paged;

internal sealed partial class Catalog
{
    private const int LiteralGramRows = 256;
    private const int LiteralGramWords = 256;
    private const int LiteralGramMask = LiteralGramWords * 64 - 1;
    private static ulong[] BuildLiteralGramFilter(LiteralNameIndex index, CancellationToken cancellationToken)
    {
        var blockCount = checked((index.Rows.Length + LiteralGramRows - 1) / LiteralGramRows);
        var blocks = new ulong[checked(blockCount * LiteralGramWords)];
        for (var position = 0; position < index.Rows.Length; position++)
        {
            if ((position & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            AddLiteralGrams(blocks, position / LiteralGramRows * LiteralGramWords, index, index.Rows[position]);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return blocks;
    }

    private static void AddLiteralGrams(ulong[] blocks, int baseWord, LiteralNameIndex index, LiteralNameRow row)
    {
        if (row.Wide)
        {
            Array.Fill(blocks, ulong.MaxValue, baseWord, LiteralGramWords);
            return;
        }
        var name = index.Name(row);
        for (var offset = 0; offset + 2 < name.Length; offset++)
        {
            var hash = LiteralGramHash(LowerAscii(name[offset]), LowerAscii(name[offset + 1]), LowerAscii(name[offset + 2]));
            SetLiteralGramBit(blocks, baseWord, (int)(hash & LiteralGramMask));
            SetLiteralGramBit(blocks, baseWord, (int)((hash >> 14) & LiteralGramMask));
        }
    }

    private static List<int> CandidateLiteralGramBlocks(byte[] pattern, ulong[] blocks, CancellationToken cancellationToken)
    {
        var masks = new (int Word, ulong Bit)[checked((pattern.Length - 2) * 2)];
        for (var offset = 0; offset + 2 < pattern.Length; offset++)
        {
            if ((offset & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var hash = LiteralGramHash(pattern[offset], pattern[offset + 1], pattern[offset + 2]);
            var first = (int)(hash & LiteralGramMask);
            var second = (int)((hash >> 14) & LiteralGramMask);
            masks[offset * 2] = (first >> 6, 1UL << (first & 63));
            masks[offset * 2 + 1] = (second >> 6, 1UL << (second & 63));
        }
        var candidates = new List<int>();
        for (var block = 0; block < blocks.Length / LiteralGramWords; block++)
        {
            if ((block & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var possible = true;
            foreach (var mask in masks)
                if ((blocks[block * LiteralGramWords + mask.Word] & mask.Bit) == 0)
                {
                    possible = false;
                    break;
                }
            if (possible) candidates.Add(block);
        }
        return candidates;
    }

    private static void SetLiteralGramBit(ulong[] blocks, int baseWord, int bit) =>
        blocks[baseWord + (bit >> 6)] |= 1UL << (bit & 63);

    private static uint LiteralGramHash(byte first, byte second, byte third)
    {
        var hash = (uint)first | (uint)second << 8 | (uint)third << 16;
        hash ^= hash >> 16;
        hash *= 0x7feb352d;
        hash ^= hash >> 15;
        hash *= 0x846ca68b;
        return hash ^ hash >> 16;
    }
}
