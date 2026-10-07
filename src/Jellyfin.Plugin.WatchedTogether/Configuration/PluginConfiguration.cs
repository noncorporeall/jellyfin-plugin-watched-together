using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.WatchedTogether.Configuration;

/// <summary>
/// Admin settings for the Watched Together shelf.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the title shown above the shelf.</summary>
    public string SectionTitle { get; set; } = "Recently Watched on This Server";

    /// <summary>Gets or sets how far back (in days) activity is considered.</summary>
    public int LookbackDays { get; set; } = 14;

    /// <summary>Gets or sets the maximum number of cards on the shelf.</summary>
    public int MaxItems { get; set; } = 20;

    /// <summary>Gets or sets how many recent items per user are scanned when building the shelf.</summary>
    public int ItemsScannedPerUser { get; set; } = 40;

    /// <summary>Gets or sets a value indicating whether episodes collapse into their series card.</summary>
    public bool GroupEpisodesBySeries { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the viewer's own activity is hidden from their shelf.</summary>
    public bool HideOwnActivity { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether items that were only started (not finished) count.</summary>
    public bool IncludeInProgress { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether movies are included.</summary>
    public bool IncludeMovies { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether TV episodes are included.</summary>
    public bool IncludeEpisodes { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether music videos are included.</summary>
    public bool IncludeMusicVideos { get; set; } = false;

    /// <summary>Gets or sets a value indicating whether profile-picture badges are drawn on the cards.</summary>
    public bool ShowAvatars { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether a "Watched by Alice" caption is added under each card.</summary>
    public bool ShowNamesCaption { get; set; } = true;

    /// <summary>Gets or sets the avatar diameter as a percentage of the card's width (scales with screen size).</summary>
    public int AvatarSizePercent { get; set; } = 16;

    /// <summary>Gets or sets a value indicating whether people watching right now are shown first, with a LIVE badge.</summary>
    public bool ShowLiveSessions { get; set; } = true;

    /// <summary>Gets or sets the maximum avatars drawn per card before collapsing into "+N".</summary>
    public int MaxAvatarsPerCard { get; set; } = 3;

    /// <summary>Gets or sets how long (seconds) the server-wide activity snapshot is cached.</summary>
    public int CacheSeconds { get; set; } = 120;

    /// <summary>Gets or sets user IDs whose activity is never shared.</summary>
    public string[] ExcludedUserIds { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets library (collection folder) IDs whose items never appear on the shelf.</summary>
    public string[] ExcludedLibraryIds { get; set; } = Array.Empty<string>();
}
