using OmniSpot.Benchmarking;
using System.Diagnostics;

var processEntryTimestamp = Stopwatch.GetTimestamp();
return await BenchmarkRootCommand.Create(processEntryTimestamp).Parse(args).InvokeAsync();
