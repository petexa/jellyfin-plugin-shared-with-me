using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SharedWithMe.Model;
using Jellyfin.Plugin.SharedWithMe.Storage;

namespace Jellyfin.Plugin.SharedWithMe.Sharing;

/// <summary>Who a restore put back on "all libraries", and who it left on their own list.</summary>
/// <param name="Restored">Users put back on "all libraries" (or already there).</param>
/// <param name="LeftOnOwnList">Users left on their explicit list because it was changed after the plugin moved them.</param>
public record RestoreResult(List<UserInfo> Restored, List<UserInfo> LeftOnOwnList);

/// <summary>
/// Owns the per-recipient share libraries and the move of every user from "all libraries" to an explicit list,
/// so a share library is only ever enabled for its recipient. Holds no state between calls and does not lock;
/// the caller serialises. Every step is ordered so that a crash leaves state a re-run can finish.
/// </summary>
public class LibraryAccessManager
{
    /// <summary>Prefix of the plugin's own tags. A copy carries <c>swm-&lt;recipient id, N format&gt;</c>.</summary>
    public const string TagPrefix = "swm-";

    private readonly IServerGateway _gateway;
    private readonly ShareStore _store;
    private readonly ShareFolderBuilder _builder;

    /// <summary>Initializes a new instance of the <see cref="LibraryAccessManager"/> class.</summary>
    /// <param name="gateway">The server seam.</param>
    /// <param name="store">The share store.</param>
    /// <param name="builder">Sweeps copy folders no record names before a share library is created or adopted.</param>
    public LibraryAccessManager(IServerGateway gateway, ShareStore store, ShareFolderBuilder? builder = null)
    {
        _gateway = gateway;
        _store = store;
        _builder = builder ?? new ShareFolderBuilder();
    }

    /// <summary>
    /// Moves every user on "all libraries" to an explicit list of the real libraries (snapshot written first),
    /// and strips share libraries from lists they do not belong to. Safe to repeat.
    /// </summary>
    public void EnsureIsolation()
    {
        // Tags first: every user blocks the tag of every other user who has plugin items.
        EnsureTagBlocks();

        var shareLibs = _store.Libraries();
        var allShareIds = shareLibs.Values.SelectMany(k => k.Values).ToHashSet();
        var real = _gateway.GetRealLibraryIds().Where(id => !allShareIds.Contains(id)).ToList();

        foreach (var user in _gateway.GetUsers())
        {
            if (user.AllLibraries)
            {
                // The snapshot (first) remembers which real libraries the plugin gave: a list later narrowed by someone
                // else is then left alone by a restore and by new-library grants.
                var existing = _store.Snapshots().FirstOrDefault(x => x.UserId == user.Id);
                _store.UpsertSnapshot(existing is null
                    ? new PolicySnapshot(user.Id, true, DateTime.UtcNow, real)
                    : existing with { GivenLibraries = real });

                var list = new List<Guid>(real);
                if (shareLibs.TryGetValue(user.Id, out var own))
                {
                    list.AddRange(own.Values.Where(id => !list.Contains(id)));
                }

                _gateway.SetLibraryAccess(user.Id, false, list);
            }
            else
            {
                var foreign = allShareIds.Where(id => user.EnabledLibraries.Contains(id)
                    && !(shareLibs.TryGetValue(user.Id, out var mine) && mine.Values.Contains(id))).ToHashSet();
                if (foreign.Count > 0)
                {
                    _gateway.SetLibraryAccess(user.Id, false, user.EnabledLibraries.Where(id => !foreign.Contains(id)).ToList());
                }
            }
        }
    }

