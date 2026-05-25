using System;
using System.Reflection;

namespace PortChecker.Services;

internal static class ApplicationInfo
{
    public const string Name = "Port Checker";
    public const string DeveloperName = "yin2hao-windowsTools";
    public const string DeveloperHomeUrl = "https://github.com/yin2hao-windowsTools";
    public const string RepositoryUrl = "https://github.com/yin2hao-windowsTools/portChecker";
    public const string LicenseName = "未声明";
    public const string LicenseDescription = "当前源码仓库未包含 LICENSE 文件；请以仓库发布页或维护者说明为准。";

    public static Version CurrentVersion => typeof(ApplicationInfo).Assembly.GetName().Version ?? new Version(1, 0, 0);

    public static string CurrentVersionText
    {
        get
        {
            var informationalVersion = typeof(ApplicationInfo).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                var metadataIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
                return metadataIndex > 0
                    ? informationalVersion[..metadataIndex]
                    : informationalVersion;
            }

            return FormatVersion(CurrentVersion);
        }
    }

    public static string FormatVersion(Version version)
    {
        var build = version.Build >= 0 ? version.Build : 0;
        var revision = version.Revision >= 0 ? version.Revision : 0;

        return revision > 0
            ? $"{version.Major}.{version.Minor}.{build}.{revision}"
            : $"{version.Major}.{version.Minor}.{build}";
    }
}
