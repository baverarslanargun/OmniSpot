using System.CommandLine;
using System.Globalization;
using System.Text.Json;

namespace OmniSpot.Benchmarking.Ntfs;

internal static class MftPipelineSimCommand
{
    internal static Command CreateCommand()
    {
        var command = new Command("mft-pipeline-sim",
            "MFT hattı denetleyicilerini varsayılan disk modelleri üzerinde benzetir; ölçüm değil, model hesabıdır.")
        {
            Hidden = true
        };
        var gate = new Option<string>("--nvme-gate") { DefaultValueFactory = _ => "1220,1330,1390,1440,1440,1440,1440,1440" };
        var loadFactor = new Option<string>("--load-factor") { DefaultValueFactory = _ => "0.5" };
        var parseMs = new Option<string>("--parse-ms") { DefaultValueFactory = _ => "0.5" };
        var parseProfile = new Option<string>("--parse-profile") { DefaultValueFactory = _ => "1,1,1" };
        var mftMiB = new Option<string>("--mft-mib") { DefaultValueFactory = _ => "4625" };
        var seeds = new Option<int>("--seeds") { DefaultValueFactory = _ => 7 };
        var output = new Option<string?>("--output");
        foreach (var option in new Option[] { gate, loadFactor, parseMs, parseProfile, mftMiB, seeds, output })
            command.Options.Add(option);
        command.SetAction(result => Run(Numbers(result.GetValue(gate)!), Numbers(result.GetValue(loadFactor)!)[0],
            Numbers(result.GetValue(parseMs)!)[0], Numbers(result.GetValue(parseProfile)!), Numbers(result.GetValue(mftMiB)!)[0],
            result.GetValue(seeds), result.GetValue(output)));
        return command;
    }

    private static double[] Numbers(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => double.Parse(part, CultureInfo.InvariantCulture)).ToArray();

