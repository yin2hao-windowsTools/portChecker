using System.Collections.Generic;

namespace PortChecker.Services;

internal sealed record NativePortScanResult(
    IReadOnlyList<PortSnapshot> Ports,
    string? Warning);
