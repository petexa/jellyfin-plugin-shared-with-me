using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Jellyfin.Plugin.SharedWithMe.Configuration;
using Jellyfin.Plugin.SharedWithMe.Model;
using Jellyfin.Plugin.SharedWithMe.Sharing;
using Jellyfin.Plugin.SharedWithMe.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SharedWithMe.Tests;

// 0.2, C: parent approval and notifications.
public sealed class ParentApprovalTests : IDisposable
{
    private const string Hook = "https://hooks.example.org/notify/s3cret-path?token=abc";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "swm-appr-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServerGateway _gw = new();
    private readonly ShareStore _store;
    private readonly string _media;
    private readonly string _shareRoot;
    private readonly PluginGate _gate = new();
    private DateTime _now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
    private ApprovalMode _mode = ApprovalMode.LimitedProfiles;
    private WhoMayShare _who = WhoMayShare.Everyone;
    private string? _url = Hook;
    private NotifyOn _on = NotifyOn.ApprovalsOnly;
    private int _tvDays;
    private Func<Uri, string, CancellationToken, Task<int>>? _post;
    private readonly ConcurrentQueue<(Uri Uri, string Body)> _sent = new();
    private ShareNotifier? _notifier;

    private readonly Guid _lib = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _adult = Guid.NewGuid();
    private readonly Guid _kid1 = Guid.NewGuid();
    private readonly Guid _kid2 = Guid.NewGuid();
    private readonly Guid _kid3 = Guid.NewGuid();
    private readonly Guid _e18 = Guid.NewGuid();
    private readonly Guid _f15 = Guid.NewGuid();
    private readonly Guid _pg = Guid.NewGuid();

    public ParentApprovalTests()
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
        AddUser(_admin, "Admin", null, admin: true);
        AddUser(_adult, "Alex", null);
        AddUser(_kid1, "Sam", 15, 3); // sees the 15 and the PG
        AddUser(_kid2, "Jo", 12, 1); // sees the PG only
        AddUser(_kid3, "Kai", 12, 1); // sees the PG only
        AddFilm(_e18, "Spike Eighteen", "GB-18");
        AddFilm(_f15, "Spike Fifteen", "GB-15");
        AddFilm(_pg, "Spike PG", "GB-PG");
        _gw.SetVisible(_kid1, _f15);
        _gw.SetVisible(_kid1, _pg);
        _gw.SetVisible(_kid2, _pg);
        _gw.SetVisible(_kid3, _pg);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---- set-up ----

    private ShareNotifier Notifier() => _notifier ??= new ShareNotifier(() => _url, () => _on, _post ?? Record, NullLogger.Instance);

    private Task<int> Record(Uri uri, string body, CancellationToken cancel)
    {
        _sent.Enqueue((uri, body));
        return Task.FromResult(204);
    }

    private ShareEligibility Eligibility() => new(_gw, _store, () => _who);

    private LibraryAccessManager Access() => new(_gw, _store);

    private ShareService Service() =>
        new(_gw, _store, Eligibility(), Access(), new ShareFolderBuilder(), () => _shareRoot, _gate, NullLogger.Instance, null, () => _now, () => _mode, Notifier());

    private ShareSynchronizer Sync() => new(_gw, _store, Access(), new ShareFolderBuilder(), () => _shareRoot, _gate, NullLogger.Instance, () => _now);

    private SharePlaylistManager Playlists() =>
        new(_gw, _store, Service(), Eligibility(), Access(), () => true, () => null, _gate, NullLogger.Instance, () => _now, () => _tvDays);

    private AuditService Audit(SharePlaylistManager? playlists = null) =>
        new(_gw, _store, Access(), new ShareFolderBuilder(), Sync(), () => _shareRoot, _gate, NullLogger.Instance, () => _now, playlists, () => false, () => _shareRoot);

    private void AddUser(Guid id, string name, int? cap, int? sub = null, bool admin = false) =>
        _gw.Users[id] = new UserInfo(id, name, admin, false, true, cap, sub, true, new List<Guid>(), Array.Empty<string>(), Array.Empty<string>());

