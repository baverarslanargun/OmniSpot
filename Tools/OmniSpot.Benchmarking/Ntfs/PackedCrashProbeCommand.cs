using System.CommandLine;
using System.Text.Json;
using SmartFileLauncher.Core.Search;

namespace OmniSpot.Benchmarking.Ntfs;

internal static class PackedCrashProbeCommand
{
    internal static Command CreateCommand()
    {
        var command = new Command("packed-crash-probe", "Ayrı sentetik katalogda süreç sonlandırma deneyi.");
        var directory = new Option<string>("--directory") { Required = true };
        var stage = new Option<int>("--stage") { Required = true };
        var verify = new Option<bool>("--verify");
        command.Options.Add(directory); command.Options.Add(stage); command.Options.Add(verify);
        command.SetAction(result => Run(result.GetValue(directory)!, result.GetValue(stage), result.GetValue(verify)));
        return command;
    }
    private static int Run(string directory, int stage, bool verify)
    {
        directory = Path.GetFullPath(directory);
        if (stage < 0 || stage > 3) throw new ArgumentOutOfRangeException(nameof(stage));
        var path = Path.Combine(directory, "catalog.bin");
        var root = new SearchItem("Fixture", @"C:\Fixture", true, null, null, null, 0, "");
        var original = new PackedRecord(new("before.txt", @"C:\Fixture\before.txt", false, 1, null, null, 0, root.FullPath),
            638000000000000000, 637000000000000000, false, false, 638100000000000000);
        var updated = original with { Item = original.Item with { Name = "after.txt", FullPath = @"C:\Fixture\after.txt", SizeBytes = 123, OpenCount = 99 }, Hidden = true };
        var source = new PackedSourcePosition("C:", 123, 100, new Guid("11111111-2222-3333-4444-555555555555"));
        if (!verify)
        {
            if (Directory.Exists(directory)) throw new ArgumentException("Yeni deney dizini gerekli.");
            Directory.CreateDirectory(directory);
            using var builder = new PackedCatalogBuilder(Path.Combine(directory, "scratch"), new BasicTokenizer());
            builder.Add(new(root, original.ModifiedUtc, original.CreatedUtc, false, false, original.IndexedUtc), -1, null);
            builder.Add(original, 0, root.FullPath);
            _ = builder.Complete(path);
        }
        using var store = new PackedCatalogStore(path);
        if (verify)
        {
            var committed = stage != 0;
            var expected = committed ? updated : original;
            if (store.Position.Sequence != (committed ? 1 : 0) || store.Position.Source != (committed ? source : null) ||
                store.FindRecord(expected.Item.FullPath) != expected || store.State.ItemCount != 2 ||
                store.FindRecord(committed ? original.Item.FullPath : updated.Item.FullPath) is not null ||
                store.State.Get(committed ? "after" : "before").Single() != expected.Item)
                throw new InvalidDataException("Süreç kapatma sonrası atomiklik/parite uyuşmadı.");
            var before = new FileInfo(store.ActivePath).Length;
            if (committed && !store.Commit(0, null, source, [new(original.Item.FullPath, null), new(updated.Item.FullPath, updated)]).Replayed)
                throw new InvalidDataException("Süreç tekrarında idempotence uyuşmadı.");
            if (before != new FileInfo(store.ActivePath).Length) throw new InvalidDataException("Tekrar kayıt yazdı.");
            var report = JsonSerializer.Serialize(new { Stage = stage, Verified = true, store.Position, store.State.ItemCount });
            File.WriteAllText(Path.Combine(directory, "verified.json"), report);
            Console.WriteLine(report);
            return 0;
        }
        store.FaultPoint = point =>
        {
            if ((int)point != stage) return;
            File.WriteAllText(Path.Combine(directory, "paused.txt"), point.ToString());
            Thread.Sleep(Timeout.Infinite);
        };
        store.Commit(0, null, source, [new(original.Item.FullPath, null), new(updated.Item.FullPath, updated)]);
        store.Checkpoint();
        throw new InvalidOperationException("Beklenen süreç kesme noktası tetiklenmedi.");
    }
}
