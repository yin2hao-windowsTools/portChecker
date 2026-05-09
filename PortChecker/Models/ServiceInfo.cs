namespace PortChecker.Models;

public sealed record ServiceInfo(
    string Name,
    string DisplayName,
    string State,
    string StartMode,
    string PathName);
