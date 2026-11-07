using System.CommandLine;
using OmniSpot.Benchmarking.Diagnostics;
using OmniSpot.Benchmarking.Measurements;
using OmniSpot.Benchmarking.Profiling;
using OmniSpot.Benchmarking.Ntfs;

namespace OmniSpot.Benchmarking;

internal static class BenchmarkRootCommand
{
    internal static RootCommand Create(long? processEntryTimestamp = null)
    {
        var rootCommand = new RootCommand("OmniSpot yerel benchmark yardımcı araçları.");
        rootCommand.Subcommands.Add(ProfileCommand.CreateCommand());
        rootCommand.Subcommands.Add(DiagnosticsCommand.CreateCommand());
        rootCommand.Subcommands.Add(MftProbeCommand.CreateCommand());
        rootCommand.Subcommands.Add(DirectoryProbeCommand.CreateCommand());
        rootCommand.Subcommands.Add(HierarchyProbeCommand.CreateCommand());
        rootCommand.Subcommands.Add(PackedCrashProbeCommand.CreateCommand());
        rootCommand.Subcommands.Add(LiveCatalogProbeCommand.CreateCommand());
        rootCommand.Subcommands.Add(SharedFirstReadyCommand.CreateCommand(
            processEntryTimestamp ?? System.Diagnostics.Stopwatch.GetTimestamp()));
        rootCommand.Subcommands.Add(SharedFirstReadyCommand.CreateServerCommand());
        rootCommand.Subcommands.Add(SharedFirstReadyCommand.CreateServiceCommand());
        rootCommand.Subcommands.Add(SharedFirstReadyCommand.CreateClientCommand(
            processEntryTimestamp ?? System.Diagnostics.Stopwatch.GetTimestamp()));
        MeasurementCommand.AddTo(rootCommand);
        return rootCommand;
    }
}
