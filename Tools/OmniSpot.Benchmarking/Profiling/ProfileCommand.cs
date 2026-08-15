using System.CommandLine;
using System.Text;
using SmartFileLauncher.Core.Search;

namespace OmniSpot.Benchmarking.Profiling;

internal static class ProfileCommand
{
    internal static Command CreateCommand()
    {
        var profileCommand = new Command(
            "profile",
            "Dosya içeriğini açmadan anonim B-1 profilini üretir.");
        var omnispotRootsOption = new Option<bool>("--omnispot-roots")
        {
            Description = "OmniSpot production kök seçimini kullanır."
        };
        var customRootsOption = new Option<string[]>("--root")
        {
            Description = "Taranacak custom kök. Birden çok kez verilebilir.",
            AllowMultipleArgumentsPerToken = true
        };
        var outputOption = new Option<string?>("--output")
        {
            Description = "JSON çıktı yolu. Varsayılan notes-local altındadır."
        };
        var yesOption = new Option<bool>("--yes")
        {
            Description = "Kök onayını etkileşimsiz kabul eder."
        };
        var printOption = new Option<bool>("--print")
        {
            Description = "Özeti basar; tek başına kullanıldığında dosya yazmaz."
        };
        var showPathsOption = new Option<bool>("--show-paths")
        {
            Description = "Kök ve çıktı yollarını terminalde açık gösterir."
        };

        profileCommand.Options.Add(omnispotRootsOption);
        profileCommand.Options.Add(customRootsOption);
        profileCommand.Options.Add(outputOption);
        profileCommand.Options.Add(yesOption);
        profileCommand.Options.Add(printOption);
        profileCommand.Options.Add(showPathsOption);

        profileCommand.SetAction(async (parseResult, cancellationToken) =>
            await RunAsync(
                parseResult.GetValue(omnispotRootsOption),
                parseResult.GetValue(customRootsOption) ?? Array.Empty<string>(),
                parseResult.GetValue(outputOption),
                parseResult.GetValue(yesOption),
                parseResult.GetValue(printOption),
                parseResult.GetValue(showPathsOption),
                cancellationToken));
        return profileCommand;
    }

    private static async Task<int> RunAsync(
        bool includeOmniSpotRoots,
        IReadOnlyList<string> customRoots,
        string? outputPath,
        bool assumeYes,
        bool print,
        bool showPaths,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProfileRootRequest> roots;
        try
        {
            roots = ProfileRootResolver.Resolve(includeOmniSpotRoots, customRoots);
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("En az bir profil kökü geçerli değil.");
            return 2;
        }

        if (roots.Count == 0)
        {
            Console.Error.WriteLine("--omnispot-roots veya en az bir --root gerekli.");
            return 2;
        }

        Console.Out.Write(FormatRootPreview(roots, showPaths));
        if (!assumeYes && !Confirm())
        {
            Console.Error.WriteLine("Profil taraması başlatılmadı.");
            return 3;
        }

        ProfileDocument profile;
        try
        {
            var scanner = new FileSystemProfileScanner(new BasicTokenizer());
            profile = scanner.Scan(roots, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Profil taraması iptal edildi.");
            return 130;
        }

        if (print)
        {
            Console.Out.Write(ProfileSummaryFormatter.Format(profile));
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return 0;
            }
        }

        var resolvedOutput = string.IsNullOrWhiteSpace(outputPath)
            ? DefaultOutputPath()
            : Path.GetFullPath(outputPath);
        try
        {
            var directory = Path.GetDirectoryName(resolvedOutput);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(
                resolvedOutput,
                ProfileJson.Serialize(profile) + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine("Profil JSON çıktısı yazılamadı.");
            return 4;
        }

        Console.Out.WriteLine(
            "Profil JSON çıktısı yazıldı: " + FormatPathForDisplay(resolvedOutput, showPaths));
        return 0;
    }

    internal static string FormatRootPreview(
        IReadOnlyList<ProfileRootRequest> roots,
        bool showPaths)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Taranacak kökler:");
        foreach (var root in roots)
        {
            var label = root.Kind == ProfileRootKind.Custom
                ? "custom-" + root.Ordinal
                : root.Kind.ToString().ToLowerInvariant();
            builder.AppendLine(
                "  " + label + ": " + FormatPathForDisplay(root.Path, showPaths));
        }

        builder.AppendLine(
            "Dosya içerikleri açılmaz; kalıcı çıktıda path, ad ve token bulunmaz.");
        if (!showPaths)
        {
            builder.AppendLine("Tam yollar gizlendi; yerel terminalde görmek için --show-paths kullanın.");
        }

        return builder.ToString();
    }

    internal static string FormatPathForDisplay(string path, bool showPaths)
    {
        if (showPaths)
        {
            return path;
        }

        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfile))
            {
                return "<gizli-path>";
            }

            var fullPath = Path.GetFullPath(path);
            var fullUserProfile = Path.GetFullPath(userProfile);
            var relative = Path.GetRelativePath(fullUserProfile, fullPath);
            if (relative == ".")
            {
                return "%USERPROFILE%";
            }

            if (!Path.IsPathRooted(relative) &&
                relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                return Path.Combine("%USERPROFILE%", relative);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }

        return "<gizli-path>";
    }

    private static bool Confirm()
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine(
                "Etkileşimsiz çalıştırmada açık onay için --yes kullanın.");
            return false;
        }

        Console.Out.Write("Taramayı başlatayım mı? [e/H] ");
        var answer = Console.ReadLine();
        return string.Equals(answer, "e", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(answer, "evet", StringComparison.OrdinalIgnoreCase);
    }

    private static string DefaultOutputPath() =>
        Path.Combine(
            "notes-local",
            "benchmarks",
            "profiles",
            "profile-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
}
