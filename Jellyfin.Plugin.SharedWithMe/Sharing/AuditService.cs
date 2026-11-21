using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SharedWithMe.Model;
using Jellyfin.Plugin.SharedWithMe.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SharedWithMe.Sharing;

/// <summary>A user named on a page.</summary>
/// <param name="Id">User id.</param>
/// <param name="Name">Display name.</param>
public record UserRef(Guid Id, string Name);

/// <summary>One share as the audit page shows it.</summary>
/// <param name="Id">Share id.</param>
/// <param name="Title">Title.</param>
/// <param name="Year">Year, if known.</param>
/// <param name="OfficialRating">The original's own rating, or null when the original is gone.</param>
/// <param name="SharerName">Who shared it (current name, or the name at share time when the sharer is gone).</param>
/// <param name="SharerExists">Whether the sharer still exists.</param>
/// <param name="CreatedUtc">When it was shared.</param>
/// <param name="State">Active, Broken or Closed.</param>
/// <param name="StateReason">Why it is not Active.</param>
/// <param name="VisibleNow">Whether the server says the recipient can see the copy right now.</param>
/// <param name="Pending">The copy is on disk but the server has not scanned it yet (a new share, first check).</param>
/// <param name="NotFound">The copy should be on disk but the server has no item for it, past the first scan.</param>
/// <param name="ExpiresUtc">When the share ends by itself; null until it is removed.</param>
/// <param name="Expired">The share's time is up but the check-shares run has not ended it yet (it does within seconds).</param>
public record AuditShare(Guid Id, string Title, int? Year, string? OfficialRating, string SharerName, bool SharerExists, DateTime CreatedUtc, ShareState State, string? StateReason, bool VisibleNow, bool Pending, bool NotFound, DateTime? ExpiresUtc = null, bool Expired = false);

/// <summary>One profile on the audit page.</summary>
/// <param name="User">The profile.</param>
/// <param name="CapLabel">The rating name of its age limit ("12"), or "no limit".</param>
/// <param name="Shares">Shares it received, newest first.</param>
public record AuditProfile(UserRef User, string CapLabel, IReadOnlyList<AuditShare> Shares);

/// <summary>Everything the audit page shows except the counters.</summary>
/// <param name="Profiles">Every profile, by name, then any deleted recipient that still has records.</param>
/// <param name="SwitchedUsers">Profiles the plugin moved off "all libraries" (they have a snapshot).</param>
/// <param name="InCharge">Whether the plugin currently manages library access.</param>
/// <param name="SetupProblem">The current share-root problem in plain words, or null.</param>
/// <param name="ShareRootLocked">Whether a change of the share folder setting would be refused (while in charge).</param>
/// <param name="NarrowedUsers">Profiles the plugin moved whose library list was changed since: a restore or an uninstall
/// leaves them on that list rather than putting them back on "all libraries".</param>
public record AuditReport(IReadOnlyList<AuditProfile> Profiles, IReadOnlyList<UserRef> SwitchedUsers, bool InCharge, string? SetupProblem, bool ShareRootLocked, IReadOnlyList<UserRef>? NarrowedUsers = null)
{
    /// <summary>Gets the profiles a restore would leave on their own list (never null).</summary>
    public IReadOnlyList<UserRef> NarrowedUsers { get; init; } = NarrowedUsers ?? Array.Empty<UserRef>();
}

/// <summary>The shares a user made that they may see, and how many more are hidden from them.</summary>
/// <param name="Shares">Shares whose original or copy the user can still see.</param>
/// <param name="HiddenCount">Shares the user made whose title they can no longer see (a number only).</param>
public record SharedByResult(IReadOnlyList<ShareRecord> Shares, int HiddenCount);

/// <summary>What a restore did.</summary>
/// <param name="Done">True when every user was restored and the plugin is no longer in charge.</param>
/// <param name="Message">Why it was refused or stopped; null when done.</param>
/// <param name="RestoredUsers">Profiles put back on "all libraries".</param>
/// <param name="Summary">Uninstall only: one line saying exactly what was left behind.</param>
/// <param name="LeftOnOwnList">Profiles left on their own library list because it was changed after the plugin moved them.</param>
public record RestoreOutcome(bool Done, string? Message, IReadOnlyList<UserRef> RestoredUsers, string? Summary = null, IReadOnlyList<UserRef>? LeftOnOwnList = null);

/// <summary>
/// The one record of everything an uninstall did not complete, for its last log line. "Left nothing behind" is said only
/// when every step ran and succeeded.
/// </summary>
public sealed class Leftovers
{
    /// <summary>Gets the share libraries not removed (folder paths, or a note when they could not be listed).</summary>
    public List<string> Libraries { get; } = new();

    /// <summary>Gets the copy and recipient folders not removed.</summary>
    public List<string> Folders { get; } = new();

    /// <summary>Gets the plugin playlists not removed (owner and name).</summary>
    public List<string> Playlists { get; } = new();

    /// <summary>Gets the profiles not put back on "all libraries".</summary>
    public List<string> NotRestored { get; } = new();

    /// <summary>Gets the profiles left on their own list on purpose: it was changed after the plugin moved them (not a failure).</summary>
    public List<string> LeftOnOwnList { get; } = new();

    /// <summary>Gets or sets why nobody could be checked or put back; null when each profile was handled on its own.</summary>
    public string? NotRestoredReason { get; set; }

    /// <summary>Gets the profiles whose plugin tags could not be lifted (a failed write).</summary>
    public List<string> TagsNotLifted { get; } = new();

