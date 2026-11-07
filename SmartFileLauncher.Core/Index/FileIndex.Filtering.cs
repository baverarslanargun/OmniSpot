using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Index;

public sealed partial class FileIndex
{
    private sealed class CatalogNameFilter : IDisposable
    {
        private const byte ExtensionMatch = 1;
        private const byte SegmentMatch = 2;
        private readonly string[]? _extensions;
        private readonly string[]? _segments;
        private readonly bool _includeDirectories;
        private readonly bool _rootMatches;
        private IndexNames? _source;
        private byte[] _flags = [];
        private int _prepared;

        internal CatalogNameFilter(QueryCatalogFilter filter, string rootPath)
        {
            _extensions = filter.AllowedExtensions is { Count: > 0 } ? filter.AllowedExtensions.ToArray() : null;
            _segments = filter.PathSegments?.ToArray();
            _includeDirectories = filter.IncludeDirectoriesWithoutExtension;
            if (_segments is not null)
            {
                var rootParts = rootPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                for (var i = 0; i + 1 < rootParts.Length; i++)
                    if (Contains(_segments, rootParts[i])) { _rootMatches = true; break; }
            }
        }

        internal void Prepare(IndexNames source, CancellationToken cancellationToken)
        {
            if (_extensions is null && (_segments is null || _rootMatches)) return;
            if (!ReferenceEquals(source, _source))
            {
                _source = source;
                _prepared = 0;
            }
            if (_flags.Length < source.Count)
            {
                var flags = ArrayPool<byte>.Shared.Rent(source.Count);
                _flags.AsSpan(0, _prepared).CopyTo(flags);
                if (_flags.Length > 0) ArrayPool<byte>.Shared.Return(_flags);
                _flags = flags;
            }
            Span<char> buffer = stackalloc char[256];
            char[]? rented = null;
            try
            {
                for (var id = _prepared; id < source.Count; id++)
                {
                    if ((id & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var original = source.Original(id);
                    if (source.IsWide(id))
                    {
                        _flags[id] = Classify(MemoryMarshal.Cast<byte, char>(original));
                        continue;
                    }
                    var decoded = buffer;
                    if (original.Length > buffer.Length)
                    {
                        if (rented is null || rented.Length < original.Length)
                        {
                            var replacement = ArrayPool<char>.Shared.Rent(original.Length);
                            if (rented is not null) ArrayPool<char>.Shared.Return(rented);
                            rented = replacement;
                        }
                        decoded = rented;
                    }
                    var written = Encoding.UTF8.GetChars(original, decoded);
                    _flags[id] = Classify(decoded[..written]);
                }
                _prepared = source.Count;
            }
            finally
            {
                if (rented is not null) ArrayPool<char>.Shared.Return(rented);
            }
        }

        private byte Classify(ReadOnlySpan<char> name)
        {
            var flags = (byte)0;
            if (_extensions is not null && Contains(_extensions, Path.GetExtension(name))) flags |= ExtensionMatch;
            if (_segments is not null && !_rootMatches && Contains(_segments, name)) flags |= SegmentMatch;
            return flags;
        }

        internal bool Matches(FileIndex index, uint row, bool directory)
        {
            if (_extensions is not null && !(directory && _includeDirectories) &&
                (_flags[index._name[row]] & ExtensionMatch) == 0) return false;
            if (_segments is null || _rootMatches) return true;
            for (var at = row; at != NoRow && at != index._root; at = index._parent[at])
                if ((_flags[index._name[at]] & SegmentMatch) != 0) return true;
            return false;
        }

        private static bool Contains(string[] values, ReadOnlySpan<char> candidate)
        {
            foreach (var value in values)
                if (candidate.Equals(value.AsSpan(), StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public void Dispose()
        {
            if (_flags.Length == 0) return;
            ArrayPool<byte>.Shared.Return(_flags);
            _flags = [];
        }
    }
}
