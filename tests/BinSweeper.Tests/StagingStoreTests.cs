using BinSweeper.Core.Scan;
using BinSweeper.Core.Staging;

namespace BinSweeper.Tests;

public sealed class StagingStoreTests
{
    [Fact]
    public void StageMovesPairThenRestorePutsItBack()
    {
        using var temp = new TempDirectory();
        var bin = Path.Combine(temp.FullPath, "recycle");
        Directory.CreateDirectory(bin);

        var metaPath = Path.Combine(bin, "$ISMALL.txt");
        var contentPath = Path.Combine(bin, "$RSMALL.txt");
        File.WriteAllBytes(contentPath, new byte[100]);
        File.WriteAllBytes(metaPath, TestData.BuildInfoFile(100, DateTime.UtcNow, @"C:\Work\small.txt"));

        var entry = new ScannedEntry
        {
            MetaPath = metaPath,
            ContentPath = contentPath,
            ContentExists = true,
            IsDirectory = false,
            Kind = EntryKind.EligibleSmallFile,
            Size = 100,
            SmallFileCount = 1,
            SmallBytes = 100,
            OriginalPath = @"C:\Work\small.txt",
            InScope = true,
        };

        var store = new StagingStore(Path.Combine(temp.FullPath, "staging"));
        var outcome = store.Stage(entry, DateTimeOffset.Now);

        Assert.True(outcome.Success);
        Assert.False(File.Exists(metaPath));
        Assert.False(File.Exists(contentPath));
        Assert.True(File.Exists(outcome.Item!.MetaPath));
        Assert.True(File.Exists(outcome.Item.ContentPath));

        var listed = store.List();
        Assert.Single(listed);
        Assert.Equal(@"C:\Work\small.txt", listed[0].OriginalPath);
        Assert.Equal(bin, listed[0].BinDirectory);
        Assert.Null(listed[0].Error);

        var error = store.Restore(listed[0]);

        Assert.Null(error);
        Assert.True(File.Exists(metaPath));
        Assert.True(File.Exists(contentPath));
        Assert.Empty(store.List());
    }

    [Fact]
    public void StageMovesDirectoryAsWhole()
    {
        using var temp = new TempDirectory();
        var bin = Path.Combine(temp.FullPath, "recycle");
        Directory.CreateDirectory(bin);

        var metaPath = Path.Combine(bin, "$INODE");
        var contentPath = Path.Combine(bin, "$RNODE");
        Directory.CreateDirectory(contentPath);
        File.WriteAllBytes(Path.Combine(contentPath, "a.txt"), new byte[10]);
        File.WriteAllBytes(metaPath, TestData.BuildInfoFile(10, DateTime.UtcNow, @"C:\Work\node"));

        var entry = new ScannedEntry
        {
            MetaPath = metaPath,
            ContentPath = contentPath,
            ContentExists = true,
            IsDirectory = true,
            Kind = EntryKind.EligibleSmallDirectory,
            Size = 10,
            SmallFileCount = 1,
            SmallBytes = 10,
            OriginalPath = @"C:\Work\node",
            InScope = true,
        };

        var store = new StagingStore(Path.Combine(temp.FullPath, "staging"));
        var outcome = store.Stage(entry, DateTimeOffset.Now);

        Assert.True(outcome.Success);
        Assert.False(Directory.Exists(contentPath));
        Assert.True(Directory.Exists(outcome.Item!.ContentPath));

        var error = store.Restore(store.List()[0]);

        Assert.Null(error);
        Assert.True(Directory.Exists(contentPath));
    }

    [Fact]
    public void DeleteRemovesStagedItemPermanently()
    {
        using var temp = new TempDirectory();
        var bin = Path.Combine(temp.FullPath, "recycle");
        Directory.CreateDirectory(bin);

        var metaPath = Path.Combine(bin, "$ISMALL.txt");
        var contentPath = Path.Combine(bin, "$RSMALL.txt");
        File.WriteAllBytes(contentPath, new byte[100]);
        File.WriteAllBytes(metaPath, TestData.BuildInfoFile(100, DateTime.UtcNow, @"C:\Work\small.txt"));

        var entry = new ScannedEntry
        {
            MetaPath = metaPath,
            ContentPath = contentPath,
            ContentExists = true,
            IsDirectory = false,
            Kind = EntryKind.EligibleSmallFile,
            Size = 100,
            SmallFileCount = 1,
            SmallBytes = 100,
            OriginalPath = @"C:\Work\small.txt",
            InScope = true,
        };

        var store = new StagingStore(Path.Combine(temp.FullPath, "staging"));
        var outcome = store.Stage(entry, DateTimeOffset.Now);

        Assert.True(outcome.Success);

        // 只读文件也要能清掉。
        File.SetAttributes(outcome.Item!.ContentPath, FileAttributes.ReadOnly);

        var item = store.List()[0];
        var error = store.Delete(item);

        Assert.Null(error);
        Assert.False(Directory.Exists(item.DirectoryPath));
        Assert.Empty(store.List());
    }

    [Fact]
    public void RejectsEntryOnAnotherVolume()
    {
        var store = new StagingStore(Path.Combine(Path.GetTempPath(), "binsweeper-staging"));
        var entry = new ScannedEntry
        {
            MetaPath = @"Z:\$Recycle.Bin\S-1-5-21-test\$IABC.txt",
            ContentPath = @"Z:\$Recycle.Bin\S-1-5-21-test\$RABC.txt",
            IsDirectory = false,
            Kind = EntryKind.EligibleSmallFile,
            Size = 10,
            SmallFileCount = 1,
        };

        var outcome = store.Stage(entry, DateTimeOffset.Now);

        Assert.False(outcome.Success);
        Assert.Contains("不在同一个卷", outcome.Error);
    }
}
