using System.Reflection;
using System.Text.Json;
using Jasnote.Core;

namespace Jasnote;

public static class AppInfo
{
    static readonly Lazy<AppInfoConfig> s_config = new(Load);
    static readonly JsonSerializerOptions s_jsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    public static string AppName => s_config.Value.AppName;
    public static string HomeUrl => s_config.Value.WebsiteUrl;
    public static string ReleaseUrl => s_config.Value.WebsiteUrl + "/releases";
    public static string ReportUrl => s_config.Value.WebsiteUrl + "/issues";
    public static string GithubLatestReleaseApiUrl => s_config.Value.GithubLatestReleaseApiUrl;
    public static string GithubUserAgent => s_config.Value.GithubUserAgent;
    public static string Copyright =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
        ?? "";

    static AppInfoConfig Load()
    {
        using var stream =
            Assembly.GetExecutingAssembly().GetManifestResourceStream(GlobalSettings.AppInfo)
            ?? throw new InvalidOperationException(
                $"Missing embedded app info resource: {GlobalSettings.AppInfo}"
            );

        return JsonSerializer.Deserialize<AppInfoConfig>(stream, s_jsonOptions)
            ?? throw new InvalidOperationException(
                $"Invalid app info resource: {GlobalSettings.AppInfo}"
            );
    }

    sealed class AppInfoConfig
    {
        public required string AppName { get; init; }
        public required string WebsiteUrl { get; init; }
        public required string GithubLatestReleaseApiUrl { get; init; }
        public required string GithubUserAgent { get; init; }
    }
}
