using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.IO;
using SmartFileLauncher.Core.Services;

namespace SmartFileLauncher.Core.Diagnostics;

public sealed class MeasurementRunLayout : IDisposable
{
    public const string DataDirectoryName = "bos-uretim-data";
    public const string ProductionCopyDataDirectoryName = "uretim-kopya-data";

    private const string EmptyProductionLeaseFileName = ".bos-uretim.lock";
    private const string ProductionCopyLeaseFileName = ".uretim-kopya.lock";
    private FileStream? _runLease;

    private MeasurementRunLayout(
        MeasurementProfile profile,
        string runRoot,
        string dataRoot,
        string settingsDirectory,
        string settingsPath,
        string indexDirectory,
        string databasePath,
        string thumbnailCachePath,
        string? corpusPath,
        FileStream runLease)
    {
        Profile = profile;
        RunRoot = runRoot;
        DataRoot = dataRoot;
        SettingsDirectory = settingsDirectory;
        SettingsPath = settingsPath;
        IndexDirectory = indexDirectory;
        DatabasePath = databasePath;
        DatabaseWalPath = databasePath + "-wal";
        DatabaseShmPath = databasePath + "-shm";
        ThumbnailCachePath = thumbnailCachePath;
        CorpusPath = corpusPath;
        LeasePath = runLease.Name ?? Path.Combine(dataRoot, GetLeaseFileName(profile));
        _runLease = runLease;
    }

    public MeasurementProfile Profile { get; }
    public string RunRoot { get; }
    public string DataRoot { get; }
    public string SettingsDirectory { get; }
    public string SettingsPath { get; }
    public string IndexDirectory { get; }
    public string DatabasePath { get; }
    public string DatabaseWalPath { get; }
    public string DatabaseShmPath { get; }
    public string ThumbnailCachePath { get; }
    public string? CorpusPath { get; }
    public string LeasePath { get; }

    public static MeasurementRunLayout Prepare(ApplicationStartupOptions startup)
    {
        ArgumentNullException.ThrowIfNull(startup);
        if (startup.Error != null ||
            startup.Profile is not (MeasurementProfile.EmptyProduction or MeasurementProfile.ProductionCopy) ||
            string.IsNullOrWhiteSpace(startup.Diagnostics.Directory))
        {
            throw new InvalidOperationException(
                "Geçerli bir ölçüm başlangıç seçeneği olmadan koşum düzeni hazırlanamaz.");
        }

        var roamingRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OmniSpot");
        var localRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmniSpot");

        return Prepare(
            startup.Diagnostics.Directory,
            roamingRoot,
            localRoot,
            startup.Profile.Value);
    }

    public static MeasurementRunLayout PrepareProductionCopy(string runRoot)
    {
        var roamingRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OmniSpot");
        var localRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmniSpot");

        return Prepare(
            runRoot,
            roamingRoot,
            localRoot,
            MeasurementProfile.ProductionCopy);
    }

    internal static MeasurementRunLayout Prepare(
        string runRoot,
        string productionRoamingRoot,
        string productionLocalRoot,
        FileSystemPathGuard? pathGuard = null)
    {
        return Prepare(
            runRoot,
            productionRoamingRoot,
            productionLocalRoot,
            MeasurementProfile.EmptyProduction,
            pathGuard);
    }

