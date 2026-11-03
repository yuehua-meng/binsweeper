using BinSweeper.Core.RecycleBin;

namespace BinSweeper.Tests;

public sealed class RecycleBinInfoParserTests
{
    [Fact]
    public void ParsesVersion2Metadata()
    {
        using var temp = new TempDirectory();
        var metaPath = Path.Combine(temp.FullPath, "$IABC123.txt");
        var deletedAt = new DateTime(2026, 5, 1, 10, 30, 0, DateTimeKind.Utc);

        File.WriteAllBytes(metaPath, TestData.BuildInfoFile(12_345, deletedAt, @"D:\work\a.txt"));

        var info = RecycleBinInfoParser.TryParse(metaPath, out var error);

        Assert.Null(error);
        Assert.NotNull(info);
        Assert.Equal(2, info!.Version);
        Assert.Equal(12_345, info.OriginalSize);
        Assert.Equal(@"D:\work\a.txt", info.OriginalPath);
        Assert.Equal(deletedAt, info.DeletedAt!.Value.UtcDateTime);
    }

    [Fact]
    public void RejectsVersion1()
    {
        using var temp = new TempDirectory();
        var metaPath = Path.Combine(temp.FullPath, "$IABC123.txt");
        var bytes = TestData.BuildInfoFile(100, DateTime.UtcNow, @"C:\a.txt");
        BitConverter.GetBytes(1L).CopyTo(bytes, 0);

        File.WriteAllBytes(metaPath, bytes);

        var info = RecycleBinInfoParser.TryParse(metaPath, out var error);

        Assert.Null(info);
        Assert.Contains("版本 1", error);
    }

    [Fact]
    public void RejectsTruncatedPath()
    {
        using var temp = new TempDirectory();
        var metaPath = Path.Combine(temp.FullPath, "$IABC123.txt");
        var bytes = TestData.BuildInfoFile(100, DateTime.UtcNow, @"C:\a.txt");

        File.WriteAllBytes(metaPath, bytes[..40]);

        var info = RecycleBinInfoParser.TryParse(metaPath, out var error);

        Assert.Null(info);
        Assert.Contains("截断", error);
    }
}
