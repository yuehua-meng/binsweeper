namespace BinSweeper.Core.Policy;

/// <summary>
/// 数量水位与节奏策略。只按“小文件数量”判断，不看条目年龄。
/// </summary>
public sealed class PolicySettings
{
    /// <summary>小文件界值，超过该值视为大文件，不自动处理。</summary>
    public long SmallFileLimitBytes { get; init; } = 1L << 20;

    /// <summary>可清理小文件数达到该值时开始搬运。</summary>
    public int TriggerCount { get; init; } = 5_000;

    /// <summary>搬运进行到该值以下时停止。</summary>
    public int TargetCount { get; init; } = 2_000;

    /// <summary>达到该值时忽略间隔，立即降到 TargetCount。</summary>
    public int EmergencyCount { get; init; } = 20_000;

    /// <summary>两次搬运之间的最小间隔。</summary>
    public TimeSpan MinimumInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>条目进入待清理区后保留的可恢复时长。</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);
}
