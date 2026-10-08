using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.WatchedTogether.Configuration;
using Jellyfin.Plugin.WatchedTogether.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchedTogether.Controllers;

/// <summary>
/// "Watch together" invites and the Most Popular badges. The signed-in user always comes from the
/// access token, never from the request body.
/// </summary>
[ApiController]
[Route("WatchedTogether")]
[Authorize]
public class WatchTogetherController : ControllerBase
{
    private readonly IUserManager _userManager;
    private readonly WatchTogetherService _watchTogether;
    private readonly PopularityService _popularity;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchTogetherController"/> class.
    /// </summary>
    public WatchTogetherController(
        IUserManager userManager,
        ILibraryManager libraryManager,
        ISessionManager sessionManager,
        ISyncPlayManager syncPlayManager,
        IApplicationPaths appPaths,
        ILogger<WatchTogetherController> logger)
    {
        _userManager = userManager;
        _watchTogether = new WatchTogetherService(sessionManager, syncPlayManager, userManager, libraryManager, logger);
        _popularity = new PopularityService(libraryManager, appPaths, logger);
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Ask someone who is watching right now to watch together.</summary>
    /// <param name="request">Who to ask.</param>
    /// <returns>"Joined" when they were already in a SyncPlay group; otherwise the pending invite.</returns>
    [HttpPost("Invites")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<InviteResponse> CreateInvite([FromBody] CreateInviteRequest request)
    {
        User? me = SignedInUser();
        if (me == null)
        {
            return Unauthorized();
        }

        if (!Config.EnableWatchTogether || !Config.ShowLiveSessions)
        {
            return new InviteResponse { Outcome = "Error", Message = "Watching together is turned off on this server." };
        }

        User? target = Guid.TryParse(request.UserId, out Guid targetId) ? _userManager.GetUserById(targetId) : null;
        if (target == null || IsExcluded(target.Id))
        {
            return new InviteResponse { Outcome = "Error", Message = "That person isn't available." };
        }

        InviteResult result = _watchTogether.Ask(me, DeviceId(), target);
        return new InviteResponse
        {
            Outcome = result.Outcome,
            Message = result.Message,
            Invite = result.Invite == null ? null : InviteDto.From(result.Invite)
        };
    }

    /// <summary>Invites waiting for the signed-in user to answer.</summary>
    /// <returns>Pending invites, oldest first.</returns>
    [HttpGet("Invites/Incoming")]
    public ActionResult<IncomingResponse> GetIncoming()
    {
        User? me = SignedInUser();
        if (me == null)
        {
            return Unauthorized();
        }

        bool enabled = Config.EnableWatchTogether && Config.ShowLiveSessions;
        return new IncomingResponse
        {
            Enabled = enabled,
            Invites = enabled ? _watchTogether.Incoming(me.Id).Select(i => InviteDto.From(i)).ToList() : new List<InviteDto>()
        };
    }

    /// <summary>Status of an invite the signed-in user sent or received.</summary>
    /// <param name="id">Invite id.</param>
    /// <returns>The invite.</returns>
    [HttpGet("Invites/{id}")]
    public ActionResult<InviteDto> GetInvite([FromRoute] Guid id)
    {
        User? me = SignedInUser();
        if (me == null)
        {
            return Unauthorized();
        }

        Invite? invite = _watchTogether.Get(id, me.Id);
        return invite == null ? NotFound() : InviteDto.From(invite, _watchTogether.IsRequesterPlaying(invite));
    }

    /// <summary>
    /// The asker's page calls this when SyncPlay said it was starting but nothing began playing on
    /// this device; the server re-joins it and restarts the group's queue at the current position.
    /// </summary>
    /// <param name="id">Invite id.</param>
    /// <returns>The invite.</returns>
    [HttpPost("Invites/{id}/Resync")]
    public ActionResult<InviteDto> Resync([FromRoute] Guid id)
    {
        User? me = SignedInUser();
        if (me == null)
        {
            return Unauthorized();
        }

        Invite? invite = _watchTogether.Resync(id, me, DeviceId());
        return invite == null ? NotFound() : InviteDto.From(invite, _watchTogether.IsRequesterPlaying(invite));
    }

    /// <summary>Accept or decline an invite.</summary>
    /// <param name="id">Invite id.</param>
    /// <param name="request">The answer.</param>
    /// <returns>The updated invite.</returns>
    [HttpPost("Invites/{id}/Respond")]
    public async Task<ActionResult<InviteDto>> Respond([FromRoute] Guid id, [FromBody] RespondRequest request)
    {
        User? me = SignedInUser();
        if (me == null)
        {
            return Unauthorized();
        }

        Invite? invite = await _watchTogether.RespondAsync(id, me, DeviceId(), request.Accept).ConfigureAwait(false);
        return invite == null ? NotFound() : InviteDto.From(invite);
    }

    /// <summary>Rank and watch time for each card on the signed-in user's Most Popular shelves.</summary>
    /// <returns>Section id → (item id → rank info).</returns>
    [HttpGet("Popular")]
    public ActionResult<PopularResponse> GetPopular()
    {
        User? me = SignedInUser();
        if (me == null)
        {
            return Unauthorized();
        }

        PopularResponse response = new()
        {
            Days = Math.Max(0, Config.PopularDays),
            AvatarSizePercent = Math.Clamp(Config.AvatarSizePercent, 5, 40)
        };

        foreach (string sectionId in new[] { PopularityService.MoviesSectionId, PopularityService.ShowsSectionId })
        {
            response.Sections[sectionId] = _popularity.GetShelfFor(me, sectionId).ToDictionary(
                x => x.Item.Id.ToString("N"),
                x => new PopularRankDto { Rank = x.Rank, Hours = Math.Round(x.Entry.Seconds / 3600.0, 1), Plays = x.Entry.Plays });
        }

        return response;
    }

    private bool IsExcluded(Guid userId)
    {
        string[] excluded = Config.ExcludedUserIds ?? Array.Empty<string>();
        return excluded.Any(e => Guid.TryParse(e, out Guid g) && g == userId);
    }

    private User? SignedInUser()
    {
        string? raw = User.Claims
            .FirstOrDefault(c => c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
        return Guid.TryParse(raw, out Guid id) && id != Guid.Empty ? _userManager.GetUserById(id) : null;
    }

    private string? DeviceId()
    {
        return User.Claims
            .FirstOrDefault(c => c.Type.Equals("Jellyfin-DeviceId", StringComparison.OrdinalIgnoreCase))?.Value;
    }
}

/// <summary>Body for <c>POST /WatchedTogether/Invites</c>.</summary>
public class CreateInviteRequest
{
    /// <summary>Gets or sets the user to ask.</summary>
    public string UserId { get; set; } = string.Empty;
}

/// <summary>Body for <c>POST /WatchedTogether/Invites/{id}/Respond</c>.</summary>
public class RespondRequest
{
    /// <summary>Gets or sets a value indicating whether the invite is accepted.</summary>
    public bool Accept { get; set; }
}

/// <summary>Result of creating an invite.</summary>
public class InviteResponse
{
    /// <summary>Gets or sets "Invited", "Joined" or "Error".</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Gets or sets an explanation for errors.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets the invite, when one was created.</summary>
    public InviteDto? Invite { get; set; }
}

/// <summary>Invites waiting for an answer.</summary>
public class IncomingResponse
{
    /// <summary>Gets or sets a value indicating whether the feature is on.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the invites.</summary>
    public List<InviteDto> Invites { get; set; } = new();
}

/// <summary>An invite as sent to the browser.</summary>
public class InviteDto
{
    /// <summary>Gets or sets the id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the asker's id.</summary>
    public string FromUserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the asker's name.</summary>
    public string FromName { get; set; } = string.Empty;

    /// <summary>Gets or sets the asker's profile image tag.</summary>
    public string? FromImageTag { get; set; }

    /// <summary>Gets or sets the asked person's name.</summary>
    public string ToName { get; set; } = string.Empty;

    /// <summary>Gets or sets what they're watching.</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Gets or sets the status.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the SyncPlay group id once accepted.</summary>
    public string? GroupId { get; set; }

    /// <summary>Gets or sets a value indicating whether the asker was joined server-side.</summary>
    public bool RequesterJoined { get; set; }

    /// <summary>Gets or sets an explanation for Failed.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets a value indicating whether the asker's device is playing (after acceptance).</summary>
    public bool RequesterPlaying { get; set; }

    /// <summary>Gets or sets seconds left before a pending invite expires.</summary>
    public int SecondsLeft { get; set; }

    /// <summary>Maps an invite.</summary>
    /// <param name="invite">The invite.</param>
    /// <param name="requesterPlaying">Whether the asker's device is playing.</param>
    /// <returns>The DTO.</returns>
    public static InviteDto From(Invite invite, bool requesterPlaying = false) => new()
    {
        RequesterPlaying = requesterPlaying,
        Id = invite.Id.ToString("N"),
        FromUserId = invite.FromUserId.ToString("N"),
        FromName = invite.FromName,
        FromImageTag = invite.FromImageTag,
        ToName = invite.ToName,
        ItemName = invite.ItemName,
        Status = invite.Status.ToString(),
        GroupId = invite.GroupId?.ToString(),
        RequesterJoined = invite.RequesterJoined,
        Message = invite.Message,
        SecondsLeft = Math.Max(0, 90 - (int)(DateTime.UtcNow - invite.CreatedUtc).TotalSeconds)
    };
}

/// <summary>Response for <c>GET /WatchedTogether/Popular</c>.</summary>
public class PopularResponse
{
    /// <summary>Gets or sets the window in days (0 = all time).</summary>
    public int Days { get; set; }

    /// <summary>Gets or sets the badge size as a % of card width.</summary>
    public int AvatarSizePercent { get; set; }

    /// <summary>Gets or sets section id → (item id → rank info).</summary>
    public Dictionary<string, Dictionary<string, PopularRankDto>> Sections { get; set; } = new();
}

/// <summary>One card's rank.</summary>
public class PopularRankDto
{
    /// <summary>Gets or sets the rank (1 = gold).</summary>
    public int Rank { get; set; }

    /// <summary>Gets or sets total hours watched in the window.</summary>
    public double Hours { get; set; }

    /// <summary>Gets or sets the number of plays.</summary>
    public int Plays { get; set; }
}
