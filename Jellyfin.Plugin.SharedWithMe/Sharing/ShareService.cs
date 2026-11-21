using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SharedWithMe.Configuration;
using Jellyfin.Plugin.SharedWithMe.Model;
using Jellyfin.Plugin.SharedWithMe.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SharedWithMe.Sharing;

/// <summary>What an approval did.</summary>
public enum ApprovalResult
{
    /// <summary>The share was built and is Active.</summary>
    Approved,

    /// <summary>The share was no longer possible (or its end time had passed) and is now Closed.</summary>
    Closed,

    /// <summary>Building it failed; it is still Pending and can be approved again.</summary>
    Failed,

    /// <summary>No pending share has that id, or the caller is not an enabled administrator. Nothing changed.</summary>
    NotFound,
}

/// <summary>What an approval did, in plain words for the administrator.</summary>
/// <param name="Result">What happened.</param>
/// <param name="Message">One plain sentence; empty when approved.</param>
/// <param name="Record">The record as it is now (Active or Closed); null when nothing changed.</param>
public record ApprovalOutcome(ApprovalResult Result, string Message, ShareRecord? Record);

/// <summary>Creates, removes and lists shares. Every change runs under the shared <see cref="PluginGate"/>.</summary>
public class ShareService
{
    /// <summary>What the sharer sees when the share folder is misconfigured. Never carries server paths.</summary>
    public const string SetupProblemMessage = "Sharing isn't set up correctly on this server. Ask the person who runs it to check the Shared With Me settings.";

    /// <summary>What a sharer sees once the plugin is being uninstalled.</summary>
    public const string UninstallingMessage = "Shared With Me is being removed from this server, so nothing can be shared.";

    /// <summary>State reason for a share whose copy could not be removed.</summary>
    public const string CouldNotRemoveReason = "could not remove the shared copy";

    /// <summary>What the sharer sees when the chosen end time has already passed.</summary>
    public const string ExpiryPastMessage = "That end time has already passed. Choose a later one, or keep the share until you remove it.";

    /// <summary>What the sharer sees when the chosen end time is more than <see cref="PluginConfiguration.MaxShareDays"/> days away.</summary>
    public const string ExpiryTooFarMessage = "A share can be kept for at most 366 days. Choose an earlier end date, or keep the share until you remove it.";

    /// <summary>Most search results returned.</summary>
    public const int SearchLimit = 30;

    /// <summary>State reason of a pending share an administrator declined.</summary>
    public const string DeclinedReason = "declined";

    /// <summary>Start of the state reason of a pending share that could not be approved because a rule now refuses it.</summary>
    public const string NoLongerPossiblePrefix = "no longer possible: ";

    /// <summary>The longest decline note kept.</summary>
    public const int DeclineNoteMaxLength = 200;

    /// <summary>What an administrator sees for an approval of an unknown share, or of one that no longer waits.</summary>
    public const string NotPendingMessage = "That share is not waiting for approval.";

    /// <summary>What an administrator sees when a pending share's end time passed before it was approved.</summary>
    public const string ExpiredWhileWaitingMessage = "The time this share was asked for ran out while it waited, so it was closed as expired. The person can share it again.";

    /// <summary>What an administrator sees when building an approved share failed.</summary>
    public const string ApproveFailedMessage = "The share could not be made just now. It is still waiting; try again, and check the server log if it keeps happening.";

    private readonly IServerGateway _gateway;
    private readonly ShareStore _store;
    private readonly ShareEligibility _eligibility;
    private readonly LibraryAccessManager _access;
    private readonly ShareFolderBuilder _builder;
    private readonly Func<string> _shareRoot;
    private readonly PluginGate _gate;
    private readonly ILogger _logger;
    private readonly Action<string> _probe;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<ApprovalMode> _approvalMode;
    private readonly ShareNotifier? _notifier;

