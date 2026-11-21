using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SharedWithMe.Model;
using Jellyfin.Plugin.SharedWithMe.Sharing;

namespace Jellyfin.Plugin.SharedWithMe.Tests;

/// <summary>
/// In-memory IServerGateway shared by the sharing tests.
/// Visibility defaults to false for any (user, item) pair not set explicitly, and SetLibraryAccess does NOT
/// change visibility: a test asserting "cannot see" after a transition must first set the pair visible.
/// </summary>
public sealed class FakeServerGateway : IServerGateway
{
    public Dictionary<Guid, UserInfo> Users { get; } = new();

    public Dictionary<Guid, ItemInfo> Items { get; } = new();

    public List<RatingOption> Ratings { get; } = new();

    public List<Guid> RealLibraryIds { get; } = new();

    /// <summary>Explicit visibility table; anything not set is false.</summary>
    public Dictionary<(Guid UserId, Guid ItemId), bool> Visibility { get; } = new();

    /// <summary>Every interface call in order: method name and argument values.</summary>
    public List<(string Method, object?[] Args)> Calls { get; } = new();

    public Dictionary<string, Guid> ItemsByPath { get; } = new();

    public Dictionary<Guid, SourceMedia> Media { get; } = new();

    public List<(string Name, ShareKind Kind, string Path, Guid Id)> CreatedLibraries { get; } = new();

    public List<Guid> RemovedLibraries { get; } = new();

    public List<(Guid UserId, bool AllLibraries, IReadOnlyList<Guid> Enabled)> LibraryAccessCalls { get; } = new();

    public List<Guid> ScanQueue { get; } = new();

    public List<Guid> ItemRefreshQueue { get; } = new();

    /// <summary>Called after every policy write (SetLibraryAccess, SetBlockedTags), as the server raises user-updated.</summary>
    public Action<Guid>? OnPolicyWritten { get; set; }

    public List<(Guid UserId, IReadOnlyList<string> Tags)> BlockedTagCalls { get; } = new();

    /// <summary>Folder of a real library; ids in <see cref="RealLibraryIds"/> without an entry read /real/&lt;id&gt;.</summary>
    public Dictionary<Guid, string> RealPaths { get; } = new();

    /// <summary>When set, real libraries whose folder lies inside it are not real (as the real gateway decides by path).</summary>
    public string? ShareRoot { get; set; }

    /// <summary>Adds a real library reading a folder; its id and path come from this one entry.</summary>
    /// <param name="path">Folder path.</param>
    /// <returns>The library id.</returns>
    public Guid AddRealLibrary(string path)
    {
        var id = Guid.NewGuid();
        RealLibraryIds.Add(id);
        RealPaths[id] = path;
        return id;
    }

    private string RealPath(Guid id) => RealPaths.TryGetValue(id, out var p) ? p : "/real/" + id.ToString("N");

    /// <summary>Existing libraries by folder path (created by the fake or seeded by a test).</summary>
    public Dictionary<string, Guid> LibraryPaths { get; } = new();

    /// <summary>When true, SetLibraryAccess updates the enabled list but leaves AllLibraries unchanged.</summary>
    public bool IgnoreAllLibrariesChange { get; set; }

    /// <summary>Seeds a library that already exists at a path, as if created outside the plugin or before a crash.</summary>
    /// <param name="path">Folder path.</param>
    /// <returns>The library id.</returns>
    public Guid SeedLibrary(string path)
    {
        var id = Guid.NewGuid();
        LibraryPaths[path] = id;
        return id;
    }

    /// <summary>When set to a method name, the next call to that method throws once and clears this.</summary>
    public string? ThrowOnce { get; set; }

    /// <summary>When set, every call for which it returns true throws (method name and arguments).</summary>
    public Func<string, object?[], bool>? ThrowWhen { get; set; }

    /// <summary>The exception <see cref="ThrowWhen"/> throws; an InvalidOperationException when unset.</summary>
    public Func<Exception>? ThrowWith { get; set; }

    /// <summary>Milliseconds every call sleeps, so tests can widen race windows between concurrent callers.</summary>
    public int CallDelayMs { get; set; }

    public void SetVisible(Guid userId, Guid itemId, bool visible = true) => Visibility[(userId, itemId)] = visible;

    public UserInfo? GetUser(Guid id)
    {
        Hit(nameof(GetUser), id);
        return Users.GetValueOrDefault(id);
    }

    public IReadOnlyList<UserInfo> GetUsers()
    {
        Hit(nameof(GetUsers));
        return Users.Values.ToList();
    }

    public ItemInfo? GetItem(Guid id)
    {
        Hit(nameof(GetItem), id);
        return Items.GetValueOrDefault(id);
    }

