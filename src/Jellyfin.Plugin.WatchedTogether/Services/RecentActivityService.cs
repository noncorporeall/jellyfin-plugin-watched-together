using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.WatchedTogether.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchedTogether.Services;

/// <summary>One person's recent watch of a shelf item.</summary>
/// <param name="UserId">The watcher.</param>
/// <param name="UserName">The watcher's display name.</param>
/// <param name="ImageTag">Cache tag for their profile picture, or null when they have none.</param>
/// <param name="LastPlayed">When they last played it (UTC).</param>
/// <param name="Finished">Whether they finished it.</param>
/// <param name="Detail">Extra context such as "S2:E5" for grouped series.</param>
public sealed record Watcher(Guid UserId, string UserName, string? ImageTag, DateTime LastPlayed, bool Finished, string? Detail);

/// <summary>A card on the shelf and the people who watched it.</summary>
/// <param name="ItemId">The item shown on the card (a movie, an episode, or a series when grouped).</param>
/// <param name="Watchers">Who watched it, most recent first.</param>
public sealed record ActivityEntry(Guid ItemId, IReadOnlyList<Watcher> Watchers)
{
    /// <summary>Gets the most recent play across all watchers.</summary>
    public DateTime LastPlayed => Watchers.Count > 0 ? Watchers[0].LastPlayed : DateTime.MinValue;
}

/// <summary>
/// Builds the server-wide "who watched what recently" snapshot from Jellyfin's own
/// per-user play data (no Playback Reporting dependency), then filters it per viewer.
/// </summary>
public class RecentActivityService
{
    private static readonly object CacheLock = new();
    private static List<ActivityEntry>? _cachedSnapshot;
    private static DateTime _cachedAtUtc = DateTime.MinValue;

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecentActivityService"/> class.
    /// </summary>
    public RecentActivityService(IUserManager userManager, ILibraryManager libraryManager, IUserDataManager userDataManager, ILogger logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Drops the cached snapshot (called when settings change).</summary>
    public static void InvalidateCache()
    {
        lock (CacheLock)
        {
            _cachedSnapshot = null;
        }
    }

    /// <summary>
    /// Gets the shelf for one viewer: only items that viewer is allowed to see,
    /// optionally without their own activity, newest first.
    /// </summary>
    /// <param name="viewer">The user looking at their home screen.</param>
    /// <returns>Shelf entries with their watchers.</returns>
    public IReadOnlyList<(BaseItem Item, ActivityEntry Entry)> GetShelfFor(User viewer)
    {
        PluginConfiguration config = Config;
        List<(BaseItem, ActivityEntry)> shelf = new();

        foreach (ActivityEntry entry in GetSnapshot())
        {
            IReadOnlyList<Watcher> watchers = config.HideOwnActivity
                ? entry.Watchers.Where(w => w.UserId != viewer.Id).ToList()
                : entry.Watchers;

            if (watchers.Count == 0)
            {
                continue;
            }

            BaseItem? item = _libraryManager.GetItemById(entry.ItemId);

            // IsVisibleStandalone covers library access, parental ratings and blocked tags for the viewer.
            if (item == null || !item.IsVisibleStandalone(viewer))
            {
                continue;
            }

            shelf.Add((item, entry with { Watchers = watchers }));
            if (shelf.Count >= Math.Max(1, config.MaxItems))
            {
                break;
            }
        }

        return shelf;
    }

    private List<ActivityEntry> GetSnapshot()
    {
        int cacheSeconds = Math.Max(0, Config.CacheSeconds);
        lock (CacheLock)
        {
            if (_cachedSnapshot != null && DateTime.UtcNow - _cachedAtUtc < TimeSpan.FromSeconds(cacheSeconds))
            {
                return _cachedSnapshot;
            }
        }

        List<ActivityEntry> snapshot;
        try
        {
            snapshot = BuildSnapshot();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WatchedTogether] Failed to build recent activity");
            snapshot = new List<ActivityEntry>();
        }

        lock (CacheLock)
        {
            _cachedSnapshot = snapshot;
            _cachedAtUtc = DateTime.UtcNow;
        }

        return snapshot;
    }

