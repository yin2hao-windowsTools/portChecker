using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PortChecker.Services;

internal sealed class UpdateInstallService
{
    private const string PortableZipMode = "portableZip";
    private const string MsiMode = "msi";
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async Task<UpdateInstallLaunchResult> DownloadAndLaunchAsync(
        UpdateCheckResult update,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var package = SelectPackage(update);
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("无法定位当前程序，不能执行自动更新。");
        }

        var workDirectory = Path.Combine(Path.GetTempPath(), "PortChecker", "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);

        var assetPath = Path.Combine(workDirectory, GetSafeFileName(package.Asset.Name));
        progress?.Report($"正在下载更新包 {package.Asset.Name}...");
        await DownloadFileAsync(package.Asset.DownloadUrl, assetPath, package.Asset.Size, progress, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report("正在校验更新包...");
        await VerifyDigestAsync(package.Asset, assetPath, cancellationToken).ConfigureAwait(false);

        var payloadPath = assetPath;
        if (package.Mode == PortableZipMode)
        {
            payloadPath = Path.Combine(workDirectory, "payload");
            progress?.Report("正在解压更新包...");
            ZipFile.ExtractToDirectory(assetPath, payloadPath);
        }

        var scriptPath = Path.Combine(workDirectory, "install-update.ps1");
        var logPath = Path.Combine(workDirectory, "install-update.log");
        await File.WriteAllTextAsync(scriptPath, GetInstallScript(), new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);

        var requiresElevation = package.Mode == MsiMode || RequiresElevation(package.TargetPath);
        StartInstallerScript(
            scriptPath,
            package.Mode,
            payloadPath,
            package.TargetPath,
            executablePath,
            logPath,
            workDirectory,
            requiresElevation);

        return new UpdateInstallLaunchResult(package.Asset.Name, logPath, requiresElevation);
    }

    private static UpdatePackage SelectPackage(UpdateCheckResult update)
    {
        if (update.Assets.Count == 0)
        {
            throw new InvalidOperationException("最新 Release 没有可下载的更新包。");
        }

        if (PortableMode.IsEnabled)
        {
            return new UpdatePackage(
                FindAsset(update, asset => asset.Name.EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase)),
                PortableZipMode,
                Path.TrimEndingDirectorySeparator(PortableMode.RootDirectory));
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("无法定位当前程序，不能选择更新包。");
        }

        if (IsProtectedInstallDirectory(Path.GetDirectoryName(executablePath)))
        {
            return new UpdatePackage(
                FindAsset(update, asset => asset.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)),
                MsiMode,
                executablePath);
        }

