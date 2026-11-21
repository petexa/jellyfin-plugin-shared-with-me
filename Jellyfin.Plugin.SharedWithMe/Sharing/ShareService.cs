using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SharedWithMe.Configuration;
using Jellyfin.Plugin.SharedWithMe.Model;
using Jellyfin.Plugin.SharedWithMe.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SharedWithMe.Sharing;

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
    public ShareService(IServerGateway gateway, ShareStore store, ShareEligibility eligibility, LibraryAccessManager access, ShareFolderBuilder builder, Func<string> shareRoot, PluginGate gate, ILogger logger, Action<string>? probeShareRoot = null, Func<DateTime>? utcNow = null)
    {
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
    /// Removes a share. Only the sharer or an administrator may. A share listed in its sharer's "Share with" playlist is
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

        // Before anything else (Check itself classifies libraries by the share root): a share root that overlaps a
        // library would turn that library's files into "share libraries", or the copies into a real library.
        var shareRoot = _shareRoot();
        var overlap = ShareLayout.FindOverlap(_gateway.GetAllLibraryPaths(), shareRoot);
        if (overlap is not null)
        {
            _logger.LogError("Share refused: the share root {ShareRoot} overlaps the library folder {Folder}", shareRoot, overlap);
            return (Refuse(Refusal.ShareFolderUnusable, SetupProblemMessage), null);
        }

        var decision = _eligibility.Check(sharerId, itemId, recipientId);
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

        _access.EnsureIsolation();

        var sharer = _gateway.GetUser(sharerId) ?? throw new InvalidOperationException($"Sharer {sharerId} vanished during the share.");
        media ??= _gateway.GetSourceMedia(item.Id);
        var kindRoot = KindRoot(shareRoot, recipientId, kind);
        var record = new ShareRecord(
            Guid.NewGuid(),
            item.Id,
            kind,
            item.Title,
            item.Year,
            new Dictionary<string, string>(item.ProviderIds),
            item.LibraryId,
            sharerId,
            sharer.Name,
            recipientId,
            now,
            ShareState.Active,
            null,
            string.Empty,
            ExpiresUtc: expires);

        // Build cleans up its own temporary folder when it fails, so nothing is left before the folder exists.
        // Every copy carries its recipient's tag, which every other user blocks (see LibraryAccessManager).
        var tags = decision.CopyTags.Append(LibraryAccessManager.RecipientTag(recipientId)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var folder = _builder.Build(kindRoot, record, media, item.OfficialRating ?? string.Empty, decision.CustomRating ?? string.Empty, tags);
        record = record with { FolderName = folder };
        try
        {
            var libraryId = _access.EnsureShareLibrary(recipientId, kind, kindRoot, folder);
            _store.Upsert(record);
            _gateway.QueueScan(libraryId);
        }
        catch
        {
            Undo(kindRoot, record);
            throw;
        }

        return (decision, record);
    }

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
