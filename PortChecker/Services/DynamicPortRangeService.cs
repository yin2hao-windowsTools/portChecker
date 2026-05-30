using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PortChecker.Models;

namespace PortChecker.Services;

internal sealed class DynamicPortRangeService
{
    private const string SetDynamicPortRangeArgument = "--set-dynamic-port-range";
    private static readonly TimeSpan NetshTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ElevatedHelperTimeout = TimeSpan.FromSeconds(90);

    public Task<IReadOnlyList<DynamicPortRange>> GetDynamicRangesAsync(string store, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateStore(store);

            var ranges = new List<DynamicPortRange>();
            foreach (var addressFamily in new[] { "ipv4", "ipv6" })
            {
                foreach (var protocol in new[] { PortProtocol.Tcp, PortProtocol.Udp })
                {
                    var output = RunNetsh(
                        $"interface {addressFamily} show dynamicportrange protocol={ToNetshProtocol(protocol)} store={store}",
                        cancellationToken);
                    ranges.Add(ParseShowOutput(output, addressFamily, protocol, store));
                }
            }

            return (IReadOnlyList<DynamicPortRange>)ranges
                .OrderBy(range => range.AddressFamilyText)
                .ThenBy(range => range.Protocol)
                .ToList();
        }, cancellationToken);
    }

    public Task SetDynamicRangeAsync(
        string addressFamily,
        PortProtocol protocol,
        int startPort,
        int portCount,
        string store,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetDynamicRangeCore(
                addressFamily,
                protocol,
                startPort,
                portCount,
                store,
                allowElevatedRetry: !PrivilegeService.IsRunningAsAdministrator(),
                cancellationToken);
        }, cancellationToken);
    }

    public Task SetDynamicRangeElevatedAsync(
        string addressFamily,
        PortProtocol protocol,
        int startPort,
        int portCount,
        string store,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateRange(addressFamily, protocol, startPort, portCount, store);

            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new ProcessControlException("无法定位当前程序，不能发起管理员权限操作。", false);
            }

            using var process = StartElevatedHelper(
                executablePath,
                SetDynamicPortRangeArgument,
                NormalizeAddressFamily(addressFamily),
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
                throw new ProcessControlException(GetElevatedSetFailureMessage(process.ExitCode), false);
            }
        }, cancellationToken);
    }

    public static bool TryHandleElevatedCommand(string[] args)
    {
        if (args.Length != 6 || !args[0].Equals(SetDynamicPortRangeArgument, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return HandleElevatedCommand(() =>
        {
            var service = new DynamicPortRangeService();
            var addressFamily = NormalizeAddressFamily(args[1]);
            var protocol = ParseProtocolArgument(args[2]);
            var startPort = ParsePortArgument(args[3], "startPort");
            var portCount = ParsePortCountArgument(args[4]);
            var store = args[5];

            service.SetDynamicRangeCore(
                addressFamily,
                protocol,
                startPort,
                portCount,
                store,
                allowElevatedRetry: false,
                CancellationToken.None);
        });
    }

    internal static DynamicPortRange ParseShowOutput(
        string output,
        string addressFamily,
        PortProtocol protocol,
        string store)
    {
        int? startPort = null;
        int? portCount = null;

        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Start Port", StringComparison.OrdinalIgnoreCase))
            {
                startPort = TryParseValueAfterColon(trimmed);
                continue;
            }

            if (trimmed.StartsWith("Number of Ports", StringComparison.OrdinalIgnoreCase))
            {
                portCount = TryParseValueAfterColon(trimmed);
            }
        }

        if (startPort is null || portCount is null)
        {
            throw new InvalidOperationException("未能从 netsh 输出中解析动态端口范围。");
        }

        return new DynamicPortRange
        {
            AddressFamily = NormalizeAddressFamily(addressFamily),
            Protocol = protocol,
            StartPort = startPort.Value,
            PortCount = portCount.Value,
            Store = store
        };
    }

    private void SetDynamicRangeCore(
        string addressFamily,
        PortProtocol protocol,
        int startPort,
        int portCount,
        string store,
        bool allowElevatedRetry,
        CancellationToken cancellationToken)
    {
        ValidateRange(addressFamily, protocol, startPort, portCount, store);

        try
        {
            RunNetsh(
                $"interface {NormalizeAddressFamily(addressFamily)} set dynamicportrange protocol={ToNetshProtocol(protocol)} startport={startPort.ToString(CultureInfo.InvariantCulture)} numberofports={portCount.ToString(CultureInfo.InvariantCulture)} store={store}",
                cancellationToken);
        }
        catch (Win32Exception exception) when (IsAccessDenied(exception))
        {
            throw new ProcessControlException("当前普通权限不足以管理 Windows 动态端口。", allowElevatedRetry, exception);
        }
        catch (InvalidOperationException exception) when (IsAccessDeniedOutput(exception.Message))
        {
            throw new ProcessControlException("当前普通权限不足以管理 Windows 动态端口。", allowElevatedRetry, exception);
        }
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

    private static int? TryParseValueAfterColon(string line)
    {
        var index = line.IndexOf(':');
        if (index < 0)
        {
            return null;
        }

        var value = line[(index + 1)..].Trim();
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static void ValidateRange(string addressFamily, PortProtocol protocol, int startPort, int portCount, string store)
    {
        _ = NormalizeAddressFamily(addressFamily);

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
            throw new ArgumentException("动态端口存储范围必须是 active 或 persistent。", nameof(store));
        }
    }

    private static string NormalizeAddressFamily(string value)
    {
        return value.Equals("ipv6", StringComparison.OrdinalIgnoreCase)
            ? "ipv6"
            : value.Equals("ipv4", StringComparison.OrdinalIgnoreCase)
                ? "ipv4"
                : throw new ArgumentException("地址族参数无效。", nameof(value));
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

    private static string GetElevatedSetFailureMessage(int exitCode)
    {
        return exitCode switch
        {
            1 => "管理员权限操作未能设置动态端口范围，参数可能无效或与当前系统规则冲突。",
            _ => $"管理员权限设置动态端口范围失败，退出码 {exitCode}。"
        };
    }
}
