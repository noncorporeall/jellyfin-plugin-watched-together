using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.WatchedTogether.Configuration;
using Jellyfin.Plugin.WatchedTogether.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchedTogether.Controllers;

/// <summary>
/// The shelves as plain item lists, for apps that build their own home screen from an API endpoint
/// (for example Streamyfin's "custom" home sections). Same items and order as on the web, without
/// the web-only decorations. The viewer always comes from the access token.
/// </summary>
[ApiController]
[Route("WatchedTogether/Shelves")]
[Authorize]
public class ShelvesController : ControllerBase
{
    private readonly IUserManager _userManager;
    private readonly ResultsHandler _results;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShelvesController"/> class.
    /// </summary>
    public ShelvesController(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IDtoService dtoService,
        ISessionManager sessionManager,
        ISyncPlayManager syncPlayManager,
        IApplicationPaths appPaths,
        ILogger<ResultsHandler> logger)
    {
        _userManager = userManager;
        _results = new ResultsHandler(userManager, libraryManager, userDataManager, dtoService, sessionManager, syncPlayManager, appPaths, logger);
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Recently Watched on This Server (live items first).</summary>
    /// <param name="startIndex">Paging offset.</param>
    /// <param name="limit">Page size.</param>
    /// <returns>Items.</returns>
    [HttpGet("Recent")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<QueryResult<BaseItemDto>> GetRecent([FromQuery] int? startIndex, [FromQuery] int? limit)
        => Page(r => _results.GetResults(r), startIndex, limit);

    /// <summary>Most Popular Movies, in rank order.</summary>
    /// <param name="startIndex">Paging offset.</param>
    /// <param name="limit">Page size.</param>
    /// <returns>Items.</returns>
    [HttpGet("PopularMovies")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<QueryResult<BaseItemDto>> GetPopularMovies([FromQuery] int? startIndex, [FromQuery] int? limit)
        => Config.PopularMoviesEnabled ? Page(r => _results.GetPopularMovies(r), startIndex, limit) : Empty();

    /// <summary>Most Popular Shows, in rank order.</summary>
    /// <param name="startIndex">Paging offset.</param>
    /// <param name="limit">Page size.</param>
    /// <returns>Items.</returns>
    [HttpGet("PopularShows")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<QueryResult<BaseItemDto>> GetPopularShows([FromQuery] int? startIndex, [FromQuery] int? limit)
        => Config.PopularShowsEnabled ? Page(r => _results.GetPopularShows(r), startIndex, limit) : Empty();

    private static QueryResult<BaseItemDto> Empty() => new(null, 0, Array.Empty<BaseItemDto>());

    private ActionResult<QueryResult<BaseItemDto>> Page(Func<SectionRequest, QueryResult<BaseItemDto>> source, int? startIndex, int? limit)
    {
        string? raw = User.Claims
            .FirstOrDefault(c => c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
        User? viewer = Guid.TryParse(raw, out Guid id) && id != Guid.Empty ? _userManager.GetUserById(id) : null;
        if (viewer == null)
        {
            return Unauthorized();
        }

        IReadOnlyList<BaseItemDto> all = source(new SectionRequest { UserId = viewer.Id }).Items;

        // Apps page through "infinite" rows; the shelves are short, so later pages are simply empty.
        int skip = Math.Max(0, startIndex ?? 0);
        int take = Math.Clamp(limit ?? all.Count, 0, 100);
        List<BaseItemDto> page = all.Skip(skip).Take(take).ToList();
        return new QueryResult<BaseItemDto>(skip, all.Count, page);
    }
}