    /// <summary>
    /// An episode the visibility table does not name is judged by its series (as the server does for an episode with no
    /// rating of its own); a pair set explicitly wins.
    /// </summary>
    public bool CanSee(Guid userId, Guid itemId)
    {
        Hit(nameof(CanSee), userId, itemId);
        if (Visibility.TryGetValue((userId, itemId), out var v))
        {
            return v;
        }

        return _episodeSeries.TryGetValue(itemId, out var series) && Visibility.GetValueOrDefault((userId, series));
    }

    /// <summary>Own data of an original episode by its file; an episode without an entry gets <see cref="EpisodeId"/>, no rating and no tags.</summary>
    public Dictionary<string, EpisodeDetail> EpisodeDetails { get; } = new();

    private readonly Dictionary<Guid, Guid> _episodeSeries = new();

    /// <summary>The item id of the original episode at a file (stable for the file).</summary>
    public Guid EpisodeId(string file) => EpisodeDetails.TryGetValue(file, out var d)
        ? d.Id
        : new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(file)));

    /// <summary>As the gateway: a series' media carries every episode's own details (see <see cref="EpisodeDetails"/>).</summary>
    public SourceMedia GetSourceMedia(Guid itemId)
    {
        Hit(nameof(GetSourceMedia), itemId);
        if (!Media.TryGetValue(itemId, out var m))
        {
            throw new InvalidOperationException($"No source media set for {itemId}.");
        }

        if (m.Episodes.Count == 0 || m.EpisodeDetails is not null)
        {
            return m;
        }

        var details = new Dictionary<string, EpisodeDetail>(StringComparer.Ordinal);
        foreach (var (_, _, file) in m.Episodes)
        {
            var d = EpisodeDetails.TryGetValue(file, out var set) ? set : new EpisodeDetail(EpisodeId(file), string.Empty, Array.Empty<string>());
            details[file] = d;
            _episodeSeries[d.Id] = itemId;
        }

        return m with { EpisodeDetails = details };
    }

    public IReadOnlyList<RatingOption> GetRatings()
    {
        Hit(nameof(GetRatings));
        return Ratings.ToList();
    }

    public IReadOnlyList<Guid> GetRealLibraryIds()
    {
        Hit(nameof(GetRealLibraryIds));
        return RealLibraryIds.Where(id => ShareRoot is null || !ShareLayout.IsInside(RealPath(id), ShareRoot)).ToList();
    }

    public Guid CreateLibrary(string name, ShareKind kind, string path)
    {
        Hit(nameof(CreateLibrary), name, kind, path);
        var id = Guid.NewGuid();
        CreatedLibraries.Add((name, kind, path, id));
        LibraryPaths[path] = id;
        return id;
    }

    public void RemoveLibrary(Guid libraryId)
    {
        Hit(nameof(RemoveLibrary), libraryId);
        var path = LibraryPaths.Where(p => p.Value == libraryId).Select(p => p.Key).FirstOrDefault();
        if (path is null)
        {
            return; // unknown id: no-op, per the interface contract
        }

        LibraryPaths.Remove(path);
        RemovedLibraries.Add(libraryId);
    }

    public Guid? FindLibraryByPath(string path)
    {
        Hit(nameof(FindLibraryByPath), path);
        return LibraryPaths.TryGetValue(path, out var id) ? id : null;
    }

    public void SetLibraryAccess(Guid userId, bool allLibraries, IReadOnlyList<Guid> enabled)
    {
        Hit(nameof(SetLibraryAccess), userId, allLibraries, enabled);
        LibraryAccessCalls.Add((userId, allLibraries, enabled));
        if (Users.TryGetValue(userId, out var u))
        {
            Users[userId] = u with { AllLibraries = IgnoreAllLibrariesChange ? u.AllLibraries : allLibraries, EnabledLibraries = enabled };
        }

        OnPolicyWritten?.Invoke(userId);
    }

    public void SetBlockedTags(Guid userId, IReadOnlyList<string> tags)
    {
        Hit(nameof(SetBlockedTags), userId, tags);
        BlockedTagCalls.Add((userId, tags));
        if (Users.TryGetValue(userId, out var u))
        {
            Users[userId] = u with { BlockedTags = tags.ToList() };
        }

        OnPolicyWritten?.Invoke(userId);
    }

    public IReadOnlyList<string> GetAllLibraryPaths()
    {
        Hit(nameof(GetAllLibraryPaths));
        return RealLibraryIds.Select(RealPath).Concat(LibraryPaths.Keys).Distinct().ToList();
    }

    public IReadOnlyList<Guid> FindByProviderIds(Guid libraryId, ShareKind kind, Dictionary<string, string> ids)
    {
        Hit(nameof(FindByProviderIds), libraryId, kind, ids);
        return Items.Values
            .Where(i => i.LibraryId == libraryId && i.Kind == kind
                && i.ProviderIds.Any(p => ids.Any(q => string.Equals(p.Key, q.Key, StringComparison.OrdinalIgnoreCase) && p.Value == q.Value)))
            .Select(i => i.Id)
            .ToList();
    }

    public Guid? FindItemByPath(string path)
    {
        Hit(nameof(FindItemByPath), path);
        return ItemsByPath.TryGetValue(path, out var id) ? id : null;
    }

    public void QueueScan(Guid libraryId)
    {
        Hit(nameof(QueueScan), libraryId);
        ScanQueue.Add(libraryId);
    }

    public void QueueItemRefresh(Guid itemId)
    {
        Hit(nameof(QueueItemRefresh), itemId);
        ItemRefreshQueue.Add(itemId);
    }

    public IReadOnlyList<SearchHit> SearchVisible(Guid userId, string term, int limit)
    {
        Hit(nameof(SearchVisible), userId, term, limit);
        return Items.Values
            .Where(i => i.Kind is not null && Visibility.GetValueOrDefault((userId, i.Id))
                && i.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(i => new SearchHit(i.Id, i.Title, i.Year, i.Kind!.Value, i.OfficialRating, i.Path))
            .ToList();
    }

    /// <summary>Library names by folder; a folder without an entry has no name (null).</summary>
    public Dictionary<string, string> LibraryNames { get; } = new();

    public string? GetLibraryName(string folder)
    {
        Hit(nameof(GetLibraryName), folder);
        return LibraryNames.GetValueOrDefault(folder);
    }

    /// <summary>A playlist the fake holds.</summary>
    public sealed class FakePlaylist
    {
        public Guid Owner { get; set; }

        public string Name { get; set; } = string.Empty;

        public bool IsPublic { get; set; }

        public bool SharedWithOthers { get; set; }

        public List<string> Tags { get; } = new();

        public string? Rating { get; set; }

        public List<Guid> Items { get; } = new();
    }

    /// <summary>Every playlist on the fake server, plugin-made or not.</summary>
    public Dictionary<Guid, FakePlaylist> Playlists { get; } = new();

    /// <summary>Every playlist write (create, rename, private, remove, add, remove entries) in order.</summary>
    public List<(string Method, Guid PlaylistId)> PlaylistWrites { get; } = new();

    /// <summary>Called after every playlist write, as the server raises item-updated for the playlist.</summary>
    public Action<Guid>? OnPlaylistWritten { get; set; }

    /// <summary>Adds a playlist as if a user made it (or as a test sets up a state).</summary>
    public Guid SeedPlaylist(Guid owner, string name, params Guid[] items)
    {
        var id = Guid.NewGuid();
        var p = new FakePlaylist { Owner = owner, Name = name };
        p.Items.AddRange(items);
        Playlists[id] = p;
        return id;
    }

    public Guid CreatePlaylist(Guid ownerUserId, string name)
    {
        Hit(nameof(CreatePlaylist), ownerUserId, name);
        var id = Guid.NewGuid();
        Playlists[id] = new FakePlaylist { Owner = ownerUserId, Name = name };
        Wrote(nameof(CreatePlaylist), id);
        return id;
    }

    public void RenamePlaylist(Guid playlistId, string name)
    {
        Hit(nameof(RenamePlaylist), playlistId, name);
        if (Playlists.TryGetValue(playlistId, out var p))
        {
            p.Name = name;
            Wrote(nameof(RenamePlaylist), playlistId);
        }
    }

    public void TagPlaylist(Guid playlistId, string tag)
    {
        Hit(nameof(TagPlaylist), playlistId, tag);
        if (Playlists.TryGetValue(playlistId, out var p) && !p.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            p.Tags.Add(tag);
            Wrote(nameof(TagPlaylist), playlistId);
        }
    }

    public void SetPlaylistRating(Guid playlistId, string? rating)
    {
        Hit(nameof(SetPlaylistRating), playlistId, rating ?? string.Empty);
        if (Playlists.TryGetValue(playlistId, out var p) && !string.Equals(p.Rating ?? string.Empty, rating ?? string.Empty, StringComparison.Ordinal))
        {
            p.Rating = rating;
            Wrote(nameof(SetPlaylistRating), playlistId);
        }
    }

    public void MakePlaylistPrivate(Guid playlistId)
    {
        Hit(nameof(MakePlaylistPrivate), playlistId);
        if (Playlists.TryGetValue(playlistId, out var p))
        {
            p.IsPublic = false;
            p.SharedWithOthers = false;
            Wrote(nameof(MakePlaylistPrivate), playlistId);
        }
    }

    public void RemovePlaylist(Guid playlistId)
    {
        Hit(nameof(RemovePlaylist), playlistId);
        if (Playlists.Remove(playlistId))
        {
            Wrote(nameof(RemovePlaylist), playlistId);
        }
    }

    public PlaylistInfo? GetPlaylist(Guid playlistId)
    {
        Hit(nameof(GetPlaylist), playlistId);
        return Playlists.TryGetValue(playlistId, out var p) ? new PlaylistInfo(playlistId, p.Owner, p.Name, p.IsPublic, p.SharedWithOthers, p.Tags.ToList(), p.Rating) : null;
    }

    public IReadOnlyList<PlaylistInfo> GetPlaylistsOwnedBy(Guid ownerUserId)
    {
        Hit(nameof(GetPlaylistsOwnedBy), ownerUserId);
        return Playlists.Where(p => p.Value.Owner == ownerUserId)
            .Select(p => new PlaylistInfo(p.Key, p.Value.Owner, p.Value.Name, p.Value.IsPublic, p.Value.SharedWithOthers, p.Value.Tags.ToList(), p.Value.Rating))
            .ToList();
    }

    /// <summary>Stored paths of entries whose item is gone (as the playlist file keeps the old path); none means no path.</summary>
    public Dictionary<Guid, string> GonePaths { get; } = new();

    /// <summary>Playlists whose entries read as unknown (deleted between two calls) while GetPlaylist still finds them.</summary>
    public HashSet<Guid> VanishedEntries { get; } = new();

    /// <summary>Playlists whose own file cannot be read (missing, unreadable, malformed): entries come back "cannot tell".</summary>
    public HashSet<Guid> UnreadableFiles { get; } = new();

    public PlaylistEntries? GetPlaylistEntries(Guid playlistId)
    {
        Hit(nameof(GetPlaylistEntries), playlistId);
        if (!Playlists.TryGetValue(playlistId, out var p) || VanishedEntries.Contains(playlistId))
        {
            return null;
        }

        return new PlaylistEntries(
            p.Items.Where(Items.ContainsKey).ToList(),
            p.Items.Where(i => !Items.ContainsKey(i)).Select(i => GonePaths.TryGetValue(i, out var path) ? path : null).ToList(),
            UnreadableFiles.Contains(playlistId));
    }

    /// <summary>When true, RemoveUnresolvedEntries removes nothing (as a server that would not save the playlist).</summary>
    public bool KeepDanglingEntries { get; set; }

    public int RemoveUnresolvedEntries(Guid playlistId, Func<string?, bool> which)
    {
        Hit(nameof(RemoveUnresolvedEntries), playlistId);
        if (KeepDanglingEntries || !Playlists.TryGetValue(playlistId, out var p))
        {
            return 0;
        }

        var n = p.Items.RemoveAll(i => !Items.ContainsKey(i) && which(GonePaths.TryGetValue(i, out var path) ? path : null));
        if (n > 0)
        {
            Wrote(nameof(RemoveUnresolvedEntries), playlistId);
        }

        return n;
    }

    /// <summary>As the server: only items the owner can see; a series is stored as its visible episodes.</summary>
    public void AddToPlaylist(Guid playlistId, Guid itemId)
    {
        Hit(nameof(AddToPlaylist), playlistId, itemId);
        if (!Playlists.TryGetValue(playlistId, out var p))
        {
            return;
        }

        if (Items.TryGetValue(itemId, out var item) && item.Kind == ShareKind.Series)
        {
            p.Items.AddRange(Items.Values.Where(e => e.SeriesId == itemId && Visibility.GetValueOrDefault((p.Owner, e.Id))).Select(e => e.Id));
        }
        else if (Visibility.GetValueOrDefault((p.Owner, itemId)))
        {
            p.Items.Add(itemId);
        }

        Wrote(nameof(AddToPlaylist), playlistId);
    }

    public void RemoveFromPlaylist(Guid playlistId, Guid itemId)
    {
        Hit(nameof(RemoveFromPlaylist), playlistId, itemId);
        if (Playlists.TryGetValue(playlistId, out var p)
            && p.Items.RemoveAll(i => i == itemId || (Items.TryGetValue(i, out var e) && e.SeriesId == itemId)) > 0)
        {
            Wrote(nameof(RemoveFromPlaylist), playlistId);
        }
    }

    private void Wrote(string method, Guid playlistId)
    {
        PlaylistWrites.Add((method, playlistId));
        OnPlaylistWritten?.Invoke(playlistId);
    }

    private void Hit(string method, params object?[] args)
    {
        if (CallDelayMs > 0)
        {
            System.Threading.Thread.Sleep(CallDelayMs);
        }

        Calls.Add((method, args));
        if (ThrowWhen?.Invoke(method, args) == true)
        {
            throw ThrowWith?.Invoke() ?? new InvalidOperationException($"Simulated failure in {method}.");
        }

        if (ThrowOnce == method)
        {
            ThrowOnce = null;
            throw new InvalidOperationException($"Simulated failure in {method}.");
        }
    }
}
