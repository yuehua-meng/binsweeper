using BinSweeper.Core.Delete;
using BinSweeper.Core.Policy;
using BinSweeper.Core.RecycleBin;
using BinSweeper.Core.Scan;

namespace BinSweeper.Tests;

public sealed class RecycleBinPurgerTests
{
    private const long Limit = 1L << 20;

    [Fact]
    public void PurgesOnlyEligibleSmallFiles()
    {
        using var temp = new TempDirectory();
        var bin = Path.Combine(temp.FullPath, "S-1-5-21-test");
        Directory.CreateDirectory(bin);

        WriteFileEntry(bin, "SMALL.txt", new byte[100], @"C:\Work\small.txt");
        WriteFileEntry(bin, "BIG.bin", new byte[2 * 1024 * 1024], @"C:\Work\big.bin");
        WriteFileEntry(bin, "OTHER.txt", new byte[10], @"D:\Other\other.txt");
        WriteDirectoryEntry(bin, "MIX", new[] { ("a.txt", 100), ("big.bin", 2 * 1024 * 1024) }, @"C:\Work\mix");

        var report = Scan(bin);
        var outcome = RecycleBinPurger.Purge(report.Entries);

        Assert.Equal(1, outcome.DeletedEntries);
        Assert.Equal(1, outcome.DeletedFiles);
        Assert.Equal(100, outcome.DeletedBytes);
        Assert.Equal(0, outcome.Failed);

        Assert.False(File.Exists(Path.Combine(bin, "$RSMALL.txt")));
        Assert.False(File.Exists(Path.Combine(bin, "$ISMALL.txt")));

        // 大文件、范围外条目、混合目录都不能被动到。
        Assert.True(File.Exists(Path.Combine(bin, "$RBIG.bin")));
        Assert.True(File.Exists(Path.Combine(bin, "$IBIG.bin")));
        Assert.True(File.Exists(Path.Combine(bin, "$ROTHER.txt")));
        Assert.True(Directory.Exists(Path.Combine(bin, "$RMIX")));
    }

    [Fact]
    public void PurgesDirectoryTreeIncludingReadOnlyFiles()
    {
        using var temp = new TempDirectory();
        var bin = Path.Combine(temp.FullPath, "S-1-5-21-test");
        Directory.CreateDirectory(bin);

        var directory = Path.Combine(bin, "$RPURE");
        Directory.CreateDirectory(Path.Combine(directory, "nested"));
        WriteFile(Path.Combine(directory, "a.txt"), 200);
        var readOnly = Path.Combine(directory, "nested", "b.txt");
        WriteFile(readOnly, 300);
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        File.WriteAllBytes(
            Path.Combine(bin, "$IPURE"),
            TestData.BuildInfoFile(500, DateTime.UtcNow, @"C:\Work\pure"));

        var report = Scan(bin);
        var entry = report.Entries.Single(e => Path.GetFileName(e.MetaPath) == "$IPURE");
        Assert.Equal(EntryKind.EligibleSmallDirectory, entry.Kind);

        var outcome = RecycleBinPurger.Purge(report.Entries);

        Assert.Equal(1, outcome.DeletedEntries);
        Assert.Equal(2, outcome.DeletedFiles);
        Assert.Equal(500, outcome.DeletedBytes);
        Assert.False(Directory.Exists(directory));
        Assert.False(File.Exists(Path.Combine(bin, "$IPURE")));
    }

    [Fact]
    public void SkipsEntryWhoseContentIsMissing()
    {
        using var temp = new TempDirectory();
        var bin = Path.Combine(temp.FullPath, "S-1-5-21-test");
        Directory.CreateDirectory(bin);

        // 只有 $I、没有 $R，扫描即判为 Unknown，清理时不能碰它。
        File.WriteAllBytes(
            Path.Combine(bin, "$IORPHAN.txt"),
            TestData.BuildInfoFile(50, DateTime.UtcNow, @"C:\Work\orphan.txt"));

        var report = Scan(bin);
        var outcome = RecycleBinPurger.Purge(report.Entries);

        Assert.Equal(0, outcome.DeletedEntries);
        Assert.True(File.Exists(Path.Combine(bin, "$IORPHAN.txt")));
    }

    private static ScanReport Scan(string bin)
        => new RecycleBinScanner(
                new ScanOptions { ScopeRoots = new[] { @"C:\Work" } },
                new PolicySettings { SmallFileLimitBytes = Limit })
            .Scan(new[] { new RecycleBinVolume(@"C:\", bin) });

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
            WriteFile(Path.Combine(directory, name), size);
            total += size;
        }

        File.WriteAllBytes(
            Path.Combine(bin, "$I" + key),
            TestData.BuildInfoFile(total, DateTime.UtcNow, originalPath));
    }

    private static void WriteFile(string path, int size)
        => File.WriteAllBytes(path, new byte[size]);
}
