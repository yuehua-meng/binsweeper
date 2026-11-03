using BinSweeper.Core.Delete;
using BinSweeper.Core.Policy;
using BinSweeper.Core.RecycleBin;
using BinSweeper.Core.Scan;
using BinSweeper.Core.Staging;

namespace BinSweeper.App;

public sealed record StageSummary(
    int MovedEntries,
    int MovedFiles,
    long MovedBytes,
    int Failed,
    int Remaining,
    IReadOnlyList<string> Failures);

public sealed record RestoreSummary(int Restored, int Failed);

public sealed record ClearStagedSummary(
    int Deleted,
    long DeletedBytes,
    int Failed,
    IReadOnlyList<string> Failures);

public sealed record PurgeSummary(
    int DeletedEntries,
    int DeletedFiles,
    long DeletedBytes,
    int Failed,
    IReadOnlyList<string> Failures);

/// <summary>界面调用的同步业务入口；耗时操作由调用方放进后台线程。</summary>
public sealed class SweepWorker
{
    private readonly PolicySettings _policy;

    public SweepWorker(PolicySettings policy)
    {
        _policy = policy;
    }

    public PolicySettings Policy => _policy;

    public ScanReport Scan(CancellationToken token = default)
    {
        var volumes = RecycleBinLocator.LocateCurrentUser();
        return new RecycleBinScanner(new ScanOptions(), _policy).Scan(volumes, token);
    }

    public IReadOnlyList<StagedItem> ListStaged()
        => new StagingStore(StagingStore.DefaultRoot).List();

    public StageSummary Stage(CancellationToken token = default)
        => Stage(_policy.TargetCount, token);

    public StageSummary Stage(int target, CancellationToken token = default)
    {
        var report = Scan(token);
        var store = new StagingStore(StagingStore.DefaultRoot);

        var remaining = report.EligibleFileCount;
        var movedEntries = 0;
        var movedFiles = 0;
        var movedBytes = 0L;
        var failed = 0;
        var failures = new List<string>();

        var candidates = report.Entries
            .Where(e => e.IsEligible)
            .OrderByDescending(e => e.IsDirectory ? e.SmallFileCount : 1)
            .ThenByDescending(e => e.SmallBytes);

        foreach (var entry in candidates)
        {
            if (remaining <= target)
            {
                break;
            }

            token.ThrowIfCancellationRequested();

            var outcome = store.Stage(entry, DateTimeOffset.Now);
            if (outcome.Item is null)
            {
                failed++;
                if (failures.Count < 10)
                {
                    failures.Add($"{entry.OriginalPath}: {outcome.Error}");
                }

                continue;
            }

            var files = entry.IsDirectory ? entry.SmallFileCount : 1;
            remaining -= files;
            movedEntries++;
            movedFiles += files;
            movedBytes += entry.SmallBytes;
        }

        return new StageSummary(movedEntries, movedFiles, movedBytes, failed, remaining, failures);
    }

    /// <summary>跳过待清理区，永久删除所有可清理小文件；大文件、混合目录、未知条目不动。</summary>
    public PurgeSummary Purge(IProgress<PurgeProgress>? progress = null, CancellationToken token = default)
    {
        var report = Scan(token);
        var outcome = RecycleBinPurger.Purge(
            report.Entries.Where(e => e.IsEligible).ToList(),
            token,
            p => progress?.Report(p));

        return new PurgeSummary(
            outcome.DeletedEntries,
            outcome.DeletedFiles,
            outcome.DeletedBytes,
            outcome.Failed,
            outcome.Failures);
    }

    public RestoreSummary RestoreAll()
    {
        var store = new StagingStore(StagingStore.DefaultRoot);
        var restored = 0;
        var failed = 0;

        foreach (var item in store.List())
        {
            if (store.Restore(item) is null)
            {
                restored++;
            }
            else
            {
                failed++;
            }
        }

        return new RestoreSummary(restored, failed);
    }

    /// <summary>永久清空待清理区，删除后不可恢复。</summary>
    public ClearStagedSummary ClearStaged()
    {
        var store = new StagingStore(StagingStore.DefaultRoot);
        var items = store.List();
        var deleted = 0;
        var deletedBytes = 0L;
        var failed = 0;
        var failures = new List<string>();

        foreach (var item in items)
        {
            var error = store.Delete(item);
            if (error is null)
            {
                deleted++;
                deletedBytes += item.Size;
                continue;
            }

            failed++;
            if (failures.Count < 10)
            {
                failures.Add($"{item.OriginalPath ?? item.Id}: {error}");
            }
        }

        return new ClearStagedSummary(deleted, deletedBytes, failed, failures);
    }
}
