using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.SharedWithMe.Model;
using Jellyfin.Plugin.SharedWithMe.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SharedWithMe.Sharing;

/// <summary>What one synchronizer run did.</summary>
/// <param name="Checked">Active or Broken records of existing recipients examined.</param>
/// <param name="Relinked">Shares moved to a replaced original.</param>
/// <param name="Closed">Shares closed this run.</param>
/// <param name="Broken">Shares that became Broken (or changed Broken reason) this run.</param>
/// <param name="Reapplied">Copies rebuilt, or whose NFO was rewritten for a changed rating or tag.</param>
/// <param name="Removed">Records deleted: recipient gone, a stuck copy finally removed, Closed for over 30 days.</param>
/// <param name="SetupProblem">Why the run did nothing at all, when the share root is misconfigured; else null.</param>
public record SyncReport(int Checked, int Relinked, int Closed, int Broken, int Reapplied, int Removed = 0, string? SetupProblem = null);

/// <summary>
/// Keeps every share in step with the server: isolation, new libraries, then the numbered rules per record (first match
/// wins). Every run holds the <see cref="PluginGate"/>. Every failure ends with a shared copy disappearing, never with
/// anyone seeing more.
/// </summary>
public class ShareSynchronizer
{
    /// <summary>Reason for a share whose original is gone with no single replacement.</summary>
    public const string OriginalMissingReason = "original missing";

    /// <summary>Reason for a share whose original is gone and several items could replace it.</summary>
    public const string MoreThanOneMatchReason = "more than one match";

    /// <summary>Reason for a share closed because the recipient can see the original.</summary>
    public const string NoLongerNeededReason = "no longer needed";

    /// <summary>Reason for a share closed because the recipient's profile blocks a tag the original carries.</summary>
    public const string BlockedTagReason = "the recipient's profile blocks a tag on the title";

    /// <summary>Reason for a share no rating can lift over the recipient's cap.</summary>
    public const string NoRatingFitsReason = "no rating fits the recipient's age limit";

    /// <summary>Reason for a share whose copy the recipient still cannot see after a rewrite.</summary>
    public const string OverrideNotTakingEffectReason = "override not taking effect";

    /// <summary>Reason for a share closed because the plugin is no longer in charge (for example after a restore).</summary>
    public const string SwitchedOffReason = "sharing was switched off";

    /// <summary>Reason for a share closed because the time it was shared for is up.</summary>
    public const string ExpiredReason = "expired";

    /// <summary>Reason for a share whose check threw.</summary>
    public const string CheckFailedReason = "check failed";

    /// <summary>The least time between two counted checks of one share (rule 7): a refresh or a scan gets this long to land.</summary>
    public static readonly TimeSpan MinCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>The setup problem while the plugin's record of shares cannot be read (corrupt, or written by a newer version).</summary>
    public const string StoreUnreadableMessage = "The plugin's record of shares (shares.json in the plugin's configuration folder) cannot be read, so sharing is paused: the plugin only keeps every profile out of other profiles' share libraries and changes nothing else. Put back a readable copy of the file (the server log says what is wrong with it), then press Check now.";

    /// <summary>How long a Closed record is kept for the audit page.</summary>
    public static readonly TimeSpan ClosedRetention = TimeSpan.FromDays(30);

    // Consecutive runs a copy may be missing from the server (not scanned yet) before it is rebuilt.
    private const int MissingRunsBeforeRebuild = 3;

    private readonly IServerGateway _gateway;
    private readonly ShareStore _store;
    private readonly LibraryAccessManager _access;
    private readonly ShareFolderBuilder _builder;
    private readonly Func<string> _shareRoot;
    private readonly PluginGate _gate;
    private readonly ILogger _logger;
    private readonly Func<DateTime> _utcNow;
    private int _runs;
    private SyncReport? _lastReport;
    private DateTime? _lastRunUtc;

