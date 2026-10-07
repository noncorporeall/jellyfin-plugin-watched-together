using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.WatchedTogether.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchedTogether.Services;

/// <summary>One person's recent (or current) watch of a shelf item.</summary>
/// <param name="UserId">The watcher.</param>
/// <param name="UserName">The watcher's display name.</param>
/// <param name="ImageTag">Cache tag for their profile picture, or null when they have none.</param>
/// <param name="LastPlayed">When they last played it (UTC); now, for live watchers.</param>
/// <param name="Finished">Whether they finished it.</param>
/// <param name="Detail">Extra context such as "S2:E5" for grouped series.</param>
/// <param name="IsLive">Whether they are playing it right now.</param>
/// <param name="Progress">For live watchers, how far through they are (0–1), when known.</param>
/// <param name="IsPaused">For live watchers, whether playback is paused.</param>
/// <param name="RuntimeSeconds">For live watchers, the item's length, so browsers can animate progress.</param>
public sealed record Watcher(
    Guid UserId,
    string UserName,
    string? ImageTag,
    DateTime LastPlayed,
    bool Finished,
    string? Detail,
    bool IsLive = false,
    double? Progress = null,
    bool IsPaused = false,
    double? RuntimeSeconds = null);

/// <summary>A card on the shelf and the people who watched it.</summary>
/// <param name="ItemId">The item shown on the card (a movie, an episode, or a series when grouped).</param>
/// <param name="Watchers">Who watched it: live watchers first, then most recent first.</param>
public sealed record ActivityEntry(Guid ItemId, IReadOnlyList<Watcher> Watchers)
{
    /// <summary>Gets the most recent play across all watchers.</summary>
    public DateTime LastPlayed => Watchers.Count > 0 ? Watchers.Max(w => w.LastPlayed) : DateTime.MinValue;

    /// <summary>Gets a value indicating whether anyone is watching this right now.</summary>
    public bool HasLive => Watchers.Any(w => w.IsLive);
}

/// <summary>
/// Builds the server-wide "who watched what recently" snapshot from Jellyfin's own
/// per-user play data (no Playback Reporting dependency), overlays who is watching
/// right now from active sessions, then filters it per viewer.
/// </summary>
public class RecentActivityService
{
    private static readonly object CacheLock = new();
    private static List<ActivityEntry>? _cachedSnapshot;
    private static DateTime _cachedAtUtc = DateTime.MinValue;

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecentActivityService"/> class.
    /// </summary>
    public RecentActivityService(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        ISessionManager sessionManager,
        ILogger logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _sessionManager = sessionManager;
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
    /// optionally without their own activity. Items someone is watching right now come first.
    /// </summary>
    /// <param name="viewer">The user looking at their home screen.</param>
    /// <returns>Shelf entries with their watchers.</returns>
    public IReadOnlyList<(BaseItem Item, ActivityEntry Entry)> GetShelfFor(User viewer)
    {
        PluginConfiguration config = Config;
        List<(BaseItem, ActivityEntry)> shelf = new();

        foreach (ActivityEntry entry in MergeLive(GetSnapshot(), config))
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

    /// <summary>
    /// Overlays live sessions on the (cached) history. Never cached itself: sessions change by the second.
    /// </summary>
    private List<ActivityEntry> MergeLive(List<ActivityEntry> history, PluginConfiguration config)
    {
        if (!config.ShowLiveSessions)
        {
            return history;
        }

        List<(Guid CardId, Watcher Watcher)> live;
        try
        {
            live = GetLiveWatchers(config);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WatchedTogether] Failed to read active sessions");
            return history;
        }

        if (live.Count == 0)
        {
            return history;
        }

        // Copy so the cached snapshot is never modified.
        Dictionary<Guid, List<Watcher>> byCard = history.ToDictionary(e => e.ItemId, e => e.Watchers.ToList());
        foreach ((Guid cardId, Watcher watcher) in live)
        {
            if (!byCard.TryGetValue(cardId, out List<Watcher>? list))
            {
                list = new List<Watcher>();
                byCard[cardId] = list;
            }

            // A live watcher replaces that person's older history entry on the same card.
            list.RemoveAll(w => w.UserId == watcher.UserId && !w.IsLive);
            if (!list.Any(w => w.UserId == watcher.UserId))
            {
                list.Add(watcher);
            }
        }

        return byCard
            .Select(kv => new ActivityEntry(
                kv.Key,
                kv.Value.OrderByDescending(w => w.IsLive).ThenByDescending(w => w.LastPlayed).ToList()))
            .OrderByDescending(e => e.HasLive)
            .ThenByDescending(e => e.LastPlayed)
            .ToList();
    }

    private List<(Guid CardId, Watcher Watcher)> GetLiveWatchers(PluginConfiguration config)
    {
        Filters filters = Filters.From(config);
        List<(Guid, Watcher)> result = new();
        HashSet<(Guid UserId, Guid CardId)> seen = new();
        Dictionary<Guid, bool> libraryAllowedCache = new();

        foreach (SessionInfo session in _sessionManager.Sessions)
        {
            if (session.UserId == Guid.Empty || session.NowPlayingItem == null || filters.IsUserExcluded(session.UserId))
            {
                continue;
            }

            BaseItem? item = session.FullNowPlayingItem ?? _libraryManager.GetItemById(session.NowPlayingItem.Id);
            if (item == null || !filters.Kinds.Contains(item.GetBaseItemKind()))
            {
                continue;
            }

            User? user = _userManager.GetUserById(session.UserId);
            if (user == null || user.HasPermission(PermissionKind.IsDisabled))
            {
                continue;
            }

            if (!IsLibraryAllowed(item, filters.ExcludedLibraries, libraryAllowedCache))
            {
                continue;
            }

            (Guid cardId, string? detail) = ResolveCard(item, config);
            if (!seen.Add((user.Id, cardId)))
            {
                continue;
            }

            double? progress = null;
            long? position = session.PlayState?.PositionTicks;
            if (position.HasValue && item.RunTimeTicks is > 0)
            {
                progress = Math.Clamp((double)position.Value / item.RunTimeTicks.Value, 0, 1);
            }

            result.Add((cardId, new Watcher(
                user.Id,
                user.Username,
                ImageTagFor(user),
                DateTime.UtcNow,
                false,
                detail,
                IsLive: true,
                Progress: progress,
                IsPaused: session.PlayState?.IsPaused ?? false,
                RuntimeSeconds: item.RunTimeTicks is > 0 ? item.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond : null)));
        }

        return result;
    }

    private List<ActivityEntry> BuildSnapshot()
    {
        PluginConfiguration config = Config;
        Filters filters = Filters.From(config);
        DateTime cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, config.LookbackDays));

