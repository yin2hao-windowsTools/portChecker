using System.Diagnostics;
using System.ComponentModel;
using System.IO;
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

    private static string GetElevatedKillFailureMessage(int exitCode)
    {
        return exitCode switch
        {
            1 => "管理员权限操作未能结束该进程，进程可能已经退出或受系统保护。",
            _ => $"管理员权限操作失败，退出码 {exitCode}。"
        };
    }
}