    /// <summary>Gets or sets why no profile's tags were lifted at all; null when tags were lifted.</summary>
    public string? TagsNotLiftedReason { get; set; }

    /// <summary>Gets the owners (share library or playlist left) whose tag every other profile still blocks.</summary>
    public List<string> KeptOwnerTags { get; } = new();

    /// <summary>Gets the individual policy writes that failed (profile and which write).</summary>
    public List<string> FailedWrites { get; } = new();

    /// <summary>Gets or sets where the share record was renamed to; null when it was not.</summary>
    public string? RenamedTo { get; set; }

    /// <summary>Gets or sets a value indicating whether a share record existed but could not be renamed.</summary>
    public bool RenameFailed { get; set; }

    /// <summary>Gets or sets how many steps failed in all (each is logged on its own line).</summary>
    public int FailedSteps { get; set; }

    /// <summary>The summary line.</summary>
    /// <returns>One line.</returns>
    public string Summary()
    {
        var clean = FailedSteps == 0 && Libraries.Count == 0 && Folders.Count == 0 && Playlists.Count == 0
            && NotRestored.Count == 0 && NotRestoredReason is null && TagsNotLifted.Count == 0 && TagsNotLiftedReason is null
            && KeptOwnerTags.Count == 0 && FailedWrites.Count == 0 && !RenameFailed;
        static string List(IEnumerable<string> items)
        {
            var all = items.Distinct().ToList();
            return all.Count == 0 ? "none" : string.Join(", ", all);
        }

        var ownList = LeftOnOwnList.Count == 0 ? string.Empty : $" Profiles {LibraryAccessManager.LeftOnOwnListReason}: {List(LeftOnOwnList)}.";
        if (clean)
        {
            return "Shared With Me uninstall left nothing behind." + (RenamedTo is null ? string.Empty : $" The share record was renamed to {RenamedTo}.") + ownList;
        }

        var notRestored = NotRestoredReason is null
            ? List(NotRestored)
            : (NotRestored.Count == 0 ? "none could be checked" : List(NotRestored)) + $" ({NotRestoredReason})";
        string tags;
        if (TagsNotLiftedReason is not null)
        {
            tags = $"all profiles (tags were not lifted because {TagsNotLiftedReason})";
        }
        else
        {
            var parts = new List<string>();
            if (TagsNotLifted.Count > 0)
            {
                parts.Add(List(TagsNotLifted));
            }

            if (KeptOwnerTags.Count > 0)
            {
                parts.Add($"every profile still blocks the tags of {List(KeptOwnerTags)} (a share library or playlist of theirs remains)");
            }

            tags = parts.Count == 0 ? "none" : string.Join("; ", parts);
        }

        var record = RenamedTo is not null ? $"renamed to {RenamedTo}" : RenameFailed ? "NOT renamed" : "there was none";
        return "Shared With Me uninstall did not finish everything. "
            + $"Libraries not removed: {List(Libraries)}. "
            + $"Copy folders not removed: {List(Folders)}. "
            + $"Playlists not removed: {List(Playlists)}. "
            + $"Profiles not put back on all libraries: {notRestored}. "
            + $"Profiles whose swm- blocked tags were not lifted: {tags}. "
            + $"Policy writes that failed: {List(FailedWrites)}. "
            + $"Share record: {record}. "
            + $"Failed steps: {FailedSteps} (each logged above)."
            + ownList;
    }
}

/// <summary>A share as its recipient sees it.</summary>
/// <param name="Id">Share id.</param>
/// <param name="Title">Title.</param>
/// <param name="Year">Year, if known.</param>
/// <param name="SharerName">Who shared it.</param>
/// <param name="CreatedUtc">When.</param>
/// <param name="ExpiresUtc">When it ends by itself; null until it is removed.</param>
public record ReceivedShare(Guid Id, string Title, int? Year, string SharerName, DateTime CreatedUtc, DateTime? ExpiresUtc = null);

/// <summary>
/// What the two pages show about shares, and the administrator's restore. Every method runs under the
/// <see cref="PluginGate"/>; nothing here reveals a title to someone who can see neither the original nor the copy.
/// </summary>
public class AuditService
{
    /// <summary>Refusal while any share is Active.</summary>
    public const string SharesExistMessage = "Remove every share first, then restore.";

    /// <summary>Restore stopped because a copy stayed on disk.</summary>
    public const string CopyNotRemovedMessage = "A shared copy could not be removed, so nobody's access was restored. Check the server log, then try again.";

    /// <summary>Restore stopped because a share library could not be removed.</summary>
    public const string LibraryNotRemovedMessage = "A share library could not be removed. Nothing was restored.";

    /// <summary>How long a copy the server has not found yet counts as "pending" rather than "not found".</summary>
    public static readonly TimeSpan PendingWindow = TimeSpan.FromMinutes(10);

    /// <summary>Restore stopped because a library in the share folder also reads other folders.</summary>
    public const string MixedLibraryMessage = "A library in the share folder also reads other folders, so nobody's access was restored. Remove that library under Libraries, then restore again.";

    /// <summary>Restore stopped because a library in the share folder has no id yet.</summary>
    public const string LibraryNotReadyMessage = "A library in the share folder isn't ready yet, so nobody's access was restored. Run a library scan, then restore again.";

    /// <summary>Restore refused because the plugin is being uninstalled (the uninstall does its own, forced, restore).</summary>
    public const string UninstallingMessage = "Shared With Me is being uninstalled; it is already putting everything back.";

    /// <summary>State reason for a share ended by an uninstall.</summary>
    public const string UninstalledReason = "the plugin was uninstalled";