    internal static MeasurementRunLayout Prepare(
        string runRoot,
        string productionRoamingRoot,
        string productionLocalRoot,
        MeasurementProfile profile,
        FileSystemPathGuard? pathGuard = null)
    {
        pathGuard ??= FileSystemPathGuard.Default;
        ValidateProfile(profile);

        if (string.IsNullOrWhiteSpace(runRoot) || !Path.IsPathFullyQualified(runRoot))
        {
            throw new InvalidOperationException(
                $"{GetProfileName(profile)} koşum dizini mutlak bir yol olmalıdır.");
        }

        RejectNonLocalOrDevicePath(runRoot, profile);

        var requestedRunRoot = pathGuard.Canonicalize(runRoot);
        RejectReparsePoint(pathGuard, requestedRunRoot, profile);
        var canonicalRunRoot = pathGuard.ResolvePhysicalPath(requestedRunRoot);
        var volumeRoot = Path.GetPathRoot(canonicalRunRoot);
        if (!string.IsNullOrEmpty(volumeRoot) &&
            canonicalRunRoot.Equals(
                Path.TrimEndingDirectorySeparator(volumeRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{GetProfileName(profile)} koşum dizini bir sürücü kökü olamaz.");
        }

        RejectProductionOverlap(pathGuard, canonicalRunRoot, productionRoamingRoot, profile);
        RejectProductionOverlap(pathGuard, canonicalRunRoot, productionLocalRoot, profile);

        var dataRoot = Path.Combine(canonicalRunRoot, GetDataDirectoryName(profile));
        var settingsDirectory = Path.Combine(dataRoot, "settings");
        var indexDirectory = Path.Combine(dataRoot, "index");
        var thumbnailCachePath = Path.Combine(dataRoot, "thumbcache");
        var corpusPath = profile == MeasurementProfile.EmptyProduction
            ? Path.Combine(dataRoot, "corpus")
            : null;
        var settingsPath = Path.Combine(settingsDirectory, "settings.json");
        var databasePath = Path.Combine(indexDirectory, "index.db");
        var leasePath = Path.Combine(dataRoot, GetLeaseFileName(profile));

        EnsureDescendant(dataRoot, settingsDirectory, indexDirectory, profile);
        EnsureDescendant(dataRoot, thumbnailCachePath, settingsPath, profile);
        EnsureDescendant(dataRoot, databasePath, databasePath + "-wal", profile);
        EnsureDescendant(dataRoot, databasePath + "-shm", leasePath, profile);
        if (corpusPath != null)
        {
            EnsureDescendant(dataRoot, corpusPath, corpusPath, profile);
        }

        if (profile == MeasurementProfile.ProductionCopy)
        {
            ValidatePreseededProductionCopy(
                pathGuard,
                canonicalRunRoot,
                dataRoot,
                settingsDirectory,
                settingsPath,
                indexDirectory,
                databasePath,
                thumbnailCachePath,
                leasePath);
        }
        else
        {
            CreateSafeDirectory(pathGuard, canonicalRunRoot, profile);
            if (Directory.Exists(dataRoot))
            {
                RejectReparsePoint(pathGuard, dataRoot, profile);
                if (Directory.EnumerateFileSystemEntries(dataRoot).Any())
                {
                    throw new InvalidOperationException(
                        $"{GetDataDirectoryName(profile)} boş değil. Yeni bir koşum dizini kullanın; mevcut veri silinmedi.");
                }
            }

            CreateSafeDirectory(pathGuard, dataRoot, profile);
            CreateSafeDirectory(pathGuard, settingsDirectory, profile);
            CreateSafeDirectory(pathGuard, indexDirectory, profile);
            CreateSafeDirectory(pathGuard, thumbnailCachePath, profile);
            CreateSafeDirectory(pathGuard, corpusPath!, profile);
        }

        FileStream? runLease = null;
        try
        {
            runLease = AcquireRunLease(leasePath, profile);
            var layout = new MeasurementRunLayout(
                profile,
                canonicalRunRoot,
                dataRoot,
                settingsDirectory,
                settingsPath,
                indexDirectory,
                databasePath,
                thumbnailCachePath,
                corpusPath,
                runLease);

            if (profile == MeasurementProfile.ProductionCopy)
            {
                ValidatePreseededFiles(layout, pathGuard);
            }

            return layout;
        }
        catch
        {
            runLease?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _runLease, null)?.Dispose();
    }

    private static void ValidatePreseededProductionCopy(
        FileSystemPathGuard pathGuard,
        string runRoot,
        string dataRoot,
        string settingsDirectory,
        string settingsPath,
        string indexDirectory,
        string databasePath,
        string thumbnailCachePath,
        string leasePath)
    {
        if (!Directory.Exists(runRoot))
        {
            throw new InvalidOperationException(
                "uretim-kopya koşum kökü orkestratör pre-seed'i olmadan bulunamadı.");
        }

        RejectReparsePoint(pathGuard, runRoot, MeasurementProfile.ProductionCopy);
        RequireOnlyEntries(
            runRoot,
            new[] { dataRoot },
            MeasurementProfile.ProductionCopy);
        RequireDirectory(pathGuard, dataRoot, MeasurementProfile.ProductionCopy);
        RequireOnlyEntries(
            dataRoot,
            new[] { settingsDirectory, indexDirectory, thumbnailCachePath },
            MeasurementProfile.ProductionCopy);
        RequireDirectory(pathGuard, settingsDirectory, MeasurementProfile.ProductionCopy);
        RequireDirectory(pathGuard, indexDirectory, MeasurementProfile.ProductionCopy);
        RequireDirectory(pathGuard, thumbnailCachePath, MeasurementProfile.ProductionCopy);

        RequireOnlyEntries(
            settingsDirectory,
            File.Exists(settingsPath) ? new[] { settingsPath } : Array.Empty<string>(),
            MeasurementProfile.ProductionCopy);
        RequireOnlyEntries(
            indexDirectory,
            new[] { databasePath },
            MeasurementProfile.ProductionCopy);

        if (Directory.EnumerateFileSystemEntries(thumbnailCachePath).Any())
        {
            throw new InvalidOperationException(
                "uretim-kopya thumbnail cache başlangıçta boş olmalıdır.");
        }

        if (File.Exists(leasePath))
        {
            throw new InvalidOperationException(
                "uretim-kopya sahiplik kilidi önceden mevcut olamaz.");
        }

        if (!File.Exists(databasePath))
        {
            throw new InvalidOperationException(
                "uretim-kopya pre-seed içinde index.db zorunludur.");
        }
    }

    private static void ValidatePreseededFiles(
        MeasurementRunLayout layout,
        FileSystemPathGuard pathGuard)
    {
        ValidateSeedFile(layout.SettingsPath, pathGuard, required: false);
        ValidateSeedFile(layout.DatabasePath, pathGuard, required: true);

        if (File.Exists(layout.SettingsPath))
        {
            _ = new JsonSettingsStore(layout.SettingsPath).LoadStrict();
        }

        IndexDatabase.ValidateSeed(layout.DatabasePath);
    }

    private static void ValidateSeedFile(
        string path,
        FileSystemPathGuard pathGuard,
        bool required)
    {
        if (!File.Exists(path))
        {
            if (required)
            {
                throw new InvalidDataException(
                    "uretim-kopya pre-seed içinde index.db bulunamadı.");
            }

            return;
        }

        RejectReparsePoint(pathGuard, path, MeasurementProfile.ProductionCopy);
        if (pathGuard.HasMultipleLinks(path))
        {
            throw new InvalidOperationException(
                "uretim-kopya pre-seed dosyaları hardlink veya multi-link olamaz.");
        }
    }

    private static void RequireDirectory(
        FileSystemPathGuard pathGuard,
        string path,
        MeasurementProfile profile)
    {
        if (!Directory.Exists(path))
        {
            throw new InvalidOperationException(
                $"{GetProfileName(profile)} beklenen managed dizini bulamadı.");
        }

        RejectReparsePoint(pathGuard, path, profile);
    }

    private static void RequireOnlyEntries(
        string directory,
        IReadOnlyList<string> expected,
        MeasurementProfile profile)
    {
        var actual = Directory.EnumerateFileSystemEntries(directory)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedSet = expected
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(expectedSet))
        {
            throw new InvalidOperationException(
                $"{GetProfileName(profile)} managed veri yerleşiminde beklenmeyen dosya veya dizin var.");
        }
    }

    private static FileStream AcquireRunLease(
        string leasePath,
        MeasurementProfile profile)
    {
        try
        {
            return new FileStream(
                leasePath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose | FileOptions.WriteThrough);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"{GetProfileName(profile)} koşum dizini başka bir süreç tarafından kullanılıyor.",
                ex);
        }
    }