    /// <summary>Initializes a new instance of the <see cref="ShareService"/> class.</summary>
    /// <param name="gateway">The server seam.</param>
    /// <param name="store">The share store.</param>
    /// <param name="eligibility">Decides whether a share may happen.</param>
    /// <param name="access">Owns share libraries and library access.</param>
    /// <param name="builder">Builds share folders.</param>
    /// <param name="shareRoot">Reads the current share root folder.</param>
    /// <param name="gate">The process-wide serialiser.</param>
    /// <param name="logger">Logger for details the sharer must not see.</param>
    /// <param name="probeShareRoot">Checks the share root can hold links; defaults to
    /// <see cref="ShareFolderBuilder.ProbeSymlinkSupport"/>.</param>
    /// <param name="utcNow">Clock; defaults to <see cref="DateTime.UtcNow"/>.</param>
    /// <param name="approvalMode">Reads the approval setting; <see cref="ApprovalMode.None"/> when null.</param>
    /// <param name="notifier">Sends the notification events; none when null.</param>
    public ShareService(IServerGateway gateway, ShareStore store, ShareEligibility eligibility, LibraryAccessManager access, ShareFolderBuilder builder, Func<string> shareRoot, PluginGate gate, ILogger logger, Action<string>? probeShareRoot = null, Func<DateTime>? utcNow = null, Func<ApprovalMode>? approvalMode = null, ShareNotifier? notifier = null)
    {
        _approvalMode = approvalMode ?? (() => ApprovalMode.None);
        _notifier = notifier;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _probe = probeShareRoot ?? ShareFolderBuilder.ProbeSymlinkSupport;
        _gateway = gateway;
        _store = store;
        _eligibility = eligibility;
        _access = access;
        _builder = builder;
        _shareRoot = shareRoot;
        _gate = gate;
        _logger = logger;
    }

    /// <summary>Gets the folder a recipient's share library of one kind reads.</summary>
    /// <param name="shareRoot">The share root.</param>
    /// <param name="recipientId">The recipient.</param>
    /// <param name="kind">Movie or series.</param>
    /// <returns>The kind root path.</returns>
    public static string KindRoot(string shareRoot, Guid recipientId, ShareKind kind) =>
        Path.Combine(shareRoot, recipientId.ToString("N"), kind == ShareKind.Series ? "shows" : "movies");

    /// <summary>
    /// Shares an item. Order: the end time, share-root sanity, check, probe the share root, isolate users, build the
    /// folder, ensure the share library, record, queue a scan. Any exception once the folder exists removes the folder and
    /// the record (or keeps the record as Broken when the folder cannot be removed), then rethrows the original exception.
    /// With <see cref="ApprovalMode.LimitedProfiles"/> and a sharer who needs approval (<see cref="NeedsApproval"/>) every
    /// check still runs, so a doomed share is refused at once, but the share is only recorded as Pending: nothing after the
    /// probe happens until <see cref="Approve"/>.
    /// </summary>
    /// <param name="sharerId">Who is sharing.</param>
    /// <param name="itemId">What they are sharing.</param>
    /// <param name="recipientId">Who it is shared with.</param>
    /// <param name="expiresUtc">When the share ends by itself (UTC); null keeps it until it is removed. Refused
    /// (<see cref="Refusal.InvalidExpiry"/>) when it is not after now, or more than
    /// <see cref="PluginConfiguration.MaxShareDays"/> days ahead.</param>
    /// <returns>The decision, and the new record when the share was made.</returns>
    public (Decision decision, ShareRecord? record) Create(Guid sharerId, Guid itemId, Guid recipientId, DateTime? expiresUtc = null) =>
        _gate.Run(() => CreateLocked(sharerId, itemId, recipientId, expiresUtc));

    /// <summary>
    /// Approves a pending share (enabled administrators only): every rule is checked again as of now, then the share is built
    /// by the same code as a new share and becomes Active, keeping its id, its creation time and the end time its sharer
    /// chose. A share whose end time passed while it waited is closed as expired; one a rule now refuses is closed with the
    /// reason "no longer possible: ...". Neither is left waiting.
    /// </summary>
    /// <param name="shareId">The pending share.</param>
    /// <param name="actingUserId">Who is approving.</param>
    /// <returns>What happened.</returns>
    public ApprovalOutcome Approve(Guid shareId, Guid actingUserId) => _gate.Run(() => ApproveLocked(shareId, actingUserId));

