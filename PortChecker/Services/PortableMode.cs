using System.IO;

namespace PortChecker.Services;

internal static class PortableMode
{
    private const string MarkerFileName = ".portable";
    private const string PortableEnvironmentVariable = "PORTCHECKER_PORTABLE";

    public static bool IsEnabled { get; private set; }

    public static void Initialize()
    {
        var baseDirectory = AppContext.BaseDirectory;
        IsEnabled = IsPortableRequested(baseDirectory);
        if (!IsEnabled)
        {
            return;
        }

        Directory.SetCurrentDirectory(baseDirectory);

        var tempDirectory = GetLocalTempDirectory(baseDirectory);
        SetProcessEnvironment("PORTCHECKER_DATA_DIR", Path.Combine(baseDirectory, "data"));
        SetProcessEnvironment("DOTNET_BUNDLE_EXTRACT_BASE_DIR", tempDirectory);
        SetProcessEnvironment("TEMP", tempDirectory);
        SetProcessEnvironment("TMP", tempDirectory);
    }

    private static bool IsPortableRequested(string baseDirectory)
    {
        return string.Equals(
                Environment.GetEnvironmentVariable(PortableEnvironmentVariable),
                "1",
                StringComparison.OrdinalIgnoreCase)
            || File.Exists(Path.Combine(baseDirectory, MarkerFileName));
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
