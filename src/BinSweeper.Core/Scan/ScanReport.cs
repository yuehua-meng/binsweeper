namespace BinSweeper.Core.Scan;

public enum WaterLevel
{
    Normal,
    AboveTrigger,
    AboveEmergency,
}

public sealed class ScanReport
{
    public ScanReport(
        IReadOnlyList<ScannedEntry> entries,
        IReadOnlyList<string> skippedDirectories,
        Policy.PolicySettings policy,
        DateTimeOffset scannedAt)
    {
        Entries = entries;
        SkippedDirectories = skippedDirectories;
        Policy = policy;
        ScannedAt = scannedAt;
    }

    public IReadOnlyList<ScannedEntry> Entries { get; }

    public IReadOnlyList<string> SkippedDirectories { get; }

    public Policy.PolicySettings Policy { get; }

    public DateTimeOffset ScannedAt { get; }

    public int TotalEntries => Entries.Count;

    public int OutOfScopeCount => Entries.Count(e => !e.InScope);

    /// <summary>可清理条目数（文件 + 目录）。</summary>
    public int EligibleEntryCount => Entries.Count(e => e.IsEligible);

    /// <summary>可清理的小文件总数，也是水位判断依据。</summary>
    public int EligibleFileCount
        => Entries.Where(e => e.IsEligible).Sum(e => e.IsDirectory ? e.SmallFileCount : 1);

    public long EligibleBytes => Entries.Where(e => e.IsEligible).Sum(e => e.SmallBytes);

    public int LargeFileCount => Entries.Count(e => e.InScope && e.Kind == EntryKind.LargeFile);

    public long LargeBytes => Entries.Where(e => e.InScope && e.Kind == EntryKind.LargeFile).Sum(e => e.Size);

    public int MixedDirectoryCount => Entries.Count(e => e.Kind == EntryKind.MixedDirectory);

    public int UnknownCount => Entries.Count(e => e.Kind == EntryKind.Unknown);

    public WaterLevel Level
    {
        get
        {
            if (EligibleFileCount >= Policy.EmergencyCount)
            {
                return WaterLevel.AboveEmergency;
            }

            return EligibleFileCount >= Policy.TriggerCount ? WaterLevel.AboveTrigger : WaterLevel.Normal;
        }
    }
}
