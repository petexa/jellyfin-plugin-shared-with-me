using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SharedWithMe.Configuration;
using Jellyfin.Plugin.SharedWithMe.Model;
using Jellyfin.Plugin.SharedWithMe.Sharing;
using Jellyfin.Plugin.SharedWithMe.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SharedWithMe.Tests;

public sealed class ShareServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "swm-svc-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServerGateway _gw = new();
    private readonly ShareStore _store;
    private readonly string _media;
    private string _shareRoot;
    private readonly PluginGate _gate = new();
    private readonly TestBuilder _builder = new();
    private WhoMayShare _who = WhoMayShare.Everyone;
    private Guid[] _allowed = Array.Empty<Guid>();

    private readonly Guid _lib = Guid.NewGuid();
    private readonly Guid _adult = Guid.NewGuid();
    private readonly Guid _kid1 = Guid.NewGuid();
    private readonly Guid _kid2 = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _film = Guid.NewGuid();
    private readonly Guid _film2 = Guid.NewGuid();

    public ShareServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new ShareStore(Path.Combine(_dir, "store"));
        _shareRoot = Path.Combine(_dir, "shares");
        _media = Path.Combine(_dir, "media");
        Directory.CreateDirectory(_media);

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

    private Action<string>? _probe;
    private Func<DateTime>? _clock;

    private ShareService Sut()
    {
        _gw.ShareRoot = _shareRoot;
        var eligibility = new ShareEligibility(_gw, _store, () => _who, () => _allowed);
        var access = new LibraryAccessManager(_gw, _store);
        return new ShareService(_gw, _store, eligibility, access, _builder, () => _shareRoot, _gate, NullLogger.Instance, _probe, _clock);
    }

    private void AddUser(Guid id, string name, int? cap, int? sub = null, bool admin = false, bool disabled = false, bool hidden = false) =>
        _gw.Users[id] = new UserInfo(id, name, admin, disabled, hidden, cap, sub, true, new List<Guid>(), Array.Empty<string>(), Array.Empty<string>());

    private void AddFilm(Guid id, string title, int year, string rating)
    {
        var folder = Path.Combine(_media, title);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, title + ".mp4");
        File.WriteAllText(file, "video");
        _gw.Items[id] = new ItemInfo(id, ShareKind.Movie, title, year, rating, new Dictionary<string, string> { ["Tmdb"] = "1" }, _lib, new List<string>(), file);
        _gw.Media[id] = new SourceMedia(file, Array.Empty<string>(), Array.Empty<(int, int, string)>());
        _gw.SetVisible(_adult, id);
        _gw.SetVisible(_admin, id);
    }

    private string MoviesRoot(Guid recipient) => Path.Combine(_shareRoot, recipient.ToString("N"), "movies");

    // Every entry under the share root that a scan could pick up (anything not dot-prefixed).
    private List<string> Visible(Guid recipient) =>
        Directory.Exists(MoviesRoot(recipient))
            ? Directory.EnumerateFileSystemEntries(MoviesRoot(recipient)).Where(p => !Path.GetFileName(p).StartsWith('.')).ToList()
            : new List<string>();

    private int Count(string method) => _gw.Calls.Count(c => c.Method == method);

    [Fact]
    public void Create_happy_path_writes_folder_record_and_library()
    {
        var (decision, record) = Sut().Create(_adult, _film, _kid1);

        Assert.Equal(Refusal.None, decision.Refusal);
        Assert.NotNull(record);
        Assert.Equal(_film, record!.OriginalItemId);
        Assert.Equal(_kid1, record.RecipientUserId);
        Assert.Equal(_adult, record.SharerUserId);
        Assert.Equal("Adult", record.SharerName);
        Assert.Equal(ShareKind.Movie, record.Kind);
        Assert.Equal(ShareState.Active, record.State);
        Assert.Equal(_lib, record.SourceLibraryId);
        Assert.Equal("Spike Eighteen", record.Title);

        var stored = Assert.Single(_store.All());
        Assert.Equal(record, stored with { ProviderIds = record.ProviderIds });
        Assert.False(string.IsNullOrEmpty(stored.FolderName));

        var folder = Path.Combine(MoviesRoot(_kid1), stored.FolderName);
        Assert.True(Directory.Exists(folder));
        var nfo = File.ReadAllText(Path.Combine(folder, stored.FolderName + ".nfo"));
        Assert.Contains("<mpaa>GB-18</mpaa>", nfo, StringComparison.Ordinal);
        Assert.Contains("<customrating>12</customrating>", nfo, StringComparison.Ordinal);
        Assert.Single(Visible(_kid1));

        var lib = Assert.Single(_gw.CreatedLibraries);
        Assert.Equal(MoviesRoot(_kid1), lib.Path);
        Assert.Equal(ShareKind.Movie, lib.Kind);
        Assert.Equal(lib.Id, _store.Libraries()[_kid1][nameof(ShareKind.Movie)]);
        Assert.Contains(lib.Id, _gw.Users[_kid1].EnabledLibraries);
        Assert.DoesNotContain(lib.Id, _gw.Users[_kid2].EnabledLibraries);
        Assert.DoesNotContain(lib.Id, _gw.Users[_adult].EnabledLibraries);
        Assert.Equal(new[] { lib.Id }, _gw.ScanQueue);
    }

    [Fact]
    public void Create_order_is_check_isolation_folder_library_record_scan()
    {
        Sut().Create(_adult, _film, _kid1);

        var methods = _gw.Calls.Select(c => c.Method).ToList();
        var firstCanSee = methods.IndexOf(nameof(IServerGateway.CanSee));
        var isolation = methods.IndexOf(nameof(IServerGateway.SetLibraryAccess));
        var media = methods.IndexOf(nameof(IServerGateway.GetSourceMedia));
        var create = methods.IndexOf(nameof(IServerGateway.CreateLibrary));
        var scan = methods.IndexOf(nameof(IServerGateway.QueueScan));
        Assert.True(firstCanSee >= 0 && firstCanSee < isolation && isolation < media && media < create && create < scan, string.Join(",", methods));
        Assert.Equal(scan, methods.Count - 1);
    }

    [Fact]
    public void Create_refused_by_check_changes_nothing()
    {
        _gw.SetVisible(_kid1, _film); // recipient already sees it

        var (decision, record) = Sut().Create(_adult, _film, _kid1);

        Assert.Equal(Refusal.RecipientAlreadySees, decision.Refusal);
        Assert.Null(record);
        Assert.Empty(_store.All());
        Assert.Empty(_store.Snapshots());
        Assert.Empty(_gw.LibraryAccessCalls);
        Assert.Equal(0, Count(nameof(IServerGateway.CreateLibrary)));
        Assert.False(Directory.Exists(_shareRoot));
    }

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("io")]
    public void A_probe_that_is_refused_access_is_the_setup_refusal_not_an_error(string kind)
    {
        _probe = _ => throw (kind == "io" ? new IOException("no links") : new UnauthorizedAccessException("denied"));

        var (decision, record) = Sut().Create(_adult, _film, _kid1);

        Assert.Equal(Refusal.ShareFolderUnusable, decision.Refusal);
        Assert.Equal(ShareService.SetupProblemMessage, decision.Message);
        Assert.Null(record);
        Assert.Empty(_store.All());
        Assert.Empty(_gw.LibraryAccessCalls);
    }

    // Review Focus 2.
    [Fact]
    public void Create_with_unusable_share_root_is_refused_and_leaves_nothing()
    {
        var blocker = Path.Combine(_dir, "a-file");
        File.WriteAllText(blocker, "not a folder");
        _shareRoot = Path.Combine(blocker, "shares");

        var (decision, record) = Sut().Create(_adult, _film, _kid1);

        Assert.Equal(Refusal.ShareFolderUnusable, decision.Refusal);
        Assert.Equal(ShareService.SetupProblemMessage, decision.Message);
        Assert.Null(record);
        Assert.Empty(_store.All());
        Assert.Empty(_store.Snapshots());
        Assert.Empty(_store.Libraries());
        Assert.Equal(0, Count(nameof(IServerGateway.CreateLibrary)));
        Assert.Empty(_gw.LibraryAccessCalls);
        Assert.Empty(_gw.ScanQueue);
    }

    [Fact]
    public void Create_when_library_creation_throws_leaves_no_folder_and_no_record()
    {
        _gw.ThrowOnce = nameof(IServerGateway.CreateLibrary);

        Assert.Throws<InvalidOperationException>(() => Sut().Create(_adult, _film, _kid1));

        Assert.Empty(_store.All());
        Assert.Empty(Visible(_kid1));
        Assert.Empty(Directory.Exists(MoviesRoot(_kid1)) ? Directory.EnumerateFileSystemEntries(MoviesRoot(_kid1)) : Array.Empty<string>());
        Assert.Empty(_gw.ScanQueue);
    }

    [Fact]
    public void Create_when_access_write_after_library_throws_leaves_no_folder_and_no_record()
    {
        // Isolation writes for Adult, Kid2 and Admin and Kid1 succeed; fail the recipient grant after CreateLibrary.
        var sut = Sut();
        sut.Create(_adult, _film2, _kid2); // isolation done, Kid2 has a library: later access writes are only grants
        _gw.Calls.Clear();
        _gw.ThrowOnce = nameof(IServerGateway.SetLibraryAccess);

        Assert.Throws<InvalidOperationException>(() => sut.Create(_adult, _film, _kid1));

        Assert.Single(_store.All()); // Kid2's share only
        Assert.DoesNotContain(_store.All(), r => r.RecipientUserId == _kid1);
        Assert.Empty(Visible(_kid1));
    }

    [Fact]
    public void Create_when_scan_throws_removes_folder_and_record()
    {
        _gw.ThrowOnce = nameof(IServerGateway.QueueScan);

        Assert.Throws<InvalidOperationException>(() => Sut().Create(_adult, _film, _kid1));

        Assert.Empty(_store.All());
        Assert.Empty(Visible(_kid1));
    }

    [Fact]
    public void Create_from_a_copy_shares_the_original()
    {
        var sut = Sut();
        var (_, kid1Share) = sut.Create(_adult, _film, _kid1);
        var shareLib = _store.Libraries()[_kid1][nameof(ShareKind.Movie)];
        var copy = Guid.NewGuid();
        var copyPath = Path.Combine(MoviesRoot(_kid1), kid1Share!.FolderName, kid1Share.FolderName + ".mp4");
        _gw.Items[copy] = _gw.Items[_film] with { Id = copy, LibraryId = shareLib, Path = copyPath };
        _gw.SetVisible(_adult, copy); // the sharer must see what they submit

        var (decision, record) = sut.Create(_adult, copy, _kid2);

        Assert.Equal(Refusal.None, decision.Refusal);
        Assert.Equal(_film, record!.OriginalItemId);
        var link = new FileInfo(Path.Combine(MoviesRoot(_kid2), record.FolderName, record.FolderName + ".mp4"));
        Assert.Equal(_gw.Items[_film].Path, link.LinkTarget);
    }

    [Fact]
    public void Create_writes_the_recipient_tag_into_the_nfo()
    {
        var (_, record) = Sut().Create(_adult, _film, _kid1);

        var nfo = File.ReadAllText(Path.Combine(MoviesRoot(_kid1), record!.FolderName, record.FolderName + ".nfo"));
        Assert.Contains("<tag>" + LibraryAccessManager.RecipientTag(_kid1) + "</tag>", nfo, StringComparison.Ordinal);
        Assert.Contains(LibraryAccessManager.RecipientTag(_kid1), _gw.Users[_kid2].BlockedTags);
        Assert.DoesNotContain(LibraryAccessManager.RecipientTag(_kid1), _gw.Users[_kid1].BlockedTags);
    }

    [Theory]
    [InlineData("share-root-inside-library")]
    [InlineData("share-root-is-library")]
    [InlineData("library-inside-share-root")]
    [InlineData("library-in-a-recipient-folder-but-not-the-layout")]
    public void Create_refuses_a_share_root_overlapping_a_library_and_creates_nothing(string layout)
    {
        var libPath = layout switch
        {
            "share-root-inside-library" => _dir,
            "share-root-is-library" => _shareRoot,
            "library-inside-share-root" => Path.Combine(_shareRoot, "x"),
            _ => Path.Combine(_shareRoot, Guid.NewGuid().ToString("N"), "films"),
        };
        _gw.AddRealLibrary(libPath);

        var (decision, record) = Sut().Create(_adult, _film, _kid1);

        Assert.Equal(Refusal.ShareFolderUnusable, decision.Refusal);
        Assert.Equal(ShareService.SetupProblemMessage, decision.Message);
        Assert.DoesNotContain("/", decision.Message, StringComparison.Ordinal);
        Assert.Null(record);
        Assert.False(Directory.Exists(_shareRoot));
        Assert.Empty(_store.Snapshots());
        Assert.Empty(_gw.BlockedTagCalls);
        Assert.Empty(_gw.LibraryAccessCalls);
        Assert.Equal(0, Count(nameof(IServerGateway.CreateLibrary)));
        Assert.Equal(0, Count(nameof(IServerGateway.CanSee))); // decided before Check looks at anything
    }

    [Fact]
    public void Create_allows_share_libraries_in_the_layout_and_prefix_only_siblings()
    {
        _gw.AddRealLibrary(_shareRoot + "-other");
        _gw.SeedLibrary(Path.Combine(_shareRoot, Guid.NewGuid().ToString("N"), "movies"));
        _gw.SeedLibrary(Path.Combine(_shareRoot, Guid.NewGuid().ToString("N"), "shows"));

        Assert.Equal(Refusal.None, Sut().Create(_adult, _film, _kid1).decision.Refusal);
    }

    [Fact]
    public void Remove_when_the_copy_cannot_be_removed_keeps_the_record_as_broken_and_throws()
    {
        var (_, record) = Sut().Create(_adult, _film, _kid1);
        var lib = _gw.CreatedLibraries.Single().Id;
        _builder.FailRemove = true;

        Assert.Throws<IOException>(() => Sut().Remove(record!.Id, _adult));

        var kept = Assert.Single(_store.All());
        Assert.Equal(ShareState.Broken, kept.State);
        Assert.Equal(ShareService.CouldNotRemoveReason, kept.StateReason);
        Assert.Empty(_gw.RemovedLibraries);
        Assert.Contains(lib, _gw.Users[_kid1].EnabledLibraries);
    }

    [Fact]
    public void Undo_when_the_copy_cannot_be_removed_keeps_a_broken_record_and_rethrows_the_original()
    {
        _gw.ThrowOnce = nameof(IServerGateway.QueueScan);
        _builder.FailRemove = true;

        var ex = Assert.Throws<InvalidOperationException>(() => Sut().Create(_adult, _film, _kid1));

        Assert.Contains("QueueScan", ex.Message, StringComparison.Ordinal);
        var kept = Assert.Single(_store.All());
        Assert.Equal(ShareState.Broken, kept.State);
        Assert.Equal(ShareService.CouldNotRemoveReason, kept.StateReason);
        Assert.Single(Visible(_kid1));
        Assert.Empty(_gw.RemovedLibraries); // the library still holds the copy
    }

    [Fact]
    public void A_failed_first_share_leaves_no_tag_blocks()
    {
        _gw.ThrowOnce = nameof(IServerGateway.CreateLibrary); // fails after the blocks were placed

        Assert.Throws<InvalidOperationException>(() => Sut().Create(_adult, _film, _kid1));

        Assert.NotEmpty(_gw.BlockedTagCalls);
        Assert.All(_gw.Users.Values, u => Assert.Empty(u.BlockedTags));
        Assert.Empty(_store.All());
        Assert.Empty(Visible(_kid1));
    }

    [Fact]
    public void RecipientsFor_is_empty_for_someone_who_may_not_share()
    {
        _who = WhoMayShare.AdminsOnly;
        Assert.Empty(Sut().RecipientsFor(_adult));
        Assert.NotEmpty(Sut().RecipientsFor(_admin));

        _who = WhoMayShare.UncappedOnly;
        Assert.Empty(Sut().RecipientsFor(_kid1));
        Assert.NotEmpty(Sut().RecipientsFor(_adult));
    }

    [Fact]
    public void RecipientsFor_with_selected_profiles_lists_people_for_administrators_and_listed_profiles_only()
    {
        _who = WhoMayShare.SelectedProfiles;
        _allowed = new[] { _kid1 };

        Assert.NotEmpty(Sut().RecipientsFor(_admin));
        Assert.Contains(Sut().RecipientsFor(_kid1), u => u.Id == _kid2);
        Assert.Empty(Sut().RecipientsFor(_adult));
        Assert.Empty(Sut().RecipientsFor(_kid2));
    }

    [Fact]
    public void Create_with_selected_profiles_refuses_an_unlisted_sharer_and_keeps_the_shares_of_one_taken_off()
    {
        _who = WhoMayShare.SelectedProfiles;
        _allowed = new[] { _adult };
        var (made, record) = Sut().Create(_adult, _film, _kid1);
        Assert.Equal(Refusal.None, made.Refusal);
        Assert.NotNull(record);

        _allowed = Array.Empty<Guid>();
        var (refused, none) = Sut().Create(_adult, _film2, _kid1);
        Assert.Equal(Refusal.SharingNotAllowed, refused.Refusal);
        Assert.Null(none);
        Assert.Equal(new[] { record!.Id }, _store.All().Where(r => r.State == ShareState.Active).Select(r => r.Id));

        var (byAdmin, adminRecord) = Sut().Create(_admin, _film2, _kid1);
        Assert.Equal(Refusal.None, byAdmin.Refusal);
        Assert.NotNull(adminRecord);
    }

    // Review Focus 4.
    [Fact]
    public async Task Two_parallel_creates_for_one_recipient_give_two_records_and_one_library()
    {
        _gw.CallDelayMs = 5;
        var sut = Sut();
        using var start = new ManualResetEventSlim(false);
        var a = Task.Run(() => { start.Wait(); return sut.Create(_adult, _film, _kid1); });
        var b = Task.Run(() => { start.Wait(); return Sut().Create(_adult, _film2, _kid1); });
        start.Set();
        var results = await Task.WhenAll(a, b);

        Assert.All(results, r => Assert.Equal(Refusal.None, r.decision.Refusal));
        Assert.Equal(2, _store.All().Count);
        Assert.Single(_gw.CreatedLibraries);
        Assert.Equal(2, Visible(_kid1).Count);
    }

    [Fact]
    public void Remove_by_a_third_user_returns_false_and_changes_nothing()
    {
        var (_, record) = Sut().Create(_adult, _film, _kid1);
        _gw.Calls.Clear();

        Assert.False(Sut().Remove(record!.Id, _kid2));
        Assert.False(Sut().Remove(record.Id, _kid1)); // the recipient is not the sharer either

        Assert.Single(_store.All());
        Assert.Single(Visible(_kid1));
        Assert.Empty(_gw.RemovedLibraries);
    }

    [Fact]
    public void Remove_unknown_id_returns_false()
    {
        Assert.False(Sut().Remove(Guid.NewGuid(), _admin));
    }

    [Fact]
    public void Remove_by_sharer_removes_folder_record_and_empty_library()
    {
        var (_, record) = Sut().Create(_adult, _film, _kid1);
        var lib = _gw.CreatedLibraries.Single().Id;

        Assert.True(Sut().Remove(record!.Id, _adult));

        Assert.Empty(_store.All());
        Assert.Empty(Visible(_kid1));
        Assert.Equal(new[] { lib }, _gw.RemovedLibraries);
        Assert.DoesNotContain(lib, _gw.Users[_kid1].EnabledLibraries);
        Assert.False(_store.Libraries().ContainsKey(_kid1));
        Assert.All(_gw.Users.Values, u => Assert.DoesNotContain(u.BlockedTags, t => t.StartsWith("swm-", StringComparison.Ordinal)));
    }

    [Fact]
    public void Remove_by_admin_keeps_library_while_other_shares_remain_and_queues_scan()
    {
        var sut = Sut();
        var (_, first) = sut.Create(_adult, _film, _kid1);
        sut.Create(_adult, _film2, _kid1);
        var lib = _gw.CreatedLibraries.Single().Id;
        _gw.ScanQueue.Clear();

        Assert.True(sut.Remove(first!.Id, _admin));

        Assert.Single(_store.All());
        Assert.Single(Visible(_kid1));
        Assert.Empty(_gw.RemovedLibraries);
        Assert.Equal(new[] { lib }, _gw.ScanQueue);
    }

    [Fact]
    public void ListFor_gives_own_shares_to_a_user_and_all_to_an_admin()
    {
        var sut = Sut();
        sut.Create(_adult, _film, _kid1);
        AddUser(_kid2, "Kid2", null); // uncapped Kid2 can share too
        _gw.SetVisible(_kid2, _film2);
        sut.Create(_kid2, _film2, _kid1);

        Assert.Equal(new[] { _film }, sut.ListFor(_adult).Select(r => r.OriginalItemId));
        Assert.Equal(new[] { _film2 }, sut.ListFor(_kid2).Select(r => r.OriginalItemId));
        Assert.Empty(sut.ListFor(_kid1));
        Assert.Equal(2, sut.ListFor(_admin).Count);
        Assert.Empty(sut.ListFor(Guid.NewGuid()));
    }

    [Fact]
    public void RecipientsFor_leaves_out_self_and_disabled()
    {
        var hidden = Guid.NewGuid();
        var disabled = Guid.NewGuid();
        AddUser(hidden, "Hidden", null, hidden: true);
        AddUser(disabled, "Disabled", null, disabled: true);

        var names = Sut().RecipientsFor(_adult).Select(u => u.Name).ToList();

        Assert.Equal(new[] { "Admin", "Hidden", "Kid1", "Kid2" }, names); // hidden only means "not on the login screen"
    }

    // ---------- Search ----------

    [Fact]
    public void Search_returns_only_what_the_caller_can_see()
    {
        _gw.SetVisible(_kid1, _film2);

        var hits = Sut().Search(_kid1, "Spike")!;

        Assert.Equal(new[] { _film2 }, hits.Select(h => h.Id));
        Assert.Equal(2, Sut().Search(_adult, "spike")!.Count); // control: the same term finds both for Adult
        var h = Sut().Search(_adult, "Eighteen")!.Single();
        Assert.Equal(("Spike Eighteen", 2002, ShareKind.Movie, "GB-18"), (h.Title, h.Year, h.Kind, h.OfficialRating));
    }

    [Fact]
    public void Search_with_fewer_than_two_characters_is_empty_without_asking_the_server()
    {
        Assert.Empty(Sut().Search(_adult, "S")!);
        Assert.Empty(Sut().Search(_adult, "  ")!);
        Assert.Empty(Sut().Search(_adult, null)!);
        Assert.Equal(0, Count(nameof(IServerGateway.SearchVisible)));
    }

    [Fact]
    public void Search_leaves_out_anything_in_a_share_library()
    {
        var (_, record) = Sut().Create(_adult, _film, _kid1);
        var copy = Guid.NewGuid();
        var path = Path.Combine(MoviesRoot(_kid1), record!.FolderName, record.FolderName + ".mp4");
        _gw.Items[copy] = _gw.Items[_film] with { Id = copy, Path = path };
        _gw.SetVisible(_kid1, copy);
        _gw.SetVisible(_adult, copy); // even if the server ever let it through

        Assert.Empty(Sut().Search(_kid1, "Spike")!);
        Assert.DoesNotContain(copy, Sut().Search(_adult, "Spike")!.Select(h => h.Id));
        Assert.Contains(_film, Sut().Search(_adult, "Spike")!.Select(h => h.Id)); // control
    }

    [Fact]
    public void Search_returns_at_most_thirty()
    {
        for (var i = 0; i < 40; i++)
        {
            AddFilm(Guid.NewGuid(), $"Spike Extra {i:00}", 2000, "PG");
        }

        Assert.Equal(ShareService.SearchLimit, Sut().Search(_adult, "Spike")!.Count);
    }

    [Fact]
    public void Search_as_another_user_is_for_enabled_administrators_only()
    {
        Assert.Null(Sut().Search(_adult, "Spike", _kid1));
        Assert.Equal(0, Count(nameof(IServerGateway.SearchVisible)));

        Assert.Empty(Sut().Search(_admin, "Spike", _kid1)!); // Kid1 sees neither film
        Assert.Equal(2, Sut().Search(_adult, "Spike", _adult)!.Count); // oneself is fine

        AddUser(_admin, "Admin", null, admin: true, disabled: true);
        Assert.Null(Sut().Search(_admin, "Spike", _kid1));
    }

    [Fact]
    public void Search_by_a_disabled_or_unknown_user_is_empty()
    {
        AddUser(_adult, "Adult", null, disabled: true);
        Assert.Empty(Sut().Search(_adult, "Spike")!);
        Assert.Empty(Sut().Search(Guid.NewGuid(), "Spike")!);
    }

    [Fact]
    public void A_series_with_no_episode_file_is_refused_and_nothing_is_built()
    {
        var show = Guid.NewGuid();
        _gw.Items[show] = new ItemInfo(show, ShareKind.Series, "Empty Show", 2010, "GB-15", new Dictionary<string, string>(), _lib, new List<string>(), Path.Combine(_media, "Empty Show"));
        _gw.Media[show] = new SourceMedia(string.Empty, Array.Empty<string>(), Array.Empty<(int, int, string)>());
        _gw.SetVisible(_adult, show);

        var (decision, record) = Sut().Create(_adult, show, _kid1);

        Assert.Equal(Refusal.NotShareableType, decision.Refusal);
        Assert.Equal("There are no episodes of Empty Show you can share yet.", decision.Message);
        Assert.Null(record);
        Assert.Empty(_store.All());
        Assert.Empty(_gw.CreatedLibraries);
        Assert.Empty(_gw.LibraryAccessCalls);
        Assert.False(Directory.Exists(Path.Combine(_shareRoot, _kid1.ToString("N"))));
    }

    // M6: a disc-folder film (BDMV / VIDEO_TS) has a folder as its path: refused before anything changes.
    [Fact]
    public void A_disc_folder_film_is_refused_with_a_plain_message_and_nothing_changes()
    {
        var disc = Guid.NewGuid();
        var folder = Path.Combine(_media, "Disc Film (2004)");
        Directory.CreateDirectory(Path.Combine(folder, "BDMV"));
        _gw.Items[disc] = new ItemInfo(disc, ShareKind.Movie, "Disc Film", 2004, "GB-15", new Dictionary<string, string>(), _lib, new List<string>(), folder);
        _gw.SetVisible(_adult, disc);

        var (decision, record) = Sut().Create(_adult, disc, _kid1);

        Assert.Equal(Refusal.NotShareableType, decision.Refusal);
        Assert.Equal("Disc Film is a disc folder (Blu-ray or DVD), which can't be shared.", decision.Message);
        Assert.Null(record);
        Assert.Empty(_gw.LibraryAccessCalls);
        Assert.Empty(_gw.CreatedLibraries);
        Assert.Empty(_store.All());
    }

    // ---- Version 0.2, B: expiring shares ----

    [Fact]
    public void Create_with_an_end_time_stores_it_as_utc()
    {
        var now = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
        _clock = () => now;

        var (decision, record) = Sut().Create(_adult, _film, _kid1, now.AddSeconds(5));

        Assert.Equal(Refusal.None, decision.Refusal);
        Assert.Equal(now.AddSeconds(5), record!.ExpiresUtc);
        Assert.Equal(DateTimeKind.Utc, record.ExpiresUtc!.Value.Kind);
        Assert.Equal(now, record.CreatedUtc);
        var stored = Assert.Single(_store.All());
        Assert.Equal(now.AddSeconds(5), stored.ExpiresUtc);
        Assert.Equal(DateTimeKind.Utc, stored.ExpiresUtc!.Value.Kind);

        // An end time with no zone is taken as UTC.
        var (_, second) = Sut().Create(_adult, _film2, _kid1, DateTime.SpecifyKind(now.AddDays(1), DateTimeKind.Unspecified));
        Assert.Equal(now.AddDays(1), second!.ExpiresUtc);
        Assert.Equal(DateTimeKind.Utc, second.ExpiresUtc!.Value.Kind);
    }

    [Fact]
    public void Create_without_an_end_time_keeps_the_share_until_removed()
    {
        var (_, record) = Sut().Create(_adult, _film, _kid1);

        Assert.Null(record!.ExpiresUtc);
        Assert.Null(Assert.Single(_store.All()).ExpiresUtc);
    }

    [Theory]
    [InlineData(-60)]
    [InlineData(0)] // exactly now would never be live
    public void Create_refuses_an_end_time_that_has_passed_and_changes_nothing(int seconds)
    {
        var now = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
        _clock = () => now;

        var (decision, record) = Sut().Create(_adult, _film, _kid1, now.AddSeconds(seconds));

        Assert.Null(record);
        Assert.Equal(Refusal.InvalidExpiry, decision.Refusal);
        Assert.Equal(ShareService.ExpiryPastMessage, decision.Message);
        Assert.Empty(_store.All());
        Assert.Empty(_gw.CreatedLibraries);
        Assert.Empty(Visible(_kid1));
        Assert.Empty(_gw.LibraryAccessCalls);
    }

    [Fact]
    public void Create_refuses_an_end_time_more_than_366_days_ahead_and_allows_366()
    {
        var now = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
        _clock = () => now;

        var (decision, record) = Sut().Create(_adult, _film, _kid1, now.AddDays(366).AddSeconds(1));
        Assert.Null(record);
        Assert.Equal(Refusal.InvalidExpiry, decision.Refusal);
        Assert.Equal(ShareService.ExpiryTooFarMessage, decision.Message);
        Assert.Empty(_store.All());

        var (ok, made) = Sut().Create(_adult, _film, _kid1, now.AddDays(366));
        Assert.Equal(Refusal.None, ok.Refusal);
        Assert.Equal(now.AddDays(366), made!.ExpiresUtc);
    }

    [Fact]
    public void A_closed_expired_share_does_not_stop_the_title_being_shared_again()
    {
        var (_, first) = Sut().Create(_adult, _film, _kid1);
        _store.Upsert(Assert.Single(_store.All()) with { State = ShareState.Closed, StateReason = ShareSynchronizer.ExpiredReason, FolderName = string.Empty });
        _builder.Remove(MoviesRoot(_kid1), first!.FolderName);

        var (decision, again) = Sut().Create(_adult, _film, _kid1);

        Assert.Equal(Refusal.None, decision.Refusal);
        Assert.NotEqual(first.Id, again!.Id);
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
