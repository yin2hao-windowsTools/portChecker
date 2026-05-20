using System;
using System.Collections.Generic;
using System.Linq;

namespace PortChecker.Models;

public sealed class PortEntry
{
    private string? _searchIndex;

    public required PortProtocol Protocol { get; init; }

    public required string LocalAddress { get; init; }

    public required int LocalPort { get; init; }

    public string RemoteAddress { get; init; } = "-";

    public int? RemotePort { get; init; }

    public string State { get; init; } = "-";

    public required int ProcessId { get; init; }

    public string ProcessName { get; init; } = "Unknown";

    public string? ProcessPath { get; init; }

    public string? CommandLine { get; init; }

    public string? UserName { get; init; }

    public bool IsSvchost { get; init; }

    public IReadOnlyList<ServiceInfo> Services { get; init; } = Array.Empty<ServiceInfo>();

    public string ProtocolText => Protocol.ToString().ToUpperInvariant();

    public string LocalEndpoint => FormatEndpoint(LocalAddress, LocalPort);

    public string RemoteEndpoint => RemotePort is int port ? FormatEndpoint(RemoteAddress, port) : RemoteAddress;

    public string ServiceSummary => Services.Count == 0
        ? (IsSvchost ? "未解析到服务" : "-")
        : string.Join(", ", Services.Select(service => service.Name));

    public string PathSummary => string.IsNullOrWhiteSpace(ProcessPath) ? "无路径或权限不足" : ProcessPath;

    public string CommandSummary => string.IsNullOrWhiteSpace(CommandLine) ? "无命令行或权限不足" : CommandLine;

    public string SearchIndex => _searchIndex ??= BuildSearchIndex();

    private string BuildSearchIndex()
    {
        var serviceIndex = Services.Count == 0
            ? string.Empty
            : string.Join(' ', Services.Select(service => $"{service.Name} {service.DisplayName}"));

        return string.Join(' ', new[]
        {
            ProtocolText,
            LocalEndpoint,
            RemoteEndpoint,
            State,
            ProcessId.ToString(),
            ProcessName,
            ProcessPath,
            CommandLine,
            UserName,
            serviceIndex
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string FormatEndpoint(string address, int port)
    {
        return address.Contains(":", StringComparison.Ordinal)
            && !address.StartsWith("[", StringComparison.Ordinal)
            ? $"[{address}]:{port}"
            : $"{address}:{port}";
    }
}