    /// <summary>
    /// Isolation that needs no store, for when <c>shares.json</c> cannot be read: for every library at a share-layout path
    /// (its recipient is in the path), every other user is taken off "all libraries" (to an explicit list of every real
    /// library, plus their own share libraries), loses that library from their list, and blocks that recipient's tag.
    /// Nothing is recorded (the store cannot be written safely), so a user moved here is not put back by a later restore.
    /// Only narrows access; writes only on a real change, so the echo of these writes changes nothing.
    /// </summary>
    /// <param name="shareRoot">The share root.</param>
    /// <param name="moved">Called with the name of each user taken off "all libraries".</param>
    public void IsolateWithoutStore(string shareRoot, Action<string> moved)
    {
        var shareLibs = new List<(Guid Recipient, Guid? Id)>();
        foreach (var path in _gateway.GetAllLibraryPaths().Where(p => ShareLayout.IsShareLibraryPath(p, shareRoot)))
        {
            Guid? id = null;
            try
            {
                id = _gateway.FindLibraryByPath(path);
            }
            catch (InvalidOperationException)
            {
                // A library that also reads other folders is never real and never granted; users on "all libraries" are
                // still moved off below, which keeps them out of it.
            }

            shareLibs.Add((ShareLayout.RecipientOf(path, shareRoot)!.Value, id));
        }

        if (shareLibs.Count == 0)
        {
            return;
        }

        var real = _gateway.GetRealLibraryIds();
        foreach (var user in _gateway.GetUsers())
        {
            var foreign = shareLibs.Where(l => l.Recipient != user.Id).ToList();
            if (foreign.Count == 0)
            {
                continue;
            }

            var foreignIds = foreign.Select(l => l.Id).OfType<Guid>().ToHashSet();
            if (user.AllLibraries)
            {
                var own = shareLibs.Where(l => l.Recipient == user.Id).Select(l => l.Id).OfType<Guid>();
                moved(user.Name);
                _gateway.SetLibraryAccess(user.Id, false, real.Where(id => !foreignIds.Contains(id)).Concat(own).Distinct().ToList());
            }
            else if (user.EnabledLibraries.Any(foreignIds.Contains))
            {
                _gateway.SetLibraryAccess(user.Id, false, user.EnabledLibraries.Where(id => !foreignIds.Contains(id)).ToList());
            }

            WriteBlockedTags(user, foreign.Select(l => RecipientTag(l.Recipient)).Distinct(StringComparer.OrdinalIgnoreCase), Array.Empty<string>());
        }
    }

    /// <summary>
    /// The users whose <c>swm-</c> tag every other user blocks: those with plugin items, that is a share library in the
    /// store (their copies carry it) or a plugin "Share with" playlist in the store (it carries its owner's tag).
    /// </summary>
    /// <returns>The users.</returns>
    public HashSet<Guid> TagOwners() => _store.Libraries().Keys.Union(_store.SharePlaylists().Keys).ToHashSet();

    /// <summary>
    /// The tag pass on its own: every user blocks the tag of every other tag owner and not their own. Only adds blocks
    /// (and drops a user's own tag); never touches library access, so it is safe when the plugin is not in charge.
    /// </summary>
    public void EnsureTagBlocks()
    {
        var owners = TagOwners();
        foreach (var user in _gateway.GetUsers())
        {
            WriteBlockedTags(user, owners.Where(o => o != user.Id).Select(RecipientTag), new[] { RecipientTag(user.Id) });
        }
    }

    /// <summary>Makes every other user block one user's tag, and that user not block it, before something carrying it exists.</summary>
    /// <param name="ownerId">The user whose tag it is.</param>
    public void BlockFor(Guid ownerId)
    {
        var tag = RecipientTag(ownerId);
        foreach (var user in _gateway.GetUsers())
        {
            if (user.Id == ownerId)
            {
                WriteBlockedTags(user, Array.Empty<string>(), new[] { tag });
            }
            else
            {
                WriteBlockedTags(user, new[] { tag }, Array.Empty<string>());
            }
        }
    }

