using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchedTogether.Services;

/// <summary>
/// Shape of the request Home Screen Sections sends to our results method.
/// </summary>
public class SectionRequest
{
    /// <summary>Gets or sets the user whose home screen is being built.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the additional data we registered (unused).</summary>
    public string? AdditionalData { get; set; }
}

/// <summary>
/// Called by Home Screen Sections (via reflection) to fill the shelf.
/// It is constructed with Home Screen Sections' service provider, so only server services are injected.
/// </summary>
public class ResultsHandler
{
    private readonly IUserManager _userManager;
    private readonly IDtoService _dtoService;
    private readonly RecentActivityService _activity;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResultsHandler"/> class.
    /// </summary>
    public ResultsHandler(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IDtoService dtoService,
        ILogger<ResultsHandler> logger)
    {
        _userManager = userManager;
        _dtoService = dtoService;
        _activity = new RecentActivityService(userManager, libraryManager, userDataManager, logger);
    }

    /// <summary>
    /// Returns the shelf's cards for the requesting user.
    /// </summary>
    /// <param name="request">Request from Home Screen Sections.</param>
    /// <returns>Items to render.</returns>
    public QueryResult<BaseItemDto> GetResults(SectionRequest request)
    {
        User? viewer = _userManager.GetUserById(request.UserId);
        if (viewer == null)
        {
            return new QueryResult<BaseItemDto>();
        }

        List<BaseItem> items = _activity.GetShelfFor(viewer).Select(x => x.Item).ToList();

        DtoOptions dtoOptions = new DtoOptions
        {
            Fields = new[] { ItemFields.PrimaryImageAspectRatio },
            ImageTypeLimit = 1,
            ImageTypes = new[] { ImageType.Thumb, ImageType.Backdrop, ImageType.Primary }
        };

        IReadOnlyList<BaseItemDto> dtos = _dtoService.GetBaseItemDtos(items, dtoOptions, viewer);
        return new QueryResult<BaseItemDto>(null, dtos.Count, dtos);
    }
}
