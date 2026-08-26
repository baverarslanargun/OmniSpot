using System.CommandLine;
using OmniSpot.Benchmarking.Diagnostics;
using OmniSpot.Benchmarking.Measurements;
using OmniSpot.Benchmarking.Profiling;

namespace OmniSpot.Benchmarking;

internal static class BenchmarkRootCommand
{
    internal static RootCommand Create()
    {
        var rootCommand = new RootCommand("OmniSpot yerel benchmark yardımcı araçları.");
        rootCommand.Subcommands.Add(ProfileCommand.CreateCommand());
        rootCommand.Subcommands.Add(DiagnosticsCommand.CreateCommand());
        MeasurementCommand.AddTo(rootCommand);
        return rootCommand;
    }
}
