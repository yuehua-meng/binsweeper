namespace BinSweeper.Core.Staging;

/// <summary>待清理区里的一条记录，目录内保留原始的 $I/$R 文件。</summary>
public sealed record StagedItem(
    string Id,
    string DirectoryPath,
    string VolumeRoot,
    string BinDirectory,
    string MetaPath,
    string ContentPath,
    bool ContentExists,
    string? OriginalPath,
    long Size,
    DateTimeOffset StagedAt,
    string? Error);

public sealed record StageOutcome(StagedItem? Item, string? Error)
{
    public bool Success => Error is null;

    public static StageOutcome Ok(StagedItem item) => new(item, null);

    public static StageOutcome Fail(string error) => new(null, error);
}
