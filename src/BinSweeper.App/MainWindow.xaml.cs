using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using BinSweeper.Core.Delete;
using BinSweeper.Core.Policy;
using BinSweeper.Core.Scan;
using BinSweeper.Core.Staging;

namespace BinSweeper.App;

public partial class MainWindow : Window
{
    private readonly SweepWorker _worker = new(new PolicySettings());
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(5) };

    private bool _initializing = true;
    private bool _allowClose;
    private bool _busy;
    private bool _purging;
    private ScanReport? _lastReport;
    private int _lastStagedCount;

    public MainWindow()
    {
        InitializeComponent();

        AutoCleanCheckBox.IsChecked = SettingsStore.AutoClean;
        AutoStartCheckBox.IsChecked = StartupRegistration.IsEnabled();
        _initializing = false;

        Loaded += async (_, _) => await RefreshAsync();
        _timer.Tick += async (_, _) => await AutoTickAsync();
        _timer.Start();
        Closing += OnClosing;
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void AllowClose() => _allowClose = true;

    public void TriggerScan() => _ = RefreshAsync();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        // 关闭按钮只收进托盘，后台继续监测。
        e.Cancel = true;
        Hide();
    }

    private async void OnScanClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnStageClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var report = _lastReport ?? await Task.Run(() => _worker.Scan());
        var target = _worker.Policy.TargetCount;

        if (report.EligibleFileCount <= target)
        {
            System.Windows.MessageBox.Show(
                this,
                $"当前只有 {report.EligibleFileCount:N0} 个可清理小文件，已低于目标 {target:N0}，无需清理。",
                "无需清理",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            this,
            $"将把约 {report.EligibleFileCount - target:N0} 个小文件移入待清理区：\n{StagingStore.DefaultRoot}\n\n"
            + "文件不会被删除，默认 7 天内可以点「恢复全部」放回回收站。继续？",
            "确认清理",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        SetBusy(true, "正在搬移…");
        StageSummary summary;

        try
        {
            summary = await Task.Run(() => _worker.Stage(target));
        }
        catch (Exception ex)
        {
            SetBusy(false, $"清理失败：{ex.Message}");
            return;
        }

        SetBusy(false, null);
        await RefreshAsync();
        StatusText.Text = $"已搬移 {summary.MovedEntries:N0} 个条目 / {summary.MovedFiles:N0} 个小文件 / "
            + $"{FormatBytes(summary.MovedBytes)}；失败 {summary.Failed} 个；回收站剩余 {summary.Remaining:N0}。"
            + (summary.Failures.Count > 0 ? $"\n首个失败：{summary.Failures[0]}" : string.Empty);
    }

    private async void OnPurgeClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var report = _lastReport ?? await Task.Run(() => _worker.Scan());

        if (report.EligibleFileCount == 0)
        {
            System.Windows.MessageBox.Show(
                this,
                "当前没有可清理的小文件。",
                "无需删除",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            this,
            $"将永久删除 {report.EligibleFileCount:N0} 个小文件（{FormatBytes(report.EligibleBytes)}），"
            + $"共 {report.EligibleEntryCount:N0} 个条目。\n\n"
            + "删除后无法恢复，也不会进入待清理区。\n"
            + $"大文件 {report.LargeFileCount:N0} 个、混合目录 {report.MixedDirectoryCount:N0} 个、未知 {report.UnknownCount:N0} 个保持不动。\n\n"
            + "确定继续？",
            "永久删除确认",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        SetBusy(true, "正在永久删除…");
        PurgeSummary summary;

        var progress = new Progress<PurgeProgress>(p =>
        {
            if (!_purging)
            {
                return;
            }

            // 删除过程中实时减少主卡片的数字。
            EligibleText.Text = p.RemainingFiles.ToString("N0");
            EligibleDetailText.Text = $"{p.DeletedEntries:N0} 个条目已删 · {FormatBytes(p.RemainingBytes)} 剩余 · 删除中…";
            StatusText.Text = $"正在永久删除…已删 {p.DeletedEntries:N0} 个条目，剩余 {p.RemainingFiles:N0} 个小文件"
                + (p.FailedEntries > 0 ? $"，失败 {p.FailedEntries:N0}" : string.Empty);
        });

        _purging = true;

        try
        {
            summary = await Task.Run(() => _worker.Purge(progress));
        }
        catch (Exception ex)
        {
            _purging = false;
            SetBusy(false, $"删除失败：{ex.Message}");
            return;
        }

        _purging = false;
        SetBusy(false, null);
        await RefreshAsync();
        StatusText.Text = $"已永久删除 {summary.DeletedEntries:N0} 个条目 / {summary.DeletedFiles:N0} 个小文件 / "
            + $"{FormatBytes(summary.DeletedBytes)}；失败 {summary.Failed} 个；回收站剩余 {_lastReport?.EligibleFileCount ?? 0:N0}。"
            + (summary.Failures.Count > 0 ? $"\n首个失败：{summary.Failures[0]}" : string.Empty);
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var items = await Task.Run(() => _worker.ListStaged());
        if (items.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "待清理区为空。", "无需恢复", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            this,
            $"将把 {items.Count:N0} 项从待清理区放回回收站（之后仍可在资源管理器里还原到原位置）。继续？",
            "确认恢复",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        SetBusy(true, "正在恢复…");
        RestoreSummary summary;

        try
        {
            summary = await Task.Run(() => _worker.RestoreAll());
        }
        catch (Exception ex)
        {
            SetBusy(false, $"恢复失败：{ex.Message}");
            return;
        }

        SetBusy(false, null);
        await RefreshAsync();
        StatusText.Text = $"已放回回收站 {summary.Restored:N0} 项，失败 {summary.Failed:N0} 项。";
    }

    private async void OnPurgeStagingClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var items = await Task.Run(() => _worker.ListStaged());
        if (items.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "待清理区已经是空的。", "无需删除", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var bytes = items.Where(i => i.Error is null).Sum(i => i.Size);
        var confirm = System.Windows.MessageBox.Show(
            this,
            $"将永久删除待清理区里的 {items.Count:N0} 项（{FormatBytes(bytes)}），删除后无法恢复。\n\n确定继续？",
            "清空待清理区确认",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        SetBusy(true, "正在清空待清理区…");
        ClearStagedSummary summary;

        try
        {
            summary = await Task.Run(() => _worker.ClearStaged());
        }
        catch (Exception ex)
        {
            SetBusy(false, $"清空失败：{ex.Message}");
            return;
        }

        SetBusy(false, null);
        await RefreshAsync();
        StatusText.Text = $"待清理区已清空：删除 {summary.Deleted:N0} 项 / {FormatBytes(summary.DeletedBytes)}；失败 {summary.Failed:N0} 项。"
            + (summary.Failures.Count > 0 ? $"\n首个失败：{summary.Failures[0]}" : string.Empty);
    }

    private void OnOpenStagingClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var root = StagingStore.DefaultRoot;
            Directory.CreateDirectory(root);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{root}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开待清理区失败：{ex.Message}";
        }
    }

    private void OnAutoCleanChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        SettingsStore.AutoClean = AutoCleanCheckBox.IsChecked == true;
        StatusText.Text = SettingsStore.AutoClean ? "已开启后台自动清理。" : "已关闭后台自动清理。";
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        var enabled = AutoStartCheckBox.IsChecked == true;
        if (!StartupRegistration.SetEnabled(enabled))
        {
            _initializing = true;
            AutoStartCheckBox.IsChecked = StartupRegistration.IsEnabled();
            _initializing = false;
            StatusText.Text = "开机自启需要在发布后的 exe 上设置；当前是开发态运行。";
            return;
        }

        StatusText.Text = enabled ? "已设置开机自启。" : "已取消开机自启。";
    }

    private async Task AutoTickAsync()
    {
        if (_busy)
        {
            return;
        }

        await RefreshAsync();

        if (AutoCleanCheckBox.IsChecked != true || _lastReport is null)
        {
            return;
        }

        if (_lastReport.Level == WaterLevel.Normal)
        {
            return;
        }

        SetBusy(true, "达到水位，正在自动搬移…");
        StageSummary summary;

        try
        {
            summary = await Task.Run(() => _worker.Stage());
        }
        catch (Exception ex)
        {
            SetBusy(false, $"自动清理失败：{ex.Message}");
            return;
        }

        SetBusy(false, null);
        await RefreshAsync();
        StatusText.Text = $"自动清理已完成：搬移 {summary.MovedEntries:N0} 个条目 / "
            + $"{summary.MovedFiles:N0} 个小文件，回收站剩余 {summary.Remaining:N0}。";
    }

    private async Task RefreshAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, "正在扫描回收站…");
        EligibleText.Text = "正在扫描…";
        EligibleDetailText.Text = "正在读取回收站条目，请稍候…";

        try
        {
            var report = await Task.Run(() => _worker.Scan());
            var stagedCount = await Task.Run(() => _worker.ListStaged().Count);
            _lastReport = report;
            _lastStagedCount = stagedCount;
            Render(report, stagedCount);
            StatusText.Text = $"{DescribeLevel(report)} · {DateTime.Now:HH:mm:ss} 扫描完成";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"扫描失败：{ex.Message}";

            if (_lastReport is null)
            {
                EligibleText.Text = "—";
                EligibleDetailText.Text = "扫描失败，请点「扫描」重试";
            }
            else
            {
                Render(_lastReport, _lastStagedCount);
            }
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void Render(ScanReport report, int stagedCount)
    {
        var policy = report.Policy;

        EligibleText.Text = report.EligibleFileCount.ToString("N0");
        EligibleDetailText.Text =
            $"{report.EligibleEntryCount:N0} 个条目 · {FormatBytes(report.EligibleBytes)} · "
            + $"触发 {policy.TriggerCount:N0} / 停止 {policy.TargetCount:N0}";
        LargeText.Text = $"{report.LargeFileCount:N0} 个 · {FormatBytes(report.LargeBytes)}";
        StagingText.Text = $"{stagedCount:N0} 项 · {policy.Retention.TotalDays:0} 天可恢复";
    }

    private static string DescribeLevel(ScanReport report)
    {
        var policy = report.Policy;

        return report.Level switch
        {
            WaterLevel.AboveEmergency => $"水位：紧急（≥{policy.EmergencyCount:N0}）",
            WaterLevel.AboveTrigger => $"水位：已达触发线（≥{policy.TriggerCount:N0}）",
            _ => $"水位：正常（还差 {Math.Max(0, policy.TriggerCount - report.EligibleFileCount):N0} 到触发线）",
        };
    }

    private void SetBusy(bool busy, string? message)
    {
        _busy = busy;
        ScanButton.IsEnabled = !busy;
        StageButton.IsEnabled = !busy;
        PurgeButton.IsEnabled = !busy;
        PurgeStagingButton.IsEnabled = !busy;
        RestoreButton.IsEnabled = !busy;

        if (message is not null)
        {
            StatusText.Text = message;
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
        var index = 0;

        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {units[index]}";
    }
}
