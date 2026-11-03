using BinSweeper.Core.Scan;

namespace BinSweeper.Core.Delete;

public sealed record PurgeOutcome(
    int DeletedEntries,
    int DeletedFiles,
    long DeletedBytes,
    int Failed,
    IReadOnlyList<string> Failures);

public sealed record PurgeProgress(
    int RemainingFiles,
    long RemainingBytes,
    int DeletedEntries,
    int FailedEntries);

/// <summary>
/// 永久删除：跳过待清理区，直接删除回收站里的 $R 内容与 $I 元数据。不可恢复。
/// 只处理可清理小文件条目，大文件、混合目录、未知条目一律不动。
/// </summary>
public static class RecycleBinPurger
{
    private const int MaxFailures = 20;

    /// <summary>删除所有可清理小文件；progress 会在删除过程中回调，用于界面实时减少计数。</summary>
    public static PurgeOutcome Purge(
        IEnumerable<ScannedEntry> entries,
        CancellationToken token = default,
        Action<PurgeProgress>? progress = null)
    {
        var candidates = entries.Where(e => e.IsEligible).ToList();
        var totalFiles = candidates.Sum(e => e.IsDirectory ? e.SmallFileCount : 1);
        var totalBytes = candidates.Sum(e => e.SmallBytes);

        var deletedEntries = 0;
        var deletedFiles = 0;
        var deletedBytes = 0L;
        var failed = 0;
        var failures = new List<string>();
        var lastReportAt = DateTime.UtcNow;

        foreach (var entry in candidates)
        {
            token.ThrowIfCancellationRequested();

            var error = DeleteEntry(entry);
            if (error is not null)
            {
                failed++;
                if (failures.Count < MaxFailures)
                {
                    failures.Add($"{entry.OriginalPath}: {error}");
                }

                continue;
            }

            deletedEntries++;
            deletedFiles += entry.IsDirectory ? entry.SmallFileCount : 1;
            deletedBytes += entry.SmallBytes;

            // 限流，避免每个条目都触发一次界面刷新。
            if (progress is not null
                && (DateTime.UtcNow - lastReportAt).TotalMilliseconds >= 80)
            {
                lastReportAt = DateTime.UtcNow;
                progress(new PurgeProgress(
                    totalFiles - deletedFiles,
                    totalBytes - deletedBytes,
                    deletedEntries,
                    failed));
            }
        }

        progress?.Invoke(new PurgeProgress(totalFiles - deletedFiles, totalBytes - deletedBytes, deletedEntries, failed));

        return new PurgeOutcome(deletedEntries, deletedFiles, deletedBytes, failed, failures);
    }

    /// <summary>删除一条回收站条目；返回 null 表示成功，否则返回错误原因。</summary>
    public static string? DeleteEntry(ScannedEntry entry)
    {
        if (!entry.IsEligible)
        {
            return "条目不在可清理范围";
        }

        try
        {
            // 先删内容，再删 $I：中途失败时元数据还在，条目会以未知状态被后续扫描跳过。
            DeleteContent(entry.ContentPath);

            // 某些安全策略会让删除调用“成功”但文件仍在，必须复查。
            if (File.Exists(entry.ContentPath) || Directory.Exists(entry.ContentPath))
            {
                return "内容未被删除（可能被安全策略拦截）";
            }

            FileDeleter.DeleteFileIfExists(entry.MetaPath);
            if (File.Exists(entry.MetaPath))
            {
                return "$I 未被删除（可能被安全策略拦截）";
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private static void DeleteContent(string path)
    {
        if (Directory.Exists(path))
        {
            FileDeleter.DeleteDirectoryTree(path);
        }
        else if (File.Exists(path))
        {
            FileDeleter.DeleteFileIfExists(path);
        }
    }
}