    private static void RejectProductionOverlap(
        FileSystemPathGuard pathGuard,
        string runRoot,
        string productionRoot,
        MeasurementProfile profile)
    {
        if (string.IsNullOrWhiteSpace(productionRoot))
        {
            throw new InvalidOperationException(
                $"{GetProfileName(profile)} production veri yolu bulunamadı.");
        }

        RejectNonLocalOrDevicePath(productionRoot, profile);
        var canonicalProductionRoot = pathGuard.Canonicalize(productionRoot);
        RejectReparsePoint(pathGuard, canonicalProductionRoot, profile);
        canonicalProductionRoot = pathGuard.ResolvePhysicalPath(canonicalProductionRoot);
        if (IsSameOrDescendant(runRoot, canonicalProductionRoot) ||
            IsSameOrDescendant(canonicalProductionRoot, runRoot))
        {
            throw new InvalidOperationException(
                $"{GetProfileName(profile)} koşum dizini production OmniSpot veri yolu ile çakışamaz.");
        }
    }

    private static void CreateSafeDirectory(
        FileSystemPathGuard pathGuard,
        string path,
        MeasurementProfile profile)
    {
        RejectReparsePoint(pathGuard, path, profile);
        Directory.CreateDirectory(path);
        RejectReparsePoint(pathGuard, path, profile);

        var resolvedPath = pathGuard.ResolvePhysicalPath(path);
        if (!resolvedPath.Equals(
                pathGuard.Canonicalize(path),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{GetProfileName(profile)} veri yolu oluşturulurken başka bir konuma yönlendirildi.");
        }
    }

    private static void RejectReparsePoint(
        FileSystemPathGuard pathGuard,
        string path,
        MeasurementProfile profile)
    {
        if (pathGuard.FindReparsePointInExistingPath(path) != null)
        {
            throw new InvalidOperationException(
                $"{GetProfileName(profile)} koşum yolu yeniden yönlendirilmiş bir klasörden geçemez.");
        }
    }

    private static void RejectNonLocalOrDevicePath(
        string path,
        MeasurementProfile profile)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{GetProfileName(profile)} koşum dizini device namespace kullanamaz.");
        }

