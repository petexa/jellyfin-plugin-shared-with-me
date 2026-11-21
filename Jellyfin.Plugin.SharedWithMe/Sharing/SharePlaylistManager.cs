using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SharedWithMe.Model;
using Jellyfin.Plugin.SharedWithMe.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SharedWithMe.Sharing;

/// <summary>
/// Sharing from any app: every user who may share gets one private playlist "Share with &lt;name&gt;" per restricted
/// profile. Adding a title there shares it (through <see cref="ShareService.Create"/>, the one code path for the rules);
/// removing it un-shares it. The playlist is read as a set of titles: a film is itself, an episode stands for its series.
/// Every method runs under the <see cref="PluginGate"/>. A playlist is known by the id stored for it, never by its name.
/// </summary>
public class SharePlaylistManager
{
    /// <summary>What every plugin playlist is called, before the recipient's name.</summary>
    public const string NamePrefix = "Share with ";

    private static readonly Regex RandomName = new("^swm-[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    private readonly IServerGateway _gateway;
    private readonly ShareStore _store;
    private readonly ShareService _service;
    private readonly ShareEligibility _eligibility;
    private readonly LibraryAccessManager _access;
    private readonly Func<bool> _enabled;
    private readonly Func<string?> _setupProblem;
    private readonly PluginGate _gate;
    private readonly ILogger _logger;
    private readonly Func<DateTime> _utcNow;

    // Shares whose mirror into the playlist did not take (the server would not store the item): not retried on every
    // playlist event, only on the next full run, so a mirror that cannot succeed never loops.
    private readonly HashSet<Guid> _mirrorFailed = new();

    // Shares kept for a dangling entry whose playlist repair did not take: not retried until the next full run.
    private readonly HashSet<Guid> _repairFailed = new();

    /// <summary>Initializes a new instance of the <see cref="SharePlaylistManager"/> class.</summary>
    /// <param name="gateway">The server seam.</param>
    /// <param name="store">The share store.</param>
    /// <param name="service">The one code path that creates and removes shares.</param>
    /// <param name="eligibility">Who may share.</param>
    /// <param name="access">Places and lifts the owner-tag blocks.</param>
    /// <param name="enabled">Reads the TV-sharing setting.</param>
    /// <param name="setupProblem">The current share-root problem, or null.</param>
    /// <param name="gate">The process-wide serialiser.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="utcNow">Clock; defaults to <see cref="DateTime.UtcNow"/>.</param>
    public SharePlaylistManager(IServerGateway gateway, ShareStore store, ShareService service, ShareEligibility eligibility, LibraryAccessManager access, Func<bool> enabled, Func<string?> setupProblem, PluginGate gate, ILogger logger, Func<DateTime>? utcNow = null)
    {
        _gateway = gateway;
        _store = store;
        _service = service;
        _eligibility = eligibility;
        _access = access;
        _enabled = enabled;
        _setupProblem = setupProblem;
        _gate = gate;
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Gets the display name of the playlist for a recipient.</summary>
    /// <param name="recipientName">The recipient's name.</param>
    /// <returns><c>Share with &lt;name&gt;</c>.</returns>
    public static string PlaylistName(string recipientName) => NamePrefix + recipientName;

    /// <summary>
    /// The playlists work of one worker run: <see cref="EnsurePlaylists"/> then <see cref="Reconcile"/>. While the share
    /// folder setting is wrong nothing is created or reconciled, but a setting turned off still removes every playlist
    /// (removing only narrows). A full run also retries mirrors and repairs that did not take earlier.
    /// </summary>
    /// <param name="full">Whether this follows a full synchronizer run.</param>
    public void Run(bool full) => _gate.Run(() =>
    {
        if (_gate.IsClosed)
        {
            return;
        }

        if (_setupProblem() is not null)
        {
            if (!_enabled())
            {
                RemoveAllLocked();
            }

            return;
        }

        EnsurePlaylists();
        Reconcile(full);
    });

    /// <summary>
    /// Creates, renames, heals and removes plugin playlists to match the users and the setting. A sharer is a user who
    /// may share; a possible recipient is any other enabled profile that is restricted. Each new playlist is created under
    /// a random name (its folder, and so its id, can then not be guessed), tagged with its owner's <c>swm-</c> tag (which
    /// every other user blocks first) and renamed; a playlist whose tag or rename fails is deleted. Leftover random-named
    /// playlists of a sharer are swept. Setting off: every playlist goes, and owners with no share library are unblocked.
    /// </summary>
    public void EnsurePlaylists() => _gate.Run(() =>
    {
        if (_gate.IsClosed)
        {
            return;
        }

        if (!_enabled())
        {
            RemoveAllLocked();
            return;
        }

        var ownersBefore = _store.SharePlaylists().Keys.ToList();
        var users = _gateway.GetUsers();
        var byId = users.ToDictionary(u => u.Id);
        var real = RealLibrariesToCount();
        var recipients = users.Where(u => !u.IsDisabled && IsRestricted(u, real)).ToList();
        var sharers = users.Where(u => _eligibility.MayShare(u.Id)).ToList();
        var wanted = sharers
            .SelectMany(s => recipients.Where(r => r.Id != s.Id).Select(r => (Sharer: s.Id, Recipient: r.Id)))
            .ToHashSet();

        // Stored playlists: forget or remove what is no longer wanted, heal the rest.
        foreach (var (sharer, map) in _store.SharePlaylists())
        {
            foreach (var (recipient, id) in map)
            {
                var info = _gateway.GetPlaylist(id);
                var mine = info is not null && info.OwnerUserId == sharer;
                if (!wanted.Contains((sharer, recipient)))
                {
                    if (mine)
                    {
                        _gateway.RemovePlaylist(id);
                    }

                    _store.SetSharePlaylist(sharer, recipient, null);
                    continue;
                }

                if (!mine)
                {
                    // Deleted, or the id now names someone else's playlist: never touched; a new one is made below.
                    _store.SetSharePlaylist(sharer, recipient, null);
                    continue;
                }

                var name = PlaylistName(byId[recipient].Name);
                if (!string.Equals(info!.Name, name, StringComparison.Ordinal))
                {
                    _gateway.RenamePlaylist(id, name);
                }

                if (info.IsPublic || info.SharedWithOthers)
                {
                    _gateway.MakePlaylistPrivate(id);
                }

                if (!info.Tags.Contains(LibraryAccessManager.RecipientTag(sharer), StringComparer.OrdinalIgnoreCase))
                {
                    _gateway.TagPlaylist(id, LibraryAccessManager.RecipientTag(sharer));
                }
            }
        }

        var stored = _store.SharePlaylists();
        foreach (var (sharer, recipient) in wanted.OrderBy(p => byId[p.Sharer].Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => byId[p.Recipient].Name, StringComparer.OrdinalIgnoreCase))
        {
            if (stored.TryGetValue(sharer, out var map) && map.ContainsKey(recipient))
            {
                continue;
            }

            Create(sharer, recipient, PlaylistName(byId[recipient].Name));
        }

        Sweep(users.Select(u => u.Id));

        // One tag rule (ruling B2): every user blocks the tag of every other user with plugin items, owners of plugin
        // playlists included, whether or not the plugin manages library access. A user who no longer owns any and has no
        // share library is unblocked everywhere.
        if (_store.SharePlaylists().Count > 0)
        {
            _access.EnsureTagBlocks();
        }

        Unblock(ownersBefore);
    });

    /// <summary>
    /// Playlists to shares, both directions, for every stored playlist. Idempotent: a second run changes nothing.
    /// Nothing happens while the setting is off.
    /// </summary>
    /// <param name="retryMirrors">A full run: retry mirrors that did not take earlier in this process, and count the runs on
    /// which only an unattributable dangling entry kept a share.</param>
    public void Reconcile(bool retryMirrors = false) => _gate.Run(() =>
    {
        if (_gate.IsClosed || !_enabled())
        {
            return;
        }

        if (retryMirrors)
        {
            _mirrorFailed.Clear();
            _repairFailed.Clear();
        }

        foreach (var (sharer, map) in _store.SharePlaylists())
        {
            foreach (var (recipient, id) in map)
            {
                try
                {
                    ReconcileOne(sharer, recipient, id, retryMirrors);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Shared With Me: reconciling playlist {PlaylistId} failed", id);
                }
            }
        }
    });

    /// <summary>Removes every plugin playlist the plugin still owns and clears the map; shares stay as they are.</summary>
    public void RemoveAll() => _gate.Run(RemoveAllLocked);

    /// <summary>
    /// The uninstall's version of <see cref="RemoveAll"/>: every plugin playlist (mapped, and tagged or random-named
    /// leftovers) is removed one by one, a failure is logged and the rest go on, and nobody's tag is touched (the
    /// teardown lifts tags itself, only for owners with nothing left). The sweep runs for every user even when the
    /// store's playlist map cannot be read; every plugin playlist carries its owner's tag, so it is still found.
    /// </summary>
    /// <returns>What could not be removed, in plain words; empty when everything went.</returns>
    public IReadOnlyList<string> RemoveAllForced() => _gate.Run<IReadOnlyList<string>>(() =>
    {
        var stuck = new List<string>();
        void Each(Action step, string what)
        {
            try
            {
                step();
            }
            catch (Exception ex)
            {
                stuck.Add(what);
                _logger.LogError(ex, "Shared With Me: could not remove {What}", what);
            }
        }

        Dictionary<Guid, Dictionary<Guid, Guid>>? map = null;
        Each(() => map = _store.SharePlaylists(), "the playlist map (unreadable)");
        foreach (var (sharer, recipients) in map ?? new Dictionary<Guid, Dictionary<Guid, Guid>>())
        {
            foreach (var (recipient, id) in recipients)
            {
                Each(
                    () =>
                    {
                        if (_gateway.GetPlaylist(id) is { } info && info.OwnerUserId == sharer)
                        {
                            _gateway.RemovePlaylist(id);
                        }

                        _store.SetSharePlaylist(sharer, recipient, null);
                    },
                    Describe(sharer, id));
            }
        }

        IReadOnlyList<UserInfo> users = Array.Empty<UserInfo>();
        Each(() => users = _gateway.GetUsers(), "left-over playlists of any profile (the profiles could not be read)");

        // "<name>" of <owner>, as far as the server can still say.
        string Describe(Guid owner, Guid playlistId)
        {
            string? name = null, ownerName = null;
            try
            {
                name = _gateway.GetPlaylist(playlistId)?.Name;
                ownerName = _gateway.GetUser(owner)?.Name;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Shared With Me: could not name playlist {PlaylistId}", playlistId);
            }

            return $"\"{name ?? playlistId.ToString("N")}\" of {ownerName ?? owner.ToString("N")}";
        }
        var known = map?.Values.SelectMany(m => m.Values).ToHashSet() ?? new HashSet<Guid>();
        foreach (var user in users)
        {
            Each(() => Sweep(new[] { user.Id }, known), $"left-over playlists of {user.Name}");
        }

        return stuck;
    });

    /// <summary>The refusal notes a user may read: their own; every note for an enabled administrator asking for all.</summary>
    /// <param name="callerId">The caller.</param>
    /// <param name="all">All notes (administrators only).</param>
    /// <returns>The notes, newest first, or null when the caller may not read all.</returns>
    public IReadOnlyList<RefusalNote>? NotesFor(Guid callerId, bool all)
    {
        var caller = _gateway.GetUser(callerId);
        if (all)
        {
            return caller is { IsAdmin: true, IsDisabled: false } ? _store.RefusalNotes() : null;
        }

        return caller is null || caller.IsDisabled
            ? Array.Empty<RefusalNote>()
            : _store.RefusalNotes().Where(n => n.SharerUserId == callerId).ToList();
    }

    /// <summary>The profiles a user has a plugin "Share with" playlist for.</summary>
    /// <param name="sharer">The user.</param>
    /// <returns>The recipients' ids.</returns>
    public IReadOnlyList<Guid> RecipientsWithPlaylist(Guid sharer) =>
        _store.SharePlaylists().TryGetValue(sharer, out var map) ? map.Keys.ToList() : Array.Empty<Guid>();

    /// <summary>Clears a user's own refusal notes.</summary>
    /// <param name="userId">The user.</param>
    public void ClearNotes(Guid userId) => _gate.Run(() =>
    {
        if (!_gate.IsClosed)
        {
            _store.ClearRefusalNotes(userId); // after an uninstall nothing may write shares.json again
        }
    });

    /// <summary>
    /// Whether a profile is restricted, ignoring the plugin's own changes (its explicit library lists and swm- tags): a
    /// rating cap, blocked unrated items, a blocked tag of its own, an allowed-tags list, or no access to a real library.
    /// </summary>
    /// <param name="u">The profile.</param>
    /// <param name="realLibraries">The real libraries to count.</param>
    /// <returns>True when restricted.</returns>
    public static bool IsRestricted(UserInfo u, IReadOnlyCollection<Guid> realLibraries) =>
        u.CapScore is not null
        || u.BlocksUnrated
        || u.BlockedTags.Any(t => !LibraryAccessManager.IsPluginTag(t))
        || u.AllowedTags.Count > 0
        || (!u.AllLibraries && realLibraries.Any(l => !u.EnabledLibraries.Contains(l)));

    // A real library the synchronizer has not recorded yet is granted by the next full run; until then it is not counted,
    // so a new library never makes everyone look restricted for a moment.
    private List<Guid> RealLibrariesToCount()
    {
        var real = _gateway.GetRealLibraryIds();
        var known = _store.KnownRealLibraries();
        return known is null ? real.ToList() : real.Where(known.Contains).ToList();
    }

    private void Create(Guid sharer, Guid recipient, string name)
    {
        // Shares already made toward this recipient are mirrored into the new (empty) playlist, never read as removed.
        foreach (var r in _store.All().Where(r => r.SharerUserId == sharer && r.RecipientUserId == recipient && r.ViaPlaylist))
        {
            _store.Upsert(r with { ViaPlaylist = false });
        }

        // 1. Every other user blocks the owner's tag before the playlist exists.
        _access.BlockFor(sharer);

        // 2. Created under a random name (its folder and id), video, private.
        Guid id;
        try
        {
            id = _gateway.CreatePlaylist(sharer, "swm-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shared With Me: could not create a playlist for {Sharer} toward {Recipient}", sharer, recipient);
            _access.UnblockIfNoPluginItems(sharer);
            return;
        }

        // 3. Tagged, 4. named, 5. stored: it never carries its display name without the tag, and a failure at any of these
        // steps deletes it, so no untracked "Share with" playlist is left behind.
        try
        {
            _gateway.TagPlaylist(id, LibraryAccessManager.RecipientTag(sharer));
            _gateway.RenamePlaylist(id, name);
            _store.SetSharePlaylist(sharer, recipient, id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shared With Me: could not tag, name or record the new playlist {PlaylistId}; deleting it", id);
            try
            {
                _gateway.RemovePlaylist(id);
            }
            catch (Exception removeEx)
            {
                _logger.LogError(removeEx, "Shared With Me: could not delete the unnamed playlist {PlaylistId}; the next run sweeps it", id);
            }

            _access.UnblockIfNoPluginItems(sharer);
        }
    }

    // Users who owned plugin playlists and may own none now: their tag is lifted when they have no plugin items left.
    private void Unblock(IEnumerable<Guid> formerOwners)
    {
        var owners = _store.SharePlaylists().Keys.ToHashSet();
        foreach (var user in formerOwners.Where(u => !owners.Contains(u)))
        {
            _access.UnblockIfNoPluginItems(user);
        }
    }

    // Plugin playlists the map does not hold (a create whose tag, rename or store write was lost, or a crash) are deleted.
    // Only these, of a user U and not in the map: (a) any playlist that carries U's own swm- tag itself - only the plugin
    // ever writes that tag on a playlist, so a user-made one never matches; (b) a random-named (swm-<32 hex>) playlist with
    // no tag and no entry at all. Never a playlist on its name alone if it carries anything else.
    private void Sweep(IEnumerable<Guid> users, HashSet<Guid>? knownIds = null)
    {
        var known = knownIds ?? _store.SharePlaylists().Values.SelectMany(m => m.Values).ToHashSet();
        foreach (var user in users)
        {
            var tag = LibraryAccessManager.RecipientTag(user);
            foreach (var p in _gateway.GetPlaylistsOwnedBy(user).Where(p => p.OwnerUserId == user && !known.Contains(p.Id)))
            {
                var tagged = p.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
                if (tagged || (RandomName.IsMatch(p.Name) && p.Tags.Count == 0 && IsEmpty(p.Id)))
                {
                    _gateway.RemovePlaylist(p.Id);
                }
            }
        }
    }

    private bool IsEmpty(Guid playlistId) =>
        _gateway.GetPlaylistEntries(playlistId) is { CannotTell: false } e && e.ItemIds.Count == 0 && e.Unresolved.Count == 0;

    private void RemoveAllLocked()
    {
        var owners = _store.SharePlaylists().Keys.ToList();
        foreach (var (sharer, map) in _store.SharePlaylists())
        {
            foreach (var (recipient, id) in map)
            {
                if (_gateway.GetPlaylist(id) is { } info && info.OwnerUserId == sharer)
                {
                    _gateway.RemovePlaylist(id);
                }

                _store.SetSharePlaylist(sharer, recipient, null);
            }
        }

        Sweep(_gateway.GetUsers().Select(u => u.Id));
        Unblock(owners);
    }

    private void ReconcileOne(Guid sharer, Guid recipient, Guid playlistId, bool full)
    {
        if (_gateway.GetUser(sharer) is not { IsDisabled: false } || _gateway.GetUser(recipient) is not { IsDisabled: false }
            || _gateway.GetPlaylist(playlistId) is not { } info || info.OwnerUserId != sharer)
        {
            return;
        }

        // Unknown here means deleted since GetPlaylist: never read as empty (that would remove every share); the next
        // EnsurePlaylists makes a new playlist and mirrors the shares into it.
        if (_gateway.GetPlaylistEntries(playlistId) is not { } entries)
        {
            return;
        }

        var records = _store.All().Where(r => r.SharerUserId == sharer && r.RecipientUserId == recipient).ToList();
        var titles = Titles(entries.ItemIds, out var others);

        // 1. Shares the synchronizer closed or broke leave the playlist; only a Broken one has something to tell.
        foreach (var r in records.Where(r => r.ViaPlaylist && r.State != ShareState.Active))
        {
            if (titles.ContainsKey(r.OriginalItemId))
            {
                _gateway.RemoveFromPlaylist(playlistId, r.OriginalItemId);
                titles.Remove(r.OriginalItemId);
                if (r.State == ShareState.Broken)
                {
                    var name = VisibleTitle(sharer, r.OriginalItemId);
                    Note(sharer, recipient, name, $"{(name.Length > 0 ? name : "A title")} stopped being shared: {r.StateReason ?? "it could not be kept"}.");
                }
            }

            _store.Upsert(r with { ViaPlaylist = false });
        }

        // 2. Entries that are neither a film nor an episode.
        foreach (var other in others)
        {
            _gateway.RemoveFromPlaylist(playlistId, other);
            Note(sharer, recipient, VisibleTitle(sharer, other), "Only movies and series can be shared.");
        }

        // 3. Every title in the playlist without an Active share from this sharer is shared, or taken out.
        var active = _store.All().Where(r => r.SharerUserId == sharer && r.RecipientUserId == recipient && r.State == ShareState.Active).ToList();
        foreach (var title in titles.Keys.ToList())
        {
            if (active.FirstOrDefault(r => r.OriginalItemId == title) is { } existing)
            {
                if (!existing.ViaPlaylist)
                {
                    _store.Upsert(existing with { ViaPlaylist = true });
                }

                continue;
            }

            Decision decision;
            ShareRecord? record;
            try
            {
                (decision, record) = _service.Create(sharer, title, recipient);
            }
            catch (Exception ex)
            {
                // Not a refusal: something failed. Taken out with a note, so it is not retried on every pass forever.
                _logger.LogError(ex, "Shared With Me: sharing an entry of playlist {PlaylistId} failed", playlistId);
                _gateway.RemoveFromPlaylist(playlistId, title);
                titles.Remove(title);
                Note(sharer, recipient, VisibleTitle(sharer, title), RetryMessage);
                continue;
            }

            if (record is not null)
            {
                _store.Upsert(record with { ViaPlaylist = true });
                if (record.OriginalItemId != title)
                {
                    // A copy was resolved to its original: the playlist holds originals only.
                    _gateway.RemoveFromPlaylist(playlistId, title);
                    _gateway.AddToPlaylist(playlistId, record.OriginalItemId);
                }

                continue;
            }

            _gateway.RemoveFromPlaylist(playlistId, title);
            titles.Remove(title);
            if (decision.Refusal is not (Refusal.AlreadyShared or Refusal.RecipientAlreadySees))
            {
                Note(sharer, recipient, VisibleTitle(sharer, title), decision.Message);
            }
        }

        // 4. Shares taken in by the playlist whose title is no longer there are removed - unless a dangling entry could
        // still be that title (see MayBeDangling): a dangling entry is never read as "removed by the owner", and neither is
        // a playlist whose own file cannot be read. One whose original is gone is left to the synchronizer (it relinks or
        // breaks it). A share kept only by an entry that cannot be matched to any title (no path, or no folder to match
        // it to) is kept for DanglingRunsLimit full runs; then that entry is removed with a note, and the next pass
        // reconciles normally.
        if (_gateway.GetPlaylistEntries(playlistId) is not { } now)
        {
            return;
        }

        if (now.CannotTell)
        {
            _logger.LogWarning("Shared With Me: the file of playlist {PlaylistId} cannot be read; its shares are kept", playlistId);
        }
        else
        {
            KeepOrRemove(sharer, recipient, playlistId, now, full);
        }

        if (_gateway.GetPlaylistEntries(playlistId) is not { } afterRepair)
        {
            return;
        }

        var present = Titles(afterRepair.ItemIds, out _);

        // 5. Shares made elsewhere (the share page) are mirrored in, when the sharer can see the original now.
        foreach (var r in _store.All().Where(r => r.SharerUserId == sharer && r.RecipientUserId == recipient && r.State == ShareState.Active && !r.ViaPlaylist))
        {
            if (_mirrorFailed.Contains(r.Id) || !_gateway.CanSee(sharer, r.OriginalItemId))
            {
                continue;
            }

            if (!present.ContainsKey(r.OriginalItemId))
            {
                _gateway.AddToPlaylist(playlistId, r.OriginalItemId);
                if (_gateway.GetPlaylistEntries(playlistId) is not { } added)
                {
                    return;
                }

                present = Titles(added.ItemIds, out _);
            }

            if (present.ContainsKey(r.OriginalItemId))
            {
                _store.Upsert(r with { ViaPlaylist = true });
            }
            else
            {
                _mirrorFailed.Add(r.Id);
                _logger.LogWarning("Shared With Me: {Title} could not be added to playlist {PlaylistId}", r.Title, playlistId);
            }
        }
    }

    /// <summary>The note for a dangling entry that kept a share for <see cref="DanglingRunsLimit"/> full runs and was removed.</summary>
    public const string DanglingRemovedMessage = "A playlist entry pointed at a file that no longer exists and was removed.";

    /// <summary>Full runs a share may be kept by a dangling entry that cannot be matched to any title.</summary>
    public const int DanglingRunsLimit = 3;

    /// <summary>The note for an entry whose share failed (not a refusal).</summary>
    public const string RetryMessage = "That title could not be shared just now. Add it again to retry.";

    /// <summary>
    /// Whether an unresolved (dangling) playlist entry may still stand for a title whose folder is
    /// <paramref name="titleFolder"/> (a series' folder, or the folder holding a film's file). The rule: true when any
    /// unresolved entry has no stored path, or lies inside that folder; and, when the title's folder is unknown (null),
    /// true when there is any unresolved entry at all. So a share is kept rather than removed whenever a dangling entry
    /// could be it; a wrongly kept share is still one its owner made and can remove on the share page.
    /// </summary>
    /// <param name="unresolved">The stored paths of the playlist's unresolved entries.</param>
    /// <param name="titleFolder">The title's folder, or null when unknown.</param>
    /// <returns>True when the share must be kept.</returns>
    public static bool MayBeDangling(IReadOnlyList<string?> unresolved, string? titleFolder) =>
        titleFolder is null
            ? unresolved.Count > 0
            : unresolved.Any(p => p is null || ShareLayout.IsInside(p, titleFolder));

    private void KeepOrRemove(Guid sharer, Guid recipient, Guid playlistId, PlaylistEntries now, bool full)
    {
        var present = Titles(now.ItemIds, out _);
        var mine = _store.All().Where(r => r.SharerUserId == sharer && r.RecipientUserId == recipient && r.State == ShareState.Active && r.ViaPlaylist).ToList();
        var folders = mine.ToDictionary(r => r.Id, r => _gateway.GetItem(r.OriginalItemId) is { } o ? TitleFolder(o) : null);
        var removedDangling = false;
        foreach (var r in mine)
        {
            if (present.ContainsKey(r.OriginalItemId) || _gateway.GetItem(r.OriginalItemId) is not { } original)
            {
                ResetDangling(r);
                continue;
            }

            var folder = folders[r.Id];
            if (!MayBeDangling(now.Unresolved, folder))
            {
                _logger.LogInformation("Shared With Me: {Title} was taken out of playlist {PlaylistId}; removing the share", r.Title, playlistId);
                _service.Remove(r.Id, sharer);
                continue;
            }

            if (folder is not null && now.Unresolved.Any(p => p is not null && ShareLayout.IsInside(p, folder)))
            {
                ResetDangling(r);
                Repair(playlistId, r, original, folder);
                continue;
            }

            // Kept only by an entry that cannot be matched to a title: counted on full runs only.
            if (!full || removedDangling)
            {
                continue;
            }

            if (r.DanglingRuns + 1 < DanglingRunsLimit)
            {
                _store.Upsert(r with { DanglingRuns = r.DanglingRuns + 1 });
                continue;
            }

            // Such an entry is one with no path, or (for a title whose folder is unknown) one inside no folder of another
            // share in this playlist, which could still be that share's.
            var known = folders.Where(f => f.Key != r.Id && f.Value is not null).Select(f => f.Value!).ToList();
            bool Unattributable(string? path) => path is null || (folder is null && !known.Any(f => ShareLayout.IsInside(path, f)));
            if (_gateway.RemoveUnresolvedEntries(playlistId, Unattributable) == 0)
            {
                // Nothing went (the server would not save the playlist): no note, the count stays, the next full run retries.
                _logger.LogWarning("Shared With Me: the dangling entry keeping {Title} in playlist {PlaylistId} could not be removed", r.Title, playlistId);
                continue;
            }

            _store.Upsert(r with { DanglingRuns = 0 });
            Note(sharer, recipient, string.Empty, DanglingRemovedMessage);
            _logger.LogInformation("Shared With Me: a dangling entry kept {Title} in playlist {PlaylistId} for {Runs} full runs and was removed", r.Title, playlistId, DanglingRunsLimit);
            removedDangling = true;
        }
    }

    private void ResetDangling(ShareRecord r)
    {
        if (r.DanglingRuns != 0)
        {
            _store.Upsert(r with { DanglingRuns = 0 });
        }
    }

    // A series' own folder; for a film, the folder that holds its file.
    private static string? TitleFolder(ItemInfo original)
    {
        if (string.IsNullOrEmpty(original.Path))
        {
            return null;
        }

        return original.Kind == ShareKind.Series ? original.Path : Path.GetDirectoryName(original.Path);
    }

    // A share kept for a dangling entry inside its title's folder: one current entry of that title goes in (a film
    // itself, a series its first episode with a file), then the dangling entries in that folder come out, so the
    // playlist converges and the next pass changes nothing. If the title cannot be put back, nothing is removed.
    private void Repair(Guid playlistId, ShareRecord r, ItemInfo original, string folder)
    {
        if (_repairFailed.Contains(r.Id))
        {
            return;
        }

        // A series: its first episode in the shared set (C1), which the sharer can see, so the server stores it.
        Guid? entry = original.Kind != ShareKind.Series
            ? original.Id
            : _gateway.GetUser(r.RecipientUserId) is { } recipient
                ? ShareEligibility.ShareableEpisodes(_gateway, _gateway.GetSourceMedia(original.Id), r.SharerUserId, recipient, original.OfficialRating)
                    .Episodes.Select(e => _gateway.FindItemByPath(e.File)).FirstOrDefault(id => id is not null)
                : null;
        if (entry is { } add)
        {
            _gateway.AddToPlaylist(playlistId, add);
        }

        if (_gateway.GetPlaylistEntries(playlistId) is { } e && Titles(e.ItemIds, out _).ContainsKey(original.Id))
        {
            _gateway.RemoveUnresolvedEntries(playlistId, p => p is not null && ShareLayout.IsInside(p, folder));
            _logger.LogInformation("Shared With Me: repaired playlist {PlaylistId}: a dangling entry of {Title} was replaced", playlistId, r.Title);
        }
        else
        {
            _repairFailed.Add(r.Id);
            _logger.LogWarning("Shared With Me: playlist {PlaylistId} holds a dangling entry of {Title} that could not be replaced; the share is kept", playlistId, r.Title);
        }
    }

    // The playlist as titles: a film (or a series id) is itself, an episode or season stands for its series. Entries the
    // server no longer has are skipped; anything else is returned in others.
    private Dictionary<Guid, int> Titles(IReadOnlyList<Guid> entries, out List<Guid> others)
    {
        var titles = new Dictionary<Guid, int>();
        others = new List<Guid>();
        foreach (var id in entries.Distinct())
        {
            var item = _gateway.GetItem(id);
            Guid? title = item switch
            {
                null => null,
                { Kind: not null } => id,
                { SeriesId: { } series } => series,
                _ => null,
            };
            if (title is { } t)
            {
                titles[t] = titles.GetValueOrDefault(t) + 1;
            }
            else if (item is not null)
            {
                others.Add(id);
            }
        }

        return titles;
    }

    // A title is named in a note only when the sharer can see that item now.
    private string VisibleTitle(Guid sharer, Guid itemId) =>
        _gateway.CanSee(sharer, itemId) && _gateway.GetItem(itemId) is { } item ? item.Title : string.Empty;

    private void Note(Guid sharer, Guid recipient, string title, string message) =>
        _store.AddRefusalNote(new RefusalNote(sharer, recipient, title, message, _utcNow()));
}