    /// <summary>Initializes a new instance of the <see cref="ShareSynchronizer"/> class.</summary>
    /// <param name="gateway">The server seam.</param>
    /// <param name="store">The share store.</param>
    /// <param name="access">Owns share libraries and library access.</param>
    /// <param name="builder">Builds share folders.</param>
    /// <param name="shareRoot">Reads the current share root folder.</param>
    /// <param name="gate">The process-wide serialiser.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="utcNow">Clock; defaults to <see cref="DateTime.UtcNow"/>.</param>
    public ShareSynchronizer(IServerGateway gateway, ShareStore store, LibraryAccessManager access, ShareFolderBuilder builder, Func<string> shareRoot, PluginGate gate, ILogger logger, Func<DateTime>? utcNow = null)
    {
        _gateway = gateway;
        _store = store;
        _access = access;
        _builder = builder;
        _shareRoot = shareRoot;
        _gate = gate;
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Gets the number of full runs finished since the server started.</summary>
    public int Runs => Volatile.Read(ref _runs);

    /// <summary>Gets the report of the last full run, or null before the first.</summary>
    public SyncReport? LastReport => Volatile.Read(ref _lastReport);

    /// <summary>Gets when the last full run finished, or null before the first.</summary>
    public DateTime? LastRunUtc => _lastRunUtc;

    /// <summary>
    /// Runs isolation, new-library grants and every rule over every share, while the plugin is in charge.
    /// With a misconfigured share root it runs isolation only (it only ever narrows access) and reports the problem in
    /// <see cref="SyncReport.SetupProblem"/>. When the plugin is not in charge it removes every copy left on disk and
    /// closes those shares, and changes no policy. A failure on one share marks that share Broken and the run goes on;
    /// a failure in isolation is rethrown.
    /// </summary>
    /// <param name="progress">Optional progress, 0 to 100.</param>
    /// <returns>What the run did.</returns>
    public SyncReport Run(IProgress<double>? progress = null) => _gate.Run(() =>
    {
        if (_gate.IsClosed)
        {
            return new SyncReport(0, 0, 0, 0, 0); // being uninstalled: nothing may be changed back
        }

        var report = RunLocked(progress);
        Volatile.Write(ref _lastReport, report);
        _lastRunUtc = _utcNow();
        Interlocked.Increment(ref _runs);
        return report;
    });

    /// <summary>
    /// Re-applies library isolation only (a user was created or an admin edited a policy), whenever the plugin is in
    /// charge, even with a misconfigured share root: isolation only ever narrows access.
    /// </summary>
    /// <remarks>With an unreadable store only the store-free isolation runs (see <see cref="LibraryAccessManager.IsolateWithoutStore"/>).</remarks>
    public void EnsureIsolation() => _gate.Run(() =>
    {
        if (_gate.IsClosed)
        {
            return;
        }

        if (!StoreReadable())
        {
            IsolateWithoutStore();
        }
        else if (IsInCharge())
        {
            _access.EnsureIsolation();
        }
    });

    // Logs why the store cannot be read (once per call site run); false when it cannot.
    private bool StoreReadable()
    {
        if (_store.IsReadable(out var why))
        {
            return true;
        }

        _logger.LogError("Shared With Me: the share record cannot be read ({Problem}); only keeping profiles out of share libraries", why);
        return false;
    }

    private void IsolateWithoutStore() =>
        _access.IsolateWithoutStore(_shareRoot(), name => _logger.LogWarning(
            "Shared With Me: the share record cannot be read, so {User} was moved off \"all libraries\" to an explicit list of every library, and no record of this was kept. Once the record is readable again a restore will not put {User} back: tick \"Enable access to all libraries\" for them by hand when sharing is no longer used",
            name,
            name));

    /// <summary>
    /// Whether the plugin is in charge: the store holds a snapshot or a share library, or the server has a library at a
    /// share-layout path (so a lost store never switches isolation off while share libraries exist). Records alone do not
    /// count: they outlive a Restore for the audit page, and a plugin that never shared or was restored must not isolate
    /// anyone. Callers that act on the answer must hold the <see cref="PluginGate"/>.
    /// </summary>
    /// <returns>True while the plugin manages library access.</returns>
    /// <remarks>An unreadable store counts as in charge, the strictest answer (the share folder setting stays locked).</remarks>
    public bool IsInCharge()
    {
        if (!_store.IsReadable(out _))
        {
            return true;
        }

        if (_store.Snapshots().Count > 0 || _store.Libraries().Count > 0)
        {
            return true;
        }

        var root = _shareRoot();
        return _gateway.GetAllLibraryPaths().Any(p => ShareLayout.IsShareLibraryPath(p, root));
    }

    /// <summary>
    /// The current share-root check, in plain words for an administrator (it names the library and both folders); null
    /// when the share root is usable. While the plugin is in charge the share folder setting is locked, so the advice is
    /// to remove or move the library; otherwise choosing another share folder is offered too. Does not log.
    /// </summary>
    /// <returns>The problem, or null.</returns>
    public string? CurrentSetupProblem()
    {
        if (!_store.IsReadable(out _))
        {
            return StoreUnreadableMessage;
        }

        var root = _shareRoot();
        var overlap = ShareLayout.FindOverlap(_gateway.GetAllLibraryPaths(), root);
        if (overlap is null)
        {
            return null;
        }

        var name = _gateway.GetLibraryName(overlap) ?? overlap;
        var fix = IsInCharge()
            ? "Remove that library (Dashboard, Libraries) or move it, then press Check now."
            : "Remove that library (Dashboard, Libraries) or move it, or choose a different share folder below.";
        return $"The library \"{name}\" uses a folder that overlaps the share folder. {fix} Library folder: {overlap}. Share folder: {root}.";
    }

    /// <summary>
    /// Finds the server item of a share's copy by its file path, only when it lives in the recipient's own share
    /// library (the same lookup the rules use).
    /// </summary>
    /// <param name="r">The share; its <see cref="ShareRecord.FolderName"/> must be set.</param>
    /// <returns>The copy's item id, or null when the server has not scanned it (yet).</returns>
    /// <exception cref="InvalidOperationException">The original is gone or has no media file, so the copy's file name is unknown.</exception>
    /// <param name="libraries">The store's library map, read once by a caller checking many shares; null to read it now.</param>
    public Guid? FindCopy(ShareRecord r, IReadOnlyDictionary<Guid, Dictionary<string, Guid>>? libraries = null)
    {
        var original = _gateway.GetItem(r.OriginalItemId)
            ?? throw new InvalidOperationException($"The original {r.OriginalItemId} of share {r.Id} is gone.");
        var kindRoot = ShareService.KindRoot(_shareRoot(), r.RecipientUserId, r.Kind);
        return FindCopy(r, kindRoot, ShareLibrary(r, libraries), () => _gateway.GetSourceMedia(original.Id));
    }

    /// <summary>
    /// Finds the server items of a series copy's episode links, in episode order, only those that live in the
    /// recipient's own share library. Empty for a movie share or before the episodes are scanned.
    /// </summary>
    /// <param name="r">The share; its <see cref="ShareRecord.FolderName"/> must be set.</param>
    /// <param name="libraries">The store's library map, read once by a caller checking many shares; null to read it now.</param>
    /// <returns>The episode item ids found, looked up lazily one link at a time, so a caller can stop early.</returns>
    /// <exception cref="InvalidOperationException">The original is gone.</exception>
    public IEnumerable<Guid> FindCopyEpisodes(ShareRecord r, IReadOnlyDictionary<Guid, Dictionary<string, Guid>>? libraries = null)
    {
        if (r.Kind != ShareKind.Series)
        {
            return Array.Empty<Guid>();
        }

        // The same episode set the copy was built from: only shareable episodes count (C1).
        var original = _gateway.GetItem(r.OriginalItemId);
        if (_gateway.GetUser(r.RecipientUserId) is not { } recipient || original is null)
        {
            return Array.Empty<Guid>();
        }

        var media = ShareEligibility.ShareableEpisodes(_gateway, _gateway.GetSourceMedia(r.OriginalItemId), r.SharerUserId, recipient, original.OfficialRating);
        var kindRoot = ShareService.KindRoot(_shareRoot(), r.RecipientUserId, r.Kind);
        var library = ShareLibrary(r, libraries);
        return ShareFolderBuilder.EpisodeLinks(media)
            .Select(link => FindInShareLibrary(Path.Combine(kindRoot, r.FolderName, link), library))
            .OfType<Guid>();
    }

    private static List<string> CopyTags(UserInfo recipient) =>
        ShareEligibility.CopyTagsFor(recipient).Append(LibraryAccessManager.RecipientTag(recipient.Id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private SyncReport RunLocked(IProgress<double>? progress)
    {
        var problem = SetupProblem();
        var c = new Counts();
        if (problem == StoreUnreadableMessage)
        {
            // Nothing that needs the store runs: no rebuild, no playlist, no record rule. Only the store-free isolation.
            IsolateOrAbort(IsolateWithoutStore);
            progress?.Report(100);
            return c.Report() with { SetupProblem = problem };
        }

        var inCharge = IsInCharge();
        if (problem is not null)
        {
            if (inCharge)
            {
                IsolateOrAbort(() => _access.EnsureIsolation());
            }

            progress?.Report(100);
            return c.Report() with { SetupProblem = problem };
        }

        if (!inCharge)
        {
            SwitchedOff(c);
            progress?.Report(100);
            return c.Report();
        }

        IsolateOrAbort(Isolate);
        progress?.Report(10);
        var root = _shareRoot();
        var records = _store.All();
        var gone = new HashSet<Guid>();
        for (var i = 0; i < records.Count; i++)
        {
            try
            {
                Check(records[i], root, c, gone);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shared With Me: checking share {ShareId} failed", records[i].Id);
                MarkFailed(records[i].Id, c);
            }

            progress?.Report(10 + (80.0 * (i + 1) / records.Count));
        }

        CleanUpGoneRecipients(root, gone);
        SweepUnknownCopies();
        PurgeClosed(c);
        progress?.Report(100);
        var report = c.Report();
        _logger.LogInformation("Shared With Me sync: {Report}", report);
        return report;
    }

    // Decision 11: nothing after isolation is safe if it failed, so the run stops and the failure is rethrown.
    private void IsolateOrAbort(Action isolate)
    {
        try
        {
            isolate();
        }
        catch (Exception ex)
        {
            // Nothing after this is safe: a copy must never be (re)built while another profile may still reach it.
            _logger.LogError(ex, "Shared With Me sync aborted: library isolation failed");
            throw;
        }
    }

    // Not in charge (never shared, restored, or the store was reset): no policy is touched, but no copy may stay on
    // disk for a later share library to bring back with its lifting NFO. Deleting never widens access.
    private void SwitchedOff(Counts c)
    {
        var root = _shareRoot();
        foreach (var r in _store.All().Where(r => !string.IsNullOrEmpty(r.FolderName)))
        {
            try
            {
                if (TryRemoveFolder(r, ShareService.KindRoot(root, r.RecipientUserId, r.Kind), c))
                {
                    _store.Upsert(r with { State = ShareState.Closed, StateReason = SwitchedOffReason, FolderName = string.Empty, FailedChecks = 0, MissingChecks = 0, ClosedUtc = _utcNow() });
                    c.Closed++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shared With Me: closing share {ShareId} after sharing was switched off failed", r.Id);
            }
        }

        foreach (var r in _store.All().Where(r => string.IsNullOrEmpty(r.FolderName) && _gateway.GetUser(r.RecipientUserId) is null))
        {
            _store.Remove(r.Id);
            c.Removed++;
        }

        SweepUnknownCopies();
        PurgeClosed(c);
    }

    /// <summary>
    /// Removes every copy folder under the share root that no record names (same recipient and kind, non-empty
    /// <see cref="ShareRecord.FolderName"/> equal to the folder's name): left by a crash between a build and the store
    /// write, a lost store, a restore or an earlier install. Also finishes left-over delete and build folders. Only folders
    /// with the share marker are touched; links are never followed. Callers hold the <see cref="PluginGate"/>, and must
    /// not call it with a setup problem (an overlapping share root) or an unreadable store.
    /// </summary>
    public void SweepUnknownCopies()
    {
        var root = _shareRoot();
        if (ShareFolderBuilder.IsLink(root) || !Directory.Exists(root))
        {
            return;
        }

        var records = _store.All();
        foreach (var dir in Directory.GetFileSystemEntries(root))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith(ShareFolderBuilder.DeletePrefix, StringComparison.Ordinal))
            {
                _builder.DeleteTree(dir);
                continue;
            }

            if (!Guid.TryParseExact(name, "N", out var recipient) || ShareFolderBuilder.IsLink(dir))
            {
                continue;
            }

            foreach (var kind in Enum.GetValues<ShareKind>())
            {
                var keep = records
                    .Where(r => r.RecipientUserId == recipient && r.Kind == kind && !string.IsNullOrEmpty(r.FolderName))
                    .Select(r => r.FolderName)
                    .ToHashSet(StringComparer.Ordinal);
                _builder.SweepKindRoot(ShareService.KindRoot(root, recipient, kind), keep);
            }
        }
    }

    // Decision 2: the same share-root check ShareService.Create starts with. Null when usable.
    private string? SetupProblem()
    {
        var problem = CurrentSetupProblem();
        if (problem is not null)
        {
            _logger.LogError("Shared With Me sync skipped, nothing changed: {Problem}", problem);
        }

        return problem;
    }

    private void Isolate()
    {
        _access.EnsureIsolation();

        var real = _gateway.GetRealLibraryIds();
        var known = _store.KnownRealLibraries();
        List<Guid> added;
        if (known is null)
        {
            // First run with no memory: only a library that no isolated user has at all counts as new, so a library an
            // admin deliberately took away from someone is never handed back.
            var isolated = _store.Snapshots().Select(s => _gateway.GetUser(s.UserId)).OfType<UserInfo>().Where(u => !u.AllLibraries).ToList();
            added = isolated.Count == 0 ? new List<Guid>() : real.Where(id => isolated.All(u => !u.EnabledLibraries.Contains(id))).ToList();
        }
        else
        {
            added = real.Except(known).ToList();
        }

        foreach (var id in added)
        {
            _logger.LogInformation("Shared With Me: new library {LibraryId} granted to every isolated user", id);
            _access.OnRealLibraryAdded(id);
        }

        if (known is null || !known.ToHashSet().SetEquals(real))
        {
            _store.SetKnownRealLibraries(real);
        }
    }

    // Rules 3 and 4 for a series whose shareable set came out empty although it has episodes: the recipient blocks a tag
    // the series carries, or one that every episode carries, or can now see the series. True when the share was closed.
    private bool CloseWhenRecipientNoLongerNeedsIt(ShareRecord r, ItemInfo original, UserInfo recipient, SourceMedia all, string kindRoot, Counts c)
    {
        var blocked = recipient.BlockedTags.Where(t => !LibraryAccessManager.IsPluginTag(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seriesBlocked = original.Tags.Any(t => !LibraryAccessManager.IsPluginTag(t) && blocked.Contains(t));
        var everyEpisodeBlocked = all.EpisodeDetails is not null
            && all.Episodes.All(e => all.EpisodeDetails.TryGetValue(e.File, out var d)
                && d.Tags.Any(t => !LibraryAccessManager.IsPluginTag(t) && blocked.Contains(t)));
        if (seriesBlocked || everyEpisodeBlocked)
        {
            Retire(r, ShareState.Closed, BlockedTagReason, kindRoot, c);
            return true;
        }

        if (_gateway.CanSee(recipient.Id, original.Id))
        {
            Retire(r, ShareState.Closed, NoLongerNeededReason, kindRoot, c);
            return true;
        }

        return false;
    }

    private void Check(ShareRecord r, string root, Counts c, HashSet<Guid> gone)
    {
        var kindRoot = ShareService.KindRoot(root, r.RecipientUserId, r.Kind);

        // Rule 1: recipient gone. Records in any state go (a pending one too), with their folder.
        var recipient = _gateway.GetUser(r.RecipientUserId);
        if (recipient is null)
        {
            gone.Add(r.RecipientUserId);
            if (TryRemoveFolder(r, kindRoot, c))
            {
                _store.Remove(r.Id);
                c.Removed++;
            }

            return;
        }

        // A pending share has nothing on disk or on the server to keep in step, and must never be built here: only an
        // administrator's approval builds it (through ShareService). Its rules are checked again at that moment.
        if (r.State == ShareState.Pending)
        {
            return;
        }

        if (r.State == ShareState.Closed)
        {
            if (r.ClosedUtc is null)
            {
                _store.Upsert(r with { ClosedUtc = _utcNow() });
            }

            return;
        }

        c.Checked++;

        // Expiry, before every other rule (and before 5b, so a copy that could not be removed at expiry is retried here
        // and the record ends Closed "expired" rather than being deleted): the share ends exactly as a removal does. A
        // Closed record is never looked at again, so nothing in this run or a later one rebuilds it.
        if (r.IsExpired(_utcNow()))
        {
            Retire(r, ShareState.Closed, ExpiredReason, kindRoot, c);
            return;
        }

        // Decision 5b: a copy that could not be removed is retried, never rebuilt.
        if (r.State == ShareState.Broken && r.StateReason == ShareService.CouldNotRemoveReason)
        {
            if (TryRemoveFolder(r, kindRoot, c))
            {
                _store.Remove(r.Id);
                c.Removed++;
                DropAndRescan(r);
            }

            return;
        }

        // Rule 2: original gone, so exactly one replacement in the same real library, else Broken. A series whose original
        // has no episode file left counts as gone, but only on the second run in a row: the first sighting may be a scan
        // in progress, and a working copy must not be deleted for that.
        var original = _gateway.GetItem(r.OriginalItemId);
        SourceMedia? media = null;
        var relinked = false;
        if (original is not null && original.Kind == r.Kind && r.Kind == ShareKind.Series)
        {
            // Only the shareable episodes (C1): an empty set counts as no episodes at all.
            var all = _gateway.GetSourceMedia(original.Id);
            media = ShareEligibility.ShareableEpisodes(_gateway, all, r.SharerUserId, recipient, original.OfficialRating);
            if (media.Episodes.Count == 0 && all.Episodes.Count > 0 && CloseWhenRecipientNoLongerNeedsIt(r, original, recipient, all, kindRoot, c))
            {
                // The episodes are there but none is shareable because of what the recipient blocks (or the recipient
                // can now see the series): that is an answer, not a scan in progress, so it must not wait for the grace.
                return;
            }

            // The grace is for a scan in progress, which shows as NO episode files at all, not as episodes filtered away.
            if (all.Episodes.Count == 0 && media.Episodes.Count == 0 && r.EmptyChecks == 0)
            {
                _store.Upsert(r with { EmptyChecks = 1 });
                return;
            }

            if (media.Episodes.Count > 0 && r.EmptyChecks > 0)
            {
                _store.Upsert(r = r with { EmptyChecks = 0 });
            }
        }

        if (original is null || original.Kind != r.Kind || media is { Episodes.Count: 0 })
        {
            var (match, matchMedia, reason) = FindReplacement(r, recipient);
            if (match is null)
            {
                Retire(r, ShareState.Broken, reason, kindRoot, c);
                return;
            }

            _logger.LogInformation("Shared With Me: share {ShareId} follows its title to {ItemId}", r.Id, match.Id);
            // The playlist entry of the old original no longer resolves: the share leaves the playlist's view, so the
            // playlists pass mirrors the new original in rather than reading the share as taken out.
            r = r with { OriginalItemId = match.Id, Title = match.Title, Year = match.Year, ProviderIds = new Dictionary<string, string>(match.ProviderIds), ViaPlaylist = false };
            original = match;
            media = matchMedia;
            relinked = true;
        }

        // The original's files, read once per check (a movie's only when a rule needs them).
        var originalId = original.Id;
        SourceMedia Media() => media ??= _gateway.GetSourceMedia(originalId);

        // Rule 3: the recipient blocks a tag the original now carries (the plugin's own tags never count).
        if (original.Tags.Any(t => !LibraryAccessManager.IsPluginTag(t)
            && recipient.BlockedTags.Any(b => !LibraryAccessManager.IsPluginTag(b) && string.Equals(b, t, StringComparison.OrdinalIgnoreCase))))
        {
            Retire(r, ShareState.Closed, BlockedTagReason, kindRoot, c);
            return;
        }

        // Rule 4: the server says the recipient can see the original.
        if (_gateway.CanSee(recipient.Id, original.Id))
        {
            Retire(r, ShareState.Closed, NoLongerNeededReason, kindRoot, c);
            return;
        }

        // The rating for the recipient's current settings, the same rule as at Create (ShareEligibility.CustomRatingFor),
        // worked out before any build.
        var (fits, chosen) = ShareEligibility.CustomRatingFor(_gateway.GetRatings(), recipient, r.Kind, original.OfficialRating);
        if (!fits)
        {
            Retire(r, ShareState.Broken, NoRatingFitsReason, kindRoot, c);
            return;
        }

        var rating = chosen ?? string.Empty;

        var tags = CopyTags(recipient);
        var official = original.OfficialRating ?? string.Empty;
        var library = ShareLibrary(r, null);

        // Rule 5 (and the relink of rule 2): rebuild when the target changed or the links do not resolve. For a series
        // also rule 6: rebuild when anything differs from what Build would make now, links (an episode added or removed)
        // or the content of any NFO (series, season or episode: rating, tags, numbers).
        if (relinked || string.IsNullOrEmpty(r.FolderName) || !_builder.LinksResolve(kindRoot, r.FolderName)
            || (r.Kind == ShareKind.Series && !_builder.Matches(kindRoot, r, Media(), official, rating, tags)))
        {
            if (Rebuild(r, official, Media(), rating, tags, kindRoot, c))
            {
                if (relinked)
                {
                    c.Relinked++;
                }
                else
                {
                    c.Reapplied++;
                }
            }

            return;
        }

        // Rule 6 for a movie: the NFO must be exactly what would be written now (customrating, tags, rating, title).
        if (r.Kind == ShareKind.Movie && !_builder.Matches(kindRoot, r, Media(), official, rating, tags))
        {
            _builder.RewriteNfo(kindRoot, r, official, rating, tags);
            if (FindCopy(r, kindRoot, library, Media) is { } copy)
            {
                _gateway.QueueItemRefresh(copy);
            }

            _gateway.QueueScan(_access.EnsureShareLibrary(r.RecipientUserId, r.Kind, kindRoot));
            Save(r, r with { FailedChecks = 0 });
            c.Reapplied++;
            return;
        }

        // Rule 7: the recipient must really see the copy; for a series the series item AND an episode (the first one the
        // server has, regular seasons before specials), so an override that reaches the series but not its episodes is
        // caught like any other failed check. No episode scanned yet counts as a copy not found yet, never as failed.
        var copyId = FindCopy(r, kindRoot, library, Media);
        Guid? episodeId = null;
        if (copyId is not null && r.Kind == ShareKind.Series)
        {
            episodeId = ShareFolderBuilder.EpisodeLinks(Media())
                .Select(link => FindInShareLibrary(Path.Combine(kindRoot, r.FolderName, link), library))
                .FirstOrDefault(id => id is not null);
            copyId = episodeId is null ? null : copyId;
        }

        if (copyId is null)
        {
            // Not scanned yet is not a failure; but a copy the server never picks up is rebuilt. A check soon after the
            // last counted one waits for the scan it queued.
            if (r.MissingChecks > 0 && CountedRecently(r))
            {
                return;
            }

            if (r.MissingChecks + 1 >= MissingRunsBeforeRebuild)
            {
                if (Rebuild(r, official, Media(), rating, tags, kindRoot, c))
                {
                    c.Reapplied++;
                }

                return;
            }

            _gateway.QueueScan(_access.EnsureShareLibrary(r.RecipientUserId, r.Kind, kindRoot));
            Save(r, r with { MissingChecks = r.MissingChecks + 1, CountedUtc = _utcNow() });
            return;
        }

        if (_gateway.CanSee(recipient.Id, copyId.Value) && (episodeId is null || _gateway.CanSee(recipient.Id, episodeId.Value)))
        {
            Save(r, r with { State = ShareState.Active, StateReason = null, FailedChecks = 0, MissingChecks = 0, CountedUtc = null });
            return;
        }

        if (r.FailedChecks == 0)
        {
            _builder.RewriteNfo(kindRoot, r, official, rating, tags);
            _gateway.QueueItemRefresh(copyId.Value);
            Save(r, r with { FailedChecks = 1, MissingChecks = 0, CountedUtc = _utcNow() });
            return;
        }

        // The refresh queued by the first failure gets time to land (the server applies a customrating a few seconds
        // after a scan): full runs come often (every user edit), and a run seconds later must not break the share.
        if (CountedRecently(r))
        {
            return;
        }

        // Still blind on the next run: Broken, copy kept (it is blocked for everyone else, which is safe).
        if (r.State != ShareState.Broken || r.StateReason != OverrideNotTakingEffectReason)
        {
            c.Broken++;
            _logger.LogWarning("Shared With Me: {Recipient} still cannot see the copy of share {ShareId}", recipient.Name, r.Id);
        }

        Save(r, r with { State = ShareState.Broken, StateReason = OverrideNotTakingEffectReason, FailedChecks = r.FailedChecks + 1, MissingChecks = 0, CountedUtc = _utcNow() });
    }

    private bool CountedRecently(ShareRecord r) => r.CountedUtc is { } at && _utcNow() - at < MinCheckInterval;

    // Decision 6: exactly one Movie/Series of the record's kind, in the record's source library, which must still be real.
    // A series candidate must have episode files; its media is returned so it is not read again.
    private (ItemInfo? Match, SourceMedia? Media, string Reason) FindReplacement(ShareRecord r, UserInfo recipient)
    {
        var ids = r.ProviderIds.Where(p => !string.IsNullOrEmpty(p.Value)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0 || !_gateway.GetRealLibraryIds().Contains(r.SourceLibraryId))
        {
            return (null, null, OriginalMissingReason);
        }

        // While the sharer exists and is enabled, only a replacement they can see now (I8): an unrated or longer cut with the
        // same ids is not something they could have shared. With the sharer gone, as before.
        var sharerSees = _gateway.GetUser(r.SharerUserId) is { IsDisabled: false };
        var hits = _gateway.FindByProviderIds(r.SourceLibraryId, r.Kind, ids)
            .Distinct()
            .Select(_gateway.GetItem)
            .OfType<ItemInfo>()
            .Where(i => i.Kind == r.Kind && i.LibraryId == r.SourceLibraryId && SameTitleIds(ids, i.ProviderIds))
            .Where(i => !sharerSees || _gateway.CanSee(r.SharerUserId, i.Id))
            .Select(i => (Item: i, Media: r.Kind == ShareKind.Series ? ShareEligibility.ShareableEpisodes(_gateway, _gateway.GetSourceMedia(i.Id), r.SharerUserId, recipient, i.OfficialRating) : null))
            .Where(h => h.Media is null || h.Media.Episodes.Count > 0)
            .ToList();
        return hits.Count switch
        {
            0 => (null, null, OriginalMissingReason),
            1 => (hits[0].Item, hits[0].Media, string.Empty),
            _ => (null, null, MoreThanOneMatchReason),
        };
    }

    // A candidate is the same title only if it shares at least one provider and every provider both carry agrees.
    private static bool SameTitleIds(Dictionary<string, string> record, Dictionary<string, string> candidate)
    {
        var shared = 0;
        foreach (var (key, value) in candidate.Where(p => !string.IsNullOrEmpty(p.Value)))
        {
            if (record.TryGetValue(key, out var mine))
            {
                if (!string.Equals(mine, value, StringComparison.Ordinal))
                {
                    return false;
                }

                shared++;
            }
        }

        return shared > 0;
    }

    // The copy's item on the server (a series copy: its series item, found by the folder), only when it lives in the
    // recipient's own share library.
    private Guid? FindCopy(ShareRecord r, string kindRoot, Guid? library, Func<SourceMedia> media)
    {
        var folder = Path.Combine(kindRoot, r.FolderName);
        var path = r.Kind == ShareKind.Series
            ? folder
            : Path.Combine(folder, ShareFolderBuilder.LinkName(r.FolderName, media().PrimaryFile));
        return FindInShareLibrary(path, library);
    }

    // The recipient's share library of the share's kind, from the given map or the store.
    private Guid? ShareLibrary(ShareRecord r, IReadOnlyDictionary<Guid, Dictionary<string, Guid>>? libraries) =>
        (libraries ?? _store.Libraries()).TryGetValue(r.RecipientUserId, out var kinds) && kinds.TryGetValue(r.Kind.ToString(), out var id) ? id : null;

    // The item at a path inside a share's copy, only when it lives in that share library.
    private Guid? FindInShareLibrary(string path, Guid? library)
    {
        if (library is null || _gateway.FindItemByPath(path) is not { } id)
        {
            return null;
        }

        return _gateway.GetItem(id) is { } item && item.LibraryId == library ? id : null;
    }

    // Same order as a new share: folder (atomic), record, library (others block the tag first), scan.
    // A copy rebuilt in place (same folder) keeps its items; they are refreshed so changed NFOs are read again.
    private bool Rebuild(ShareRecord r, string officialRating, SourceMedia media, string rating, List<string> tags, string kindRoot, Counts c)
    {
        if (!string.IsNullOrEmpty(r.FolderName) && r.FolderName != ShareFolderBuilder.FolderName(r))
        {
            // The name changes (relink): the old folder goes first, so a failure leaves no copy rather than two.
            if (!TryRemoveFolder(r, kindRoot, c))
            {
                return false;
            }

            r = r with { FolderName = string.Empty };
            _store.Upsert(r);
        }

        var folder = _builder.Build(kindRoot, r, media, officialRating, rating, tags);
        r = r with { FolderName = folder, State = ShareState.Active, StateReason = null, FailedChecks = 0, MissingChecks = 0, EmptyChecks = 0, CountedUtc = null };
        _store.Upsert(r);
        var library = _access.EnsureShareLibrary(r.RecipientUserId, r.Kind, kindRoot);
        if (FindCopy(r, kindRoot, library, () => media) is { } copy)
        {
            _gateway.QueueItemRefresh(copy);
        }

        _gateway.QueueScan(library);
        return true;
    }

    // Removes the copy and moves the record to Closed or Broken; a copy that cannot be removed leaves it Broken
    // (could not remove) still holding its folder and library (decision 5c).
    private void Retire(ShareRecord r, ShareState state, string reason, string kindRoot, Counts c)
    {
        if (r.State == state && r.StateReason == reason && string.IsNullOrEmpty(r.FolderName))
        {
            return; // already retired for this reason: nothing to count or write
        }

        if (!TryRemoveFolder(r, kindRoot, c))
        {
            return;
        }

        var next = r with
        {
            State = state,
            StateReason = reason,
            FolderName = string.Empty,
            FailedChecks = 0,
            MissingChecks = 0,
            EmptyChecks = 0,
            ClosedUtc = state == ShareState.Closed ? _utcNow() : r.ClosedUtc,
        };
        _store.Upsert(next);
        if (state == ShareState.Closed)
        {
            c.Closed++;
        }
        else
        {
            c.Broken++;
        }

        _logger.LogInformation("Shared With Me: share {ShareId} is now {State} ({Reason})", r.Id, state, reason);
        DropAndRescan(next);
    }

    // True when no folder is left. On failure the record becomes Broken (could not remove), keeping its folder name.
    private bool TryRemoveFolder(ShareRecord r, string kindRoot, Counts c)
    {
        if (string.IsNullOrEmpty(r.FolderName))
        {
            return true;
        }

        try
        {
            _builder.Remove(kindRoot, r.FolderName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shared With Me: could not remove the copy {Folder} of share {ShareId}", r.FolderName, r.Id);
            if (r.State != ShareState.Broken || r.StateReason != ShareService.CouldNotRemoveReason)
            {
                c.Broken++;
            }

            // Saved from the stored version, so a relink in progress is not recorded against the old folder.
            var stored = _store.All().FirstOrDefault(x => x.Id == r.Id) ?? r;
            _store.Upsert(stored with { State = ShareState.Broken, StateReason = ShareService.CouldNotRemoveReason });
            return false;
        }
    }

    private void DropAndRescan(ShareRecord r)
    {
        _access.DropShareLibraryIfEmpty(r.RecipientUserId, r.Kind);
        if (_store.Libraries().TryGetValue(r.RecipientUserId, out var kinds) && kinds.TryGetValue(r.Kind.ToString(), out var library))
        {
            _gateway.QueueScan(library);
        }
    }

    private void Save(ShareRecord before, ShareRecord after)
    {
        if (after != before)
        {
            _store.Upsert(after);
        }
    }

    // Decision 11: the folder state of a share whose check threw is unknown, so an Active share becomes Broken; it keeps
    // its folder name, and with it its library, until a later run settles it.
    private void MarkFailed(Guid id, Counts c)
    {
        try
        {
            var stored = _store.All().FirstOrDefault(x => x.Id == id);
            if (stored is null || stored.State != ShareState.Active)
            {
                return;
            }

            _store.Upsert(stored with { State = ShareState.Broken, StateReason = CheckFailedReason });
            c.Broken++;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shared With Me: could not record share {ShareId} as Broken", id);
        }
    }

    // Decision 10: once a gone recipient has no record left, their libraries, tag blocks, snapshot and whole share
    // directory go. A recipient whose copy could not be removed keeps everything until a later run succeeds.
    private void CleanUpGoneRecipients(string root, HashSet<Guid> gone)
    {
        var candidates = gone.Union(_store.Libraries().Keys.Where(id => _gateway.GetUser(id) is null)).ToList();
        foreach (var id in candidates)
        {
            try
            {
                if (_store.All().Any(r => r.RecipientUserId == id))
                {
                    continue;
                }

                foreach (var kind in Enum.GetValues<ShareKind>())
                {
                    _access.DropShareLibraryIfEmpty(id, kind);
                }

                _access.UnblockIfNoPluginItems(id);
                // A recipient folder that is a link is removed as a link; a real one goes out of view first (I4).
                _builder.DeleteTree(Path.Combine(root, id.ToString("N")));

                if (_store.Snapshots().Any(s => s.UserId == id))
                {
                    _store.RemoveSnapshot(id);
                }

                _logger.LogInformation("Shared With Me: removed everything for deleted user {UserId}", id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shared With Me: cleanup for deleted user {UserId} did not finish", id);
            }
        }
    }

    private void PurgeClosed(Counts c)
    {
        var cutoff = _utcNow() - ClosedRetention;
        _store.PurgeRefusalNotes(cutoff);
        foreach (var r in _store.All().Where(r => r.State == ShareState.Closed && r.ClosedUtc is { } closed && closed < cutoff))
        {
            _store.Remove(r.Id);
            c.Removed++;
        }
    }

    private sealed class Counts
    {
        public int Checked { get; set; }

        public int Relinked { get; set; }

        public int Closed { get; set; }

        public int Broken { get; set; }

        public int Reapplied { get; set; }

        public int Removed { get; set; }

        public SyncReport Report() => new(Checked, Relinked, Closed, Broken, Reapplied, Removed);
    }
}
