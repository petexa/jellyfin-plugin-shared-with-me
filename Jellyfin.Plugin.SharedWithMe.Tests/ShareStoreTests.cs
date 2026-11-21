using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.SharedWithMe.Model;
using Jellyfin.Plugin.SharedWithMe.Storage;
using Xunit;

namespace Jellyfin.Plugin.SharedWithMe.Tests;

public sealed class ShareStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "swm-" + Guid.NewGuid().ToString("N"));

    public ShareStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string FilePath => Path.Combine(_dir, "shares.json");

    private static ShareRecord Record(Guid? id = null, string title = "Heat") => new(
        id ?? Guid.NewGuid(),
        Guid.NewGuid(),
        ShareKind.Movie,
        title,
        1995,
        new Dictionary<string, string> { ["Imdb"] = "tt0113277" },
        Guid.NewGuid(),
        Guid.NewGuid(),
        "Adult",
        Guid.NewGuid(),
        new DateTime(2026, 11, 19, 8, 0, 0, DateTimeKind.Utc),
        ShareState.Active,
        null,
        "Heat (1995) [Adult]");

    [Fact]
    public void Round_trip_survives_a_new_store_on_the_same_path()
    {
        var record = Record();
        var snapshot = new PolicySnapshot(Guid.NewGuid(), true, new DateTime(2026, 11, 19, 9, 0, 0, DateTimeKind.Utc));
        var recipient = Guid.NewGuid();
        var libraryId = Guid.NewGuid();

        var first = new ShareStore(_dir);
        first.Upsert(record);
        first.UpsertSnapshot(snapshot);
        first.SetLibrary(recipient, ShareKind.Series, libraryId);

        var second = new ShareStore(_dir);
        Assert.Equal(record, Assert.Single(second.All()), new RecordComparer());
        Assert.Equal(snapshot, Assert.Single(second.Snapshots()));
        Assert.Equal(libraryId, second.Libraries()[recipient]["Series"]);
    }

    [Fact]
    public void A_file_written_by_the_previous_version_loads_with_zero_counters_and_no_closed_time()
    {
        // The shape Task 6 wrote: no FailedChecks, MissingChecks, ClosedUtc or KnownRealLibraries.
        File.WriteAllText(FilePath, """
            {
              "Shares": [
                {
                  "Id": "6f1c0f5e-7c43-4a59-9d0c-1d5c9f0b1a01",
                  "OriginalItemId": "0b9a2c7e-1f6d-4c1a-8a51-2b8e1d7c6f02",
                  "Kind": "Movie",
                  "Title": "Heat",
                  "Year": 1995,
                  "ProviderIds": { "Imdb": "tt0113277" },
                  "SourceLibraryId": "3c2d1e0f-9a8b-4c7d-8e6f-5a4b3c2d1e03",
                  "SharerUserId": "4d3e2f10-0b9c-4d8e-9f70-6b5c4d3e2f04",
                  "SharerName": "Adult",
                  "RecipientUserId": "5e4f3021-1cad-4e9f-a081-7c6d5e4f3005",
                  "CreatedUtc": "2026-11-19T08:00:00Z",
                  "State": "Broken",
                  "StateReason": "could not remove the shared copy",
                  "FolderName": "Heat (1995) [swm-6f1c0f5e]"
                }
              ],
              "Snapshots": [],
              "Libraries": {}
            }
            """);

        var store = new ShareStore(_dir);
        var r = Assert.Single(store.All());

        Assert.Equal(0, r.FailedChecks);
        Assert.Equal(0, r.MissingChecks);
        Assert.Equal(0, r.EmptyChecks); // a store written before Task 9's fix round 1 has no EmptyChecks
        Assert.Null(r.ClosedUtc);
        Assert.Equal("Heat (1995) [swm-6f1c0f5e]", r.FolderName);
        Assert.Equal(ShareState.Broken, r.State);
        Assert.Null(store.KnownRealLibraries());
        Assert.False(r.ViaPlaylist); // a store written before Task 11 has no ViaPlaylist, no playlists and no notes
        Assert.Empty(store.SharePlaylists());
        Assert.Empty(store.RefusalNotes());

        store.Upsert(r with { FailedChecks = 2, MissingChecks = 1, ClosedUtc = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc) });
        store.SetKnownRealLibraries(new[] { r.SourceLibraryId });
        var again = Assert.Single(new ShareStore(_dir).All());
        Assert.Equal(2, again.FailedChecks);
        Assert.Equal(1, again.MissingChecks);
        Assert.Equal(new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc), again.ClosedUtc);
        Assert.Equal(new[] { r.SourceLibraryId }, new ShareStore(_dir).KnownRealLibraries());
    }

    [Fact]
    public void Playlist_map_and_via_playlist_survive_a_new_store()
    {
        var (sharer, kid1, kid2) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (p1, p2) = (Guid.NewGuid(), Guid.NewGuid());
        var store = new ShareStore(_dir);
        store.SetSharePlaylist(sharer, kid1, p1);
        store.SetSharePlaylist(sharer, kid2, p2);
        store.Upsert(Record() with { ViaPlaylist = true });

        var again = new ShareStore(_dir);
        Assert.Equal(p1, again.SharePlaylists()[sharer][kid1]);
        Assert.Equal(p2, again.SharePlaylists()[sharer][kid2]);
        Assert.True(Assert.Single(again.All()).ViaPlaylist);

        again.SetSharePlaylist(sharer, kid1, null);
        again.SetSharePlaylist(sharer, kid2, null);
        Assert.Empty(new ShareStore(_dir).SharePlaylists());
    }

    [Fact]
    public void Refusal_notes_keep_the_newest_50_per_sharer_and_purge_by_age()
    {
        var (a, b, r) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var t0 = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var store = new ShareStore(_dir);
        for (var i = 0; i < 55; i++)
        {
            store.AddRefusalNote(new RefusalNote(a, r, "T" + i, "m", t0.AddMinutes(i)));
        }

        store.AddRefusalNote(new RefusalNote(b, r, "B", "m", t0));
        var notes = new ShareStore(_dir).RefusalNotes();
        Assert.Equal(50, notes.Count(n => n.SharerUserId == a));
        Assert.DoesNotContain(notes, n => n.Title == "T4");
        Assert.Contains(notes, n => n.Title == "T5");
        Assert.Contains(notes, n => n.Title == "B"); // another sharer's notes are not counted against a
        Assert.Equal("T54", notes[0].Title); // newest first

        Assert.Equal(1 + 5, store.PurgeRefusalNotes(t0.AddMinutes(10))); // B and T5..T9
        Assert.Equal(45, store.RefusalNotes().Count);
        store.ClearRefusalNotes(a);
        Assert.Empty(store.RefusalNotes());
    }

    [Fact]
    public void Upsert_twice_with_one_id_leaves_one_record()
    {
        var id = Guid.NewGuid();
        var store = new ShareStore(_dir);

        store.Upsert(Record(id, "Old"));
        store.Upsert(Record(id, "New"));

        Assert.Equal("New", Assert.Single(new ShareStore(_dir).All()).Title);
    }

    [Fact]
    public void Remove_and_snapshot_and_library_removal_work()
    {
        var store = new ShareStore(_dir);
        var record = Record();
        var userId = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        store.Upsert(record);
        store.UpsertSnapshot(new PolicySnapshot(userId, false, DateTime.UtcNow));
        store.SetLibrary(recipient, ShareKind.Movie, Guid.NewGuid());

        store.Remove(record.Id);
        store.RemoveSnapshot(userId);
        store.SetLibrary(recipient, ShareKind.Movie, null);

        var reloaded = new ShareStore(_dir);
        Assert.Empty(reloaded.All());
        Assert.Empty(reloaded.Snapshots());
        Assert.False(reloaded.Libraries().TryGetValue(recipient, out var kinds) && kinds.Count > 0);
    }

    [Fact]
    public void Corrupt_file_throws_and_is_left_untouched()
    {
        var good = new ShareStore(_dir);
        good.Upsert(Record());
        var full = File.ReadAllBytes(FilePath);
        var truncated = full.Take(full.Length / 2).ToArray();
        File.WriteAllBytes(FilePath, truncated);

        var store = new ShareStore(_dir);
        Assert.Throws<InvalidDataException>(() => store.All());
        Assert.Throws<InvalidDataException>(() => store.Upsert(Record()));
        Assert.Throws<InvalidDataException>(() => store.Snapshots());
        Assert.Throws<InvalidDataException>(() => store.Libraries());

        Assert.Equal(truncated, File.ReadAllBytes(FilePath));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{}")]
    [InlineData("{\"Shares\":[],\"Snapshots\":[]}")]
    [InlineData("{\"Shares\":[],\"Snapshots\":[],\"Libraries\":{},\"Extra\":1}")]
    [InlineData("{\"shares\":[],\"Snapshots\":[],\"Libraries\":{}}")]
    [InlineData("{\"Shares\":null,\"Snapshots\":[],\"Libraries\":{}}")]
    [InlineData("{\"Shares\":[],\"Snapshots\":null,\"Libraries\":{}}")]
    [InlineData("{\"Shares\":[],\"Snapshots\":[],\"Libraries\":null}")]
    [InlineData("{\"Shares\":[],\"Snapshots\":[],\"Libraries\":{},\"SharePlaylists\":null}")]
    [InlineData("{\"Shares\":[],\"Snapshots\":[],\"Libraries\":{},\"RefusalNotes\":null}")]
    [InlineData("{\"Shares\":[null],\"Snapshots\":[],\"Libraries\":{}}")]
    [InlineData("{\"Shares\":[],\"Snapshots\":[null],\"Libraries\":{}}")]
    [InlineData("{\"Shares\":[],\"Snapshots\":[],\"Libraries\":{\"5e4f3021-1cad-4e9f-a081-7c6d5e4f3005\":null}}")]
    public void Valid_json_of_the_wrong_shape_is_corrupt_and_left_untouched(string json)
    {
        File.WriteAllText(FilePath, json);

        var store = new ShareStore(_dir);
        Assert.Throws<InvalidDataException>(() => store.All());
        Assert.Throws<InvalidDataException>(() => store.Upsert(Record()));
        Assert.Equal(json, File.ReadAllText(FilePath));
    }

    [Fact]
    public void Known_real_libraries_may_be_null_as_every_version_wrote_it_before_the_first_sync()
    {
        File.WriteAllText(FilePath, "{\"Shares\":[],\"Snapshots\":[],\"Libraries\":{},\"KnownRealLibraries\":null}");

        Assert.Null(new ShareStore(_dir).KnownRealLibraries());
    }

    // One record in each shape a version of the plugin wrote: 19d3036 (Task 2), 5696761 (Task 7: checks, ClosedUtc),
    // 030e6b0 (Task 9: EmptyChecks), 1332f29 (Task 11: ViaPlaylist), Task 10 (DanglingRuns).
    [Theory]
    [InlineData("")]
    [InlineData(",\"FailedChecks\":2,\"MissingChecks\":1,\"ClosedUtc\":\"2026-11-20T00:00:00Z\"")]
    [InlineData(",\"FailedChecks\":2,\"MissingChecks\":1,\"ClosedUtc\":null,\"EmptyChecks\":1")]
    [InlineData(",\"FailedChecks\":0,\"MissingChecks\":0,\"ClosedUtc\":null,\"EmptyChecks\":0,\"ViaPlaylist\":true")]
    [InlineData(",\"FailedChecks\":0,\"MissingChecks\":0,\"ClosedUtc\":null,\"EmptyChecks\":0,\"ViaPlaylist\":true,\"DanglingRuns\":2")]
    public void A_record_in_every_previous_shape_still_loads(string tail)
    {
        var record = "{\"Id\":\"6f1c0f5e-7c43-4a59-9d0c-1d5c9f0b1a01\",\"OriginalItemId\":\"0b9a2c7e-1f6d-4c1a-8a51-2b8e1d7c6f02\","
            + "\"Kind\":\"Series\",\"Title\":\"Heat\",\"Year\":1995,\"ProviderIds\":{\"Imdb\":\"tt0113277\"},"
            + "\"SourceLibraryId\":\"3c2d1e0f-9a8b-4c7d-8e6f-5a4b3c2d1e03\",\"SharerUserId\":\"4d3e2f10-0b9c-4d8e-9f70-6b5c4d3e2f04\","
            + "\"SharerName\":\"Adult\",\"RecipientUserId\":\"5e4f3021-1cad-4e9f-a081-7c6d5e4f3005\",\"CreatedUtc\":\"2026-11-19T08:00:00Z\","
            + "\"State\":\"Active\",\"StateReason\":null,\"FolderName\":\"Heat (1995) [swm-6f1c0f5e]\"" + tail + "}";
        File.WriteAllText(FilePath, "{\"Shares\":[" + record + "],\"Snapshots\":[],\"Libraries\":{}}");

        var r = Assert.Single(new ShareStore(_dir).All());

        Assert.Equal("Heat", r.Title);
        Assert.Equal(ShareKind.Series, r.Kind);
        Assert.Equal("tt0113277", r.ProviderIds["Imdb"]);
        Assert.Equal(tail.Contains("FailedChecks\":2", StringComparison.Ordinal) ? 2 : 0, r.FailedChecks);
        Assert.Equal(tail.Contains("EmptyChecks\":1", StringComparison.Ordinal) ? 1 : 0, r.EmptyChecks);
        Assert.Equal(tail.Contains("ViaPlaylist", StringComparison.Ordinal), r.ViaPlaylist);
        Assert.Equal(tail.Contains("DanglingRuns", StringComparison.Ordinal) ? 2 : 0, r.DanglingRuns);
    }

    // The other members in the shape they were first written (unchanged since): PolicySnapshot and Libraries 19d3036,
    // KnownRealLibraries 5696761, SharePlaylists and RefusalNotes 1332f29.
    [Fact]
    public void Every_other_member_in_its_first_written_shape_still_loads()
    {
        File.WriteAllText(FilePath, """
            {
              "Shares": [],
              "Snapshots": [ { "UserId": "4d3e2f10-0b9c-4d8e-9f70-6b5c4d3e2f04", "HadAllLibraries": true, "ChangedUtc": "2026-11-19T08:00:00Z" } ],
              "Libraries": { "5e4f3021-1cad-4e9f-a081-7c6d5e4f3005": { "Movie": "3c2d1e0f-9a8b-4c7d-8e6f-5a4b3c2d1e03" } },
              "KnownRealLibraries": [ "0b9a2c7e-1f6d-4c1a-8a51-2b8e1d7c6f02" ],
              "SharePlaylists": { "4d3e2f10-0b9c-4d8e-9f70-6b5c4d3e2f04": { "5e4f3021-1cad-4e9f-a081-7c6d5e4f3005": "6f1c0f5e-7c43-4a59-9d0c-1d5c9f0b1a01" } },
              "RefusalNotes": [ { "SharerUserId": "4d3e2f10-0b9c-4d8e-9f70-6b5c4d3e2f04", "RecipientUserId": "5e4f3021-1cad-4e9f-a081-7c6d5e4f3005", "Title": "Heat", "Message": "m", "AtUtc": "2026-11-19T09:00:00Z" } ]
            }
            """);

        var store = new ShareStore(_dir);
        var sharer = Guid.Parse("4d3e2f10-0b9c-4d8e-9f70-6b5c4d3e2f04");
        var recipient = Guid.Parse("5e4f3021-1cad-4e9f-a081-7c6d5e4f3005");

        var snapshot = Assert.Single(store.Snapshots());
        Assert.Equal(sharer, snapshot.UserId);
        Assert.True(snapshot.HadAllLibraries);
        Assert.Equal(Guid.Parse("3c2d1e0f-9a8b-4c7d-8e6f-5a4b3c2d1e03"), store.Libraries()[recipient]["Movie"]);
        Assert.Equal(new[] { Guid.Parse("0b9a2c7e-1f6d-4c1a-8a51-2b8e1d7c6f02") }, store.KnownRealLibraries());
        Assert.Equal(Guid.Parse("6f1c0f5e-7c43-4a59-9d0c-1d5c9f0b1a01"), store.SharePlaylists()[sharer][recipient]);
        var note = Assert.Single(store.RefusalNotes());
        Assert.Equal(("Heat", "m", recipient), (note.Title, note.Message, note.RecipientUserId));
    }

    [Fact]
    public void Twenty_parallel_upserts_all_persist()
    {
        var store = new ShareStore(_dir);
        var records = Enumerable.Range(0, 20).Select(i => Record(title: "T" + i)).ToList();

        Parallel.ForEach(records, new ParallelOptions { MaxDegreeOfParallelism = 20 }, r => store.Upsert(r));

        var ids = new ShareStore(_dir).All().Select(r => r.Id).ToHashSet();
        Assert.Equal(20, ids.Count);
        Assert.All(records, r => Assert.Contains(r.Id, ids));
    }

    [Fact]
    public void Missing_file_means_empty_store_and_no_temp_file_is_left_behind()
    {
        var store = new ShareStore(_dir);
        Assert.Empty(store.All());
        store.Upsert(Record());
        Assert.Equal(new[] { FilePath }, Directory.GetFiles(_dir));
    }

    // Records compare Dictionary by reference, so compare the dictionary contents explicitly.
    private sealed class RecordComparer : IEqualityComparer<ShareRecord>
    {
        private static readonly Dictionary<string, string> Blank = new();

        public bool Equals(ShareRecord? x, ShareRecord? y) =>
            x is not null && y is not null
            && x with { ProviderIds = Blank } == y with { ProviderIds = Blank }
            && x.ProviderIds.OrderBy(p => p.Key).SequenceEqual(y.ProviderIds.OrderBy(p => p.Key));

        public int GetHashCode(ShareRecord obj) => obj.Id.GetHashCode();
    }
}
