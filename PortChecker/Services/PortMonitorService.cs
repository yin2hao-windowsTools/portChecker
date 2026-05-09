using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using PortChecker.Models;

namespace PortChecker.Services;

internal sealed class PortMonitorService
{
    private readonly NativePortScanner _nativePortScanner = new();
    private readonly ProcessMetadataProvider _processMetadataProvider = new();

    public Task<PortScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var snapshots = _nativePortScanner.GetActivePorts()
                    .OrderBy(snapshot => snapshot.Protocol)
                    .ThenBy(snapshot => snapshot.LocalPort)
                    .ThenBy(snapshot => snapshot.LocalAddress)
                    .ToList();

                cancellationToken.ThrowIfCancellationRequested();

                var metadata = _processMetadataProvider.GetMetadata(snapshots.Select(snapshot => snapshot.ProcessId));
                var entries = snapshots.Select(snapshot => BuildEntry(snapshot, metadata)).ToList();

                return new PortScanResult(
                    entries,
                    DateTimeOffset.Now,
                    IsRunningAsAdministrator(),
                    null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new PortScanResult(
                    Array.Empty<PortEntry>(),
                    DateTimeOffset.Now,
                    IsRunningAsAdministrator(),
                    exception.Message);
            }
        }, cancellationToken);
    }

    private static PortEntry BuildEntry(PortSnapshot snapshot, IReadOnlyDictionary<int, ProcessMetadata> metadata)
    {
        metadata.TryGetValue(snapshot.ProcessId, out var process);
        process ??= new ProcessMetadata();

        var processName = NormalizeProcessName(process.ProcessName);
        return new PortEntry
        {
            Protocol = snapshot.Protocol,
            LocalAddress = snapshot.LocalAddress,
            LocalPort = snapshot.LocalPort,
            RemoteAddress = snapshot.RemoteAddress,
            RemotePort = snapshot.RemotePort,
            State = snapshot.State,
            ProcessId = snapshot.ProcessId,
            ProcessName = processName,
            ProcessPath = process.ProcessPath,
            CommandLine = process.CommandLine,
            UserName = process.UserName,
            IsSvchost = processName.Equals("svchost", StringComparison.OrdinalIgnoreCase)
                || processName.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase),
            Services = process.Services
        };
    }

    private static string NormalizeProcessName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return "Unknown";
        }

        return processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