    private static int Run(double[] nvmeGate, double loadFactor, double parseMs, double[] regions, double mftMiB,
        int seeds, string? outputPath)
    {
        var profile = Enumerable.Range(0, 10).Select(decile => regions[Math.Min(regions.Length - 1, decile * regions.Length / 10)]).ToArray();
        double[] sata = [480, 530, 540, 545];
        double[] hddMild = [160, 158, 155, 150, 145, 140, 135, 130];
        double[] hddHarsh = [160, 110, 85, 70, 60, 52, 46, 42];
        double[] usbHdd = [90, 78, 60, 46, 38, 32, 28, 25];
        var scenarios = new[]
        {
            new MftSimScenario("nvme-sakin", "ssd", nvmeGate, mftMiB, parseMs, 8, ParseProfile: profile),
            new MftSimScenario("nvme-yuk", "ssd", nvmeGate, mftMiB, parseMs, 8, LoadFromSeconds: 0.5, LoadToSeconds: 1e9, LoadFactor: loadFactor, ParseProfile: profile),
            new MftSimScenario("nvme-yuk-gecici", "ssd", nvmeGate, mftMiB, parseMs, 8, LoadFromSeconds: 1.0, LoadToSeconds: 2.5, LoadFactor: loadFactor, ParseProfile: profile),
            new MftSimScenario("nvme-yavas-alici", "ssd", nvmeGate, mftMiB, parseMs, 8, ConsumerMiBPerSecond: 600, ParseProfile: profile),
            new MftSimScenario("nvme-kucuk-birim", "ssd", nvmeGate, 300, parseMs, 8, ParseProfile: profile),
            new MftSimScenario("nvme-zayif-islemci", "ssd", nvmeGate, mftMiB, parseMs * 2, 2, ParseProfile: profile),
            new MftSimScenario("sata-ssd", "ssd", sata, mftMiB, parseMs, 8, ParseProfile: profile),
            new MftSimScenario("hdd-yumusak", "hdd", hddMild, mftMiB, parseMs, 8, ParseProfile: profile),
            new MftSimScenario("hdd-sert", "hdd", hddHarsh, mftMiB, parseMs, 8, ParseProfile: profile),
            new MftSimScenario("hdd-sert-yuk", "hdd", hddHarsh, mftMiB, parseMs, 8, LoadFromSeconds: 10, LoadToSeconds: 20, LoadFactor: 0.5, ParseProfile: profile),
            new MftSimScenario("hdd-sert-zayif-islemci", "hdd", hddHarsh, mftMiB, parseMs * 2, 2, ParseProfile: profile),
            new MftSimScenario("usb-hdd", "hdd", usbHdd, mftMiB, parseMs, 4, ParseProfile: profile)
        };

        var rows = new List<object>();
        foreach (var scenario in scenarios)
        {
            var logical = scenario.Cores * 2;
            var maxWorkers = Math.Max(2, Math.Clamp(scenario.Cores - 1, 1, 8));
            var hdd = scenario.DeviceClass == "hdd";
            var candidates = new List<(string Name, Func<IMftSimController> Create)>();
            foreach (var workers in new[] { 1, 2, 3, 4, 6, 8, 12, 16 })
            {
                foreach (var readers in new[] { 1, 2, 4, 64 })
                {
                    if (readers != 64 && readers > workers)
                        continue;
                    var r = readers;
                    var n = workers;
                    candidates.Add(($"sabit r{(r == 64 ? "*" : r.ToString())}/n{n}", () => new MftPipelineSim.Fixed(r, n)));
                }
            }
            var fixedOutcomes = candidates.Select(candidate => Median(scenario, candidate.Name, candidate.Create, seeds)).ToList();
            var oracle = fixedOutcomes.MinBy(outcome => outcome.Seconds)!;
            var fixedFour = fixedOutcomes.First(outcome => outcome.Controller == "sabit r*/n4");
            var fixedOne = fixedOutcomes.First(outcome => outcome.Controller == "sabit r1/n1");
            var fixedSixteen = fixedOutcomes.First(outcome => outcome.Controller == "sabit r*/n16");
            var startWorkers = Math.Clamp(scenario.Cores / 2, 1, 4);
            var proposed = Median(scenario, "öneri", () => new MftPipelineSim.Proposed(
                hdd ? 1 : 2, hdd ? 2 : 4, startWorkers, maxWorkers, hdd ? 0.9 : 1.5), seeds);
            var queued = Median(scenario, "rampa + kuyruk", () => new MftPipelineSim.Queued(
                hdd ? 1 : 2, hdd ? 2 : 4, startWorkers, maxWorkers), seeds);
            var queuedWrongPrior = Median(scenario, "rampa + kuyruk, yanlış ön bilgi", () => new MftPipelineSim.Queued(
                hdd ? 2 : 1, 4, startWorkers, maxWorkers), seeds);
            var current = Median(scenario, "bugünkü seçici", () => new MftPipelineSim.CurrentTuner(Math.Clamp(logical / 2, 1, 4)), seeds);
            var old = Median(scenario, "eski ikiye katlama", () => new MftPipelineSim.OldDoubling(), seeds);
            rows.Add(new
            {
                Scenario = scenario.Name,
                scenario.DeviceClass,
                Gate = scenario.GateMiBPerSecond,
                scenario.MftMiB,
                scenario.ParseMsPerMiB,
                scenario.Cores,
                Oracle = oracle,
                FixedFour = fixedFour,
                FixedOne = fixedOne,
                FixedSixteen = fixedSixteen,
                Proposed = proposed,
                ProposedOverOracle = Math.Round(proposed.Seconds / oracle.Seconds, 3),
                Queued = queued,
                QueuedOverOracle = Math.Round(queued.Seconds / oracle.Seconds, 3),
                QueuedWrongPrior = queuedWrongPrior,
                QueuedWrongPriorOverOracle = Math.Round(queuedWrongPrior.Seconds / oracle.Seconds, 3),
                CurrentTuner = current,
                CurrentOverOracle = Math.Round(current.Seconds / oracle.Seconds, 3),
                OldDoubling = old,
                OldOverOracle = Math.Round(old.Seconds / oracle.Seconds, 3)
            });
        }

        var json = JsonSerializer.Serialize(new { Kind = "model", Seeds = seeds, Rows = rows },
            new JsonSerializerOptions { WriteIndented = true });
        Console.Out.WriteLine(json);
        if (!string.IsNullOrWhiteSpace(outputPath))
            File.WriteAllText(outputPath, json);
        return 0;
    }

    private static MftSimOutcome Median(MftSimScenario scenario, string name, Func<IMftSimController> create, int seeds)
    {
        var outcomes = Enumerable.Range(1, seeds)
            .Select(seed => MftPipelineSim.Run(scenario, create(), name, seed * 7919))
            .OrderBy(outcome => outcome.Seconds).ToArray();
        return outcomes[outcomes.Length / 2];
    }
}