        if (filters.Kinds.Length == 0)
        {
            return new List<ActivityEntry>();
        }

        // key = item shown on the card; value = watcher per user (keep the latest)
        Dictionary<Guid, Dictionary<Guid, Watcher>> grouped = new();
        Dictionary<Guid, bool> libraryAllowedCache = new();

        foreach (User user in _userManager.GetUsers())
        {
            if (filters.IsUserExcluded(user.Id) || user.HasPermission(PermissionKind.IsDisabled))
            {
                continue;
            }

            foreach (BaseItem item in GetRecentlyPlayed(user, filters.Kinds, config))
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

                if (!IsLibraryAllowed(item, filters.ExcludedLibraries, libraryAllowedCache))
                {
                    continue;
                }

                (Guid cardId, string? detail) = ResolveCard(item, config);

                Watcher watcher = new(
                    user.Id,
                    user.Username,
                    ImageTagFor(user),
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

    private static (Guid CardId, string? Detail) ResolveCard(BaseItem item, PluginConfiguration config)
    {
        if (config.GroupEpisodesBySeries && item is Episode episode)
        {
            Guid seriesId = episode.SeriesId != Guid.Empty ? episode.SeriesId : episode.FindSeriesId();
            if (seriesId != Guid.Empty)
            {
                return (seriesId, FormatEpisode(episode));
            }
        }

        return (item.Id, null);
    }

    private static string? ImageTagFor(User user)
    {
        return user.ProfileImage == null
            ? null
            : user.ProfileImage.LastModified.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
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

    /// <summary>The admin's include/exclude settings, parsed once per build.</summary>
    private sealed class Filters
    {
        private HashSet<string> _excludedUsers = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<Guid> ExcludedLibraries { get; private set; } = new();

        public BaseItemKind[] Kinds { get; private set; } = Array.Empty<BaseItemKind>();

        public static Filters From(PluginConfiguration config)
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

            return new Filters
            {
                _excludedUsers = new HashSet<string>(config.ExcludedUserIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
                ExcludedLibraries = (config.ExcludedLibraryIds ?? Array.Empty<string>())
                    .Select(s => Guid.TryParse(s, out Guid g) ? g : Guid.Empty)
                    .Where(g => g != Guid.Empty)
                    .ToHashSet(),
                Kinds = kinds.ToArray()
            };
        }

        public bool IsUserExcluded(Guid userId)
        {
            return _excludedUsers.Contains(userId.ToString()) || _excludedUsers.Contains(userId.ToString("N"));
        }
    }
}
