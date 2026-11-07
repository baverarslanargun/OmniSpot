using Xunit;

namespace OmniSpot.Benchmarking.Tests.Measurements;

public sealed class BenchmarkRootCommandTests
{
    [Fact]
    public void Create_ExposesApprovedB2CommandsAtRoot()
    {
        var root = BenchmarkRootCommand.Create();
        var commandNames = root.Subcommands
            .Where(command => !command.Hidden)
            .Select(command => command.Name)
            .ToArray();

        Assert.Equal(
            ["profile", "diag", "mft-probe", "directory-probe", "hierarchy-probe",
                "packed-crash-probe", "live-catalog-probe", "shared-first-ready",
                "shared-first-ready-client",
                "pilot", "measure", "compare", "phases", "realtree"],
            commandNames);
        Assert.True(root.Subcommands.Single(command =>
            command.Name == "shared-first-ready-server").Hidden);
        Assert.True(root.Subcommands.Single(command =>
            command.Name == "shared-first-ready-service").Hidden);
    }
}
