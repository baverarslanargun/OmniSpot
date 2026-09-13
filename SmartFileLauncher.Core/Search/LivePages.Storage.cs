using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SmartFileLauncher.Core.Search;

internal sealed unsafe partial class LivePages
{
    private sealed partial class Page
    {
        private const ulong PatchMagic = 0x314843544150534FUL;
        private const int PatchBlockSize = 64;
        private readonly bool _owned;
        private readonly ulong[]? _patchBlocks;
        private bool _persisted;
        internal string? BaseFile { get; private set; }
        internal string? BaseSha256 { get; private set; }

        internal Page(string path, Page source)
        {
            Path = path; _owned = true;
            BaseFile = source.BaseFile ?? System.IO.Path.GetFileName(source.Path);
            BaseSha256 = source.BaseSha256 ?? source.Hash();
            _patchBlocks = source.BaseFile is null ? new ulong[PageSize / PatchBlockSize / 64] : (ulong[])source._patchBlocks!.Clone();
            Pointer = (byte*)NativeMemory.Alloc(PageSize);
            if (Pointer == null) throw new OutOfMemoryException();
            new ReadOnlySpan<byte>(source.Pointer, PageSize).CopyTo(new Span<byte>(Pointer, PageSize));
            GC.AddMemoryPressure(PageSize); _memoryPressure = true;
        }

        internal Page(string directory, PageReference reference)
        {
            Path = System.IO.Path.Combine(directory, reference.File); _owned = true;
            _patchBlocks = new ulong[PageSize / PatchBlockSize / 64];
            BaseFile = reference.BaseFile; BaseSha256 = reference.BaseSha256;
            if (BaseFile is null || System.IO.Path.GetFileName(BaseFile) != BaseFile || !BaseFile.EndsWith(".page", StringComparison.Ordinal) || BaseSha256 is not { Length: 64 })
                throw new InvalidDataException("Live temel sayfa başvurusu geçersiz.");
            Pointer = (byte*)NativeMemory.Alloc(PageSize);
            if (Pointer == null) throw new OutOfMemoryException();
            try
            {
                var target = new Span<byte>(Pointer, PageSize);
                ReadBaseline(directory, target);
                using var file = File.OpenRead(Path);
                if (file.Length < 12 || file.Length >= PageSize / 2) throw new InvalidDataException("Live sayfa farkı boyutu geçersiz.");
                using var reader = new BinaryReader(file);
                if (reader.ReadUInt64() != PatchMagic) throw new InvalidDataException("Live sayfa farkı biçimi geçersiz.");
                var count = reader.ReadInt32(); var end = 0;
                if (count < 0 || count > PageSize / PatchBlockSize) throw new InvalidDataException("Live sayfa farkı sayısı geçersiz.");
                for (var index = 0; index < count; index++)
                {
                    var offset = reader.ReadInt32(); var length = reader.ReadInt32();
                    if (offset < end || length <= 0 || (long)offset + length > PageSize || offset % PatchBlockSize != 0 || length % PatchBlockSize != 0)
                        throw new InvalidDataException("Live sayfa farkı aralığı geçersiz.");
                    file.ReadExactly(target.Slice(offset, length)); MarkChanged(offset, length); end = offset + length;
                }
                if (file.Position != file.Length) throw new InvalidDataException("Live sayfa farkı sonu geçersiz.");
                _persisted = true; GC.AddMemoryPressure(PageSize); _memoryPressure = true;
            }
            catch { NativeMemory.Free(Pointer); Pointer = null; throw; }
        }

        private void ReadBaseline(string directory, Span<byte> target)
        {
            using var input = new FileStream(System.IO.Path.Combine(directory, BaseFile!), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length != PageSize) throw new InvalidDataException("Live temel sayfa boyutu geçersiz.");
            input.ReadExactly(target);
            if (Convert.ToHexString(SHA256.HashData(target)) != BaseSha256) throw new InvalidDataException("Live temel sayfa checksum uyuşmuyor.");
        }

        private void PersistPatch()
        {
            if (_persisted) return;
            var current = new ReadOnlySpan<byte>(Pointer, PageSize);
            var ranges = new List<(int Offset, int Length)>(); var bytes = 12;
            for (var block = 0; block < PageSize / PatchBlockSize;)
            {
                if (!Changed(block)) { block++; continue; }
                var start = block++;
                while (block < PageSize / PatchBlockSize && Changed(block)) block++;
                var length = (block - start) * PatchBlockSize;
                ranges.Add((start * PatchBlockSize, length)); bytes += 8 + length;
            }
            using var output = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);
            if (bytes < PageSize / 2)
            {
                using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true);
                writer.Write(PatchMagic); writer.Write(ranges.Count);
                foreach (var range in ranges) { writer.Write(range.Offset); writer.Write(range.Length); writer.Write(current.Slice(range.Offset, range.Length)); }
                writer.Flush();
            }
            else { output.Write(current); BaseFile = null; BaseSha256 = null; }
            output.Flush(true); _persisted = true;
        }
        internal void MarkChanged(int offset, int length)
        {
            if (_patchBlocks is null || length == 0) return;
            var end = (offset + length - 1) / PatchBlockSize;
            for (var block = offset / PatchBlockSize; block <= end; block++) _patchBlocks[block / 64] |= 1UL << (block % 64);
        }
        private bool Changed(int block) => (_patchBlocks![block / 64] & (1UL << (block % 64))) != 0;
    }
}
