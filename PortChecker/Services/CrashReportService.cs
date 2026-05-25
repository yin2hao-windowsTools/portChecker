using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace PortChecker.Services;

internal static class CrashReportService
{
    private const string DataDirectoryEnvironmentVariable = "PORTCHECKER_DATA_DIR";
    private static int _reportCounter;

    public static void Register(Application application)
    {
        application.DispatcherUnhandledException += (_, e) => HandleDispatcherException(application, e);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteReport("UnhandledException", e.ExceptionObject, e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteReport("UnobservedTaskException", e.Exception, false);
            e.SetObserved();
        };
    }

    private static void HandleDispatcherException(Application application, DispatcherUnhandledExceptionEventArgs e)
    {
        var reportPath = WriteReport("DispatcherUnhandledException", e.Exception, true);
        e.Handled = true;

        ShowCrashMessage(reportPath);
        application.Shutdown(1);
    }

    private static string? WriteReport(string source, object exceptionObject, bool isTerminating)
    {
        try
        {
            var crashDirectory = GetCrashDirectory();
            Directory.CreateDirectory(crashDirectory);

            var sequence = Interlocked.Increment(ref _reportCounter);
            var fileName = $"crash-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}-{sequence}.log";
            var reportPath = Path.Combine(crashDirectory, fileName);

            File.WriteAllText(reportPath, BuildReport(source, exceptionObject, isTerminating), Encoding.UTF8);
            return reportPath;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildReport(string source, object exceptionObject, bool isTerminating)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{ApplicationInfo.Name} Crash Report");
        builder.AppendLine($"Version: {ApplicationInfo.CurrentVersionText}");
        builder.AppendLine($"Source: {source}");
        builder.AppendLine($"IsTerminating: {isTerminating}");
        builder.AppendLine($"LocalTime: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
        builder.AppendLine($"UtcTime: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff 'UTC'}");
        builder.AppendLine($"ProcessId: {Environment.ProcessId}");
        builder.AppendLine($"ProcessPath: {Environment.ProcessPath ?? "Unknown"}");
        builder.AppendLine($"BaseDirectory: {AppContext.BaseDirectory}");
        builder.AppendLine($"CurrentDirectory: {Environment.CurrentDirectory}");
        builder.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        builder.AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}");
        builder.AppendLine($"OSArchitecture: {RuntimeInformation.OSArchitecture}");
        builder.AppendLine($"ProcessArchitecture: {RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine();
        builder.AppendLine("Exception:");
        builder.AppendLine(exceptionObject.ToString());
        return builder.ToString();
    }

    private static string GetCrashDirectory()
    {
        var dataDirectory = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            return Path.Combine(dataDirectory, "crash");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, "PortChecker", "crash");
        }

        return Path.Combine(AppContext.BaseDirectory, "crash");
    }

    private static void ShowCrashMessage(string? reportPath)
    {
        var reportText = string.IsNullOrWhiteSpace(reportPath)
            ? "未能写入 crash 日志。"
            : $"Crash 日志已保存到：{reportPath}";

        MessageBox.Show(
            $"程序遇到未处理异常，将退出。{Environment.NewLine}{Environment.NewLine}{reportText}",
            $"{ApplicationInfo.Name} Crash",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
