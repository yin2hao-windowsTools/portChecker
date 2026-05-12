using System;

namespace PortChecker.Models;

public sealed record ScanMetrics(
    TimeSpan PortSnapshotDuration,
    TimeSpan MetadataDuration,
    TimeSpan EntryProjectionDuration,
    TimeSpan TotalDuration,
    int SnapshotCount,
    int DistinctProcessCount);
