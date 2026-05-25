using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PortChecker.Services;

internal sealed class ReleaseUpdateService
{
    private static readonly Uri LatestReleaseApiUri = new("https://api.github.com/repos/yin2hao-windowsTools/portChecker/releases/latest");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async Task<UpdateCheckResult> CheckLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUri);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return UpdateCheckResult.NoRelease(ApplicationInfo.CurrentVersionText);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub 返回 {(int)response.StatusCode} {response.ReasonPhrase}。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (release is null || string.IsNullOrWhiteSpace(release.TagName))
        {
            throw new InvalidOperationException("GitHub Release 响应缺少版本标签。");
        }

        var releaseUrl = string.IsNullOrWhiteSpace(release.HtmlUrl)
            ? $"{ApplicationInfo.RepositoryUrl}/releases/latest"
            : release.HtmlUrl;
        var latestVersion = TryParseVersion(release.TagName);
        if (latestVersion is null)
        {
            return new UpdateCheckResult(
                UpdateCheckState.VersionUnknown,
                ApplicationInfo.CurrentVersionText,
                release.TagName,
                release.Name,
                releaseUrl,
                release.PublishedAt);
        }

        var state = CompareVersions(latestVersion, ApplicationInfo.CurrentVersion) > 0
            ? UpdateCheckState.UpdateAvailable
            : UpdateCheckState.UpToDate;

        return new UpdateCheckResult(
            state,
            ApplicationInfo.CurrentVersionText,
            ApplicationInfo.FormatVersion(latestVersion),
            release.Name,
            releaseUrl,
            release.PublishedAt);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd($"PortChecker/{ApplicationInfo.CurrentVersionText}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static Version? TryParseVersion(string tagName)
    {
        var match = Regex.Match(tagName.Trim(), @"\d+(?:\.\d+){0,3}", RegexOptions.CultureInvariant);
        return match.Success && Version.TryParse(match.Value, out var version)
            ? NormalizeVersion(version)
            : null;
    }

    private static int CompareVersions(Version left, Version right)
    {
        return NormalizeVersion(left).CompareTo(NormalizeVersion(right));
    }

    private static Version NormalizeVersion(Version version)
    {
        return new Version(
            version.Major,
            Math.Max(version.Minor, 0),
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));
    }

    private sealed class GitHubReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; init; }
    }
}

internal enum UpdateCheckState
{
    NoRelease,
    VersionUnknown,
    UpToDate,
    UpdateAvailable
}

internal sealed record UpdateCheckResult(
    UpdateCheckState State,
    string CurrentVersionText,
    string? LatestVersionText,
    string? ReleaseName,
    string? ReleaseUrl,
    DateTimeOffset? PublishedAt)
{
    public static UpdateCheckResult NoRelease(string currentVersionText)
    {
        return new UpdateCheckResult(
            UpdateCheckState.NoRelease,
            currentVersionText,
            null,
            null,
            null,
            null);
    }
}