    /// <summary>
    /// Declines a pending share (enabled administrators only): it is closed with the reason "declined" and the note, and taken
    /// out of its sharer's "Share with" playlist. Nothing else changes: nothing was built for it.
    /// </summary>
    /// <param name="shareId">The pending share.</param>
    /// <param name="actingUserId">Who is declining.</param>
    /// <param name="note">A short note for the sharer, or null (see <see cref="CleanNote"/>).</param>
    /// <returns>The closed record, or null (nothing changed) when no pending share has that id or the caller may not.</returns>
    public ShareRecord? Decline(Guid shareId, Guid actingUserId, string? note) => _gate.Run(() => DeclineLocked(shareId, actingUserId, note));

    /// <summary>
    /// Removes a share. Only the sharer or an administrator may. A pending share is withdrawn the same way (it has no copy). A share listed in its sharer's "Share with" playlist is
    /// taken out of that playlist first; if that fails nothing is removed (a stale entry would share it again).
    /// </summary>
    /// <param name="shareId">The share.</param>
    /// <param name="actingUserId">Who is asking.</param>
    /// <returns>False, changing nothing, when the share is unknown or the user may not remove it.</returns>
    /// <exception cref="IOException">The copy could not be removed; the record is kept as Broken.</exception>
    public bool Remove(Guid shareId, Guid actingUserId) => _gate.Run(() => RemoveLocked(shareId, actingUserId));

    /// <summary>Lists the shares a user made; every share for an administrator.</summary>
    /// <param name="userId">The user.</param>
    /// <returns>The shares.</returns>
    public IReadOnlyList<ShareRecord> ListFor(Guid userId)
    {
        var user = _gateway.GetUser(userId);
        if (user is null || user.IsDisabled)
        {
            return Array.Empty<ShareRecord>();
        }

        var all = _store.All();
        return user.IsAdmin ? all : all.Where(r => r.SharerUserId == userId).ToList();
    }

