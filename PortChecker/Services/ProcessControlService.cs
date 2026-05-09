using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace PortChecker.Services;

internal sealed class ProcessControlService
{
    public Task KillProcessAsync(int processId, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }, cancellationToken);
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
}