    /// <summary>Gets, or creates, the recipient's share library for a kind and enables it for the recipient only.</summary>
    /// <param name="recipientId">The recipient user.</param>
    /// <param name="kind">Movie or series.</param>
    /// <param name="kindRoot">Folder the library reads.</param>
    /// <param name="keepFolder">A copy folder just built whose record is not stored yet; every other copy folder there that
    /// no record names is removed before the library is created, adopted or scanned.</param>
    /// <returns>The library id.</returns>
    public Guid EnsureShareLibrary(Guid recipientId, ShareKind kind, string kindRoot, string? keepFolder = null)
    {
        var suffix = kind switch
        {
            ShareKind.Movie => string.Empty,
            ShareKind.Series => " (shows)",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported share kind."),
        };

        // A copy no record names (a crash between a build and the store write, a lost store, an earlier install) must not
        // be shown by the library about to read this folder (I5).
        var keep = _store.All()
            .Where(r => r.RecipientUserId == recipientId && r.Kind == kind && !string.IsNullOrEmpty(r.FolderName))
            .Select(r => r.FolderName)
            .ToHashSet(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(keepFolder))
        {
            keep.Add(keepFolder);
        }

        _builder.SweepKindRoot(kindRoot, keep);

        EnsureIsolation();

        var recipient = _gateway.GetUser(recipientId)
            ?? throw new InvalidOperationException($"Recipient {recipientId} does not exist.");
        var name = "Shared with " + recipient.Name + suffix;
        if (recipient.AllLibraries)
        {
            throw new InvalidOperationException($"Recipient {recipientId} is still on all libraries after isolation.");
        }

        Guid? stale = null;
        if (_store.Libraries().TryGetValue(recipientId, out var kinds) && kinds.TryGetValue(kind.ToString(), out var stored))
        {
            stale = stored;
        }

        // The library is identified by its folder, so one created just before a crash is adopted, not duplicated.
        var found = _gateway.FindLibraryByPath(kindRoot);
        if (found is { } f)
        {
            var usedElsewhere = _store.Libraries().Any(r => r.Value.Any(k => k.Value == f
                && !(r.Key == recipientId && k.Key == kind.ToString())));
            if (usedElsewhere)
            {
                throw new InvalidOperationException($"The share folder {kindRoot} is already in use by another share library.");
            }

            if (_gateway.GetRealLibraryIds().Contains(f))
            {
                throw new InvalidOperationException($"A normal library already uses the folder {kindRoot}.");
            }
        }

        // Every other user blocks this recipient's tag before the library can exist, so no other profile can
        // reach a copy even through calls that skip library access (Latest, Filters with a parentId).
        BlockFor(recipientId);

        var id = found ?? _gateway.CreateLibrary(name, kind, kindRoot);

        // Access list first, then the old library, then the store. The store keeps the old id until the old library
        // is gone, so a crash at any step leaves it findable: a re-run repeats the removal (a no-op if it already
        // happened) and then records the new id.
        var enabled = recipient.EnabledLibraries.Where(l => l != stale || l == id).ToList();
        if (!enabled.Contains(id))
        {
            enabled.Add(id);
        }

        if (!enabled.ToHashSet().SetEquals(recipient.EnabledLibraries))
        {
            _gateway.SetLibraryAccess(recipientId, false, enabled);
        }

        if (stale != id)
        {
            if (stale is { } old)
            {
                _gateway.RemoveLibrary(old);
            }

            _store.SetLibrary(recipientId, kind, id);
        }

        return id;
    }

    /// <summary>
    /// Removes the recipient's share library for a kind when nothing on disk uses it: no record for that recipient and
    /// kind is Active, or Broken with a non-empty folder name (a copy that could not be removed is still on disk).
    /// </summary>
    /// <param name="recipientId">The recipient user.</param>
    /// <param name="kind">Movie or series.</param>
    public void DropShareLibraryIfEmpty(Guid recipientId, ShareKind kind)
    {
        if (!_store.Libraries().TryGetValue(recipientId, out var kinds) || !kinds.TryGetValue(kind.ToString(), out var id))
        {
            return;
        }

        if (_store.All().Any(r => r.RecipientUserId == recipientId && r.Kind == kind
            && (r.State == ShareState.Active || (r.State == ShareState.Broken && !string.IsNullOrEmpty(r.FolderName)))))
        {
            return;
        }

        RemoveShareLibrary(recipientId, kind.ToString(), id);
        UnblockIfNoPluginItems(recipientId);
    }

    /// <summary>
    /// Adds a newly created real library to the enabled list of every user the plugin isolated whose list still holds
    /// every library the plugin gave them (a list someone narrowed since is never added to), and remembers it as given.
    /// </summary>
    /// <param name="libraryId">The new library.</param>
    public void OnRealLibraryAdded(Guid libraryId)
    {
        EnsureIsolation();

        var real = _gateway.GetRealLibraryIds();
        if (_store.Libraries().Values.Any(k => k.Values.Contains(libraryId)) || !real.Contains(libraryId))
        {
            return;
        }

        foreach (var snapshot in _store.Snapshots())
        {
            var user = _gateway.GetUser(snapshot.UserId);
            if (user is null || user.AllLibraries || user.EnabledLibraries.Contains(libraryId) || !StillHolds(user, snapshot, real, libraryId))
            {
                continue;
            }

            _gateway.SetLibraryAccess(user.Id, false, user.EnabledLibraries.Append(libraryId).ToList());
            var given = snapshot.GivenLibraries ?? real.Where(id => id != libraryId).ToList();
            _store.UpsertSnapshot(snapshot with { GivenLibraries = given.Append(libraryId).Distinct().ToList() });
        }
    }

