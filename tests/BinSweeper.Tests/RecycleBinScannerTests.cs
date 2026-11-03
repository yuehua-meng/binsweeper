using BinSweeper.Core.Policy;
using BinSweeper.Core.RecycleBin;
using BinSweeper.Core.Scan;

namespace BinSweeper.Tests;

public sealed class RecycleBinScannerTests
{
    private const long Limit = 1L << 20;

    [Fact]
    public void ClassifiesEntriesBySizeScopeAndOrphans()
    {
        using var temp = new TempDirectory();
        var bin = Path.Combine(temp.FullPath, "S-1-5-21-test");
        Directory.CreateDirectory(bin);

        WriteFileEntry(bin, "SMALL.txt", new byte[100], @"C:\Work\small.txt");
        WriteFileEntry(bin, "BIG.bin", new byte[2 * 1024 * 1024], @"C:\Work\big.bin");
        WriteDirectoryEntry(bin, "PURE", new[] { ("a.txt", 200), ("b.txt", 300) }, @"C:\Work\pure");
        WriteDirectoryEntry(bin, "MIX", new[] { ("a.txt", 100), ("big.bin", 2 * 1024 * 1024) }, @"C:\Work\mix");
        WriteFileEntry(bin, "OTHER.txt", new byte[10], @"D:\Other\other.txt");

        // 只有 $I、没有 $R：内容丢失，按未知处理。
        File.WriteAllBytes(
            Path.Combine(bin, "$IORPHAN.txt"),
            TestData.BuildInfoFile(50, DateTime.UtcNow, @"C:\Work\orphan.txt"));

        var scanner = new RecycleBinScanner(
            new ScanOptions { ScopeRoots = new[] { @"C:\Work" } },
            new PolicySettings { SmallFileLimitBytes = Limit });

        var report = scanner.Scan(new[] { new RecycleBinVolume(@"C:\", bin) });

        Assert.Equal(6, report.TotalEntries);
        Assert.Equal(EntryKind.EligibleSmallFile, ByKey(report, "SMALL.txt").Kind);
        Assert.Equal(EntryKind.LargeFile, ByKey(report, "BIG.bin").Kind);
        Assert.Equal(EntryKind.EligibleSmallDirectory, ByKey(report, "PURE").Kind);
        Assert.Equal(EntryKind.MixedDirectory, ByKey(report, "MIX").Kind);
        Assert.Equal(EntryKind.Unknown, ByKey(report, "ORPHAN.txt").Kind);
        Assert.False(ByKey(report, "OTHER.txt").InScope);

        Assert.Equal(3, report.EligibleFileCount);
        Assert.Equal(1, report.LargeFileCount);
        Assert.Equal(1, report.MixedDirectoryCount);
        Assert.Equal(1, report.UnknownCount);
        Assert.Equal(1, report.OutOfScopeCount);
        Assert.Equal(WaterLevel.Normal, report.Level);
    }

    [Fact]
    public void ReportsWaterLevelFromEligibleSmallFileCount()
    {
        using var temp = new TempDirectory();
        var bin = Path.Combine(temp.FullPath, "S-1-5-21-test");
        Directory.CreateDirectory(bin);

        WriteFileEntry(bin, "A.txt", new byte[10], @"C:\Work\a.txt");
        WriteFileEntry(bin, "B.txt", new byte[10], @"C:\Work\b.txt");
        WriteFileEntry(bin, "C.txt", new byte[10], @"C:\Work\c.txt");

        var scanner = new RecycleBinScanner(
            new ScanOptions { ScopeRoots = new[] { @"C:\Work" } },
            new PolicySettings { SmallFileLimitBytes = Limit, TriggerCount = 2, TargetCount = 1 });

        var report = scanner.Scan(new[] { new RecycleBinVolume(@"C:\", bin) });

        Assert.Equal(3, report.EligibleFileCount);
        Assert.Equal(WaterLevel.AboveTrigger, report.Level);
    }

    private static ScannedEntry ByKey(ScanReport report, string key)
        => report.Entries.Single(e => Path.GetFileName(e.MetaPath) == "$I" + key);

    private static void WriteFileEntry(string bin, string key, byte[] content, string originalPath)
    {
        File.WriteAllBytes(Path.Combine(bin, "$R" + key), content);
        File.WriteAllBytes(
            Path.Combine(bin, "$I" + key),
            TestData.BuildInfoFile(content.Length, DateTime.UtcNow, originalPath));
    }

    private static void WriteDirectoryEntry(
        string bin,
        string key,
        (string Name, int Size)[] files,
        string originalPath)
    {
        var directory = Path.Combine(bin, "$R" + key);
        Directory.CreateDirectory(directory);

        var total = 0L;
        foreach (var (name, size) in files)
        {
            File.WriteAllBytes(Path.Combine(directory, name), new byte[size]);
            total += size;
        }

        File.WriteAllBytes(
            Path.Combine(bin, "$I" + key),
            TestData.BuildInfoFile(total, DateTime.UtcNow, originalPath));
    }
}
