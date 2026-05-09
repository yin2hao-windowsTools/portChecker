using System.Collections.Generic;
using PortChecker.Models;

namespace PortChecker.Services;

internal sealed record ProcessMetadata
{
    public string ProcessName { get; init; } = "Unknown";

    public string? ProcessPath { get; init; }

    public string? CommandLine { get; init; }

    public string? UserName { get; init; }

    public IReadOnlyList<ServiceInfo> Services { get; init; } = [];
}