    private void AddFilm(Guid id, string title, string rating)
    {
        var folder = Path.Combine(_media, title);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, title + ".mp4");
        File.WriteAllText(file, "video");
        _gw.Items[id] = new ItemInfo(id, ShareKind.Movie, title, 2002, rating, new Dictionary<string, string> { ["Tmdb"] = id.ToString("N")[..6] }, _lib, new List<string>(), file);
        _gw.Media[id] = new SourceMedia(file, Array.Empty<string>(), Array.Empty<(int, int, string)>());
        _gw.SetVisible(_admin, id);
        _gw.SetVisible(_adult, id);
    }

    // Everything that would change what anyone can see or reach: policies, tags, libraries, scans, playlists.
    private static readonly string[] WriteMethods =
    {
        nameof(IServerGateway.SetLibraryAccess), nameof(IServerGateway.SetBlockedTags), nameof(IServerGateway.CreateLibrary),
        nameof(IServerGateway.RemoveLibrary), nameof(IServerGateway.QueueScan), nameof(IServerGateway.QueueItemRefresh),
        nameof(IServerGateway.CreatePlaylist), nameof(IServerGateway.RemovePlaylist), nameof(IServerGateway.TagPlaylist),
        nameof(IServerGateway.RenamePlaylist), nameof(IServerGateway.AddToPlaylist), nameof(IServerGateway.RemoveFromPlaylist),
    };

    private int Writes() => _gw.Calls.Count(c => WriteMethods.Contains(c.Method));

    private string RecipientFolder(Guid recipient) => Path.Combine(_shareRoot, recipient.ToString("N"));

    private ShareRecord Stored(Guid id) => _store.All().Single(r => r.Id == id);

    private ShareRecord Ask(Guid sharer, Guid item, Guid recipient, DateTime? expires = null)
    {
        var (decision, record) = Service().Create(sharer, item, recipient, expires);
        Assert.Equal(Refusal.None, decision.Refusal);
        return record!;
    }

    private List<NotifyBody> Sent()
    {
        Notifier().WhenIdle().Wait(TimeSpan.FromSeconds(10));
        return _sent.Select(s => JsonSerializer.Deserialize<NotifyBody>(s.Body)!).ToList();
    }

    private Guid Playlist(Guid sharer, Guid recipient) => _store.SharePlaylists()[sharer][recipient];

    private List<Guid> Entries(Guid sharer, Guid recipient) => _gw.Playlists[Playlist(sharer, recipient)].Items;

    // ---- who waits ----

    [Fact]
    public void A_share_by_a_restricted_profile_waits_and_changes_nothing_for_anyone()
    {
        var expires = _now.AddDays(3);
        var record = Ask(_kid1, _f15, _kid2, expires);

        Assert.Equal(ShareState.Pending, record.State);
        Assert.Equal(string.Empty, record.FolderName);
        Assert.Equal(expires, record.ExpiresUtc);
        Assert.Equal(_now, record.CreatedUtc);
        Assert.Equal(record, Stored(record.Id) with { ProviderIds = record.ProviderIds });
        Assert.Equal(0, Writes());
        Assert.False(Directory.Exists(RecipientFolder(_kid2)));
        Assert.Empty(_store.Libraries());
        Assert.Empty(_store.Snapshots());
        Assert.All(_gw.Users.Values, u => Assert.True(u.AllLibraries));
        Assert.All(_gw.Users.Values, u => Assert.Empty(u.BlockedTags));
        Assert.False(new ShareSynchronizer(_gw, _store, Access(), new ShareFolderBuilder(), () => _shareRoot, _gate, NullLogger.Instance).IsInCharge());
    }

    [Fact]
    public void Administrators_and_unrestricted_profiles_never_wait()
    {
        AddUser(_admin, "Admin", 12, 1, admin: true); // an administrator with an age limit of their own still never waits
        _gw.SetVisible(_admin, _f15);
        Assert.Equal(ShareState.Active, Ask(_admin, _f15, _kid2).State);
        Assert.Equal(ShareState.Active, Ask(_adult, _e18, _kid2).State);
        Assert.DoesNotContain(_store.All(), r => r.State == ShareState.Pending);
    }

    [Fact]
    public void Each_kind_of_restriction_the_playlists_count_makes_a_profile_wait()
    {
        var restricted = new[]
        {
            new UserInfo(_adult, "Alex", false, false, true, null, null, true, new List<Guid>(), Array.Empty<string>(), Array.Empty<string>(), BlocksUnrated: true),
            new UserInfo(_adult, "Alex", false, false, true, null, null, true, new List<Guid>(), new[] { "gore" }, Array.Empty<string>()),
            new UserInfo(_adult, "Alex", false, false, true, null, null, true, new List<Guid>(), Array.Empty<string>(), new[] { "family" }),
            new UserInfo(_adult, "Alex", false, false, true, null, null, false, new List<Guid>(), Array.Empty<string>(), Array.Empty<string>()),
        };
        foreach (var u in restricted)
        {
            _gw.Users[_adult] = u;
            Assert.True(SharePlaylistManager.IsRestricted(u, _gw.RealLibraryIds));
            Assert.True(Service().NeedsApproval(u));
        }

        // The plugin's own changes (an explicit list of every real library, swm- tags) are not a restriction.
        var isolated = new UserInfo(_adult, "Alex", false, false, true, null, null, false, new List<Guid> { _lib, Guid.NewGuid() }, new[] { LibraryAccessManager.RecipientTag(_kid1) }, Array.Empty<string>());
        Assert.False(Service().NeedsApproval(isolated));
    }

    [Fact]
    public void With_approval_off_a_restricted_profile_shares_at_once_as_before()
    {
        _mode = ApprovalMode.None;
        var record = Ask(_kid1, _f15, _kid2);

        Assert.Equal(ShareState.Active, record.State);
        Assert.NotEqual(string.Empty, record.FolderName);
        Assert.Single(_gw.CreatedLibraries);
    }

    [Fact]
    public void A_doomed_share_is_refused_at_once_with_the_same_answer_whether_approval_is_on_or_off()
    {
        var blocked = Guid.NewGuid();
        AddFilm(blocked, "Spike Gore", "GB-15");
        _gw.Items[blocked] = _gw.Items[blocked] with { Tags = new List<string> { "gore" } };
        _gw.SetVisible(_kid1, blocked);
        _gw.Users[_kid2] = _gw.Users[_kid2] with { BlockedTags = new[] { "gore" } };
        var cases = new (Guid Sharer, Guid Item, Guid Recipient, DateTime? Expires)[]
        {
            (_kid1, _e18, _kid2, null), // the sharer cannot see it
            (_kid1, Guid.NewGuid(), _kid2, null), // no such item
            (_kid1, _pg, _kid2, null), // the recipient can already see it
            (_kid1, blocked, _kid2, null), // the recipient blocks its tag
            (_kid1, _f15, _kid1, null), // the sharer
            (_kid1, _f15, Guid.NewGuid(), null), // no such recipient
            (_kid1, _f15, _kid2, _now.AddMinutes(-1)), // an end time in the past
            (_kid1, _f15, _kid2, _now.AddDays(400)), // too far ahead
        };

        foreach (var (sharer, item, recipient, expires) in cases)
        {
            _mode = ApprovalMode.None;
            var off = Service().Create(sharer, item, recipient, expires);
            _mode = ApprovalMode.LimitedProfiles;
            var on = Service().Create(sharer, item, recipient, expires);

            Assert.NotEqual(Refusal.None, on.decision.Refusal);
            Assert.Equal((off.decision.Refusal, off.decision.Message), (on.decision.Refusal, on.decision.Message));
            Assert.Null(on.record);
        }

        _who = WhoMayShare.AdminsOnly;
        Assert.Equal(Refusal.SharingNotAllowed, Service().Create(_kid1, _f15, _kid2).decision.Refusal);
        Assert.Empty(_store.All());
        Assert.Equal(0, Writes());
    }

    [Fact]
    public void Asking_twice_is_refused_but_someone_else_may_still_share_the_title()
    {
        var first = Ask(_kid1, _f15, _kid2);
        var (again, none) = Service().Create(_kid1, _f15, _kid2);
        Assert.Equal(Refusal.AlreadyShared, again.Refusal);
        Assert.Contains("waiting for approval", again.Message, StringComparison.Ordinal);
        Assert.Null(none);

        var adult = Ask(_adult, _f15, _kid2); // a pending request gives Jo nothing yet, so it does not stop Alex
        Assert.Equal(ShareState.Active, adult.State);
        Assert.Equal(ShareState.Pending, Stored(first.Id).State);
    }

    // ---- approve ----

    [Fact]
    public void Approve_builds_the_share_through_the_normal_path_and_keeps_its_id_and_times()
    {
        var expires = _now.AddDays(7);
        var pending = Ask(_kid1, _f15, _kid2, expires);
        _now = _now.AddHours(5);

        var outcome = Service().Approve(pending.Id, _admin);

        Assert.Equal(ApprovalResult.Approved, outcome.Result);
        var active = Stored(pending.Id);
        Assert.Equal(outcome.Record, active with { ProviderIds = outcome.Record!.ProviderIds });
        Assert.Equal(ShareState.Active, active.State);
        Assert.Null(active.StateReason);
        Assert.Equal(pending.CreatedUtc, active.CreatedUtc);
        Assert.Equal(expires, active.ExpiresUtc); // the clock started when Sam asked
        var folder = Path.Combine(RecipientFolder(_kid2), "movies", active.FolderName);
        Assert.True(Directory.Exists(folder));
        Assert.Contains("<customrating>12</customrating>", File.ReadAllText(Path.Combine(folder, active.FolderName + ".nfo")), StringComparison.Ordinal);
        var lib = Assert.Single(_gw.CreatedLibraries);
        Assert.Contains(lib.Id, _gw.Users[_kid2].EnabledLibraries);
        Assert.DoesNotContain(lib.Id, _gw.Users[_kid1].EnabledLibraries);
        Assert.Contains(LibraryAccessManager.RecipientTag(_kid2), _gw.Users[_kid1].BlockedTags);
        Assert.Equal(new[] { lib.Id }, _gw.ScanQueue);
        Assert.Equal(ApprovalResult.NotFound, Service().Approve(pending.Id, _admin).Result); // no longer waiting
    }

    [Fact]
    public void Only_an_enabled_administrator_may_approve_or_decline()
    {
        var pending = Ask(_kid1, _f15, _kid2);
        var disabled = Guid.NewGuid();
        _gw.Users[disabled] = _gw.Users[_admin] with { Id = disabled, IsDisabled = true };

        foreach (var who in new[] { _kid1, _kid2, _adult, disabled, Guid.NewGuid() })
        {
            Assert.Equal(ApprovalResult.NotFound, Service().Approve(pending.Id, who).Result);
            Assert.Null(Service().Decline(pending.Id, who, "no"));
        }

        Assert.Equal(ShareState.Pending, Stored(pending.Id).State);
        Assert.Equal(0, Writes());
        Assert.Equal(ApprovalResult.NotFound, Service().Approve(Guid.NewGuid(), _admin).Result);
        Assert.Null(Service().Decline(Guid.NewGuid(), _admin, null));
    }

    [Fact]
    public void Approve_checks_every_rule_again_and_closes_a_share_that_is_no_longer_possible()
    {
        var seen = Ask(_kid1, _f15, _kid2);
        var tagged = Ask(_kid1, _f15, _kid3);
        _gw.SetVisible(_kid2, _f15); // Jo's limit was raised meanwhile
        _gw.Users[_kid3] = _gw.Users[_kid3] with { BlockedTags = new[] { "gore" } }; // Kai now blocks a tag the title got
        _gw.Items[_f15] = _gw.Items[_f15] with { Tags = new List<string> { "gore" } };
        Assert.Equal(ApprovalResult.Closed, Service().Approve(tagged.Id, _admin).Result);
        Assert.Equal(ShareService.NoLongerPossiblePrefix + "Kai's settings block Spike Fifteen", Stored(tagged.Id).StateReason);

        var outcome = Service().Approve(seen.Id, _admin);

        Assert.Equal(ApprovalResult.Closed, outcome.Result);
        Assert.Contains("no longer possible", outcome.Message, StringComparison.Ordinal);
        var closed = Stored(seen.Id);
        Assert.Equal(ShareState.Closed, closed.State);
        Assert.Equal(ShareService.NoLongerPossiblePrefix + "Jo can already watch Spike Fifteen", closed.StateReason);
        Assert.Equal(_now, closed.ClosedUtc);
        Assert.Empty(_gw.CreatedLibraries);
        Assert.False(Directory.Exists(RecipientFolder(_kid2)));
    }

    [Fact]
    public void Approve_closes_the_share_when_the_sharer_may_no_longer_share_or_see_the_title()
    {
        var a = Ask(_kid1, _f15, _kid2);
        _gw.SetVisible(_kid1, _f15, false); // Sam's own limit was lowered
        Assert.Equal(ApprovalResult.Closed, Service().Approve(a.Id, _admin).Result);
        Assert.Equal(ShareService.NoLongerPossiblePrefix + "You can't share that title", Stored(a.Id).StateReason);

        _gw.SetVisible(_kid1, _f15);
        var b = Ask(_kid1, _f15, _kid2);
        _who = WhoMayShare.AdminsOnly;
        Assert.Equal(ApprovalResult.Closed, Service().Approve(b.Id, _admin).Result);
        Assert.StartsWith(ShareService.NoLongerPossiblePrefix, Stored(b.Id).StateReason, StringComparison.Ordinal);
        Assert.Empty(_gw.CreatedLibraries);
    }

    [Fact]
    public void A_pending_share_does_not_expire_and_an_approval_after_its_end_time_closes_it_as_expired()
    {
        var pending = Ask(_kid1, _f15, _kid2, _now.AddHours(1));
        _now = _now.AddHours(2);
        Assert.False(Stored(pending.Id).IsExpired(_now));
        Sync().Run();
        Assert.Equal(ShareState.Pending, Stored(pending.Id).State);
        Assert.False(new ExpiryWatch(_store, NullLogger.Instance, () => _now).Due());

        var outcome = Service().Approve(pending.Id, _admin);

        Assert.Equal(ApprovalResult.Closed, outcome.Result);
        Assert.Equal(ShareService.ExpiredWhileWaitingMessage, outcome.Message);
        Assert.Equal((ShareState.Closed, ShareSynchronizer.ExpiredReason), (Stored(pending.Id).State, Stored(pending.Id).StateReason));
        Assert.Empty(_gw.CreatedLibraries);
    }

    [Fact]
    public void An_approval_whose_build_fails_leaves_the_share_waiting_so_it_can_be_approved_again()
    {
        var pending = Ask(_kid1, _f15, _kid2);
        _gw.ThrowOnce = nameof(IServerGateway.CreateLibrary);

        var outcome = Service().Approve(pending.Id, _admin);

        Assert.Equal(ApprovalResult.Failed, outcome.Result);
        Assert.Equal(ShareState.Pending, Stored(pending.Id).State);
        Assert.Equal(ApprovalResult.Approved, Service().Approve(pending.Id, _admin).Result);
        Assert.Equal(ShareState.Active, Stored(pending.Id).State);
    }

    // ---- decline and withdraw ----

    [Fact]
    public void Decline_closes_the_share_with_a_plain_note_and_changes_nothing_else()
    {
        var pending = Ask(_kid1, _f15, _kid2);
        var note = "  Not this one,\r\nmaybe <b>next</b> week.  " + new string('x', 300);

        var closed = Service().Decline(pending.Id, _admin, note);

        Assert.NotNull(closed);
        var stored = Stored(pending.Id);
        Assert.Equal((ShareState.Closed, ShareService.DeclinedReason), (stored.State, stored.StateReason));
        Assert.Equal(_now, stored.ClosedUtc);
        Assert.StartsWith("Not this one, maybe <b>next</b> week. xxx", stored.DeclineNote, StringComparison.Ordinal);
        Assert.Equal(ShareService.DeclineNoteMaxLength, stored.DeclineNote!.Length);
        Assert.DoesNotContain(stored.DeclineNote, char.IsControl);
        Assert.Equal(0, Writes());
        Assert.Null(Service().Decline(pending.Id, _admin, null)); // no longer waiting
        Assert.Equal(ApprovalResult.NotFound, Service().Approve(pending.Id, _admin).Result);

        var other = Ask(_kid1, _f15, _kid2); // a declined request does not stop a new one
        Assert.Null(Assert.IsType<ShareRecord>(Service().Decline(other.Id, _admin, "   ")).DeclineNote);
        Assert.Null(ShareService.CleanNote("\n\t"));
    }

    [Fact]
    public void The_sharer_can_withdraw_a_pending_share_and_nothing_else_changes()
    {
        var pending = Ask(_kid1, _f15, _kid2);
        Assert.False(Service().Remove(pending.Id, _kid2)); // only the sharer or an administrator

        Assert.True(Service().Remove(pending.Id, _kid1));

        Assert.Empty(_store.All());
        Assert.Equal(0, Writes());
    }

    // ---- what the others see ----

    [Fact]
    public void The_recipient_never_sees_a_pending_share_and_the_sync_never_builds_it()
    {
        var pending = Ask(_kid1, _f15, _kid2);
        var active = Ask(_adult, _e18, _kid1); // something real, so the plugin is in charge and the sync runs every rule

        for (var i = 0; i < 3; i++)
        {
            var report = Sync().Run();
            Assert.Equal(1, report.Checked); // the pending share is not checked: nothing of it exists
        }

        Assert.Equal(ShareState.Pending, Stored(pending.Id).State);
        Assert.Equal(string.Empty, Stored(pending.Id).FolderName);
        Assert.False(Directory.Exists(RecipientFolder(_kid2)));
        Assert.False(_store.Libraries().ContainsKey(_kid2));
        Assert.DoesNotContain(_gw.Users.Values, u => u.BlockedTags.Contains(LibraryAccessManager.RecipientTag(_kid2)));
        Assert.Empty(Audit().Received(_kid2));
        Assert.Contains(Audit().SharedBy(_kid1).Shares, r => r.Id == pending.Id);

        var report2 = Audit().Build();
        var waiting = Assert.Single(report2.AwaitingApproval);
        Assert.Equal((pending.Id, "Sam", "Jo", false, false), (waiting.Id, waiting.SharerName, waiting.RecipientName, waiting.ViaPlaylist, waiting.EndTimePassed));
        Assert.Empty(report2.Profiles.Single(p => p.User.Id == _kid2).Shares);
        Assert.Contains(report2.Profiles.Single(p => p.User.Id == _kid1).Shares, s => s.Id == active.Id);
    }

    [Fact]
    public void A_pending_share_whose_recipient_is_deleted_goes_with_them()
    {
        Ask(_adult, _e18, _kid1);
        Ask(_kid1, _f15, _kid2);
        _gw.Users.Remove(_kid2);

        Sync().Run();

        Assert.DoesNotContain(_store.All(), r => r.RecipientUserId == _kid2);
    }

    // ---- restore and uninstall ----

    [Fact]
    public void Pending_shares_never_stop_a_restore_and_are_closed_by_it()
    {
        var done = Ask(_adult, _e18, _kid1);
        var pending = Ask(_kid1, _f15, _kid2);
        Assert.Equal(AuditService.SharesExistMessage, Audit().Restore().Message); // an Active share still does

        Assert.True(Service().Remove(done.Id, _adult));
        var outcome = Audit().Restore();

        Assert.True(outcome.Done, outcome.Message);
        Assert.Equal((ShareState.Closed, ShareSynchronizer.SwitchedOffReason), (Stored(pending.Id).State, Stored(pending.Id).StateReason));
        Assert.All(_gw.Users.Values, u => Assert.True(u.AllLibraries));
        Assert.Equal(ApprovalResult.NotFound, Service().Approve(pending.Id, _admin).Result);
    }

    [Fact]
    public void A_restore_with_only_pending_shares_has_nothing_to_wait_for()
    {
        Ask(_kid1, _f15, _kid2);
        Assert.True(Audit().Restore().Done);
        Assert.DoesNotContain(_store.All(), r => r.State == ShareState.Pending);
    }

    [Fact]
    public void The_uninstall_discards_pending_shares()
    {
        Ask(_adult, _e18, _kid1);
        var pending = Ask(_kid1, _f15, _kid2);

        var outcome = Audit().Uninstall();

        Assert.True(outcome.Done, outcome.Message);
        var kept = Directory.GetFiles(Path.Combine(_dir, "store"), "shares.uninstalled-*.json").Single();
        var records = JsonDocument.Parse(File.ReadAllText(kept)).RootElement.GetProperty("Shares").EnumerateArray().ToList();
        var mine = records.Single(r => r.GetProperty("Id").GetGuid() == pending.Id);
        Assert.Equal(("Closed", AuditService.UninstalledReason), (mine.GetProperty("State").GetString(), mine.GetProperty("StateReason").GetString()));
        Assert.DoesNotContain(records, r => r.GetProperty("State").GetString() == "Pending");
        Assert.All(_gw.Users.Values, u => Assert.True(u.AllLibraries));
    }

    // ---- the TV playlists ----

    [Fact]
    public void A_title_put_in_a_playlist_waits_with_its_entry_kept_and_no_second_request()
    {
        _tvDays = 2;
        Playlists().EnsurePlaylists();
        var before = Writes();
        Entries(_kid1, _kid2).Add(_f15);

        for (var i = 0; i < 3; i++)
        {
            Playlists().Reconcile();
            Sync().Run();
            Playlists().Run(full: true);
        }

        var pending = Assert.Single(_store.All());
        Assert.Equal((ShareState.Pending, true), (pending.State, pending.ViaPlaylist));
        Assert.Equal(_now.AddDays(2), pending.ExpiresUtc);
        Assert.Equal(new[] { _f15 }, Entries(_kid1, _kid2));
        Assert.Empty(_store.RefusalNotes());
        Assert.Equal(before, Writes());
        Assert.Single(Sent(), b => b.Event == ShareNotifier.ApprovalNeededEvent); // one request, one message

        Assert.Equal(ApprovalResult.Approved, Service().Approve(pending.Id, _admin).Result);
        Playlists().Run(full: true);
        var active = Assert.Single(_store.All());
        Assert.Equal((pending.Id, ShareState.Active, true), (active.Id, active.State, active.ViaPlaylist));
        Assert.Equal(new[] { _f15 }, Entries(_kid1, _kid2));
    }

    [Fact]
    public void A_declined_playlist_share_leaves_the_playlist_and_never_comes_back()
    {
        Playlists().EnsurePlaylists();
        Entries(_kid1, _kid2).Add(_f15);
        Playlists().Reconcile();
        var pending = Assert.Single(_store.All());

        Service().Decline(pending.Id, _admin, "Ask me first");
        Assert.Empty(Entries(_kid1, _kid2));
        Playlists().Reconcile();
        Sync().Run();
        Playlists().Run(full: true);

        var closed = Assert.Single(_store.All());
        Assert.Equal((ShareState.Closed, ShareService.DeclinedReason, false), (closed.State, closed.StateReason, closed.ViaPlaylist));
        Assert.Empty(Entries(_kid1, _kid2));
    }

    [Fact]
    public void A_declined_share_whose_entry_the_server_keeps_is_not_asked_for_again()
    {
        Playlists().EnsurePlaylists();
        Entries(_kid1, _kid2).Add(_f15);
        Playlists().Reconcile();
        var pending = Assert.Single(_store.All());
        _gw.IgnoreRemoveFromPlaylist = true;

        Service().Decline(pending.Id, _admin, null);
        for (var i = 0; i < 3; i++)
        {
            Sync().Run();
            Playlists().Run(full: true);
        }

        var kept = Assert.Single(_store.All());
        Assert.Equal((pending.Id, ShareState.Closed, true), (kept.Id, kept.State, kept.ViaPlaylist));
        Assert.Equal(new[] { _f15 }, Entries(_kid1, _kid2));

        _gw.IgnoreRemoveFromPlaylist = false;
        Playlists().Reconcile();
        Assert.Empty(Entries(_kid1, _kid2));
        Assert.False(Assert.Single(_store.All()).ViaPlaylist);
    }

    [Fact]
    public void Approval_found_no_longer_possible_lets_go_of_the_playlist_the_same_way()
    {
        var closed = new ShareRecord(Guid.NewGuid(), _f15, ShareKind.Movie, "t", null, new(), _lib, _kid1, "Sam", _kid2, _now, ShareState.Closed, ShareService.NoLongerPossiblePrefix + "x", string.Empty, ViaPlaylist: true);
        Assert.True(SharePlaylistManager.LetsGoOnlyWhenGone(closed));
        Assert.True(SharePlaylistManager.LetsGoOnlyWhenGone(closed with { StateReason = ShareService.DeclinedReason }));
        Assert.True(SharePlaylistManager.LetsGoOnlyWhenGone(closed with { StateReason = ShareSynchronizer.ExpiredReason }));
        Assert.False(SharePlaylistManager.LetsGoOnlyWhenGone(closed with { StateReason = ShareSynchronizer.NoLongerNeededReason }));
        Assert.False(SharePlaylistManager.LetsGoOnlyWhenGone(closed with { State = ShareState.Pending, StateReason = null }));
    }

    [Fact]
    public void Taking_a_pending_title_out_of_the_playlist_withdraws_it()
    {
        Playlists().EnsurePlaylists();
        Entries(_kid1, _kid2).Add(_f15);
        Playlists().Reconcile();
        Assert.Single(_store.All());

        Entries(_kid1, _kid2).Clear();
        Playlists().Reconcile();

        Assert.Empty(_store.All());
    }

    [Fact]
    public void Withdrawing_on_the_share_page_takes_the_entry_out_of_the_playlist()
    {
        Playlists().EnsurePlaylists();
        Entries(_kid1, _kid2).Add(_f15);
        Playlists().Reconcile();

        Assert.True(Service().Remove(Assert.Single(_store.All()).Id, _kid1));
        Playlists().Reconcile();

        Assert.Empty(_store.All());
        Assert.Empty(Entries(_kid1, _kid2));
    }

    // ---- notifications ----

    [Theory]
    [InlineData(NotifyOn.Nothing, "")]
    [InlineData(NotifyOn.ApprovalsOnly, "approval-needed,approved,approval-needed,declined")]
    [InlineData(NotifyOn.EverySharing, "approval-needed,approved,approval-needed,declined,shared")]
    public void Notifications_follow_the_setting(NotifyOn on, string expected)
    {
        _on = on;
        Service().Approve(Ask(_kid1, _f15, _kid2).Id, _admin);
        Service().Decline(Ask(_kid1, _f15, _kid3).Id, _admin, "no reason sent");
        Ask(_adult, _e18, _kid2);

        Assert.Equal(expected, string.Join(",", Sent().Select(b => b.Event)));
    }

    [Fact]
    public void A_message_names_the_people_and_the_title_and_nothing_else()
    {
        _on = NotifyOn.EverySharing;
        var pending = Ask(_kid1, _f15, _kid2, _now.AddDays(1));
        Service().Decline(pending.Id, _admin, "secret note for Sam");
        Ask(_adult, _e18, _kid2);

        var sent = Sent();
        Assert.Equal(
            new[]
            {
                new NotifyBody("approval-needed", "Sam is waiting for approval to share Spike Fifteen with Jo.", "Sam", "Jo", "Spike Fifteen"),
                new NotifyBody("declined", "Sam may not share Spike Fifteen with Jo: it was declined.", "Sam", "Jo", "Spike Fifteen"),
                new NotifyBody("shared", "Alex shared Spike Eighteen with Jo.", "Alex", "Jo", "Spike Eighteen"),
            },
            sent);
        var raw = string.Join("\n", _sent.Select(s => s.Body));
        Assert.DoesNotContain(_media, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(_shareRoot, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("secret note", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("2002", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(_kid1.ToString("N"), raw, StringComparison.Ordinal);
        Assert.All(_sent, s => Assert.Equal(new Uri(Hook), s.Uri));
        Assert.Equal(
            new[] { "event", "message", "sharer", "recipient", "title" },
            JsonDocument.Parse(_sent.First().Body).RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Sam may now share Spike Fifteen with Jo: it was approved.", ShareNotifier.MessageFor(ShareNotifier.ApprovedEvent, "Sam", "Jo", "Spike Fifteen"));
    }

    [Fact]
    public async Task A_failing_notification_never_fails_or_holds_up_a_share()
    {
        _on = NotifyOn.EverySharing;
        _post = (_, _, _) => throw new HttpRequestExceptionStandIn();
        Assert.Equal(ShareState.Active, Ask(_adult, _e18, _kid2).State);
        var pending = Ask(_kid1, _f15, _kid2);
        Assert.Equal(ApprovalResult.Approved, Service().Approve(pending.Id, _admin).Result);
        await Notifier().WhenIdle().WaitAsync(TimeSpan.FromSeconds(10));

        // One that never answers: the share returns at once, and the attempt is given up after the timeout.
        _notifier = null;
        var started = 0;
        _post = async (_, _, cancel) =>
        {
            Interlocked.Increment(ref started);
            await Task.Delay(Timeout.Infinite, cancel);
            return 200;
        };
        var clock = Stopwatch.StartNew();
        Assert.Equal(ShareState.Active, Ask(_adult, _f15, _kid3).State);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), clock.Elapsed.ToString());
        await Notifier().WhenIdle().WaitAsync(TimeSpan.FromSeconds(9)); // throws TimeoutException if it was not given up
        Assert.Equal(1, started); // one attempt, no retry
    }

    [Fact]
    public void No_message_without_a_valid_address()
    {
        _on = NotifyOn.EverySharing;
        foreach (var url in new[] { null, string.Empty, "ftp://example.org/x", "file:///etc/passwd", "example.org/hook", "http:///nohost", "http://exa mple.org/" })
        {
            _url = url;
            Ask(_adult, _e18, _kid2);
            Service().Remove(_store.All().Single().Id, _adult);
        }

        Assert.Empty(Sent());
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("http://10.1.2.3:8123/api/webhook/abc", true)]
    [InlineData("https://ntfy.example.org/family?x=1", true)]
    [InlineData("HTTPS://EXAMPLE.ORG", true)]
    [InlineData("ftp://example.org", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("/relative/path", false)]
    [InlineData("http://", false)]
    [InlineData("http://exa mple.org", false)]
    [InlineData("http://example.org/\nX", false)]
    public void Only_absolute_http_and_https_addresses_are_accepted(string url, bool ok)
    {
        Assert.Equal(ok, PluginConfiguration.IsValidNotifyUrl(url));
    }

    [Fact]
    public void The_log_names_only_the_scheme_host_and_port_of_the_address()
    {
        Assert.Equal("https://hooks.example.org", ShareNotifier.SafeAddress(new Uri(Hook)));
        Assert.Equal("http://10.1.2.3:8123", ShareNotifier.SafeAddress(new Uri("http://user:pw@10.1.2.3:8123/api/webhook/secret?k=v#f")));
    }

    [Theory]
    [InlineData(NotifyOn.Nothing, "approval-needed", false)]
    [InlineData(NotifyOn.Nothing, "shared", false)]
    [InlineData(NotifyOn.ApprovalsOnly, "approval-needed", true)]
    [InlineData(NotifyOn.ApprovalsOnly, "approved", true)]
    [InlineData(NotifyOn.ApprovalsOnly, "declined", true)]
    [InlineData(NotifyOn.ApprovalsOnly, "shared", false)]
    [InlineData(NotifyOn.EverySharing, "approval-needed", true)]
    [InlineData(NotifyOn.EverySharing, "approved", true)]
    [InlineData(NotifyOn.EverySharing, "declined", true)]
    [InlineData(NotifyOn.EverySharing, "shared", true)]
    [InlineData(NotifyOn.EverySharing, "test", false)]
    public void Which_events_each_setting_sends(NotifyOn on, string eventName, bool sent)
    {
        Assert.Equal(sent, ShareNotifier.Wanted(on, eventName));
    }

    [Fact]
    public async Task The_test_message_is_sent_whatever_notify_on_says_and_answers_in_plain_words()
    {
        _on = NotifyOn.Nothing;
        var (sent, message) = await Notifier().SendTestAsync();
        Assert.True(sent, message);
        var body = JsonSerializer.Deserialize<NotifyBody>(Assert.Single(_sent).Body)!;
        Assert.Equal(("test", "Shared With Me test message."), (body.Event, body.Message));

        _notifier = null;
        _post = (_, _, _) => Task.FromResult(500);
        (sent, message) = await Notifier().SendTestAsync();
        Assert.False(sent);
        Assert.Contains("500", message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", message, StringComparison.Ordinal);

        _notifier = null;
        _url = string.Empty;
        (sent, message) = await Notifier().SendTestAsync();
        Assert.False(sent);
        Assert.Contains("No notification address", message, StringComparison.Ordinal);
    }

    // ---- settings and the record of shares ----

    [Fact]
    public void Approval_and_notification_settings_survive_a_save_and_load_and_default_to_off()
    {
        var defaults = new PluginConfiguration();
        Assert.Equal((ApprovalMode.None, string.Empty, NotifyOn.Nothing), (defaults.ApprovalMode, defaults.NotifyUrl, defaults.NotifyOn));

        var config = new PluginConfiguration { ApprovalMode = ApprovalMode.LimitedProfiles, NotifyUrl = Hook, NotifyOn = NotifyOn.EverySharing };
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var xml = new StringWriter();
        serializer.Serialize(xml, config);
        var back = (PluginConfiguration)serializer.Deserialize(new StringReader(xml.ToString()))!;
        Assert.Equal((ApprovalMode.LimitedProfiles, Hook, NotifyOn.EverySharing), (back.ApprovalMode, back.NotifyUrl, back.NotifyOn));

        // A settings file of 0.1 or 0.2 A/B has none of them: approval and notifications are off.
        const string old = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            + "<PluginConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n"
            + "  <ShareRoot />\n  <WhoMayShare>Everyone</WhoMayShare>\n  <TvSharingPlaylists>true</TvSharingPlaylists>\n  <TvShareDays>3</TvShareDays>\n"
            + "</PluginConfiguration>\n";
        var older = (PluginConfiguration)serializer.Deserialize(new StringReader(old))!;
        Assert.Equal((ApprovalMode.None, string.Empty, NotifyOn.Nothing, 3), (older.ApprovalMode, older.NotifyUrl, older.NotifyOn, older.TvShareDays));
    }

    [Fact]
    public void A_pending_share_and_its_decline_note_survive_a_new_store_and_an_older_record_has_no_note()
    {
        var pending = Ask(_kid1, _f15, _kid2);
        Service().Decline(Ask(_kid1, _f15, _kid3).Id, _admin, "not yet");

        var again = new ShareStore(Path.Combine(_dir, "store")).All();
        Assert.Equal(ShareState.Pending, again.Single(r => r.Id == pending.Id).State);
        Assert.Equal("not yet", again.Single(r => r.RecipientUserId == _kid3).DeclineNote);
        Assert.Contains("\"State\": \"Pending\"", File.ReadAllText(Path.Combine(_dir, "store", "shares.json")), StringComparison.Ordinal);
        Assert.Null(new ShareStore(Path.Combine(_dir, "store")).All().Single(r => r.Id == pending.Id).DeclineNote);
    }

    // What 0.1 and 0.2 A/B see in a record holding Pending: a state name they do not know, so the whole file is unreadable
    // to them (sharing pauses, the safe direction). Shown here with a state name this version does not know.
    [Fact]
    public void A_state_name_a_version_does_not_know_makes_the_whole_record_unreadable()
    {
        var file = Path.Combine(_dir, "store", "shares.json");
        Ask(_kid1, _f15, _kid2);
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"Pending\"", "\"AwaitingSomethingNewer\"", StringComparison.Ordinal));

        var store = new ShareStore(Path.Combine(_dir, "store"));
        Assert.False(store.IsReadable(out var why));
        Assert.Contains("corrupt", why, StringComparison.Ordinal);
    }

    private sealed class HttpRequestExceptionStandIn : System.Net.Http.HttpRequestException
    {
        public HttpRequestExceptionStandIn()
            : base("Connection refused (hooks.example.org:443)")
        {
        }
    }
}
