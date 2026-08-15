using System.CommandLine;
using System.Text.Json;
using OmniSpot.Benchmarking.Profiling;

namespace OmniSpot.Benchmarking.Measurements;

internal static class MeasurementCommand
{
    internal static void AddTo(RootCommand rootCommand)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        rootCommand.Subcommands.Add(CreatePilotCommand());
        rootCommand.Subcommands.Add(CreateMeasureCommand());
        rootCommand.Subcommands.Add(CreateCompareCommand());
        rootCommand.Subcommands.Add(CreatePhasesCommand());
    }

    private static Command CreatePhasesCommand()
    {
        var command = new Command(
            "phases",
            "SearchState.Create fazlarının süre ve allocation payını ölçer.");
        var seedOption = IntegerOption("--seed", MeasurementConstants.DefaultSeed);
        var itemCountOption = IntegerOption("--item-count", 500_000);
        var repeatsOption = IntegerOption("--repeats", 3);
        var warmupItemCountOption = IntegerOption("--warmup-item-count", 20_000);
        var outputOption = new Option<string?>("--output")
        {
            Description = "Faz dağılımı JSON çıktı yolu."
        };
        command.Options.Add(seedOption);
        command.Options.Add(itemCountOption);
        command.Options.Add(repeatsOption);
        command.Options.Add(warmupItemCountOption);
        command.Options.Add(outputOption);
        command.SetAction((parseResult, cancellationToken) => RunPhasesAsync(
            new PhaseSplitRequest(
                parseResult.GetValue(itemCountOption),
                parseResult.GetValue(seedOption),
                parseResult.GetValue(repeatsOption),
                parseResult.GetValue(warmupItemCountOption)),
            parseResult.GetValue(outputOption),
            cancellationToken));
        return command;
    }

    private static Command CreatePilotCommand()
    {
        var command = new Command(
            "pilot",
            "B-2 ölçüm rejimini 30-60 dakikalık deneyle belirler.");
        var seedOption = IntegerOption("--seed", MeasurementConstants.DefaultSeed);
        var maximumMinutesOption = IntegerOption("--max-minutes", 45);
        var itemCountOption = IntegerOption("--item-count", 0);
        itemCountOption.Description =
            "Fixture öğe sayısını sabitler. Verilmezse kalibrasyonla seçilir.";
        var outputOption = new Option<string?>("--output")
        {
            Description = "Pilot JSON çıktı yolu."
        };
        command.Options.Add(seedOption);
        command.Options.Add(maximumMinutesOption);
        command.Options.Add(itemCountOption);
        command.Options.Add(outputOption);
        command.SetAction((parseResult, cancellationToken) => RunPilotAsync(
            parseResult.GetValue(seedOption),
            parseResult.GetValue(maximumMinutesOption),
            parseResult.GetValue(itemCountOption),
            parseResult.GetValue(outputOption),
            cancellationToken));
        return command;
    }

    private static Command CreateMeasureCommand()
    {
        var command = new Command(
            "measure",
            "Dondurulmuş B-2 rejimiyle SearchState.Create ölçümü üretir.");
        var outputOption = new Option<string?>("--output")
        {
            Description = "Ölçüm JSON çıktı yolu."
        };
        command.Options.Add(outputOption);
        command.SetAction((parseResult, cancellationToken) => RunMeasureAsync(
            parseResult.GetValue(outputOption),
            cancellationToken));
        return command;
    }

    private static Command CreateCompareCommand()
    {
        var command = new Command(
            "compare",
            "İki uyumlu B-2 ölçümünü karşılaştırır.");
        var baselineOption = new Option<string>("--baseline")
        {
            Description = "Önce ölçüm JSON'u.",
            Required = true
        };
        var candidateOption = new Option<string>("--candidate")
        {
            Description = "Sonra ölçüm JSON'u.",
            Required = true
        };
        var outputOption = new Option<string?>("--output")
        {
            Description = "İsteğe bağlı karşılaştırma JSON çıktı yolu."
        };
        var allowCanaryOption = new Option<bool>("--allow-canary")
        {
            Description = "Yalnız pilotta canary farkına izin verir."
        };
        command.Options.Add(baselineOption);
        command.Options.Add(candidateOption);
        command.Options.Add(outputOption);
        command.Options.Add(allowCanaryOption);
        command.SetAction((parseResult, cancellationToken) => RunCompareAsync(
            parseResult.GetRequiredValue(baselineOption),
            parseResult.GetRequiredValue(candidateOption),
            parseResult.GetValue(outputOption),
            parseResult.GetValue(allowCanaryOption),
            cancellationToken));
        return command;
    }

    private static async Task<int> RunPilotAsync(
        int seed,
        int maximumMinutes,
        int itemCount,
        string? outputPath,
        CancellationToken cancellationToken)
    {
        if (maximumMinutes is < 30 or > 60)
        {
            Console.Error.WriteLine("--max-minutes 30 ile 60 arasında olmalı.");
            return 2;
        }

        if (itemCount < 0)
        {
            Console.Error.WriteLine("--item-count negatif olamaz.");
            return 2;
        }

        var environmentCapture = BeginEnvironmentCapture();
        if (!ValidateEnvironment(environmentCapture.StartEnvironment))
        {
            return 3;
        }

        try
        {
            var resolvedOutput = ResolveOutput(outputPath, "pilots", "pilot");
            var artifacts = Path.Combine(
                "notes-local",
                "benchmarks",
                "artifacts",
                Path.GetFileNameWithoutExtension(resolvedOutput));
            var pilot = PilotRunner.Run(
                seed,
                maximumMinutes,
                itemCount > 0 ? itemCount : null,
                artifacts,
                environmentCapture,
                cancellationToken);
            await MeasurementJson.WriteAsync(resolvedOutput, pilot, cancellationToken);
            Console.Out.Write(MeasurementSummaryFormatter.Format(pilot));
            Console.Out.WriteLine("Pilot JSON çıktısı yazıldı.");
            return pilot.Regime.Passed ? 0 : 5;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("B-2 pilotu iptal edildi.");
            return 130;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine("B-2 pilotu tamamlanamadı: " + exception.Message);
            return 4;
        }
    }

    private static async Task<int> RunMeasureAsync(
        string? outputPath,
        CancellationToken cancellationToken)
    {
        if (MeasurementConstants.FrozenMeasurementCount <= 0)
        {
            Console.Error.WriteLine("B-2 pilot rejimi henüz dondurulmadı; önce pilot çalıştırılmalı.");
            return 6;
        }

        var environmentCapture = BeginEnvironmentCapture();
        if (!ValidateEnvironment(environmentCapture.StartEnvironment))
        {
            return 3;
        }

        try
        {
            var resolvedOutput = ResolveOutput(outputPath, "measurements", "measurement");
            var document = MeasurementRunner.Run(
                new MeasurementRunRequest(
                    MeasurementConstants.FrozenItemCount,
                    MeasurementConstants.DefaultSeed,
                    MeasurementConstants.FrozenLaunchCount,
                    MeasurementConstants.FrozenWarmupCount,
                    MeasurementConstants.FrozenMeasurementCount,
                    MemoryDiagnoserEnabled: true,
                    [MeasurementConstants.FrozenIdleSeconds],
                    MeasurementConstants.FrozenRegressionThresholdPercent,
                    MeasurementConstants.FrozenMinimumDetectableDifferencePercent,
                    CanaryPercent: 0,
                    CanaryDelayNanoseconds: 0,
                    Path.Combine(
                        "notes-local",
                        "benchmarks",
                        "artifacts",
                        Path.GetFileNameWithoutExtension(resolvedOutput))),
                environmentCapture,
                cancellationToken);
            await MeasurementJson.WriteAsync(resolvedOutput, document, cancellationToken);
            Console.Out.Write(MeasurementSummaryFormatter.Format(document));
            Console.Out.WriteLine("Ölçüm JSON çıktısı yazıldı.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("B-2 ölçümü iptal edildi.");
            return 130;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine("B-2 ölçümü tamamlanamadı: " + exception.Message);
            return 4;
        }
    }

    private static async Task<int> RunPhasesAsync(
        PhaseSplitRequest request,
        string? outputPath,
        CancellationToken cancellationToken)
    {
        if (request.ItemCount < 1 || request.RepeatCount < 1 || request.WarmupItemCount < 0)
        {
            Console.Error.WriteLine("--item-count ve --repeats en az 1, --warmup-item-count negatif olamaz.");
            return 2;
        }

        var environmentCapture = BeginEnvironmentCapture();
        if (!ValidateProfilerLaneEnvironment(environmentCapture.StartEnvironment))
        {
            return 3;
        }

        try
        {
            var resolvedOutput = ResolveOutput(outputPath, "phases", "phases");
            var document = PhaseSplitRunner.Run(request, environmentCapture, cancellationToken);
            await MeasurementJson.WriteAsync(resolvedOutput, document, cancellationToken);
            Console.Out.Write(PhaseSplitSummaryFormatter.Format(document));
            Console.Out.WriteLine("Faz dağılımı JSON çıktısı yazıldı.");
            return document.AcceptanceFailures.Count == 0 ? 0 : 5;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Faz dağılımı ölçümü iptal edildi.");
            return 130;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine("Faz dağılımı ölçümü tamamlanamadı: " + exception.Message);
            return 4;
        }
    }

    private static async Task<int> RunCompareAsync(
        string baselinePath,
        string candidatePath,
        string? outputPath,
        bool allowCanary,
        CancellationToken cancellationToken)
    {
        try
        {
            var baseline = MeasurementJson.Deserialize<MeasurementDocument>(
                await File.ReadAllTextAsync(baselinePath, cancellationToken));
            var candidate = MeasurementJson.Deserialize<MeasurementDocument>(
                await File.ReadAllTextAsync(candidatePath, cancellationToken));
            var comparison = MeasurementComparer.Compare(baseline, candidate, allowCanary);
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                await MeasurementJson.WriteAsync(outputPath, comparison, cancellationToken);
            }

            Console.Out.Write(MeasurementSummaryFormatter.Format(comparison));
            return comparison.Verdict == ComparisonVerdict.Regression ? 10 : 0;
        }
        catch (IncompatibleMeasurementException exception)
        {
            Console.Error.WriteLine("Karşılaştırma reddedildi:");
            foreach (var failure in exception.Failures)
            {
                Console.Error.WriteLine("  - " + failure);
            }

            return 7;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Console.Error.WriteLine("Karşılaştırma girdisi okunamadı: " + exception.Message);
            return 4;
        }
    }

    private static ProfileEnvironmentCapture BeginEnvironmentCapture() =>
        ProfileEnvironmentProbe.BeginCapture(Array.Empty<ProfileRootRequest>());

    private static bool ValidateEnvironment(ProfileEnvironment environment)
    {
        if (environment.ServerGc)
        {
            Console.Error.WriteLine("B-2 yalnız Workstation GC ile çalışır.");
            return false;
        }

        if (environment.OmniSpotProcessRunning)
        {
            Console.Error.WriteLine("Ölçümden önce çalışan OmniSpot sürecini kapatın.");
            return false;
        }

        if (environment.ProcessorThrottleMaxAcStartPercent != 99 ||
            environment.ProcessorThrottleMaxDcStartPercent != 99)
        {
            Console.Error.WriteLine(
                "B-2 için PROCTHROTTLEMAX AC ve DC değerleri %99 olmalı.");
            return false;
        }

        if (environment.ProcessorNominalBaseMhz is not > 0 ||
            environment.ProcessorFrequencyStartMhz is not > 0)
        {
            Console.Error.WriteLine("B-2 CPU frekans örneği alınamadı.");
            return false;
        }

        return true;
    }

    private static bool ValidateProfilerLaneEnvironment(ProfileEnvironment environment)
    {
        if (environment.ServerGc)
        {
            Console.Error.WriteLine("Faz dağılımı yalnız Workstation GC ile çalışır.");
            return false;
        }

        if (environment.OmniSpotProcessRunning)
        {
            Console.Error.WriteLine("Ölçümden önce çalışan OmniSpot sürecini kapatın.");
            return false;
        }

        if (environment.ProcessorThrottleMaxAcStartPercent != 99 ||
            environment.ProcessorThrottleMaxDcStartPercent != 99)
        {
            Console.Out.WriteLine(
                "Uyarı: PROCTHROTTLEMAX %99 değil. Faz payları oran olduğundan koşum " +
                "sürdürülüyor; mutlak süreler kalıcı baseline sayılmaz.");
        }

        return true;
    }

    private static Option<int> IntegerOption(string name, int defaultValue) =>
        new(name)
        {
            DefaultValueFactory = _ => defaultValue
        };

    private static string ResolveOutput(string? outputPath, string folder, string prefix) =>
        string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(
                "notes-local",
                "benchmarks",
                folder,
                prefix + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json")
            : Path.GetFullPath(outputPath);
}