    /// <summary>Restore ran but the plugin still manages access.</summary>
    public const string NotFinishedMessage = "Restore did not finish: some of the plugin's changes are still in place. Try again, and check the server log if it keeps happening.";

    private readonly IServerGateway _gateway;
    private readonly ShareStore _store;
    private readonly LibraryAccessManager _access;
    private readonly ShareFolderBuilder _builder;
    private readonly ShareSynchronizer _sync;
    private readonly Func<string> _shareRoot;
    private readonly PluginGate _gate;
    private readonly ILogger _logger;
    private readonly Func<DateTime> _utcNow;
    private readonly SharePlaylistManager? _playlists;
    private readonly Func<bool>? _turnOffTvSharing;
    private readonly Func<string>? _defaultShareRoot;

    /// <summary>Gets or sets how the uninstall lists a folder's entries (a seam so tests can make it fail).</summary>
    public Func<string, IEnumerable<string>> ListFolder { get; set; } = Directory.EnumerateFileSystemEntries;

    /// <summary>Initializes a new instance of the <see cref="AuditService"/> class.</summary>
    /// <param name="gateway">The server seam.</param>
    /// <param name="store">The share store.</param>
    /// <param name="access">Owns share libraries and library access.</param>
    /// <param name="builder">Removes share folders.</param>
    /// <param name="sync">The synchronizer (copy lookup, in-charge and share-root checks).</param>
    /// <param name="shareRoot">Reads the current share root folder.</param>
    /// <param name="gate">The process-wide serialiser.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="utcNow">Clock; defaults to <see cref="DateTime.UtcNow"/>.</param>
    /// <param name="playlists">The "Share with" playlists, removed by a restore; null when there are none to manage.</param>
    /// <param name="turnOffTvSharing">Turns the TV-sharing setting off, so a restore's removed playlists stay gone; returns
    /// true when it was on.</param>
    /// <param name="defaultShareRoot">The share root an empty setting means (&lt;data path&gt;/shared-with-me); an uninstall
    /// deletes the share root itself only when it is this folder and empty.</param>
    public AuditService(IServerGateway gateway, ShareStore store, LibraryAccessManager access, ShareFolderBuilder builder, ShareSynchronizer sync, Func<string> shareRoot, PluginGate gate, ILogger logger, Func<DateTime>? utcNow = null, SharePlaylistManager? playlists = null, Func<bool>? turnOffTvSharing = null, Func<string>? defaultShareRoot = null)
    {
        _defaultShareRoot = defaultShareRoot;
        _playlists = playlists;
        _turnOffTvSharing = turnOffTvSharing;
        _gateway = gateway;
        _store = store;
        _access = access;
        _builder = builder;
        _sync = sync;
        _shareRoot = shareRoot;
        _gate = gate;
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Builds the audit page's data. Visibility is asked of the server now, never read from settings.</summary>
    /// <returns>The report.</returns>
    /// <remarks>An unreadable store gives the profiles with no shares and the store problem as the setup problem.</remarks>
    public AuditReport Build() => _gate.Run(() =>
    {
        var users = _gateway.GetUsers();
        if (!_store.IsReadable(out _))
        {
            var ratingsNow = _gateway.GetRatings();
            var bare = users.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
                .Select(u => new AuditProfile(new UserRef(u.Id, u.Name), CapLabel(u, ratingsNow), Array.Empty<AuditShare>()))
                .ToList();
            return new AuditReport(bare, Array.Empty<UserRef>(), true, ShareSynchronizer.StoreUnreadableMessage, true);
        }

        var ratings = _gateway.GetRatings();
        var records = _store.All();
        var libraries = _store.Libraries();
        var profiles = users
            .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .Select(u => new AuditProfile(new UserRef(u.Id, u.Name), CapLabel(u, ratings), Rows(records.Where(r => r.RecipientUserId == u.Id), u.Id, libraries)))
            .ToList();

        // A recipient deleted since: its records stay listed until the next sync removes them.
        foreach (var gone in records.Select(r => r.RecipientUserId).Distinct().Where(id => users.All(u => u.Id != id)))
        {
            profiles.Add(new AuditProfile(new UserRef(gone, "(deleted profile)"), "unknown", Rows(records.Where(r => r.RecipientUserId == gone), gone, libraries)));
        }

        var snapshots = _store.Snapshots();
        var switched = snapshots
            .Select(s => new UserRef(s.UserId, users.FirstOrDefault(u => u.Id == s.UserId)?.Name ?? "(deleted profile)"))
            .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var real = _gateway.GetRealLibraryIds();
        var narrowed = snapshots
            .Where(s => s.HadAllLibraries)
            .Select(s => (Snapshot: s, User: users.FirstOrDefault(u => u.Id == s.UserId)))
            .Where(x => x.User is { AllLibraries: false } u && !LibraryAccessManager.StillHolds(u, x.Snapshot, real))
            .Select(x => new UserRef(x.User!.Id, x.User.Name))
            .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var inCharge = _sync.IsInCharge();
        return new AuditReport(profiles, switched, inCharge, _sync.CurrentSetupProblem(), inCharge, narrowed);
    });

    /// <summary>
    /// Puts every user the plugin isolated back on "all libraries" and removes every trace of sharing, in an order where
    /// no step can show anyone more: refuse while a share is Active or the share folder setting is wrong; turn sharing
    /// from TV apps off and remove every plugin playlist; remove every copy still on disk (a failure stops here); remove any share-layout library the store did not know; then restore
    /// access, and check the plugin is no longer in charge.
    /// </summary>
    /// <returns>The outcome.</returns>
    public RestoreOutcome Restore() => _gate.Run(() => _gate.IsClosed
        ? new RestoreOutcome(false, UninstallingMessage, Array.Empty<UserRef>())
        : TeardownLocked(forced: false));

    /// <summary>
    /// The uninstall's forced restore: the same teardown as <see cref="Restore"/>, but it never refuses (Active shares
    /// included) and a failed step is logged and the rest go on, because an uninstall cannot be retried. In order:
    /// nothing changes anything any more (the gate is closed for good); every plugin playlist goes; every copy and
    /// left-over build folder under the share root goes; every share library goes; plugin tags are lifted, except the tag
    /// of a user who still has a share library or plugin playlist; everyone the plugin moved goes back to "all libraries"
    /// unless a share library is still there (then nobody does, and the log names them); the empty recipient folders go,
    /// and the share root itself when it is the default location and empty; <c>shares.json</c> is renamed
    /// <c>shares.uninstalled-&lt;UTC time&gt;.json</c> as a record for the administrator.
    /// </summary>
    /// <returns>Done when every step went through; otherwise the message lists what was left.</returns>
    public RestoreOutcome Uninstall() => _gate.Run(() => TeardownLocked(forced: true));

    /// <summary>Active shares a user received whose copy the server says they can see now, leaving out any whose time is up.</summary>
    /// <param name="userId">The recipient.</param>
    /// <returns>The shares, newest first.</returns>
    public IReadOnlyList<ReceivedShare> Received(Guid userId) => _gate.Run<IReadOnlyList<ReceivedShare>>(() =>
    {
        if (_gateway.GetUser(userId) is not { IsDisabled: false })
        {
            return Array.Empty<ReceivedShare>();
        }

        var libraries = _store.Libraries();
        var now = _utcNow();
        return _store.All()
            .Where(r => r.RecipientUserId == userId && r.State == ShareState.Active && !r.IsExpired(now) && CopyVisible(r, userId, libraries))
            .OrderByDescending(r => r.CreatedUtc)
            .Select(r => new ReceivedShare(r.Id, r.Title, r.Year, _gateway.GetUser(r.SharerUserId)?.Name ?? r.SharerName, r.CreatedUtc, r.ExpiresUtc))
            .ToList();
    });

    /// <summary>Shares a user made, limited to titles the server says they can still see (the original or the copy).</summary>
    /// <param name="userId">The sharer.</param>
    /// <returns>The records, and how many of the user's shares were left out.</returns>
    public SharedByResult SharedBy(Guid userId) => _gate.Run(() =>
    {
        if (_gateway.GetUser(userId) is not { IsDisabled: false })
        {
            return new SharedByResult(Array.Empty<ShareRecord>(), 0);
        }

        var mine = _store.All().Where(r => r.SharerUserId == userId).ToList();
        var libraries = _store.Libraries();
        var visible = mine.Where(r => _gateway.CanSee(userId, r.OriginalItemId) || CopyVisible(r, userId, libraries)).ToList();
        return new SharedByResult(visible, mine.Count - visible.Count);
    });

    private static string CapLabel(UserInfo u, IReadOnlyList<RatingOption> ratings)
    {
        if (u.CapScore is not { } cap)
        {
            return "no limit";
        }

        return RatingChooser.Choose(ratings, cap, u.CapSubScore) ?? $"age limit {cap}";
    }

    private List<AuditShare> Rows(IEnumerable<ShareRecord> records, Guid recipientId, IReadOnlyDictionary<Guid, Dictionary<string, Guid>> libraries)
    {
        var now = _utcNow();
        return records
            .OrderByDescending(r => r.CreatedUtc)
            .Select(r =>
            {
                var (visible, pending, notFound) = Visibility(r, recipientId, libraries);
                var sharer = _gateway.GetUser(r.SharerUserId);
                return new AuditShare(
                    r.Id,
                    r.Title,
                    r.Year,
                    _gateway.GetItem(r.OriginalItemId)?.OfficialRating,
                    sharer?.Name ?? r.SharerName,
                    sharer is not null,
                    r.CreatedUtc,
                    r.State,
                    r.StateReason,
                    visible,
                    pending,
                    notFound,
                    r.ExpiresUtc,
                    r.IsExpired(now));
            })
            .ToList();
    }

    // The server's answer for the copy. A copy the server has no item for is "pending" only for a new share before its
    // first missed check; after that it is "not found". Anything unknown is "not visible".
    private (bool Visible, bool Pending, bool NotFound) Visibility(ShareRecord r, Guid userId, IReadOnlyDictionary<Guid, Dictionary<string, Guid>> libraries)
    {
        if (string.IsNullOrEmpty(r.FolderName))
        {
            return (false, false, false);
        }

        try
        {
            if (_sync.FindCopy(r, libraries) is { } copy)
            {
                if (r.Kind != ShareKind.Series)
                {
                    return (_gateway.CanSee(userId, copy), false, false);
                }

                // A series copy is visible only with at least one of its episodes; none scanned yet is "pending". Episodes are
                // looked up one at a time and the walk stops at the first visible one.
                var seriesVisible = _gateway.CanSee(userId, copy);
                var anyFound = false;
                foreach (var episode in _sync.FindCopyEpisodes(r, libraries))
                {
                    anyFound = true;
                    if (!seriesVisible || _gateway.CanSee(userId, episode))
                    {
                        return (seriesVisible, false, false);
                    }
                }

                if (anyFound)
                {
                    return (false, false, false);
                }
            }

            var pending = r.MissingChecks == 0 && _utcNow() - r.CreatedUtc < PendingWindow;
            return (false, pending, !pending);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Shared With Me: no copy lookup for share {ShareId}", r.Id);
            return (false, false, false);
        }
    }

    private bool CopyVisible(ShareRecord r, Guid userId, IReadOnlyDictionary<Guid, Dictionary<string, Guid>> libraries) => Visibility(r, userId, libraries).Visible;

    private RestoreOutcome TeardownLocked(bool forced)
    {
        var none = Array.Empty<UserRef>();
        var left = new List<string>(); // forced: what could not be done, in plain words
        var lost = new Leftovers();
        if (forced)
        {
            _gate.Close();
            _logger.LogWarning("Shared With Me is being uninstalled: removing every share, share library and plugin playlist, and putting library access back");
        }
        else
        {
            // Nobody is known to be owed "all libraries" back while the record cannot be read: nothing is changed.
            if (!_store.IsReadable(out _))
            {
                return new RestoreOutcome(false, ShareSynchronizer.StoreUnreadableMessage, none);
            }

            if (_store.All().Any(r => r.State == ShareState.Active))
            {
                return new RestoreOutcome(false, SharesExistMessage, none);
            }

            if (_sync.CurrentSetupProblem() is { } problem)
            {
                _logger.LogWarning("Shared With Me: restore refused: {Problem}", problem);
                return new RestoreOutcome(false, problem, none);
            }
        }

        // 0. The "Share with" playlists go (a restore also turns TV sharing off so they do not come straight back; an
        // uninstall closes the gate instead and leaves the setting as the admin chose it). This is before any tag is lifted.
        var tvOff = !forced && (_turnOffTvSharing?.Invoke() ?? false);
        RestoreOutcome Failed(string message) =>
            new(false, tvOff ? message + " Sharing from TV apps was turned off." : message, none);
        if (forced)
        {
            var stuck = Attempt(() => _playlists?.RemoveAllForced() ?? Array.Empty<string>(), "removing the plugin playlists", left);
            if (stuck is null)
            {
                lost.Playlists.Add("unknown (the removal failed)");
            }
            else if (stuck.Count > 0)
            {
                lost.Playlists.AddRange(stuck);
                left.Add("Some plugin playlists could not be removed; their owners' tags stay blocked.");
            }
        }
        else
        {
            _playlists?.RemoveAll();
        }

        // Store reads and writes: a restore lets a failure surface as before; an uninstall notes it and goes on. A store
        // that cannot be read means nobody is known to be owed "all libraries" back, so nobody is put back and no tag lifted.
        var storeOk = true;
        T Read<T>(Func<T> read, T fallback, string what)
        {
            if (!forced)
            {
                return read();
            }

            try
            {
                return read();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shared With Me: failed {What}", what);
                left.Add($"Failed {what}.");
                storeOk = false;
                return fallback;
            }
        }

        void Write(Action write, string what)
        {
            if (forced)
            {
                Attempt(write, what, left);
            }
            else
            {
                write();
            }
        }

        // 1. Every copy still on disk goes first: a folder left behind would be shown again, with its lifting NFO, by
        // the next share library at that path. A restore stops at one that cannot be removed; an uninstall goes on.
        var root = _shareRoot();
        foreach (var r in Read<IReadOnlyList<ShareRecord>>(() => _store.All(), Array.Empty<ShareRecord>(), "reading the share records").Where(r => !string.IsNullOrEmpty(r.FolderName)))
        {
            try
            {
                _builder.Remove(ShareService.KindRoot(root, r.RecipientUserId, r.Kind), r.FolderName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shared With Me: the copy {Folder} of share {ShareId} could not be removed", r.FolderName, r.Id);
                Write(() => _store.Upsert(r with { State = ShareState.Broken, StateReason = ShareService.CouldNotRemoveReason }), "recording a copy that stayed");
                if (!forced)
                {
                    return Failed(CopyNotRemovedMessage);
                }

                var folder = Path.Combine(ShareService.KindRoot(root, r.RecipientUserId, r.Kind), r.FolderName);
                lost.Folders.Add(folder);
                left.Add($"The copy folder {folder} could not be removed; delete it by hand.");
                continue;
            }

            Write(
                () => _store.Upsert(r with
                {
                    State = ShareState.Closed,
                    StateReason = forced ? UninstalledReason : ShareSynchronizer.SwitchedOffReason,
                    FolderName = string.Empty,
                    FailedChecks = 0,
                    MissingChecks = 0,
                    ClosedUtc = _utcNow(),
                }),
                "recording a removed copy");
        }

        // A restore also removes every copy folder no record names (a crash, a lost store, an earlier install): with every
        // copy gone, none may be left for a later share library to show (I5).
        if (!forced)
        {
            _sync.SweepUnknownCopies();
        }

        // Forced: Active records are ended too, and every folder the plugin made that no record knows goes as well.
        if (forced)
        {
            foreach (var r in Read<IReadOnlyList<ShareRecord>>(() => _store.All(), Array.Empty<ShareRecord>(), "reading the share records").Where(r => r.State == ShareState.Active))
            {
                Attempt(() => _store.Upsert(r with { State = ShareState.Closed, StateReason = UninstalledReason, ClosedUtc = _utcNow() }), "closing a share record", left);
            }

            SweepShareRoot(root, left, lost);
        }

        // 2. Every library at a share-layout path goes before anyone is given "all libraries" back, including one the
        // store does not know (lost store, crash), or every profile could open it in between. All are found first, so a
        // restore stops before any is removed; an uninstall removes what it can and remembers what is left.
        var libraries = new List<(string Path, Guid Id)>();
        var libraryLeft = false;
        var librariesUnknown = false; // the server's library list could not be read: owners of what is left are unknown
        var stuckPaths = new List<string>();
        IReadOnlyList<string> sharePaths;
        if (forced)
        {
            var listed = Attempt<IReadOnlyList<string>>(() => _gateway.GetAllLibraryPaths().Where(p => ShareLayout.IsShareLibraryPath(p, root)).ToList(), "listing the libraries", left);
            sharePaths = listed ?? Array.Empty<string>();
            if (listed is null)
            {
                libraryLeft = true; // the libraries are unknown: nobody is put back and no tag is lifted
                librariesUnknown = true;
                lost.Libraries.Add("unknown (the libraries could not be listed)");
            }
        }
        else
        {
            sharePaths = _gateway.GetAllLibraryPaths().Where(p => ShareLayout.IsShareLibraryPath(p, root)).ToList();
        }

        foreach (var path in sharePaths)
        {
            Guid? id;
            try
            {
                id = _gateway.FindLibraryByPath(path);
            }
            catch (Exception ex) when (ex is InvalidOperationException || forced)
            {
                var mixed = ex is InvalidOperationException;
                _logger.LogError(ex, mixed ? "Shared With Me: the library on {Path} also reads other folders" : "Shared With Me: the library on {Path} could not be looked up", path);
                if (!forced)
                {
                    return Failed(MixedLibraryMessage);
                }

                libraryLeft = true;
                stuckPaths.Add(path);
                lost.Libraries.Add(path);
                left.Add(mixed
                    ? $"The library on {path} also reads other folders and was not removed; remove it under Libraries."
                    : $"The library on {path} could not be looked up and was not removed; remove it under Libraries.");
                continue;
            }

            if (id is null)
            {
                // A library at a share path with no id yet (for example a crash between adding and validating it):
                // it cannot be removed, so access must not be restored around it.
                _logger.LogError("Shared With Me: the library on {Path} has no id yet", path);
                if (!forced)
                {
                    return Failed(LibraryNotReadyMessage);
                }

                libraryLeft = true;
                stuckPaths.Add(path);
                lost.Libraries.Add(path);
                left.Add($"The library on {path} has no id yet and was not removed; remove it under Libraries.");
                continue;
            }

            libraries.Add((path, id.Value));
        }

        var removed = new HashSet<Guid>();
        foreach (var (path, id) in libraries)
        {
            try
            {
                DropFromLists(id, forced, left);
                _gateway.RemoveLibrary(id); // RestoreAll's own removal of a known library is then a no-op
                removed.Add(id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shared With Me: the share library on {Path} could not be removed", path);
                if (!forced)
                {
                    return Failed(LibraryNotRemovedMessage);
                }

                libraryLeft = true;
                stuckPaths.Add(path);
                lost.Libraries.Add(path);
                left.Add($"The share library on {path} could not be removed; remove it under Libraries.");
            }
        }

        // 3. Access back, for the users the plugin moved.
        var restored = Read(
            () => _store.Snapshots()
                .Where(s => s.HadAllLibraries)
                .Select(s => _gateway.GetUser(s.UserId))
                .OfType<UserInfo>()
                .Select(u => new UserRef(u.Id, u.Name))
                .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            new List<UserRef>(),
            "reading who the plugin moved");
        if (!forced)
        {
            var result = _access.RestoreAll();
            restored = Refs(result.Restored);
            var ownList = Refs(result.LeftOnOwnList);
            if (ownList.Count > 0)
            {
                _logger.LogWarning("Shared With Me: {Users} {Reason}", string.Join(", ", ownList.Select(u => u.Name)), LibraryAccessManager.LeftOnOwnListReason);
            }

            if (_sync.IsInCharge())
            {
                _logger.LogError("Shared With Me: restore ran but the plugin is still in charge");
                return Failed(NotFinishedMessage) with { RestoredUsers = restored, LeftOnOwnList = ownList };
            }

            _logger.LogInformation("Shared With Me: restored {Count} profiles to all libraries", restored.Count);
            return new RestoreOutcome(true, null, restored, null, ownList);
        }

        // Forced: the store's own libraries (a share root changed since, or one not found by path above).
        foreach (var (recipient, kinds) in Read(() => _store.Libraries(), new Dictionary<Guid, Dictionary<string, Guid>>(), "reading the share libraries"))
        {
            foreach (var (kindKey, id) in kinds)
            {
                var path = Enum.TryParse<ShareKind>(kindKey, out var kind) ? ShareService.KindRoot(root, recipient, kind) : null;
                if (!removed.Contains(id) && path is not null && stuckPaths.Contains(path))
                {
                    continue; // already known to be left
                }

                try
                {
                    if (!removed.Contains(id))
                    {
                        DropFromLists(id, true, left);
                        _gateway.RemoveLibrary(id);
                    }

                    _store.ClearLibraryKey(recipient, kindKey);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Shared With Me: the share library {LibraryId} could not be removed", id);
                    libraryLeft = true;
                    lost.Libraries.Add(path ?? id.ToString());
                    left.Add($"The share library {id} could not be removed; remove it under Libraries.");
                }
            }
        }

        try
        {
            foreach (var still in _gateway.GetAllLibraryPaths().Where(p => ShareLayout.IsShareLibraryPath(p, root)))
            {
                libraryLeft = true;
                if (!lost.Libraries.Contains(still))
                {
                    lost.Libraries.Add(still);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shared With Me: could not list the libraries again; treating a share library as still there");
            left.Add("Failed listing the libraries after removing the share libraries.");
            libraryLeft = true; // could not tell: the safe direction
            librariesUnknown = true;
            if (!lost.Libraries.Any(l => l.StartsWith("unknown", StringComparison.Ordinal)))
            {
                lost.Libraries.Add("unknown (the libraries could not be listed after the removals)");
            }
        }

        // Owners whose tag every other user must keep blocking: anyone with a share library or a plugin playlist left.
        var owners = new HashSet<Guid>(Read(() => _store.Libraries().Keys.ToList(), new List<Guid>(), "reading the share libraries"));
        owners.UnionWith(Read(() => _store.SharePlaylists().Keys.ToList(), new List<Guid>(), "reading the plugin playlists"));
        owners.UnionWith(stuckPaths.Select(p => ShareLayout.RecipientOf(p, root)).OfType<Guid>());
        var users = Attempt(() => _gateway.GetUsers(), "reading the users", left);
        foreach (var u in users ?? Array.Empty<UserInfo>())
        {
            Attempt(() =>
            {
                if (_gateway.GetPlaylistsOwnedBy(u.Id).Any(p => p.Tags.Contains(LibraryAccessManager.RecipientTag(u.Id), StringComparer.OrdinalIgnoreCase)))
                {
                    owners.Add(u.Id);
                }
            }, "looking for plugin playlists left", left);
        }

        string NameOf(Guid id) => (users ?? Array.Empty<UserInfo>()).FirstOrDefault(u => u.Id == id)?.Name ?? id.ToString("N");

        // 4 and 5, through the one routine Restore uses: plugin tags lifted except those of owners with something left,
        // then "all libraries" back for the users the plugin moved. Nothing of either while the library state or the
        // records are unknown, and no tag either when the users (and so the owners of left-over playlists) could not be read.
        lost.TagsNotLiftedReason = !storeOk ? "the share records could not be read"
            : librariesUnknown ? "the library state is unknown (the libraries could not be listed)"
            : users is null ? "the profiles could not be read"
            : null;
        if (!storeOk)
        {
            lost.NotRestoredReason = "the share records could not be read";
        }
        else if (libraryLeft)
        {
            lost.NotRestoredReason = librariesUnknown ? "the library state is unknown" : "a share library remains";
            lost.NotRestored.AddRange(restored.Select(u => u.Name));
        }

        if (lost.NotRestoredReason is not null)
        {
            var isolated = string.Join(", ", restored.Select(u => u.Name));
            _logger.LogError("Shared With Me: {Why}, so these profiles were left on their explicit library list (the safe direction): {Users}. Remove the library under Libraries, then tick \"Enable access to all libraries\" for each of them.", lost.NotRestoredReason, isolated);
            left.Add($"Nobody was put back on all libraries because {lost.NotRestoredReason}: {isolated}.");
        }

        if (lost.TagsNotLiftedReason is null)
        {
            lost.KeptOwnerTags.AddRange(owners.Select(NameOf));
        }

        var back = _access.LiftTagsAndRestore(
            owners,
            liftTags: lost.TagsNotLiftedReason is null,
            restoreAccess: lost.NotRestoredReason is null,
            (what, step) =>
            {
                if (Attempt(() => { step(); return true; }, what, left))
                {
                    return true;
                }

                if (what.StartsWith(LibraryAccessManager.LiftTagsStep, StringComparison.Ordinal))
                {
                    var name = what[LibraryAccessManager.LiftTagsStep.Length..];
                    lost.TagsNotLifted.Add(name);
                    lost.FailedWrites.Add($"{name}: blocked tags");
                }
                else if (what.StartsWith(LibraryAccessManager.RestoreStep, StringComparison.Ordinal))
                {
                    var id = what[LibraryAccessManager.RestoreStep.Length..];
                    var name = Guid.TryParseExact(id, "N", out var g) ? NameOf(g) : id;
                    lost.NotRestored.Add(name);
                    lost.FailedWrites.Add($"{name}: library access");
                }
                else if (what == LibraryAccessManager.ReadUsersStep)
                {
                    lost.TagsNotLiftedReason ??= "the profiles could not be read";
                }
                else
                {
                    lost.NotRestoredReason ??= "who the plugin moved could not be read";
                }

                return false;
            });
        restored = Refs(back.Restored);
        var leftOnOwnList = Refs(back.LeftOnOwnList);
        lost.LeftOnOwnList.AddRange(leftOnOwnList.Select(u => u.Name));

        // 6. Empty recipient folders, and the share root when it is the default location and empty.
        RemoveEmptyFolders(root, left, lost);

        // 7. The record stays with the administrator, renamed; it is never read again.
        var renamed = Attempt(() => _store.Retire(_utcNow()) ?? string.Empty, "renaming shares.json", left);
        if (renamed is null)
        {
            lost.RenameFailed = true;
        }
        else if (renamed.Length > 0)
        {
            lost.RenamedTo = renamed;
            _logger.LogInformation("Shared With Me: the share records were kept as {File}", renamed);
        }

        foreach (var line in left)
        {
            _logger.LogError("Shared With Me uninstall: {Left}", line);
        }

        // The last line of the uninstall: everything that did not complete, from the one record.
        lost.FailedSteps = left.Count;
        var summary = lost.Summary();
        if (left.Count == 0 && summary.StartsWith("Shared With Me uninstall left nothing behind.", StringComparison.Ordinal))
        {
            _logger.LogWarning("Shared With Me was uninstalled cleanly: {Count} profiles are back on all libraries. {Summary}", restored.Count, summary);
            return new RestoreOutcome(true, null, restored, summary, leftOnOwnList);
        }

        _logger.LogError("{Summary}", summary);
        return new RestoreOutcome(false, string.Join(" ", left), restored, summary, leftOnOwnList);
    }

    private static List<UserRef> Refs(IEnumerable<UserInfo> users) =>
        users.Select(u => new UserRef(u.Id, u.Name)).OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();

    // Removes a share library from every explicit library list that holds it (its recipient's), so nobody keeps an id
    // of a library that no longer exists.
    private void DropFromLists(Guid libraryId, bool forced, List<string> left)
    {
        foreach (var u in _gateway.GetUsers().Where(u => !u.AllLibraries && u.EnabledLibraries.Contains(libraryId)))
        {
            void Drop() => _gateway.SetLibraryAccess(u.Id, false, u.EnabledLibraries.Where(l => l != libraryId).ToList());
            if (forced)
            {
                Attempt(Drop, $"taking a share library off {u.Name}'s list", left);
            }
            else
            {
                Drop();
            }
        }
    }

    // Every folder the plugin made under the share root that no record named: copies (the share marker) and dot-prefixed
    // build and probe folders. A recipient or kind folder that is a link is never followed. Anything else is left alone.
    private void SweepShareRoot(string root, List<string> left, Leftovers lost)
    {
        foreach (var kindRoot in KindRoots(root, left))
        {
            var entries = Attempt(() => Directory.GetFileSystemEntries(kindRoot), $"listing {kindRoot}", left);
            if (entries is null)
            {
                lost.Folders.Add(kindRoot);
            }

            foreach (var entry in entries ?? Array.Empty<string>())
            {
                var name = Path.GetFileName(entry);
                if (name.Contains(" [swm-", StringComparison.Ordinal))
                {
                    Attempt(() => _builder.Remove(kindRoot, name), $"removing the copy folder {entry}", left);
                }
                else if (name.StartsWith(".swm-", StringComparison.Ordinal))
                {
                    Attempt(() => _builder.DeleteTree(entry), $"removing the build folder {entry}", left);
                }
            }
        }

        foreach (var probe in Attempt(() => Directory.Exists(root) ? Directory.GetFileSystemEntries(root, ".swm-probe-*") : Array.Empty<string>(), "listing the share root", left) ?? Array.Empty<string>())
        {
            Attempt(() => _builder.DeleteTree(probe), $"removing {probe}", left);
        }
    }

    // <root>/<32 hex>/movies|shows folders that exist and are real folders (not links).
    private List<string> KindRoots(string root, List<string> left)
    {
        var found = new List<string>();
        foreach (var recipient in RecipientFolders(root, left))
        {
            foreach (var kind in new[] { "movies", "shows" })
            {
                var kindRoot = Path.Combine(recipient, kind);
                if (IsLink(kindRoot))
                {
                    _logger.LogError("Shared With Me: {Folder} is a link and was not followed", kindRoot);
                    left.Add($"{kindRoot} is a link and was left alone.");
                }
                else if (Directory.Exists(kindRoot))
                {
                    found.Add(kindRoot);
                }
            }
        }

        return found;
    }

    private List<string> RecipientFolders(string root, List<string> left)
    {
        var dirs = Attempt(() => Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>(), "listing the share root", left) ?? Array.Empty<string>();
        var found = new List<string>();
        foreach (var dir in dirs.Where(d => ShareLayout.RecipientOf(Path.Combine(d, "movies"), root) is not null))
        {
            if (IsLink(dir))
            {
                _logger.LogError("Shared With Me: {Folder} is a link and was not followed", dir);
                left.Add($"{dir} is a link and was left alone.");
            }
            else
            {
                found.Add(dir);
            }
        }

        return found;
    }

    // Every step is attempted on its own; a recipient folder still on disk afterwards is listed as left.
    private void RemoveEmptyFolders(string root, List<string> left, Leftovers lost)
    {
        foreach (var recipient in RecipientFolders(root, new List<string>()))
        {
            Attempt(() =>
            {
                foreach (var kind in new[] { "movies", "shows" })
                {
                    var kindRoot = Path.Combine(recipient, kind);
                    if (!IsLink(kindRoot) && Directory.Exists(kindRoot) && !ListFolder(kindRoot).Any())
                    {
                        Directory.Delete(kindRoot);
                    }
                }

                if (!ListFolder(recipient).Any())
                {
                    Directory.Delete(recipient);
                }
                else
                {
                    _logger.LogWarning("Shared With Me: {Folder} still holds files the plugin did not make, or that could not be removed, so it was kept", recipient);
                }
            }, $"removing {recipient}", left);
            if (Directory.Exists(recipient))
            {
                lost.Folders.Add(recipient);
            }
        }

        Attempt(() =>
        {
            var isDefault = _defaultShareRoot is not null && string.Equals(ShareLayout.Normalise(_defaultShareRoot()), ShareLayout.Normalise(root), StringComparison.Ordinal);
            if (isDefault && !IsLink(root) && Directory.Exists(root) && !ListFolder(root).Any())
            {
                Directory.Delete(root);
            }
        }, "removing the share folder", left);
    }

    private static bool IsLink(string path)
    {
        var info = new FileInfo(path);
        return info.Exists || Directory.Exists(path) ? info.LinkTarget is not null : false;
    }

    // Runs one uninstall step; a failure is logged and noted, and the teardown goes on.
    private void Attempt(Action step, string what, List<string> left) => Attempt<bool>(() => { step(); return true; }, what, left);

    private T? Attempt<T>(Func<T> step, string what, List<string> left)
    {
        try
        {
            return step();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shared With Me: failed {What}", what);
            left.Add($"Failed {what}.");
            return default;
        }
    }
}
