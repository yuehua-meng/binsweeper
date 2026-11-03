using System.Globalization;
using BinSweeper.Core.Delete;
using BinSweeper.Core.Policy;
using BinSweeper.Core.RecycleBin;
using BinSweeper.Core.Scan;
using BinSweeper.Core.Staging;

namespace BinSweeper.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintUsage();
                return 0;
            }

            var rest = args.Skip(1).ToArray();

            switch (args[0])
            {
                case "scan":
                    RunScan(ParseOptions("scan", rest));
                    return 0;
                case "stage":
                    RunStage(ParseOptions("stage", rest));
                    return 0;
                case "purge":
                    RunPurge(ParseOptions("purge", rest));
                    return 0;
                case "staging":
                    RunStaging();
                    return 0;
                case "restore":
                    RunRestore(rest);
                    return 0;
                default:
                    Console.Error.WriteLine($"未知命令：{args[0]}");
                    PrintUsage();
                    return 2;
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"执行失败：{ex.Message}");
            return 1;
        }
    }

    private static void RunScan(Options options)
    {
        var volumes = RecycleBinLocator.LocateCurrentUser();
        var policy = options.ToPolicy();

        Console.WriteLine("BinSweeper 扫描报告");
        Console.WriteLine($"时间: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"小文件界值: {FormatBytes(policy.SmallFileLimitBytes)}");
        Console.WriteLine($"清理范围: {DescribeScope(options.ScopeRoots)}");
        WarnMissingScope(options.ScopeRoots);

        if (volumes.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("未找到当前用户可访问的回收站目录。");
            return;
        }

        var report = new RecycleBinScanner(options.ToScanOptions(), policy).Scan(volumes);

        Console.WriteLine();
        var totalEligibleFiles = 0;
        var totalEligibleEntries = 0;
        var totalEligibleBytes = 0L;
        var totalLargeFiles = 0;
        var totalLargeBytes = 0L;

        foreach (var volume in volumes)
        {
            var entries = EntriesOf(report, volume).ToList();
            var eligibleEntries = entries.Where(e => e.IsEligible).ToList();
            var eligibleFiles = eligibleEntries.Sum(e => e.IsDirectory ? e.SmallFileCount : 1);
            var largeFiles = entries.Where(e => e.InScope && e.Kind == EntryKind.LargeFile).ToList();

            totalEligibleFiles += eligibleFiles;
            totalEligibleEntries += eligibleEntries.Count;
            totalEligibleBytes += eligibleEntries.Sum(e => e.SmallBytes);
            totalLargeFiles += largeFiles.Count;
            totalLargeBytes += largeFiles.Sum(e => e.Size);

            Console.WriteLine($"[{volume.RootPath}] {volume.BinDirectory}");
            Console.WriteLine($"  条目总数: {entries.Count}");
            Console.WriteLine($"  可清理小文件: {eligibleFiles}（{eligibleEntries.Count} 个条目，其中目录 {eligibleEntries.Count(e => e.IsDirectory)} 个）");
            Console.WriteLine($"  可清理占用: {FormatBytes(eligibleEntries.Sum(e => e.SmallBytes))}");
            Console.WriteLine($"  大文件: {largeFiles.Count} 个 / {FormatBytes(largeFiles.Sum(e => e.Size))}（不自动处理）");
            Console.WriteLine($"  混合目录: {entries.Count(e => e.Kind == EntryKind.MixedDirectory)} 个（整目录跳过）");
            Console.WriteLine($"  未知条目: {entries.Count(e => e.Kind == EntryKind.Unknown)} 个（不处理）");

            foreach (var group in entries
                .Where(e => e.Kind == EntryKind.Unknown)
                .GroupBy(e => e.Reason ?? "未说明")
                .OrderByDescending(g => g.Count())
                .Take(3))
            {
                Console.WriteLine($"    - {group.Key} ×{group.Count()}");
            }

            Console.WriteLine($"  范围外条目: {entries.Count(e => !e.InScope)} 个");
            Console.WriteLine();
        }

        foreach (var skipped in report.SkippedDirectories)
        {
            Console.WriteLine($"跳过目录: {skipped}");
        }

        if (report.SkippedDirectories.Count > 0)
        {
            Console.WriteLine();
        }

        Console.WriteLine($"合计可清理小文件: {totalEligibleFiles}（条目 {totalEligibleEntries} 个，占用 {FormatBytes(totalEligibleBytes)}）");
        Console.WriteLine($"大文件合计: {totalLargeFiles} 个 / {FormatBytes(totalLargeBytes)}（需手动处理）");
        Console.WriteLine($"水位: {DescribeLevel(report)}");

        if (options.Top > 0)
        {
            Console.WriteLine();
            PrintTopEntries(report, options.Top);
        }
    }

    private static void RunStage(Options options)
    {
        var policy = options.ToPolicy();
        var volumes = RecycleBinLocator.LocateCurrentUser();

        if (volumes.Count == 0)
        {
            Console.WriteLine("未找到当前用户可访问的回收站目录。");
            return;
        }

        WarnMissingScope(options.ScopeRoots);

        var report = new RecycleBinScanner(options.ToScanOptions(), policy).Scan(volumes);
        var target = options.Target ?? policy.TargetCount;
        var store = new StagingStore(StagingStore.DefaultRoot);
        var candidates = report.Entries
            .Where(e => e.IsEligible)
            .OrderByDescending(e => e.IsDirectory ? e.SmallFileCount : 1)
            .ThenByDescending(e => e.SmallBytes)
            .ToList();

        var remaining = report.EligibleFileCount;
        Console.WriteLine($"当前可清理小文件: {remaining}，目标降到 {target}");
        Console.WriteLine($"待清理区: {store.RootDirectory}");
        Console.WriteLine();

        if (!options.Apply)
        {
            var planned = 0;
            var plannedFiles = 0;
            var plannedBytes = 0L;
            const int previewLimit = 20;

            foreach (var entry in candidates)
            {
                if (remaining <= target)
                {
                    break;
                }

                var files = entry.IsDirectory ? entry.SmallFileCount : 1;
                remaining -= files;
                planned++;
                plannedFiles += files;
                plannedBytes += entry.SmallBytes;

                if (planned <= previewLimit)
                {
                    Console.WriteLine($"  [预演] {DescribeEntry(entry)}");
                }
            }

            if (planned > previewLimit)
            {
                Console.WriteLine($"  ……其余 {planned - previewLimit} 个条目略");
            }

            Console.WriteLine();
            Console.WriteLine($"预演结果：将搬运 {planned} 个条目 / {plannedFiles} 个小文件 / {FormatBytes(plannedBytes)}，剩余 {remaining}。");
            Console.WriteLine("未做任何改动。加 --apply 才会真正移入待清理区。");
            return;
        }

        var moved = 0;
        var movedFiles = 0;
        var movedBytes = 0L;
        var failed = 0;

        foreach (var entry in candidates)
        {
            if (remaining <= target)
            {
                break;
            }

            var outcome = store.Stage(entry, DateTimeOffset.Now);
            if (!outcome.Success)
            {
                failed++;
                if (failed <= 10)
                {
                    Console.WriteLine($"  跳过: {entry.OriginalPath}（{outcome.Error}）");
                }

                continue;
            }

            var files = entry.IsDirectory ? entry.SmallFileCount : 1;
            remaining -= files;
            moved++;
            movedFiles += files;
            movedBytes += entry.SmallBytes;
        }

        Console.WriteLine();
        Console.WriteLine($"已移入待清理区: {moved} 个条目 / {movedFiles} 个小文件 / {FormatBytes(movedBytes)}，失败 {failed} 个。");
        Console.WriteLine($"回收站内剩余可清理小文件: {remaining}");
        Console.WriteLine("可用 `binsweeper staging` 查看，或用 `binsweeper restore all` 放回回收站。");
    }

    private static void RunPurge(Options options)
    {
        var policy = options.ToPolicy();
        var volumes = RecycleBinLocator.LocateCurrentUser();

        if (volumes.Count == 0)
        {
            Console.WriteLine("未找到当前用户可访问的回收站目录。");
            return;
        }

        WarnMissingScope(options.ScopeRoots);

        var report = new RecycleBinScanner(options.ToScanOptions(), policy).Scan(volumes);
        var candidates = report.Entries.Where(e => e.IsEligible).ToList();
        var files = candidates.Sum(e => e.IsDirectory ? e.SmallFileCount : 1);
        var bytes = candidates.Sum(e => e.SmallBytes);

        Console.WriteLine($"可清理小文件: {files}（条目 {candidates.Count} 个，占用 {FormatBytes(bytes)}）");
        Console.WriteLine($"保持不动: 大文件 {report.LargeFileCount} 个、混合目录 {report.MixedDirectoryCount} 个、未知 {report.UnknownCount} 个");

        if (!options.Apply)
        {
            Console.WriteLine();
            Console.WriteLine("预演结束，未做任何改动。加 --apply 才会永久删除，删除后无法恢复。");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("正在永久删除…");

        var outcome = RecycleBinPurger.Purge(candidates);

        Console.WriteLine($"已永久删除: {outcome.DeletedEntries} 个条目 / {outcome.DeletedFiles} 个小文件 / {FormatBytes(outcome.DeletedBytes)}，失败 {outcome.Failed} 个。");

        foreach (var failure in outcome.Failures)
        {
            Console.WriteLine($"  失败: {failure}");
        }
    }

    private static void RunStaging()
    {
        var policy = new PolicySettings();
        var store = new StagingStore(StagingStore.DefaultRoot);
        var items = store.List();

        Console.WriteLine($"待清理区: {store.RootDirectory}");

        if (items.Count == 0)
        {
            Console.WriteLine("当前没有待清理条目。");
            return;
        }

        var totalBytes = items.Sum(i => i.Size);
        Console.WriteLine($"共 {items.Count} 项 / {FormatBytes(totalBytes)}");
        Console.WriteLine();

        var now = DateTimeOffset.Now;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var expiry = item.StagedAt + policy.Retention;
            var left = expiry - now;
            var state = item.Error is not null
                ? $"异常: {item.Error}"
                : left > TimeSpan.Zero
                    ? $"剩余 {DescribeDuration(left)}"
                    : "已到期";

            Console.WriteLine($"  {i + 1}. {item.Id}  {FormatBytes(item.Size)}  {item.OriginalPath}");
            Console.WriteLine($"     {state}（到期 {expiry:yyyy-MM-dd HH:mm}）");
        }
    }

    private static void RunRestore(string[] args)
    {
        if (args.Length != 1)
        {
            throw new ArgumentException("用法：binsweeper restore <id|all>");
        }

        var store = new StagingStore(StagingStore.DefaultRoot);
        var items = store.List();
        var targets = args[0] == "all"
            ? items
            : items.Where(i => i.Id.Equals(args[0], StringComparison.OrdinalIgnoreCase)).ToList();

        if (targets.Count == 0)
        {
            Console.WriteLine(args[0] == "all" ? "待清理区为空。" : $"未找到条目：{args[0]}");
            return;
        }

        var restored = 0;
        foreach (var item in targets)
        {
            var error = store.Restore(item);
            if (error is null)
            {
                restored++;
                Console.WriteLine($"已放回回收站: {item.OriginalPath}");
            }
            else
            {
                Console.WriteLine($"放回失败: {item.OriginalPath}（{error}）");
            }
        }

        Console.WriteLine($"完成：{restored}/{targets.Count} 项已放回回收站。");
    }

    private static IEnumerable<ScannedEntry> EntriesOf(ScanReport report, RecycleBinVolume volume)
        => report.Entries.Where(e => e.MetaPath.StartsWith(volume.BinDirectory, StringComparison.OrdinalIgnoreCase));

    private static string DescribeEntry(ScannedEntry entry)
    {
        var kind = entry.IsDirectory ? $"目录 {entry.SmallFileCount} 个文件" : "文件";
        return $"[{kind}] {FormatBytes(entry.SmallBytes)} {entry.OriginalPath}";
    }

    private static void PrintTopEntries(ScanReport report, int top)
    {
        var candidates = report.Entries
            .Where(e => e.IsEligible)
            .OrderByDescending(e => e.SmallFileCount)
            .ThenByDescending(e => e.SmallBytes)
            .Take(top)
            .ToList();

        if (candidates.Count == 0)
        {
            return;
        }

        Console.WriteLine($"可清理条目（前 {candidates.Count} 项）:");
        for (var i = 0; i < candidates.Count; i++)
        {
            Console.WriteLine($"  {i + 1}. {DescribeEntry(candidates[i])}");
        }
    }

    private static string DescribeLevel(ScanReport report)
    {
        var policy = report.Policy;
        var count = report.EligibleFileCount;

        return report.Level switch
        {
            WaterLevel.AboveEmergency =>
                $"紧急（≥ {policy.EmergencyCount}），应立即降到 {policy.TargetCount}",
            WaterLevel.AboveTrigger =>
                $"已达触发线（≥ {policy.TriggerCount}），可开始搬运至 {policy.TargetCount}",
            _ => $"正常（触发线 {policy.TriggerCount}，还差 {policy.TriggerCount - count}）",
        };
    }

    private static string DescribeDuration(TimeSpan span)
        => span.Days > 0 ? $"{span.Days} 天 {span.Hours} 小时" : $"{span.Hours} 小时 {span.Minutes} 分钟";

    private static string DescribeScope(IReadOnlyList<string> scopeRoots)
        => scopeRoots.Count == 0 ? "全部回收站" : string.Join("；", scopeRoots);

    private static void WarnMissingScope(IReadOnlyList<string> scopeRoots)
    {
        foreach (var scopeRoot in scopeRoots.Where(r => !Directory.Exists(r)))
        {
            Console.WriteLine($"警告: 范围目录不存在，相关条目不会被判定为可清理 -> {scopeRoot}");
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var units = new[] { "KB", "MB", "GB", "TB" };
        var value = bytes / 1024.0;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unitIndex]}";
    }

    private static Options ParseOptions(string command, string[] args)
    {
        var options = new Options();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scope" when command is "scan" or "stage" or "purge":
                    options.ScopeRoots.Add(RequireValue(args, ref i, "--scope"));
                    break;
                case "--small-limit" when command is "scan" or "stage" or "purge":
                    options.SmallLimit = ParseSize(RequireValue(args, ref i, "--small-limit"));
                    break;
                case "--top" when command is "scan":
                    options.Top = ParseInt(RequireValue(args, ref i, "--top"), "--top");
                    break;
                case "--target" when command is "stage":
                    options.Target = ParseInt(RequireValue(args, ref i, "--target"), "--target");
                    break;
                case "--apply" when command is "stage" or "purge":
                    options.Apply = true;
                    break;
                default:
                    throw new ArgumentException($"未知参数：{args[i]}");
            }
        }

        if (options.SmallLimit <= 0)
        {
            throw new ArgumentException("--small-limit 必须大于 0");
        }

        if (options.Target is < 0)
        {
            throw new ArgumentException("--target 不能为负数");
        }

        return options;
    }

    private static int ParseInt(string text, string option)
    {
        try
        {
            return int.Parse(text, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            throw new ArgumentException($"{option} 需要一个整数：{text}");
        }
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{option} 需要一个参数");
        }

        return args[++index];
    }

    private static long ParseSize(string text)
    {
        var value = text.Trim();
        var multiplier = 1L;
        var upper = value.ToUpperInvariant();

        if (upper.EndsWith("KB", StringComparison.Ordinal))
        {
            multiplier = 1L << 10;
            value = value[..^2];
        }
        else if (upper.EndsWith("MB", StringComparison.Ordinal))
        {
            multiplier = 1L << 20;
            value = value[..^2];
        }
        else if (upper.EndsWith("GB", StringComparison.Ordinal))
        {
            multiplier = 1L << 30;
            value = value[..^2];
        }
        else if (upper.EndsWith("B", StringComparison.Ordinal))
        {
            value = value[..^1];
        }

        try
        {
            return (long)(double.Parse(value.Trim(), CultureInfo.InvariantCulture) * multiplier);
        }
        catch (FormatException)
        {
            throw new ArgumentException($"无法识别的容量：{text}（示例：64KB、1MB、1048576）");
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            BinSweeper - 回收站小文件清理工具（Stage A：移入待清理区）

            用法:
              binsweeper scan    [--scope <目录>] [--small-limit <值>] [--top <数量>]
              binsweeper stage   [--scope <目录>] [--small-limit <值>] [--target <数量>] [--apply]
              binsweeper purge   [--scope <目录>] [--small-limit <值>] [--apply]
              binsweeper staging
              binsweeper restore <id|all>

            选项:
              --scope <目录>      只看原路径位于该目录下的条目，可重复指定；缺省为全部回收站
              --small-limit <值>  小文件界值，支持 KB/MB/GB，默认 1MB
              --top <数量>        scan 时列出前 N 个可清理条目，默认 10，填 0 不列出
              --target <数量>     stage 时降到该数量为止，默认 2000
              --apply             stage/purge 时真正执行；不加只做预演
              -h, --help          显示帮助

            说明:
              stage 只做同卷 rename，把 $I/$R 移出回收站到待清理区，内容不删除，
              默认 7 天内可用 restore 放回回收站。
              purge 跳过待清理区，永久删除可清理小文件，无法恢复；大文件不动。
            """);
    }

    private sealed class Options
    {
        public List<string> ScopeRoots { get; } = new();

        public long SmallLimit { get; set; } = 1L << 20;

        public int Top { get; set; } = 10;

        public bool Apply { get; set; }

        public int? Target { get; set; }

        public PolicySettings ToPolicy() => new() { SmallFileLimitBytes = SmallLimit };

        public ScanOptions ToScanOptions() => new() { ScopeRoots = ScopeRoots };
    }
}
