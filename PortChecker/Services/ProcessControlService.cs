using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace PortChecker.Services;

internal sealed class ProcessControlService
{
    private const string KillProcessArgument = "--kill-process";

    public Task KillProcessAsync(int processId, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            TryKillProcess(processId, allowElevatedRetry: !PrivilegeService.IsRunningAsAdministrator());
        }, cancellationToken);
    }

    public async Task StopServiceAsync(string serviceName, CancellationToken cancellationToken)
    {
        await InvokeServiceMethodAsync(serviceName, "StopService", cancellationToken);
        await WaitForServiceStateAsync(serviceName, "Stopped", TimeSpan.FromSeconds(15), cancellationToken);
    }

    public async Task RestartServiceAsync(string serviceName, CancellationToken cancellationToken)
    {
        await InvokeServiceMethodAsync(serviceName, "StopService", cancellationToken);
        await WaitForServiceStateAsync(serviceName, "Stopped", TimeSpan.FromSeconds(15), cancellationToken);
        await InvokeServiceMethodAsync(serviceName, "StartService", cancellationToken);
        await WaitForServiceStateAsync(serviceName, "Running", TimeSpan.FromSeconds(15), cancellationToken);
    }

    public Task KillProcessElevatedAsync(int processId, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new ProcessControlException("无法定位当前程序，不能发起管理员权限操作。", false);
            }

            using var process = StartElevatedHelper(executablePath, processId);

            if (process is null)
            {
                throw new ProcessControlException("管理员权限操作未能启动。", false);
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new ProcessControlException(GetElevatedKillFailureMessage(process.ExitCode), false);
            }
        }, cancellationToken);
    }

    public static bool TryHandleElevatedCommand(string[] args)
    {
        if (args.Length != 2
            || !args[0].Equals(KillProcessArgument, StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(args[1], out var processId)
            || processId <= 0)
        {
            return false;
        }

        try
        {
            TryKillProcess(processId, allowElevatedRetry: false);
            Environment.ExitCode = 0;
        }
        catch
        {
            Environment.ExitCode = 1;
        }

        return true;
    }

    public Task OpenFileLocationAsync(string filePath)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{filePath}\"",
                UseShellExecute = true
            });
        });
    }

    public Task OpenTaskManagerAsync()
    {
        return Task.Run(() =>
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "taskmgr.exe",
                UseShellExecute = true
            });
        });
    }

    private static Task InvokeServiceMethodAsync(
        string serviceName,
        string methodName,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateServiceName(serviceName);

            using var service = GetService(serviceName);
            var result = Convert.ToUInt32(service.InvokeMethod(methodName, null));
            if (result != 0)
            {
                throw new ProcessControlException(GetServiceControlErrorMessage(result), result == 2);
            }
        }, cancellationToken);
    }

    private static Task WaitForServiceStateAsync(
        string serviceName,
        string targetState,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            var deadline = DateTimeOffset.Now + timeout;
            while (DateTimeOffset.Now < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var service = GetService(serviceName);
                var state = service["State"]?.ToString();
                if (targetState.Equals(state, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                await Task.Delay(500, cancellationToken);
            }

            throw new TimeoutException($"等待服务进入 {targetState} 状态超时。");
        }, cancellationToken);
    }

    private static ManagementObject GetService(string serviceName)
    {
        var escapedName = serviceName.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        var service = new ManagementObject($"Win32_Service.Name=\"{escapedName}\"");

        try
        {
            service.Get();
            return service;
        }
        catch
        {
            service.Dispose();
            throw;
        }
    }

    private static void ValidateServiceName(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName) || serviceName == "-")
        {
            throw new ArgumentException("服务名称无效。", nameof(serviceName));
        }
    }

    private static void TryKillProcess(int processId, bool allowElevatedRetry)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (ArgumentException exception)
        {
            throw new ProcessControlException($"PID {processId} 不存在或已经退出。", false, exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new ProcessControlException($"PID {processId} 不存在或已经退出。", false, exception);
        }
        catch (Win32Exception exception) when (IsAccessDenied(exception))
        {
            throw new ProcessControlException("当前普通权限不足以结束该进程。", allowElevatedRetry, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ProcessControlException("当前普通权限不足以结束该进程。", allowElevatedRetry, exception);
        }
    }

    private static Process? StartElevatedHelper(string executablePath, int processId)
    {
        try
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = $"{KillProcessArgument} {processId}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
            });
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new ProcessControlException("已取消管理员权限请求。", false, exception);
        }
    }

    private static bool IsAccessDenied(Win32Exception exception)
    {
        return exception.NativeErrorCode is 5 or 740;
    }

    private static string GetServiceControlErrorMessage(uint returnCode)
    {
        return returnCode switch
        {
            1 => "请求不受支持。",
            2 => "访问被拒绝，请使用管理员权限运行。",
            3 => "依赖服务正在运行，无法完成操作。",
            4 => "服务控制请求无效。",
            5 => "服务无法接受当前控制请求。",
            6 => "服务未处于活动状态。",
            7 => "服务请求超时。",
            8 => "未知失败。",
            9 => "找不到服务路径。",
            10 => "服务已在运行。",
            11 => "服务数据库被锁定。",
            12 => "服务依赖项已被删除。",
            13 => "服务依赖项启动失败。",
            14 => "服务已被禁用。",
            15 => "服务登录失败。",
            16 => "服务被标记为待删除。",
            17 => "服务没有线程。",
            18 => "服务存在循环依赖。",
            19 => "服务名称重复。",
            20 => "服务名称包含无效字符。",
            21 => "传入参数无效。",
            22 => "服务账户无效或缺少运行权限。",
            23 => "服务已存在。",
            24 => "服务已暂停。",
            _ => $"服务控制失败，返回码 {returnCode}。"
        };
    }

    private static string GetElevatedKillFailureMessage(int exitCode)
    {
        return exitCode switch
        {
            1 => "管理员权限操作未能结束该进程，进程可能已经退出或受系统保护。",
            _ => $"管理员权限操作失败，退出码 {exitCode}。"
        };
    }
}
