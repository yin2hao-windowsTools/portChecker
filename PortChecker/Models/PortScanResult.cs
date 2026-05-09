using System;
using System.Collections.Generic;

namespace PortChecker.Models;

public sealed record PortScanResult(
    IReadOnlyList<PortEntry> Entries,
    DateTimeOffset ScannedAt,
    bool IsElevated,
    string? Warning);
