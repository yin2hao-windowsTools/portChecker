using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using PortChecker.Models;

namespace PortChecker.Services;

internal sealed class ProcessMetadataProvider
{
    public Dictionary<int, ProcessMetadata> GetMetadata(IEnumerable<int> processIds)
    {
        var requestedIds = processIds.Where(id => id > 0).Distinct().ToHashSet();
        var processMetadata = new Dictionary<int, ProcessMetadata>();

        AddRuntimeProcessData(requestedIds, processMetadata);
        AddWmiProcessData(requestedIds, processMetadata);
        AddServiceData(requestedIds, processMetadata);

        return processMetadata;
    }

    private static void AddRuntimeProcessData(IReadOnlySet<int> processIds, IDictionary<int, ProcessMetadata> metadata)
    {
        foreach (var processId in processIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                var existing = GetOrCreate(metadata, processId);
                metadata[processId] = existing with
                {
                    ProcessName = string.IsNullOrWhiteSpace(process.ProcessName) ? existing.ProcessName : process.ProcessName,
                    ProcessPath = TryGetProcessPath(process) ?? existing.ProcessPath
                };
            }
            catch
            {
                metadata[processId] = GetOrCreate(metadata, processId);
            }
        }
    }

    private static void AddWmiProcessData(IReadOnlySet<int> processIds, IDictionary<int, ProcessMetadata> metadata)
    {
        if (processIds.Count == 0)
        {
            return;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId,Name,ExecutablePath,CommandLine FROM Win32_Process");

            foreach (ManagementObject process in searcher.Get())
            {
                using (process)
                {
                    var processId = Convert.ToInt32(process["ProcessId"]);
                    if (!processIds.Contains(processId))
                    {
                        continue;
                    }

                    var existing = GetOrCreate(metadata, processId);
                    metadata[processId] = existing with
                    {
                        ProcessName = ReadString(process["Name"]) ?? existing.ProcessName,
                        ProcessPath = ReadString(process["ExecutablePath"]) ?? existing.ProcessPath,
                        CommandLine = ReadString(process["CommandLine"]) ?? existing.CommandLine,
                        UserName = ReadProcessOwner(process) ?? existing.UserName
                    };
                }
            }
        }
        catch
        {
            // Runtime process data is still useful when WMI is unavailable or restricted.
        }
    }

    private static void AddServiceData(IReadOnlySet<int> processIds, IDictionary<int, ProcessMetadata> metadata)
    {
        if (processIds.Count == 0)
        {
            return;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name,DisplayName,State,StartMode,PathName,ProcessId FROM Win32_Service");

            foreach (ManagementObject service in searcher.Get())
            {
                using (service)
                {
                    var processId = Convert.ToInt32(service["ProcessId"]);
                    if (!processIds.Contains(processId))
                    {
                        continue;
                    }

                    var existing = GetOrCreate(metadata, processId);
                    var services = existing.Services.ToList();
                    services.Add(new ServiceInfo(
                        ReadString(service["Name"]) ?? "-",
                        ReadString(service["DisplayName"]) ?? "-",
                        ReadString(service["State"]) ?? "-",
                        ReadString(service["StartMode"]) ?? "-",
                        ReadString(service["PathName"]) ?? "-"));

                    metadata[processId] = existing with { Services = services };
                }
            }
        }
        catch
        {
            // svchost service details are best effort; do not fail the port scan.
        }
    }

    private static ProcessMetadata GetOrCreate(IDictionary<int, ProcessMetadata> metadata, int processId)
    {
        if (metadata.TryGetValue(processId, out var existing))
        {
            return existing;
        }

        var created = new ProcessMetadata();
        metadata[processId] = created;
        return created;
    }

    private static string? ReadString(object? value)
    {
        return string.IsNullOrWhiteSpace(value?.ToString()) ? null : value.ToString();
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadProcessOwner(ManagementObject process)
    {
        try
        {
            var args = new object[2];
            var result = Convert.ToUInt32(process.InvokeMethod("GetOwner", args));
            if (result != 0)
            {
                return null;
            }

            var user = args[0]?.ToString();
            var domain = args[1]?.ToString();

            return string.IsNullOrWhiteSpace(domain) ? user : $@"{domain}\{user}";
        }
        catch
        {
            return null;
        }
    }
}
