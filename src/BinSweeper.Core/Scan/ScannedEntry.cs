namespace BinSweeper.Core.Scan;

public enum EntryKind
{
    /// <summary>单个小文件，可清理。</summary>
    EligibleSmallFile,

    /// <summary>完全由小文件组成的目录，可整体清理。</summary>
    EligibleSmallDirectory,

    /// <summary>单个大文件，只统计不处理。</summary>
    LargeFile,

    /// <summary>目录内同时存在大文件，整目录跳过。</summary>
    MixedDirectory,

    /// <summary>无法确定情况（$R 丢失、解析失败、目录过大等），不处理。</summary>
    Unknown,
}

/// <summary>回收站中一条顶层条目（一对 $I/$R）的扫描结果。</summary>
public sealed class ScannedEntry
{
    public required string MetaPath { get; init; }

    public required string ContentPath { get; init; }

    public bool ContentExists { get; init; }

    public bool IsDirectory { get; init; }

    public EntryKind Kind { get; init; }

    /// <summary>原始大小（文件取 $I 记录值，目录取内部小文件总量）。</summary>
    public long Size { get; init; }

    /// <summary>目录内部的小文件数；文件条目为 1。</summary>
    public int SmallFileCount { get; init; }

    /// <summary>目录内部的小文件总字节数。</summary>
    public long SmallBytes { get; init; }

    /// <summary>目录内部发现的大文件数（发现后即停止统计，仅表示“存在”）。</summary>
    public int LargeFileCount { get; init; }

    public DateTimeOffset? DeletedAt { get; init; }

    public string? OriginalPath { get; init; }

    /// <summary>原路径是否落在配置的开发目录范围内。</summary>
    public bool InScope { get; init; }

    public string? Reason { get; init; }

    public bool IsEligible => InScope && Kind is EntryKind.EligibleSmallFile or EntryKind.EligibleSmallDirectory;
}
