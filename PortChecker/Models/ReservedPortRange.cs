using System;

namespace PortChecker.Models;

public sealed class ReservedPortRange
{
    public required PortProtocol Protocol { get; init; }

    public required int StartPort { get; init; }

    public required int EndPort { get; init; }

    public required string Store { get; init; }

    public bool IsAdministered { get; init; }

    public int PortCount => EndPort - StartPort + 1;

    public string ProtocolText => Protocol.ToString().ToUpperInvariant();

    public string RangeText => StartPort == EndPort
        ? StartPort.ToString()
        : $"{StartPort}-{EndPort}";

    public string StoreText => Store.Equals("persistent", StringComparison.OrdinalIgnoreCase)
        ? "持久"
        : "当前";

    public string SourceText => IsAdministered ? "用户保留" : "系统保留";

    public string DeleteTargetText => $"{ProtocolText} {RangeText}（{StoreText}）";
}