    /// <summary>
    /// Whether a user's explicit list still holds every real library the plugin gave them that still exists (a snapshot
    /// from before this was recorded is compared with every real library). False means someone narrowed the list since.
    /// </summary>
    /// <param name="user">The user, on an explicit list.</param>
    /// <param name="snapshot">The user's snapshot.</param>
    /// <param name="realLibraries">The real libraries now.</param>
    /// <param name="except">A library not to count (a new one not granted yet).</param>
    /// <returns>True when nothing the plugin gave is missing.</returns>
    public static bool StillHolds(UserInfo user, PolicySnapshot snapshot, IReadOnlyCollection<Guid> realLibraries, Guid? except = null) =>
        (snapshot.GivenLibraries ?? realLibraries)
            .Where(id => realLibraries.Contains(id) && id != except)
            .All(user.EnabledLibraries.Contains);

    /// <summary>Plain words for a profile a restore leaves on its own list (the audit page, the restore answer and the log).</summary>
    public const string LeftOnOwnListReason = "left on their own library list because it was changed after the plugin moved them";

    /// <summary>
    /// Removes every share library and returns snapshot users to "all libraries". Records and share folders are left
    /// alone. Every caller must hold <see cref="PluginGate"/> for the whole restore and remove the share folders first
    /// (a folder left behind would be shown again by the next share library at that path); the synchronizer closes any
    /// copy still on disk once the plugin is no longer in charge, as a safety net.
    /// </summary>
    /// <returns>Who was put back on "all libraries" and who was left on their own list.</returns>
    public RestoreResult RestoreAll()
    {
        foreach (var (recipient, kinds) in _store.Libraries())
        {
            foreach (var (kindName, id) in kinds)
            {
                RemoveShareLibrary(recipient, kindName, id);
            }
        }

        return LiftTagsAndRestore(new HashSet<Guid>(), true, true, (_, step) =>
        {
            step();
            return true;
        });
    }

    /// <summary>The step name for lifting one user's tags; the user's name follows.</summary>
    public const string LiftTagsStep = "lifting the plugin's blocked tags from ";

    /// <summary>The step name for putting one user back on all libraries; the user's id (N) follows.</summary>
    public const string RestoreStep = "putting back on all libraries ";

    /// <summary>The step name for reading the users before lifting tags.</summary>
    public const string ReadUsersStep = "reading the users";

    /// <summary>
    /// The one place plugin tags are lifted and the users the plugin moved are put back on "all libraries", used by
    /// <see cref="RestoreAll"/> and by the uninstall. Tags: every plugin tag goes from every user, except the tag of a
    /// <paramref name="keepOwners"/> user, which every other user keeps blocking. Access: each snapshot user still on the
    /// server goes back to all libraries; a snapshot is removed only once that went through, and a user the server says
    /// is gone has theirs dropped.
    /// </summary>
    /// <param name="keepOwners">Users who still have a share library or plugin playlist.</param>
    /// <param name="liftTags">Whether to lift tags at all.</param>
    /// <param name="restoreAccess">Whether to put snapshot users back (false leaves every snapshot in place).</param>
    /// <param name="step">Runs one step: a restore runs it as is (a failure stops it); an uninstall logs a failure, goes on
    /// and returns false.</param>
    /// <returns>The users put back on all libraries, and those left on their own list.</returns>
    public RestoreResult LiftTagsAndRestore(ISet<Guid> keepOwners, bool liftTags, bool restoreAccess, Func<string, Action, bool> step)
    {
        if (liftTags)
        {
            IReadOnlyList<UserInfo> users = Array.Empty<UserInfo>();
            step(ReadUsersStep, () => users = _gateway.GetUsers());
            var keep = keepOwners.Select(RecipientTag).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var user in users)
            {
                var own = RecipientTag(user.Id);
                var lift = user.BlockedTags.Where(t => IsPluginTag(t) && (!keep.Contains(t) || string.Equals(t, own, StringComparison.OrdinalIgnoreCase))).ToList();
                step(LiftTagsStep + user.Name, () => WriteBlockedTags(user, Array.Empty<string>(), lift));
            }
        }

