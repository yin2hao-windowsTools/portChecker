using System.IO;

namespace PortChecker.Services;

internal static class PortableMode
{
    private const string MarkerFileName = ".portable";
    private const string PortableEnvironmentVariable = "PORTCHECKER_PORTABLE";
    private const string PortableRootEnvironmentVariable = "PORTCHECKER_PORTABLE_ROOT";

    public static bool IsEnabled { get; private set; }
    public static string RootDirectory { get; private set; } = AppContext.BaseDirectory;

    public static void Initialize()
    {
        var resolvedBaseDirectory = ResolvePortableRoot(AppContext.BaseDirectory);
        IsEnabled = resolvedBaseDirectory is not null;
        if (!IsEnabled)
        {
            return;
        }

        var baseDirectory = resolvedBaseDirectory!;
        RootDirectory = baseDirectory;
        Directory.SetCurrentDirectory(baseDirectory);

        var tempDirectory = GetLocalTempDirectory(baseDirectory);
        SetProcessEnvironment("PORTCHECKER_DATA_DIR", Path.Combine(baseDirectory, "data"));
        SetProcessEnvironment(PortableRootEnvironmentVariable, baseDirectory);
        SetProcessEnvironment("DOTNET_BUNDLE_EXTRACT_BASE_DIR", tempDirectory);
        SetProcessEnvironment("TEMP", tempDirectory);
        SetProcessEnvironment("TMP", tempDirectory);
    }

    private static string? ResolvePortableRoot(string baseDirectory)
    {
        var configuredRoot = Environment.GetEnvironmentVariable(PortableRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return configuredRoot;
        }

        if (File.Exists(Path.Combine(baseDirectory, MarkerFileName)))
        {
            return baseDirectory;
        }

        var parentDirectory = Directory.GetParent(Path.TrimEndingDirectorySeparator(baseDirectory))?.FullName;
        if (!string.IsNullOrWhiteSpace(parentDirectory)
            && File.Exists(Path.Combine(parentDirectory, MarkerFileName)))
        {
            return parentDirectory;
        }

        return string.Equals(
            Environment.GetEnvironmentVariable(PortableEnvironmentVariable),
            "1",
            StringComparison.OrdinalIgnoreCase)
            ? baseDirectory
            : null;
    }

    private static string GetLocalTempDirectory(string baseDirectory)
    {
        var tempDirectory = Path.Combine(baseDirectory, "data", "temp");
        try
        {
            Directory.CreateDirectory(tempDirectory);
            return tempDirectory;
        }
        catch (IOException)
        {
            return baseDirectory;
        }
        catch (UnauthorizedAccessException)
        {
            return baseDirectory;
        }
    }

    private static void SetProcessEnvironment(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
    }
}