        return new UpdatePackage(
            FindAsset(update, asset => asset.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)),
            MsiMode,
            executablePath);
    }

    private static ReleaseAsset FindAsset(UpdateCheckResult update, Func<ReleaseAsset, bool> predicate)
    {
        return update.Assets.FirstOrDefault(predicate)
            ?? throw new InvalidOperationException("最新 Release 没有适用于当前安装方式的更新包。");
    }

    private static async Task DownloadFileAsync(
        string url,
        string destinationPath,
        long expectedSize,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength > 0
            ? response.Content.Headers.ContentLength.Value
            : expectedSize;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(destinationPath);

        var buffer = new byte[1024 * 128];
        long downloadedBytes = 0;
        var lastProgressReport = Stopwatch.StartNew();

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            downloadedBytes += read;

            if (totalBytes <= 0 || lastProgressReport.ElapsedMilliseconds < 500)
            {
                continue;
            }

            var percent = Math.Clamp(downloadedBytes * 100 / totalBytes, 0, 100);
            progress?.Report($"正在下载更新包 {percent}%...");
            lastProgressReport.Restart();
        }
    }

    private static async Task VerifyDigestAsync(
        ReleaseAsset asset,
        string filePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(asset.Digest)
            || !asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var expectedDigest = asset.Digest["sha256:".Length..].Trim();
        await using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        var actualDigest = Convert.ToHexString(hash).ToLowerInvariant();

        if (!actualDigest.Equals(expectedDigest, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("更新包校验失败，下载文件可能不完整或已被篡改。");
        }
    }

    private static void StartInstallerScript(
        string scriptPath,
        string mode,
        string payloadPath,
        string targetPath,
        string restartPath,
        string logPath,
        string tempRoot,
        bool requiresElevation)
    {
        var arguments = string.Join(
            " ",
            "-NoProfile",
            "-ExecutionPolicy Bypass",
            "-File",
            QuoteArgument(scriptPath),
            "-Mode",
            QuoteArgument(mode),
            "-PayloadPath",
            QuoteArgument(payloadPath),
            "-TargetPath",
            QuoteArgument(targetPath),
            "-RestartPath",
            QuoteArgument(restartPath),
            "-ProcessId",
            Environment.ProcessId.ToString(),
            "-LogPath",
            QuoteArgument(logPath),
            "-TempRoot",
            QuoteArgument(tempRoot));

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = arguments,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = tempRoot
            };

            if (requiresElevation)
            {
                startInfo.Verb = "runas";
            }

            if (Process.Start(startInfo) is null)
            {
                throw new InvalidOperationException("更新程序未能启动。");
            }
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("已取消管理员权限请求，自动更新未执行。", exception);
        }
    }

    private static bool RequiresElevation(string targetPath)
    {
        if (PrivilegeService.IsRunningAsAdministrator())
        {
            return false;
        }

        var directory = Directory.Exists(targetPath)
            ? targetPath
            : Path.GetDirectoryName(targetPath);

        if (IsProtectedInstallDirectory(directory))
        {
            return true;
        }

        return directory is not null && !CanWriteToDirectory(directory);
    }

    private static bool IsProtectedInstallDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(directory);
        return IsPathUnder(fullPath, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            || IsPathUnder(fullPath, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86))
            || IsPathUnder(fullPath, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    }

    private static bool IsPathUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanWriteToDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var testPath = Path.Combine(directory, $".portchecker-write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testPath, string.Empty);
            File.Delete(testPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd($"PortChecker/{ApplicationInfo.CurrentVersionText}");
        return client;
    }

    private static string GetSafeFileName(string fileName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var safeName = new string(fileName.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(safeName) ? "PortCheckerUpdate" : safeName;
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

    private static string GetInstallScript()
    {
        return """
param(
    [Parameter(Mandatory = $true)]
    [string] $Mode,

    [Parameter(Mandatory = $true)]
    [string] $PayloadPath,

    [Parameter(Mandatory = $true)]
    [string] $TargetPath,

    [Parameter(Mandatory = $true)]
    [string] $RestartPath,

    [Parameter(Mandatory = $true)]
    [int] $ProcessId,

    [Parameter(Mandatory = $true)]
    [string] $LogPath,

    [Parameter(Mandatory = $true)]
    [string] $TempRoot
)

$ErrorActionPreference = "Stop"

function Write-UpdateLog {
    param([string] $Message)
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    Add-Content -LiteralPath $LogPath -Value "[$timestamp] $Message"
}

try {
    Write-UpdateLog "Waiting for Port Checker process $ProcessId to exit."
    $deadline = (Get-Date).AddSeconds(120)
    while ($null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
        if ((Get-Date) -gt $deadline) {
            throw "Port Checker did not exit within 120 seconds."
        }

        Start-Sleep -Milliseconds 500
    }

    Start-Sleep -Milliseconds 800
    Write-UpdateLog "Installing update mode $Mode."

    switch ($Mode) {
        "portableZip" {
            if (-not (Test-Path -LiteralPath $TargetPath -PathType Container)) {
                New-Item -ItemType Directory -Force -Path $TargetPath | Out-Null
            }

            Get-ChildItem -LiteralPath $PayloadPath -Force | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination $TargetPath -Recurse -Force
            }
        }
        "msi" {
            $process = Start-Process -FilePath "msiexec.exe" -ArgumentList @("/i", $PayloadPath, "/passive", "/norestart") -Wait -PassThru
            if ($process.ExitCode -ne 0 -and $process.ExitCode -ne 3010) {
                throw "MSI installer failed with exit code $($process.ExitCode)."
            }
        }
        default {
            throw "Unknown update mode '$Mode'."
        }
    }

    Write-UpdateLog "Update installed."
    if (Test-Path -LiteralPath $RestartPath) {
        Start-Process -FilePath $RestartPath -WorkingDirectory (Split-Path -Path $RestartPath -Parent)
        Write-UpdateLog "Port Checker restarted."
    }

    Start-Sleep -Seconds 2
    Remove-Item -LiteralPath $TempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
catch {
    Write-UpdateLog "Update failed: $($_.Exception.ToString())"
    throw
}
""";
    }

    private sealed record UpdatePackage(ReleaseAsset Asset, string Mode, string TargetPath);
}

internal sealed record UpdateInstallLaunchResult(
    string AssetName,
    string LogPath,
    bool RequiresElevation);
