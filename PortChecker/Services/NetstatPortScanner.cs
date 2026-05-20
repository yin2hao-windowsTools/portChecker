using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using PortChecker.Models;

namespace PortChecker.Services;

internal sealed class NetstatPortScanner
{
    private static readonly TimeSpan NetstatTimeout = TimeSpan.FromSeconds(10);

    public IReadOnlyList<PortSnapshot> GetActivePorts()
    {
        return ParseOutput(RunNetstat());
    }

    internal static IReadOnlyList<PortSnapshot> ParseOutput(string output)
    {
        var rows = new List<PortSnapshot>();
        using var reader = new StringReader(output);

        while (reader.ReadLine() is { } line)
        {
            if (TryParseLine(line, out var snapshot))
            {
                rows.Add(snapshot);
            }
        }

        return rows;
    }

    internal static bool TryParseLine(string line, [NotNullWhen(true)] out PortSnapshot? snapshot)
    {
        snapshot = null;
        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 4)
        {
            return false;
        }

        if (fields[0].Equals("TCP", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseTcpLine(fields, out snapshot);
        }

        if (fields[0].Equals("UDP", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseUdpLine(fields, out snapshot);
        }

        return false;
    }

    private static bool TryParseTcpLine(string[] fields, [NotNullWhen(true)] out PortSnapshot? snapshot)
    {
        snapshot = null;
        if (fields.Length < 5
            || !TryParseEndpoint(fields[1], out var localAddress, out var localPort)
            || !TryParseEndpoint(fields[2], out var remoteAddress, out var remotePort)
            || localPort is null
            || !TryParseProcessId(fields[^1], out var processId))
        {
            return false;
        }

        snapshot = new PortSnapshot(
            PortProtocol.Tcp,
            localAddress,
            localPort.Value,
            remoteAddress,
            remotePort,
            NormalizeState(fields[3]),
            processId);
        return true;
    }

    private static bool TryParseUdpLine(string[] fields, [NotNullWhen(true)] out PortSnapshot? snapshot)
    {
        snapshot = null;
        if (!TryParseEndpoint(fields[1], out var localAddress, out var localPort)
            || localPort is null
            || !TryParseProcessId(fields[^1], out var processId))
        {
            return false;
        }

        snapshot = new PortSnapshot(
            PortProtocol.Udp,
            localAddress,
            localPort.Value,
            "-",
            null,
            "-",
            processId);
        return true;
    }

    private static string RunNetstat()
    {
        var executablePath = Path.Combine(Environment.SystemDirectory, "netstat.exe");
        if (!File.Exists(executablePath))
        {
            executablePath = "netstat.exe";
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "-anoq",
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
            throw new Win32Exception("netstat 进程未能启动。");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)NetstatTimeout.TotalMilliseconds))
        {
            TryKill(process);
            throw new TimeoutException("netstat 执行超时。");
        }

        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"netstat 退出码 {process.ExitCode}。"
                    : $"netstat 退出码 {process.ExitCode}：{error.Trim()}");
        }

        return output;
    }

    private static bool TryParseEndpoint(string endpoint, out string address, out int? port)
    {
        address = "-";
        port = null;

        if (endpoint.Equals("*:*", StringComparison.Ordinal))
        {
            return true;
        }

        var separatorIndex = endpoint.LastIndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == endpoint.Length - 1)
        {
            return false;
        }

        var addressPart = endpoint[..separatorIndex].Trim('[', ']');
        var portPart = endpoint[(separatorIndex + 1)..];
        if (!int.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort)
            || parsedPort is < 0 or > 65535)
        {
            return false;
        }

        address = NormalizeAddress(addressPart);
        port = parsedPort;
        return true;
    }

    private static string NormalizeAddress(string address)
    {
        if (address.Equals("*", StringComparison.Ordinal))
        {
            return "-";
        }

        return IPAddress.TryParse(address, out var ipAddress)
            ? ipAddress.ToString()
            : address;
    }

    private static string NormalizeState(string state)
    {
        return string.IsNullOrWhiteSpace(state)
            ? "-"
            : state.Trim().ToUpperInvariant();
    }

    private static bool TryParseProcessId(string value, out int processId)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out processId)
            && processId >= 0;
    }

    private static void TryKill(Process process)
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
}
