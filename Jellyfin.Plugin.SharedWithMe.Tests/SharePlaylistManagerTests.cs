using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SharedWithMe.Configuration;
using Jellyfin.Plugin.SharedWithMe.Model;
using Jellyfin.Plugin.SharedWithMe.Sharing;
using Jellyfin.Plugin.SharedWithMe.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SharedWithMe.Tests;

public sealed class SharePlaylistManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "swm-pl-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServerGateway _gw = new();
    private readonly ShareStore _store;
    private readonly string _media;
    private readonly string _shareRoot;
    private readonly PluginGate _gate = new();
    private readonly DateTime _now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
    private bool _enabled = true;
    private string? _setupProblem;
    private WhoMayShare _who = WhoMayShare.Everyone;

    private readonly Guid _lib = Guid.NewGuid();
    private readonly Guid _adult = Guid.NewGuid();
    private readonly Guid _kid1 = Guid.NewGuid();
    private readonly Guid _kid2 = Guid.NewGuid();
    private readonly Guid _e18 = Guid.NewGuid();
    private readonly Guid _f15 = Guid.NewGuid();
    private readonly Guid _pg = Guid.NewGuid();
    private readonly Guid _show = Guid.NewGuid();
    private readonly Guid _ep1 = Guid.NewGuid();
    private readonly Guid _ep2 = Guid.NewGuid();

    public SharePlaylistManagerTests()
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
        AddUser(_adult, "adult", null);
        AddUser(_kid1, "kid1", 12, 1);
        AddUser(_kid2, "kid2", 12, 1);
        AddFilm(_e18, "Spike Eighteen", "GB-18");
        AddFilm(_f15, "Spike Fifteen", "GB-15");
        AddFilm(_pg, "Spike PG", "GB-PG");
        foreach (var kid in new[] { _kid1, _kid2 })
        {
            _gw.SetVisible(kid, _pg);
        }

        AddShow();
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---- set-up helpers ----

    private ShareEligibility Eligibility() => new(_gw, _store, () => _who);

    private ShareService Service() =>
        new(_gw, _store, Eligibility(), new LibraryAccessManager(_gw, _store), new ShareFolderBuilder(), () => _shareRoot, _gate, NullLogger.Instance);

    private SharePlaylistManager Sut() =>
        new(_gw, _store, Service(), Eligibility(), new LibraryAccessManager(_gw, _store), () => _enabled, () => _setupProblem, _gate, NullLogger.Instance, () => _now);

    private static string Tag(Guid id) => LibraryAccessManager.RecipientTag(id);

    private IEnumerable<string> PluginTags(Guid user) => _gw.Users[user].BlockedTags.Where(LibraryAccessManager.IsPluginTag).OrderBy(t => t);

    private void AddUser(Guid id, string name, int? cap, int? sub = null, bool admin = false, bool disabled = false) =>
        _gw.Users[id] = new UserInfo(id, name, admin, disabled, true, cap, sub, true, new List<Guid>(), Array.Empty<string>(), Array.Empty<string>());

    private void AddFilm(Guid id, string title, string rating)
    {
        var folder = Path.Combine(_media, title);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, title + ".mp4");
        File.WriteAllText(file, "video");
        _gw.Items[id] = new ItemInfo(id, ShareKind.Movie, title, 2002, rating, new Dictionary<string, string> { ["Tmdb"] = id.ToString("N")[..6] }, _lib, new List<string>(), file);
        _gw.Media[id] = new SourceMedia(file, Array.Empty<string>(), Array.Empty<(int, int, string)>());
        _gw.SetVisible(_adult, id);
    }

    // Spike Show (GB-15) with two episodes; adult sees the series and both episodes.
    private void AddShow()
    {
        var season = Path.Combine(_media, "Spike Show (2010)", "Season 01");
        Directory.CreateDirectory(season);
        var files = new[] { Path.Combine(season, "Spike Show S01E01.mp4"), Path.Combine(season, "Spike Show S01E02.mp4") };
        foreach (var f in files)
        {
            File.WriteAllText(f, "video");
        }

        _gw.Items[_show] = new ItemInfo(_show, ShareKind.Series, "Spike Show", 2010, "GB-15", new Dictionary<string, string> { ["Tvdb"] = "990010" }, _lib, new List<string>(), Path.Combine(_media, "Spike Show (2010)"));
        _gw.Media[_show] = new SourceMedia(string.Empty, Array.Empty<string>(), new List<(int, int, string)> { (1, 1, files[0]), (1, 2, files[1]) });
        _gw.Items[_ep1] = new ItemInfo(_ep1, null, "Spike Show One", 2010, null, new Dictionary<string, string>(), _lib, new List<string>(), files[0], _show);
        _gw.Items[_ep2] = new ItemInfo(_ep2, null, "Spike Show Two", 2010, null, new Dictionary<string, string>(), _lib, new List<string>(), files[1], _show);
        _gw.ItemsByPath[files[0]] = _ep1;
        _gw.ItemsByPath[files[1]] = _ep2;
        foreach (var id in new[] { _show, _ep1, _ep2 })
        {
            _gw.SetVisible(_adult, id);
        }
    }

    private Guid Playlist(Guid sharer, Guid recipient) => _store.SharePlaylists()[sharer][recipient];

    private List<Guid> Entries(Guid sharer, Guid recipient) => _gw.Playlists[Playlist(sharer, recipient)].Items;

    private void Add(Guid sharer, Guid recipient, Guid item) => _gw.Playlists[Playlist(sharer, recipient)].Items.Add(item);

    private List<ShareRecord> Active(Guid sharer, Guid recipient) =>
        _store.All().Where(r => r.SharerUserId == sharer && r.RecipientUserId == recipient && r.State == ShareState.Active).ToList();

    private ShareRecord WebShare(Guid sharer, Guid item, Guid recipient)
    {
        var (decision, record) = Service().Create(sharer, item, recipient);
        Assert.Equal(Refusal.None, decision.Refusal);
        return record!;
    }

    // Registers the scanned copy of a movie share as an item in the recipient's share library, visible to them.
    private Guid RegisterCopy(ShareRecord r)
    {
        var copy = Guid.NewGuid();
        var library = _store.Libraries()[r.RecipientUserId][nameof(ShareKind.Movie)];
        var path = Path.Combine(_shareRoot, r.RecipientUserId.ToString("N"), "movies", r.FolderName, r.FolderName + ".mp4");
        _gw.ItemsByPath[path] = copy;
        _gw.Items[copy] = _gw.Items[r.OriginalItemId] with { Id = copy, LibraryId = library, Path = path };
        _gw.SetVisible(r.RecipientUserId, copy);
        return copy;
    }

    private (string Store, int Writes, int Policy) Fingerprint() =>
        (File.Exists(Path.Combine(_dir, "store", "shares.json")) ? File.ReadAllText(Path.Combine(_dir, "store", "shares.json")) : string.Empty,
         _gw.PlaylistWrites.Count,
         _gw.LibraryAccessCalls.Count + _gw.BlockedTagCalls.Count);

    private static readonly Regex RandomName = new("^swm-[0-9a-f]{32}$");

    // ---- EnsurePlaylists ----

    [Fact]
    public void Each_sharer_gets_one_private_playlist_per_restricted_profile_and_none_toward_the_unrestricted()
    {
        var newbie = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var off = Guid.NewGuid();
        AddUser(newbie, "newbie", null);
        AddUser(admin, "admin", null, admin: true);
        AddUser(off, "kid3", 12, 1, disabled: true);

        Sut().EnsurePlaylists();

        var map = _store.SharePlaylists();
        Assert.Equal(new[] { _kid1, _kid2 }.OrderBy(g => g), map[_adult].Keys.OrderBy(g => g));
        Assert.Equal(new[] { _kid2 }, map[_kid1].Keys);
        Assert.Equal(new[] { _kid1 }, map[_kid2].Keys);
        Assert.Equal(2, map[newbie].Count);
        Assert.Equal(2, map[admin].Count);
        Assert.False(map.ContainsKey(off)); // disabled: no playlists of its own...
        Assert.DoesNotContain(map.Values, m => m.ContainsKey(off) || m.ContainsKey(newbie) || m.ContainsKey(admin) || m.ContainsKey(_adult)); // ...and none toward it
        Assert.Equal(8, _gw.Playlists.Count);
        foreach (var (sharer, recipients) in map)
        {
            foreach (var (recipient, id) in recipients)
            {
                var p = _gw.Playlists[id];
                Assert.Equal(sharer, p.Owner);
                Assert.Equal("Share with " + _gw.Users[recipient].Name, p.Name);
                Assert.False(p.IsPublic);
                Assert.False(p.SharedWithOthers);
            }
        }
    }

    [Fact]
    public void A_playlist_is_created_under_a_random_name_then_renamed_so_its_id_cannot_be_derived()
    {
        Sut().EnsurePlaylists();

        var creates = _gw.Calls.Where(c => c.Method == nameof(IServerGateway.CreatePlaylist)).Select(c => (string)c.Args[1]!).ToList();
        Assert.Equal(4, creates.Count);
        Assert.All(creates, n => Assert.Matches(RandomName, n));
        Assert.Equal(creates.Count, creates.Distinct().Count());
        foreach (var (method, id) in _gw.PlaylistWrites.Where(w => w.Method == nameof(IServerGateway.CreatePlaylist)))
        {
            // the owner's tag and then the rename follow its create at once
            var i = _gw.PlaylistWrites.IndexOf((method, id));
            Assert.Equal((nameof(IServerGateway.TagPlaylist), id), _gw.PlaylistWrites[i + 1]);
            Assert.Equal((nameof(IServerGateway.RenamePlaylist), id), _gw.PlaylistWrites[i + 2]);
        }

        // never under the display name, also when recreated
        Assert.DoesNotContain(_gw.Calls, c => c.Method == nameof(IServerGateway.CreatePlaylist) && ((string)c.Args[1]!).StartsWith("Share with", StringComparison.Ordinal));
        var first = Playlist(_adult, _kid1);
        _gw.Playlists.Remove(first);
        Sut().EnsurePlaylists();
        Assert.NotEqual(first, Playlist(_adult, _kid1));
        Assert.Equal("Share with kid1", _gw.Playlists[Playlist(_adult, _kid1)].Name);
    }

    [Fact]
    public void A_second_ensure_writes_nothing()
    {
        Sut().EnsurePlaylists();
        var before = Fingerprint();

        Sut().EnsurePlaylists();

        Assert.Equal(before, Fingerprint());
    }

    [Fact]
    public void Renaming_a_profile_renames_the_playlists_that_point_at_it()
    {
        Sut().EnsurePlaylists();
        var ids = (Playlist(_adult, _kid1), Playlist(_kid2, _kid1));
        _gw.Users[_kid1] = _gw.Users[_kid1] with { Name = "Renamed Kid" };

        Sut().EnsurePlaylists();

        Assert.Equal((ids.Item1, ids.Item2), (Playlist(_adult, _kid1), Playlist(_kid2, _kid1)));
        Assert.Equal("Share with Renamed Kid", _gw.Playlists[ids.Item1].Name);
        Assert.Equal("Share with Renamed Kid", _gw.Playlists[ids.Item2].Name);
        Assert.Equal("Share with kid2", _gw.Playlists[Playlist(_kid1, _kid2)].Name);
    }

    [Fact]
    public void Deleting_a_profile_removes_the_playlists_that_point_at_it_and_forgets_its_own()
    {
        Sut().EnsurePlaylists();
        var toKid1 = new[] { Playlist(_adult, _kid1), Playlist(_kid2, _kid1) };
        var own = Playlist(_kid1, _kid2);
        _gw.Users.Remove(_kid1);
        _gw.Playlists.Remove(own); // the server deletes a deleted user's playlists itself

        Sut().EnsurePlaylists();

        Assert.All(toKid1, id => Assert.False(_gw.Playlists.ContainsKey(id)));
        Assert.False(_store.SharePlaylists().ContainsKey(_kid1));
        Assert.DoesNotContain(_store.SharePlaylists().Values, m => m.ContainsKey(_kid1));
        Assert.False(_store.SharePlaylists().ContainsKey(_kid2)); // kid2 has nobody restricted left to share with
    }

    [Fact]
    public void A_profile_that_stops_being_restricted_loses_the_playlists_toward_it()
    {
        Sut().EnsurePlaylists();
        var id = Playlist(_adult, _kid1);
        _gw.Users[_kid1] = _gw.Users[_kid1] with { CapScore = null, CapSubScore = null };

        Sut().EnsurePlaylists();

        Assert.False(_gw.Playlists.ContainsKey(id));
        Assert.False(_store.SharePlaylists()[_adult].ContainsKey(_kid1));
    }

    [Fact]
    public void Restricted_ignores_the_plugins_own_library_list_and_tags_but_counts_everything_else()
    {
        var other = Guid.NewGuid();
        AddUser(other, "other", null);
        Sut().EnsurePlaylists();
        Assert.DoesNotContain(_store.SharePlaylists()[_adult].Keys, k => k == other);

        // the plugin's isolation: an explicit list of every real library (plus a share library) and swm- tags
        var share = Guid.NewGuid();
        _store.SetLibrary(other, ShareKind.Movie, share);
        _gw.Users[other] = _gw.Users[other] with { AllLibraries = false, EnabledLibraries = new List<Guid> { _lib, share }, BlockedTags = new[] { LibraryAccessManager.RecipientTag(_kid1) } };
        Sut().EnsurePlaylists();
        Assert.DoesNotContain(_store.SharePlaylists()[_adult].Keys, k => k == other);

        void Restricted(UserInfo u)
        {
            _gw.Users[other] = u;
            Sut().EnsurePlaylists();
            Assert.Contains(other, _store.SharePlaylists()[_adult].Keys);
            _gw.Users[other] = u with { AllLibraries = true, EnabledLibraries = new List<Guid>(), BlockedTags = Array.Empty<string>(), AllowedTags = Array.Empty<string>(), BlocksUnrated = false, CapScore = null };
            Sut().EnsurePlaylists();
            Assert.DoesNotContain(other, _store.SharePlaylists()[_adult].Keys);
        }

        var plain = _gw.Users[other] with { AllLibraries = true, EnabledLibraries = new List<Guid>(), BlockedTags = Array.Empty<string>() };
        Restricted(plain with { CapScore = 15 });
        Restricted(plain with { BlocksUnrated = true });
        Restricted(plain with { BlockedTags = new[] { "horror" } });
        Restricted(plain with { AllowedTags = new[] { "kids" } });
        Restricted(plain with { AllLibraries = false, EnabledLibraries = new List<Guid> { share } }); // lacks the real library

        // a real library the plugin has not seen yet (granted by the next full sync) does not count
        _store.SetKnownRealLibraries(new[] { _lib });
        var fresh = _gw.AddRealLibrary(Path.Combine(_dir, "fresh"));
        _gw.Users[other] = plain with { AllLibraries = false, EnabledLibraries = new List<Guid> { _lib } };
        Sut().EnsurePlaylists();
        Assert.DoesNotContain(other, _store.SharePlaylists()[_adult].Keys);
        _store.SetKnownRealLibraries(new[] { _lib, fresh });
        Sut().EnsurePlaylists();
        Assert.Contains(other, _store.SharePlaylists()[_adult].Keys);
    }

    [Fact]
    public void Who_may_share_decides_who_gets_playlists()
    {
        _who = WhoMayShare.UncappedOnly;

        Sut().EnsurePlaylists();

        Assert.Equal(new[] { _adult }, _store.SharePlaylists().Keys);
    }

    [Fact]
    public void Renamed_deleted_public_or_shared_plugin_playlists_are_healed()
    {
        Sut().EnsurePlaylists();
        var renamed = Playlist(_adult, _kid1);
        var deleted = Playlist(_adult, _kid2);
        var published = Playlist(_kid1, _kid2);
        var shared = Playlist(_kid2, _kid1);
        _gw.Playlists[renamed].Name = "my list";
        _gw.Playlists.Remove(deleted);
        _gw.Playlists[published].IsPublic = true;
        _gw.Playlists[shared].SharedWithOthers = true;

        Sut().EnsurePlaylists();

        Assert.Equal("Share with kid1", _gw.Playlists[renamed].Name);
        Assert.NotEqual(deleted, Playlist(_adult, _kid2));
        Assert.Equal("Share with kid2", _gw.Playlists[Playlist(_adult, _kid2)].Name);
        Assert.False(_gw.Playlists[published].IsPublic);
        Assert.False(_gw.Playlists[shared].SharedWithOthers);
    }

    [Fact]
    public void A_stored_id_that_now_belongs_to_another_user_is_never_touched()
    {
        Sut().EnsurePlaylists();
        var id = Playlist(_adult, _kid1);
        var newbie = Guid.NewGuid();
        AddUser(newbie, "newbie", null);
        _gw.Playlists[id].Owner = newbie; // adult deleted it; newbie's new playlist got the same folder and id
        _gw.Playlists[id].Name = "Share with kid1";
        _gw.Playlists[id].Items.Add(_pg);
        _gw.PlaylistWrites.Clear();

        Sut().EnsurePlaylists();
        Sut().Reconcile();

        Assert.NotEqual(id, Playlist(_adult, _kid1));
        Assert.Equal(newbie, _gw.Playlists[id].Owner);
        Assert.Equal(new[] { _pg }, _gw.Playlists[id].Items);
        Assert.DoesNotContain(_gw.PlaylistWrites, w => w.PlaylistId == id);
    }

    [Fact]
    public void A_user_made_playlist_with_the_same_name_is_never_adopted_or_touched()
    {
        var mine = _gw.SeedPlaylist(_adult, "Share with kid1", _e18);

        Sut().EnsurePlaylists();
        Sut().Reconcile();

        Assert.NotEqual(mine, Playlist(_adult, _kid1));
        Assert.Equal(new[] { _e18 }, _gw.Playlists[mine].Items);
        Assert.DoesNotContain(_gw.PlaylistWrites, w => w.PlaylistId == mine);
        Assert.Empty(_store.All());
    }

    [Fact]
    public void A_failed_rename_deletes_the_new_playlist_and_stores_nothing()
    {
        _gw.ThrowOnce = nameof(IServerGateway.RenamePlaylist);

        Sut().EnsurePlaylists();

        Assert.DoesNotContain(_gw.Playlists.Values, p => RandomName.IsMatch(p.Name));
        Assert.Equal(3, _gw.Playlists.Count); // the other three were made; the failed one goes again next time
        Sut().EnsurePlaylists();
        Assert.Equal(4, _gw.Playlists.Count);
    }

    [Fact]
    public void Leftover_random_named_playlists_of_a_sharer_are_swept()
    {
        var leftover = _gw.SeedPlaylist(_adult, "swm-" + Guid.NewGuid().ToString("N"));
        var lookalike = _gw.SeedPlaylist(_adult, "swm-not-ours");

        Sut().EnsurePlaylists();

        Assert.False(_gw.Playlists.ContainsKey(leftover));
        Assert.True(_gw.Playlists.ContainsKey(lookalike));
    }

    [Fact]
    public void Turning_the_setting_off_removes_every_playlist_and_keeps_the_shares_and_on_mirrors_them_back()
    {
        Sut().EnsurePlaylists();
        var share = WebShare(_adult, _e18, _kid1);
        Sut().Reconcile();
        Assert.Contains(_e18, Entries(_adult, _kid1));

        _enabled = false;
        Sut().EnsurePlaylists();
        Sut().Reconcile();
        Assert.Empty(_gw.Playlists);
        Assert.Empty(_store.SharePlaylists());
        Assert.Equal(ShareState.Active, _store.All().Single(r => r.Id == share.Id).State);

        _enabled = true;
        Sut().EnsurePlaylists();
        Sut().Reconcile();
        Assert.Equal(new[] { _e18 }, Entries(_adult, _kid1));
        Assert.Equal(ShareState.Active, _store.All().Single(r => r.Id == share.Id).State);
        Assert.True(_store.All().Single(r => r.Id == share.Id).ViaPlaylist);
    }

    // ---- the owner tag (ruling B2) ----

    [Fact]
    public void Every_plugin_playlist_carries_its_owners_tag_and_every_other_user_blocks_it()
    {
        Sut().EnsurePlaylists();

        foreach (var (sharer, map) in _store.SharePlaylists())
        {
            Assert.All(map.Values, id => Assert.Equal(new[] { Tag(sharer) }, _gw.Playlists[id].Tags));
        }

        Assert.Equal(new[] { Tag(_kid1), Tag(_kid2) }.OrderBy(t => t), PluginTags(_adult));
        Assert.Equal(new[] { Tag(_adult), Tag(_kid2) }.OrderBy(t => t), PluginTags(_kid1));
        Assert.Equal(new[] { Tag(_adult), Tag(_kid1) }.OrderBy(t => t), PluginTags(_kid2));
        Assert.All(_gw.Users.Values, u => Assert.True(u.AllLibraries)); // tags only narrow; nobody is switched off all libraries
        Assert.Empty(_store.Snapshots());
    }

    [Fact]
    public void The_block_comes_before_the_playlist_and_the_tag_before_its_display_name()
    {
        var newbie = Guid.NewGuid();
        AddUser(newbie, "newbie", null);

        Sut().EnsurePlaylists();

        var create = _gw.Calls.FindIndex(c => c.Method == nameof(IServerGateway.CreatePlaylist) && (Guid)c.Args[0]! == newbie);
        var id = _gw.PlaylistWrites.First(w => w.Method == nameof(IServerGateway.CreatePlaylist) && _gw.Playlists.TryGetValue(w.PlaylistId, out var p) && p.Owner == newbie).PlaylistId;
        var block = _gw.Calls.FindIndex(c => c.Method == nameof(IServerGateway.SetBlockedTags) && (Guid)c.Args[0]! == _kid1 && ((IReadOnlyList<string>)c.Args[1]!).Contains(Tag(newbie)));
        var tag = _gw.Calls.FindIndex(c => c.Method == nameof(IServerGateway.TagPlaylist) && (Guid)c.Args[0]! == id);
        var rename = _gw.Calls.FindIndex(c => c.Method == nameof(IServerGateway.RenamePlaylist) && (Guid)c.Args[0]! == id);
        Assert.True(block >= 0 && block < create && create < tag && tag < rename, $"{block} {create} {tag} {rename}");
    }

    [Fact]
    public void A_failed_tag_deletes_the_new_playlist_and_stores_nothing()
    {
        _gw.ThrowOnce = nameof(IServerGateway.TagPlaylist);

        Sut().EnsurePlaylists();

        Assert.Equal(3, _gw.Playlists.Count);
        Assert.All(_gw.Playlists.Values, p => Assert.Single(p.Tags));
        Assert.DoesNotContain(_gw.Playlists.Values, p => RandomName.IsMatch(p.Name));
    }

    [Fact]
    public void A_tag_or_block_removed_by_hand_comes_back_and_a_new_user_blocks_every_owner()
    {
        Sut().EnsurePlaylists();
        var id = Playlist(_adult, _kid1);
        _gw.Playlists[id].Tags.Clear();
        _gw.Users[_kid2] = _gw.Users[_kid2] with { BlockedTags = Array.Empty<string>() };
        var newbie = Guid.NewGuid();
        AddUser(newbie, "newbie", null);

        Sut().EnsurePlaylists();

        Assert.Equal(new[] { Tag(_adult) }, _gw.Playlists[id].Tags);
        Assert.Equal(new[] { Tag(_adult), Tag(_kid1), Tag(newbie) }.OrderBy(t => t), PluginTags(_kid2));
        Assert.Equal(new[] { Tag(_adult), Tag(_kid1), Tag(_kid2) }.OrderBy(t => t), PluginTags(newbie));
    }

    [Fact]
    public void With_the_setting_off_and_no_share_library_nobody_has_a_plugin_tag()
    {
        Sut().EnsurePlaylists();
        _enabled = false;

        Sut().EnsurePlaylists();

        Assert.All(_gw.Users.Keys, u => Assert.Empty(PluginTags(u)));
    }

    [Fact]
    public void With_the_setting_off_a_recipients_tag_stays_while_their_share_library_does()
    {
        Sut().EnsurePlaylists();
        WebShare(_adult, _e18, _kid1);
        _enabled = false;

        Sut().EnsurePlaylists();

        Assert.Equal(new[] { Tag(_kid1) }, PluginTags(_adult));
        Assert.Equal(new[] { Tag(_kid1) }, PluginTags(_kid2));
        Assert.Empty(PluginTags(_kid1));
    }

    // ---- Fix round 1 ----

    private string ShowFolder => Path.Combine(_media, "Spike Show (2010)");

    // S01E02's file is renamed and scanned again: a new item, the old entry dangles at the old path.
    private Guid ReplaceEpisodeTwo()
    {
        var oldPath = _gw.Items[_ep2].Path!;
        var newPath = Path.Combine(ShowFolder, "Season 01", "Spike Show S01E02 Remastered.mp4");
        File.Move(oldPath, newPath);
        var ep2b = Guid.NewGuid();
        _gw.Items.Remove(_ep2);
        _gw.GonePaths[_ep2] = oldPath;
        _gw.Items[ep2b] = new ItemInfo(ep2b, null, "Spike Show Two", 2010, null, new Dictionary<string, string>(), _lib, new List<string>(), newPath, _show);
        _gw.ItemsByPath[newPath] = ep2b;
        _gw.SetVisible(_adult, ep2b);
        var first = _gw.Media[_show].Episodes[0];
        _gw.Media[_show] = new SourceMedia(string.Empty, Array.Empty<string>(), new List<(int, int, string)> { first, (1, 2, newPath) });
        return ep2b;
    }

    [Fact]
    public void A_dangling_episode_entry_keeps_the_series_share_and_the_playlist_is_repaired_and_settles()
    {
        Sut().EnsurePlaylists();
        Add(_adult, _kid1, _ep2);
        Sut().Reconcile();
        var share = Assert.Single(Active(_adult, _kid1));
        ReplaceEpisodeTwo();

        Sut().Reconcile();

        Assert.Equal(share.Id, Assert.Single(Active(_adult, _kid1)).Id);
        Assert.Equal(new[] { _ep1 }, Entries(_adult, _kid1)); // one current episode added, the dangling entry gone
        var before = Fingerprint();
        Sut().Reconcile();
        Assert.Equal(before, Fingerprint());
    }

    [Fact]
    public void A_dangling_entry_that_cannot_belong_to_the_series_does_not_keep_it_but_one_without_a_path_does()
    {
        Sut().EnsurePlaylists();
        Add(_adult, _kid1, _ep2);
        Sut().Reconcile();
        var elsewhere = Guid.NewGuid();
        _gw.GonePaths[elsewhere] = Path.Combine(_dir, "elsewhere", "Some Film (1999)", "Some Film.mp4");
        Entries(_adult, _kid1).Clear();
        Add(_adult, _kid1, elsewhere);

        // a path-less dangling entry could be anything: the share is kept
        var nowhere = Guid.NewGuid();
        Add(_adult, _kid1, nowhere);
        Sut().Reconcile();
        Assert.Single(Active(_adult, _kid1));

        // only a dangling entry outside the series folder is left: the series was genuinely taken out
        Entries(_adult, _kid1).Remove(nowhere);
        Sut().Reconcile();
        Assert.Empty(Active(_adult, _kid1));
    }

    [Fact]
    public void A_playlist_whose_file_cannot_be_read_keeps_its_shares_until_it_can()
    {
        Sut().EnsurePlaylists();
        Add(_adult, _kid1, _e18);
        Sut().Reconcile();
        var share = Assert.Single(Active(_adult, _kid1));
        Entries(_adult, _kid1).Clear(); // as 12.4 shows a dangling entry: gone from the item, only in the file
        _gw.UnreadableFiles.Add(Playlist(_adult, _kid1));

        for (var i = 0; i < SharePlaylistManager.DanglingRunsLimit + 2; i++)
        {
            Sut().Reconcile(retryMirrors: true);
        }

        Assert.Equal(share.Id, Assert.Single(Active(_adult, _kid1)).Id);
        Assert.Equal(0, Assert.Single(Active(_adult, _kid1)).DanglingRuns); // never counted
        Assert.Empty(_store.RefusalNotes());

        _gw.UnreadableFiles.Clear();
        Sut().Reconcile();
        Assert.Empty(Active(_adult, _kid1));
    }

    [Fact]
    public void A_pathless_dangling_entry_keeps_a_share_for_three_full_runs_then_goes_with_a_note()
    {
        Sut().EnsurePlaylists();
        Add(_adult, _kid1, _e18);
        Sut().Reconcile();
        var pid = Playlist(_adult, _kid1);
        var nowhere = Guid.NewGuid(); // an entry whose item is gone and which has no stored path
        Entries(_adult, _kid1).Clear();
        Add(_adult, _kid1, nowhere);

        Sut().Reconcile(retryMirrors: true);
        Sut().Reconcile(retryMirrors: true);
        for (var i = 0; i < 5; i++)
        {
            Sut().Reconcile(); // event passes are not counted
        }

        var kept = Assert.Single(Active(_adult, _kid1));
        Assert.Equal(2, kept.DanglingRuns);
        Assert.Contains(nowhere, Entries(_adult, _kid1));
        Assert.Empty(_store.RefusalNotes());

        Sut().Reconcile(retryMirrors: true); // the third full run: the entry goes, with a note naming no title

        Assert.DoesNotContain(nowhere, _gw.Playlists[pid].Items);
        var note = Assert.Single(_store.RefusalNotes());
        Assert.Equal("A playlist entry pointed at a file that no longer exists and was removed.", note.Message);
        Assert.Equal(string.Empty, note.Title);
        Assert.Equal(_adult, note.SharerUserId);

        // from then on the playlist decides normally: the title is not in it, so the share ends
        Sut().Reconcile();
        Assert.Empty(Active(_adult, _kid1));

        // and a still-wanted title can be put back by its owner
        Add(_adult, _kid1, _e18);
        Sut().Reconcile();
        Assert.Single(Active(_adult, _kid1));
    }

    [Fact]
    public void A_dangling_entry_the_server_will_not_remove_gets_no_note_and_is_tried_again()
    {
        Sut().EnsurePlaylists();
        Add(_adult, _kid1, _e18);
        Sut().Reconcile();
        var nowhere = Guid.NewGuid();
        Entries(_adult, _kid1).Clear();
        Add(_adult, _kid1, nowhere);
        _gw.KeepDanglingEntries = true;

        for (var i = 0; i < SharePlaylistManager.DanglingRunsLimit + 1; i++)
        {
            Sut().Reconcile(retryMirrors: true);
        }

        Assert.Empty(_store.RefusalNotes());
        Assert.Contains(nowhere, Entries(_adult, _kid1));
        Assert.Single(Active(_adult, _kid1));

        _gw.KeepDanglingEntries = false;
        Sut().Reconcile(retryMirrors: true);
        Assert.DoesNotContain(nowhere, Entries(_adult, _kid1));
        Assert.Single(_store.RefusalNotes());
    }

    [Fact]
    public void The_dangling_count_starts_again_when_the_title_is_back_in_the_playlist()
    {
        Sut().EnsurePlaylists();
        Add(_adult, _kid1, _e18);
        Sut().Reconcile();
        var nowhere = Guid.NewGuid();
        Entries(_adult, _kid1).Clear();
        Add(_adult, _kid1, nowhere);
        Sut().Reconcile(retryMirrors: true);
        Sut().Reconcile(retryMirrors: true);

        Add(_adult, _kid1, _e18);
        Sut().Reconcile(retryMirrors: true);
        Assert.Equal(0, Assert.Single(Active(_adult, _kid1)).DanglingRuns);

        Entries(_adult, _kid1).Remove(_e18);
        Sut().Reconcile(retryMirrors: true);
        Sut().Reconcile(retryMirrors: true);
        Assert.Contains(nowhere, Entries(_adult, _kid1)); // two runs, not three: still kept, nothing removed
        Assert.Single(Active(_adult, _kid1));
    }

    [Fact]
    public void A_dangling_entry_for_a_title_with_no_known_folder_is_counted_and_removed_but_one_inside_another_shares_folder_stays()
    {
        Sut().EnsurePlaylists();
        Add(_adult, _kid1, _e18);
        Add(_adult, _kid1, _ep1);
        Sut().Reconcile();
        Assert.Equal(2, Active(_adult, _kid1).Count);
        _gw.Items[_e18] = _gw.Items[_e18] with { Path = null }; // the film's folder is unknown
        var gone = Guid.NewGuid();
        _gw.GonePaths[gone] = Path.Combine(_dir, "elsewhere", "Gone (1999)", "Gone.mp4");
        var showEntry = Guid.NewGuid(); // dangling inside the series' folder: could be the series, never removed for the film
        _gw.GonePaths[showEntry] = Path.Combine(_media, "Spike Show (2010)", "Season 01", "Old S01E09.mp4");
        Entries(_adult, _kid1).Remove(_e18);
        Add(_adult, _kid1, gone);
        Add(_adult, _kid1, showEntry);

        for (var i = 0; i < SharePlaylistManager.DanglingRunsLimit; i++)
        {
            Sut().Reconcile(retryMirrors: true);
        }

        Assert.DoesNotContain(gone, Entries(_adult, _kid1));
        Assert.Contains(_ep1, Entries(_adult, _kid1));
        Assert.Single(_store.RefusalNotes());
    }

    [Fact]
    public void A_playlist_whose_entries_vanish_mid_reconcile_is_not_read_as_empty()
    {
        Sut().EnsurePlaylists();
        Add(_adult, _kid1, _e18);
        Sut().Reconcile();
        _gw.VanishedEntries.Add(Playlist(_adult, _kid1));

        Sut().Reconcile();

        Assert.Single(Active(_adult, _kid1));
    }

    [Fact]
    public void A_create_that_throws_takes_the_entry_out_with_a_retry_note()
    {
        Sut().EnsurePlaylists();
        Add(_adult, _kid1, _e18);
        _gw.ThrowOnce = nameof(IServerGateway.GetSourceMedia);

        Sut().Reconcile();
        Sut().Reconcile();

        Assert.Empty(Entries(_adult, _kid1));
        Assert.Empty(_store.All());
        var note = Assert.Single(_store.RefusalNotes());
        Assert.Equal("That title could not be shared just now. Add it again to retry.", note.Message);
    }

    [Fact]
    public void Turning_the_setting_off_works_during_a_setup_problem_and_nothing_else_runs()
    {
        Sut().EnsurePlaylists();
        _setupProblem = "the share folder overlaps a library";
        _enabled = false;

        Sut().Run(false);

        Assert.Empty(_gw.Playlists);
        Assert.Empty(_store.SharePlaylists());
        Assert.All(_gw.Users.Keys, u => Assert.Empty(PluginTags(u)));

        _enabled = true;
        Sut().Run(true);
        Assert.Empty(_gw.Playlists); // with the problem, nothing is created
    }

    [Fact]
    public void A_named_tagged_playlist_missing_from_the_map_is_deleted_and_exactly_one_is_made()
    {
        var orphan = _gw.SeedPlaylist(_adult, "Share with kid1"); // rename done, store write lost
        _gw.Playlists[orphan].Tags.Add(Tag(_adult));

        Sut().EnsurePlaylists();

        Assert.False(_gw.Playlists.ContainsKey(orphan));
        Assert.Single(_gw.Playlists.Values, p => p.Owner == _adult && p.Name == "Share with kid1");
    }

    [Fact]
    public void A_failed_store_write_deletes_the_new_playlist_and_stores_nothing()
    {
        var tmp = Path.Combine(_dir, "store", "shares.json.tmp");
        Directory.CreateDirectory(tmp); // every store write now fails

        Sut().EnsurePlaylists();

        Directory.Delete(tmp);
        Assert.Empty(_gw.Playlists);
        Assert.Empty(_store.SharePlaylists());
    }

    [Fact]
    public void Setting_off_also_deletes_a_tagged_playlist_missing_from_the_map()
    {
        Sut().EnsurePlaylists();
        var orphan = _gw.SeedPlaylist(_kid1, "Share with kid2");
        _gw.Playlists[orphan].Tags.Add(Tag(_kid1));
        _enabled = false;

        Sut().EnsurePlaylists();

        Assert.Empty(_gw.Playlists);
    }

    [Fact]
    public void A_users_own_random_looking_playlist_with_entries_and_no_plugin_tag_is_left_alone()
    {
        var mine = _gw.SeedPlaylist(_adult, "swm-" + Guid.NewGuid().ToString("N"), _pg);
        var otherTag = _gw.SeedPlaylist(_adult, "swm-" + Guid.NewGuid().ToString("N"));
        _gw.Playlists[otherTag].Tags.Add("favourites");
        var someoneElses = _gw.SeedPlaylist(_adult, "Share with kid1");
        _gw.Playlists[someoneElses].Tags.Add(Tag(_kid1)); // not its owner's tag

        Sut().EnsurePlaylists();

        Assert.True(_gw.Playlists.ContainsKey(mine));
        Assert.True(_gw.Playlists.ContainsKey(otherTag));
        Assert.True(_gw.Playlists.ContainsKey(someoneElses));
    }

    // ---- Reconcile ----

    [Fact]
    public void Adding_a_film_shares_it_and_removing_it_unshares_it()
    {
        Sut().EnsurePlaylists();
        Add(_adult, _kid1, _e18);

        Sut().Reconcile();

        var share = Assert.Single(Active(_adult, _kid1));
        Assert.Equal(_e18, share.OriginalItemId);
        Assert.True(share.ViaPlaylist);
        Assert.Equal(new[] { _e18 }, Entries(_adult, _kid1));

        Entries(_adult, _kid1).Clear();
        Sut().Reconcile();
        Assert.Empty(_store.All());
    }

    [Fact]
    public void Already_shared_or_already_seen_is_taken_out_with_no_note_and_no_share()
    {
        Sut().EnsurePlaylists();
        WebShare(_adult, _e18, _kid2);       // kid2 already has Spike Eighteen
        Add(_kid1, _kid2, _pg);              // kid2 already sees Spike PG
        _gw.SetVisible(_kid1, _e18);         // let kid1 see the 18 (raised cap) so it may try
        Add(_kid1, _kid2, _e18);

        Sut().Reconcile();

        Assert.Empty(Entries(_kid1, _kid2));
        Assert.Empty(Active(_kid1, _kid2));
        Assert.Empty(_store.RefusalNotes());
    }

    [Fact]
    public void A_title_seen_only_through_a_share_is_taken_out_with_the_reshare_note_and_the_other_kid_gains_nothing()
    {
        Sut().EnsurePlaylists();
        var share = WebShare(_adult, _e18, _kid1);
        var copy = RegisterCopy(share);
        Add(_kid1, _kid2, copy);

        Sut().Reconcile();

        Assert.Empty(Entries(_kid1, _kid2));
        Assert.DoesNotContain(_store.All(), r => r.RecipientUserId == _kid2);
        var note = Assert.Single(_store.RefusalNotes());
        Assert.Equal((_kid1, _kid2), (note.SharerUserId, note.RecipientUserId));
        Assert.Equal("Spike Eighteen was shared with you, so you can't pass it on.", note.Message);
        Assert.Equal("Spike Eighteen", note.Title);
        Assert.Equal(_now, note.AtUtc);
    }

    [Fact]
    public void A_refusal_for_a_title_the_sharer_cannot_see_names_no_title()
    {
        Sut().EnsurePlaylists();
        Add(_kid1, _kid2, _f15); // the server would not store it; the plugin must not trust that

        Sut().Reconcile();

        Assert.Empty(Entries(_kid1, _kid2));
        var note = Assert.Single(_store.RefusalNotes());
        Assert.Equal(string.Empty, note.Title);
        Assert.Equal(ShareEligibility.CannotShareMessage, note.Message);
        Assert.DoesNotContain("Fifteen", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Any_episode_stands_for_its_series_and_shares_the_whole_series()
    {
        Sut().EnsurePlaylists();
        Add(_adult, _kid1, _ep2); // one episode of two

        Sut().Reconcile();

        var share = Assert.Single(Active(_adult, _kid1));
        Assert.Equal((_show, ShareKind.Series), (share.OriginalItemId, share.Kind));
        Assert.True(share.ViaPlaylist);
        Assert.Equal(new[] { _ep2 }, Entries(_adult, _kid1)); // new or missing episodes are not churned in
        Assert.Empty(_store.RefusalNotes());

        Sut().Reconcile();
        Assert.Single(Active(_adult, _kid1));

        Entries(_adult, _kid1).Clear(); // no episode left: the series goes
        Sut().Reconcile();
        Assert.Empty(_store.All());
    }

    // M1: a title someone else already shared with the recipient is refused as "can already watch it" (naming nobody), and
    // the entry leaves the playlist without a note, as for any title the recipient can already see.
    [Fact]
    public void A_title_already_shared_by_someone_else_leaves_the_playlist_without_a_note()
    {
        var other = Guid.NewGuid();
        AddUser(other, "other", null);
        _gw.SetVisible(other, _e18);
        WebShare(_adult, _e18, _kid1);
        Sut().EnsurePlaylists();
        Add(other, _kid1, _e18);

        Sut().Reconcile();

        Assert.DoesNotContain(_e18, Entries(other, _kid1));
        Assert.DoesNotContain(_store.RefusalNotes(), n => n.SharerUserId == other);
        Assert.Single(_store.All(), r => r.State == ShareState.Active && r.SharerUserId == _adult);
    }

    [Fact]
    public void A_refused_series_takes_out_every_entry_of_that_series()
    {
        Sut().EnsurePlaylists();
        _gw.SetVisible(_kid1, _show); // kid1 already sees the series
        Add(_adult, _kid1, _ep1);
        Add(_adult, _kid1, _pg);
        Add(_adult, _kid1, _ep2);

        Sut().Reconcile();

        Assert.DoesNotContain(_ep1, Entries(_adult, _kid1));
        Assert.DoesNotContain(_ep2, Entries(_adult, _kid1));
        Assert.Empty(_store.RefusalNotes()); // RecipientAlreadySees: nothing to tell
    }

    [Fact]
    public void An_entry_that_is_neither_film_nor_episode_is_taken_out_with_a_note()
    {
        var song = Guid.NewGuid();
        _gw.Items[song] = new ItemInfo(song, null, "A Song", null, null, new Dictionary<string, string>(), _lib, new List<string>(), "/music/a.mp3");
        _gw.SetVisible(_adult, song);
        Sut().EnsurePlaylists();
        Add(_adult, _kid1, song);

        Sut().Reconcile();

        Assert.Empty(Entries(_adult, _kid1));
        var note = Assert.Single(_store.RefusalNotes());
        Assert.Equal("A Song", note.Title);
        Assert.Equal("Only movies and series can be shared.", note.Message);
    }

    [Fact]
    public void A_web_share_is_mirrored_into_the_playlist_and_removing_it_on_the_web_takes_it_out()
    {
        Sut().EnsurePlaylists();
        var film = WebShare(_adult, _e18, _kid1);
        var show = WebShare(_adult, _show, _kid1);

        Sut().Reconcile();

        Assert.Equal(new[] { _e18, _ep1, _ep2 }, Entries(_adult, _kid1)); // the series id is expanded by the server
        Assert.All(Active(_adult, _kid1), r => Assert.True(r.ViaPlaylist));

        Assert.True(Service().Remove(film.Id, _adult));
        Assert.True(Service().Remove(show.Id, _adult));
        Assert.Empty(Entries(_adult, _kid1));
        Sut().Reconcile();
        Assert.Empty(_store.All()); // not shared again from a stale entry
    }

    [Fact]
    public void Removing_a_share_whose_playlist_entry_cannot_be_taken_out_keeps_the_share()
    {
        Sut().EnsurePlaylists();
        Add(_adult, _kid1, _e18);
        Sut().Reconcile();
        var share = Assert.Single(Active(_adult, _kid1));
        _gw.ThrowOnce = nameof(IServerGateway.RemoveFromPlaylist);

        Assert.ThrowsAny<Exception>(() => Service().Remove(share.Id, _adult));

        Assert.Single(Active(_adult, _kid1)); // a stale entry would only share it again
        Assert.Equal(new[] { _e18 }, Entries(_adult, _kid1));
    }

    [Fact]
    public void A_web_share_of_a_title_the_sharer_cannot_see_now_is_left_out_of_the_playlist_and_kept()
    {
        Sut().EnsurePlaylists();
        var film = WebShare(_adult, _e18, _kid1);
        _gw.SetVisible(_adult, _e18, false);

        Sut().Reconcile();
        var writes = _gw.PlaylistWrites.Count;
        Sut().Reconcile();

        Assert.Empty(Entries(_adult, _kid1));
        Assert.False(_store.All().Single(r => r.Id == film.Id).ViaPlaylist);
        Assert.Equal(ShareState.Active, _store.All().Single(r => r.Id == film.Id).State);
        Assert.Equal(writes, _gw.PlaylistWrites.Count); // no add attempted again and again
    }

    [Fact]
    public void Closed_and_broken_shares_leave_the_playlist_a_note_only_for_broken_and_a_re_add_shares_afresh()
    {
        Sut().EnsurePlaylists();
        Add(_adult, _kid1, _e18);
        Add(_adult, _kid1, _f15);
        Sut().Reconcile();
        var e18 = Active(_adult, _kid1).Single(r => r.OriginalItemId == _e18);
        var f15 = Active(_adult, _kid1).Single(r => r.OriginalItemId == _f15);
        _store.Upsert(e18 with { State = ShareState.Closed, StateReason = ShareSynchronizer.NoLongerNeededReason, FolderName = string.Empty, ClosedUtc = _now });
        _store.Upsert(f15 with { State = ShareState.Broken, StateReason = ShareSynchronizer.OriginalMissingReason, FolderName = string.Empty });

        Sut().Reconcile();

        Assert.Empty(Entries(_adult, _kid1));
        var note = Assert.Single(_store.RefusalNotes());
        Assert.Equal("Spike Fifteen", note.Title);
        Assert.Contains("original missing", note.Message, StringComparison.Ordinal);
        Assert.All(_store.All(), r => Assert.False(r.ViaPlaylist));
        Sut().Reconcile();
        Assert.Single(_store.RefusalNotes());

        Add(_adult, _kid1, _f15); // added again: a fresh share
        Sut().Reconcile();
        Assert.Single(Active(_adult, _kid1));
        Assert.Contains(_f15, Entries(_adult, _kid1));
    }

    [Fact]
    public void A_share_whose_original_is_gone_is_left_to_the_sync()
    {
        Sut().EnsurePlaylists();
        Add(_adult, _kid1, _e18);
        Sut().Reconcile();
        _gw.Items.Remove(_e18); // file renamed, not scanned again yet: the entry no longer resolves

        Sut().Reconcile();

        Assert.Single(Active(_adult, _kid1));
    }

    [Fact]
    public void A_copy_added_by_someone_who_can_see_the_original_shares_the_original_and_the_playlist_holds_the_original()
    {
        Sut().EnsurePlaylists();
        var share = WebShare(_adult, _e18, _kid1);
        var copy = RegisterCopy(share);
        _gw.SetVisible(_kid1, _e18); // kid1's cap was raised
        Add(_kid1, _kid2, copy);

        Sut().Reconcile();
        Sut().Reconcile();

        var mine = Assert.Single(Active(_kid1, _kid2));
        Assert.Equal(_e18, mine.OriginalItemId);
        Assert.Equal(new[] { _e18 }, Entries(_kid1, _kid2));
    }

    [Fact]
    public void Reconcile_twice_changes_nothing_the_second_time_and_its_own_writes_settle()
    {
        Sut().EnsurePlaylists();
        WebShare(_adult, _f15, _kid1);
        Add(_adult, _kid1, _e18);
        Add(_adult, _kid1, _ep1);
        Add(_kid1, _kid2, _pg);
        WebShare(_adult, _e18, _kid2);
        Sut().Reconcile();
        var before = Fingerprint();

        Sut().EnsurePlaylists();
        Sut().Reconcile();

        Assert.Equal(before, Fingerprint());
    }

    [Fact]
    public void Nothing_happens_while_the_setting_is_off()
    {
        Sut().EnsurePlaylists();
        Add(_adult, _kid1, _e18);
        _enabled = false;

        Sut().Reconcile();

        Assert.Empty(_store.All());
    }

    [Fact]
    public void Notes_are_the_callers_own_and_all_only_for_an_admin()
    {
        var admin = Guid.NewGuid();
        AddUser(admin, "admin", null, admin: true);
        _store.AddRefusalNote(new RefusalNote(_kid1, _kid2, "A", "m", _now));
        _store.AddRefusalNote(new RefusalNote(_adult, _kid1, "B", "m", _now));

        Assert.Equal(new[] { "A" }, Sut().NotesFor(_kid1, false)!.Select(n => n.Title));
        Assert.Null(Sut().NotesFor(_kid1, true));
        Assert.Empty(Sut().NotesFor(admin, false)!);
        Assert.Equal(2, Sut().NotesFor(admin, true)!.Count);
    }

    [Fact]
    public void Remove_all_deletes_only_plugin_playlists_it_still_owns_and_clears_the_map()
    {
        Sut().EnsurePlaylists();
        var mine = _gw.SeedPlaylist(_adult, "Share with kid1");

        Sut().RemoveAll();

        Assert.Equal(new[] { mine }, _gw.Playlists.Keys);
        Assert.Empty(_store.SharePlaylists());
    }

    // ---- Playlists of a profile that blocks unrated "Other" items (Jellyfin counts a playlist as one) ----

    private void BlockUnratedOther(Guid user) =>
        _gw.Users[user] = _gw.Users[user] with { BlocksUnrated = true, BlockedUnratedKinds = new[] { "Movie", "Series", "Other" } };

    [Fact]
    public void A_playlist_owned_by_a_profile_that_blocks_unrated_Other_items_carries_a_rating_that_profile_may_see()
    {
        BlockUnratedOther(_kid1);

        Sut().EnsurePlaylists();

        Assert.Equal("12", _gw.Playlists[Playlist(_kid1, _kid2)].Rating);
    }

    [Fact]
    public void A_playlist_carries_no_rating_for_an_owner_who_does_not_block_unrated_Other_items()
    {
        Sut().EnsurePlaylists();

        Assert.Null(_gw.Playlists[Playlist(_kid1, _kid2)].Rating);
        Assert.Null(_gw.Playlists[Playlist(_adult, _kid1)].Rating);
    }

    [Fact]
    public void A_playlist_rating_follows_the_owner_blocking_and_unblocking_unrated_Other_items()
    {
        Sut().EnsurePlaylists();
        Assert.Null(_gw.Playlists[Playlist(_kid1, _kid2)].Rating);

        BlockUnratedOther(_kid1);
        Sut().EnsurePlaylists();
        Assert.Equal("12", _gw.Playlists[Playlist(_kid1, _kid2)].Rating);

        _gw.Users[_kid1] = _gw.Users[_kid1] with { BlocksUnrated = false, BlockedUnratedKinds = Array.Empty<string>() };
        Sut().EnsurePlaylists();
        Assert.Null(_gw.Playlists[Playlist(_kid1, _kid2)].Rating);
    }

    [Fact]
    public void A_playlist_rating_follows_the_owners_rating_limit()
    {
        BlockUnratedOther(_kid1);
        Sut().EnsurePlaylists();

        AddUser(_kid1, "kid1", 8);
        BlockUnratedOther(_kid1);
        Sut().EnsurePlaylists();

        Assert.Equal("PG", _gw.Playlists[Playlist(_kid1, _kid2)].Rating);
    }

    [Fact]
    public void A_settled_playlist_rating_is_not_written_again()
    {
        BlockUnratedOther(_kid1);
        Sut().EnsurePlaylists();
        var before = _gw.PlaylistWrites.Count(w => w.Method == nameof(IServerGateway.SetPlaylistRating));

        Sut().EnsurePlaylists();

        Assert.Equal(before, _gw.PlaylistWrites.Count(w => w.Method == nameof(IServerGateway.SetPlaylistRating)));
    }

    [Fact]
    public void An_owner_with_no_limit_who_blocks_unrated_Other_items_gets_the_highest_known_rating()
    {
        AddUser(_kid1, "kid1", null);
        BlockUnratedOther(_kid1);

        Assert.Equal("18", SharePlaylistManager.OwnerRatingFor(_gw.GetRatings(), _gw.Users[_kid1]));
        Assert.Null(SharePlaylistManager.OwnerRatingFor(_gw.GetRatings(), null));
    }
}
