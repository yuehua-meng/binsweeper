using System.Globalization;
using BinSweeper.Core.Delete;
using BinSweeper.Core.RecycleBin;
using BinSweeper.Core.Scan;

namespace BinSweeper.Core.Staging;

/// <summary>
/// 待清理区：把回收站里成对的 $I/$R 以同卷 rename 的方式移到
/// %LOCALAPPDATA%\BinSweeper\staging\&lt;卷&gt; 下，条目从回收站消失但内容仍完整保留，可随时放回。
/// </summary>
public sealed class StagingStore
{
    private const string MetaPrefix = "$I";
    private const string ContentPrefix = "$R";
    private const string TimestampFormat = "yyyyMMdd-HHmmss";
    private const string OriginFileName = "origin.txt";

    public StagingStore(string rootDirectory)
    {
        RootDirectory = rootDirectory;
    }

    public string RootDirectory { get; }

    /// <summary>环境变量 BINSWEEPER_STAGING 可覆盖待清理区位置（便于测试和自定义存放）。</summary>
    public static string DefaultRoot
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("BINSWEEPER_STAGING");
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                return Path.GetFullPath(overridden);
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BinSweeper",
                "staging");
        }
    }

    public static bool IsSameVolume(string path, string otherPath)
    {
        var rootA = Path.GetPathRoot(Path.GetFullPath(path));
        var rootB = Path.GetPathRoot(Path.GetFullPath(otherPath));
        return rootA is not null
            && rootB is not null
            && string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
    }

    public string VolumeDirectory(string volumeRoot)
        => Path.Combine(RootDirectory, VolumeKey(volumeRoot));

    /// <summary>把一条回收站条目整体移入待清理区；不是同卷则直接拒绝。</summary>
    public StageOutcome Stage(ScannedEntry entry, DateTimeOffset now)
    {
        var binDirectory = Path.GetDirectoryName(entry.MetaPath);
        if (binDirectory is null)
        {
            return StageOutcome.Fail("无法确定回收站目录");
        }

        if (!IsSameVolume(RootDirectory, entry.MetaPath))
        {
            return StageOutcome.Fail($"条目位于 {Path.GetPathRoot(entry.MetaPath)}，与待清理区不在同一个卷");
        }

        if (!File.Exists(entry.MetaPath))
        {
            return StageOutcome.Fail("$I 元数据已不存在");
        }

        if (!File.Exists(entry.ContentPath) && !Directory.Exists(entry.ContentPath))
        {
            return StageOutcome.Fail("$R 内容已不存在");
        }

        var volumeDirectory = VolumeDirectory(Path.GetPathRoot(entry.MetaPath)!);
        var itemId = $"{now.LocalDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture)}-{RandomSuffix()}";
        var itemDirectory = Path.Combine(volumeDirectory, itemId);

        try
        {
            Directory.CreateDirectory(itemDirectory);
            // 记录来源回收站目录，恢复时按原路放回。
            File.WriteAllText(Path.Combine(itemDirectory, OriginFileName), binDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StageOutcome.Fail($"无法创建待清理目录：{ex.Message}");
        }

        var stagedMetaPath = Path.Combine(itemDirectory, Path.GetFileName(entry.MetaPath));
        var stagedContentPath = Path.Combine(itemDirectory, Path.GetFileName(entry.ContentPath));
        var isDirectory = entry.IsDirectory && Directory.Exists(entry.ContentPath);

        try
        {
            MoveContent(entry.ContentPath, stagedContentPath, isDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteDirectory(itemDirectory, contentMayRemain: false);
            return StageOutcome.Fail($"搬运内容失败：{ex.Message}");
        }

        try
        {
            File.Move(entry.MetaPath, stagedMetaPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 元数据没搬走，内容必须搬回去，否则回收站里会留下孤立 $R。
            TryMoveContent(stagedContentPath, entry.ContentPath, isDirectory);
            TryDeleteDirectory(itemDirectory, contentMayRemain: true);
            return StageOutcome.Fail($"搬运元数据失败：{ex.Message}");
        }

        return StageOutcome.Ok(new StagedItem(
            itemId,
            itemDirectory,
            Path.GetPathRoot(entry.MetaPath)!,
            binDirectory,
            stagedMetaPath,
            stagedContentPath,
            true,
            entry.OriginalPath,
            entry.Size,
            now,
            null));
    }

    /// <summary>把待清理区的条目放回回收站（仍可在资源管理器里还原到原位置）。</summary>
    public string? Restore(StagedItem item)
    {
        var binDirectory = item.BinDirectory;
        if (string.IsNullOrEmpty(binDirectory) || !Directory.Exists(binDirectory))
        {
            return $"回收站目录不存在：{binDirectory}";
        }

        if (item.Error is not null)
        {
            return item.Error;
        }

        var metaName = Path.GetFileName(item.MetaPath);
        var contentName = Path.GetFileName(item.ContentPath);
        var destinationMetaPath = Path.Combine(binDirectory, metaName);
        var destinationContentPath = Path.Combine(binDirectory, contentName);

        if (File.Exists(destinationMetaPath) || Directory.Exists(destinationMetaPath))
        {
            return "$I 目标位置已被占用";
        }

        if (File.Exists(destinationContentPath) || Directory.Exists(destinationContentPath))
        {
            return "$R 目标位置已被占用";
        }

        var isDirectory = Directory.Exists(item.ContentPath);

        try
        {
            MoveContent(item.ContentPath, destinationContentPath, isDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"放回内容失败：{ex.Message}";
        }

        try
        {
            File.Move(item.MetaPath, destinationMetaPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryMoveContent(destinationContentPath, item.ContentPath, isDirectory);
            return $"放回元数据失败：{ex.Message}";
        }

        TryDeleteDirectory(item.DirectoryPath, contentMayRemain: false);
        return null;
    }

    /// <summary>永久删除待清理区里的一条：内容、$I、origin.txt 一并删除，不可恢复。</summary>
    public string? Delete(StagedItem item)
    {
        try
        {
            if (Directory.Exists(item.DirectoryPath))
            {
                FileDeleter.DeleteDirectoryTree(item.DirectoryPath);
            }

            if (Directory.Exists(item.DirectoryPath))
            {
                return "待清理区目录未被删除（可能被安全策略拦截）";
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    public IReadOnlyList<StagedItem> List()
    {
        var items = new List<StagedItem>();
        if (!Directory.Exists(RootDirectory))
        {
            return items;
        }

        IEnumerable<string> volumeDirectories;
        try
        {
            volumeDirectories = Directory.EnumerateDirectories(RootDirectory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return items;
        }

        foreach (var volumeDirectory in volumeDirectories)
        {
            var volumeRoot = VolumeRootFromKey(Path.GetFileName(volumeDirectory));
            if (volumeRoot is null)
            {
                continue;
            }

            IEnumerable<string> itemDirectories;
            try
            {
                itemDirectories = Directory.EnumerateDirectories(volumeDirectory).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            items.AddRange(itemDirectories.Select(dir => ReadItem(dir, volumeRoot)));
        }

        return items.OrderBy(i => i.StagedAt).ToList();
    }

    private static StagedItem ReadItem(string itemDirectory, string volumeRoot)
    {
        var id = Path.GetFileName(itemDirectory);
        var stagedAt = ParseTimestamp(id) ?? DateTimeOffset.Now;
        var binDirectory = string.Empty;

        try
        {
            var originPath = Path.Combine(itemDirectory, OriginFileName);
            if (File.Exists(originPath))
            {
                binDirectory = File.ReadAllText(originPath).Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读取失败时按缺失处理，下面会给出异常原因。
        }

        var metaFiles = Directory.Exists(itemDirectory)
            ? Directory.EnumerateFiles(itemDirectory, MetaPrefix + "*").ToList()
            : new List<string>();

        if (metaFiles.Count == 0)
        {
            return new StagedItem(id, itemDirectory, volumeRoot, binDirectory, string.Empty, string.Empty, false, null, 0, stagedAt, "待清理区里缺少 $I");
        }

        var metaPath = metaFiles[0];
        var contentPath = Path.Combine(itemDirectory, ContentPrefix + Path.GetFileName(metaPath)[MetaPrefix.Length..]);
        var info = RecycleBinInfoParser.TryParse(metaPath, out var parseError);

        var error = parseError;
        if (error is null && binDirectory.Length == 0)
        {
            error = "缺少 origin.txt，无法确定来源回收站";
        }

        return new StagedItem(
            id,
            itemDirectory,
            volumeRoot,
            binDirectory,
            metaPath,
            contentPath,
            File.Exists(contentPath) || Directory.Exists(contentPath),
            info?.OriginalPath,
            info?.OriginalSize ?? 0,
            stagedAt,
            error);
    }

    private static DateTimeOffset? ParseTimestamp(string id)
    {
        if (id.Length < 15)
        {
            return null;
        }

        try
        {
            var local = DateTime.ParseExact(
                id[..15],
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None);
            return new DateTimeOffset(local);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void MoveContent(string source, string destination, bool isDirectory)
    {
        if (isDirectory)
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    private static void TryMoveContent(string source, string destination, bool isDirectory)
    {
        try
        {
            MoveContent(source, destination, isDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 回滚失败时保留现场，交由上层报告。
        }
    }

    /// <summary>
    /// 清理暂存目录：contentMayRemain 为 true 时只删除空目录，避免回滚失败后误删未搬回的内容。
    /// 文件句柄释放有延迟，失败时重试几次。
    /// </summary>
    private static void TryDeleteDirectory(string directory, bool contentMayRemain)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                var originPath = Path.Combine(directory, OriginFileName);
                if (File.Exists(originPath))
                {
                    File.Delete(originPath);
                }

                if (contentMayRemain && Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    return;
                }

                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static string VolumeKey(string volumeRoot)
    {
        var root = volumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return root.TrimEnd(':');
    }

    private static string? VolumeRootFromKey(string key)
        => key.Length == 1 && char.IsLetter(key[0]) ? key + @":\" : null;

    private static string RandomSuffix()
        => Random.Shared.Next(0x10000000, 0x7FFFFFFF).ToString("x8", CultureInfo.InvariantCulture);
}
