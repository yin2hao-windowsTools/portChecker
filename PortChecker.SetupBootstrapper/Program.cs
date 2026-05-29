using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace PortChecker.SetupBootstrapper;

internal static partial class Program
{
    private const string AppName = "Port Checker";
    private const string PayloadResourceName = "PortChecker.Payload.msi";
    private const string DotNetDownloadUrl = "https://dotnet.microsoft.com/download/dotnet/8.0";
    private const string BootstrapperModeMetadataName = "PortCheckerBootstrapperMode";
    private const string PortableTargetPathMetadataName = "PortCheckerPortableTargetPath";
    private const uint MbIconError = 0x10;
    private const uint MbIconQuestion = 0x20;
    private const uint MbYesNo = 0x04;
    private const uint IdYes = 6;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            return GetBootstrapperMode() switch
            {
                "portable" => RunPortableLauncher(args),
                _ => RunSetupBootstrapper()
            };
        }
        catch (Exception exception)
        {
            var caption = GetBootstrapperMode() == "portable"
                ? AppName
                : $"{AppName} Setup";
            var message = GetBootstrapperMode() == "portable"
                ? "便携版启动器启动失败。"
                : "安装引导程序启动失败。";

            MessageBox(
                IntPtr.Zero,
                message + Environment.NewLine + Environment.NewLine + exception.Message,
                caption,
                MbIconError);
            return 1;
        }
    }

    private static int RunSetupBootstrapper()
    {
        if (!HasDesktopRuntime8())
        {
            var message =
                "此安装包需要 .NET 8 Desktop Runtime (x64) 才能继续安装。"
                + Environment.NewLine
                + Environment.NewLine
                + "是否现在打开微软官方下载页？";

            if (MessageBox(IntPtr.Zero, message, $"{AppName} Setup", MbYesNo | MbIconQuestion) == IdYes)
            {
                OpenUrl(DotNetDownloadUrl);
            }

            return 1;
        }

        var payloadPath = ExtractPayloadToTemp();
        LaunchMsiInstaller(payloadPath);
        return 0;
    }

    private static int RunPortableLauncher(IReadOnlyList<string> args)
    {
        if (!HasDesktopRuntime8())
        {
            var message =
                "此便携版需要 .NET 8 Desktop Runtime (x64) 才能启动。"
                + Environment.NewLine
                + Environment.NewLine
                + "是否现在打开微软官方下载页？";

            if (MessageBox(IntPtr.Zero, message, AppName, MbYesNo | MbIconQuestion) == IdYes)
            {
                OpenUrl(DotNetDownloadUrl);
            }

            return 1;
        }

        var portableRoot = AppContext.BaseDirectory;
        var targetRelativePath = GetAssemblyMetadata(PortableTargetPathMetadataName);
        if (string.IsNullOrWhiteSpace(targetRelativePath))
        {
            throw new InvalidOperationException("未配置便携版主程序路径。");
        }

        var targetPath = Path.GetFullPath(Path.Combine(portableRoot, targetRelativePath));
        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException("未找到便携版主程序。", targetPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = targetPath,
            UseShellExecute = true,
            WorkingDirectory = portableRoot
        };

        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (Process.Start(startInfo) is null)
        {
            throw new InvalidOperationException("未能启动便携版主程序。");
        }

        return 0;
    }

    private static bool HasDesktopRuntime8()
    {
        var runtimeRoot = GetDesktopRuntimeRoot();
        if (string.IsNullOrWhiteSpace(runtimeRoot) || !Directory.Exists(runtimeRoot))
        {
            return false;
        }

        foreach (var directoryPath in Directory.EnumerateDirectories(runtimeRoot))
        {
            var directoryName = Path.GetFileName(directoryPath);
            if (Version.TryParse(directoryName, out var version) && version.Major >= 8)
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetDesktopRuntimeRoot()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
        {
            return null;
        }

        return Path.Combine(programFiles, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
    }

    private static string ExtractPayloadToTemp()
    {
        using var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName)
            ?? throw new InvalidOperationException("未找到内置 MSI 安装包资源。");

        var tempRoot = Path.Combine(Path.GetTempPath(), "PortChecker", "setup");
        Directory.CreateDirectory(tempRoot);

        var fileName = $"PortChecker-{ThisAssemblyVersion}-setup.msi";
        var payloadPath = Path.Combine(tempRoot, fileName);

        using var fileStream = File.Create(payloadPath);
        resourceStream.CopyTo(fileStream);
        return payloadPath;
    }

    private static void LaunchMsiInstaller(string payloadPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            Arguments = $"/i \"{payloadPath}\"",
            UseShellExecute = true
        };

        if (Process.Start(startInfo) is null)
        {
            throw new InvalidOperationException("未能启动 MSI 安装程序。");
        }
    }

    private static void OpenUrl(string url)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        };

        if (Process.Start(startInfo) is null)
        {
            throw new InvalidOperationException("未能打开下载页面。");
        }
    }

    private static string GetBootstrapperMode()
    {
        var mode = GetAssemblyMetadata(BootstrapperModeMetadataName);
        return string.IsNullOrWhiteSpace(mode)
            ? "setup"
            : mode.Trim().ToLowerInvariant();
    }

    private static string? GetAssemblyMetadata(string key)
    {
        return Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key.Equals(key, StringComparison.Ordinal))
            ?.Value;
    }

    private static string ThisAssemblyVersion
    {
        get
        {
            var informationalVersion = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                var metadataIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
                return metadataIndex > 0
                    ? SanitizeFileSegment(informationalVersion[..metadataIndex])
                    : SanitizeFileSegment(informationalVersion);
            }

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null
                ? "0.0.0"
                : SanitizeFileSegment(version.ToString());
        }
    }

    private static string SanitizeFileSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(Path.GetInvalidFileNameChars().Contains(character) ? '-' : character);
        }

        return builder.Length == 0 ? "0.0.0" : builder.ToString();
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr windowHandle, string text, string caption, uint type);
}
