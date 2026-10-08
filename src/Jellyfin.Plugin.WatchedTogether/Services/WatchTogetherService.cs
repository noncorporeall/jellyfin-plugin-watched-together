using System.Collections.Concurrent;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.SyncPlay;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchedTogether.Services;

/// <summary>Where an invite stands.</summary>
public enum InviteStatus
{
    /// <summary>Waiting for the other person to answer.</summary>
    Pending,

    /// <summary>They said yes; the SyncPlay group exists.</summary>
    Accepted,

    /// <summary>They said not now.</summary>
    Declined,

    /// <summary>Nobody answered in time.</summary>
    Expired,

    /// <summary>Something went wrong setting up the group.</summary>
    Failed
}

/// <summary>A "watch together" request from one user to someone who is watching right now.</summary>
public sealed class Invite
{
    /// <summary>Gets the invite id.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Gets who asked.</summary>
    public Guid FromUserId { get; init; }

    /// <summary>Gets the asker's name.</summary>
    public string FromName { get; init; } = string.Empty;

    /// <summary>Gets the asker's profile image tag.</summary>
    public string? FromImageTag { get; init; }

    /// <summary>Gets the device the asker clicked on (so we join that one).</summary>
    public string? FromDeviceId { get; init; }

    /// <summary>Gets who is being asked.</summary>
    public Guid ToUserId { get; init; }

    /// <summary>Gets the asked person's name.</summary>
    public string ToName { get; init; } = string.Empty;

    /// <summary>Gets the item they were playing when asked.</summary>
    public Guid ItemId { get; init; }

    /// <summary>Gets the item's display name.</summary>
    public string ItemName { get; init; } = string.Empty;

    /// <summary>Gets when the invite was made (UTC).</summary>
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Gets or sets the status.</summary>
    public InviteStatus Status { get; set; } = InviteStatus.Pending;

    /// <summary>Gets or sets the SyncPlay group once accepted.</summary>
    public Guid? GroupId { get; set; }

    /// <summary>Gets or sets a value indicating whether the asker's session was joined server-side.</summary>
    public bool RequesterJoined { get; set; }

    /// <summary>Gets or sets a human-readable reason for Failed.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets how many times the asker's playback has been nudged to start.</summary>
    public int Resyncs { get; set; }
}

/// <summary>Result of asking to watch with someone.</summary>
/// <param name="Outcome">"Invited", "Joined" (they were already in a group) or "Error".</param>
/// <param name="Invite">The invite, when one was created.</param>
/// <param name="Message">Explanation for errors.</param>
public sealed record InviteResult(string Outcome, Invite? Invite, string? Message);

/// <summary>
/// Polite SyncPlay: an invite is shown to the person watching; only when they accept is a group
/// created at their current position, with both of them in it.
/// </summary>
public class WatchTogetherService
{
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan KeepAnswered = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<Guid, Invite> Invites = new();

