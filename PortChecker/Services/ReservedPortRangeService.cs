using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PortChecker.Models;

namespace PortChecker.Services;

internal sealed class ReservedPortRangeService
{
    private const string AddReservedPortRangeArgument = "--add-reserved-port-range";
    private const string DeleteReservedPortRangeArgument = "--delete-reserved-port-range";
    private static readonly TimeSpan NetshTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ElevatedHelperTimeout = TimeSpan.FromSeconds(90);

    public Task<IReadOnlyList<ReservedPortRange>> GetReservedRangesAsync(string store, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateStore(store);

            var ranges = new List<ReservedPortRange>();
            foreach (var protocol in new[] { PortProtocol.Tcp, PortProtocol.Udp })
            {
                var output = RunNetsh(
                    $"interface ipv4 show excludedportrange protocol={ToNetshProtocol(protocol)} store={store}",
                    cancellationToken);
                ranges.AddRange(ParseShowOutput(output, protocol, store));
            }

            return (IReadOnlyList<ReservedPortRange>)ranges
                .OrderBy(range => range.Protocol)
                .ThenBy(range => range.StartPort)
                .ThenBy(range => range.EndPort)
                .ToList();
        }, cancellationToken);
    }

    public Task AddReservedRangeAsync(
        PortProtocol protocol,
        int startPort,
        int portCount,
        string store,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddReservedRangeCore(
                protocol,
                startPort,
                portCount,
                store,
                allowElevatedRetry: !PrivilegeService.IsRunningAsAdministrator(),
                cancellationToken);
        }, cancellationToken);
    }

    public Task DeleteReservedRangeAsync(
        ReservedPortRange range,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteReservedRangeCore(
                range.Protocol,
                range.StartPort,
                range.PortCount,
                range.Store,
                allowElevatedRetry: !PrivilegeService.IsRunningAsAdministrator(),
                cancellationToken);
        }, cancellationToken);
    }

    public Task AddReservedRangeElevatedAsync(
        PortProtocol protocol,
        int startPort,
        int portCount,
        string store,
        CancellationToken cancellationToken)
    {
        return RunElevatedReservedRangeCommandAsync(
            AddReservedPortRangeArgument,
            protocol,
            startPort,
            portCount,
            store,
            cancellationToken);
    }

    public Task DeleteReservedRangeElevatedAsync(
        ReservedPortRange range,
        CancellationToken cancellationToken)
    {
        return RunElevatedReservedRangeCommandAsync(
            DeleteReservedPortRangeArgument,
            range.Protocol,
            range.StartPort,
            range.PortCount,
            range.Store,
            cancellationToken);
    }

    public static bool TryHandleElevatedCommand(string[] args)
    {
        if (args.Length != 5)
        {
            return false;
        }

        var command = args[0];
        if (!command.Equals(AddReservedPortRangeArgument, StringComparison.OrdinalIgnoreCase)
            && !command.Equals(DeleteReservedPortRangeArgument, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return HandleElevatedCommand(() =>
        {
            var service = new ReservedPortRangeService();
            var protocol = ParseProtocolArgument(args[1]);
            var startPort = ParsePortArgument(args[2], "startPort");
            var portCount = ParsePortCountArgument(args[3]);
            var store = args[4];

            if (command.Equals(AddReservedPortRangeArgument, StringComparison.OrdinalIgnoreCase))
            {
                service.AddReservedRangeCore(
                    protocol,
                    startPort,
                    portCount,
                    store,
                    allowElevatedRetry: false,
                    CancellationToken.None);
                return;
            }

            service.DeleteReservedRangeCore(
                protocol,
                startPort,
                portCount,
                store,
                allowElevatedRetry: false,
                CancellationToken.None);
        });
    }

    internal static IReadOnlyList<ReservedPortRange> ParseShowOutput(
        string output,
        PortProtocol protocol,
        string store)
    {
        var ranges = new List<ReservedPortRange>();
        using var reader = new StringReader(output);

        while (reader.ReadLine() is { } line)
        {
            if (TryParseRangeLine(line, protocol, store, out var range))
            {
                ranges.Add(range);
            }
        }

        return ranges;
    }

    internal static bool TryParseRangeLine(
        string line,
        PortProtocol protocol,
        string store,
        [NotNullWhen(true)] out ReservedPortRange? range)
    {
        range = null;
        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2
            || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var startPort)
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var endPort)
            || !IsValidPort(startPort)
            || !IsValidPort(endPort)
            || endPort < startPort)
        {
            return false;
        }

        range = new ReservedPortRange
        {
            Protocol = protocol,
            StartPort = startPort,
            EndPort = endPort,
            Store = store,
            IsAdministered = fields.Skip(2).Any(field => field.Equals("*", StringComparison.Ordinal))
        };
        return true;
    }

    private void AddReservedRangeCore(
        PortProtocol protocol,
        int startPort,
        int portCount,
        string store,
        bool allowElevatedRetry,
        CancellationToken cancellationToken)
    {
        ValidateRange(protocol, startPort, portCount, store);
        RunReservedRangeCommand(
            "add",
            protocol,
            startPort,
            portCount,
            store,
            allowElevatedRetry,
            cancellationToken);
    }

    private void DeleteReservedRangeCore(
        PortProtocol protocol,
        int startPort,
        int portCount,
        string store,
        bool allowElevatedRetry,
        CancellationToken cancellationToken)
    {
        ValidateRange(protocol, startPort, portCount, store);
        RunReservedRangeCommand(
            "delete",
            protocol,
            startPort,
            portCount,
            store,
            allowElevatedRetry,
            cancellationToken);
    }

    private static void RunReservedRangeCommand(
        string operation,
        PortProtocol protocol,
        int startPort,
        int portCount,
        string store,
        bool allowElevatedRetry,
        CancellationToken cancellationToken)
    {
        try
        {
            RunNetsh(
                $"interface ipv4 {operation} excludedportrange protocol={ToNetshProtocol(protocol)} startport={startPort.ToString(CultureInfo.InvariantCulture)} numberofports={portCount.ToString(CultureInfo.InvariantCulture)} store={store}",
                cancellationToken);
        }
        catch (Win32Exception exception) when (IsAccessDenied(exception))
        {
            throw new ProcessControlException("当前普通权限不足以管理 Windows 保留端口。", allowElevatedRetry, exception);
        }
        catch (InvalidOperationException exception) when (IsAccessDeniedOutput(exception.Message))
        {
            throw new ProcessControlException("当前普通权限不足以管理 Windows 保留端口。", allowElevatedRetry, exception);
        }
    }

    private static Task RunElevatedReservedRangeCommandAsync(
        string commandArgument,
        PortProtocol protocol,
        int startPort,
        int portCount,
        string store,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateRange(protocol, startPort, portCount, store);

            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new ProcessControlException("无法定位当前程序，不能发起管理员权限操作。", false);
            }

            using var process = StartElevatedHelper(
                executablePath,
                commandArgument,
                ToNetshProtocol(protocol),
                startPort.ToString(CultureInfo.InvariantCulture),
                portCount.ToString(CultureInfo.InvariantCulture),
                store);

            if (process is null)
            {
                throw new ProcessControlException("管理员权限操作未能启动。", false);
            }

            WaitForElevatedHelperExit(process, cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new ProcessControlException(GetElevatedReservedRangeFailureMessage(commandArgument, process.ExitCode), false);
            }
        }, cancellationToken);
    }

    private static string RunNetsh(string arguments, CancellationToken cancellationToken)
    {
        var executablePath = Path.Combine(Environment.SystemDirectory, "netsh.exe");
        if (!File.Exists(executablePath))
        {
            executablePath = "netsh.exe";
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.Default,
                StandardErrorEncoding = Encoding.Default
            }
        };

        if (!process.Start())
        {
            throw new Win32Exception("netsh 进程未能启动。");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        if (!WaitForExit(process, NetshTimeout, cancellationToken))
        {
            TryKillProcessTree(process);
            throw new TimeoutException("netsh 执行超时。");
        }

        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"netsh 退出码 {process.ExitCode}：{output.Trim()}"
                    : $"netsh 退出码 {process.ExitCode}：{error.Trim()}");
        }

        return output;
    }

    private static bool WaitForExit(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (process.WaitForExit(100))
            {
                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        return false;
    }

    private static Process? StartElevatedHelper(string executablePath, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = string.Join(' ', arguments.Select(QuoteArgument)),
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
            };

            return Process.Start(startInfo);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new ProcessControlException("已取消管理员权限请求。", false, exception);
        }
    }

    private static void WaitForElevatedHelperExit(Process process, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!process.WaitForExit(250))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed < ElevatedHelperTimeout)
            {
                continue;
            }

            TryKillProcessTree(process);
            throw new ProcessControlException("管理员权限操作等待超时，已尝试停止辅助进程。", false);
        }
    }

    private static bool HandleElevatedCommand(Action operation)
    {
        try
        {
            operation();
            Environment.ExitCode = 0;
        }
        catch
        {
            Environment.ExitCode = 1;
        }

        return true;
    }

    private static PortProtocol ParseProtocolArgument(string value)
    {
        if (value.Equals("tcp", StringComparison.OrdinalIgnoreCase))
        {
            return PortProtocol.Tcp;
        }

        if (value.Equals("udp", StringComparison.OrdinalIgnoreCase))
        {
            return PortProtocol.Udp;
        }

        throw new ArgumentException("协议参数无效。", nameof(value));
    }

    private static int ParsePortArgument(string value, string argumentName)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || !IsValidPort(port))
        {
            throw new ArgumentException("端口参数无效。", argumentName);
        }

        return port;
    }

    private static int ParsePortCountArgument(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var portCount)
            || portCount <= 0)
        {
            throw new ArgumentException("端口数量参数无效。", nameof(value));
        }

        return portCount;
    }

    private static void ValidateRange(PortProtocol protocol, int startPort, int portCount, string store)
    {
        if (protocol is not PortProtocol.Tcp and not PortProtocol.Udp)
        {
            throw new ArgumentException("协议必须是 TCP 或 UDP。", nameof(protocol));
        }

        if (!IsValidPort(startPort))
        {
            throw new ArgumentOutOfRangeException(nameof(startPort), "起始端口必须在 0 到 65535 之间。");
        }

        if (portCount <= 0 || startPort + portCount - 1 > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(portCount), "端口数量必须大于 0，且结束端口不能超过 65535。");
        }

        ValidateStore(store);
    }

    private static void ValidateStore(string store)
    {
        if (!store.Equals("active", StringComparison.OrdinalIgnoreCase)
            && !store.Equals("persistent", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("保留端口存储范围必须是 active 或 persistent。", nameof(store));
        }
    }

    private static bool IsValidPort(int port)
    {
        return port is >= 0 and <= 65535;
    }

    private static string ToNetshProtocol(PortProtocol protocol)
    {
        return protocol switch
        {
            PortProtocol.Tcp => "tcp",
            PortProtocol.Udp => "udp",
            _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "协议必须是 TCP 或 UDP。")
        };
    }

    private static bool IsAccessDenied(Win32Exception exception)
    {
        return exception.NativeErrorCode is 5 or 740;
    }

    private static bool IsAccessDeniedOutput(string message)
    {
        return message.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("access denied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("requires elevation", StringComparison.OrdinalIgnoreCase)
            || message.Contains("operation requires elevation", StringComparison.OrdinalIgnoreCase)
            || message.Contains("访问被拒绝", StringComparison.OrdinalIgnoreCase)
            || message.Contains("请求的操作需要提升", StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteArgument(string argument)
    {
        var builder = new StringBuilder(argument.Length + 2);
        builder.Append('"');

        var backslashCount = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', (backslashCount * 2) + 1);
                builder.Append('"');
                backslashCount = 0;
                continue;
            }

            builder.Append('\\', backslashCount);
            builder.Append(character);
            backslashCount = 0;
        }

        builder.Append('\\', backslashCount * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private static string GetElevatedReservedRangeFailureMessage(string commandArgument, int exitCode)
    {
        var actionText = commandArgument.Equals(AddReservedPortRangeArgument, StringComparison.OrdinalIgnoreCase)
            ? "添加"
            : "删除";

        return exitCode switch
        {
            1 => $"管理员权限操作未能{actionText}保留端口，规则可能冲突、参数不匹配或已被系统占用。",
            _ => $"管理员权限{actionText}保留端口失败，退出码 {exitCode}。"
        };
    }
}
