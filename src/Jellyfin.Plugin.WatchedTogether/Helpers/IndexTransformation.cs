using System.Reflection;
using System.Text.RegularExpressions;
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
    private static readonly Regex HeadOpen = new Regex("<head[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static string? _tizenBridge;

    /// <summary>
    /// The hosted Samsung TV app's bridge, inlined so it runs before jellyfin-web starts.
    /// It is inert in every other browser and app.
    /// </summary>
    private static string TizenBridge
    {
        get
        {
            if (_tizenBridge == null)
            {
                using Stream? stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream($"{typeof(Plugin).Namespace}.Web.tizenBridge.js");
                using StreamReader? reader = stream == null ? null : new StreamReader(stream);
                _tizenBridge = reader?.ReadToEnd() ?? string.Empty;
            }

            return _tizenBridge;
        }
    }

    /// <summary>True for jellyfin-web's index.html (a document with a head and body), false for scripts.</summary>
    private static bool LooksLikeHtmlPage(string contents)
    {
        string start = contents.Length > 512 ? contents.Substring(0, 512) : contents;
        return start.TrimStart().StartsWith("<", StringComparison.Ordinal)
            && contents.Contains("</head>", StringComparison.OrdinalIgnoreCase)
            && contents.Contains("</body>", StringComparison.OrdinalIgnoreCase);
    }

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

        // File Transformation matches "index.html" as a regex, so it also hands us JavaScript chunks
        // whose names contain "index-html" (e.g. session-login-index-html.*.chunk.js). Only ever
        // touch the real HTML page; anything else goes back byte-for-byte unchanged.
        if (!LooksLikeHtmlPage(contents))
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

        string result = contents
            .Replace("</head>", css + "</head>", StringComparison.Ordinal)
            .Replace("</body>", js + "</body>", StringComparison.Ordinal);

        string bridge = TizenBridge;
        if (bridge.Length > 0)
        {
            string tag = $"<script {Marker}-tizen>{bridge}</script>";
            Match head = HeadOpen.Match(result);
            result = head.Success
                ? result.Insert(head.Index + head.Length, tag)
                : tag + result;
        }

        return result;
    }
}
