using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SharedWithMe.Configuration;
using Jellyfin.Plugin.SharedWithMe.Model;
using Jellyfin.Plugin.SharedWithMe.Sharing;
using Jellyfin.Plugin.SharedWithMe.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SharedWithMe.Tests;

public sealed class AuditServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "swm-audit-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServerGateway _gw = new();
    private readonly ShareStore _store;
    private readonly string _media;
    private readonly string _shareRoot;
    private readonly PluginGate _gate = new();
    private readonly TestBuilder _builder = new();
    private readonly DateTime _now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    private readonly Guid _lib = Guid.NewGuid();
    private readonly Guid _adult = Guid.NewGuid();
    private readonly Guid _kid1 = Guid.NewGuid();
    private readonly Guid _kid2 = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _film = Guid.NewGuid();
    private readonly Guid _film2 = Guid.NewGuid();

    public AuditServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new ShareStore(Path.Combine(_dir, "store"));
        _shareRoot = Path.Combine(_dir, "shares");
        _media = Path.Combine(_dir, "media");
        Directory.CreateDirectory(_media);
        _gw.ShareRoot = _shareRoot;

        _gw.RealLibraryIds.Add(_lib);
        _gw.RealPaths[_lib] = _media;
        _gw.Ratings.AddRange(new[] { new RatingOption("PG", 8, 0), new RatingOption("12", 12, 1), new RatingOption("15", 15, 3), new RatingOption("18", 18, 1) });
        AddUser(_adult, "Adult", null);
        AddUser(_kid1, "Kid1", 12, 1);
        AddUser(_kid2, "Kid2", 12, 1);
        AddUser(_admin, "Admin", null, admin: true);
        AddFilm(_film, "Spike Eighteen", 2002, "GB-18");
        AddFilm(_film2, "Spike Fifteen", 2001, "GB-15");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private LibraryAccessManager Access() => new(_gw, _store);

    private ShareService Shares() =>
        new(_gw, _store, new ShareEligibility(_gw, _store, () => WhoMayShare.Everyone), Access(), _builder, () => _shareRoot, _gate, NullLogger.Instance);

    private ShareSynchronizer Sync() =>
        new(_gw, _store, Access(), _builder, () => _shareRoot, _gate, NullLogger.Instance, () => _now);

    private int _tvOff;
    private bool _tvWasOn;

    private string? _defaultRoot;

    private AuditService Sut(SharePlaylistManager? playlists = null) =>
        new(_gw, _store, Access(), _builder, Sync(), () => _shareRoot, _gate, NullLogger.Instance, () => _now, playlists, () => { _tvOff++; return _tvWasOn; }, () => _defaultRoot ?? _shareRoot);

    private SharePlaylistManager Playlists() =>
        new(_gw, _store, Shares(), new ShareEligibility(_gw, _store, () => WhoMayShare.Everyone), Access(), () => true, () => null, _gate, NullLogger.Instance, () => _now);

    private void AddUser(Guid id, string name, int? cap, int? sub = null, bool admin = false) =>
        _gw.Users[id] = new UserInfo(id, name, admin, false, false, cap, sub, true, new List<Guid>(), Array.Empty<string>(), Array.Empty<string>());

    private void AddFilm(Guid id, string title, int year, string rating)
    {
        var folder = Path.Combine(_media, title);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, title + ".mp4");
        File.WriteAllText(file, "video");
        _gw.Items[id] = new ItemInfo(id, ShareKind.Movie, title, year, rating, new Dictionary<string, string> { ["Tmdb"] = id.ToString("N")[..6] }, _lib, new List<string>(), file);
        _gw.Media[id] = new SourceMedia(file, Array.Empty<string>(), Array.Empty<(int, int, string)>());
        _gw.SetVisible(_adult, id);
        _gw.SetVisible(_admin, id);
    }

    private string MoviesRoot(Guid recipient) => Path.Combine(_shareRoot, recipient.ToString("N"), "movies");

    private string Folder(ShareRecord r) => Path.Combine(MoviesRoot(r.RecipientUserId), r.FolderName);

    private ShareRecord Stored(Guid id) => _store.All().Single(r => r.Id == id);

    private Guid Library(Guid recipient) => _store.Libraries()[recipient][nameof(ShareKind.Movie)];

    private ShareRecord Share(Guid film, Guid recipient, Guid? sharer = null)
    {
        var (decision, record) = Shares().Create(sharer ?? _adult, film, recipient);
        Assert.Equal(Refusal.None, decision.Refusal);
        return record!;
    }

    // Registers the copy as scanned by the server, as it would be after the share library's scan.
    private Guid RegisterCopy(ShareRecord r, bool visible = true)
    {
        var copy = Guid.NewGuid();
        var original = _gw.Items[r.OriginalItemId];
        var path = Path.Combine(Folder(r), r.FolderName + Path.GetExtension(_gw.Media[r.OriginalItemId].PrimaryFile));
        _gw.ItemsByPath[path] = copy;
        _gw.Items[copy] = original with { Id = copy, LibraryId = Library(r.RecipientUserId), Path = path };
        _gw.SetVisible(r.RecipientUserId, copy, visible);
        return copy;
    }

    // Records hold a provider-id dictionary, compared by reference; compare everything else.
    private static object Shape(ShareRecord x) =>
        (x.Id, x.OriginalItemId, x.State, x.StateReason, x.FolderName, x.FailedChecks, x.MissingChecks, x.ClosedUtc, x.Title, string.Join(",", x.ProviderIds.Select(p => p.Key + "=" + p.Value)));

    private AuditShare AuditOf(AuditReport report, Guid user, Guid share) =>
        report.Profiles.Single(p => p.User.Id == user).Shares.Single(s => s.Id == share);

    private int Writes() => _gw.Calls.Count(c => c.Method is nameof(IServerGateway.SetLibraryAccess) or nameof(IServerGateway.SetBlockedTags)
        or nameof(IServerGateway.CreateLibrary) or nameof(IServerGateway.RemoveLibrary));

    // ---------- Audit ----------

    [Fact]
    public void I2_restore_with_an_unreadable_store_is_refused_in_plain_words_and_changes_nothing()
    {
        Share(_film, _kid1);
        File.WriteAllText(Path.Combine(_dir, "store", "shares.json"), "{ nope");
        var users = _gw.Users.Values.Select(u => (u.Id, u.AllLibraries, string.Join(",", u.BlockedTags))).ToList();
        _gw.Calls.Clear();

        var outcome = Sut().Restore();

        Assert.False(outcome.Done);
        Assert.Equal(ShareSynchronizer.StoreUnreadableMessage, outcome.Message);
        Assert.Equal(0, Writes());
        Assert.Equal(users, _gw.Users.Values.Select(u => (u.Id, u.AllLibraries, string.Join(",", u.BlockedTags))).ToList());
        Assert.Equal(0, _tvOff);
    }

    [Fact]
    public void I2_the_audit_page_reports_an_unreadable_store_as_a_setup_problem_instead_of_failing()
    {
        Share(_film, _kid1);
        File.WriteAllText(Path.Combine(_dir, "store", "shares.json"), "[1, 2");

        var report = Sut().Build();

        Assert.Equal(ShareSynchronizer.StoreUnreadableMessage, report.SetupProblem);
        Assert.True(report.InCharge);
        Assert.Equal(new[] { "Admin", "Adult", "Kid1", "Kid2" }, report.Profiles.Select(p => p.User.Name));
    }

    [Fact]
    public void Audit_reports_live_visibility_sharer_true_rating_and_cap_label()
    {
        var r = Share(_film, _kid1);
        RegisterCopy(r);

        var report = Sut().Build();

        var s = AuditOf(report, _kid1, r.Id);
        Assert.True(s.VisibleNow);
        Assert.False(s.Pending);
        Assert.Equal("Adult", s.SharerName);
        Assert.True(s.SharerExists);
        Assert.Equal("GB-18", s.OfficialRating);
        Assert.Equal("Spike Eighteen", s.Title);
        Assert.Equal(2002, s.Year);
        Assert.Equal(ShareState.Active, s.State);
        Assert.Equal("12", report.Profiles.Single(p => p.User.Id == _kid1).CapLabel);
        Assert.Equal("no limit", report.Profiles.Single(p => p.User.Id == _adult).CapLabel);
        Assert.Empty(report.Profiles.Single(p => p.User.Id == _kid2).Shares);
        Assert.True(report.InCharge);
        Assert.Null(report.SetupProblem);
    }

    [Fact]
    public void Audit_asks_the_server_now_so_a_copy_the_recipient_cannot_see_is_not_visible()
    {
        var r = Share(_film, _kid1);
        var copy = RegisterCopy(r);
        Assert.True(AuditOf(Sut().Build(), _kid1, r.Id).VisibleNow); // control

        _gw.SetVisible(_kid1, copy, false);

        var s = AuditOf(Sut().Build(), _kid1, r.Id);
        Assert.False(s.VisibleNow);
        Assert.False(s.Pending);
    }

    [Fact]
    public void Audit_counts_a_series_visible_only_with_the_series_and_at_least_one_episode_visible()
    {
        var show = Guid.NewGuid();
        var dir = Path.Combine(_media, "Spike Show (2010)", "Season 01");
        Directory.CreateDirectory(dir);
        var files = new[] { Path.Combine(dir, "Spike Show S01E01.mp4"), Path.Combine(dir, "Spike Show S01E02.mp4") };
        foreach (var f in files)
        {
            File.WriteAllText(f, "video");
        }

        _gw.Items[show] = new ItemInfo(show, ShareKind.Series, "Spike Show", 2010, "GB-15", new Dictionary<string, string> { ["Tvdb"] = "990010" }, _lib, new List<string>(), Path.GetDirectoryName(dir));
        _gw.Media[show] = new SourceMedia(string.Empty, Array.Empty<string>(), new List<(int, int, string)> { (1, 1, files[0]), (1, 2, files[1]) });
        _gw.SetVisible(_adult, show);
        var r = Share(show, _kid1);
        var library = _store.Libraries()[_kid1][nameof(ShareKind.Series)];
        var folder = Path.Combine(_shareRoot, _kid1.ToString("N"), "shows", r.FolderName);
        var series = Guid.NewGuid();
        _gw.ItemsByPath[folder] = series;
        _gw.Items[series] = _gw.Items[show] with { Id = series, LibraryId = library, Path = folder };
        _gw.SetVisible(_kid1, series);

        // The series is scanned but no episode yet: pending, not visible.
        var s = AuditOf(Sut().Build(), _kid1, r.Id);
        Assert.False(s.VisibleNow);
        Assert.True(s.Pending);

        var episodes = files.Select(f =>
        {
            var link = Path.Combine(folder, "Season 01", Path.GetFileName(f));
            var id = Guid.NewGuid();
            _gw.ItemsByPath[link] = id;
            _gw.Items[id] = new ItemInfo(id, null, Path.GetFileName(f), 2010, null, new Dictionary<string, string>(), library, new List<string>(), link);
            return id;
        }).ToList();

        Assert.False(AuditOf(Sut().Build(), _kid1, r.Id).VisibleNow); // series visible, no episode visible

        _gw.SetVisible(_kid1, episodes[1]);
        Assert.True(AuditOf(Sut().Build(), _kid1, r.Id).VisibleNow); // any one episode is enough

        _gw.SetVisible(_kid1, series, false);
        Assert.False(AuditOf(Sut().Build(), _kid1, r.Id).VisibleNow); // never without the series
        Assert.Equal("Spike Show", AuditOf(Sut().Build(), _kid1, r.Id).Title);
        Assert.Equal(2010, AuditOf(Sut().Build(), _kid1, r.Id).Year);
    }

    [Fact]
    public void Audit_marks_a_copy_not_yet_scanned_as_pending_and_not_visible()
    {
        var r = Share(_film, _kid1);
        _store.Upsert(Stored(r.Id) with { CreatedUtc = _now - TimeSpan.FromMinutes(1) }); // a new share, first check not run

        var s = AuditOf(Sut().Build(), _kid1, r.Id);

        Assert.False(s.VisibleNow);
        Assert.True(s.Pending);
        Assert.False(s.NotFound);
    }

    [Fact]
    public void A_copy_still_missing_after_a_check_is_not_found_rather_than_pending()
    {
        var r = Share(_film, _kid1);
        _store.Upsert(Stored(r.Id) with { MissingChecks = 1 });

        var s = AuditOf(Sut().Build(), _kid1, r.Id);

        Assert.False(s.VisibleNow);
        Assert.False(s.Pending);
        Assert.True(s.NotFound);
    }

    [Fact]
    public void A_copy_missing_from_an_old_share_is_not_found_rather_than_pending()
    {
        var r = Share(_film, _kid1);
        _store.Upsert(Stored(r.Id) with { CreatedUtc = _now - AuditService.PendingWindow - TimeSpan.FromSeconds(1) });

        var s = AuditOf(Sut().Build(), _kid1, r.Id);

        Assert.False(s.Pending);
        Assert.True(s.NotFound);
    }

    [Fact]
    public void Audit_includes_closed_and_broken_records_with_their_reason()
    {
        var r = Share(_film, _kid1);
        var b = Share(_film2, _kid1);
        _store.Upsert(r with { State = ShareState.Closed, StateReason = ShareSynchronizer.NoLongerNeededReason, FolderName = string.Empty, ClosedUtc = _now });
        _store.Upsert(b with { State = ShareState.Broken, StateReason = ShareSynchronizer.OriginalMissingReason, FolderName = string.Empty });
        _gw.Items.Remove(_film2);

        var report = Sut().Build();

        var closed = AuditOf(report, _kid1, r.Id);
        Assert.Equal(ShareState.Closed, closed.State);
        Assert.Equal(ShareSynchronizer.NoLongerNeededReason, closed.StateReason);
        Assert.False(closed.VisibleNow);
        Assert.False(closed.Pending);
        var broken = AuditOf(report, _kid1, b.Id);
        Assert.Equal(ShareState.Broken, broken.State);
        Assert.Equal(ShareSynchronizer.OriginalMissingReason, broken.StateReason);
        Assert.Null(broken.OfficialRating);
        Assert.False(broken.VisibleNow);
    }

    [Fact]
    public void Audit_names_a_sharer_that_is_gone_by_the_recorded_name()
    {
        var r = Share(_film, _kid1);
        _gw.Users.Remove(_adult);

        var s = AuditOf(Sut().Build(), _kid1, r.Id);

        Assert.Equal("Adult", s.SharerName);
        Assert.False(s.SharerExists);
    }

    [Fact]
    public void Audit_lists_the_profiles_the_plugin_switched()
    {
        Assert.Empty(Sut().Build().SwitchedUsers); // control: nothing switched before the first share
        Assert.False(Sut().Build().InCharge);

        Share(_film, _kid1);

        var switched = Sut().Build().SwitchedUsers.Select(u => u.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "Admin", "Adult", "Kid1", "Kid2" }, switched);
    }

    [Fact]
    public void Audit_reports_the_current_setup_problem_in_plain_words()
    {
        Share(_film, _kid1);
        Assert.Null(Sut().Build().SetupProblem);

        _gw.AddRealLibrary(Path.Combine(_shareRoot, "stray"));

        var problem = Sut().Build().SetupProblem;
        Assert.False(string.IsNullOrEmpty(problem));
        Assert.Contains("stray", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_changes_nothing()
    {
        var r = Share(_film, _kid1);
        RegisterCopy(r);
        var before = Writes();
        var records = _store.All();

        Sut().Build();

        Assert.Equal(before, Writes());
        Assert.Equal(records.Select(Shape), _store.All().Select(Shape));
    }

    // ---------- Restore ----------

    [Fact]
    public void Restore_is_refused_while_a_share_is_active_and_changes_nothing()
    {
        var r = Share(_film, _kid1);
        var before = Writes();

        var outcome = Sut().Restore();

        Assert.False(outcome.Done);
        Assert.Equal(AuditService.SharesExistMessage, outcome.Message);
        Assert.Equal(before, Writes());
        Assert.True(Directory.Exists(Folder(r)));
        Assert.All(_gw.Users.Values, u => Assert.False(u.AllLibraries));
    }

    [Fact]
    public void Restore_removes_every_plugin_playlist_and_clears_the_map_before_restoring_access()
    {
        var r = Share(_film, _kid1);
        Assert.True(Shares().Remove(r.Id, _adult));
        var playlists = Playlists();
        playlists.EnsurePlaylists();
        Assert.NotEmpty(_store.SharePlaylists());
        var mine = _gw.SeedPlaylist(_adult, "Share with Kid1");
        _gw.Calls.Clear();

        var outcome = Sut(playlists).Restore();

        Assert.True(outcome.Done, outcome.Message);
        Assert.Equal(new[] { mine }, _gw.Playlists.Keys);
        Assert.Empty(_store.SharePlaylists());
        var lastRemove = _gw.Calls.FindLastIndex(c => c.Method == nameof(IServerGateway.RemovePlaylist));
        var firstRestore = _gw.Calls.FindIndex(c => c.Method == nameof(IServerGateway.SetLibraryAccess) && (bool)c.Args[1]!);
        Assert.True(lastRemove >= 0 && firstRestore > lastRemove, $"{lastRemove} {firstRestore}");
        Assert.Equal(1, _tvOff); // a restore is the way out: the playlists must not come straight back
        var firstStrip = _gw.Calls.FindIndex(c => c.Method == nameof(IServerGateway.SetBlockedTags));
        Assert.True(firstStrip > lastRemove, $"tags stripped at {firstStrip} before the playlists went at {lastRemove}");
        Assert.All(_gw.Users.Values, u => Assert.DoesNotContain(u.BlockedTags, LibraryAccessManager.IsPluginTag));
    }

    [Fact]
    public void Restore_removes_a_tagged_orphan_playlist_before_tags_are_stripped()
    {
        var r = Share(_film, _kid1);
        Assert.True(Shares().Remove(r.Id, _adult));
        var playlists = Playlists();
        playlists.EnsurePlaylists();
        var orphan = _gw.SeedPlaylist(_adult, "Share with Kid1");
        _gw.Playlists[orphan].Tags.Add(LibraryAccessManager.RecipientTag(_adult));
        _gw.Calls.Clear();

        Assert.True(Sut(playlists).Restore().Done);

        Assert.False(_gw.Playlists.ContainsKey(orphan));
        var orphanGone = _gw.Calls.FindIndex(c => c.Method == nameof(IServerGateway.RemovePlaylist) && (Guid)c.Args[0]! == orphan);
        var firstStrip = _gw.Calls.FindIndex(c => c.Method == nameof(IServerGateway.SetBlockedTags));
        Assert.True(orphanGone >= 0 && firstStrip > orphanGone, $"{orphanGone} {firstStrip}");
    }

    [Fact]
    public void A_restore_that_fails_after_turning_tv_sharing_off_says_so()
    {
        var r = Share(_film, _kid1);
        _store.Upsert(r with { State = ShareState.Broken, StateReason = ShareSynchronizer.OverrideNotTakingEffectReason });
        _builder.FailRemove = true;
        _tvWasOn = true;

        var outcome = Sut(Playlists()).Restore();

        Assert.False(outcome.Done);
        Assert.Equal(AuditService.CopyNotRemovedMessage + " Sharing from TV apps was turned off.", outcome.Message);
        Assert.Equal(1, _tvOff);
    }

    [Fact]
    public void A_refused_restore_leaves_the_playlists_alone()
    {
        Share(_film, _kid1);
        var playlists = Playlists();
        playlists.EnsurePlaylists();
        var before = _gw.Playlists.Keys.ToList();

        Assert.False(Sut(playlists).Restore().Done);

        Assert.Equal(0, _tvOff);
        Assert.Equal(before, _gw.Playlists.Keys.ToList());
        Assert.NotEmpty(_store.SharePlaylists());
    }

    [Fact]
    public void I5_restore_also_removes_a_copy_folder_no_record_names()
    {
        var r = Share(_film, _kid1);
        Assert.True(Shares().Remove(r.Id, _adult));
        var lost = r with { Id = Guid.NewGuid(), FolderName = string.Empty };
        var stray = _builder.Build(MoviesRoot(_kid1), lost, _gw.Media[_film], "GB-18", "12", Array.Empty<string>());

        Assert.True(Sut().Restore().Done);

        Assert.False(Directory.Exists(Path.Combine(MoviesRoot(_kid1), stray)));
    }

    [Fact]
    public void Restore_removes_left_over_copies_then_returns_everyone_to_all_libraries()
    {
        var r = Share(_film, _kid1);
        var lib = Library(_kid1);
        _store.Upsert(r with { State = ShareState.Broken, StateReason = ShareSynchronizer.OverrideNotTakingEffectReason });

        var outcome = Sut().Restore();

        Assert.True(outcome.Done, outcome.Message);
        Assert.Equal(new[] { "Admin", "Adult", "Kid1", "Kid2" }, outcome.RestoredUsers.Select(u => u.Name).OrderBy(n => n));
        Assert.False(Directory.Exists(Folder(r)));
        var stored = Stored(r.Id);
        Assert.Equal(ShareState.Closed, stored.State);
        Assert.Equal(ShareSynchronizer.SwitchedOffReason, stored.StateReason);
        Assert.Equal(string.Empty, stored.FolderName);
        Assert.Equal(_now, stored.ClosedUtc);
        Assert.Contains(lib, _gw.RemovedLibraries);
        Assert.All(_gw.Users.Values, u => Assert.True(u.AllLibraries));
        Assert.All(_gw.Users.Values, u => Assert.DoesNotContain(u.BlockedTags, LibraryAccessManager.IsPluginTag));
        Assert.Empty(_store.Snapshots());
        Assert.Empty(_store.Libraries());
        Assert.False(Sync().IsInCharge());
    }

    [Fact]
    public void I1_restore_leaves_a_narrowed_profile_on_its_own_list_and_names_it_and_the_audit_page_warns_first()
    {
        var r = Share(_film, _kid1);
        Assert.True(Shares().Remove(r.Id, _adult));
        var given = _gw.Users[_kid2].EnabledLibraries;
        _gw.Users[_kid2] = _gw.Users[_kid2] with { EnabledLibraries = new List<Guid>() }; // a parent took every library away

        Assert.Equal(new[] { "Kid2" }, Sut().Build().NarrowedUsers.Select(u => u.Name));
        var outcome = Sut().Restore();

        Assert.True(outcome.Done, outcome.Message);
        Assert.Equal(new[] { "Kid2" }, outcome.LeftOnOwnList!.Select(u => u.Name));
        Assert.DoesNotContain("Kid2", outcome.RestoredUsers.Select(u => u.Name));
        Assert.False(_gw.Users[_kid2].AllLibraries);
        Assert.Empty(_gw.Users[_kid2].EnabledLibraries);
        Assert.True(_gw.Users[_kid1].AllLibraries);
        Assert.Empty(_store.Snapshots());
        Assert.NotEmpty(given);
    }

    [Fact]
    public void I1_the_uninstall_leaves_a_narrowed_profile_on_its_own_list_and_its_summary_names_it()
    {
        Share(_film, _kid1);
        _gw.Users[_kid2] = _gw.Users[_kid2] with { EnabledLibraries = new List<Guid>() };

        var outcome = Sut().Uninstall();

        Assert.True(outcome.Done, outcome.Message);
        Assert.False(_gw.Users[_kid2].AllLibraries);
        Assert.True(_gw.Users[_kid1].AllLibraries);
        Assert.Equal(new[] { "Kid2" }, outcome.LeftOnOwnList!.Select(u => u.Name));
        Assert.Contains("left on their own library list because it was changed after the plugin moved them: Kid2", outcome.Summary, StringComparison.Ordinal);
        Assert.StartsWith("Shared With Me uninstall left nothing behind.", outcome.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_stops_before_touching_access_when_a_copy_cannot_be_removed()
    {
        var r = Share(_film, _kid1);
        var lib = Library(_kid1);
        _store.Upsert(r with { State = ShareState.Broken, StateReason = ShareSynchronizer.OverrideNotTakingEffectReason });
        _builder.FailRemove = true;
        var accessWrites = _gw.LibraryAccessCalls.Count;

        var outcome = Sut().Restore();

        Assert.False(outcome.Done);
        Assert.Equal(AuditService.CopyNotRemovedMessage, outcome.Message);
        Assert.Empty(outcome.RestoredUsers);
        var stored = Stored(r.Id);
        Assert.Equal(ShareState.Broken, stored.State);
        Assert.Equal(ShareService.CouldNotRemoveReason, stored.StateReason);
        Assert.Equal(r.FolderName, stored.FolderName);
        Assert.Equal(accessWrites, _gw.LibraryAccessCalls.Count);
        Assert.DoesNotContain(lib, _gw.RemovedLibraries);
        Assert.All(_gw.Users.Values, u => Assert.False(u.AllLibraries));
        Assert.NotEmpty(_store.Snapshots());
    }

    [Fact]
    public void Restore_removes_a_share_layout_library_the_store_did_not_know_before_restoring_access()
    {
        var r = Share(_film, _kid1);
        Assert.True(Shares().Remove(r.Id, _adult));
        var unknown = _gw.SeedLibrary(MoviesRoot(_kid2)); // a share-layout library with no store entry (lost store, crash)
        Assert.True(Sync().IsInCharge()); // control: it alone would keep the plugin in charge

        var outcome = Sut().Restore();

        Assert.True(outcome.Done, outcome.Message);
        Assert.Contains(unknown, _gw.RemovedLibraries);
        var removedAt = _gw.Calls.FindIndex(c => c.Method == nameof(IServerGateway.RemoveLibrary) && Equals(c.Args[0], unknown));
        var firstRestore = _gw.Calls.FindIndex(c => c.Method == nameof(IServerGateway.SetLibraryAccess) && Equals(c.Args[1], true));
        Assert.True(removedAt >= 0 && firstRestore > removedAt, "the unknown library must go before anyone is put back on all libraries");
        Assert.All(_gw.Users.Values, u => Assert.True(u.AllLibraries));
        Assert.False(Sync().IsInCharge());
    }

    [Fact]
    public void Restore_stops_when_a_library_in_the_share_folder_also_reads_other_folders()
    {
        var r = Share(_film, _kid1);
        Assert.True(Shares().Remove(r.Id, _adult));
        _gw.SeedLibrary(MoviesRoot(_kid2));
        _gw.ThrowOnce = nameof(IServerGateway.FindLibraryByPath); // the real gateway throws for a mixed library
        var accessWrites = _gw.LibraryAccessCalls.Count;

        var outcome = Sut().Restore();

        Assert.False(outcome.Done);
        Assert.Equal(AuditService.MixedLibraryMessage, outcome.Message);
        Assert.Equal(accessWrites, _gw.LibraryAccessCalls.Count);
        Assert.All(_gw.Users.Values, u => Assert.False(u.AllLibraries));
    }

    [Fact]
    public void Restore_stops_before_touching_access_when_a_share_layout_library_has_no_id()
    {
        var r = Share(_film, _kid1);
        Assert.True(Shares().Remove(r.Id, _adult));
        _gw.AddRealLibrary(MoviesRoot(_kid2)); // listed by path, but FindLibraryByPath finds no id for it
        var snapshots = _store.Snapshots().Count;

        var outcome = Sut().Restore();

        Assert.False(outcome.Done);
        Assert.Equal(AuditService.LibraryNotReadyMessage, outcome.Message);
        Assert.DoesNotContain(_gw.LibraryAccessCalls, c => c.AllLibraries);
        Assert.All(_gw.Users.Values, u => Assert.False(u.AllLibraries));
        Assert.Equal(snapshots, _store.Snapshots().Count);
        Assert.NotEqual(0, snapshots);
    }

    [Fact]
    public void Restore_is_refused_while_the_share_folder_setting_overlaps_a_library()
    {
        var r = Share(_film, _kid1);
        Assert.True(Shares().Remove(r.Id, _adult));
        _gw.AddRealLibrary(Path.Combine(_shareRoot, "stray"));
        var before = Writes();

        var outcome = Sut().Restore();

        Assert.False(outcome.Done);
        Assert.Equal(Sync().CurrentSetupProblem(), outcome.Message); // the same actionable sentence as the audit shows
        Assert.Contains("then press Check now.", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(before, Writes());
    }

    [Fact]
    public void The_setup_problem_names_the_library_and_says_what_to_do_while_in_charge()
    {
        Share(_film, _kid1);
        var stray = Path.Combine(_shareRoot, "stray");
        _gw.AddRealLibrary(stray);
        _gw.LibraryNames[stray] = "Holiday Films";

        var problem = Sut().Build().SetupProblem!;

        Assert.StartsWith("The library \"Holiday Films\" uses a folder that overlaps the share folder. Remove that library (Dashboard, Libraries) or move it, then press Check now.", problem, StringComparison.Ordinal);
        Assert.Contains(stray, problem, StringComparison.Ordinal);
        Assert.DoesNotContain("choose a different share folder", problem, StringComparison.Ordinal); // the field is locked
        Assert.True(Sut().Build().ShareRootLocked);
    }

    [Fact]
    public void The_setup_problem_offers_another_share_folder_when_not_in_charge()
    {
        var stray = Path.Combine(_shareRoot, "stray");
        _gw.AddRealLibrary(stray);
        _gw.LibraryNames[stray] = "Holiday Films";

        var report = Sut().Build();

        Assert.False(report.InCharge);
        Assert.False(report.ShareRootLocked);
        Assert.StartsWith("The library \"Holiday Films\" uses a folder that overlaps the share folder. Remove that library (Dashboard, Libraries) or move it, or choose a different share folder below.", report.SetupProblem, StringComparison.Ordinal);
        Assert.DoesNotContain(_shareRoot, ShareService.SetupProblemMessage, StringComparison.Ordinal); // the sharer's text stays path-free
        Assert.DoesNotContain("/", ShareService.SetupProblemMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_stops_before_touching_access_when_a_share_library_cannot_be_removed()
    {
        var r = Share(_film, _kid1);
        Assert.True(Shares().Remove(r.Id, _adult));
        _gw.SeedLibrary(MoviesRoot(_kid2));
        _gw.ThrowOnce = nameof(IServerGateway.RemoveLibrary);

        var outcome = Sut().Restore();

        Assert.False(outcome.Done);
        Assert.Equal(AuditService.LibraryNotRemovedMessage, outcome.Message);
        Assert.DoesNotContain(_gw.LibraryAccessCalls, c => c.AllLibraries);
        Assert.All(_gw.Users.Values, u => Assert.False(u.AllLibraries));
        Assert.NotEmpty(_store.Snapshots());
    }

    [Fact]
    public void A_sync_straight_after_a_successful_restore_changes_nothing()
    {
        var r = Share(_film, _kid1);
        RegisterCopy(r);
        Assert.True(Shares().Remove(r.Id, _adult));
        var c = Share(_film2, _kid2);
        _store.Upsert(c with { State = ShareState.Broken, StateReason = ShareSynchronizer.OverrideNotTakingEffectReason });
        Assert.True(Sut().Restore().Done);
        var calls = _gw.Calls.Count(x => x.Method is nameof(IServerGateway.SetLibraryAccess) or nameof(IServerGateway.SetBlockedTags)
            or nameof(IServerGateway.CreateLibrary) or nameof(IServerGateway.RemoveLibrary) or nameof(IServerGateway.QueueScan)
            or nameof(IServerGateway.QueueItemRefresh));
        var records = _store.All();
        var users = _gw.Users.Values.Select(u => (u.Id, u.AllLibraries, string.Join(",", u.BlockedTags), string.Join(",", u.EnabledLibraries))).ToList();

        var report = Sync().Run();
        Sync().EnsureIsolation();

        Assert.Equal(new SyncReport(0, 0, 0, 0, 0), report);
        Assert.Equal(calls, _gw.Calls.Count(x => x.Method is nameof(IServerGateway.SetLibraryAccess) or nameof(IServerGateway.SetBlockedTags)
            or nameof(IServerGateway.CreateLibrary) or nameof(IServerGateway.RemoveLibrary) or nameof(IServerGateway.QueueScan)
            or nameof(IServerGateway.QueueItemRefresh)));
        Assert.Equal(records.Select(Shape), _store.All().Select(Shape));
        Assert.Equal(users, _gw.Users.Values.Select(u => (u.Id, u.AllLibraries, string.Join(",", u.BlockedTags), string.Join(",", u.EnabledLibraries))).ToList());
    }

    // ---------- Uninstall (forced teardown) ----------

    private string StoreDir => Path.Combine(_dir, "store");

    private string RetiredStore => Path.Combine(StoreDir, "shares.uninstalled-20261119T120000Z.json");

    private bool BlocksAnyPluginTag(Guid user) => _gw.Users[user].BlockedTags.Any(LibraryAccessManager.IsPluginTag);

    // Two Active shares (two recipients), the playlists made, an unrelated block on Kid2, a user-made playlist.
    private (ShareRecord Kid1, ShareRecord Kid2, Guid UserPlaylist, SharePlaylistManager Playlists) Populated()
    {
        _gw.Users[_kid2] = _gw.Users[_kid2] with { BlockedTags = new List<string> { "horror" } };
        var kid1 = Share(_film, _kid1);
        var kid2 = Share(_film2, _kid2);
        var playlists = Playlists();
        playlists.EnsurePlaylists();
        Assert.NotEmpty(_store.SharePlaylists());
        var mine = _gw.SeedPlaylist(_adult, "Films for the weekend", _film);
        return (kid1, kid2, mine, playlists);
    }

    [Fact]
    public void Uninstall_with_active_shares_removes_every_trace_and_puts_everyone_back()
    {
        var (kid1, kid2, mine, playlists) = Populated();
        var libs = new[] { Library(_kid1), Library(_kid2) };
        var stray = Path.Combine(MoviesRoot(_kid1), "Old Film (1999) [swm-0123abcd]"); // a folder no record knows
        Directory.CreateDirectory(stray);
        var temp = Path.Combine(MoviesRoot(_kid2), ".swm-tmp-" + Guid.NewGuid().ToString("N")); // a build cut short
        Directory.CreateDirectory(temp);

        var outcome = Sut(playlists).Uninstall();

        Assert.True(outcome.Done, outcome.Message);
        Assert.Equal(new[] { mine }, _gw.Playlists.Keys);
        Assert.All(libs, l => Assert.Contains(l, _gw.RemovedLibraries));
        Assert.Empty(_gw.LibraryPaths);
        Assert.All(_gw.Users.Values, u => Assert.True(u.AllLibraries, u.Name));
        Assert.All(_gw.Users.Values, u => Assert.False(BlocksAnyPluginTag(u.Id), u.Name));
        Assert.Equal(new[] { "horror" }, _gw.Users[_kid2].BlockedTags);
        Assert.False(Directory.Exists(_shareRoot)); // the default location, empty: removed
        Assert.False(File.Exists(Path.Combine(StoreDir, "shares.json")));
        Assert.True(File.Exists(RetiredStore));
        var retired = new ShareStore(StoreDir);
        File.Copy(RetiredStore, Path.Combine(StoreDir, "shares.json"));
        Assert.All(retired.All(), r => Assert.Equal(ShareState.Closed, r.State));
        Assert.Empty(retired.Snapshots());
        Assert.Empty(retired.Libraries());
        Assert.Empty(retired.SharePlaylists());
        Assert.Equal(0, _tvOff); // the setting is left as it was: a reinstall starts from the admin's own choice
    }

    [Fact]
    public void Uninstall_runs_its_steps_in_the_safe_order()
    {
        var (kid1, _, _, playlists) = Populated();
        _gw.Calls.Clear();
        var folderGoneBeforeLibrary = false;
        _gw.ThrowWhen = (method, _) =>
        {
            if (method == nameof(IServerGateway.RemoveLibrary))
            {
                folderGoneBeforeLibrary |= !Directory.Exists(Folder(kid1));
            }

            return false;
        };

        Assert.True(Sut(playlists).Uninstall().Done);

        int First(string m, Func<object?[], bool>? when = null) => _gw.Calls.FindIndex(c => c.Method == m && (when?.Invoke(c.Args) ?? true));
        int Last(string m) => _gw.Calls.FindLastIndex(c => c.Method == m);
        Assert.True(folderGoneBeforeLibrary, "copies must be off disk before their library goes");
        Assert.True(Last(nameof(IServerGateway.RemovePlaylist)) < First(nameof(IServerGateway.RemoveLibrary)));
        Assert.True(Last(nameof(IServerGateway.RemoveLibrary)) < First(nameof(IServerGateway.SetBlockedTags)));
        Assert.True(Last(nameof(IServerGateway.SetBlockedTags)) < First(nameof(IServerGateway.SetLibraryAccess), a => (bool)a[1]!));
        Assert.True(File.Exists(RetiredStore));
    }

    [Fact]
    public void After_uninstall_nothing_shares_syncs_or_makes_playlists_again()
    {
        var (_, _, _, playlists) = Populated();
        Assert.True(Sut(playlists).Uninstall().Done);
        _gw.Calls.Clear();

        var (decision, record) = Shares().Create(_adult, _film, _kid1);
        Sync().Run();
        Sync().EnsureIsolation();
        playlists.Run(true);
        playlists.EnsurePlaylists();
        playlists.Reconcile(true);
        var restore = Sut(playlists).Restore();

        Assert.Equal(Refusal.ShareFolderUnusable, decision.Refusal);
        Assert.Equal(ShareService.UninstallingMessage, decision.Message);
        Assert.Null(record);
        Assert.False(restore.Done);
        Assert.DoesNotContain(_gw.Calls, c => c.Method is nameof(IServerGateway.SetLibraryAccess) or nameof(IServerGateway.SetBlockedTags)
            or nameof(IServerGateway.CreateLibrary) or nameof(IServerGateway.CreatePlaylist) or nameof(IServerGateway.QueueScan));
        Assert.False(Directory.Exists(_shareRoot));
        Assert.False(File.Exists(Path.Combine(StoreDir, "shares.json")));
    }

    [Fact]
    public void A_copy_that_cannot_be_removed_does_not_stop_the_rest()
    {
        var (kid1, _, _, playlists) = Populated();
        _builder.FailRemove = true;

        var outcome = Sut(playlists).Uninstall();

        Assert.False(outcome.Done);
        Assert.True(Directory.Exists(Folder(kid1))); // left on disk, and said so
        Assert.Empty(_gw.LibraryPaths); // ...but no library reads it any more
        Assert.All(_gw.Users.Values, u => Assert.True(u.AllLibraries, u.Name));
        Assert.All(_gw.Users.Values, u => Assert.False(BlocksAnyPluginTag(u.Id), u.Name));
        Assert.True(File.Exists(RetiredStore));
    }

    [Theory]
    [InlineData("remove-throws")]
    [InlineData("mixed")]
    [InlineData("no-id")]
    public void While_a_share_library_remains_nobody_is_put_back_on_all_libraries(string how)
    {
        var (kid1, _, _, playlists) = Populated();
        var kid1Lib = Library(_kid1);
        switch (how)
        {
            case "remove-throws":
                _gw.ThrowWhen = (m, a) => m == nameof(IServerGateway.RemoveLibrary) && Equals(a[0], kid1Lib);
                break;
            case "mixed": // the real gateway throws for a library that also reads other folders
                _gw.ThrowWhen = (m, a) => m == nameof(IServerGateway.FindLibraryByPath) && Equals(a[0], MoviesRoot(_kid1));
                break;
            default: // listed by path, but no id: cannot be removed
                _gw.AddRealLibrary(Path.Combine(_shareRoot, Guid.NewGuid().ToString("N"), "movies"));
                break;
        }

        var outcome = Sut(playlists).Uninstall();

        Assert.False(outcome.Done);
        Assert.DoesNotContain(_gw.LibraryAccessCalls, c => c.AllLibraries);
        Assert.All(_gw.Users.Values, u => Assert.False(u.AllLibraries, u.Name));
        Assert.False(Directory.Exists(Folder(kid1))); // the copies still went
        Assert.Equal(new[] { _film }, _gw.Playlists.Values.SelectMany(p => p.Items)); // only the user-made playlist is left
        if (how != "no-id")
        {
            // Kid1's library is still there: every other user still blocks Kid1's tag; Kid1 does not.
            Assert.All(_gw.Users.Values.Where(u => u.Id != _kid1), u => Assert.Contains(LibraryAccessManager.RecipientTag(_kid1), u.BlockedTags));
            Assert.DoesNotContain(LibraryAccessManager.RecipientTag(_kid1), _gw.Users[_kid1].BlockedTags);
        }

        // Kid2's library went, so nobody blocks her tag any more.
        Assert.All(_gw.Users.Values, u => Assert.DoesNotContain(LibraryAccessManager.RecipientTag(_kid2), u.BlockedTags));
        File.Copy(RetiredStore, Path.Combine(StoreDir, "shares.json"));
        Assert.NotEmpty(new ShareStore(StoreDir).Snapshots()); // the record of who was moved stays with the admin
    }

    [Fact]
    public void A_playlist_that_cannot_be_removed_keeps_its_owners_tag_blocked_and_the_rest_goes_on()
    {
        var (_, _, _, playlists) = Populated();
        var stuck = _store.SharePlaylists()[_adult][_kid1];
        _gw.ThrowWhen = (m, a) => m == nameof(IServerGateway.RemovePlaylist) && Equals(a[0], stuck);

        var outcome = Sut(playlists).Uninstall();

        Assert.False(outcome.Done);
        Assert.True(_gw.Playlists.ContainsKey(stuck));
        Assert.All(_gw.Users.Values.Where(u => u.Id != _adult), u => Assert.Contains(LibraryAccessManager.RecipientTag(_adult), u.BlockedTags));
        Assert.All(_gw.Users.Values, u => Assert.True(u.AllLibraries, u.Name)); // playlists do not hold up library access
        Assert.Empty(_gw.LibraryPaths);
        Assert.True(File.Exists(RetiredStore));
    }

    [Fact]
    public void A_failed_policy_write_for_one_user_does_not_stop_the_others()
    {
        var (_, _, _, playlists) = Populated();
        _gw.ThrowWhen = (m, a) => (m == nameof(IServerGateway.SetBlockedTags) || m == nameof(IServerGateway.SetLibraryAccess)) && Equals(a[0], _kid2);

        var outcome = Sut(playlists).Uninstall();

        Assert.False(outcome.Done);
        Assert.All(_gw.Users.Values.Where(u => u.Id != _kid2), u => Assert.True(u.AllLibraries, u.Name));
        Assert.All(_gw.Users.Values.Where(u => u.Id != _kid2), u => Assert.False(BlocksAnyPluginTag(u.Id), u.Name));
        File.Copy(RetiredStore, Path.Combine(StoreDir, "shares.json"));
        Assert.Equal(new[] { _kid2 }, new ShareStore(StoreDir).Snapshots().Select(x => x.UserId)); // only Kid2 is still owed her access
    }

    [Fact]
    public void A_failed_user_lookup_keeps_everyone_isolated_and_keeps_the_record_of_who_was_moved()
    {
        var r = Share(_film, _kid1);
        Assert.True(Shares().Remove(r.Id, _adult)); // the library goes; the snapshots stay
        var moved = _store.Snapshots().Count;
        Assert.NotEqual(0, moved);
        _gw.ThrowWhen = (m, _) => m is nameof(IServerGateway.GetUsers) or nameof(IServerGateway.GetUser);

        var outcome = Sut().Uninstall();

        Assert.False(outcome.Done);
        Assert.All(_gw.Users.Values, u => Assert.False(u.AllLibraries, u.Name));
        File.Copy(RetiredStore, Path.Combine(StoreDir, "shares.json"));
        Assert.Equal(moved, new ShareStore(StoreDir).Snapshots().Count);
    }

    [Fact]
    public void A_corrupt_store_at_uninstall_still_removes_the_libraries_and_changes_no_policy()
    {
        var (_, _, _, playlists) = Populated();
        var tags = _gw.Users.Values.ToDictionary(u => u.Id, u => string.Join(",", u.BlockedTags));
        File.WriteAllText(Path.Combine(StoreDir, "shares.json"), "{ not json");

        var outcome = Sut(playlists).Uninstall();

        Assert.False(outcome.Done);
        Assert.Empty(_gw.LibraryPaths); // found by path, so still removed
        Assert.All(_gw.Users.Values, u => Assert.False(u.AllLibraries, u.Name));
        Assert.Equal(tags, _gw.Users.Values.ToDictionary(u => u.Id, u => string.Join(",", u.BlockedTags)));
        Assert.Equal("{ not json", File.ReadAllText(RetiredStore)); // kept, renamed, for the admin
        Assert.StartsWith("Shared With Me uninstall did not finish everything.", outcome.Summary, StringComparison.Ordinal);
        Assert.Contains("Profiles not put back on all libraries: none could be checked (the share records could not be read).", outcome.Summary, StringComparison.Ordinal);
        Assert.Contains("Profiles whose swm- blocked tags were not lifted: all profiles (tags were not lifted because the share records could not be read).", outcome.Summary, StringComparison.Ordinal);
        Assert.Contains("Share record: renamed to " + RetiredStore + ".", outcome.Summary, StringComparison.Ordinal);
        // the playlist map could not be read, but every plugin playlist carries its owner's tag: all found and removed
        Assert.Equal(new[] { "Films for the weekend" }, _gw.Playlists.Values.Select(p => p.Name));
    }

    [Fact]
    public void Libraries_that_cannot_be_listed_leave_everyone_isolated_with_no_tag_lifted_and_the_rest_still_runs()
    {
        var (kid1, kid2, _, playlists) = Populated();
        var libs = new[] { Library(_kid1), Library(_kid2) };
        var tags = _gw.Users.Values.ToDictionary(u => u.Id, u => string.Join(",", u.BlockedTags));
        _gw.ThrowWhen = (m, _) => m == nameof(IServerGateway.GetAllLibraryPaths);
        _gw.ThrowWith = () => new IOException("the library list is unreadable");

        var outcome = Sut(playlists).Uninstall();

        Assert.False(outcome.Done);
        Assert.All(libs, l => Assert.Contains(l, _gw.RemovedLibraries)); // the store's own libraries still went
        Assert.False(Directory.Exists(Folder(kid1)));
        Assert.False(Directory.Exists(Folder(kid2)));
        Assert.Equal(new[] { "Films for the weekend" }, _gw.Playlists.Values.Select(p => p.Name));
        Assert.All(_gw.Users.Values, u => Assert.False(u.AllLibraries, u.Name));
        Assert.Equal(tags, _gw.Users.Values.ToDictionary(u => u.Id, u => string.Join(",", u.BlockedTags)));
        Assert.True(File.Exists(RetiredStore));
        Assert.Contains("Libraries not removed: unknown (the libraries could not be listed).", outcome.Summary, StringComparison.Ordinal);
        Assert.Contains("Profiles not put back on all libraries: Admin, Adult, Kid1, Kid2 (the library state is unknown).", outcome.Summary, StringComparison.Ordinal);
        Assert.Contains("Profiles whose swm- blocked tags were not lifted: all profiles (tags were not lifted because the library state is unknown (the libraries could not be listed)).", outcome.Summary, StringComparison.Ordinal);
        Assert.Contains("Share record: renamed to " + RetiredStore + ".", outcome.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_library_lookup_that_throws_unexpectedly_counts_as_left_and_the_rest_still_runs()
    {
        var (kid1, _, _, playlists) = Populated();
        var kid2Lib = Library(_kid2);
        _gw.ThrowWhen = (m, a) => m == nameof(IServerGateway.FindLibraryByPath) && Equals(a[0], MoviesRoot(_kid1));
        _gw.ThrowWith = () => new IOException("lookup failed");

        var outcome = Sut(playlists).Uninstall();

        Assert.False(outcome.Done);
        Assert.Contains(kid2Lib, _gw.RemovedLibraries);
        Assert.DoesNotContain(_gw.LibraryAccessCalls, c => c.AllLibraries);
        Assert.False(Directory.Exists(Folder(kid1)));
        Assert.True(File.Exists(RetiredStore));
        Assert.Contains("Libraries not removed: " + MoviesRoot(_kid1) + ".", outcome.Summary, StringComparison.Ordinal);
        Assert.Contains("Profiles not put back on all libraries: Admin, Adult, Kid1, Kid2 (a share library remains).", outcome.Summary, StringComparison.Ordinal);
        Assert.Contains("Profiles whose swm- blocked tags were not lifted: every profile still blocks the tags of Kid1 (a share library or playlist of theirs remains).", outcome.Summary, StringComparison.Ordinal);
        Assert.All(_gw.Users.Values.Where(u => u.Id != _kid1), u => Assert.Contains(LibraryAccessManager.RecipientTag(_kid1), u.BlockedTags));
    }

    [Fact]
    public void A_folder_that_cannot_be_listed_is_reported_and_the_store_is_still_renamed()
    {
        var (_, _, _, playlists) = Populated();
        var kid1Dir = Path.Combine(_shareRoot, _kid1.ToString("N"));
        var sut = Sut(playlists);
        sut.ListFolder = path => path == kid1Dir || path == _shareRoot ? throw new IOException("unreadable") : Directory.EnumerateFileSystemEntries(path);

        var outcome = sut.Uninstall();

        Assert.False(outcome.Done);
        Assert.Empty(_gw.LibraryPaths);
        Assert.All(_gw.Users.Values, u => Assert.True(u.AllLibraries, u.Name)); // folders do not hold up access
        Assert.True(File.Exists(RetiredStore));
        Assert.Contains(kid1Dir, outcome.Summary, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_shareRoot, _kid2.ToString("N"))));
    }

    [Fact]
    public void The_summary_names_a_blocked_tags_write_that_failed()
    {
        var (_, _, _, playlists) = Populated();
        _gw.ThrowWhen = (m, a) => m == nameof(IServerGateway.SetBlockedTags) && Equals(a[0], _kid2);

        var outcome = Sut(playlists).Uninstall();

        Assert.False(outcome.Done);
        Assert.Contains("Profiles whose swm- blocked tags were not lifted: Kid2.", outcome.Summary, StringComparison.Ordinal);
        Assert.Contains("Policy writes that failed: Kid2: blocked tags.", outcome.Summary, StringComparison.Ordinal);
        Assert.Contains("Profiles not put back on all libraries: none.", outcome.Summary, StringComparison.Ordinal);
        Assert.Contains("Libraries not removed: none.", outcome.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_summary_names_a_library_access_write_that_failed()
    {
        var (_, _, _, playlists) = Populated();
        _gw.ThrowWhen = (m, a) => m == nameof(IServerGateway.SetLibraryAccess) && Equals(a[0], _kid2) && Equals(a[1], true);

        var outcome = Sut(playlists).Uninstall();

        Assert.False(outcome.Done);
        Assert.Contains("Profiles not put back on all libraries: Kid2.", outcome.Summary, StringComparison.Ordinal);
        Assert.Contains("Policy writes that failed: Kid2: library access.", outcome.Summary, StringComparison.Ordinal);
        Assert.Contains("Profiles whose swm- blocked tags were not lifted: none.", outcome.Summary, StringComparison.Ordinal);
        Assert.Contains("Share record: renamed to " + RetiredStore + ".", outcome.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_summary_names_a_share_library_that_could_not_be_removed()
    {
        var (_, _, _, playlists) = Populated();
        var kid1Lib = Library(_kid1);
        _gw.ThrowWhen = (m, a) => m == nameof(IServerGateway.RemoveLibrary) && Equals(a[0], kid1Lib);

        var outcome = Sut(playlists).Uninstall();

        Assert.Equal(
            "Shared With Me uninstall did not finish everything. "
            + $"Libraries not removed: {MoviesRoot(_kid1)}. "
            + "Copy folders not removed: none. "
            + "Playlists not removed: none. "
            + "Profiles not put back on all libraries: Admin, Adult, Kid1, Kid2 (a share library remains). "
            + "Profiles whose swm- blocked tags were not lifted: every profile still blocks the tags of Kid1 (a share library or playlist of theirs remains). "
            + "Policy writes that failed: none. "
            + $"Share record: renamed to {RetiredStore}. "
            + "Failed steps: 2 (each logged above).",
            outcome.Summary);
    }

    [Fact]
    public void A_clean_uninstall_summary_says_nothing_was_left()
    {
        var (_, _, _, playlists) = Populated();

        var outcome = Sut(playlists).Uninstall();

        Assert.True(outcome.Done, outcome.Message);
        Assert.Equal("Shared With Me uninstall left nothing behind. The share record was renamed to " + RetiredStore + ".", outcome.Summary);
    }

    [Fact]
    public void After_uninstall_clearing_notes_does_not_write_the_store_again()
    {
        var (_, _, _, playlists) = Populated();
        _store.AddRefusalNote(new RefusalNote(_adult, _kid1, "T", "m", _now));
        Assert.True(Sut(playlists).Uninstall().Done);

        playlists.ClearNotes(_adult);

        Assert.False(File.Exists(Path.Combine(StoreDir, "shares.json")));
    }

    [Fact]
    public void A_share_root_that_is_not_the_default_location_is_kept_and_an_unknown_file_keeps_its_folder()
    {
        var (_, _, _, playlists) = Populated();
        _defaultRoot = Path.Combine(_dir, "somewhere-else");
        var note = Path.Combine(MoviesRoot(_kid2), "readme.txt");
        File.WriteAllText(note, "not the plugin's");

        Sut(playlists).Uninstall();

        Assert.True(Directory.Exists(_shareRoot));
        Assert.True(File.Exists(note));
        Assert.False(Directory.Exists(Path.Combine(_shareRoot, _kid1.ToString("N"))));
        Assert.Equal(new[] { _kid2.ToString("N") }, Directory.GetDirectories(_shareRoot).Select(Path.GetFileName));
    }

    [Fact]
    public void A_kind_folder_that_is_a_link_is_never_followed()
    {
        var (_, _, _, playlists) = Populated();
        var elsewhere = Path.Combine(_dir, "elsewhere");
        var victim = Path.Combine(elsewhere, "Precious (2000) [swm-feedbeef]");
        Directory.CreateDirectory(victim);
        var kid = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(_shareRoot, kid));
        Directory.CreateSymbolicLink(Path.Combine(_shareRoot, kid, "movies"), elsewhere);

        Sut(playlists).Uninstall();

        Assert.True(Directory.Exists(victim));
    }

    [Fact]
    public void Uninstall_with_no_store_and_nothing_shared_changes_nothing()
    {
        var outcome = Sut().Uninstall();

        Assert.True(outcome.Done, outcome.Message);
        Assert.DoesNotContain(_gw.Calls, c => c.Method is nameof(IServerGateway.SetLibraryAccess) or nameof(IServerGateway.SetBlockedTags) or nameof(IServerGateway.RemoveLibrary));
        Assert.Empty(Directory.Exists(StoreDir) ? Directory.GetFiles(StoreDir) : Array.Empty<string>());
    }

    // ---------- What each person sees ----------

    [Fact]
    public void Received_lists_active_shares_whose_copy_the_recipient_can_see()
    {
        var r = Share(_film, _kid1);
        Assert.Empty(Sut().Received(_kid1)); // not scanned yet: nothing to show
        var copy = RegisterCopy(r);

        var got = Sut().Received(_kid1);

        var only = Assert.Single(got);
        Assert.Equal(r.Id, only.Id);
        Assert.Equal("Spike Eighteen", only.Title);
        Assert.Equal("Adult", only.SharerName);
        Assert.Empty(Sut().Received(_kid2));
        Assert.Empty(Sut().Received(_adult));

        _gw.SetVisible(_kid1, copy, false);
        Assert.Empty(Sut().Received(_kid1));
    }

    [Fact]
    public void Received_leaves_out_closed_shares()
    {
        var r = Share(_film, _kid1);
        RegisterCopy(r);
        _store.Upsert(Stored(r.Id) with { State = ShareState.Closed, StateReason = ShareSynchronizer.NoLongerNeededReason });

        Assert.Empty(Sut().Received(_kid1));
    }

    // 0.2, B: a share whose time is up is gone from the recipient's list at once, before the sync has ended it; the audit
    // row carries the end time and says it has expired.
    [Fact]
    public void An_expired_share_leaves_Received_at_once_and_the_audit_row_says_so()
    {
        var r = Share(_film, _kid1);
        RegisterCopy(r);
        _store.Upsert(Stored(r.Id) with { ExpiresUtc = _now.AddSeconds(1) });

        var row = AuditOf(Sut().Build(), _kid1, r.Id);
        Assert.Equal(_now.AddSeconds(1), row.ExpiresUtc);
        Assert.False(row.Expired);
        Assert.Equal(_now.AddSeconds(1), Assert.Single(Sut().Received(_kid1)).ExpiresUtc);

        _store.Upsert(Stored(r.Id) with { ExpiresUtc = _now });

        Assert.Empty(Sut().Received(_kid1));
        Assert.True(AuditOf(Sut().Build(), _kid1, r.Id).Expired);

        _store.Upsert(Stored(r.Id) with { State = ShareState.Closed, StateReason = ShareSynchronizer.ExpiredReason, FolderName = string.Empty });
        row = AuditOf(Sut().Build(), _kid1, r.Id);
        Assert.False(row.Expired); // ended: the state says it
        Assert.Equal((ShareState.Closed, "expired"), (row.State, row.StateReason));
    }

    [Fact]
    public void SharedBy_hides_a_title_the_sharer_can_no_longer_see()
    {
        var r = Share(_film, _kid1);
        Assert.Equal(r.Id, Assert.Single(Sut().SharedBy(_adult).Shares).Id);
        Assert.Equal(0, Sut().SharedBy(_adult).HiddenCount);
        Assert.Empty(Sut().SharedBy(_kid2).Shares);
        Assert.Equal(0, Sut().SharedBy(_kid2).HiddenCount);

        _gw.SetVisible(_adult, _film, false);

        Assert.Empty(Sut().SharedBy(_adult).Shares);
        Assert.Equal(1, Sut().SharedBy(_adult).HiddenCount);
    }

    private sealed class TestBuilder : ShareFolderBuilder
    {
        public bool FailRemove { get; set; }

        public override void Remove(string kindRoot, string folderName)
        {
            if (FailRemove)
            {
                throw new IOException("Simulated failure removing " + folderName);
            }

            base.Remove(kindRoot, folderName);
        }
    }
}
