using System.Reflection;
using Jellyfin.Plugin.WatchedTogether.Configuration;
using Jellyfin.Plugin.WatchedTogether.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchedTogether;

/// <summary>
/// Watched Together: a Home Screen Sections shelf showing what other people on the server have been watching.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The section id registered with Home Screen Sections. Also the CSS class on the shelf container.</summary>
    public const string SectionId = "WatchedTogether";

    private readonly ILogger<Plugin> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IServerConfigurationManager serverConfigurationManager,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        ServerConfigurationManager = serverConfigurationManager;
        _logger = logger;

        ConfigurationChanged += OnConfigurationChanged;
    }

    /// <summary>Gets the running plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Watched Together";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("d73b0b70-2b96-4358-861f-78d5edb72753");

    /// <inheritdoc />
    public override string Description => "Adds a home screen shelf showing what other users on the server have watched recently, with their profile pictures on each card.";

    /// <summary>Gets the server configuration manager (used to read the base URL).</summary>
    internal IServerConfigurationManager ServerConfigurationManager { get; }

    /// <summary>Gets a cache-busting token for the injected client files.</summary>
    internal static string ClientVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        };
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        RecentActivityService.InvalidateCache();

        // Re-register so a changed shelf title takes effect without a restart.
        try
        {
            IntegrationRegistrar.RegisterSection(_logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WatchedTogether] Could not re-register the home screen section after a settings change");
        }
    }
}
