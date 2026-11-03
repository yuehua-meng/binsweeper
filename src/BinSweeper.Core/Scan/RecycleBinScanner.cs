using BinSweeper.Core.Policy;
using BinSweeper.Core.RecycleBin;

namespace BinSweeper.Core.Scan;

/// <summary>
/// 只读扫描：遍历当前用户每个卷上的回收站目录，解析 $I 元数据并判定条目类型。
/// 本阶段不做任何删除或移动。
/// </summary>
public sealed class RecycleBinScanner
{
    private static readonly EnumerationOptions DirectoryEnumeration = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    private readonly ScanOptions _options;
    private readonly PolicySettings _policy;

    public RecycleBinScanner(ScanOptions options, PolicySettings policy)
    {
        _options = options;
        _policy = policy;
    }

    public ScanReport Scan(IEnumerable<RecycleBinVolume> volumes, CancellationToken cancellationToken = default)
    {
        var entries = new List<ScannedEntry>();
        var skipped = new List<string>();

        foreach (var volume in volumes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var metaPath in EnumerateMetaFiles(volume.BinDirectory, skipped))
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(Inspect(volume.BinDirectory, metaPath));
            }
        }

        return new ScanReport(entries, skipped, _policy, DateTimeOffset.Now);
    }

    private static IReadOnlyList<string> EnumerateMetaFiles(string binDirectory, List<string> skipped)
    {
        try
        {
            return Directory.EnumerateFiles(binDirectory, "$I*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false,
                AttributesToSkip = 0,
                MatchCasing = MatchCasing.CaseInsensitive,
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped.Add($"{binDirectory}: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private ScannedEntry Inspect(string binDirectory, string metaPath)
    {
        var metaName = Path.GetFileName(metaPath);
        var key = metaName.Length > 2 ? metaName[2..] : string.Empty;
        var contentPath = key.Length == 0 ? string.Empty : Path.Combine(binDirectory, "$R" + key);

        var info = RecycleBinInfoParser.TryParse(metaPath, out var parseError);
        if (info is null || key.Length == 0)
        {
            return Unknown(metaPath, contentPath, parseError ?? "元数据文件名无效");
        }

        var inScope = IsInScope(info.OriginalPath);

        if (!File.Exists(contentPath) && !Directory.Exists(contentPath))
        {
            return Unknown(metaPath, contentPath, "对应的 $R 内容已不存在", info, inScope);
        }

        if (Directory.Exists(contentPath))
        {
            return InspectDirectory(metaPath, contentPath, info, inScope);
        }

        var size = info.OriginalSize;
        if (size <= 0)
        {
            try
            {
                size = new FileInfo(contentPath).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Unknown(metaPath, contentPath, $"无法读取文件大小：{ex.Message}", info, inScope);
            }
        }

        var kind = size <= _policy.SmallFileLimitBytes ? EntryKind.EligibleSmallFile : EntryKind.LargeFile;

        return new ScannedEntry
        {
            MetaPath = metaPath,
            ContentPath = contentPath,
            ContentExists = true,
            IsDirectory = false,
            Kind = kind,
            Size = size,
            SmallFileCount = kind == EntryKind.EligibleSmallFile ? 1 : 0,
            SmallBytes = kind == EntryKind.EligibleSmallFile ? size : 0,
            DeletedAt = info.DeletedAt,
            OriginalPath = info.OriginalPath,
            InScope = inScope,
        };
    }

    private ScannedEntry InspectDirectory(string metaPath, string contentPath, RecycleBinInfo info, bool inScope)
    {
        var measurement = MeasureDirectory(contentPath);

        if (measurement.LargeFileCount > 0)
        {
            return new ScannedEntry
            {
                MetaPath = metaPath,
                ContentPath = contentPath,
                ContentExists = true,
                IsDirectory = true,
                Kind = EntryKind.MixedDirectory,
                Size = measurement.SmallBytes + measurement.LargeBytes,
                SmallFileCount = measurement.SmallFileCount,
                SmallBytes = measurement.SmallBytes,
                LargeFileCount = measurement.LargeFileCount,
                DeletedAt = info.DeletedAt,
                OriginalPath = info.OriginalPath,
                InScope = inScope,
                Reason = $"目录内含大文件（{measurement.FirstLargeFile}），整目录跳过",
            };
        }

        if (measurement.Truncated)
        {
            return Unknown(metaPath, contentPath, "目录内部未能完整枚举，按未知处理", info, inScope);
        }

        return new ScannedEntry
        {
            MetaPath = metaPath,
            ContentPath = contentPath,
            ContentExists = true,
            IsDirectory = true,
            Kind = EntryKind.EligibleSmallDirectory,
            Size = measurement.SmallBytes,
            SmallFileCount = measurement.SmallFileCount,
            SmallBytes = measurement.SmallBytes,
            DeletedAt = info.DeletedAt,
            OriginalPath = info.OriginalPath,
            InScope = inScope,
        };
    }

    private ScannedEntry Unknown(
        string metaPath,
        string contentPath,
        string reason,
        RecycleBinInfo? info = null,
        bool inScope = false)
        => new()
        {
            MetaPath = metaPath,
            ContentPath = contentPath,
            ContentExists = false,
            IsDirectory = false,
            Kind = EntryKind.Unknown,
            DeletedAt = info?.DeletedAt,
            OriginalPath = info?.OriginalPath,
            InScope = inScope,
            Reason = reason,
        };

    private DirectoryMeasurement MeasureDirectory(string root)
    {
        var smallFileCount = 0;
        var smallBytes = 0L;
        var largeFileCount = 0;
        var largeBytes = 0L;
        string? firstLargeFile = null;
        var visited = 0;

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            List<string> children;
            try
            {
                children = Directory
                    .EnumerateFileSystemEntries(current, "*", DirectoryEnumeration)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new DirectoryMeasurement(smallFileCount, smallBytes, largeFileCount, largeBytes, firstLargeFile, true);
            }

            foreach (var child in children)
            {
                if (++visited > _options.MaxEntriesPerDirectory)
                {
                    return new DirectoryMeasurement(smallFileCount, smallBytes, largeFileCount, largeBytes, firstLargeFile, true);
                }

                if (Directory.Exists(child))
                {
                    pending.Push(child);
                    continue;
                }

                long length;
                try
                {
                    length = new FileInfo(child).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new DirectoryMeasurement(smallFileCount, smallBytes, largeFileCount, largeBytes, firstLargeFile, true);
                }

                if (length > _policy.SmallFileLimitBytes)
                {
                    // 发现大文件即可判定整目录跳过，无需继续枚举。
                    return new DirectoryMeasurement(
                        smallFileCount,
                        smallBytes,
                        largeFileCount + 1,
                        largeBytes + length,
                        firstLargeFile ?? child,
                        false);
                }

                smallFileCount++;
                smallBytes += length;
            }
        }

        return new DirectoryMeasurement(smallFileCount, smallBytes, largeFileCount, largeBytes, firstLargeFile, false);
    }

    private bool IsInScope(string originalPath)
    {
        if (_options.ScopeRoots.Count == 0)
        {
            return true;
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(originalPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (var scopeRoot in _options.ScopeRoots)
        {
            string rootFull;
            try
            {
                rootFull = Path.GetFullPath(scopeRoot);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (IsUnder(normalized, rootFull))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnder(string path, string root)
    {
        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmedRoot.Length == 0)
        {
            return false;
        }

        if (path.Equals(trimmedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record DirectoryMeasurement(
        int SmallFileCount,
        long SmallBytes,
        int LargeFileCount,
        long LargeBytes,
        string? FirstLargeFile,
        bool Truncated);
}