    private readonly ISessionManager _sessionManager;
    private readonly ISyncPlayManager _syncPlayManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchTogetherService"/> class.
    /// </summary>
    public WatchTogetherService(
        ISessionManager sessionManager,
        ISyncPlayManager syncPlayManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILogger logger)
    {
        _sessionManager = sessionManager;
        _syncPlayManager = syncPlayManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Asks <paramref name="target"/> to watch together. If they are already in a SyncPlay group the
    /// asker simply joins it; otherwise an invite is created for them to accept.
    /// </summary>
    public InviteResult Ask(User from, string? fromDeviceId, User target)
    {
        Sweep();

        if (from.Id == target.Id)
        {
            return new InviteResult("Error", null, "That's you.");
        }

        if (from.SyncPlayAccess == SyncPlayUserAccessType.None)
        {
            return new InviteResult("Error", null, "Your account isn't allowed to use SyncPlay. An admin can enable it in your user settings.");
        }

        SessionInfo? targetSession = FindPlayingSession(target.Id, null);
        if (targetSession?.NowPlayingItem == null)
        {
            return new InviteResult("Error", null, $"{target.Username} isn't watching anything right now.");
        }

        BaseItem? item = targetSession.FullNowPlayingItem ?? _libraryManager.GetItemById(targetSession.NowPlayingItem.Id);
        if (item == null || !item.IsVisibleStandalone(from))
        {
            return new InviteResult("Error", null, "You don't have access to what they're watching.");
        }

        // Already in a group? Join it directly — they've opted into shared viewing.
        SessionInfo? fromSession = FindSession(from.Id, fromDeviceId);
        if (fromSession != null && _syncPlayManager.IsUserActive(target.Id))
        {
            GroupInfoDto? group = _syncPlayManager.ListGroups(fromSession, new ListGroupsRequest())
                .FirstOrDefault(g => g.Participants.Contains(target.Username, StringComparer.OrdinalIgnoreCase));
            if (group != null)
            {
                _syncPlayManager.JoinGroup(fromSession, new JoinGroupRequest(group.GroupId), CancellationToken.None);
                _logger.LogInformation("[WatchedTogether] {From} joined {To}'s existing SyncPlay group", from.Username, target.Username);

                // Recorded as an already-accepted invite so the asker's page can check that playback
                // really starts and nudge it if not (see Resync).
                Invite joined = NewInvite(from, fromSession.DeviceId, target, item);
                joined.Status = InviteStatus.Accepted;
                joined.GroupId = group.GroupId;
                joined.RequesterJoined = true;
                Invites[joined.Id] = joined;
                return new InviteResult("Joined", joined, null);
            }
        }

        // One pending invite per pair at a time.
        Invite? existing = Invites.Values.FirstOrDefault(i =>
            i.FromUserId == from.Id && i.ToUserId == target.Id && i.Status == InviteStatus.Pending);
        if (existing != null)
        {
            return new InviteResult("Invited", existing, null);
        }

        Invite invite = NewInvite(from, fromDeviceId, target, item);
        Invites[invite.Id] = invite;
        _logger.LogInformation("[WatchedTogether] {From} asked to watch {Item} with {To}", from.Username, invite.ItemName, target.Username);
        return new InviteResult("Invited", invite, null);
    }

    /// <summary>Pending invites addressed to a user.</summary>
    public IReadOnlyList<Invite> Incoming(Guid userId)
    {
        Sweep();
        return Invites.Values
            .Where(i => i.ToUserId == userId && i.Status == InviteStatus.Pending)
            .OrderBy(i => i.CreatedUtc)
            .ToList();
    }

    /// <summary>Gets an invite if the user is its sender or recipient.</summary>
    public Invite? Get(Guid inviteId, Guid userId)
    {
        Sweep();
        return Invites.TryGetValue(inviteId, out Invite? invite) && (invite.FromUserId == userId || invite.ToUserId == userId)
            ? invite
            : null;
    }

    /// <summary>
    /// The recipient answers. On yes: their playback is paused and allowed to settle (SyncPlay
    /// starts much more reliably from a paused player), then a SyncPlay group is created from
    /// that session at its exact position and the asker's session is joined to it.
    /// </summary>
    public async Task<Invite?> RespondAsync(Guid inviteId, User recipient, string? recipientDeviceId, bool accept)
    {
        Invite? invite = Get(inviteId, recipient.Id);
        if (invite == null || invite.ToUserId != recipient.Id || invite.Status != InviteStatus.Pending)
        {
            return invite;
        }

        if (!accept)
        {
            invite.Status = InviteStatus.Declined;
            return invite;
        }

        try
        {
            if (recipient.SyncPlayAccess != SyncPlayUserAccessType.CreateAndJoinGroups)
            {
                throw new InvalidOperationException("Your account isn't allowed to create SyncPlay groups.");
            }

            // Prefer the device that answered if it is the one playing; otherwise whichever session is playing.
            SessionInfo session = FindPlayingSession(recipient.Id, recipientDeviceId)
                ?? throw new InvalidOperationException("You're no longer playing anything.");

            await PauseAndSettleAsync(session).ConfigureAwait(false);

            if (session.NowPlayingItem == null)
            {
                throw new InvalidOperationException("You're no longer playing anything.");
            }

            Guid itemId = session.NowPlayingItem.Id;
            long position = session.PlayState?.PositionTicks ?? 0;

            GroupInfoDto group = _syncPlayManager.NewGroup(
                session,
                new NewGroupRequest($"{recipient.Username} & {invite.FromName}"),
                CancellationToken.None);

            _syncPlayManager.HandleRequest(
                session,
                new PlayGroupRequest(new[] { itemId }, 0, position),
                CancellationToken.None);

            invite.GroupId = group.GroupId;
            invite.Status = InviteStatus.Accepted;

            SessionInfo? fromSession = FindSession(invite.FromUserId, invite.FromDeviceId);
            if (fromSession != null)
            {
                _syncPlayManager.JoinGroup(fromSession, new JoinGroupRequest(group.GroupId), CancellationToken.None);
                invite.RequesterJoined = true;
            }

            _logger.LogInformation("[WatchedTogether] {To} accepted {From}'s invite; SyncPlay group {Group} created", recipient.Username, invite.FromName, group.GroupId);
        }
        catch (Exception ex)
        {
            invite.Status = InviteStatus.Failed;
            invite.Message = ex is InvalidOperationException ? ex.Message : "Couldn't start SyncPlay.";
            _logger.LogWarning(ex, "[WatchedTogether] Could not set up SyncPlay for invite {Invite}", invite.Id);
        }

        return invite;
    }

    /// <summary>Whether the asker's device has started playing (used by the asker's page to spot a stalled start).</summary>
    public bool IsRequesterPlaying(Invite invite)
    {
        return invite.Status == InviteStatus.Accepted
            && FindSession(invite.FromUserId, invite.FromDeviceId)?.NowPlayingItem != null;
    }

    /// <summary>
    /// Called by the asker's page when SyncPlay said it was starting but nothing is playing. Puts the
    /// asker's session (back) in the group and restarts the group's queue at the current position,
    /// which gives every member a fresh "start playing" signal and makes the group wait for all of them.
    /// </summary>
    public Invite? Resync(Guid inviteId, User requester, string? requesterDeviceId)
    {
        Invite? invite = Get(inviteId, requester.Id);
        if (invite == null || invite.FromUserId != requester.Id || invite.Status != InviteStatus.Accepted || invite.GroupId == null)
        {
            return invite;
        }

        if (invite.Resyncs >= 3)
        {
            return invite;
        }

        try
        {
            SessionInfo? fromSession = FindSession(requester.Id, requesterDeviceId ?? invite.FromDeviceId);
            if (fromSession == null || fromSession.NowPlayingItem != null)
            {
                return invite;
            }

            invite.Resyncs++;
            Guid groupId = invite.GroupId.Value;
            if (_syncPlayManager.GetGroup(fromSession, groupId) == null)
            {
                invite.Status = InviteStatus.Failed;
                invite.Message = "The watch party has already ended.";
                return invite;
            }

            // Where the group is: the other person's player (paused by SyncPlay while it waits for us).
            SessionInfo? anchor = FindPlayingSession(invite.ToUserId, null);
            Guid itemId = anchor?.NowPlayingItem?.Id ?? invite.ItemId;
            long position = anchor?.PlayState?.PositionTicks ?? 0;

            _syncPlayManager.JoinGroup(fromSession, new JoinGroupRequest(groupId), CancellationToken.None);
            invite.RequesterJoined = true;
            _syncPlayManager.HandleRequest(fromSession, new PlayGroupRequest(new[] { itemId }, 0, position), CancellationToken.None);

            _logger.LogInformation("[WatchedTogether] Nudged SyncPlay start for {From} in group {Group} (attempt {Attempt})", requester.Username, groupId, invite.Resyncs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WatchedTogether] Could not restart SyncPlay for invite {Invite}", invite.Id);
        }

        return invite;
    }

    /// <summary>
    /// Pauses the session if it is playing and waits briefly until it reports being paused, so the
    /// group starts from an exact position and from the same state every time.
    /// </summary>
    private async Task PauseAndSettleAsync(SessionInfo session)
    {
        if (session.PlayState?.IsPaused == true)
        {
            return;
        }

        try
        {
            await _sessionManager.SendPlaystateCommand(
                null!,
                session.Id,
                new PlaystateRequest { Command = PlaystateCommand.Pause },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Some clients can't be remote-controlled; SyncPlay will still try from a playing player.
            _logger.LogDebug(ex, "[WatchedTogether] Could not pause session {Session} before starting SyncPlay", session.Id);
            return;
        }

        for (int i = 0; i < 20 && session.PlayState?.IsPaused != true; i++)
        {
            await Task.Delay(150).ConfigureAwait(false);
        }

        // Let the paused position report land.
        await Task.Delay(250).ConfigureAwait(false);
    }

    private static Invite NewInvite(User from, string? fromDeviceId, User target, BaseItem item) => new()
    {
        FromUserId = from.Id,
        FromName = from.Username,
        FromImageTag = from.ProfileImage?.LastModified.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
        FromDeviceId = fromDeviceId,
        ToUserId = target.Id,
        ToName = target.Username,
        ItemId = item.Id,
        ItemName = DisplayName(item)
    };

    private SessionInfo? FindSession(Guid userId, string? deviceId)
    {
        IEnumerable<SessionInfo> mine = _sessionManager.Sessions.Where(s => s.UserId == userId);
        return (deviceId == null ? null : mine.FirstOrDefault(s => string.Equals(s.DeviceId, deviceId, StringComparison.Ordinal)))
            ?? mine.OrderByDescending(s => s.LastActivityDate).FirstOrDefault();
    }

    private SessionInfo? FindPlayingSession(Guid userId, string? preferredDeviceId)
    {
        List<SessionInfo> playing = _sessionManager.Sessions
            .Where(s => s.UserId == userId && s.NowPlayingItem != null)
            .ToList();
        return playing.FirstOrDefault(s => preferredDeviceId != null && string.Equals(s.DeviceId, preferredDeviceId, StringComparison.Ordinal))
            ?? playing.OrderByDescending(s => s.LastPlaybackCheckIn).FirstOrDefault();
    }

    private static string DisplayName(BaseItem item)
    {
        if (item is MediaBrowser.Controller.Entities.TV.Episode episode && !string.IsNullOrEmpty(episode.SeriesName))
        {
            string number = episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue
                ? $" S{episode.ParentIndexNumber}:E{episode.IndexNumber}"
                : string.Empty;
            return $"{episode.SeriesName}{number}";
        }

        return item.Name;
    }

    private static void Sweep()
    {
        DateTime now = DateTime.UtcNow;
        foreach (Invite invite in Invites.Values)
        {
            if (invite.Status == InviteStatus.Pending && now - invite.CreatedUtc > PendingLifetime)
            {
                invite.Status = InviteStatus.Expired;
            }

            if (invite.Status != InviteStatus.Pending && now - invite.CreatedUtc > KeepAnswered)
            {
                Invites.TryRemove(invite.Id, out _);
            }
        }
    }
}
