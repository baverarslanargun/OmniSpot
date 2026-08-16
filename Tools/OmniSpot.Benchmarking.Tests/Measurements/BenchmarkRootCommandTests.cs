using Xunit;

namespace OmniSpot.Benchmarking.Tests.Measurements;

public sealed class BenchmarkRootCommandTests
{
    [Fact]
    public void Create_ExposesApprovedB2CommandsAtRoot()
    {
        var commandNames = BenchmarkRootCommand.Create()
            .Subcommands
            .Select(command => command.Name)
            .ToArray();

        Assert.Equal(
            ["profile", "pilot", "measure", "compare", "phases", "realtree"],
            commandNames);
    }
}
