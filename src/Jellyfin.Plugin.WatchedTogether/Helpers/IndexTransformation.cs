using MediaBrowser.Common.Net;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.WatchedTogether.Helpers;

/// <summary>
/// Payload File Transformation passes to our callback.
/// </summary>
public class TransformationPayload
{
    /// <summary>Gets or sets the file contents being served.</summary>
    [JsonProperty("contents")]
    public string? Contents { get; set; }
}

/// <summary>
/// Adds our stylesheet and script tags to jellyfin-web's index.html as it is served.
/// </summary>
public static class IndexTransformation
{
    private const string Marker = "data-watched-together";

    /// <summary>
    /// File Transformation callback. Must stay public, static, and take a single payload parameter.
    /// </summary>
    /// <param name="payload">The file being served.</param>
    /// <returns>The modified index.html.</returns>
    public static string Transform(TransformationPayload payload)
    {
        string contents = payload.Contents ?? string.Empty;
        if (contents.Contains(Marker, StringComparison.Ordinal))
        {
            return contents;
        }

        string root = string.Empty;
        string? baseUrl = Plugin.Instance?.ServerConfigurationManager.GetNetworkConfiguration().BaseUrl;
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            root = "/" + baseUrl.Trim().Trim('/');
        }

        string v = Uri.EscapeDataString(Plugin.ClientVersion);
        string css = $"<link rel=\"stylesheet\" {Marker} href=\"{root}/WatchedTogether/ClientStyle?v={v}\" />";
        string js = $"<script type=\"text/javascript\" {Marker} src=\"{root}/WatchedTogether/ClientScript?v={v}\" defer></script>";

        return contents
            .Replace("</head>", css + "</head>", StringComparison.Ordinal)
            .Replace("</body>", js + "</body>", StringComparison.Ordinal);
    }
}
