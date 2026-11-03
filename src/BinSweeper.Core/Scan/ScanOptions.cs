namespace BinSweeper.Core.Scan;

public sealed class ScanOptions
{
    /// <summary>清理范围（原始路径前缀）。为空表示不限制范围，覆盖整个回收站。</summary>
    public IReadOnlyList<string> ScopeRoots { get; init; } = Array.Empty<string>();

    /// <summary>单个目录最多扫描的内部条目数，超过即视为“情况不明”并跳过。</summary>
    public int MaxEntriesPerDirectory { get; init; } = 50_000;
}