    private List<ActivityEntry> BuildSnapshot()
    {
        PluginConfiguration config = Config;
        DateTime cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, config.LookbackDays));
        HashSet<string> excludedUsers = new(config.ExcludedUserIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        HashSet<Guid> excludedLibraries = (config.ExcludedLibraryIds ?? Array.Empty<string>())
            .Select(s => Guid.TryParse(s, out Guid g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToHashSet();

        BaseItemKind[] kinds = GetIncludedKinds(config);
        if (kinds.Length == 0)
        {
            return new List<ActivityEntry>();
        }

        // key = item shown on the card; value = watcher per user (keep the latest)
        Dictionary<Guid, Dictionary<Guid, Watcher>> grouped = new();
        Dictionary<Guid, bool> libraryAllowedCache = new();

        foreach (User user in _userManager.GetUsers())
        {
            if (excludedUsers.Contains(user.Id.ToString()) || excludedUsers.Contains(user.Id.ToString("N")))
            {
                continue;
            }

            if (user.HasPermission(PermissionKind.IsDisabled))
            {
                continue;
            }

            foreach (BaseItem item in GetRecentlyPlayed(user, kinds, config))
            {
                UserItemData? data = _userDataManager.GetUserData(user, item);
                if (data?.LastPlayedDate == null || data.LastPlayedDate.Value < cutoff)
                {
                    continue;
                }

                if (!data.Played && (!config.IncludeInProgress || data.PlaybackPositionTicks <= 0))
                {
                    continue;
                }

                if (!IsLibraryAllowed(item, excludedLibraries, libraryAllowedCache))
                {
                    continue;
                }

                Guid cardId = item.Id;
                string? detail = null;
                if (config.GroupEpisodesBySeries && item is Episode episode)
                {
                    Guid seriesId = episode.SeriesId != Guid.Empty ? episode.SeriesId : episode.FindSeriesId();
                    if (seriesId != Guid.Empty)
                    {
                        cardId = seriesId;
                        detail = FormatEpisode(episode);
                    }
                }

                Watcher watcher = new(
                    user.Id,
                    user.Username,
                    user.ProfileImage == null ? null : user.ProfileImage.LastModified.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    data.LastPlayedDate.Value,
                    data.Played,
                    detail);

                if (!grouped.TryGetValue(cardId, out Dictionary<Guid, Watcher>? byUser))
                {
                    byUser = new Dictionary<Guid, Watcher>();
                    grouped[cardId] = byUser;
                }

                if (!byUser.TryGetValue(user.Id, out Watcher? existing) || existing.LastPlayed < watcher.LastPlayed)
                {
                    byUser[user.Id] = watcher;
                }
            }
        }

        return grouped
            .Select(kv => new ActivityEntry(kv.Key, kv.Value.Values.OrderByDescending(w => w.LastPlayed).ToList()))
            .OrderByDescending(e => e.LastPlayed)
            .ToList();
    }

    private IEnumerable<BaseItem> GetRecentlyPlayed(User user, BaseItemKind[] kinds, PluginConfiguration config)
    {
        int limit = Math.Clamp(config.ItemsScannedPerUser, 5, 500);

        // Finished items and (optionally) half-watched items, each newest first by this user's play date.
        IEnumerable<BaseItem> finished = Query(user, kinds, limit, isPlayed: true, isResumable: null);
        IEnumerable<BaseItem> started = config.IncludeInProgress
            ? Query(user, kinds, limit, isPlayed: null, isResumable: true)
            : Enumerable.Empty<BaseItem>();

        return finished.Concat(started).DistinctBy(i => i.Id);
    }

    private IReadOnlyList<BaseItem> Query(User user, BaseItemKind[] kinds, int limit, bool? isPlayed, bool? isResumable)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = kinds,
            Recursive = true,
            IsVirtualItem = false,
            IsPlayed = isPlayed,
            IsResumable = isResumable,
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            Limit = limit
        });
    }

    private bool IsLibraryAllowed(BaseItem item, HashSet<Guid> excludedLibraries, Dictionary<Guid, bool> cache)
    {
        if (excludedLibraries.Count == 0)
        {
            return true;
        }

        Guid key = item is Episode ep && ep.SeriesId != Guid.Empty ? ep.SeriesId : item.Id;
        if (cache.TryGetValue(key, out bool allowed))
        {
            return allowed;
        }

        allowed = !_libraryManager.GetCollectionFolders(item).Any(f => excludedLibraries.Contains(f.Id));
        cache[key] = allowed;
        return allowed;
    }

    private static BaseItemKind[] GetIncludedKinds(PluginConfiguration config)
    {
        List<BaseItemKind> kinds = new();
        if (config.IncludeMovies)
        {
            kinds.Add(BaseItemKind.Movie);
        }

        if (config.IncludeEpisodes)
        {
            kinds.Add(BaseItemKind.Episode);
        }

        if (config.IncludeMusicVideos)
        {
            kinds.Add(BaseItemKind.MusicVideo);
        }

        return kinds.ToArray();
    }

    private static string? FormatEpisode(Episode episode)
    {
        int? season = episode.ParentIndexNumber;
        int? number = episode.IndexNumber;
        if (season.HasValue && number.HasValue)
        {
            return $"S{season.Value}:E{number.Value}";
        }

        return number.HasValue ? $"E{number.Value}" : null;
    }
}
