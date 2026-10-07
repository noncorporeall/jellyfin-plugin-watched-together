using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.WatchedTogether.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchedTogether.Services;

/// <summary>Total watch time for one movie or series.</summary>
/// <param name="ItemId">The movie, or the series (episodes are rolled up).</param>
/// <param name="Seconds">Total seconds watched across all users.</param>
/// <param name="Plays">Number of playback sessions recorded.</param>
public sealed record PopularEntry(Guid ItemId, double Seconds, int Plays);

/// <summary>
/// Ranks movies and shows by total watch time, using the Playback Reporting plugin's database.
/// </summary>
public class PopularityService
{
    /// <summary>The section id for the movies shelf.</summary>
    public const string MoviesSectionId = "WatchedTogetherPopularMovies";

    /// <summary>The section id for the shows shelf.</summary>
    public const string ShowsSectionId = "WatchedTogetherPopularShows";

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, (DateTime At, List<PopularEntry> Entries)> Cache = new();
    private static bool _warnedMissingDb;

    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PopularityService"/> class.
    /// </summary>
    public PopularityService(ILibraryManager libraryManager, IApplicationPaths appPaths, ILogger logger)
    {
        _libraryManager = libraryManager;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <summary>Drops cached rankings (called when settings change).</summary>
    public static void InvalidateCache()
    {
        lock (CacheLock)
        {
            Cache.Clear();
        }
    }

    /// <summary>
    /// Gets the shelf for one viewer: the most-watched items they are allowed to see, ranked 1..N.
    /// </summary>
    /// <param name="viewer">The user whose home screen is being built.</param>
    /// <param name="sectionId">Which shelf (<see cref="MoviesSectionId"/> or <see cref="ShowsSectionId"/>).</param>
    /// <returns>Items in rank order.</returns>
    public IReadOnlyList<(BaseItem Item, int Rank, PopularEntry Entry)> GetShelfFor(User viewer, string sectionId)
    {
        PluginConfiguration config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        bool movies = sectionId == MoviesSectionId;
        if ((movies && !config.PopularMoviesEnabled) || (!movies && !config.PopularShowsEnabled))
        {
            return Array.Empty<(BaseItem, int, PopularEntry)>();
        }

        HashSet<Guid> excludedLibraries = ParseGuids(config.ExcludedLibraryIds);
        int count = Math.Clamp(config.PopularCount, 1, 50);
        List<(BaseItem, int, PopularEntry)> result = new();

        foreach (PopularEntry entry in GetRanking(movies, Math.Max(0, config.PopularDays), config))
        {
            BaseItem? item = _libraryManager.GetItemById(entry.ItemId);
            if (item == null || !item.IsVisibleStandalone(viewer))
            {
                continue;
            }

            if (excludedLibraries.Count > 0 && _libraryManager.GetCollectionFolders(item).Any(f => excludedLibraries.Contains(f.Id)))
            {
                continue;
            }

            result.Add((item, result.Count + 1, entry));
            if (result.Count >= count)
            {
                break;
            }
        }

        return result;
    }

    private List<PopularEntry> GetRanking(bool movies, int days, PluginConfiguration config)
    {
        string key = $"{(movies ? "m" : "s")}:{days}";
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < CacheFor)
            {
                return cached.Entries;
            }
        }

        List<PopularEntry> entries;
        try
        {
            entries = BuildRanking(movies, days, config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WatchedTogether] Could not read Playback Reporting data for the popularity shelves");
            entries = new List<PopularEntry>();
        }

        lock (CacheLock)
        {
            Cache[key] = (DateTime.UtcNow, entries);
        }

        return entries;
    }

    private List<PopularEntry> BuildRanking(bool movies, int days, PluginConfiguration config)
    {
        string dbPath = Path.Combine(_appPaths.DataPath, "playback_reporting.db");
        if (!File.Exists(dbPath))
        {
            if (!_warnedMissingDb)
            {
                _warnedMissingDb = true;
                _logger.LogWarning("[WatchedTogether] Playback Reporting's database was not found at {Path}. Install the Playback Reporting plugin for the Most Popular shelves.", dbPath);
            }

            return new List<PopularEntry>();
        }

        HashSet<Guid> excludedUsers = ParseGuids(config.ExcludedUserIds);
        Dictionary<Guid, (double Seconds, int Plays)> totals = new();
        Dictionary<Guid, Guid> episodeToSeries = new();

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        using (SqliteConnection connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT UserId, ItemId, SUM(PlayDuration), COUNT(*) FROM PlaybackActivity " +
                "WHERE ItemType = $type" + (days > 0 ? " AND DateCreated >= $since" : string.Empty) +
                " GROUP BY UserId, ItemId";
            command.Parameters.AddWithValue("$type", movies ? "Movie" : "Episode");
            if (days > 0)
            {
                // Stored as "yyyy-MM-dd HH:mm:ss…", so a string comparison works.
                command.Parameters.AddWithValue("$since", DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            }

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(1) || !Guid.TryParse(reader.GetString(1), out Guid itemId))
                {
                    continue;
                }

                if (!reader.IsDBNull(0) && Guid.TryParse(reader.GetString(0), out Guid userId) && excludedUsers.Contains(userId))
                {
                    continue;
                }

                double seconds = reader.IsDBNull(2) ? 0 : reader.GetDouble(2);
                int plays = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                if (seconds <= 0)
                {
                    continue;
                }

                Guid key = itemId;
                if (!movies)
                {
                    if (!episodeToSeries.TryGetValue(itemId, out Guid seriesId))
                    {
                        seriesId = _libraryManager.GetItemById(itemId) is Episode episode
                            ? (episode.SeriesId != Guid.Empty ? episode.SeriesId : episode.FindSeriesId())
                            : Guid.Empty;
                        episodeToSeries[itemId] = seriesId;
                    }

                    if (seriesId == Guid.Empty)
                    {
                        continue; // episode deleted from the library
                    }

                    key = seriesId;
                }

                totals.TryGetValue(key, out var sum);
                totals[key] = (sum.Seconds + seconds, sum.Plays + plays);
            }
        }

        return totals
            .Select(kv => new PopularEntry(kv.Key, kv.Value.Seconds, kv.Value.Plays))
            .OrderByDescending(e => e.Seconds)
            .ToList();
    }

    private static HashSet<Guid> ParseGuids(string[]? values)
    {
        return (values ?? Array.Empty<string>())
            .Select(s => Guid.TryParse(s, out Guid g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToHashSet();
    }
}