    /// <summary>
    /// Lists the profiles a user can share with: enabled and not the user (hidden only means "not on the login
    /// screen"). Empty when the user may not share at all.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The profiles, by name.</returns>
    public IReadOnlyList<UserInfo> RecipientsFor(Guid userId)
    {
        if (!_eligibility.MayShare(userId))
        {
            return Array.Empty<UserInfo>();
        }

        return _gateway.GetUsers()
            .Where(u => u.Id != userId && !u.IsDisabled)
            .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Searches the movies and series a user can see (the server applies that user's restrictions in the query), leaving
    /// out anything inside the share root. Fewer than 2 characters gives an empty list.
    /// </summary>
    /// <param name="callerId">Who is searching.</param>
    /// <param name="term">Search text.</param>
    /// <param name="forUserId">Search as this user instead; accepted only from an enabled administrator.</param>
    /// <returns>Up to <see cref="SearchLimit"/> hits, or null when the caller may not search as <paramref name="forUserId"/>.</returns>
    public IReadOnlyList<SearchHit>? Search(Guid callerId, string? term, Guid? forUserId = null)
    {
        var caller = _gateway.GetUser(callerId);
        if (forUserId is { } other && other != callerId && caller is not { IsAdmin: true, IsDisabled: false })
        {
            return null;
        }

        var text = term?.Trim() ?? string.Empty;
        var target = forUserId ?? callerId;
        if (caller is null || caller.IsDisabled || text.Length < 2 || _gateway.GetUser(target) is not { IsDisabled: false })
        {
            return Array.Empty<SearchHit>();
        }

        // Asked of the server as the target user, so nothing it cannot see is ever counted or returned. Copies are left
        // out by path as well, whichever library they sit in. A few spare results make up for the ones left out.
        var root = _shareRoot();
        return _gateway.SearchVisible(target, text, SearchLimit + 20)
            .Where(h => string.IsNullOrEmpty(h.Path) || !ShareLayout.IsInside(h.Path, root))
            .Take(SearchLimit)
            .ToList();
    }

    private static Decision Refuse(Refusal reason, string message) => new(reason, message, null, new List<string>());

    private (Decision decision, ShareRecord? record) CreateLocked(Guid sharerId, Guid itemId, Guid recipientId, DateTime? expiresUtc)
    {
        if (_gate.IsClosed)
        {
            return (Refuse(Refusal.ShareFolderUnusable, UninstallingMessage), null);
        }

        // The end time first: it says nothing about the item or the people, and nothing may change for a refused one.
        // Any future time is allowed (a share for the next hour is fine); a time at or before now would never be live.
        var now = _utcNow();
        var expires = expiresUtc is { } e ? (e.Kind == DateTimeKind.Local ? e.ToUniversalTime() : DateTime.SpecifyKind(e, DateTimeKind.Utc)) : (DateTime?)null;
        if (expires <= now)
        {
            return (Refuse(Refusal.InvalidExpiry, ExpiryPastMessage), null);
        }

        if (expires > now.AddDays(PluginConfiguration.MaxShareDays))
        {
            return (Refuse(Refusal.InvalidExpiry, ExpiryTooFarMessage), null);
        }

        var (decision, prepared) = Prepare(sharerId, itemId, recipientId, null);
        if (prepared is null)
        {
            return (decision, null);
        }

        var sharer = _gateway.GetUser(sharerId) ?? throw new InvalidOperationException($"Sharer {sharerId} vanished during the share.");
        var record = new ShareRecord(
            Guid.NewGuid(),
            prepared.Item.Id,
            prepared.Kind,
            prepared.Item.Title,
            prepared.Item.Year,
            new Dictionary<string, string>(prepared.Item.ProviderIds),
            prepared.Item.LibraryId,
            sharerId,
            sharer.Name,
            recipientId,
            now,
            ShareState.Active,
            null,
            string.Empty,
            ExpiresUtc: expires);

        // Every rule above has passed, so a doomed share was refused already; one that needs approval is only recorded:
        // nothing on disk, no library, no change to anyone's access or tags.
        if (NeedsApproval(sharer))
        {
            record = record with { State = ShareState.Pending };
            _store.Upsert(record);
            _logger.LogInformation("Shared With Me: share {ShareId} by {Sharer} waits for approval", record.Id, sharer.Name);
            Notify(ShareNotifier.ApprovalNeededEvent, record, prepared.Recipient.Name);
            return (decision, record);
        }

        record = Build(prepared, record);
        Notify(ShareNotifier.SharedEvent, record, prepared.Recipient.Name);
        return (decision, record);
    }

    /// <summary>
    /// Whether a share by this sharer waits for approval: <see cref="ApprovalMode.LimitedProfiles"/> is set, the sharer is
    /// not an administrator, and the sharer is restricted by the test the playlists use
    /// (<see cref="SharePlaylistManager.IsRestricted"/>, which ignores the plugin's own changes).
    /// </summary>
    /// <param name="sharer">The sharer.</param>
    /// <returns>True when the share is created Pending.</returns>
    public bool NeedsApproval(UserInfo sharer) =>
        _approvalMode() == ApprovalMode.LimitedProfiles
        && !sharer.IsAdmin
        && SharePlaylistManager.IsRestricted(sharer, SharePlaylistManager.RealLibrariesToCount(_gateway, _store));

    // Every rule a share must pass before anything changes, in the order Create has always checked them: the share root,
    // eligibility (approving passes the pending record's id, so it does not count as a duplicate of itself), a copy resolved
    // to its original, the share root's links, a disc folder, a series with no shareable episode. Null when refused.
    private (Decision Decision, Prepared? Prepared) Prepare(Guid sharerId, Guid itemId, Guid recipientId, Guid? approving)
    {
        // Before anything else (Check itself classifies libraries by the share root): a share root that overlaps a
        // library would turn that library's files into "share libraries", or the copies into a real library.
        var shareRoot = _shareRoot();
        var overlap = ShareLayout.FindOverlap(_gateway.GetAllLibraryPaths(), shareRoot);
        if (overlap is not null)
        {
            _logger.LogError("Share refused: the share root {ShareRoot} overlaps the library folder {Folder}", shareRoot, overlap);
            return (Refuse(Refusal.ShareFolderUnusable, SetupProblemMessage), null);
        }

        var decision = _eligibility.Check(sharerId, itemId, recipientId, approving);
        if (decision.Refusal != Refusal.None)
        {
            return (decision, null);
        }

        // Check resolves a shared copy to its original; share that. Never record a copy as an original.
        var originalId = decision.OriginalItemId ?? throw new InvalidOperationException("An allowed decision carries no original item.");
        var item = _gateway.GetItem(originalId) ?? throw new InvalidOperationException($"Item {originalId} vanished during the share.");
        if (item.Kind is not { } kind || !_gateway.GetRealLibraryIds().Contains(item.LibraryId))
        {
            return (Refuse(Refusal.ResharedCopy, $"{item.Title} is a shared copy. Share the original title instead."), null);
        }

        try
        {
            _probe(shareRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Share refused: the share root {ShareRoot} cannot hold symbolic links", shareRoot);
            return (Refuse(Refusal.ShareFolderUnusable, SetupProblemMessage), null);
        }

        // A film whose path is a folder (a Blu-ray or DVD folder, BDMV / VIDEO_TS) has no single file to link (M6).
        if (kind == ShareKind.Movie && !string.IsNullOrEmpty(item.Path) && Directory.Exists(item.Path))
        {
            return (Refuse(Refusal.NotShareableType, $"{item.Title} is a disc folder (Blu-ray or DVD), which can't be shared."), null);
        }

        // A series with no episode file would become an empty copy (and be retired by the next sync): refuse before
        // anything changes. The sharer can see the item, so naming it is fine.
        // The original's files are read once: before isolation for a series (to refuse an empty one), after it for a movie.
        // Only the episodes the sharer can see and the recipient does not block are shared (C1).
        var recipientInfo = _gateway.GetUser(recipientId) ?? throw new InvalidOperationException($"Recipient {recipientId} vanished during the share.");
        var media = kind == ShareKind.Series
            ? ShareEligibility.ShareableEpisodes(_gateway, _gateway.GetSourceMedia(item.Id), sharerId, recipientInfo, item.OfficialRating)
            : null;
        if (media is { Episodes.Count: 0 })
        {
            return (Refuse(Refusal.NotShareableType, $"There are no episodes of {item.Title} you can share yet."), null);
        }

        return (decision, new Prepared(decision, item, kind, shareRoot, media, recipientInfo));
    }

    // The one build of a share, for a new share and for an approved one alike: isolate users, build the folder, ensure the
    // share library, record, queue a scan. Any exception once the folder exists removes the folder and the record (or keeps
    // the record as Broken when the folder cannot be removed), then rethrows the original exception.
    private ShareRecord Build(Prepared prepared, ShareRecord record)
    {
        _access.EnsureIsolation();

        var media = prepared.Media ?? _gateway.GetSourceMedia(prepared.Item.Id);
        var kindRoot = KindRoot(prepared.ShareRoot, record.RecipientUserId, prepared.Kind);

        // Build cleans up its own temporary folder when it fails, so nothing is left before the folder exists.
        // Every copy carries its recipient's tag, which every other user blocks (see LibraryAccessManager).
        var tags = prepared.Decision.CopyTags.Append(LibraryAccessManager.RecipientTag(record.RecipientUserId)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var folder = _builder.Build(kindRoot, record, media, prepared.Item.OfficialRating ?? string.Empty, prepared.Decision.CustomRating ?? string.Empty, tags);
        record = record with { FolderName = folder, State = ShareState.Active, StateReason = null };
        try
        {
            var libraryId = _access.EnsureShareLibrary(record.RecipientUserId, prepared.Kind, kindRoot, folder);
            _store.Upsert(record);
            _gateway.QueueScan(libraryId);
        }
        catch
        {
            Undo(kindRoot, record);
            throw;
        }

        return record;
    }

    private ApprovalOutcome ApproveLocked(Guid shareId, Guid actingUserId)
    {
        if (_gate.IsClosed || _gateway.GetUser(actingUserId) is not { IsAdmin: true, IsDisabled: false })
        {
            return new ApprovalOutcome(ApprovalResult.NotFound, NotPendingMessage, null);
        }

        var pending = _store.All().FirstOrDefault(r => r.Id == shareId);
        if (pending is not { State: ShareState.Pending })
        {
            return new ApprovalOutcome(ApprovalResult.NotFound, NotPendingMessage, null);
        }

        var recipientName = _gateway.GetUser(pending.RecipientUserId)?.Name;

        // The end time was chosen by the sharer and runs from when they asked: one that passed while the share waited
        // ends it now, as an expired share, rather than giving a share that is already over.
        if (pending.ExpiresUtc is { } until && until <= _utcNow())
        {
            var expired = Close(pending, ShareSynchronizer.ExpiredReason);
            return new ApprovalOutcome(ApprovalResult.Closed, ExpiredWhileWaitingMessage, expired);
        }

        // Every rule again, as of now: anything that changed since the request (the sharer's limit or permission, the
        // recipient's settings, the title) is decided here, and a share that is no longer possible is closed, not left waiting.
        var (decision, prepared) = Prepare(pending.SharerUserId, pending.OriginalItemId, pending.RecipientUserId, pending.Id);
        if (prepared is null || prepared.Item.Id != pending.OriginalItemId)
        {
            var why = prepared is null ? decision.Message : "the title is no longer the one that was asked for.";
            var closed = Close(pending, NoLongerPossiblePrefix + why.TrimEnd('.'));
            return new ApprovalOutcome(ApprovalResult.Closed, "It was not approved, because it is no longer possible: " + why, closed);
        }

        ShareRecord built;
        try
        {
            built = Build(prepared, pending);
        }
        catch (Exception ex)
        {
            // Build removed what it made; a record it deleted is put back as it was, so the request can be approved again.
            _logger.LogError(ex, "Shared With Me: approving share {ShareId} failed", pending.Id);
            if (_store.All().All(r => r.Id != pending.Id))
            {
                _store.Upsert(pending);
            }

            return new ApprovalOutcome(ApprovalResult.Failed, ApproveFailedMessage, null);
        }

        _logger.LogInformation("Shared With Me: share {ShareId} was approved", built.Id);
        Notify(ShareNotifier.ApprovedEvent, built, recipientName ?? prepared.Recipient.Name);
        return new ApprovalOutcome(ApprovalResult.Approved, string.Empty, built);
    }

    private ShareRecord? DeclineLocked(Guid shareId, Guid actingUserId, string? note)
    {
        if (_gate.IsClosed || _gateway.GetUser(actingUserId) is not { IsAdmin: true, IsDisabled: false })
        {
            return null;
        }

        var pending = _store.All().FirstOrDefault(r => r.Id == shareId);
        if (pending is not { State: ShareState.Pending })
        {
            return null;
        }

        var closed = Close(pending, DeclinedReason, CleanNote(note));
        _logger.LogInformation("Shared With Me: share {ShareId} was declined", closed.Id);
        Notify(ShareNotifier.DeclinedEvent, closed, _gateway.GetUser(closed.RecipientUserId)?.Name ?? string.Empty);
        return closed;
    }

    // A pending share ends: nothing was built, so only the record changes. Its playlist entry is taken out now (best
    // effort); ViaPlaylist stays set, so the playlists pass takes it out again if the server kept it, and lets go of the
    // title only once it is read back gone (the same guard as an expired share: sharing again would not refuse it).
    private ShareRecord Close(ShareRecord pending, string reason, string? note = null)
    {
        var closed = pending with { State = ShareState.Closed, StateReason = reason, ClosedUtc = _utcNow(), DeclineNote = note };
        _store.Upsert(closed);
        if (closed.ViaPlaylist
            && _store.SharePlaylists().TryGetValue(closed.SharerUserId, out var playlists)
            && playlists.TryGetValue(closed.RecipientUserId, out var playlistId))
        {
            try
            {
                _gateway.RemoveFromPlaylist(playlistId, closed.OriginalItemId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Shared With Me: {Title} could not be taken out of playlist {PlaylistId} yet; the next pass retries", closed.Title, playlistId);
            }
        }

        return closed;
    }

    /// <summary>
    /// The decline note as stored: trimmed, control characters (line breaks included) replaced by spaces, runs of spaces
    /// made one, at most <see cref="DeclineNoteMaxLength"/> characters; null when nothing is left. It is plain text and is
    /// only ever shown as text.
    /// </summary>
    /// <param name="note">What the administrator wrote.</param>
    /// <returns>The note, or null.</returns>
    public static string? CleanNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        var text = string.Join(' ', new string(note.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > DeclineNoteMaxLength)
        {
            text = text[..DeclineNoteMaxLength].TrimEnd();
        }

        return text.Length == 0 ? null : text;
    }

    // Never throws into the share path.
    private void Notify(string eventName, ShareRecord record, string recipientName)
    {
        try
        {
            if (_notifier is null)
            {
                return;
            }

            var sharerName = _gateway.GetUser(record.SharerUserId)?.Name ?? record.SharerName;
            _notifier.Notify(eventName, sharerName, recipientName, record.Title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Shared With Me: a notification could not be started");
        }
    }

    // What Prepare found, for Build.
    private sealed record Prepared(Decision Decision, ItemInfo Item, ShareKind Kind, string ShareRoot, SourceMedia? Media, UserInfo Recipient);

    private bool RemoveLocked(Guid shareId, Guid actingUserId)
    {
        if (_gate.IsClosed)
        {
            return false;
        }

        var record = _store.All().FirstOrDefault(r => r.Id == shareId);
        if (record is null)
        {
            return false;
        }

        if (record.SharerUserId != actingUserId && _gateway.GetUser(actingUserId) is not { IsAdmin: true, IsDisabled: false })
        {
            return false;
        }

        if (record.ViaPlaylist
            && _store.SharePlaylists().TryGetValue(record.SharerUserId, out var playlists)
            && playlists.TryGetValue(record.RecipientUserId, out var playlistId))
        {
            _gateway.RemoveFromPlaylist(playlistId, record.OriginalItemId);
        }

        var kindRoot = KindRoot(_shareRoot(), record.RecipientUserId, record.Kind);
        if (!string.IsNullOrEmpty(record.FolderName))
        {
            try
            {
                _builder.Remove(kindRoot, record.FolderName);
            }
            catch (Exception ex)
            {
                // The folder may still be there: keep the record so the copy stays accounted for.
                _logger.LogError(ex, "Could not remove the shared copy {Folder} of share {ShareId}", record.FolderName, record.Id);
                _store.Upsert(record with { State = ShareState.Broken, StateReason = CouldNotRemoveReason });
                throw;
            }
        }

        _store.Remove(record.Id);
        _access.DropShareLibraryIfEmpty(record.RecipientUserId, record.Kind);
        if (_store.Libraries().TryGetValue(record.RecipientUserId, out var kinds)
            && kinds.TryGetValue(record.Kind.ToString(), out var libraryId))
        {
            _gateway.QueueScan(libraryId);
        }

        return true;
    }

    // Best effort, never throws: the caller rethrows the original exception. The folder goes first (that is what a
    // scan could show). If it cannot be removed the record is kept as Broken; otherwise the record, an emptied
    // library and, when the recipient is left with no share library, the recipient's tag blocks are removed.
    private void Undo(string kindRoot, ShareRecord record)
    {
        try
        {
            _builder.Remove(kindRoot, record.FolderName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not remove the copy {Folder} after a failed share; keeping share {ShareId} as Broken", record.FolderName, record.Id);
            try
            {
                _store.Upsert(record with { State = ShareState.Broken, StateReason = CouldNotRemoveReason });
            }
            catch (Exception storeEx)
            {
                _logger.LogError(storeEx, "Could not record share {ShareId} as Broken", record.Id);
            }

            return;
        }

        try
        {
            _store.Remove(record.Id);
            _access.DropShareLibraryIfEmpty(record.RecipientUserId, record.Kind);
            _access.UnblockIfNoPluginItems(record.RecipientUserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cleanup after a failed share {ShareId} did not finish", record.Id);
        }
    }
}
