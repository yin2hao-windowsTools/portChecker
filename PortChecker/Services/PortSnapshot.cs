using PortChecker.Models;

namespace PortChecker.Services;

internal sealed record PortSnapshot(
    PortProtocol Protocol,
    string LocalAddress,
    int LocalPort,
    string RemoteAddress,
    int? RemotePort,
    string State,
    int ProcessId);
