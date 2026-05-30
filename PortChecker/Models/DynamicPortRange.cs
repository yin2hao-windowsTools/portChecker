using System;

namespace PortChecker.Models;

public sealed class DynamicPortRange
{
    public required string AddressFamily { get; init; }

    public required PortProtocol Protocol { get; init; }

    public required int StartPort { get; init; }

    public required int PortCount { get; init; }

    public required string Store { get; init; }

    public int EndPort => StartPort + PortCount - 1;

    public string AddressFamilyText => AddressFamily.Equals("ipv6", StringComparison.OrdinalIgnoreCase)
        ? "IPv6"
        : "IPv4";

    public string ProtocolText => Protocol.ToString().ToUpperInvariant();

    public string RangeText => StartPort == EndPort
        ? StartPort.ToString()
        : $"{StartPort}-{EndPort}";

    public string StoreText => Store.Equals("persistent", StringComparison.OrdinalIgnoreCase)
        ? "持久"
        : "当前";

    public string TargetText => $"{AddressFamilyText} {ProtocolText} {RangeText}（{StoreText}）";
}