        var restored = new List<UserInfo>();
        var left = new List<UserInfo>();
        if (!restoreAccess)
        {
            return new RestoreResult(restored, left);
        }

        IReadOnlyList<PolicySnapshot> snapshots = Array.Empty<PolicySnapshot>();
        IReadOnlyList<Guid> real = Array.Empty<Guid>();
        step("reading who the plugin moved", () =>
        {
            snapshots = _store.Snapshots();
            real = _gateway.GetRealLibraryIds();
        });
        foreach (var snapshot in snapshots)
        {
            UserInfo? user = null;
            var narrowed = false;
            var done = step(RestoreStep + snapshot.UserId.ToString("N"), () =>
            {
                user = _gateway.GetUser(snapshot.UserId);
                if (snapshot.HadAllLibraries && user is not null)
                {
                    // Only a list nobody changed goes back to "all libraries"; a narrowed one is left exactly as it is.
                    narrowed = !user.AllLibraries && !StillHolds(user, snapshot, real);
                    if (!narrowed)
                    {
                        _gateway.SetLibraryAccess(snapshot.UserId, true, Array.Empty<Guid>());
                    }
                }

                _store.RemoveSnapshot(snapshot.UserId);
            });
            if (done && snapshot.HadAllLibraries && user is not null)
            {
                (narrowed ? left : restored).Add(user);
            }
        }

        return new RestoreResult(restored, left);
    }

    /// <summary>Gets the tag a recipient's copies carry.</summary>
    /// <param name="recipientId">The recipient user.</param>
    /// <returns><c>swm-</c> and the id in N format.</returns>
    public static string RecipientTag(Guid recipientId) => TagPrefix + recipientId.ToString("N");

    /// <summary>Whether a tag is one of the plugin's own: <c>swm-</c> and exactly 32 hex characters.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>True for plugin tags.</returns>
    public static bool IsPluginTag(string tag) =>
        tag.Length == TagPrefix.Length + 32
        && tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)
        && tag.Skip(TagPrefix.Length).All(Uri.IsHexDigit);

    /// <summary>
    /// Removes a user's tag from every user once that user has no plugin items left: no share library and no plugin
    /// playlist in the store.
    /// </summary>
    /// <param name="userId">The user.</param>
    public void UnblockIfNoPluginItems(Guid userId)
    {
        if (!TagOwners().Contains(userId))
        {
            UnblockEverywhere(userId);
        }
    }

    private void UnblockEverywhere(Guid recipientId)
    {
        var tag = RecipientTag(recipientId);
        foreach (var user in _gateway.GetUsers())
        {
            WriteBlockedTags(user, Array.Empty<string>(), new[] { tag });
        }
    }

    // Adds and removes tags (case-insensitive), keeps every other tag in place, writes only on a real change.
    private void WriteBlockedTags(UserInfo user, IEnumerable<string> add, IEnumerable<string> remove)
    {
        var drop = remove.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tags = user.BlockedTags.Where(t => !drop.Contains(t)).ToList();
        foreach (var t in add)
        {
            if (!drop.Contains(t) && !tags.Contains(t, StringComparer.OrdinalIgnoreCase))
            {
                tags.Add(t);
            }
        }

        // Compared as sets (tags case-insensitively): a server that reorders or re-cases the list must not cause a
        // write, or the user-updated echo of that write would never settle.
        if (!tags.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(user.BlockedTags))
        {
            _gateway.SetBlockedTags(user.Id, tags);
        }
    }

    private void RemoveShareLibrary(Guid recipientId, string kindKey, Guid id)
    {
        var user = _gateway.GetUser(recipientId);
        if (user is { AllLibraries: false } && user.EnabledLibraries.Contains(id))
        {
            _gateway.SetLibraryAccess(recipientId, false, user.EnabledLibraries.Where(l => l != id).ToList());
        }

        _gateway.RemoveLibrary(id);
        _store.ClearLibraryKey(recipientId, kindKey);
    }
}
