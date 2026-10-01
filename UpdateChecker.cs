using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace DynamicIsland;

public static class UpdateChecker
{
    private const string LatestReleaseApiUrl =
        "https://api.github.com/repos/kiki-koteyka/CurrencyIsland/releases/latest";

    public sealed record Result(bool UpdateAvailable, string LatestVersion, string ReleaseUrl, string? AssetDownloadUrl, bool IsUrgent, string Notes);

    public static async Task<Result> CheckAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CurrencyIsland-UpdateChecker");

        var json = await client.GetStringAsync(LatestReleaseApiUrl);
        using var doc = JsonDocument.Parse(json);

        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        var url = doc.RootElement.TryGetProperty("html_url", out var urlProp)
            ? urlProp.GetString() ?? ""
            : "";
        var latestVersion = tag.TrimStart('v', 'V');

        var body = doc.RootElement.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";
        var isUrgent = body.TrimStart().StartsWith("URGENT", StringComparison.OrdinalIgnoreCase);

        string? assetUrl = null;
        if (doc.RootElement.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() == "CurrencyIsland.exe")
                {
                    assetUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
        }

        return new Result(IsNewer(latestVersion, AppVersion.Current), latestVersion, url, assetUrl, isUrgent, CleanNotes(body));
    }

    private static string CleanNotes(string body)
    {
        var lines = new System.Collections.Generic.List<string>();
        foreach (var raw in body.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("URGENT", StringComparison.OrdinalIgnoreCase)) line = line[6..].TrimStart(':', ' ', '-');
            if (line.StartsWith("- ") || line.StartsWith("* ")) line = "\u2022 " + line[2..];
            lines.Add(line.Replace("**", "").Replace("`", ""));
        }
        return string.Join("\n", lines).Trim();
    }

    private static bool IsNewer(string latest, string current)
    {
        if (Version.TryParse(PadForVersion(latest), out var latestV)
            && Version.TryParse(PadForVersion(current), out var currentV))
        {
            return latestV > currentV;
        }
        return !string.Equals(latest, current, StringComparison.OrdinalIgnoreCase);
    }

    private static string PadForVersion(string v) => v.Contains('.') ? v : v + ".0";
}
