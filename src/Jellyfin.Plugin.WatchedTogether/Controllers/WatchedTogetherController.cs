using System.Reflection;
using System.Security.Claims;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.WatchedTogether.Configuration;
using Jellyfin.Plugin.WatchedTogether.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchedTogether.Controllers;

/// <summary>
/// Endpoints used by the injected web script.
/// </summary>
[ApiController]
[Route("WatchedTogether")]
public class WatchedTogetherController : ControllerBase
{
    private readonly IUserManager _userManager;
    private readonly RecentActivityService _activity;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchedTogetherController"/> class.
    /// </summary>
    public WatchedTogetherController(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        ISessionManager sessionManager,
        ISyncPlayManager syncPlayManager,
        ILogger<WatchedTogetherController> logger)
    {
        _userManager = userManager;
        _activity = new RecentActivityService(userManager, libraryManager, userDataManager, sessionManager, syncPlayManager, logger);
    }

    /// <summary>
    /// Who watched each card on the signed-in user's shelf. The viewer is always taken from the
    /// access token, never from the request, so nobody can peek at another user's shelf.
    /// </summary>
    /// <returns>Display settings plus a map of item id → watchers.</returns>
    [HttpGet("Watchers")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<WatchersResponse> GetWatchers()
    {
        User? viewer = GetSignedInUser();
        if (viewer == null)
        {
            return Unauthorized();
        }

        PluginConfiguration config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        Dictionary<string, List<WatcherDto>> items = new();

        foreach ((MediaBrowser.Controller.Entities.BaseItem item, ActivityEntry entry) in _activity.GetShelfFor(viewer))
        {
            items[item.Id.ToString("N")] = entry.Watchers.Select(w => new WatcherDto
            {
                UserId = w.UserId.ToString("N"),
                Name = w.UserName,
                ImageTag = w.ImageTag,
                LastPlayed = DateTime.SpecifyKind(w.LastPlayed, DateTimeKind.Utc),
                Finished = w.Finished,
                Detail = w.Detail,
                IsLive = w.IsLive,
                Progress = w.Progress,
                IsPaused = w.IsPaused,
                RuntimeSeconds = w.RuntimeSeconds,
                SyncPlayGroupId = w.SyncPlayGroupId?.ToString("N")
            }).ToList();
        }

        return new WatchersResponse
        {
            SectionId = Plugin.SectionId,
            ShowAvatars = config.ShowAvatars,
            ShowNamesCaption = config.ShowNamesCaption,
            MaxAvatarsPerCard = Math.Clamp(config.MaxAvatarsPerCard, 1, 8),
            AvatarSizePercent = Math.Clamp(config.AvatarSizePercent, 5, 40),
            LiveRefreshSeconds = config.ShowLiveSessions ? 15 : 0,
            WatchTogether = config.ShowLiveSessions && config.EnableWatchTogether,
            ViewerId = viewer.Id.ToString("N"),
            Items = items
        };
    }

    /// <summary>Serves the injected client script.</summary>
    /// <returns>JavaScript.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetClientScript() => Resource("Web.watchedTogether.js", "application/javascript");

    /// <summary>Serves the injected client stylesheet.</summary>
    /// <returns>CSS.</returns>
    [HttpGet("ClientStyle")]
    [AllowAnonymous]
    [Produces("text/css")]
    public ActionResult GetClientStyle() => Resource("Web.watchedTogether.css", "text/css");

    private ActionResult Resource(string relativeName, string contentType)
    {
        Assembly assembly = typeof(Plugin).Assembly;
        Stream? stream = assembly.GetManifestResourceStream($"{typeof(Plugin).Namespace}.{relativeName}");
        if (stream == null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=86400";
        return File(stream, contentType);
    }

    private User? GetSignedInUser()
    {
        string? raw = User.Claims
            .FirstOrDefault(c => c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;

        return Guid.TryParse(raw, out Guid id) && id != Guid.Empty ? _userManager.GetUserById(id) : null;
    }
}

/// <summary>Response for <c>GET /WatchedTogether/Watchers</c>.</summary>
public class WatchersResponse
{
    /// <summary>Gets or sets the section id (the CSS class of the shelf).</summary>
    public string SectionId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether avatars are drawn.</summary>
    public bool ShowAvatars { get; set; }

    /// <summary>Gets or sets a value indicating whether a caption is drawn.</summary>
    public bool ShowNamesCaption { get; set; }

    /// <summary>Gets or sets the avatar cap per card.</summary>
    public int MaxAvatarsPerCard { get; set; }

    /// <summary>Gets or sets the avatar diameter as a percentage of the card's width.</summary>
    public int AvatarSizePercent { get; set; }

    /// <summary>Gets or sets how often the browser re-checks live sessions (0 = off).</summary>
    public int LiveRefreshSeconds { get; set; }

    /// <summary>Gets or sets a value indicating whether live cards show a "Watch together" button.</summary>
    public bool WatchTogether { get; set; }

    /// <summary>Gets or sets the signed-in user's id (no dashes).</summary>
    public string ViewerId { get; set; } = string.Empty;

    /// <summary>Gets or sets item id (no dashes) → watchers, newest first.</summary>
    public Dictionary<string, List<WatcherDto>> Items { get; set; } = new();
}

/// <summary>A watcher as sent to the browser.</summary>
public class WatcherDto
{
    /// <summary>Gets or sets the user id (no dashes).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the profile image cache tag, or null.</summary>
    public string? ImageTag { get; set; }

    /// <summary>Gets or sets when they last played it (UTC).</summary>
    public DateTime LastPlayed { get; set; }

    /// <summary>Gets or sets a value indicating whether they finished it.</summary>
    public bool Finished { get; set; }

    /// <summary>Gets or sets extra context, e.g. "S2:E5".</summary>
    public string? Detail { get; set; }

    /// <summary>Gets or sets a value indicating whether they are watching it right now.</summary>
    public bool IsLive { get; set; }

    /// <summary>Gets or sets how far through they are (0–1) when live.</summary>
    public double? Progress { get; set; }

    /// <summary>Gets or sets a value indicating whether their live playback is paused.</summary>
    public bool IsPaused { get; set; }

    /// <summary>Gets or sets the item's length in seconds when live (for animating progress).</summary>
    public double? RuntimeSeconds { get; set; }

    /// <summary>Gets or sets the SyncPlay group they're watching in, if any (members of one party share it).</summary>
    public string? SyncPlayGroupId { get; set; }
}
