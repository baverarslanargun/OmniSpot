using System.Diagnostics;
using OmniSpot.Benchmarking.Profiling;
using SmartFileLauncher.Core.Search;

namespace OmniSpot.Benchmarking.Measurements;

internal static class PilotRunner
{
    private static readonly int[] CandidateItemCounts = [1_000, 5_000, 10_000, 25_000, 50_000];
    private static readonly double[] CanaryPercents = [1, 2, 3, 5, 8];

    /// <summary>
    /// Ölçüm öncesi kayda alınmayan ısınma koşumu sayısı.
    /// </summary>
    private const int StabilizationRunCount = 3;

    /// <summary>
    /// Her koşumun kaç bağımsız süreçte ölçüleceği. Tek süreçte ölçüldüğünde
    /// ardışık baseline'lar arasında %5 fark kalıyordu; o fark sürecin kendi
    /// JIT ve bellek yerleşiminden geliyor ve iterasyon sayısını artırmakla
    /// azalmıyor. Örnekler birden çok sürece dağıtılınca bu bileşen ortalanır.
    /// </summary>
    private const int PilotLaunchCount = 3;

    internal static PilotDocument Run(
        int seed,
        int maximumMinutes,
        int? itemCountOverride,
        string artifactsPath,
        ProfileEnvironmentCapture environmentCapture,
        CancellationToken cancellationToken)
    {
        if (maximumMinutes is < 30 or > 60)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumMinutes));
        }

        ArgumentNullException.ThrowIfNull(environmentCapture);
        var started = DateTimeOffset.UtcNow;
        var total = Stopwatch.StartNew();
        var calibration = Calibrate(seed, cancellationToken);
        var itemCount = itemCountOverride ?? calibration
            .Where(sample => sample.ElapsedMilliseconds <= 1_000)
            .Select(sample => sample.ItemCount)
            .LastOrDefault(calibration[0].ItemCount);
        var fixture = SyntheticSearchFixtureGenerator.Create(itemCount, seed);
        var instrument = InstrumentationProbe.Measure();
        // Süreç başına iterasyon. Toplam örnek sayısı PilotLaunchCount ile çarpılır,
        // yani 3 x 20 = 60: önceki turlarla aynı örnek sayısı, üç ayrı süreçten.
        const int pilotWarmups = 12;
        const int pilotMeasurements = 20;
        var runs = new List<PilotRun>();
        var baselines = new List<PilotRun>();

        // Sistem seviyesinde ısınma. Tur 3 ölçümünde ilk koşum 704 ms, on üçüncü
        // koşum 499 ms geldi (%29 fark) ve eğri koşum boyunca oturmadı: dosya
        // cache'i, bellek yerleşimi ve JIT katmanları ancak birkaç koşum sonra
        // dengeye giriyor. Bu koşumlar kayda geçer ama baseline referans havuzuna
        // ve karara girmez; amaçları yalnız ısınmayı ölçümün dışına almak.
        for (var index = 0; index < StabilizationRunCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            runs.Add(Execute(
                "stabilization-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                fixture,
                pilotWarmups,
                pilotMeasurements,
                memoryDiagnoser: false,
                canaryPercent: 0,
                canaryDelayNanoseconds: 0,
                artifactsPath));
        }

        // İlk iki baseline ardışık alınır; ölçüm rejimi (warmup ve iterasyon sayısı)
        // yalnız bunlardan seçilir.
        var baselineZero = Execute(
            "baseline-0",
            fixture,
            pilotWarmups,
            pilotMeasurements,
            memoryDiagnoser: false,
            canaryPercent: 0,
            canaryDelayNanoseconds: 0,
            artifactsPath);
        runs.Add(baselineZero);
        cancellationToken.ThrowIfCancellationRequested();
        var baselineOne = Execute(
            "baseline-1",
            fixture,
            pilotWarmups,
            pilotMeasurements,
            memoryDiagnoser: false,
            canaryPercent: 0,
            canaryDelayNanoseconds: 0,
            artifactsPath);
        runs.Add(baselineOne);
        cancellationToken.ThrowIfCancellationRequested();

        // SelectMeasurementCount ve SelectWarmupCount tüm süreçlerin birleşik örnek
        // dizisi üzerinden çalışır; Execute ise değeri süreç başına iterasyon olarak
        // kullanır. Bölme yapılmazsa sonraki koşumlar PilotLaunchCount katı örnek
        // toplar ve ilk iki baseline ile kıyaslanamaz hale gelir.
        var measurementCount = Math.Max(
            1,
            SelectMeasurementCount(baselineZero, baselineOne) / PilotLaunchCount);
        var warmupCount = Math.Max(
            1,
            SelectWarmupCount(baselineZero.WarmupNanoseconds) / PilotLaunchCount);

        // Diagnoser koşumu ayrı tutulur ve baseline referans havuzuna girmez:
        // MemoryDiagnoser ek iterasyon çalıştırdığı için duvar süresi kıyaslanabilir değildir.
        var allocationRun = Execute(
            "baseline-memory",
            fixture,
            warmupCount,
            measurementCount,
            memoryDiagnoser: true,
            canaryPercent: 0,
            canaryDelayNanoseconds: 0,
            artifactsPath);
        runs.Add(allocationRun);

        // Merdivenin ilk referansı. baseline-0 ve baseline-1 yalnız rejim seçimi
        // içindir ve pilot ayarlarıyla koştukları için referans havuzuna alınmaz;
        // buradan sonraki bütün koşumlar aynı warmup/iterasyon rejimini kullanır.
        cancellationToken.ThrowIfCancellationRequested();
        var firstReference = Execute(
            "baseline-2",
            fixture,
            warmupCount,
            measurementCount,
            memoryDiagnoser: false,
            canaryPercent: 0,
            canaryDelayNanoseconds: 0,
            artifactsPath);
        runs.Add(firstReference);
        baselines.Add(firstReference);

        // Eşleştirilmiş canary merdiveni. Dizi: A B A B A B A B A B A
        // Her canary'nin referansı kendisinden hemen önceki ve hemen sonraki
        // baseline'ın ortalamasıdır; zaman eksenindeki doğrusal drift bu ortalamada
        // birinci derecede iptal olur.
        var delayReferenceMedian = firstReference.MedianNanoseconds;
        var pairs = new List<CanaryPairResult>();
        foreach (var canaryPercent in CanaryPercents)
        {
            if (total.Elapsed >= TimeSpan.FromMinutes(maximumMinutes - 5))
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var canaryDelay = (long)Math.Ceiling(delayReferenceMedian * canaryPercent / 100d);
            var before = baselines[^1];
            var canaryRun = Execute(
                "canary-" + canaryPercent.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                fixture,
                warmupCount,
                measurementCount,
                memoryDiagnoser: false,
                canaryPercent,
                canaryDelay,
                artifactsPath);
            runs.Add(canaryRun);
            cancellationToken.ThrowIfCancellationRequested();
            var after = Execute(
                "baseline-" + (baselines.Count + 2).ToString(System.Globalization.CultureInfo.InvariantCulture),
                fixture,
                warmupCount,
                measurementCount,
                memoryDiagnoser: false,
                canaryPercent: 0,
                canaryDelayNanoseconds: 0,
                artifactsPath);
            runs.Add(after);
            baselines.Add(after);
            var reference = PairedReference(
                before.MedianNanoseconds,
                after.MedianNanoseconds);
            pairs.Add(new CanaryPairResult(
                canaryPercent,
                canaryRun.Label,
                before.Label,
                after.Label,
                reference,
                canaryRun.MedianNanoseconds,
                MeasurementStatistics.ChangePercent(reference, canaryRun.MedianNanoseconds),
                Detected: false));
        }

        // Gürültü bandı ardışık baseline çiftlerinden gelir ve karar istatistiği
        // medyandır (sözleşme §8.1: birincil özet medyandır, p95 ancak örnek sayısı
        // dondurulduktan sonra kullanılır).
        var varianceBand = VarianceBandPercent(
            baselines.Select(baseline => baseline.MedianNanoseconds).ToArray());
        var threshold = RoundUp(Math.Max(0.5, varianceBand), 0.25);
        var canaryPairs = pairs
            .Select(pair => pair with { Detected = pair.PairedChangePercent > threshold })
            .ToArray();
        var minimumDetectableDifference = canaryPairs
            .Where(pair => pair.Detected)
            .Select(pair => (double?)pair.CanaryPercent)
            .FirstOrDefault();

        cancellationToken.ThrowIfCancellationRequested();
        var steadyMemory = SteadyMemoryProbe.Run(
            fixture,
            [0, 5, 15, 30, 60],
            cancellationToken);
        var idleSeconds = SelectIdleSeconds(steadyMemory);
        total.Stop();
        var baselineWall = (baselineZero.WallMilliseconds + baselineOne.WallMilliseconds) / 2d;
        var diagnoserOverhead = baselineWall <= 0
            ? 0
            : (allocationRun.WallMilliseconds - baselineWall) / baselineWall * 100d;
        var projectedDailyMinutes =
            2d * (allocationRun.WallMilliseconds + idleSeconds * 1_000d) / 60_000d;
        var environment = environmentCapture.Complete();
        var failures = new List<string>();
        if (minimumDetectableDifference is null)
        {
            failures.Add("canary_mdd_not_found");
        }

        if (canaryPairs.Length != CanaryPercents.Length)
        {
            failures.Add("canary_ladder_incomplete");
        }

        // Karara giren bütün koşumlar aynı örnek sayısıyla ölçülmelidir; aksi halde
        // farklı büyüklükteki dağılımlar karşılaştırılır ve varyans bandı sahte çıkar.
        var comparedSampleCounts = runs
            .Where(run => run.CanaryPercent > 0 || baselines.Contains(run))
            .Select(run => run.WorkloadNanoseconds.Count)
            .Distinct()
            .Count();
        if (comparedSampleCounts > 1)
        {
            failures.Add("compared_sample_count_mismatch");
        }

        if (minimumDetectableDifference is double minimum &&
            canaryPairs.Any(pair => pair.CanaryPercent >= minimum && !pair.Detected))
        {
            failures.Add("canary_detection_not_monotonic");
        }

        if (projectedDailyMinutes > 10)
        {
            failures.Add("daily_budget_exceeded");
        }

        if (total.Elapsed > TimeSpan.FromMinutes(maximumMinutes))
        {
            failures.Add("pilot_budget_exceeded");
        }

        if (environment.Labels.Contains("frekans-kaymasi", StringComparer.Ordinal))
        {
            failures.Add("frequency_drift_detected");
        }

        var overheadMedian = baselineZero.OverheadNanoseconds.Count == 0
            ? 0
            : MeasurementStatistics.Median(baselineZero.OverheadNanoseconds);
        var instrumentation = new InstrumentationOverhead(
            instrument.TimestampPairNanoseconds,
            instrument.AllocationPairNanoseconds,
            overheadMedian,
            (long)Math.Round(baselineWall));
        var regime = new PilotRegime(
            failures.Count == 0,
            itemCount,
            warmupCount,
            measurementCount,
            MeasurementConstants.PercentileMethod,
            MeasurementConstants.OutlierMode,
            varianceBand,
            threshold,
            minimumDetectableDifference,
            idleSeconds,
            diagnoserOverhead,
            projectedDailyMinutes,
            DecisionStatistic: "median",
            CanaryDesign: "paired-interleaved");
        return new PilotDocument(
            MeasurementConstants.SchemaMajor,
            MeasurementConstants.SchemaMinor,
            MeasurementConstants.ContractVersion,
            MeasurementConstants.ToolVersion,
            started.ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            fixture.Manifest,
            environment,
            calibration,
            instrumentation,
            steadyMemory,
            runs,
            canaryPairs,
            regime,
            failures);
    }

    /// <summary>
    /// Bir canary koşumunun referansı: kendisinden hemen önceki ve hemen sonraki
    /// baseline medyanlarının ortalaması. Ölçümler eşit aralıklı ve drift doğrusalsa
    /// bu ortalama tam olarak canary koşumunun zaman noktasına denk gelir, dolayısıyla
    /// drift farktan düşer.
    /// </summary>
    internal static double PairedReference(
        double baselineBeforeMedian,
        double baselineAfterMedian) =>
        (baselineBeforeMedian + baselineAfterMedian) / 2d;

    /// <summary>
    /// Gürültü bandı: ardışık baseline koşumları arasındaki en büyük mutlak yüzde
    /// değişimi. Baseline'lar canary koşumlarıyla dönüşümlü alındığı için bu band
    /// gerçek koşumlar-arası gürültüyü temsil eder.
    /// </summary>
    internal static double VarianceBandPercent(IReadOnlyList<double> baselineMedians)
    {
        ArgumentNullException.ThrowIfNull(baselineMedians);
        var band = 0d;
        for (var index = 1; index < baselineMedians.Count; index++)
        {
            band = Math.Max(
                band,
                Math.Abs(MeasurementStatistics.ChangePercent(
                    baselineMedians[index - 1],
                    baselineMedians[index])));
        }

        return band;
    }

    private static IReadOnlyList<CalibrationSample> Calibrate(
        int seed,
        CancellationToken cancellationToken)
    {
        var samples = new List<CalibrationSample>();
        foreach (var itemCount in CandidateItemCounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fixture = SyntheticSearchFixtureGenerator.Create(itemCount, seed);
            _ = SearchState.Create(fixture.Nodes, new BasicTokenizer());
            var stopwatch = Stopwatch.StartNew();
            var state = SearchState.Create(fixture.Nodes, new BasicTokenizer());
            stopwatch.Stop();
            GC.KeepAlive(state);
            samples.Add(new CalibrationSample(itemCount, stopwatch.Elapsed.TotalMilliseconds));
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(1))
            {
                break;
            }
        }

        return samples;
    }

    private static PilotRun Execute(
        string label,
        SyntheticSearchFixture fixture,
        int warmupCount,
        int iterationCount,
        bool memoryDiagnoser,
        double canaryPercent,
        long canaryDelayNanoseconds,
        string artifactsPath)
    {
        var result = BenchmarkExecutor.Run(new BenchmarkExecutionRequest(
            fixture.Manifest,
            PilotLaunchCount,
            warmupCount,
            iterationCount,
            memoryDiagnoser,
            canaryPercent,
            canaryDelayNanoseconds,
            Path.Combine(artifactsPath, label)));
        return new PilotRun(
            label,
            canaryPercent,
            result.WallMilliseconds,
            MeasurementStatistics.Median(result.WorkloadNanoseconds),
            MeasurementStatistics.P95(result.WorkloadNanoseconds),
            result.AllocatedBytesPerOperation,
            result.WarmupNanoseconds,
            result.WorkloadNanoseconds,
            result.OverheadNanoseconds);
    }

    private static int SelectMeasurementCount(PilotRun first, PilotRun second)
    {
        // Süreç başına iterasyon adayları; toplam örnek sayısı PilotLaunchCount katıdır.
        foreach (var count in new[] { 10, 15, 20 })
        {
            if (first.WorkloadNanoseconds.Count < count || second.WorkloadNanoseconds.Count < count)
            {
                continue;
            }

            var firstDelta = Math.Abs(MeasurementStatistics.ChangePercent(
                first.P95Nanoseconds,
                MeasurementStatistics.P95(first.WorkloadNanoseconds.Take(count).ToArray())));
            var secondDelta = Math.Abs(MeasurementStatistics.ChangePercent(
                second.P95Nanoseconds,
                MeasurementStatistics.P95(second.WorkloadNanoseconds.Take(count).ToArray())));
            if (firstDelta <= 2 && secondDelta <= 2)
            {
                return count;
            }
        }

        return Math.Min(first.WorkloadNanoseconds.Count, second.WorkloadNanoseconds.Count);
    }

    private static int SelectWarmupCount(IReadOnlyList<double> warmups)
    {
        if (warmups.Count <= 3)
        {
            return warmups.Count;
        }

        var reference = MeasurementStatistics.Median(warmups.TakeLast(3).ToArray());
        for (var count = 3; count <= warmups.Count; count++)
        {
            var settled = warmups.Skip(count - 1).All(value =>
                Math.Abs(MeasurementStatistics.ChangePercent(reference, value)) <= 5);
            if (settled)
            {
                return count;
            }
        }

        return warmups.Count;
    }

    private static int SelectIdleSeconds(IReadOnlyList<SteadyMemorySample> samples)
    {
        foreach (var sample in samples.Where(candidate => candidate.IdleSeconds > 0))
        {
            var tail = samples.Where(candidate => candidate.IdleSeconds >= sample.IdleSeconds).ToArray();
            var managedStable = IsStable(tail.Select(candidate => candidate.ManagedMemoryBytes));
            var privateStable = IsStable(tail.Select(candidate => candidate.PrivateMemoryBytes));
            if (managedStable && privateStable)
            {
                return sample.IdleSeconds;
            }
        }

        return samples.Count == 0 ? 0 : samples[^1].IdleSeconds;
    }

    private static bool IsStable(IEnumerable<long> values)
    {
        var array = values.ToArray();
        var minimum = array.Min();
        var maximum = array.Max();
        return minimum > 0 && (maximum - minimum) / (double)minimum <= 0.02;
    }

    private static double RoundUp(double value, double step) =>
        Math.Ceiling(value / step) * step;
}
