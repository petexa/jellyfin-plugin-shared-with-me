using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SharedWithMe.Model;
using Jellyfin.Plugin.SharedWithMe.Sharing;
using Jellyfin.Plugin.SharedWithMe.Storage;
using Xunit;

namespace Jellyfin.Plugin.SharedWithMe.Tests;

public sealed class LibraryAccessManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "swm-lam-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServerGateway _gw = new();
    private readonly ShareStore _store;
    private readonly Guid _libA = Guid.NewGuid();
    private readonly Guid _libB = Guid.NewGuid();
    private readonly Guid _kid1 = Guid.NewGuid();
    private readonly Guid _kid2 = Guid.NewGuid();
    private readonly Guid _adult = Guid.NewGuid();

    public LibraryAccessManagerTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new ShareStore(_dir);
        _gw.RealLibraryIds.AddRange(new[] { _libA, _libB });
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private LibraryAccessManager Sut() => new(_gw, _store);

    private void AddUser(Guid id, bool all, params Guid[] enabled) =>
        AddUser(id, all, false, false, enabled);

    private void AddUser(Guid id, bool all, bool admin, bool disabled, params Guid[] enabled) =>
        _gw.Users[id] = new UserInfo(id, "u" + id.ToString("N")[..4], admin, disabled, false, null, null, all, enabled.ToList(), Array.Empty<string>(), Array.Empty<string>());

    private IReadOnlyList<Guid> Enabled(Guid id) => _gw.Users[id].EnabledLibraries;

    private void AddRecord(Guid recipient, ShareKind kind, ShareState state, string folder = "T (2000)")
    {
        var id = Guid.NewGuid();
        _store.Upsert(new ShareRecord(id, Guid.NewGuid(), kind, "T", 2000, new Dictionary<string, string>(), _libA, _adult, "Adult", recipient, DateTime.UtcNow, state, null, folder));
    }

    private int Count(string method) => _gw.Calls.Count(c => c.Method == method);

    // Snapshots hold a list, compared by reference; compare its contents.
    private static object Snap(PolicySnapshot s) => (s.UserId, s.HadAllLibraries, s.ChangedUtc, string.Join(",", s.GivenLibraries ?? Array.Empty<Guid>()));

    // ---------- EnsureIsolation ----------

    [Fact]
    public void Isolation_moves_all_library_users_to_explicit_real_list_with_snapshots()
    {
        AddUser(_kid1, true);
        AddUser(_kid2, true);

        Sut().EnsureIsolation();

        foreach (var u in new[] { _kid1, _kid2 })
        {
            Assert.False(_gw.Users[u].AllLibraries);
            Assert.Equal(new[] { _libA, _libB }, Enabled(u));
        }

        Assert.Equal(new[] { _kid1, _kid2 }.OrderBy(x => x), _store.Snapshots().Select(s => s.UserId).OrderBy(x => x));
        Assert.All(_store.Snapshots(), s => Assert.True(s.HadAllLibraries));
        Assert.Equal(2, _gw.LibraryAccessCalls.Count);
    }

    [Fact]
    public void Isolation_leaves_explicit_user_alone_with_no_snapshot()
    {
        AddUser(_kid1, false, _libA);

        Sut().EnsureIsolation();

        Assert.Equal(new[] { _libA }, Enabled(_kid1));
        Assert.Empty(_store.Snapshots());
        Assert.Empty(_gw.LibraryAccessCalls);
    }

    [Fact]
    public void Isolation_includes_admins_and_disabled_users()
    {
        AddUser(_kid1, true, true, false);
        AddUser(_kid2, true, false, true);

        Sut().EnsureIsolation();

        Assert.False(_gw.Users[_kid1].AllLibraries);
        Assert.False(_gw.Users[_kid2].AllLibraries);
        Assert.Equal(2, _store.Snapshots().Count);
    }

    [Fact]
    public void Isolation_second_call_changes_nothing()
    {
        AddUser(_kid1, true);
        AddUser(_kid2, false, _libB);
        var sut = Sut();
        sut.EnsureIsolation();
        var calls = _gw.LibraryAccessCalls.Count;
        var snaps = _store.Snapshots().Select(Snap).ToList();

        sut.EnsureIsolation();

        Assert.Equal(calls, _gw.LibraryAccessCalls.Count);
        Assert.Equal(snaps, _store.Snapshots().Select(Snap).ToList());
    }

    [Fact]
    public void Isolation_crash_after_snapshot_leaves_snapshot_and_rerun_completes()
    {
        AddUser(_kid1, true);
        _gw.ThrowOnce = nameof(IServerGateway.SetLibraryAccess);

        Assert.Throws<InvalidOperationException>(() => Sut().EnsureIsolation());

        Assert.True(_gw.Users[_kid1].AllLibraries);
        var first = Snap(Assert.Single(_store.Snapshots()));
        Assert.Empty(_gw.LibraryAccessCalls);

        Sut().EnsureIsolation();

        Assert.False(_gw.Users[_kid1].AllLibraries);
        Assert.Equal(new[] { _libA, _libB }, Enabled(_kid1));
        Assert.Equal(first, Snap(Assert.Single(_store.Snapshots())));
        Assert.Single(_gw.LibraryAccessCalls);
    }

    [Fact]
    public void Isolation_user_with_snapshot_back_on_all_libraries_is_redone_and_keeps_original_date()
    {
        var old = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        _store.UpsertSnapshot(new PolicySnapshot(_kid1, true, old));
        var own = Guid.NewGuid();
        _store.SetLibrary(_kid1, ShareKind.Movie, own);
        AddUser(_kid1, true);

        Sut().EnsureIsolation();

        Assert.False(_gw.Users[_kid1].AllLibraries);
        Assert.Equal(new[] { _libA, _libB, own }, Enabled(_kid1));
        Assert.Equal(old, Assert.Single(_store.Snapshots()).ChangedUtc);
    }

    [Fact]
    public void Isolation_removes_share_library_that_is_not_theirs()
    {
        var kid1s = Guid.NewGuid();
        _store.SetLibrary(_kid1, ShareKind.Movie, kid1s);
        AddUser(_kid1, false, _libA, kid1s);
        AddUser(_kid2, false, _libA, kid1s);

        Sut().EnsureIsolation();

        Assert.Equal(new[] { _libA, kid1s }, Enabled(_kid1));
        Assert.Equal(new[] { _libA }, Enabled(_kid2));
        var call = Assert.Single(_gw.LibraryAccessCalls);
        Assert.Equal(_kid2, call.UserId);
        Assert.False(call.AllLibraries);

        Sut().EnsureIsolation();
        Assert.Single(_gw.LibraryAccessCalls);
    }

    // ---------- EnsureShareLibrary ----------

    [Fact]
    public void EnsureShareLibrary_twice_returns_same_id_and_creates_once_only_recipient_gains()
    {
        AddUser(_kid1, true);
        AddUser(_kid2, true);
        var sut = Sut();

        var id1 = sut.EnsureShareLibrary(_kid1, ShareKind.Movie, "/share/kid1/movies");
        var id2 = sut.EnsureShareLibrary(_kid1, ShareKind.Movie, "/share/kid1/movies");

        Assert.Equal(id1, id2);
        var created = Assert.Single(_gw.CreatedLibraries);
        Assert.Equal(("Shared with " + _gw.Users[_kid1].Name, ShareKind.Movie, "/share/kid1/movies", id1), created);
        Assert.Equal(1, Count(nameof(IServerGateway.CreateLibrary)));
        Assert.Equal(id1, _store.Libraries()[_kid1]["Movie"]);
        Assert.Equal(new[] { _libA, _libB, id1 }, Enabled(_kid1));
        Assert.Equal(new[] { _libA, _libB }, Enabled(_kid2));
        Assert.False(_gw.Users[_kid1].AllLibraries);
        Assert.False(_gw.Users[_kid2].AllLibraries);
    }

    [Fact]
    public void EnsureShareLibrary_series_uses_shows_name_and_separate_library()
    {
        AddUser(_kid1, false, _libA);
        var sut = Sut();

        var movie = sut.EnsureShareLibrary(_kid1, ShareKind.Movie, "/m");
        var series = sut.EnsureShareLibrary(_kid1, ShareKind.Series, "/s");

        Assert.NotEqual(movie, series);
        Assert.Equal("Shared with " + _gw.Users[_kid1].Name, _gw.CreatedLibraries[0].Name);
        Assert.Equal("Shared with " + _gw.Users[_kid1].Name + " (shows)", _gw.CreatedLibraries[1].Name);
        Assert.Equal(ShareKind.Series, _gw.CreatedLibraries[1].Kind);
        Assert.Equal(new[] { _libA, movie, series }, Enabled(_kid1));
    }

    [Fact]
    public void EnsureShareLibrary_runs_isolation_first()
    {
        AddUser(_kid1, true);

        Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/m");

        var access = _gw.LibraryAccessCalls;
        Assert.Equal(2, access.Count);
        Assert.Equal(new[] { _libA, _libB }, access[0].Enabled);
        Assert.Equal(3, access[1].Enabled.Count);
        var firstSet = _gw.Calls.FindIndex(c => c.Method == nameof(IServerGateway.SetLibraryAccess));
        var create = _gw.Calls.FindIndex(c => c.Method == nameof(IServerGateway.CreateLibrary));
        Assert.True(firstSet < create);
    }

    [Fact]
    public void EnsureShareLibrary_crash_after_create_does_not_create_a_second_library()
    {
        AddUser(_kid1, false, _libA);
        _gw.ThrowOnce = nameof(IServerGateway.SetLibraryAccess);

        Assert.Throws<InvalidOperationException>(() => Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/m"));

        var created = Assert.Single(_gw.CreatedLibraries);
        Assert.Equal(created.Id, _gw.FindLibraryByPath("/m"));
        Assert.Equal(new[] { _libA }, Enabled(_kid1));
        Assert.Empty(_store.Libraries());

        var id = Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/m");

        Assert.Equal(created.Id, id);
        Assert.Single(_gw.CreatedLibraries);
        Assert.Equal(new[] { _libA, id }, Enabled(_kid1));
        var calls = _gw.LibraryAccessCalls.Count;

        Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/m");
        Assert.Equal(calls, _gw.LibraryAccessCalls.Count);
    }

    [Fact]
    public void EnsureShareLibrary_unknown_recipient_throws_without_creating()
    {
        Assert.Throws<InvalidOperationException>(() => Sut().EnsureShareLibrary(Guid.NewGuid(), ShareKind.Movie, "/m"));

        Assert.Empty(_gw.CreatedLibraries);
        Assert.Empty(_store.Libraries());
    }

    // ---------- DropShareLibraryIfEmpty ----------

    [Fact]
    public void Drop_with_active_share_does_nothing()
    {
        AddUser(_kid1, false, _libA);
        var id = Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/m");
        AddRecord(_kid1, ShareKind.Movie, ShareState.Active);
        AddRecord(_kid1, ShareKind.Movie, ShareState.Closed);
        var calls = _gw.LibraryAccessCalls.Count;

        Sut().DropShareLibraryIfEmpty(_kid1, ShareKind.Movie);

        Assert.Empty(_gw.RemovedLibraries);
        Assert.Equal(calls, _gw.LibraryAccessCalls.Count);
        Assert.Equal(id, _store.Libraries()[_kid1]["Movie"]);
        Assert.Contains(id, Enabled(_kid1));
    }

    [Fact]
    public void Drop_keeps_the_library_while_a_broken_record_still_has_its_folder()
    {
        AddUser(_kid1, false, _libA);
        var id = Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/m");
        AddRecord(_kid1, ShareKind.Movie, ShareState.Broken); // e.g. "could not remove the shared copy"

        Sut().DropShareLibraryIfEmpty(_kid1, ShareKind.Movie);

        Assert.Empty(_gw.RemovedLibraries);
        Assert.Equal(id, _store.Libraries()[_kid1]["Movie"]);
        Assert.Contains(id, Enabled(_kid1));
    }

    [Fact]
    public void Drop_with_only_closed_or_broken_or_other_kind_removes_library_and_is_idempotent()
    {
        AddUser(_kid1, false, _libA);
        var sut = Sut();
        var movie = sut.EnsureShareLibrary(_kid1, ShareKind.Movie, "/m");
        var series = sut.EnsureShareLibrary(_kid1, ShareKind.Series, "/s");
        AddRecord(_kid1, ShareKind.Movie, ShareState.Closed);
        AddRecord(_kid1, ShareKind.Movie, ShareState.Broken, folder: string.Empty); // its copy is gone
        AddRecord(_kid1, ShareKind.Series, ShareState.Active);

        sut.DropShareLibraryIfEmpty(_kid1, ShareKind.Movie);

        Assert.Equal(new[] { movie }, _gw.RemovedLibraries);
        Assert.Equal(new[] { _libA, series }, Enabled(_kid1));
        var kinds = _store.Libraries()[_kid1];
        Assert.False(kinds.ContainsKey("Movie"));
        Assert.Equal(series, kinds["Series"]);

        var access = _gw.LibraryAccessCalls.Count;
        sut.DropShareLibraryIfEmpty(_kid1, ShareKind.Movie);
        Assert.Single(_gw.RemovedLibraries);
        Assert.Equal(access, _gw.LibraryAccessCalls.Count);
    }

    [Fact]
    public void Drop_without_library_does_nothing()
    {
        AddUser(_kid1, false, _libA);

        Sut().DropShareLibraryIfEmpty(_kid1, ShareKind.Movie);

        Assert.Empty(_gw.RemovedLibraries);
        Assert.Empty(_gw.LibraryAccessCalls);
    }

    [Fact]
    public void Drop_crash_on_remove_library_then_rerun_finishes()
    {
        AddUser(_kid1, false, _libA);
        var id = Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/m");
        _gw.ThrowOnce = nameof(IServerGateway.RemoveLibrary);

        Assert.Throws<InvalidOperationException>(() => Sut().DropShareLibraryIfEmpty(_kid1, ShareKind.Movie));
        Assert.Equal(id, _store.Libraries()[_kid1]["Movie"]);

        Sut().DropShareLibraryIfEmpty(_kid1, ShareKind.Movie);

        Assert.Empty(_store.Libraries());
        Assert.Equal(new[] { id }, _gw.RemovedLibraries);
        Assert.Equal(new[] { _libA }, Enabled(_kid1));
    }

    [Fact]
    public void Drop_when_recipient_no_longer_exists_still_removes_library()
    {
        AddUser(_kid1, false, _libA);
        var id = Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/m");
        _gw.Users.Remove(_kid1);

        Sut().DropShareLibraryIfEmpty(_kid1, ShareKind.Movie);

        Assert.Equal(new[] { id }, _gw.RemovedLibraries);
        Assert.Empty(_store.Libraries());
    }

    // ---------- OnRealLibraryAdded ----------

    [Fact]
    public void RealLibraryAdded_reaches_snapshot_users_only_without_duplicates()
    {
        AddUser(_kid1, true);
        AddUser(_kid2, false, _libA);
        Sut().EnsureIsolation();
        var added = Guid.NewGuid();
        _gw.RealLibraryIds.Add(added);
        var sut = Sut();

        sut.OnRealLibraryAdded(added);

        Assert.Equal(new[] { _libA, _libB, added }, Enabled(_kid1));
        Assert.Equal(new[] { _libA }, Enabled(_kid2));
        var calls = _gw.LibraryAccessCalls.Count;

        sut.OnRealLibraryAdded(added);

        Assert.Equal(calls, _gw.LibraryAccessCalls.Count);
        Assert.Single(Enabled(_kid1), added);
    }

    [Fact]
    public void RealLibraryAdded_ignores_share_libraries()
    {
        AddUser(_kid1, true);
        var id = Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/m");
        var calls = _gw.LibraryAccessCalls.Count;

        Sut().OnRealLibraryAdded(id);

        Assert.Equal(calls, _gw.LibraryAccessCalls.Count);
        Assert.Single(Enabled(_kid1), id);
    }

    // ---------- RestoreAll ----------

    [Fact]
    public void RestoreAll_removes_share_libraries_then_restores_snapshot_users_and_spares_explicit_user()
    {
        AddUser(_kid1, true);
        AddUser(_kid2, true);
        AddUser(_adult, false, _libA);
        var sut = Sut();
        var o = sut.EnsureShareLibrary(_kid1, ShareKind.Movie, "/o");
        var a = sut.EnsureShareLibrary(_kid2, ShareKind.Series, "/a");
        _gw.LibraryAccessCalls.Clear();
        _gw.Calls.Clear();

        sut.RestoreAll();

        Assert.True(_gw.Users[_kid1].AllLibraries);
        Assert.True(_gw.Users[_kid2].AllLibraries);
        Assert.False(_gw.Users[_adult].AllLibraries);
        Assert.Equal(new[] { _libA }, Enabled(_adult));
        Assert.Equal(new[] { o, a }.OrderBy(x => x), _gw.RemovedLibraries.OrderBy(x => x));
        Assert.Empty(_store.Libraries());
        Assert.Empty(_store.Snapshots());
        Assert.DoesNotContain(_gw.LibraryAccessCalls, c => c.UserId == _adult);
        Assert.Contains(_gw.LibraryAccessCalls, c => c.UserId == _kid1 && c.AllLibraries && c.Enabled.Count == 0);

        var lastRemove = _gw.Calls.FindLastIndex(c => c.Method == nameof(IServerGateway.RemoveLibrary));
        var restores = _gw.Calls.Select((c, i) => (c, i)).Where(x => x.c.Method == nameof(IServerGateway.SetLibraryAccess) && (bool)x.c.Args[1]!).Select(x => x.i);
        Assert.All(restores, i => Assert.True(i > lastRemove));
    }

    [Fact]
    public void RestoreAll_second_call_changes_nothing()
    {
        AddUser(_kid1, true);
        var sut = Sut();
        sut.EnsureShareLibrary(_kid1, ShareKind.Movie, "/o");
        sut.RestoreAll();
        _gw.Calls.Clear();

        sut.RestoreAll();

        Assert.DoesNotContain(_gw.Calls, c => c.Method is nameof(IServerGateway.SetLibraryAccess) or nameof(IServerGateway.RemoveLibrary) or nameof(IServerGateway.CreateLibrary));
        Assert.True(_gw.Users[_kid1].AllLibraries);
    }

    [Fact]
    public void RestoreAll_crash_on_remove_library_keeps_everything_isolated_and_rerun_finishes()
    {
        AddUser(_kid1, true);
        AddUser(_kid2, true);
        var sut = Sut();
        var o = sut.EnsureShareLibrary(_kid1, ShareKind.Movie, "/o");
        _gw.ThrowOnce = nameof(IServerGateway.RemoveLibrary);

        Assert.Throws<InvalidOperationException>(() => Sut().RestoreAll());

        Assert.False(_gw.Users[_kid1].AllLibraries);
        Assert.False(_gw.Users[_kid2].AllLibraries);
        Assert.Equal(2, _store.Snapshots().Count);
        Assert.Equal(o, _store.Libraries()[_kid1]["Movie"]);

        Sut().RestoreAll();

        Assert.True(_gw.Users[_kid1].AllLibraries);
        Assert.True(_gw.Users[_kid2].AllLibraries);
        Assert.Empty(_store.Snapshots());
        Assert.Empty(_store.Libraries());
        Assert.Contains(o, _gw.RemovedLibraries);
    }

    [Fact]
    public void RestoreAll_crash_on_access_restore_keeps_snapshot_and_rerun_finishes()
    {
        AddUser(_kid1, true);
        Sut().EnsureIsolation();
        _gw.ThrowOnce = nameof(IServerGateway.SetLibraryAccess);

        Assert.Throws<InvalidOperationException>(() => Sut().RestoreAll());
        Assert.Single(_store.Snapshots());
        Assert.False(_gw.Users[_kid1].AllLibraries);

        Sut().RestoreAll();

        Assert.True(_gw.Users[_kid1].AllLibraries);
        Assert.Empty(_store.Snapshots());
    }

    [Fact]
    public void RestoreAll_drops_snapshot_of_deleted_user_without_touching_gateway_access()
    {
        AddUser(_kid1, true);
        Sut().EnsureIsolation();
        _gw.Users.Remove(_kid1);
        _gw.LibraryAccessCalls.Clear();

        Sut().RestoreAll();

        Assert.Empty(_store.Snapshots());
        Assert.Empty(_gw.LibraryAccessCalls);
    }

    // ---------- I1: an administrator's later narrowing is never overwritten ----------

    [Fact]
    public void I1_isolation_remembers_the_real_libraries_it_gave_each_user()
    {
        AddUser(_kid1, true);

        Sut().EnsureIsolation();

        Assert.Equal(new[] { _libA, _libB }.OrderBy(x => x), _store.Snapshots().Single().GivenLibraries!.OrderBy(x => x));
    }

    [Fact]
    public void I1_restore_leaves_a_user_whose_list_was_narrowed_after_isolation_on_that_list_and_names_them()
    {
        AddUser(_kid1, true);
        AddUser(_kid2, true);
        var sut = Sut();
        sut.EnsureIsolation();
        _gw.Users[_kid1] = _gw.Users[_kid1] with { EnabledLibraries = new List<Guid> { _libA } }; // a parent unticks B
        _gw.LibraryAccessCalls.Clear();

        var result = sut.RestoreAll();

        Assert.False(_gw.Users[_kid1].AllLibraries);
        Assert.Equal(new[] { _libA }, Enabled(_kid1));
        Assert.DoesNotContain(_gw.LibraryAccessCalls, c => c.UserId == _kid1);
        Assert.True(_gw.Users[_kid2].AllLibraries);
        Assert.Empty(_store.Snapshots());
        Assert.Equal(new[] { _kid1 }, result.LeftOnOwnList.Select(u => u.Id));
        Assert.Equal(new[] { _kid2 }, result.Restored.Select(u => u.Id));
    }

    [Fact]
    public void I1_a_user_given_an_extra_library_by_hand_is_still_restored()
    {
        AddUser(_kid1, true);
        var sut = Sut();
        sut.EnsureIsolation();
        var extra = Guid.NewGuid();
        _gw.Users[_kid1] = _gw.Users[_kid1] with { EnabledLibraries = Enabled(_kid1).Append(extra).ToList() };

        var result = sut.RestoreAll();

        Assert.True(_gw.Users[_kid1].AllLibraries);
        Assert.Empty(result.LeftOnOwnList);
    }

    [Fact]
    public void I1_a_snapshot_written_before_the_plugin_remembered_its_libraries_is_compared_with_every_real_library()
    {
        AddUser(_kid1, false, _libA);
        AddUser(_kid2, false, _libA, _libB);
        _store.UpsertSnapshot(new PolicySnapshot(_kid1, true, DateTime.UtcNow));
        _store.UpsertSnapshot(new PolicySnapshot(_kid2, true, DateTime.UtcNow));

        var result = Sut().RestoreAll();

        Assert.False(_gw.Users[_kid1].AllLibraries);
        Assert.True(_gw.Users[_kid2].AllLibraries);
        Assert.Equal(new[] { _kid1 }, result.LeftOnOwnList.Select(u => u.Id));
    }

    [Fact]
    public void I1_a_new_library_never_reaches_a_user_whose_list_was_narrowed_and_is_remembered_for_the_others()
    {
        AddUser(_kid1, true);
        AddUser(_kid2, true);
        var sut = Sut();
        sut.EnsureIsolation();
        _gw.Users[_kid1] = _gw.Users[_kid1] with { EnabledLibraries = new List<Guid> { _libA } };
        var added = Guid.NewGuid();
        _gw.RealLibraryIds.Add(added);

        sut.OnRealLibraryAdded(added);

        Assert.Equal(new[] { _libA }, Enabled(_kid1));
        Assert.Equal(new[] { _libA, _libB, added }, Enabled(_kid2));
        Assert.Contains(added, _store.Snapshots().Single(x => x.UserId == _kid2).GivenLibraries!);
        var result = sut.RestoreAll();
        Assert.True(_gw.Users[_kid2].AllLibraries); // what the plugin gave it, the new library included, is all still there
        Assert.Equal(new[] { _kid1 }, result.LeftOnOwnList.Select(u => u.Id));
    }

    // ---------- Fix round 1 ----------

    [Fact]
    public void EnsureShareLibrary_adopts_library_already_at_path_with_no_store_entry()
    {
        AddUser(_kid1, false, _libA);
        AddUser(_kid2, false, _libA);
        var orphan = _gw.SeedLibrary("/m");

        var id = Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/m");

        Assert.Equal(orphan, id);
        Assert.Equal(0, Count(nameof(IServerGateway.CreateLibrary)));
        Assert.Equal(orphan, _store.Libraries()[_kid1]["Movie"]);
        Assert.Equal(new[] { _libA, orphan }, Enabled(_kid1));
        Assert.Equal(new[] { _libA }, Enabled(_kid2));
    }

    [Fact]
    public void EnsureShareLibrary_replaces_stale_stored_id_whose_library_is_gone()
    {
        AddUser(_kid1, false, _libA);
        var stale = Guid.NewGuid();
        _store.SetLibrary(_kid1, ShareKind.Movie, stale);
        AddUser(_kid1, false, _libA, stale);

        var id = Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/m");

        Assert.NotEqual(stale, id);
        Assert.Single(_gw.CreatedLibraries);
        Assert.Equal(id, _store.Libraries()[_kid1]["Movie"]);
        Assert.Equal(new[] { _libA, id }, Enabled(_kid1));
        Assert.Equal(new[] { stale }, _gw.Calls.Where(c => c.Method == nameof(IServerGateway.RemoveLibrary)).Select(c => (Guid)c.Args[0]!));
    }

    [Fact]
    public void EnsureShareLibrary_rejects_unsupported_kind_before_touching_gateway()
    {
        AddUser(_kid1, true);

        Assert.Throws<ArgumentOutOfRangeException>(() => Sut().EnsureShareLibrary(_kid1, (ShareKind)99, "/x"));

        Assert.Empty(_gw.Calls);
    }

    [Fact]
    public void EnsureShareLibrary_throws_when_recipient_stays_on_all_libraries()
    {
        AddUser(_kid1, true);
        _gw.IgnoreAllLibrariesChange = true;

        Assert.Throws<InvalidOperationException>(() => Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/m"));

        Assert.Equal(0, Count(nameof(IServerGateway.CreateLibrary)));
    }

    [Fact]
    public void RealLibraryAdded_ignores_id_that_is_not_a_real_library()
    {
        AddUser(_kid1, true);
        Sut().EnsureIsolation();
        var orphan = _gw.SeedLibrary("/orphan");
        var calls = _gw.LibraryAccessCalls.Count;

        Sut().OnRealLibraryAdded(orphan);

        Assert.Equal(calls, _gw.LibraryAccessCalls.Count);
        Assert.DoesNotContain(orphan, Enabled(_kid1));
    }

    [Fact]
    public void RealLibraryAdded_re_isolates_snapshot_user_back_on_all_libraries()
    {
        AddUser(_kid1, true);
        Sut().EnsureIsolation();
        var added = Guid.NewGuid();
        _gw.RealLibraryIds.Add(added);
        AddUser(_kid1, true);

        Sut().OnRealLibraryAdded(added);

        Assert.False(_gw.Users[_kid1].AllLibraries);
        Assert.Equal(new[] { _libA, _libB, added }, Enabled(_kid1));
    }

    [Fact]
    public void RestoreAll_with_store_id_the_gateway_does_not_know_still_completes()
    {
        AddUser(_kid1, true);
        AddUser(_kid2, true);
        Sut().EnsureIsolation();
        _store.SetLibrary(_kid1, ShareKind.Movie, Guid.NewGuid());

        Sut().RestoreAll();

        Assert.Empty(_store.Libraries());
        Assert.Empty(_store.Snapshots());
        Assert.True(_gw.Users[_kid1].AllLibraries);
        Assert.True(_gw.Users[_kid2].AllLibraries);
    }

    [Fact]
    public void Drop_with_store_id_the_gateway_does_not_know_clears_entry()
    {
        AddUser(_kid1, false, _libA);
        _store.SetLibrary(_kid1, ShareKind.Movie, Guid.NewGuid());

        Sut().DropShareLibraryIfEmpty(_kid1, ShareKind.Movie);

        Assert.Empty(_store.Libraries());
        Assert.Equal(1, Count(nameof(IServerGateway.RemoveLibrary)));
    }

    [Fact]
    public void RestoreAll_unparseable_kind_key_does_not_block_restore()
    {
        AddUser(_kid1, true);
        Sut().EnsureIsolation();
        var lib = _gw.SeedLibrary("/weird");
        var json = "{\"Shares\":[],\"Snapshots\":" + System.Text.Json.JsonSerializer.Serialize(_store.Snapshots())
            + ",\"Libraries\":{\"" + _kid1 + "\":{\"Bogus\":\"" + lib + "\"}}}";
        File.WriteAllText(Path.Combine(_dir, "shares.json"), json);

        Sut().RestoreAll();

        Assert.Equal(new[] { lib }, _gw.RemovedLibraries);
        Assert.Empty(_store.Libraries());
        Assert.Empty(_store.Snapshots());
        Assert.True(_gw.Users[_kid1].AllLibraries);
    }

    [Fact]
    public void EnsureShareLibrary_refuses_path_used_by_another_recipients_library()
    {
        AddUser(_kid1, false, _libA);
        AddUser(_kid2, false, _libA);
        var kid1s = Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/shared");
        var storeBefore = _store.Libraries();
        var creates = Count(nameof(IServerGateway.CreateLibrary));

        var ex = Assert.Throws<InvalidOperationException>(() => Sut().EnsureShareLibrary(_kid2, ShareKind.Movie, "/shared"));

        Assert.Contains("already in use", ex.Message);
        Assert.Equal(creates, Count(nameof(IServerGateway.CreateLibrary)));
        Assert.Equal(new[] { _libA }, Enabled(_kid2));
        Assert.Equal(storeBefore.Count, _store.Libraries().Count);
        Assert.False(_store.Libraries().ContainsKey(_kid2));
        Assert.Equal(kid1s, _store.Libraries()[_kid1]["Movie"]);
    }

    [Fact]
    public void EnsureShareLibrary_refuses_path_used_by_same_recipients_other_kind()
    {
        AddUser(_kid1, false, _libA);
        var movie = Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/shared");
        var creates = Count(nameof(IServerGateway.CreateLibrary));

        Assert.Throws<InvalidOperationException>(() => Sut().EnsureShareLibrary(_kid1, ShareKind.Series, "/shared"));

        Assert.Equal(creates, Count(nameof(IServerGateway.CreateLibrary)));
        Assert.Equal(new[] { _libA, movie }, Enabled(_kid1));
        var kinds = _store.Libraries()[_kid1];
        Assert.Single(kinds);
        Assert.Equal(movie, kinds["Movie"]);
    }

    [Fact]
    public void EnsureShareLibrary_refuses_path_used_by_a_real_library()
    {
        AddUser(_kid1, false, _libA);
        var real = _gw.SeedLibrary("/real");
        _gw.RealLibraryIds.Add(real);

        var ex = Assert.Throws<InvalidOperationException>(() => Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/real"));

        Assert.Contains("normal library", ex.Message);
        Assert.Equal(0, Count(nameof(IServerGateway.CreateLibrary)));
        Assert.Equal(new[] { _libA }, Enabled(_kid1));
        Assert.Empty(_store.Libraries());
    }

    [Fact]
    public void EnsureShareLibrary_rerun_with_own_stored_library_changes_nothing()
    {
        AddUser(_kid1, false, _libA);
        var id = Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/m");
        var storeBefore = _store.Libraries();
        var creates = Count(nameof(IServerGateway.CreateLibrary));
        var removes = Count(nameof(IServerGateway.RemoveLibrary));
        var access = _gw.LibraryAccessCalls.Count;

        Assert.Equal(id, Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/m"));

        Assert.Equal(creates, Count(nameof(IServerGateway.CreateLibrary)));
        Assert.Equal(removes, Count(nameof(IServerGateway.RemoveLibrary)));
        Assert.Equal(access, _gw.LibraryAccessCalls.Count);
        Assert.Equal(storeBefore[_kid1], _store.Libraries()[_kid1]);
        Assert.Single(Enabled(_kid1), id);
    }

    [Fact]
    public void EnsureShareLibrary_crash_on_remove_of_stale_library_then_rerun_leaves_only_new_id()
    {
        // The old library still exists at a folder that is no longer the share root.
        var stale = _gw.SeedLibrary("/old/m");
        _store.SetLibrary(_kid1, ShareKind.Movie, stale);
        AddUser(_kid1, false, _libA, stale);
        _gw.ThrowOnce = nameof(IServerGateway.RemoveLibrary);

        Assert.Throws<InvalidOperationException>(() => Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/m"));

        var id = Assert.Single(_gw.CreatedLibraries).Id;
        Assert.Equal(new[] { _libA, id }, Enabled(_kid1));
        Assert.Equal(stale, _store.Libraries()[_kid1]["Movie"]);
        Assert.Empty(_gw.RemovedLibraries);

        Assert.Equal(id, Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/m"));

        Assert.Single(_gw.CreatedLibraries);
        Assert.Equal(new[] { stale }, _gw.RemovedLibraries);
        Assert.Null(_gw.FindLibraryByPath("/old/m"));
        Assert.Equal(new[] { _libA, id }, Enabled(_kid1));
        Assert.Equal(id, _store.Libraries()[_kid1]["Movie"]);
    }

    [Fact]
    public void EnsureShareLibrary_death_after_stale_removed_before_store_write_records_new_id()
    {
        // State left by a crash after the access write and the removal, before the store write.
        var stale = Guid.NewGuid();
        var created = _gw.SeedLibrary("/m");
        _store.SetLibrary(_kid1, ShareKind.Movie, stale);
        AddUser(_kid1, false, _libA, created);

        Assert.Equal(created, Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/m"));

        Assert.Equal(0, Count(nameof(IServerGateway.CreateLibrary)));
        Assert.Equal(created, _store.Libraries()[_kid1]["Movie"]);
        Assert.Equal(new[] { _libA, created }, Enabled(_kid1));
        Assert.Equal(new[] { stale }, _gw.Calls.Where(c => c.Method == nameof(IServerGateway.RemoveLibrary)).Select(c => (Guid)c.Args[0]!));
    }

    // ---------- per-recipient blocked tags ----------

    private string Tag(Guid id) => LibraryAccessManager.RecipientTag(id);

    private IReadOnlyList<string> Blocked(Guid id) => _gw.Users[id].BlockedTags;

    private void Block(Guid id, params string[] tags) => _gw.Users[id] = _gw.Users[id] with { BlockedTags = tags };

    [Fact]
    public void Isolation_blocks_every_recipient_tag_for_every_other_user_and_never_the_own_tag()
    {
        AddUser(_kid1, false, _libA);
        AddUser(_kid2, false, _libA);
        AddUser(_adult, false, true, true, _libA); // admin and disabled are blocked too
        _store.SetLibrary(_kid1, ShareKind.Movie, Guid.NewGuid());
        Block(_kid1, "horror", Tag(_kid1));
        Block(_kid2, "horror");

        Sut().EnsureIsolation();

        Assert.Equal(new[] { "horror" }, Blocked(_kid1));
        Assert.Equal(new[] { "horror", Tag(_kid1) }, Blocked(_kid2));
        Assert.Equal(new[] { Tag(_kid1) }, Blocked(_adult));

        var writes = _gw.BlockedTagCalls.Count;
        Sut().EnsureIsolation();
        Assert.Equal(writes, _gw.BlockedTagCalls.Count);
    }

    [Fact]
    public void EnsureShareLibrary_blocks_others_before_creating_and_leaves_recipient_unblocked()
    {
        AddUser(_kid1, true);
        AddUser(_kid2, true);
        AddUser(_adult, false, true, false, _libA);
        Block(_kid2, "horror");

        Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/m");

        Assert.Equal(new[] { "horror", Tag(_kid1) }, Blocked(_kid2));
        Assert.Equal(new[] { Tag(_kid1) }, Blocked(_adult));
        Assert.Empty(Blocked(_kid1));
        var methods = _gw.Calls.Select(c => c.Method).ToList();
        Assert.True(methods.LastIndexOf(nameof(IServerGateway.SetBlockedTags)) < methods.IndexOf(nameof(IServerGateway.CreateLibrary)));
    }

    [Fact]
    public void EnsureShareLibrary_when_blocks_cannot_be_placed_creates_nothing_and_rerun_finishes()
    {
        AddUser(_kid1, false, _libA);
        AddUser(_kid2, false, _libA);
        AddUser(_adult, false, _libA);
        _gw.ThrowOnce = nameof(IServerGateway.SetBlockedTags);

        Assert.Throws<InvalidOperationException>(() => Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/m"));

        Assert.Empty(_gw.CreatedLibraries);
        Assert.Empty(_store.Libraries());
        Assert.Equal(new[] { _libA }, Enabled(_kid1));

        var id = Sut().EnsureShareLibrary(_kid1, ShareKind.Movie, "/m");

        Assert.Single(_gw.CreatedLibraries);
        Assert.Equal(new[] { Tag(_kid1) }, Blocked(_kid2));
        Assert.Equal(new[] { Tag(_kid1) }, Blocked(_adult));
        Assert.Equal(new[] { _libA, id }, Enabled(_kid1));
    }

    [Fact]
    public void Drop_of_last_library_unblocks_everyone_and_keeps_other_tags()
    {
        AddUser(_kid1, false, _libA);
        AddUser(_kid2, false, _libA);
        Block(_kid2, "horror");
        var sut = Sut();
        sut.EnsureShareLibrary(_kid1, ShareKind.Movie, "/m");
        sut.EnsureShareLibrary(_kid1, ShareKind.Series, "/s");
        sut.EnsureShareLibrary(_kid2, ShareKind.Movie, "/am");

        sut.DropShareLibraryIfEmpty(_kid1, ShareKind.Movie);
        Assert.Equal(new[] { "horror", Tag(_kid1) }, Blocked(_kid2)); // Kid1 still has his shows library

        sut.DropShareLibraryIfEmpty(_kid1, ShareKind.Series);
        Assert.Equal(new[] { "horror" }, Blocked(_kid2));
        Assert.Equal(new[] { Tag(_kid2) }, Blocked(_kid1)); // Kid2's block stays

        var writes = _gw.BlockedTagCalls.Count;
        sut.DropShareLibraryIfEmpty(_kid1, ShareKind.Series);
        Assert.Equal(writes, _gw.BlockedTagCalls.Count);
    }

    [Fact]
    public void RestoreAll_removes_every_plugin_tag_and_keeps_other_tags()
    {
        AddUser(_kid1, true);
        AddUser(_kid2, true);
        Block(_kid2, "horror", "swm-0000000000000000000000000000dead", "swm-other", "swm-0000000000000000000000000000deadX");
        var sut = Sut();
        sut.EnsureShareLibrary(_kid1, ShareKind.Movie, "/m");

        sut.RestoreAll();

        Assert.Equal(new[] { "horror", "swm-other", "swm-0000000000000000000000000000deadX" }, Blocked(_kid2)); // only swm- + 32 hex is ours
        Assert.Empty(Blocked(_kid1));
        var methods = _gw.Calls.Select(c => c.Method).ToList();
        var lastTag = methods.LastIndexOf(nameof(IServerGateway.SetBlockedTags));
        Assert.True(methods.LastIndexOf(nameof(IServerGateway.RemoveLibrary)) < lastTag);
        Assert.True(lastTag < methods.LastIndexOf(nameof(IServerGateway.SetLibraryAccess)));
        Assert.True(_gw.Users[_kid2].AllLibraries);

        var writes = _gw.BlockedTagCalls.Count;
        sut.RestoreAll();
        Assert.Equal(writes, _gw.BlockedTagCalls.Count);
    }

    [Theory]
    [InlineData("swm-0123456789abcdef0123456789ABCDEF", true)]
    [InlineData("SWM-0123456789abcdef0123456789abcdef", true)]
    [InlineData("swm-0123456789abcdef0123456789abcde", false)]
    [InlineData("swm-0123456789abcdef0123456789abcdef0", false)]
    [InlineData("swm-0123456789abcdef0123456789abcdeg", false)]
    [InlineData("swm-other", false)]
    [InlineData("horror", false)]
    public void IsPluginTag_is_exactly_prefix_and_32_hex(string tag, bool expected)
    {
        Assert.Equal(expected, LibraryAccessManager.IsPluginTag(tag));
    }

    [Fact]
    public void UnblockIfNoPluginItems_keeps_blocks_while_a_library_remains()
    {
        AddUser(_kid1, false, _libA);
        AddUser(_kid2, false, _libA);
        var sut = Sut();
        sut.EnsureShareLibrary(_kid1, ShareKind.Movie, "/m");

        sut.UnblockIfNoPluginItems(_kid1);
        Assert.Equal(new[] { Tag(_kid1) }, Blocked(_kid2));

        _store.ClearLibraryKey(_kid1, nameof(ShareKind.Movie));
        sut.UnblockIfNoPluginItems(_kid1);
        Assert.Empty(Blocked(_kid2));
    }

    // ---------- one tag rule: recipients with a library and owners of plugin playlists ----------

    [Fact]
    public void A_playlist_owners_tag_stays_blocked_after_their_last_share_library_goes()
    {
        AddUser(_kid1, false, _libA);
        AddUser(_kid2, false, _libA);
        var sut = Sut();
        sut.EnsureShareLibrary(_kid1, ShareKind.Movie, "/m");
        _store.SetSharePlaylist(_kid1, _kid2, Guid.NewGuid());

        _store.ClearLibraryKey(_kid1, nameof(ShareKind.Movie));
        sut.UnblockIfNoPluginItems(_kid1);
        Assert.Equal(new[] { Tag(_kid1) }, Blocked(_kid2));

        _store.SetSharePlaylist(_kid1, _kid2, null);
        sut.UnblockIfNoPluginItems(_kid1);
        Assert.Empty(Blocked(_kid2));
    }

    [Fact]
    public void Isolation_blocks_the_tags_of_playlist_owners_too_and_nobody_blocks_their_own()
    {
        AddUser(_kid1, false, _libA, _libB);
        AddUser(_kid2, false, _libA, _libB);
        AddUser(_adult, false, _libA, _libB);
        _store.SetLibrary(_kid1, ShareKind.Movie, Guid.NewGuid());
        _store.SetSharePlaylist(_adult, _kid1, Guid.NewGuid());

        Sut().EnsureIsolation();

        Assert.Equal(new[] { Tag(_kid1), Tag(_adult) }.OrderBy(t => t), Blocked(_kid2).OrderBy(t => t));
        Assert.Equal(new[] { Tag(_kid1) }, Blocked(_adult));
        Assert.Equal(new[] { Tag(_adult) }, Blocked(_kid1));
    }

    [Fact]
    public void The_tag_pass_alone_blocks_owner_tags_without_moving_anyone_off_all_libraries()
    {
        AddUser(_kid1, true);
        AddUser(_kid2, true);
        _store.SetSharePlaylist(_kid1, _kid2, Guid.NewGuid());

        Sut().EnsureTagBlocks();

        Assert.Equal(new[] { Tag(_kid1) }, Blocked(_kid2));
        Assert.Empty(Blocked(_kid1));
        Assert.True(_gw.Users[_kid2].AllLibraries);
        Assert.Empty(_store.Snapshots());
        Assert.Equal(0, Count(nameof(IServerGateway.SetLibraryAccess)));
    }
}
