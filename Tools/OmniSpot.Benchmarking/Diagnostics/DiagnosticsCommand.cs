using System.CommandLine;
using System.Text.Json;

namespace OmniSpot.Benchmarking.Diagnostics;

internal static class DiagnosticsCommand
{
    private const int DefaultTopCount = 12;

    internal static Command CreateCommand()
    {
        var command = new Command(
            "diag",
            "Tanılama penceresinin yazdığı sayaç CSV'sini özetler; OLAY pencereleri arasındaki farkları çıkarır.");

        var fileOption = new Option<string>("--file")
        {
            Description = "omnispot-*-metrik.csv yolu.",
            Required = true
        };
        var topOption = new Option<int>("--top")
        {
            Description = "Pencere başına gösterilecek en çok değişen metrik sayısı.",
            DefaultValueFactory = _ => DefaultTopCount
        };
        var outputOption = new Option<string?>("--output")
        {
            Description = "Özetin JSON çıktı yolu."
        };

        command.Options.Add(fileOption);
        command.Options.Add(topOption);
        command.Options.Add(outputOption);
        command.SetAction(parseResult => Run(
            parseResult.GetValue(fileOption)!,
            parseResult.GetValue(topOption),
            parseResult.GetValue(outputOption)));

        return command;
    }

    private static int Run(string filePath, int topCount, string? outputPath)
    {
        if (topCount < 1)
        {
            Console.Error.WriteLine("--top en az 1 olmalı.");
            return 2;
        }

        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"Dosya bulunamadı: {filePath}");
            return 2;
        }

        MetricLogParseResult parsed;
        try
        {
            parsed = MetricLogParser.Parse(ReadLines(filePath));
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Dosya okunamadı: {ex.Message}");
            return 2;
        }

        var analysis = MetricLogAnalyzer.Analyze(parsed);
        Console.Out.Write(MetricLogSummaryFormatter.Format(filePath, analysis, topCount));

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                outputPath,
                JsonSerializer.Serialize(
                    analysis,
                    new JsonSerializerOptions { WriteIndented = true }));
            Console.Out.WriteLine();
            Console.Out.WriteLine($"JSON yazıldı: {outputPath}");
        }

        return analysis.RowCount == 0 ? 1 : 0;
    }

    private static IEnumerable<string> ReadLines(string filePath)
    {
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}
