using System.IO;

namespace SmartFileLauncher.Core.ChangeFeed.Store;

public sealed class ChangeFeedSubscription
{
    public const int MaximumRoots = 256;

    public ChangeFeedSubscription(
        string ownerSid,
        IReadOnlyList<ChangeFeedSubscribedRoot> roots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSid);
        ArgumentNullException.ThrowIfNull(roots);

        if (roots.Count > MaximumRoots)
        {
            throw new ArgumentException(
                $"Abonelik en çok {MaximumRoots} kök taşıyabilir: {roots.Count}",
                nameof(roots));
        }

        if (roots.Count == 0)
        {
            throw new ArgumentException("Abonelik en az bir kök içermelidir.", nameof(roots));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!seen.Add(root.RootPath))
            {
                throw new ArgumentException(
                    $"Abonelikte yinelenen kök var: {root.RootPath}",
                    nameof(roots));
            }
        }

        OwnerSid = ownerSid;
        Roots = roots.ToArray();
    }

    public string OwnerSid { get; }

    public IReadOnlyList<ChangeFeedSubscribedRoot> Roots { get; }

    public bool Authorizes(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return Roots.Any(root => root.Contains(path));
    }
}

public sealed class ChangeFeedSubscribedRoot
{
    public ChangeFeedSubscribedRoot(
        string rootPath,
        ChangeFeedRootIdentity identity,
        ChangeFeedRootGeneration generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException(
                $"Kök yolu tam nitelikli olmalıdır: {rootPath}",
                nameof(rootPath));
        }

        if (identity.IsUnknown)
        {
            throw new ArgumentException("Kök kimliği boş olamaz.", nameof(identity));
        }

        if (generation.IsUnknown)
        {
            throw new ArgumentException("Kök kuşağı boş olamaz.", nameof(generation));
        }

        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        Identity = identity;
        Generation = generation;
    }

    public string RootPath { get; }

    public ChangeFeedRootIdentity Identity { get; }

    public ChangeFeedRootGeneration Generation { get; }

    public bool Contains(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception failure)
            when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (string.Equals(candidate, RootPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = RootPath + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
