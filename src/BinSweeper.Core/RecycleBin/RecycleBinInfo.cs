namespace BinSweeper.Core.RecycleBin;

/// <summary>
/// 回收站 $I 元数据文件的解析结果。
/// </summary>
public sealed record RecycleBinInfo(
    long Version,
    long OriginalSize,
    DateTimeOffset? DeletedAt,
    string OriginalPath);
