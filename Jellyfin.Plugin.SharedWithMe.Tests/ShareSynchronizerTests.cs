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

public sealed class ShareSynchronizerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "swm-sync-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServerGateway _gw = new();
    private readonly ShareStore _store;
    private readonly string _media;
    private readonly string _shareRoot;
    private readonly PluginGate _gate = new();
    private readonly TestBuilder _builder = new();
    private DateTime _now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    private readonly Guid _lib = Guid.NewGuid();
    private readonly Guid _adult = Guid.NewGuid();
    private readonly Guid _kid1 = Guid.NewGuid();
    private readonly Guid _kid2 = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _film = Guid.NewGuid();
    private readonly Guid _film2 = Guid.NewGuid();

    public ShareSynchronizerTests()
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
        AddFilm(_film, "Spike Eighteen", 2002, "GB-18", "1");
        AddFilm(_film2, "Spike Fifteen", 2001, "GB-15", "2");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ShareService Service() =>
        new(_gw, _store, new ShareEligibility(_gw, _store, () => WhoMayShare.Everyone), new LibraryAccessManager(_gw, _store), _builder, () => _shareRoot, _gate, NullLogger.Instance);

    private ShareSynchronizer Sut() =>
        new(_gw, _store, new LibraryAccessManager(_gw, _store), _builder, () => _shareRoot, _gate, NullLogger.Instance, () => _now);

    private void AddUser(Guid id, string name, int? cap, int? sub = null, bool admin = false) =>
        _gw.Users[id] = new UserInfo(id, name, admin, false, false, cap, sub, true, new List<Guid>(), Array.Empty<string>(), Array.Empty<string>());

    private string AddFile(string folderName, string fileName)
    {
        var folder = Path.Combine(_media, folderName);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, fileName);
        File.WriteAllText(file, "video");
        return file;
    }

    private void AddFilm(Guid id, string title, int year, string rating, string tmdb, Guid? library = null, string? file = null)
    {
        file ??= AddFile(title, title + ".mp4");
        _gw.Items[id] = new ItemInfo(id, ShareKind.Movie, title, year, rating, new Dictionary<string, string> { ["Tmdb"] = tmdb }, library ?? _lib, new List<string>(), file);
        _gw.Media[id] = new SourceMedia(file, Array.Empty<string>(), Array.Empty<(int, int, string)>());
        _gw.SetVisible(_adult, id);
        _gw.SetVisible(_admin, id);
    }

    private Guid AddFilmWithIds(string title, Dictionary<string, string> ids)
    {
        var id = Guid.NewGuid();
        AddFilm(id, title, 2002, "GB-18", "x", file: AddFile(title + " " + id.ToString("N")[..6], title + ".mp4"));
        _gw.Items[id] = _gw.Items[id] with { ProviderIds = ids };
        return id;
    }

    private string MoviesRoot(Guid recipient) => Path.Combine(_shareRoot, recipient.ToString("N"), "movies");

    private string Folder(ShareRecord r) => Path.Combine(MoviesRoot(r.RecipientUserId), r.FolderName);

    private ShareRecord Stored(Guid id) => _store.All().Single(r => r.Id == id);

    private Guid Library(Guid recipient) => _store.Libraries()[recipient][nameof(ShareKind.Movie)];

    // Shares a film and registers the copy as scanned and visible to its recipient, as the server would after a scan.
    private ShareRecord Share(Guid film, Guid recipient)
    {
        var (decision, record) = Service().Create(_adult, film, recipient);
        Assert.Equal(Refusal.None, decision.Refusal);
        RegisterCopy(record!);
        return record!;
    }

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

    private static string? LinkTarget(string folder) =>
        Directory.EnumerateFiles(folder).Select(f => new FileInfo(f)).FirstOrDefault(f => f.LinkTarget is not null)?.LinkTarget;

    private string Nfo(ShareRecord r) => File.ReadAllText(Path.Combine(Folder(r), r.FolderName + ".nfo"));

    private void SetCap(Guid user, int? cap, int? sub) => _gw.Users[user] = _gw.Users[user] with { CapScore = cap, CapSubScore = sub };

    private int Count(string method) => _gw.Calls.Count(c => c.Method == method);

    // ---------- Rule 1: recipient gone ----------

    [Fact]
    public void Rule1_recipient_gone_removes_folder_records_library_directory_and_tag()
    {
        var r = Share(_film, _kid1);
        var lib = Library(_kid1);
        _store.Upsert(r with { Id = Guid.NewGuid(), State = ShareState.Closed, FolderName = string.Empty, ClosedUtc = _now });
        _gw.Users.Remove(_kid1);

        var report = Sut().Run();

        Assert.Empty(_store.All());
        Assert.False(Directory.Exists(Path.Combine(_shareRoot, _kid1.ToString("N"))));
        Assert.Contains(lib, _gw.RemovedLibraries);
        Assert.False(_store.Libraries().ContainsKey(_kid1));
        Assert.All(_gw.Users.Values, u => Assert.DoesNotContain(LibraryAccessManager.RecipientTag(_kid1), u.BlockedTags));
        Assert.Equal(2, report.Removed);
    }

    // ---------- Rule 2: original gone ----------

    [Fact]
    public void A_relinked_share_leaves_the_playlist_view_so_its_new_original_is_mirrored_in()
    {
        var r = Share(_film, _kid1);
        _store.Upsert(Stored(r.Id) with { ViaPlaylist = true });
        _gw.Items.Remove(_film);
        var upgraded = Guid.NewGuid();
        AddFilm(upgraded, "Spike Eighteen Remastered", 2002, "GB-18", "1", file: AddFile("Spike Eighteen Remastered (2002)", "Spike Eighteen Remastered.mkv"));

        Assert.Equal(1, Sut().Run().Relinked);

        Assert.Equal(upgraded, Stored(r.Id).OriginalItemId);
        Assert.False(Stored(r.Id).ViaPlaylist);
    }

    [Fact]
    public void The_sync_purges_refusal_notes_older_than_30_days()
    {
        _store.AddRefusalNote(new RefusalNote(_adult, _kid1, "old", "m", _now - TimeSpan.FromDays(31)));
        _store.AddRefusalNote(new RefusalNote(_adult, _kid1, "new", "m", _now - TimeSpan.FromDays(29)));
        Share(_film, _kid1); // in charge

        Sut().Run();

        Assert.Equal(new[] { "new" }, _store.RefusalNotes().Select(n => n.Title));
    }

    [Fact]
    public void Rule2_one_match_relinks_and_rebuilds_with_tag_and_rating()
    {
        var r = Share(_film, _kid1);
        var oldFolder = Folder(r);
        _gw.Items.Remove(_film);
        var upgraded = Guid.NewGuid();
        AddFilm(upgraded, "Spike Eighteen Remastered", 2002, "GB-18", "1", file: AddFile("Spike Eighteen Remastered (2002)", "Spike Eighteen Remastered.mkv"));

        var report = Sut().Run();

        var s = Stored(r.Id);
        Assert.Equal(1, report.Relinked);
        Assert.Equal(ShareState.Active, s.State);
        Assert.Equal(upgraded, s.OriginalItemId);
        Assert.Equal("Spike Eighteen Remastered", s.Title);
        Assert.Equal("1", s.ProviderIds["Tmdb"]);
        Assert.NotEqual(r.FolderName, s.FolderName);
        Assert.False(Directory.Exists(oldFolder));
        Assert.Equal(_gw.Items[upgraded].Path, LinkTarget(Folder(s)));
        var nfo = Nfo(s);
        Assert.Contains("<tag>" + LibraryAccessManager.RecipientTag(_kid1) + "</tag>", nfo, StringComparison.Ordinal);
        Assert.Contains("<customrating>12</customrating>", nfo, StringComparison.Ordinal);
        Assert.Contains(Library(_kid1), _gw.ScanQueue);
    }

    [Fact]
    public void Rule2_no_match_breaks_the_share_and_removes_the_folder()
    {
        var r = Share(_film, _kid1);
        var lib = Library(_kid1);
        _gw.Items.Remove(_film);

        var report = Sut().Run();

        var s = Stored(r.Id);
        Assert.Equal(ShareState.Broken, s.State);
        Assert.Equal("original missing", s.StateReason);
        Assert.Equal(string.Empty, s.FolderName);
        Assert.False(Directory.Exists(Folder(r)));
        Assert.Contains(lib, _gw.RemovedLibraries); // a Broken record without a folder holds no library
        Assert.Equal(1, report.Broken);
    }

    [Fact]
    public void Rule2_two_matches_break_the_share()
    {
        var r = Share(_film, _kid1);
        _gw.Items.Remove(_film);
        AddFilm(Guid.NewGuid(), "Spike Eighteen", 2002, "GB-18", "1");
        AddFilm(Guid.NewGuid(), "Spike Eighteen Again", 2002, "GB-18", "1", file: AddFile("Again", "Again.mp4"));

        Sut().Run();

        var s = Stored(r.Id);
        Assert.Equal(ShareState.Broken, s.State);
        Assert.Equal("more than one match", s.StateReason);
        Assert.False(Directory.Exists(Folder(r)));
    }

    [Fact]
    public void Rule2_a_match_in_another_library_or_a_source_library_that_is_no_longer_real_is_no_match()
    {
        var r = Share(_film, _kid1);
        _gw.Items.Remove(_film);
        var other = _gw.AddRealLibrary(Path.Combine(_dir, "other"));
        AddFilm(Guid.NewGuid(), "Spike Eighteen", 2002, "GB-18", "1", library: other);

        Sut().Run();
        Assert.Equal("original missing", Stored(r.Id).StateReason);

        // A hit in the source library, but the source library is no longer a real library.
        var r2 = Share(_film2, _kid2);
        _gw.Items.Remove(_film2);
        AddFilm(Guid.NewGuid(), "Spike Fifteen", 2001, "GB-15", "2");
        _gw.RealLibraryIds.Remove(_lib);

        Sut().Run();
        Assert.Equal(ShareState.Broken, Stored(r2.Id).State);
        Assert.Equal(string.Empty, Stored(r2.Id).FolderName);
    }

    // ---------- Rule 3: recipient blocks a tag the original now carries ----------

    [Fact]
    public void Rule3_blocked_tag_closes_and_removes_the_folder()
    {
        var r = Share(_film, _kid1);
        _gw.Items[_film] = _gw.Items[_film] with { Tags = new List<string> { "Horror" } };
        _gw.Users[_kid1] = _gw.Users[_kid1] with { BlockedTags = _gw.Users[_kid1].BlockedTags.Append("horror").ToList() };

        var report = Sut().Run();

        var s = Stored(r.Id);
        Assert.Equal(ShareState.Closed, s.State);
        Assert.Equal(string.Empty, s.FolderName);
        Assert.Equal(_now, s.ClosedUtc);
        Assert.False(Directory.Exists(Folder(r)));
        Assert.Equal(1, report.Closed);
    }

    // ---------- Rule 4: recipient can now see the original ----------

    [Fact]
    public void Rule4_recipient_sees_the_original_closes_as_no_longer_needed()
    {
        var r = Share(_film, _kid1);
        var lib = Library(_kid1);
        _gw.SetVisible(_kid1, _film);

        var report = Sut().Run();

        var s = Stored(r.Id);
        Assert.Equal(ShareState.Closed, s.State);
        Assert.Equal("no longer needed", s.StateReason);
        Assert.Equal(string.Empty, s.FolderName);
        Assert.False(Directory.Exists(Folder(r)));
        Assert.Contains(lib, _gw.RemovedLibraries);
        Assert.Equal(1, report.Closed);
    }

    // ---------- Rule 5: links do not resolve ----------

    [Fact]
    public void Rule5_dangling_link_is_rebuilt_to_the_current_file()
    {
        var r = Share(_film, _kid1);
        var moved = AddFile("Spike Eighteen Moved", "Spike Eighteen.mp4");
        File.Delete(_gw.Items[_film].Path!);
        _gw.Items[_film] = _gw.Items[_film] with { Path = moved };
        _gw.Media[_film] = new SourceMedia(moved, Array.Empty<string>(), Array.Empty<(int, int, string)>());

        var report = Sut().Run();

        var s = Stored(r.Id);
        Assert.Equal(ShareState.Active, s.State);
        Assert.True(_builder.LinksResolve(MoviesRoot(_kid1), s.FolderName));
        Assert.Equal(moved, LinkTarget(Folder(s)));
        Assert.Contains(LibraryAccessManager.RecipientTag(_kid1), Nfo(s), StringComparison.Ordinal);
        Assert.Equal(1, report.Reapplied);
    }

    // ---------- Rule 6: rating for the recipient's current cap ----------

    [Fact]
    public void Rule6_cap_lowered_12_to_8_rewrites_customrating_to_the_8_level_option()
    {
        var r = Share(_film, _kid1);
        SetCap(_kid1, 8, 0);
        _gw.ScanQueue.Clear();

        var report = Sut().Run();

        var s = Stored(r.Id);
        Assert.Equal(ShareState.Active, s.State);
        Assert.Contains("<customrating>PG</customrating>", Nfo(s), StringComparison.Ordinal);
        Assert.Contains("<mpaa>GB-18</mpaa>", Nfo(s), StringComparison.Ordinal);
        Assert.Contains(LibraryAccessManager.RecipientTag(_kid1), Nfo(s), StringComparison.Ordinal);
        Assert.Contains(Library(_kid1), _gw.ScanQueue);
        Assert.Equal(1, report.Reapplied);

        // Settled: the next run changes nothing.
        Assert.Equal(0, Sut().Run().Reapplied);
    }

    [Fact]
    public void Rule6_no_rating_fits_breaks_and_removes_the_folder()
    {
        var r = Share(_film, _kid1);
        SetCap(_kid1, 3, 0);

        Sut().Run();

        var s = Stored(r.Id);
        Assert.Equal(ShareState.Broken, s.State);
        Assert.Equal(string.Empty, s.FolderName);
        Assert.False(Directory.Exists(Folder(r)));
    }

    [Fact]
    public void Rule6_uncapped_recipient_who_still_cannot_see_the_original_gets_no_customrating()
    {
        var r = Share(_film, _kid1);
        SetCap(_kid1, null, null);

        Sut().Run();

        var s = Stored(r.Id);
        Assert.Equal(ShareState.Active, s.State);
        Assert.DoesNotContain("customrating", Nfo(s), StringComparison.Ordinal);
        Assert.Contains(LibraryAccessManager.RecipientTag(_kid1), Nfo(s), StringComparison.Ordinal);
    }

    // ---------- Rule 7: the recipient cannot see the copy ----------

    [Fact]
    public void Rule7_first_failure_rewrites_and_refreshes_second_breaks_and_keeps_the_copy_visible_again_recovers()
    {
        var r = Share(_film, _kid1);
        var lib = Library(_kid1);
        var copy = _gw.ItemsByPath.Values.Single();
        _gw.SetVisible(_kid1, copy, false);

        Sut().Run();
        var s = Stored(r.Id);
        Assert.Equal(ShareState.Active, s.State);
        Assert.Equal(1, s.FailedChecks);
        Assert.Contains(copy, _gw.ItemRefreshQueue);

        _now = _now.Add(ShareSynchronizer.MinCheckInterval); // the refresh had its time to land
        var report = Sut().Run();
        s = Stored(r.Id);
        Assert.Equal(ShareState.Broken, s.State);
        Assert.Equal("override not taking effect", s.StateReason);
        Assert.Equal(r.FolderName, s.FolderName);
        Assert.True(Directory.Exists(Folder(r)));
        Assert.DoesNotContain(lib, _gw.RemovedLibraries); // a Broken record that keeps its copy holds the library
        Assert.Equal(1, report.Broken);

        Assert.Equal(0, Sut().Run().Broken); // still Broken, not counted again

        _gw.SetVisible(_kid1, copy, true);
        Sut().Run();
        s = Stored(r.Id);
        Assert.Equal(ShareState.Active, s.State);
        Assert.Null(s.StateReason);
        Assert.Equal(0, s.FailedChecks);
    }

    // I3 made full runs frequent (every user edit, and the echo of the plugin's own policy writes): a second check seconds
    // after the first must not break a share whose refresh has not landed yet, nor rebuild a copy still being scanned.
    [Fact]
    public void Rule7_checks_seconds_apart_wait_for_the_refresh_and_the_scan_before_counting_again()
    {
        var r = Share(_film, _kid1);
        var copy = _gw.ItemsByPath.Values.Single();
        _gw.SetVisible(_kid1, copy, false);

        Sut().Run();
        _now = _now.AddSeconds(5);
        Sut().Run();
        Sut().Run();

        Assert.Equal(ShareState.Active, Stored(r.Id).State);
        Assert.Equal(1, Stored(r.Id).FailedChecks);
        _now = _now.AddSeconds(ShareSynchronizer.MinCheckInterval.TotalSeconds);
        Sut().Run();
        Assert.Equal(ShareState.Broken, Stored(r.Id).State);

        var r2 = Share(_film2, _kid2);
        _gw.ItemsByPath.Remove(_gw.ItemsByPath.Single(p => p.Key.Contains(r2.FolderName, StringComparison.Ordinal)).Key);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(0, Sut().Run().Reapplied);
        }

        Assert.Equal(1, Stored(r2.Id).MissingChecks);
    }

    [Fact]
    public void Rule7_copy_not_scanned_yet_queues_a_scan_and_is_rebuilt_after_three_runs()
    {
        var r = Share(_film, _kid1);
        _gw.ItemsByPath.Clear();
        var lib = Library(_kid1);

        for (var run = 1; run <= 2; run++)
        {
            _gw.ScanQueue.Clear();
            _now = _now.Add(ShareSynchronizer.MinCheckInterval); // each counted check waits for the last scan's time
            Assert.Equal(0, Sut().Run().Reapplied);
            var s = Stored(r.Id);
            Assert.Equal(ShareState.Active, s.State);
            Assert.Equal(0, s.FailedChecks);
            Assert.Equal(run, s.MissingChecks);
            Assert.Contains(lib, _gw.ScanQueue);
        }

        var before = File.GetLastWriteTimeUtc(Path.Combine(Folder(r), r.FolderName + ".nfo"));
        _now = _now.Add(ShareSynchronizer.MinCheckInterval);
        var report = Sut().Run();
        var after = Stored(r.Id);
        Assert.Equal(1, report.Reapplied);
        Assert.Equal(0, after.MissingChecks);
        Assert.Equal(ShareState.Active, after.State);
        Assert.True(Directory.Exists(Folder(after)));
        Assert.True(File.GetLastWriteTimeUtc(Path.Combine(Folder(after), after.FolderName + ".nfo")) >= before);
    }

    [Fact]
    public void Rule7_a_path_hit_outside_the_recipients_share_library_is_not_the_copy()
    {
        var r = Share(_film, _kid1);
        var path = _gw.ItemsByPath.Keys.Single();
        _gw.ItemsByPath[path] = _film; // e.g. the server resolved the original

        Sut().Run();

        Assert.Equal(1, Stored(r.Id).MissingChecks);
        Assert.Equal(0, Stored(r.Id).FailedChecks);
    }

    // ---------- Sharer, new libraries ----------

    [Fact]
    public void Sharer_deleted_leaves_the_share_active()
    {
        var r = Share(_film, _kid1);
        _gw.Users.Remove(_adult);

        var report = Sut().Run();

        var s = Stored(r.Id);
        Assert.Equal(ShareState.Active, s.State);
        Assert.True(Directory.Exists(Folder(s)));
        Assert.Equal(1, report.Checked);
        Assert.Equal(0, report.Closed + report.Broken + report.Removed);
    }

    [Fact]
    public void New_real_library_reaches_snapshot_users_but_a_library_an_admin_removed_is_not_regranted()
    {
        var explicitUser = Guid.NewGuid();
        _gw.Users[explicitUser] = new UserInfo(explicitUser, "Explicit", false, false, false, null, null, false, new List<Guid> { _lib }, Array.Empty<string>(), Array.Empty<string>());
        Share(_film, _kid1);
        Sut().Run();

        var added = _gw.AddRealLibrary(Path.Combine(_dir, "new"));
        Sut().Run();

        foreach (var u in new[] { _adult, _kid1, _kid2, _admin })
        {
            Assert.Contains(added, _gw.Users[u].EnabledLibraries);
        }

        Assert.DoesNotContain(added, _gw.Users[explicitUser].EnabledLibraries);

        // An admin takes the new library away from Kid2 on purpose: later runs leave that alone.
        _gw.Users[_kid2] = _gw.Users[_kid2] with { EnabledLibraries = _gw.Users[_kid2].EnabledLibraries.Where(l => l != added).ToList() };
        Sut().Run();
        Assert.DoesNotContain(added, _gw.Users[_kid2].EnabledLibraries);
    }

    [Fact]
    public void New_real_library_reaches_snapshot_users_when_no_share_library_exists()
    {
        var r = Share(_film, _kid1);
        Sut().Run();
        Service().Remove(r.Id, _adult);
        Assert.Empty(_store.Libraries());

        var added = _gw.AddRealLibrary(Path.Combine(_dir, "new"));
        Sut().Run();

        Assert.Contains(added, _gw.Users[_kid2].EnabledLibraries);
    }

    // ---------- Decision 2: share-root sanity check ----------

    [Fact]
    public void Overlapping_share_root_runs_isolation_only_and_reports_a_setup_problem()
    {
        var r = Share(_film, _kid1);
        Sut().Run(); // seeds the known libraries
        _gw.SetVisible(_kid1, _film); // would otherwise close the share
        var brandNew = _gw.AddRealLibrary(Path.Combine(_dir, "brand-new")); // would otherwise be granted
        _gw.AddRealLibrary(Path.Combine(_shareRoot, "stray"));
        var other = _gw.Calls.Count(c => c.Method is nameof(IServerGateway.RemoveLibrary) or nameof(IServerGateway.CreateLibrary)
            or nameof(IServerGateway.QueueScan) or nameof(IServerGateway.QueueItemRefresh));
        var records = _store.All();

        var report = Sut().Run();

        Assert.False(string.IsNullOrEmpty(report.SetupProblem));
        static object Shape(ShareRecord x) => (x.Id, x.OriginalItemId, x.State, x.StateReason, x.FolderName, x.FailedChecks, x.MissingChecks);
        Assert.Equal(records.Select(Shape), _store.All().Select(Shape));
        Assert.True(Directory.Exists(Folder(r)));
        Assert.Contains(brandNew, _gw.GetRealLibraryIds()); // control: it is a real library, so "not granted" means something
        Assert.All(_gw.Users.Values, u => Assert.DoesNotContain(brandNew, u.EnabledLibraries));
        Assert.Equal(other, _gw.Calls.Count(c => c.Method is nameof(IServerGateway.RemoveLibrary) or nameof(IServerGateway.CreateLibrary)
            or nameof(IServerGateway.QueueScan) or nameof(IServerGateway.QueueItemRefresh)));
    }

    [Fact]
    public void A_setup_problem_never_switches_isolation_off()
    {
        Share(_film, _kid1);
        _gw.AddRealLibrary(Path.Combine(_shareRoot, "stray"));
        var newcomer = Guid.NewGuid();
        AddUser(newcomer, "Newcomer", null);

        Sut().EnsureIsolation();
        Assert.False(_gw.Users[newcomer].AllLibraries);
        Assert.Contains(LibraryAccessManager.RecipientTag(_kid1), _gw.Users[newcomer].BlockedTags);

        var late = Guid.NewGuid();
        AddUser(late, "Late", null);
        var report = Sut().Run();
        Assert.False(string.IsNullOrEmpty(report.SetupProblem));
        Assert.False(_gw.Users[late].AllLibraries);
        Assert.Contains(LibraryAccessManager.RecipientTag(_kid1), _gw.Users[late].BlockedTags);
    }

    // ---------- Decision 5: Broken records that still hold a folder ----------

    [Fact]
    public void Broken_could_not_remove_is_retried_and_on_success_the_record_and_library_go()
    {
        var r = Share(_film, _kid1);
        var lib = Library(_kid1);
        _store.Upsert(r with { State = ShareState.Broken, StateReason = ShareService.CouldNotRemoveReason });
        _gw.SetVisible(_kid1, _film, false);

        _builder.FailRemove = true;
        Sut().Run();
        Assert.Equal(ShareService.CouldNotRemoveReason, Stored(r.Id).StateReason);
        Assert.True(Directory.Exists(Folder(r)));
        Assert.DoesNotContain(lib, _gw.RemovedLibraries);

        _builder.FailRemove = false;
        var report = Sut().Run();
        Assert.Empty(_store.All());
        Assert.False(Directory.Exists(Folder(r)));
        Assert.Contains(lib, _gw.RemovedLibraries);
        Assert.Equal(1, report.Removed);
    }

    [Fact]
    public void A_removal_that_fails_leaves_the_record_broken_holding_its_folder_and_library()
    {
        var r = Share(_film, _kid1);
        var lib = Library(_kid1);
        _gw.SetVisible(_kid1, _film);
        _builder.FailRemove = true;

        Sut().Run();

        var s = Stored(r.Id);
        Assert.Equal(ShareState.Broken, s.State);
        Assert.Equal(ShareService.CouldNotRemoveReason, s.StateReason);
        Assert.Equal(r.FolderName, s.FolderName);
        Assert.DoesNotContain(lib, _gw.RemovedLibraries);
    }

    // ---------- Decision 10: Closed retention ----------

    [Fact]
    public void Closed_records_are_kept_30_days_then_deleted()
    {
        Share(_film, _kid1);
        var old = Guid.NewGuid();
        var young = Guid.NewGuid();
        var unstamped = Guid.NewGuid();
        var template = _store.All().Single() with { State = ShareState.Closed, FolderName = string.Empty, StateReason = "no longer needed" };
        _store.Upsert(template with { Id = old, ClosedUtc = _now.AddDays(-31) });
        _store.Upsert(template with { Id = young, ClosedUtc = _now.AddDays(-29) });
        _store.Upsert(template with { Id = unstamped, ClosedUtc = null });

        var report = Sut().Run();

        Assert.DoesNotContain(_store.All(), x => x.Id == old);
        Assert.Contains(_store.All(), x => x.Id == young);
        Assert.Equal(_now, Stored(unstamped).ClosedUtc);
        Assert.Equal(1, report.Removed);
    }

    // ---------- Decision 11: failures ----------

    [Fact]
    public void One_bad_share_is_marked_broken_and_the_others_are_still_checked()
    {
        var bad = Share(_film, _kid1);
        var good = Share(_film2, _kid2);
        Directory.Delete(Folder(bad), recursive: true); // needs a rebuild...
        _gw.Media.Remove(_film); // ...which fails
        SetCap(_kid2, 8, 0);

        var report = Sut().Run();

        Assert.Equal(ShareState.Broken, Stored(bad.Id).State);
        Assert.False(string.IsNullOrEmpty(Stored(bad.Id).StateReason));
        Assert.Contains("<customrating>PG</customrating>", Nfo(Stored(good.Id)), StringComparison.Ordinal);
        Assert.Equal(2, report.Checked);
    }

    [Fact]
    public void A_failure_in_isolation_aborts_the_run()
    {
        var r = Share(_film, _kid1);
        _gw.SetVisible(_kid1, _film);
        _gw.Users[_kid2] = _gw.Users[_kid2] with { AllLibraries = true };
        _gw.ThrowOnce = nameof(IServerGateway.SetLibraryAccess);

        Assert.Throws<InvalidOperationException>(() => Sut().Run());
        Assert.Equal(ShareState.Active, Stored(r.Id).State); // no record rule ran
    }

    // ---------- Inactive plugin, isolation-only path ----------

    [Fact]
    public void A_plugin_that_never_shared_writes_nothing()
    {
        Sut().Run();
        Sut().EnsureIsolation();

        Assert.Equal(0, Count(nameof(IServerGateway.SetLibraryAccess)) + Count(nameof(IServerGateway.SetBlockedTags)));
        Assert.All(_gw.Users.Values, u => Assert.True(u.AllLibraries));
        Assert.False(File.Exists(Path.Combine(_dir, "store", "shares.json")));
    }

    [Fact]
    public void After_restore_only_closed_or_folderless_records_remain_and_nobody_is_isolated_again()
    {
        var r = Share(_film, _kid1);
        _gw.SetVisible(_kid1, _film);
        Sut().Run(); // closed: no longer needed
        Assert.Equal(ShareState.Closed, Stored(r.Id).State);
        var broken = r with { Id = Guid.NewGuid(), State = ShareState.Broken, StateReason = "original missing", FolderName = string.Empty };
        _store.Upsert(broken);
        var old = r with { Id = Guid.NewGuid(), State = ShareState.Closed, FolderName = string.Empty, ClosedUtc = _now.AddDays(-31) };
        _store.Upsert(old);
        new LibraryAccessManager(_gw, _store).RestoreAll(); // the audit page's Restore: libraries, tags, snapshots gone
        Assert.All(_gw.Users.Values, u => Assert.True(u.AllLibraries));
        var writes = Count(nameof(IServerGateway.SetLibraryAccess)) + Count(nameof(IServerGateway.SetBlockedTags));

        Sut().EnsureIsolation();
        var report = Sut().Run();

        Assert.Equal(writes, Count(nameof(IServerGateway.SetLibraryAccess)) + Count(nameof(IServerGateway.SetBlockedTags)));
        Assert.All(_gw.Users.Values, u => Assert.True(u.AllLibraries));
        Assert.Empty(_store.Snapshots());
        Assert.Contains(_store.All(), x => x.Id == r.Id); // the audit trail is kept for 30 days...
        Assert.DoesNotContain(_store.All(), x => x.Id == old.Id); // ...and then purged even when not in charge
        Assert.Equal(1, report.Removed);
    }

    [Fact]
    public void EnsureIsolation_restores_all_libraries_and_a_removed_block_on_a_user_an_admin_edited()
    {
        Share(_film, _kid1);
        var lib = Library(_kid1);
        var tag = LibraryAccessManager.RecipientTag(_kid1);
        _gw.Users[_kid2] = _gw.Users[_kid2] with { AllLibraries = true, EnabledLibraries = new List<Guid>(), BlockedTags = new List<string>() };

        Sut().EnsureIsolation();

        Assert.False(_gw.Users[_kid2].AllLibraries);
        Assert.Contains(_lib, _gw.Users[_kid2].EnabledLibraries);
        Assert.DoesNotContain(lib, _gw.Users[_kid2].EnabledLibraries);
        Assert.Contains(tag, _gw.Users[_kid2].BlockedTags);
    }

    [Fact]
    public void A_new_user_created_after_shares_exist_is_isolated_and_blocks_the_tag()
    {
        Share(_film, _kid1);
        var newbie = Guid.NewGuid();
        AddUser(newbie, "Newbie", null);

        Sut().EnsureIsolation();

        Assert.False(_gw.Users[newbie].AllLibraries);
        Assert.Equal(new[] { _lib }, _gw.Users[newbie].EnabledLibraries);
        Assert.Contains(LibraryAccessManager.RecipientTag(_kid1), _gw.Users[newbie].BlockedTags);
        Assert.Contains(_store.Snapshots(), s => s.UserId == newbie && s.HadAllLibraries);
    }

    // ---------- Fix round 1 ----------

    [Fact]
    public void Relink_refuses_a_candidate_whose_shared_provider_id_conflicts()
    {
        var r = Share(_film, _kid1);
        _store.Upsert(Stored(r.Id) with { ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt1", ["Tmdb"] = "100" } });
        _gw.Items.Remove(_film);
        AddFilmWithIds("Mis-identified", new Dictionary<string, string> { ["Imdb"] = "tt1", ["Tmdb"] = "999" });

        var report = Sut().Run();

        var s = Stored(r.Id);
        Assert.Equal(ShareState.Broken, s.State);
        Assert.Equal("original missing", s.StateReason);
        Assert.Equal(string.Empty, s.FolderName);
        Assert.False(Directory.Exists(Folder(r)));
        Assert.Equal(0, report.Relinked);
    }

    [Fact]
    public void Relink_accepts_a_candidate_with_more_ids_when_every_shared_one_matches()
    {
        var r = Share(_film, _kid1);
        _store.Upsert(Stored(r.Id) with { ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "100" } });
        _gw.Items.Remove(_film);
        var upgraded = AddFilmWithIds("Spike Eighteen", new Dictionary<string, string> { ["tmdb"] = "100", ["Imdb"] = "tt9" });

        Assert.Equal(1, Sut().Run().Relinked);
        Assert.Equal(upgraded, Stored(r.Id).OriginalItemId);
    }

    [Fact]
    public void A_record_without_provider_ids_never_relinks()
    {
        var r = Share(_film, _kid1);
        _store.Upsert(Stored(r.Id) with { ProviderIds = new Dictionary<string, string>() });
        _gw.Items.Remove(_film);
        AddFilmWithIds("Spike Eighteen", new Dictionary<string, string> { ["Tmdb"] = "1" });

        Sut().Run();

        Assert.Equal(ShareState.Broken, Stored(r.Id).State);
        Assert.Equal("original missing", Stored(r.Id).StateReason);
    }

    [Fact]
    public void Rule6_changed_copy_tags_rewrite_the_nfo()
    {
        var r = Share(_film, _kid1);
        _gw.Users[_kid1] = _gw.Users[_kid1] with { AllowedTags = new List<string> { "kids" } };

        var report = Sut().Run();

        Assert.Equal(1, report.Reapplied);
        Assert.Contains("<tag>kids</tag>", Nfo(Stored(r.Id)), StringComparison.Ordinal);
        Assert.Contains("<tag>" + LibraryAccessManager.RecipientTag(_kid1) + "</tag>", Nfo(Stored(r.Id)), StringComparison.Ordinal);
    }

    [Fact]
    public void Rule7_finds_the_copy_by_the_link_name_the_builder_used()
    {
        var r = Share(_film, _kid1);
        var file = _gw.Media[_film].PrimaryFile;
        _gw.Items[_film] = _gw.Items[_film] with { Path = Path.ChangeExtension(file, ".MP4") };

        Sut().Run();

        var s = Stored(r.Id);
        Assert.Equal(0, s.MissingChecks);
        Assert.Equal(ShareState.Active, s.State);
    }

    [Fact]
    public void Not_in_charge_closes_every_share_that_still_has_a_folder()
    {
        var r = Share(_film, _kid1);
        new LibraryAccessManager(_gw, _store).RestoreAll();
        Assert.True(Directory.Exists(Folder(r)));

        var report = Sut().Run();

        var s = Stored(r.Id);
        Assert.Equal(ShareState.Closed, s.State);
        Assert.Equal("sharing was switched off", s.StateReason);
        Assert.Equal(string.Empty, s.FolderName);
        Assert.Equal(_now, s.ClosedUtc);
        Assert.False(Directory.Exists(Folder(r)));
        Assert.Equal(1, report.Closed);
        Assert.All(_gw.Users.Values, u => Assert.True(u.AllLibraries));
    }

    [Fact]
    public void Not_in_charge_a_copy_that_cannot_be_removed_stays_broken_and_is_retried()
    {
        var r = Share(_film, _kid1);
        new LibraryAccessManager(_gw, _store).RestoreAll();
        _builder.FailRemove = true;

        Sut().Run();
        Assert.Equal(ShareService.CouldNotRemoveReason, Stored(r.Id).StateReason);
        Assert.True(Directory.Exists(Folder(r)));

        _builder.FailRemove = false;
        Sut().Run();
        Assert.False(Directory.Exists(Folder(r)));
        Assert.DoesNotContain(_store.All(), x => x.Id == r.Id && !string.IsNullOrEmpty(x.FolderName));
        Assert.All(_gw.Users.Values, u => Assert.True(u.AllLibraries));
    }

    [Fact]
    public void Restore_then_sync_then_a_new_share_does_not_bring_the_old_title_back()
    {
        var old = Share(_film, _kid1);
        new LibraryAccessManager(_gw, _store).RestoreAll();
        Sut().Run();

        var fresh = Share(_film2, _kid1);

        Assert.Equal(ShareState.Closed, Stored(old.Id).State);
        Assert.Equal(string.Empty, Stored(old.Id).FolderName);
        Assert.Equal(new[] { fresh.FolderName }, Directory.GetDirectories(MoviesRoot(_kid1)).Select(Path.GetFileName));
    }

    [Fact]
    public void A_share_library_on_the_server_keeps_the_plugin_in_charge_without_a_store()
    {
        Share(_film, _kid1);
        File.Delete(Path.Combine(_dir, "store", "shares.json")); // store lost, library still on the server
        var newcomer = Guid.NewGuid();
        AddUser(newcomer, "Newcomer", null);

        Sut().EnsureIsolation();

        Assert.False(_gw.Users[newcomer].AllLibraries);
    }

    [Fact]
    public void A_first_share_that_crashed_before_its_library_gets_one_and_others_block_its_tag()
    {
        Share(_film2, _kid2); // keeps the plugin in charge
        var r = Share(_film, _kid1);
        var lib = Library(_kid1);
        var tag = LibraryAccessManager.RecipientTag(_kid1);

        // The state a crash between saving the record and EnsureShareLibrary leaves on a first share.
        _gw.RemoveLibrary(lib);
        _store.ClearLibraryKey(_kid1, nameof(ShareKind.Movie));
        _gw.Users[_kid1] = _gw.Users[_kid1] with { EnabledLibraries = _gw.Users[_kid1].EnabledLibraries.Where(l => l != lib).ToList() };
        foreach (var u in _gw.Users.Keys.ToList())
        {
            _gw.Users[u] = _gw.Users[u] with { BlockedTags = _gw.Users[u].BlockedTags.Where(t => t != tag).ToList() };
        }

        Sut().Run();

        var now = Library(_kid1);
        Assert.Contains(now, _gw.Users[_kid1].EnabledLibraries);
        Assert.All(_gw.Users.Values.Where(u => u.Id != _kid1), u =>
        {
            Assert.DoesNotContain(now, u.EnabledLibraries);
            Assert.Contains(tag, u.BlockedTags);
        });
        Assert.Equal(ShareState.Active, Stored(r.Id).State);
    }

    // ---- Series (Task 9) ----

    private readonly Guid _show = Guid.NewGuid();

    private string ShowsRoot(Guid recipient) => Path.Combine(_shareRoot, recipient.ToString("N"), "shows");

    private string EpisodeFile(int episode) => Path.Combine(_media, "Spike Show (2010)", "Season 01", $"Spike Show S01E0{episode}.mp4");

    // The original series with these episode files on disk; the media lists only files that exist, as the gateway does.
    private void AddShow(params int[] episodes)
    {
        foreach (var e in episodes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(EpisodeFile(e))!);
            File.WriteAllText(EpisodeFile(e), "video " + e);
        }

        _gw.Items[_show] = new ItemInfo(_show, ShareKind.Series, "Spike Show", 2010, "GB-15", new Dictionary<string, string> { ["Tvdb"] = "990010" }, _lib, new List<string>(), Path.Combine(_media, "Spike Show (2010)"));
        SetEpisodes(episodes);
        _gw.SetVisible(_adult, _show);
    }

    private void SetEpisodes(params int[] episodes) =>
        _gw.Media[_show] = new SourceMedia(string.Empty, Array.Empty<string>(), episodes.Select(e => (1, e, EpisodeFile(e))).ToList());

    private ShareRecord ShareShow(bool seriesVisible = true, bool episodesVisible = true)
    {
        var (decision, record) = Service().Create(_adult, _show, _kid1);
        Assert.Equal(Refusal.None, decision.Refusal);
        RegisterShowCopy(record!, seriesVisible, episodesVisible);
        return record!;
    }

    // Registers the series copy and one item per episode link, as a scan of the share library would.
    private (Guid Series, List<Guid> Episodes) RegisterShowCopy(ShareRecord r, bool seriesVisible = true, bool episodesVisible = true)
    {
        var library = _store.Libraries()[r.RecipientUserId][nameof(ShareKind.Series)];
        var folder = Path.Combine(ShowsRoot(r.RecipientUserId), r.FolderName);
        var series = Guid.NewGuid();
        _gw.ItemsByPath[folder] = series;
        _gw.Items[series] = _gw.Items[_show] with { Id = series, LibraryId = library, Path = folder };
        _gw.SetVisible(r.RecipientUserId, series, seriesVisible);
        var episodes = new List<Guid>();
        foreach (var link in Directory.EnumerateFiles(folder, "*.mp4", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var id = Guid.NewGuid();
            _gw.ItemsByPath[link] = id;
            _gw.Items[id] = new ItemInfo(id, null, Path.GetFileNameWithoutExtension(link), 2010, null, new Dictionary<string, string>(), library, new List<string>(), link);
            _gw.SetVisible(r.RecipientUserId, id, episodesVisible);
            episodes.Add(id);
        }

        return (series, episodes);
    }

    private List<string> EpisodeLinks(ShareRecord r) =>
        Directory.EnumerateFiles(Path.Combine(ShowsRoot(r.RecipientUserId), r.FolderName), "*.mp4", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Path.Combine(ShowsRoot(r.RecipientUserId), r.FolderName), f) + " -> " + new FileInfo(f).LinkTarget)
            .Order(StringComparer.Ordinal)
            .ToList();

    private string Link(int episode) => Path.Combine("Season 01", $"Spike Show S01E0{episode}.mp4") + " -> " + EpisodeFile(episode);

    [Fact]
    public void A_series_whose_episodes_are_all_linked_and_visible_stays_as_it_is()
    {
        AddShow(1, 2);
        var r = ShareShow();
        _gw.ScanQueue.Clear();

        var report = Sut().Run();

        Assert.Equal(0, report.Reapplied);
        Assert.Equal(ShareState.Active, Stored(r.Id).State);
        Assert.Equal(0, Stored(r.Id).FailedChecks);
        Assert.Equal(new[] { Link(1), Link(2) }, EpisodeLinks(r));
        Assert.Empty(_gw.ScanQueue);
    }

    [Fact]
    public void A_new_episode_of_the_original_is_linked_in_place_and_scanned()
    {
        AddShow(1, 2);
        var r = ShareShow();
        AddShow(1, 2, 3);
        _gw.ScanQueue.Clear();

        var report = Sut().Run();

        Assert.Equal(1, report.Reapplied);
        Assert.Equal(new[] { Link(1), Link(2), Link(3) }, EpisodeLinks(r));
        Assert.Equal(r.FolderName, Stored(r.Id).FolderName); // same share, same folder
        Assert.Equal(ShareState.Active, Stored(r.Id).State);
        Assert.Contains(_store.Libraries()[_kid1][nameof(ShareKind.Series)], _gw.ScanQueue);
        Assert.True(File.Exists(Path.Combine(ShowsRoot(_kid1), r.FolderName, "tvshow.nfo")));
    }

    [Fact]
    public void An_episode_gone_from_the_original_is_unlinked()
    {
        AddShow(1, 2);
        var r = ShareShow();
        File.Delete(EpisodeFile(1));
        SetEpisodes(2);

        var report = Sut().Run();

        Assert.Equal(1, report.Reapplied);
        Assert.Equal(new[] { Link(2) }, EpisodeLinks(r));
        Assert.Equal(ShareState.Active, Stored(r.Id).State);
    }

    [Fact]
    public void A_first_sighting_of_an_original_series_with_no_episode_file_changes_nothing()
    {
        AddShow(1, 2);
        var r = ShareShow();
        var before = EpisodeLinks(r);
        SetEpisodes();

        var report = Sut().Run();

        Assert.Equal(new SyncReport(1, 0, 0, 0, 0), report);
        var stored = Stored(r.Id);
        Assert.Equal(ShareState.Active, stored.State);
        Assert.Equal(1, stored.EmptyChecks);
        Assert.Equal(before, EpisodeLinks(stored)); // the working copy is untouched (perhaps a scan in progress)
    }

    [Fact]
    public void The_empty_count_resets_when_the_episodes_are_back()
    {
        AddShow(1, 2);
        var r = ShareShow();
        SetEpisodes();
        Sut().Run();
        SetEpisodes(1, 2);

        Sut().Run();
        Assert.Equal(0, Stored(r.Id).EmptyChecks);

        SetEpisodes();
        Sut().Run();
        Assert.Equal(ShareState.Active, Stored(r.Id).State); // a new first sighting, not the second
        Assert.Equal(1, Stored(r.Id).EmptyChecks);
    }

    [Fact]
    public void An_original_series_with_no_episode_file_on_two_runs_is_original_gone_and_never_relinks_to_itself()
    {
        AddShow(1, 2);
        var r = ShareShow();
        SetEpisodes();
        Sut().Run();

        var report = Sut().Run();

        Assert.Equal(0, report.Relinked);
        Assert.Equal(1, report.Broken);
        var stored = Stored(r.Id);
        Assert.Equal(ShareState.Broken, stored.State);
        Assert.Equal(ShareSynchronizer.OriginalMissingReason, stored.StateReason);
        Assert.Equal(_show, stored.OriginalItemId);
        Assert.False(Directory.Exists(Path.Combine(ShowsRoot(_kid1), r.FolderName)));
    }

    [Fact]
    public void A_series_whose_episodes_come_back_is_rebuilt_after_being_broken()
    {
        AddShow(1, 2);
        var r = ShareShow();
        SetEpisodes();
        Sut().Run();
        Sut().Run();
        Assert.Equal(ShareState.Broken, Stored(r.Id).State);
        SetEpisodes(1, 2);

        Sut().Run();

        var stored = Stored(r.Id);
        Assert.Equal(ShareState.Active, stored.State);
        Assert.Equal(0, stored.EmptyChecks);
        Assert.Equal(new[] { Link(1), Link(2) }, EpisodeLinks(stored));
    }

    [Fact]
    public void Rule_7_a_series_visible_but_its_first_episode_not_is_a_failed_check_then_broken()
    {
        AddShow(1, 2);
        var r = ShareShow(seriesVisible: true, episodesVisible: false);
        var series = _gw.ItemsByPath[Path.Combine(ShowsRoot(_kid1), r.FolderName)];

        Sut().Run();

        Assert.Equal(1, Stored(r.Id).FailedChecks);
        Assert.Equal(ShareState.Active, Stored(r.Id).State);
        Assert.Contains(series, _gw.ItemRefreshQueue);

        _now = _now.Add(ShareSynchronizer.MinCheckInterval);
        var report = Sut().Run();

        Assert.Equal(1, report.Broken);
        Assert.Equal(ShareState.Broken, Stored(r.Id).State);
        Assert.Equal(ShareSynchronizer.OverrideNotTakingEffectReason, Stored(r.Id).StateReason);
        Assert.True(Directory.Exists(Path.Combine(ShowsRoot(_kid1), r.FolderName))); // kept: blocked for everyone else
    }

    [Fact]
    public void Rule_7_a_series_not_visible_is_a_failed_check_even_with_its_episodes_visible()
    {
        AddShow(1, 2);
        var r = ShareShow(seriesVisible: false, episodesVisible: true);

        Sut().Run();

        Assert.Equal(1, Stored(r.Id).FailedChecks);
    }

    [Fact]
    public void Rule_7_checks_the_first_episode()
    {
        AddShow(1, 2);
        var r = ShareShow();
        var episodes = _gw.ItemsByPath.Where(p => p.Key.EndsWith(".mp4", StringComparison.Ordinal))
            .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToList();
        _gw.SetVisible(_kid1, episodes[1], false); // the second episode does not count for rule 7

        Sut().Run();
        Assert.Equal(0, Stored(r.Id).FailedChecks);

        _gw.SetVisible(_kid1, episodes[1], true);
        _gw.SetVisible(_kid1, episodes[0], false);
        Sut().Run();
        Assert.Equal(1, Stored(r.Id).FailedChecks);
    }

    [Fact]
    public void Rule_7_an_episode_not_scanned_yet_is_missing_not_failed()
    {
        AddShow(1, 2);
        var r = ShareShow();
        foreach (var p in _gw.ItemsByPath.Keys.Where(k => k.EndsWith(".mp4", StringComparison.Ordinal)).ToList())
        {
            _gw.ItemsByPath.Remove(p);
        }

        Sut().Run();

        Assert.Equal(0, Stored(r.Id).FailedChecks);
        Assert.Equal(1, Stored(r.Id).MissingChecks);
        Assert.Equal(ShareState.Active, Stored(r.Id).State);
    }

    [Fact]
    public void Rule_7_tries_the_next_episode_when_the_first_is_not_scanned()
    {
        AddShow(1, 2);
        var r = ShareShow();
        var first = _gw.ItemsByPath.Keys.Where(k => k.EndsWith(".mp4", StringComparison.Ordinal)).Order(StringComparer.Ordinal).First();
        _gw.ItemsByPath.Remove(first);

        Sut().Run();

        Assert.Equal((0, 0, ShareState.Active), (Stored(r.Id).FailedChecks, Stored(r.Id).MissingChecks, Stored(r.Id).State));
    }

    [Fact]
    public void Rule_7_prefers_a_regular_season_over_specials()
    {
        AddShow(1);
        var special = Path.Combine(_media, "Spike Show (2010)", "Specials", "Spike Show S00E01.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(special)!);
        File.WriteAllText(special, "video");
        _gw.Media[_show] = new SourceMedia(string.Empty, Array.Empty<string>(), new List<(int, int, string)> { (0, 1, special), (1, 1, EpisodeFile(1)) });
        var r = ShareShow();
        var specialCopy = _gw.ItemsByPath[Path.Combine(ShowsRoot(_kid1), r.FolderName, "Season 00", "Spike Show S00E01.mp4")];
        _gw.SetVisible(_kid1, specialCopy, false); // a special the recipient cannot see does not decide rule 7

        Sut().Run();
        Assert.Equal(0, Stored(r.Id).FailedChecks);

        var regular = _gw.ItemsByPath[Path.Combine(ShowsRoot(_kid1), r.FolderName, "Season 01", "Spike Show S01E01.mp4")];
        _gw.SetVisible(_kid1, specialCopy, true);
        _gw.SetVisible(_kid1, regular, false);
        Sut().Run();
        Assert.Equal(1, Stored(r.Id).FailedChecks);
    }

    [Fact]
    public void Rule_6_a_lowered_cap_rewrites_every_level_of_a_series_copy_and_refreshes_it()
    {
        AddShow(1, 2);
        var r = ShareShow();
        var series = _gw.ItemsByPath[Path.Combine(ShowsRoot(_kid1), r.FolderName)];
        _gw.Users[_kid1] = _gw.Users[_kid1] with { CapScore = 8, CapSubScore = 0 };
        _gw.ItemRefreshQueue.Clear();

        var report = Sut().Run();

        Assert.Equal(1, report.Reapplied);
        var folder = Path.Combine(ShowsRoot(_kid1), r.FolderName);
        foreach (var nfo in new[] { "tvshow.nfo", Path.Combine("Season 01", "season.nfo"), Path.Combine("Season 01", "Spike Show S01E01.nfo"), Path.Combine("Season 01", "Spike Show S01E02.nfo") })
        {
            Assert.Contains("<customrating>PG</customrating>", File.ReadAllText(Path.Combine(folder, nfo)), StringComparison.Ordinal);
        }

        Assert.Contains(series, _gw.ItemRefreshQueue);
        Assert.Equal(new[] { Link(1), Link(2) }, EpisodeLinks(Stored(r.Id)));
    }

    [Fact]
    public void A_series_never_relinks_to_a_candidate_without_episodes_or_in_another_library()
    {
        AddShow(1, 2);
        var r = ShareShow();
        var empty = Guid.NewGuid();
        _gw.Items[empty] = _gw.Items[_show] with { Id = empty, Path = Path.Combine(_media, "Empty") };
        _gw.Media[empty] = new SourceMedia(string.Empty, Array.Empty<string>(), Array.Empty<(int, int, string)>());
        var otherLib = _gw.AddRealLibrary(Path.Combine(_dir, "other"));
        var elsewhere = Guid.NewGuid();
        _gw.Items[elsewhere] = _gw.Items[_show] with { Id = elsewhere, LibraryId = otherLib, Path = Path.Combine(_dir, "other", "Spike Show (2010)") };
        _gw.Media[elsewhere] = new SourceMedia(string.Empty, Array.Empty<string>(), new List<(int, int, string)> { (1, 1, EpisodeFile(1)) });
        _gw.Items.Remove(_show);

        var report = Sut().Run();

        Assert.Equal(0, report.Relinked);
        Assert.Equal(ShareState.Broken, Stored(r.Id).State);
        Assert.Equal(ShareSynchronizer.OriginalMissingReason, Stored(r.Id).StateReason);
        Assert.Equal(_show, Stored(r.Id).OriginalItemId);
    }

    [Fact]
    public void A_series_relinks_only_to_one_series_with_the_same_ids_and_episodes_in_the_same_library()
    {
        AddShow(1, 2);
        var r = ShareShow();
        var replacement = Guid.NewGuid();
        var file = Path.Combine(_media, "Spike Show Remastered (2010)", "Season 01", "Spike Show S01E01.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "video");
        _gw.Items[replacement] = _gw.Items[_show] with { Id = replacement, Path = Path.GetDirectoryName(Path.GetDirectoryName(file)) };
        _gw.Media[replacement] = new SourceMedia(string.Empty, Array.Empty<string>(), new List<(int, int, string)> { (1, 1, file) });
        _gw.SetVisible(_adult, replacement); // the sharer can see the replacement (and so its unrated episodes)
        _gw.Items.Remove(_show);

        var report = Sut().Run();

        Assert.Equal(1, report.Relinked);
        var stored = Stored(r.Id);
        Assert.Equal(replacement, stored.OriginalItemId);
        Assert.Equal(ShareState.Active, stored.State);
        Assert.Equal(new[] { Path.Combine("Season 01", "Spike Show S01E01.mkv") + " -> " + file },
            Directory.EnumerateFiles(Path.Combine(ShowsRoot(_kid1), stored.FolderName), "*.mkv", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(Path.Combine(ShowsRoot(_kid1), stored.FolderName), f) + " -> " + new FileInfo(f).LinkTarget));
    }

    // ---------- I7 / I8 ----------

    [Fact]
    public void I7_the_sync_keeps_the_highest_rating_on_an_unrated_copy_for_an_uncapped_recipient_who_blocks_unrated_items()
    {
        _gw.Items[_film] = _gw.Items[_film] with { OfficialRating = null };
        _gw.Users[_kid1] = _gw.Users[_kid1] with { CapScore = null, CapSubScore = null, BlocksUnrated = true, BlockedUnratedKinds = new[] { "Movie", "Series" } };
        var r = Share(_film, _kid1);
        Assert.Contains("<customrating>18</customrating>", Nfo(r), StringComparison.Ordinal);

        Assert.Equal(0, Sut().Run().Reapplied);
        Assert.Equal(0, Sut().Run().Reapplied);
        Assert.Contains("<customrating>18</customrating>", Nfo(Stored(r.Id)), StringComparison.Ordinal);
    }

    [Fact]
    public void I8_a_replacement_the_sharer_cannot_see_is_not_a_candidate()
    {
        var r = Share(_film, _kid1);
        var replacement = AddFilmWithIds("Spike Eighteen Extended", new Dictionary<string, string> { ["Tmdb"] = "1" });
        _gw.Items[replacement] = _gw.Items[replacement] with { OfficialRating = null };
        _gw.SetVisible(_adult, replacement, false); // e.g. an unrated cut over the sharer's own limit
        _gw.Items.Remove(_film);

        var report = Sut().Run();

        Assert.Equal(0, report.Relinked);
        Assert.Equal(ShareState.Broken, Stored(r.Id).State);
        Assert.Equal(ShareSynchronizer.OriginalMissingReason, Stored(r.Id).StateReason);
        Assert.Equal(_film, Stored(r.Id).OriginalItemId);

        _gw.SetVisible(_adult, replacement, true); // control: the same candidate, visible to the sharer, is followed
        Assert.Equal(1, Sut().Run().Relinked);
        Assert.Equal(replacement, Stored(r.Id).OriginalItemId);
    }

    [Fact]
    public void I8_with_the_sharer_gone_relink_is_unchanged()
    {
        var r = Share(_film, _kid1);
        var replacement = AddFilmWithIds("Spike Eighteen Extended", new Dictionary<string, string> { ["Tmdb"] = "1" });
        _gw.SetVisible(_adult, replacement, false);
        _gw.Items.Remove(_film);
        _gw.Users.Remove(_adult);

        Assert.Equal(1, Sut().Run().Relinked);
        Assert.Equal(replacement, Stored(r.Id).OriginalItemId);
    }

    // ---------- I4 / I5: deletes that never follow links; copies no record names never come back ----------

    [Fact]
    public void I4_cleaning_up_a_deleted_recipient_never_follows_a_link()
    {
        var r = Share(_film, _kid1);
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        var dir = Path.Combine(_shareRoot, _kid1.ToString("N"));
        Directory.Delete(dir, recursive: true);
        Directory.CreateSymbolicLink(dir, outside); // the recipient's folder is a link to somewhere else
        _store.Upsert(r with { FolderName = string.Empty, State = ShareState.Closed, ClosedUtc = _now });
        _gw.Users.Remove(_kid1);

        Sut().Run();

        Assert.False(Path.Exists(dir));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public void I5_a_copy_left_by_a_crash_between_build_and_store_is_removed_before_the_library_exists()
    {
        var lost = new ShareRecord(Guid.NewGuid(), _film2, ShareKind.Movie, "Spike Fifteen", 2001, new Dictionary<string, string>(), _lib, _adult, "adult", _kid1, _now, ShareState.Active, null, string.Empty);
        var leftover = _builder.Build(MoviesRoot(_kid1), lost, _gw.Media[_film2], "GB-15", "12", new[] { LibraryAccessManager.RecipientTag(_kid1) });
        var leftoverPath = Path.Combine(MoviesRoot(_kid1), leftover);
        bool? existedWhenCreated = null;
        _gw.ThrowWhen = (method, _) =>
        {
            if (method == nameof(IServerGateway.CreateLibrary))
            {
                existedWhenCreated = Directory.Exists(leftoverPath);
            }

            return false;
        };

        var r = Share(_film, _kid1);

        Assert.False(existedWhenCreated);
        Assert.False(Directory.Exists(leftoverPath));
        Assert.True(Directory.Exists(Folder(r))); // the share just built is kept
    }

    [Fact]
    public void I5_a_full_sync_removes_marker_folders_no_record_names_and_keeps_known_and_unmarked_ones()
    {
        var r = Share(_film, _kid1);
        var lost = new ShareRecord(Guid.NewGuid(), _film2, ShareKind.Movie, "Spike Fifteen", 2001, new Dictionary<string, string>(), _lib, _adult, "adult", _kid1, _now, ShareState.Active, null, string.Empty);
        var leftover = _builder.Build(MoviesRoot(_kid1), lost, _gw.Media[_film2], "GB-15", "12", Array.Empty<string>());
        var own = Path.Combine(MoviesRoot(_kid1), "Not The Plugin's (2000)");
        Directory.CreateDirectory(own);
        var otherRecipient = Path.Combine(_shareRoot, _kid2.ToString("N"), "shows");
        var stray = _builder.Build(otherRecipient, lost with { Kind = ShareKind.Series }, new SourceMedia(string.Empty, Array.Empty<string>(), Array.Empty<(int, int, string)>()), "GB-15", "12", Array.Empty<string>());

        Sut().Run();

        Assert.True(Directory.Exists(Folder(r)));
        Assert.False(Directory.Exists(Path.Combine(MoviesRoot(_kid1), leftover)));
        Assert.False(Directory.Exists(Path.Combine(otherRecipient, stray)));
        Assert.True(Directory.Exists(own));
        Assert.Equal(ShareState.Active, Stored(r.Id).State);
    }

    // ---------- I2: an unreadable store never leaves anyone on "all libraries" beside a share library ----------

    private string StoreFile => Path.Combine(_dir, "store", "shares.json");

    // A share library of kid1's on the server, a store that cannot be read, and a brand-new profile on all libraries.
    private (Guid ShareLibrary, Guid Newcomer, byte[] StoreBytes) UnreadableStoreWithAShareLibrary()
    {
        Share(_film, _kid1);
        var lib = Library(_kid1);
        File.WriteAllText(StoreFile, "{ this is not json");
        var newcomer = Guid.NewGuid();
        AddUser(newcomer, "newcomer", null);
        _gw.Calls.Clear();
        return (lib, newcomer, File.ReadAllBytes(StoreFile));
    }

    [Fact]
    public void I2_with_an_unreadable_store_a_new_user_is_isolated_and_blocks_the_tag_and_nothing_else_happens()
    {
        var (lib, newcomer, bytes) = UnreadableStoreWithAShareLibrary();

        Sut().EnsureIsolation();

        var u = _gw.Users[newcomer];
        Assert.False(u.AllLibraries);
        Assert.Equal(new[] { _lib }, u.EnabledLibraries);
        Assert.DoesNotContain(lib, u.EnabledLibraries);
        Assert.Contains(LibraryAccessManager.RecipientTag(_kid1), u.BlockedTags);
        Assert.Equal(bytes, File.ReadAllBytes(StoreFile)); // no snapshot written: the file is not touched
        Assert.DoesNotContain(_gw.Calls, c => c.Method is "CreateLibrary" or "RemoveLibrary" or "QueueScan" or "QueueItemRefresh"
            or "CreatePlaylist" or "RemovePlaylist" or "AddToPlaylist" or "RemoveFromPlaylist" or "GetSourceMedia");
        Assert.DoesNotContain(LibraryAccessManager.RecipientTag(_kid1), _gw.Users[_kid1].BlockedTags);
    }

    [Fact]
    public void I2_a_full_run_with_an_unreadable_store_isolates_without_the_store_and_reports_it_as_a_setup_problem()
    {
        var (lib, newcomer, bytes) = UnreadableStoreWithAShareLibrary();
        _gw.Users[_kid2] = _gw.Users[_kid2] with { AllLibraries = false, EnabledLibraries = new List<Guid> { _lib, lib } };

        var report = Sut().Run();

        Assert.Equal(ShareSynchronizer.StoreUnreadableMessage, report.SetupProblem);
        Assert.False(_gw.Users[newcomer].AllLibraries);
        Assert.Equal(new[] { _lib }, _gw.Users[_kid2].EnabledLibraries); // a foreign share library comes off a list
        Assert.Contains(LibraryAccessManager.RecipientTag(_kid1), _gw.Users[newcomer].BlockedTags);
        Assert.Equal(bytes, File.ReadAllBytes(StoreFile));
        Assert.DoesNotContain(_gw.Calls, c => c.Method is "CreateLibrary" or "RemoveLibrary" or "QueueScan" or "GetSourceMedia");
        Assert.Equal(ShareSynchronizer.StoreUnreadableMessage, Sut().CurrentSetupProblem());
        Assert.True(Sut().IsInCharge()); // the share folder setting stays locked
    }

    [Fact]
    public void I2_a_readable_store_takes_the_normal_path_and_writes_a_snapshot()
    {
        Share(_film, _kid1);
        var newcomer = Guid.NewGuid();
        AddUser(newcomer, "newcomer", null);

        Sut().EnsureIsolation();

        Assert.False(_gw.Users[newcomer].AllLibraries);
        Assert.Contains(_store.Snapshots(), s => s.UserId == newcomer);
        Assert.Null(Sut().CurrentSetupProblem());
    }

    // ---------- C1: a series share holds only episodes the sharer can see and the recipient does not block ----------

    private string EpisodeNfo(ShareRecord r, int episode) =>
        File.ReadAllText(Path.Combine(ShowsRoot(r.RecipientUserId), r.FolderName, "Season 01", $"Spike Show S01E0{episode}.nfo"));

    [Fact]
    public void C1_an_episode_the_sharer_cannot_see_is_not_linked_and_the_others_are()
    {
        AddShow(1, 2, 3);
        _gw.SetVisible(_adult, _gw.EpisodeId(EpisodeFile(2)), false); // e.g. rated 18 by the admin, over the sharer's cap

        var r = ShareShow();

        Assert.Equal(new[] { Link(1), Link(3) }, EpisodeLinks(r));
        Assert.False(File.Exists(Path.Combine(ShowsRoot(_kid1), r.FolderName, "Season 01", "Spike Show S01E02.nfo")));
    }

    [Fact]
    public void R1_a_blocked_tag_every_episode_carries_closes_the_series_share_on_the_first_run()
    {
        AddShow(1, 2);
        var r = ShareShow();
        foreach (var n in new[] { 1, 2 })
        {
            _gw.EpisodeDetails[EpisodeFile(n)] = new EpisodeDetail(_gw.EpisodeId(EpisodeFile(n)), string.Empty, new[] { "Gore" });
        }

        _gw.Users[_kid1] = _gw.Users[_kid1] with { BlockedTags = new[] { "gore" } };

        var report = Sut().Run();

        var stored = Stored(r.Id);
        Assert.Equal(ShareState.Closed, stored.State);
        Assert.Equal(ShareSynchronizer.BlockedTagReason, stored.StateReason);
        Assert.Equal(1, report.Closed);
        Assert.False(Directory.Exists(Path.Combine(ShowsRoot(_kid1), r.FolderName)));
    }

    [Fact]
    public void R1_a_blocked_tag_on_the_series_itself_closes_the_series_share_on_the_first_run()
    {
        AddShow(1, 2);
        var r = ShareShow();
        _gw.Items[_show] = _gw.Items[_show] with { Tags = new List<string> { "Gore" } };
        foreach (var n in new[] { 1, 2 })
        {
            _gw.EpisodeDetails[EpisodeFile(n)] = new EpisodeDetail(_gw.EpisodeId(EpisodeFile(n)), string.Empty, new[] { "Gore" });
        }

        _gw.Users[_kid1] = _gw.Users[_kid1] with { BlockedTags = new[] { "gore" } };

        Sut().Run();

        Assert.Equal(ShareState.Closed, Stored(r.Id).State);
        Assert.Equal(ShareSynchronizer.BlockedTagReason, Stored(r.Id).StateReason);
    }

    [Fact]
    public void R1_a_blocked_tag_on_only_some_episodes_keeps_the_share_and_drops_just_those_episodes()
    {
        AddShow(1, 2);
        var r = ShareShow();
        _gw.EpisodeDetails[EpisodeFile(2)] = new EpisodeDetail(_gw.EpisodeId(EpisodeFile(2)), string.Empty, new[] { "Gore" });
        _gw.Users[_kid1] = _gw.Users[_kid1] with { BlockedTags = new[] { "gore" } };

        Sut().Run();

        Assert.Equal(ShareState.Active, Stored(r.Id).State);
        Assert.Equal(new[] { Link(1) }, EpisodeLinks(Stored(r.Id)));
    }

    [Fact]
    public void C1_an_episode_whose_own_tag_the_recipient_blocks_is_not_linked()
    {
        AddShow(1, 2);
        _gw.EpisodeDetails[EpisodeFile(2)] = new EpisodeDetail(_gw.EpisodeId(EpisodeFile(2)), string.Empty, new[] { "Gore" });
        _gw.Users[_kid1] = _gw.Users[_kid1] with { BlockedTags = new[] { "gore" } };

        var r = ShareShow();

        Assert.Equal(new[] { Link(1) }, EpisodeLinks(r));
    }

    [Fact]
    public void C1_each_episode_nfo_carries_the_episodes_own_rating_else_the_series_rating()
    {
        AddShow(1, 2);
        _gw.EpisodeDetails[EpisodeFile(2)] = new EpisodeDetail(_gw.EpisodeId(EpisodeFile(2)), "GB-18", Array.Empty<string>());

        var r = ShareShow();

        Assert.Contains("<mpaa>GB-15</mpaa>", EpisodeNfo(r, 1), StringComparison.Ordinal);
        Assert.Contains("<mpaa>GB-18</mpaa>", EpisodeNfo(r, 2), StringComparison.Ordinal);
        Assert.Contains("<customrating>12</customrating>", EpisodeNfo(r, 2), StringComparison.Ordinal);
        Assert.Contains(LibraryAccessManager.RecipientTag(_kid1), EpisodeNfo(r, 2), StringComparison.Ordinal);
        Assert.Contains("<lockedfields>OfficialRating|Tags</lockedfields>", EpisodeNfo(r, 2), StringComparison.Ordinal);
    }

    [Fact]
    public void C1_a_series_with_no_episode_the_sharer_may_share_is_refused_with_a_plain_message()
    {
        AddShow(1);
        _gw.SetVisible(_adult, _gw.EpisodeId(EpisodeFile(1)), false);

        var (decision, record) = Service().Create(_adult, _show, _kid1);

        Assert.Equal(Refusal.NotShareableType, decision.Refusal);
        Assert.Equal("There are no episodes of Spike Show you can share yet.", decision.Message);
        Assert.Null(record);
        Assert.Empty(_gw.CreatedLibraries);
        Assert.Empty(_gw.LibraryAccessCalls);
    }

    [Fact]
    public void C1_right_after_a_build_with_exclusions_the_sync_finds_nothing_to_rebuild()
    {
        AddShow(1, 2, 3);
        _gw.SetVisible(_adult, _gw.EpisodeId(EpisodeFile(2)), false);
        _gw.EpisodeDetails[EpisodeFile(3)] = new EpisodeDetail(_gw.EpisodeId(EpisodeFile(3)), "GB-18", new[] { "gore" });
        _gw.Users[_kid1] = _gw.Users[_kid1] with { BlockedTags = new[] { "gore" } };
        var r = ShareShow();

        var first = Sut().Run();
        var second = Sut().Run();

        Assert.Equal(0, first.Reapplied);
        Assert.Equal(0, second.Reapplied);
        Assert.Equal(new[] { Link(1) }, EpisodeLinks(Stored(r.Id)));
        Assert.Equal(ShareState.Active, Stored(r.Id).State);
    }

    [Fact]
    public void C1_the_sync_unlinks_an_episode_the_sharer_can_no_longer_see()
    {
        AddShow(1, 2);
        var r = ShareShow();
        Assert.Equal(new[] { Link(1), Link(2) }, EpisodeLinks(r));
        _gw.SetVisible(_adult, _gw.EpisodeId(EpisodeFile(2)), false);

        var report = Sut().Run();

        Assert.Equal(1, report.Reapplied);
        Assert.Equal(new[] { Link(1) }, EpisodeLinks(Stored(r.Id)));
    }

    [Fact]
    public void C1_the_sync_unlinks_an_episode_once_the_recipient_blocks_its_own_tag()
    {
        AddShow(1, 2);
        _gw.EpisodeDetails[EpisodeFile(2)] = new EpisodeDetail(_gw.EpisodeId(EpisodeFile(2)), string.Empty, new[] { "gore" });
        var r = ShareShow();
        Assert.Equal(new[] { Link(1), Link(2) }, EpisodeLinks(r));
        _gw.Users[_kid1] = _gw.Users[_kid1] with { BlockedTags = _gw.Users[_kid1].BlockedTags.Append("GORE").ToList() };

        var report = Sut().Run();

        Assert.Equal(1, report.Reapplied);
        Assert.Equal(new[] { Link(1) }, EpisodeLinks(Stored(r.Id)));
        Assert.Equal(ShareState.Active, Stored(r.Id).State); // the series itself is not tagged: the share stays
    }

    [Fact]
    public void C1_with_the_sharer_gone_an_episode_with_its_own_different_rating_is_never_added()
    {
        AddShow(1, 2);
        var r = ShareShow();
        _gw.Users.Remove(_adult);
        AddShow(1, 2, 3, 4);
        _gw.EpisodeDetails[EpisodeFile(3)] = new EpisodeDetail(_gw.EpisodeId(EpisodeFile(3)), "GB-18", Array.Empty<string>());
        _gw.EpisodeDetails[EpisodeFile(4)] = new EpisodeDetail(_gw.EpisodeId(EpisodeFile(4)), "GB-15", Array.Empty<string>()); // same as the series

        Sut().Run();

        Assert.Equal(new[] { Link(1), Link(2), Link(4) }, EpisodeLinks(Stored(r.Id)));
    }

    [Fact]
    public void C1_rule_7_and_the_audit_look_only_at_episodes_in_the_filtered_set()
    {
        AddShow(1, 2);
        var r = ShareShow();
        var sync = Sut();
        Assert.Equal(2, sync.FindCopyEpisodes(Stored(r.Id)).Count());
        _gw.SetVisible(_adult, _gw.EpisodeId(EpisodeFile(1)), false);

        Assert.Single(sync.FindCopyEpisodes(Stored(r.Id))); // E01 is no longer part of the share, even before a rebuild
    }

    // ---- Version 0.2, B: expiring shares ----

    // A share that ends at the given time, made as usual (the share service's own clock is the real one) and then dated.
    private ShareRecord ShareUntil(Guid film, Guid recipient, DateTime expires)
    {
        var r = Share(film, recipient) with { ExpiresUtc = expires };
        _store.Upsert(r);
        return r;
    }

    [Fact]
    public void An_expired_movie_share_closes_as_expired_and_its_copy_and_library_go()
    {
        var r = ShareUntil(_film, _kid1, _now.AddHours(1));
        var lib = Library(_kid1);
        var folder = Folder(r);
        _now = _now.AddHours(2);

        var report = Sut().Run();

        var after = Stored(r.Id);
        Assert.Equal(ShareState.Closed, after.State);
        Assert.Equal(ShareSynchronizer.ExpiredReason, after.StateReason);
        Assert.Equal("expired", ShareSynchronizer.ExpiredReason);
        Assert.Equal(string.Empty, after.FolderName);
        Assert.Equal(_now, after.ClosedUtc);
        Assert.Equal(r.ExpiresUtc, after.ExpiresUtc);
        Assert.False(Directory.Exists(folder));
        Assert.Contains(lib, _gw.RemovedLibraries);
        Assert.False(_store.Libraries().ContainsKey(_kid1));
        Assert.Equal(1, report.Closed);
    }

    [Fact]
    public void An_expired_series_share_closes_as_expired_and_its_copy_and_library_go()
    {
        AddShow(1, 2);
        var r = ShareShow() with { ExpiresUtc = _now.AddMinutes(5) };
        _store.Upsert(r);
        var lib = _store.Libraries()[_kid1][nameof(ShareKind.Series)];
        var folder = Path.Combine(ShowsRoot(_kid1), r.FolderName);
        Assert.True(Directory.Exists(folder));
        _now = _now.AddMinutes(6);

        Assert.Equal(1, Sut().Run().Closed);

        Assert.Equal((ShareState.Closed, ShareSynchronizer.ExpiredReason, string.Empty), (Stored(r.Id).State, Stored(r.Id).StateReason, Stored(r.Id).FolderName));
        Assert.False(Directory.Exists(folder));
        Assert.Contains(lib, _gw.RemovedLibraries);
    }

    [Fact]
    public void A_share_without_an_end_time_is_never_touched_by_expiry()
    {
        var r = Share(_film, _kid1);
        var other = ShareUntil(_film2, _kid1, _now.AddDays(1));
        _now = _now.AddDays(400);

        Sut().Run();

        Assert.Equal(ShareState.Active, Stored(r.Id).State);
        Assert.Null(Stored(r.Id).ExpiresUtc);
        Assert.True(Directory.Exists(Folder(r)));
        Assert.Equal(ShareState.Closed, Stored(other.Id).State);
    }

    [Fact]
    public void A_share_ends_exactly_at_its_end_time_and_not_a_tick_before()
    {
        var r = ShareUntil(_film, _kid1, _now.AddMinutes(10));
        _now = r.ExpiresUtc!.Value.AddTicks(-1);

        Sut().Run();
        Assert.Equal(ShareState.Active, Stored(r.Id).State);
        Assert.False(Stored(r.Id).IsExpired(_now));

        _now = r.ExpiresUtc.Value;
        Assert.True(Stored(r.Id).IsExpired(_now));
        Sut().Run();
        Assert.Equal((ShareState.Closed, ShareSynchronizer.ExpiredReason), (Stored(r.Id).State, Stored(r.Id).StateReason));
    }

    [Fact]
    public void A_share_closed_by_expiry_is_not_revived_by_later_runs()
    {
        var r = ShareUntil(_film, _kid1, _now.AddMinutes(1));
        _now = _now.AddMinutes(2);
        Sut().Run();
        var closed = Stored(r.Id);

        // Everything that would rebuild an Active share: the original still there and the recipient still unable to see it.
        _gw.ScanQueue.Clear();
        for (var i = 0; i < 3; i++)
        {
            _now = _now.AddMinutes(1);
            var report = Sut().Run();
            Assert.Equal((0, 0, 0), (report.Closed, report.Reapplied, report.Relinked));
        }

        Assert.Equal(closed, Stored(r.Id) with { ProviderIds = closed.ProviderIds });
        Assert.False(Directory.Exists(Path.Combine(MoviesRoot(_kid1), r.FolderName)));
        Assert.Empty(_gw.ScanQueue);
        Assert.Single(_store.All());
    }

    [Fact]
    public void An_expired_copy_that_cannot_be_removed_is_retried_and_ends_closed_as_expired()
    {
        var r = ShareUntil(_film, _kid1, _now.AddMinutes(1));
        _now = _now.AddMinutes(2);
        _builder.FailRemove = true;

        Sut().Run();
        Assert.Equal((ShareState.Broken, ShareService.CouldNotRemoveReason), (Stored(r.Id).State, Stored(r.Id).StateReason));
        Assert.True(Directory.Exists(Folder(r)));

        _builder.FailRemove = false;
        Sut().Run();

        // Not deleted as a removed share would be: the record stays to say it expired.
        Assert.Equal((ShareState.Closed, ShareSynchronizer.ExpiredReason), (Stored(r.Id).State, Stored(r.Id).StateReason));
        Assert.False(Directory.Exists(Folder(r)));
    }

    [Fact]
    public void A_broken_share_that_expires_is_closed_as_expired()
    {
        var r = ShareUntil(_film, _kid1, _now.AddMinutes(1));
        _gw.Items.Remove(_film);
        Sut().Run();
        Assert.Equal((ShareState.Broken, ShareSynchronizer.OriginalMissingReason), (Stored(r.Id).State, Stored(r.Id).StateReason));

        _now = _now.AddMinutes(2);
        Sut().Run();

        Assert.Equal((ShareState.Closed, ShareSynchronizer.ExpiredReason), (Stored(r.Id).State, Stored(r.Id).StateReason));
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