        var canonicalPath = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(canonicalPath);
        if (string.IsNullOrEmpty(root) ||
            root.Length < 2 ||
            root[1] != ':' ||
            !char.IsAsciiLetter(root[0]) ||
            canonicalPath.AsSpan(2).Contains(':'))
        {
            throw new InvalidOperationException(
                $"{GetProfileName(profile)} koşum dizini yerel bir Windows sürücüsünde olmalıdır.");
        }
    }

    private static void EnsureDescendant(
        string root,
        string first,
        string second,
        MeasurementProfile profile)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        foreach (var candidate in new[] { first, second })
        {
            var canonicalCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            if (!IsSameOrDescendant(canonicalCandidate, canonicalRoot))
            {
                throw new InvalidOperationException(
                    $"{GetProfileName(profile)} veri yolu koşum dizininin dışına çıkamaz.");
            }
        }
    }

    private static void ValidateProfile(MeasurementProfile profile)
    {
        if (profile is not (MeasurementProfile.EmptyProduction or MeasurementProfile.ProductionCopy))
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }
    }

    private static string GetProfileName(MeasurementProfile profile) => profile switch
    {
        MeasurementProfile.EmptyProduction => ApplicationStartupOptions.EmptyProductionProfileName,
        MeasurementProfile.ProductionCopy => ApplicationStartupOptions.ProductionCopyProfileName,
        _ => "ölçüm"
    };

    private static string GetDataDirectoryName(MeasurementProfile profile) => profile switch
    {
        MeasurementProfile.EmptyProduction => DataDirectoryName,
        MeasurementProfile.ProductionCopy => ProductionCopyDataDirectoryName,
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };

    private static string GetLeaseFileName(MeasurementProfile profile) => profile switch
    {
        MeasurementProfile.EmptyProduction => EmptyProductionLeaseFileName,
        MeasurementProfile.ProductionCopy => ProductionCopyLeaseFileName,
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };

    private static bool IsSameOrDescendant(string candidate, string root)
    {
        if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;

        var rootedPrefix = root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
