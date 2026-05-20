using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
            var scannedAt = DateTimeOffset.Now;
            var isElevated = PrivilegeService.IsRunningAsAdministrator();
            var scanTotalStopwatch = Stopwatch.StartNew();
            TimeSpan snapshotDuration = TimeSpan.Zero;
            TimeSpan metadataDuration = TimeSpan.Zero;
            TimeSpan entryProjectionDuration = TimeSpan.Zero;
            var snapshotCount = 0;
            var distinctProcessCount = 0;

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var snapshotStopwatch = Stopwatch.StartNew();
                var activePorts = _nativePortScanner.GetActivePorts();
                var snapshots = activePorts as List<PortSnapshot> ?? activePorts.ToList();
                snapshots.Sort(CompareSnapshots);
                snapshotStopwatch.Stop();

                snapshotCount = snapshots.Count;
                snapshotDuration = snapshotStopwatch.Elapsed;

                cancellationToken.ThrowIfCancellationRequested();

                var processIds = snapshots
                    .Select(snapshot => snapshot.ProcessId)
                    .Where(processId => processId > 0)
                    .Distinct()
                    .ToArray();

                distinctProcessCount = processIds.Length;

                var metadataStopwatch = Stopwatch.StartNew();
                var metadataWarning = string.Empty;
                IReadOnlyDictionary<int, ProcessMetadata> metadata;
                try
                {
                    metadata = _processMetadataProvider.GetMetadata(processIds);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    metadata = new Dictionary<int, ProcessMetadata>();
                    metadataWarning = $"进程详情读取失败（{exception.GetType().Name}）：{exception.Message}";
                }

                metadataStopwatch.Stop();
                metadataDuration = metadataStopwatch.Elapsed;

                var entryProjectionStopwatch = Stopwatch.StartNew();
                var entries = snapshots.Select(snapshot => BuildEntry(snapshot, metadata)).ToList();
                entryProjectionStopwatch.Stop();
                entryProjectionDuration = entryProjectionStopwatch.Elapsed;

                scanTotalStopwatch.Stop();
                var metrics = new ScanMetrics(
                    snapshotDuration,
                    metadataDuration,
                    entryProjectionDuration,
                    scanTotalStopwatch.Elapsed,
                    snapshotCount,
                    distinctProcessCount);

                return new PortScanResult(
                    entries,
                    scannedAt,
                    isElevated,
                    string.IsNullOrWhiteSpace(metadataWarning) ? null : metadataWarning,
                    GetPermissionNotice(isElevated),
                    metrics);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                scanTotalStopwatch.Stop();
                var metrics = new ScanMetrics(
                    snapshotDuration,
                    metadataDuration,
                    entryProjectionDuration,
                    scanTotalStopwatch.Elapsed,
                    snapshotCount,
                    distinctProcessCount);

                return new PortScanResult(
                    Array.Empty<PortEntry>(),
                    scannedAt,
                    isElevated,
                    $"扫描失败（{exception.GetType().Name}）：{exception.Message}",
                    GetPermissionNotice(isElevated),
                    metrics);
            }
        }, cancellationToken);
    }

    private static int CompareSnapshots(PortSnapshot left, PortSnapshot right)
    {
        var protocolComparison = left.Protocol.CompareTo(right.Protocol);
        if (protocolComparison != 0)
        {
            return protocolComparison;
        }

        var localPortComparison = left.LocalPort.CompareTo(right.LocalPort);
        if (localPortComparison != 0)
        {
            return localPortComparison;
        }

        return StringComparer.Ordinal.Compare(left.LocalAddress, right.LocalAddress);
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

    private static string GetPermissionNotice(bool isElevated)
    {
        return isElevated
            ? "管理员权限：可读取更多进程路径、命令行和服务详情。"
            : "普通权限：端口和 PID 可正常查看；部分系统进程的路径、命令行、服务详情可能受限，高风险操作会按需请求管理员权限。";
    }
}
