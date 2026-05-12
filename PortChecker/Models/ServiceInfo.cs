using System;

namespace PortChecker.Models;

public sealed record ServiceInfo(
    string Name,
    string DisplayName,
    string State,
    string StartMode,
    string PathName)
{
    public bool CanControl => !string.IsNullOrWhiteSpace(Name) && Name != "-";

    public bool IsRunning => State.Equals("Running", StringComparison.OrdinalIgnoreCase);

    public string DisplayLabel => string.Equals(DisplayName, Name, StringComparison.OrdinalIgnoreCase)
        ? Name
        : $"{DisplayName} ({Name})";
}
