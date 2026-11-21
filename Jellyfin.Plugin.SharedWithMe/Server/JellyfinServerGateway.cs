using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SharedWithMe.Model;
using Jellyfin.Plugin.SharedWithMe.Sharing;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;
using ItemInfo = Jellyfin.Plugin.SharedWithMe.Sharing.ItemInfo;

namespace Jellyfin.Plugin.SharedWithMe.Server;

/// <summary><see cref="IServerGateway"/> on a real Jellyfin 12.4 server. The only sharing class that touches Jellyfin.</summary>
public class JellyfinServerGateway : IServerGateway
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILocalizationManager _localization;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly IApplicationPaths _paths;
    private readonly IPlaylistManager _playlists;
    private readonly ILogger<JellyfinServerGateway> _logger;
    private readonly HashSet<Guid> _warned = new();
    private int _policyWrites;
    private int _playlistWrites;

    /// <summary>Initializes a new instance of the <see cref="JellyfinServerGateway"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="localization">Localization manager (parental ratings).</param>
    /// <param name="providerManager">Provider manager (refresh queue).</param>
    /// <param name="fileSystem">File system.</param>
    /// <param name="paths">Application paths.</param>
    /// <param name="playlists">Playlist manager.</param>
    /// <param name="logger">Logger.</param>
    public JellyfinServerGateway(
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILocalizationManager localization,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        IApplicationPaths paths,
        IPlaylistManager playlists,
        ILogger<JellyfinServerGateway> logger)
    {
        _playlists = playlists;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _localization = localization;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Gets the share root: the configured folder, or &lt;data path&gt;/shared-with-me when none is set. Full path, no trailing separator.</summary>
    public string ShareRoot
    {
        get
        {
            return Configuration.ShareRootGuard.Effective(Plugin.Instance?.Configuration.ShareRoot, _paths.DataPath);
        }
    }

    /// <summary>Gets the number of policy writes (library access or blocked tags) since the server started.</summary>
    public int PolicyWrites => Volatile.Read(ref _policyWrites);

    /// <summary>Gets the number of playlist writes (create, rename, private, delete, add, remove) since the server started.</summary>
    public int PlaylistWrites => Volatile.Read(ref _playlistWrites);

    /// <inheritdoc />
    public UserInfo? GetUser(Guid id)
    {
        var user = _userManager.GetUserById(id);
        return user is null ? null : Map(user);
    }

    /// <inheritdoc />
    public IReadOnlyList<UserInfo> GetUsers() => _userManager.GetUsers().Select(Map).ToList();

    /// <inheritdoc />
    public ItemInfo? GetItem(Guid id)
    {
        var item = _libraryManager.GetItemById(id);
        if (item is null)
        {
            return null;
        }

        ShareKind? kind = item switch
        {
            Movie => ShareKind.Movie,
            Series => ShareKind.Series,
            _ => null,
        };

        // Inherited tags cover tags on parent folders, the series and the library itself.
        var tags = item.GetInheritedTags()
            .Concat(item.Tags ?? Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ItemInfo(
            item.Id,
            kind,
            item.Name ?? string.Empty,
            item.ProductionYear,
            item.OfficialRating,
            new Dictionary<string, string>(item.ProviderIds ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
            LibraryIdOf(item),
            tags,
            item.Path,
            item switch
            {
                Episode e => e.SeriesId,
                Season se => se.SeriesId,
                _ => null,
            });
    }

    /// <inheritdoc />
    public bool CanSee(Guid userId, Guid itemId)
    {
        var user = _userManager.GetUserById(userId);
        var item = _libraryManager.GetItemById(itemId);
        if (user is null || item is null)
        {
            return false;
        }

        // IsVisible is the parental check only; IsVisibleStandalone adds the parents and the user's library access.
        return item.IsVisibleStandalone(user);
    }

    /// <inheritdoc />
    public SourceMedia GetSourceMedia(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId) ?? throw new InvalidOperationException($"Item {itemId} does not exist.");
        if (item is Series)
        {
            // Only episodes whose file is still on disk: one deleted before the next scan must not be linked again. The
            // copy's episode NFOs carry the original's own season and episode numbers; an episode without both is left
            // out and logged, never given guessed numbers.
            var all = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    AncestorIds = new[] { itemId },
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    Recursive = true,
                })
                .OfType<Episode>()
                .Where(e => !e.IsVirtualItem && !string.IsNullOrEmpty(e.Path) && File.Exists(e.Path))
                .ToList();
            foreach (var e in all.Where(e => e.ParentIndexNumber is null || e.IndexNumber is null))
            {
                // Every sync and audit read asks again: warn once per episode per process, then only at debug level.
                _logger.Log(
                    FirstWarning(e.Id) ? LogLevel.Warning : LogLevel.Debug,
                    "Shared With Me: episode {Path} has no season or episode number, so it is not shared",
                    e.Path);
            }

            var episodes = all
                .Where(e => e.ParentIndexNumber is not null && e.IndexNumber is not null)
                .Select(e => (Item: e, Season: e.ParentIndexNumber!.Value, Number: e.IndexNumber!.Value))
                .OrderBy(e => e.Season).ThenBy(e => e.Number).ThenBy(e => e.Item.Path, StringComparer.Ordinal)
                .ToList();
            var subtitles = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var details = new Dictionary<string, EpisodeDetail>(StringComparer.Ordinal);
            foreach (var e in episodes)
            {
                subtitles[e.Item.Path] = SidecarSubtitles(e.Item);

                // An episode's own OfficialRating and Tags: what its own metadata gave it. 12.4 applies a series' (or
                // library's) tags and rating to its episodes at query time, not by copying them (api-notes, C1).
                details[e.Item.Path] = new EpisodeDetail(e.Item.Id, e.Item.OfficialRating ?? string.Empty, (e.Item.Tags ?? Array.Empty<string>()).ToList());
            }

            return new SourceMedia(
                string.Empty,
                Array.Empty<string>(),
                episodes.Select(e => (e.Season, e.Number, e.Item.Path)).ToList(),
                subtitles,
                details);
        }

        if (string.IsNullOrEmpty(item.Path) || item.IsFolder)
        {
            throw new InvalidOperationException($"Item {itemId} has no media file.");
        }

        return new SourceMedia(item.Path, SidecarSubtitles(item), Array.Empty<(int, int, string)>());
    }

    // True the first time an episode is warned about. Bounded: past 1000 ids the set starts again.
    private bool FirstWarning(Guid id)
    {
        lock (_warned)
        {
            if (_warned.Count >= 1000)
            {
                _warned.Clear();
            }

            return _warned.Add(id);
        }
    }

    private static List<string> SidecarSubtitles(BaseItem item) =>
        item.GetMediaStreams()
            .Where(s => s.IsExternal && s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.Path))
            .Select(s => s.Path)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <inheritdoc />
    public IReadOnlyList<RatingOption> GetRatings() =>
        _localization.GetParentalRatings()
            .Where(r => r.RatingScore is not null && !string.IsNullOrEmpty(r.Name))
            .Select(r => new RatingOption(r.Name, r.RatingScore!.Score, r.RatingScore.SubScore ?? 0))
            .ToList();

    /// <inheritdoc />
    public IReadOnlyList<Guid> GetRealLibraryIds()
    {
        var root = ShareRoot;
        var ids = new List<Guid>();
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            if (!Guid.TryParse(folder.ItemId, out var id) || id == Guid.Empty)
            {
                continue;
            }

            if ((folder.Locations ?? Array.Empty<string>()).Any(l => IsUnder(l, root)))
            {
                continue;
            }

            ids.Add(id);
        }

        return ids;
    }

    /// <inheritdoc />
    public Guid CreateLibrary(string name, ShareKind kind, string path)
    {
        var full = Normalise(path);
        if (!ShareLayout.IsShareLibraryPath(full, ShareRoot))
        {
            throw new InvalidOperationException($"Refusing to create a share library on {full}: not a <share root>/<id>/movies|shows folder.");
        }

        Directory.CreateDirectory(full);

        var options = new LibraryOptions
        {
            Enabled = true,
            EnableRealtimeMonitor = false,
            MetadataSavers = Array.Empty<string>(),
            SaveLocalMetadata = false,
            LocalMetadataReaderOrder = new[] { "Nfo" },
            DisabledLocalMetadataReaders = Array.Empty<string>(),
            PathInfos = new[] { new MediaPathInfo(full) },
        };
        var type = kind == ShareKind.Series ? CollectionTypeOptions.tvshows : CollectionTypeOptions.movies;

        _libraryManager.AddVirtualFolder(name, type, options, false).GetAwaiter().GetResult();

        // Without a refresh the collection folder item (and so its id) only exists once the top folders are validated.
        _libraryManager.ValidateTopLibraryFolders(CancellationToken.None, false).GetAwaiter().GetResult();
        var id = FindLibraryByPath(full)
            ?? throw new InvalidOperationException($"Library '{name}' for {full} was added but has no id after validation.");
        _logger.LogInformation("Created share library {Name} ({Id}) on {Path}", name, id, full);
        return id;
    }

    /// <inheritdoc />
    public Guid? FindLibraryByPath(string path)
    {
        var wanted = Normalise(path);
        var root = ShareRoot;
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            var locations = folder.Locations ?? Array.Empty<string>();
            if (locations.Any(l => string.Equals(Normalise(l), wanted, StringComparison.Ordinal))
                && Guid.TryParse(folder.ItemId, out var id) && id != Guid.Empty)
            {
                // Never hand out a library for adoption that also reads a folder outside the share layout.
                if (!locations.All(l => ShareLayout.IsShareLibraryPath(l, root)))
                {
                    throw new InvalidOperationException($"Library '{folder.Name}' reads {wanted} but is not a share library.");
                }

                return id;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public void RemoveLibrary(Guid libraryId)
    {
        var folder = _libraryManager.GetVirtualFolders()
            .FirstOrDefault(f => Guid.TryParse(f.ItemId, out var id) && id == libraryId);
        if (folder is null)
        {
            return;
        }

        var root = ShareRoot;
        var locations = folder.Locations ?? Array.Empty<string>();
        if (locations.Length == 0 || !locations.All(l => ShareLayout.IsShareLibraryPath(l, root)))
        {
            throw new InvalidOperationException($"Refusing to remove library '{folder.Name}': it is not a share library.");
        }

        _libraryManager.RemoveVirtualFolder(folder.Name, false).GetAwaiter().GetResult();
        _libraryManager.ValidateTopLibraryFolders(CancellationToken.None, false).GetAwaiter().GetResult();
        _logger.LogInformation("Removed share library {Name} ({Id})", folder.Name, libraryId);
    }

    /// <inheritdoc />
    public void SetLibraryAccess(Guid userId, bool allLibraries, IReadOnlyList<Guid> enabled)
    {
        var user = _userManager.GetUserById(userId) ?? throw new InvalidOperationException($"User {userId} does not exist.");

        // Read the whole policy and change only the two library fields, as the dashboard does.
        var policy = _userManager.GetUserDto(user).Policy
            ?? throw new InvalidOperationException($"User {userId} has no policy.");
        policy.EnableAllFolders = allLibraries;
        policy.EnabledFolders = allLibraries ? Array.Empty<Guid>() : enabled.Distinct().ToArray();
        Interlocked.Increment(ref _policyWrites);
        _userManager.UpdatePolicyAsync(userId, policy).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public void SetBlockedTags(Guid userId, IReadOnlyList<string> tags)
    {
        var user = _userManager.GetUserById(userId) ?? throw new InvalidOperationException($"User {userId} does not exist.");
        var policy = _userManager.GetUserDto(user).Policy
            ?? throw new InvalidOperationException($"User {userId} has no policy.");
        policy.BlockedTags = tags.ToArray();
        Interlocked.Increment(ref _policyWrites);
        _userManager.UpdatePolicyAsync(userId, policy).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetAllLibraryPaths() =>
        _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations ?? Array.Empty<string>())
            .Select(Normalise)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <inheritdoc />
    public IReadOnlyList<Guid> FindByProviderIds(Guid libraryId, ShareKind kind, Dictionary<string, string> ids)
    {
        var wanted = ids.Where(p => !string.IsNullOrEmpty(p.Value)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        return _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { kind == ShareKind.Series ? BaseItemKind.Series : BaseItemKind.Movie },
                HasAnyProviderId = wanted,
                Recursive = true,
            })
            .Where(i => !i.IsVirtualItem
                && (i.ProviderIds ?? new Dictionary<string, string>()).Any(p => wanted.TryGetValue(p.Key, out var v) && string.Equals(v, p.Value, StringComparison.Ordinal))
                && LibraryIdOf(i) == libraryId)
            .Select(i => i.Id)
            .ToList();
    }

    /// <inheritdoc />
    public Guid? FindItemByPath(string path) => _libraryManager.FindByPath(path, null)?.Id;

    /// <inheritdoc />
    public void QueueScan(Guid libraryId)
    {
        if (_libraryManager.GetItemById(libraryId) is not CollectionFolder)
        {
            throw new InvalidOperationException($"Library {libraryId} does not exist.");
        }

        // The same refresh the dashboard's per-library "Scan" queues: a collection folder refresh validates its children.
        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.Default,
            ImageRefreshMode = MetadataRefreshMode.Default,
        };
        _providerManager.QueueRefresh(libraryId, options, RefreshPriority.High);
    }

    /// <inheritdoc />
    public void QueueItemRefresh(Guid itemId)
    {
        if (_libraryManager.GetItemById(itemId) is null)
        {
            return;
        }

        // Replace-all re-reads the plugin's NFO, so a changed customrating or tag reaches the item (api-notes: the
        // swm- tag survives a replace-all refresh because it comes from the NFO).
        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.Default,
            ReplaceAllMetadata = true,
        };
        _providerManager.QueueRefresh(itemId, options, RefreshPriority.High);
    }

    /// <inheritdoc />
    public IReadOnlyList<SearchHit> SearchVisible(Guid userId, string term, int limit)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return Array.Empty<SearchHit>();
        }

        // A query built for the user, with no ParentId, AncestorIds or TopParentIds: the server then limits it to that
        // user's own views and applies the user's rating cap and blocked tags itself. Setting any of those ids would
        // bypass the library-access part, so none is set. The user's own copies are left out by their tag.
        var query = new InternalItemsQuery(user)
        {
            SearchTerm = term,
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            IsVirtualItem = false,
            ExcludeTags = new[] { LibraryAccessManager.RecipientTag(userId) },
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
            Limit = limit,
        };

        return _libraryManager.GetItemList(query)
            .Where(i => i.IsVisibleStandalone(user))
            .Select(i => (Item: i, Kind: i switch { Movie => ShareKind.Movie, Series => ShareKind.Series, _ => (ShareKind?)null }))
            .Where(x => x.Kind is not null)
            .Select(x => new SearchHit(x.Item.Id, x.Item.Name ?? string.Empty, x.Item.ProductionYear, x.Kind!.Value, x.Item.OfficialRating, x.Item.Path))
            .ToList();
    }

    /// <inheritdoc />
    public string? GetLibraryName(string folder)
    {
        var wanted = Normalise(folder);
        return _libraryManager.GetVirtualFolders()
            .FirstOrDefault(f => (f.Locations ?? Array.Empty<string>()).Any(l => string.Equals(Normalise(l), wanted, StringComparison.Ordinal)))
            ?.Name;
    }

    /// <inheritdoc />
    public Guid CreatePlaylist(Guid ownerUserId, string name)
    {
        Interlocked.Increment(ref _playlistWrites);
        var result = _playlists.CreatePlaylist(new PlaylistCreationRequest
        {
            Name = name,
            UserId = ownerUserId,
            MediaType = MediaType.Video,
            Public = false,
            ItemIdList = Array.Empty<Guid>(),
            Users = Array.Empty<PlaylistUserPermissions>(),
        }).GetAwaiter().GetResult();
        return Guid.Parse(result.Id);
    }

    /// <inheritdoc />
    public void RenamePlaylist(Guid playlistId, string name)
    {
        if (PlaylistItem(playlistId) is not { } p)
        {
            return;
        }

        Interlocked.Increment(ref _playlistWrites);
        _playlists.UpdatePlaylist(new PlaylistUpdateRequest { Id = playlistId, UserId = p.OwnerUserId, Name = name }).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public void TagPlaylist(Guid playlistId, string tag)
    {
        if (PlaylistItem(playlistId) is not { } p || (p.Tags ?? Array.Empty<string>()).Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        // The item's own tags in the database, and the playlist file (playlist.xml carries <Tags>, read back by a refresh).
        Interlocked.Increment(ref _playlistWrites);
        p.Tags = (p.Tags ?? Array.Empty<string>()).Append(tag).ToArray();
        p.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).GetAwaiter().GetResult();
        _playlists.SavePlaylistFile(p);
    }

    /// <inheritdoc />
    public void MakePlaylistPrivate(Guid playlistId)
    {
        if (PlaylistItem(playlistId) is not { } p)
        {
            return;
        }

        Interlocked.Increment(ref _playlistWrites);
        _playlists.UpdatePlaylist(new PlaylistUpdateRequest
        {
            Id = playlistId,
            UserId = p.OwnerUserId,
            Public = false,
            Users = Array.Empty<PlaylistUserPermissions>(),
        }).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public void RemovePlaylist(Guid playlistId)
    {
        if (PlaylistItem(playlistId) is not { } p)
        {
            return;
        }

        // As DELETE /Items/{id} does for a playlist: the item and its folder under the playlists folder.
        Interlocked.Increment(ref _playlistWrites);
        _libraryManager.DeleteItem(p, new DeleteOptions { DeleteFileLocation = true }, true);
    }

    /// <inheritdoc />
    public PlaylistInfo? GetPlaylist(Guid playlistId) => PlaylistItem(playlistId) is { } p ? Info(p) : null;

    /// <inheritdoc />
    public IReadOnlyList<PlaylistInfo> GetPlaylistsOwnedBy(Guid ownerUserId) =>
        _userManager.GetUserById(ownerUserId) is null
            ? Array.Empty<PlaylistInfo>()
            : _playlists.GetPlaylists(ownerUserId).Where(p => p.OwnerUserId == ownerUserId).Select(Info).ToList();

    // LinkedChild.Path is marked obsolete in 12.4 ("Use ItemId instead"), but playlist.xml stores entries by path and an
    // entry whose item is gone has nothing else: it is the only record of where a dangling entry pointed (Task 11).
#pragma warning disable CS0618

    /// <inheritdoc />
    public PlaylistEntries? GetPlaylistEntries(Guid playlistId)
    {
        if (PlaylistItem(playlistId) is not { } p)
        {
            return null;
        }

        var ids = new List<Guid>();
        var resolvedPaths = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = new List<string?>();
        foreach (var child in p.LinkedChildren ?? Array.Empty<LinkedChild>())
        {
            if (Resolve(child) is { } item)
            {
                ids.Add(item.Id);
                if (!string.IsNullOrEmpty(item.Path))
                {
                    resolvedPaths.Add(item.Path);
                }
            }
            else
            {
                unresolved.Add(string.IsNullOrEmpty(child.Path) ? null : child.Path);
            }
        }

        // 12.4 drops an entry from the playlist item as soon as its library item is deleted (a renamed file, scanned
        // again), while playlist.xml keeps the old path until the playlist is saved next. So the file is read too: a
        // stored path the server no longer resolves is an unresolved entry (api-notes, Task 11 fix round 1). A file that
        // cannot be read means "cannot tell", never "no dangling entries".
        var filePaths = FilePaths(p);
        foreach (var path in filePaths ?? new List<string>())
        {
            if (!resolvedPaths.Contains(path) && !unresolved.Contains(path) && _libraryManager.FindByPath(path, null) is null)
            {
                unresolved.Add(path);
            }
        }

        return new PlaylistEntries(ids, unresolved, filePaths is null);
    }

    /// <inheritdoc />
    public int RemoveUnresolvedEntries(Guid playlistId, Func<string?, bool> which)
    {
        if (PlaylistItem(playlistId) is not { } p)
        {
            return 0;
        }

        var children = p.LinkedChildren ?? Array.Empty<LinkedChild>();
        var keep = children
            .Where(c => Resolve(c) is not null || !which(string.IsNullOrEmpty(c.Path) ? null : c.Path))
            .ToArray();
        var inFile = (FilePaths(p) ?? new List<string>()).Count(f => which(f) && _libraryManager.FindByPath(f, null) is null
            && !children.Any(c => !string.IsNullOrEmpty(c.Path) && string.Equals(c.Path, f, StringComparison.Ordinal)));
        var removed = children.Length - keep.Length + inFile;
        if (removed == 0)
        {
            return 0;
        }

        // An entry that no longer resolves has no item id to remove it by: the playlist's own list is rewritten, as the
        // server's own removal does, and saved to the repository and the playlist file.
        Interlocked.Increment(ref _playlistWrites);
        p.LinkedChildren = keep;
        p.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).GetAwaiter().GetResult();
        _playlists.SavePlaylistFile(p);
        return removed;
    }

    // The entry paths stored in the playlist's own file (<folder>/playlist.xml); null when it cannot be read.
    private List<string>? FilePaths(Playlist p)
    {
        if (string.IsNullOrEmpty(p.Path))
        {
            return null;
        }

        var file = Path.Combine(p.Path, "playlist.xml");
        var paths = ReadPlaylistFilePaths(file);
        if (paths is null)
        {
            _logger.LogWarning("Shared With Me: could not read {File}; its playlist's shares are kept until it can be read", file);
        }

        return paths;
    }

    /// <summary>
    /// Reads the entry paths stored in a playlist file (<c>PlaylistItem/Path</c>). A DTD is refused and nothing external
    /// is resolved.
    /// </summary>
    /// <param name="file">The <c>playlist.xml</c> path.</param>
    /// <returns>The non-empty paths in file order, or null when the file is missing, unreadable or malformed ("cannot
    /// tell", never "no entries").</returns>
    public static List<string>? ReadPlaylistFilePaths(string file)
    {
        var settings = new System.Xml.XmlReaderSettings
        {
            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
            XmlResolver = null,
        };

        try
        {
            using var stream = File.OpenRead(file);
            using var reader = System.Xml.XmlReader.Create(stream, settings);
            return System.Xml.Linq.XDocument.Load(reader).Descendants("PlaylistItem")
                .Select(e => (string?)e.Element("Path"))
                .OfType<string>()
                .Where(x => x.Length > 0)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // A playlist entry's item: by its id when that still exists, else by its stored path; null when neither does.
    private BaseItem? Resolve(LinkedChild child)
    {
        if (child.ItemId is { } id && id != Guid.Empty && _libraryManager.GetItemById(id) is { } byId)
        {
            return byId;
        }

        return string.IsNullOrEmpty(child.Path) ? null : _libraryManager.FindByPath(child.Path, null);
    }
#pragma warning restore CS0618

    /// <inheritdoc />
    public void AddToPlaylist(Guid playlistId, Guid itemId)
    {
        if (PlaylistItem(playlistId) is not { } p)
        {
            return;
        }

        Interlocked.Increment(ref _playlistWrites);
        _playlists.AddItemToPlaylistAsync(playlistId, new[] { itemId }, null, p.OwnerUserId).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public void RemoveFromPlaylist(Guid playlistId, Guid itemId)
    {
        if (PlaylistItem(playlistId) is not { } p)
        {
            return;
        }

        // 12.4 keys a playlist entry by its library item id (api-notes, Task 11); a series is held as its episodes.
        var entries = (p.LinkedChildren ?? Array.Empty<LinkedChild>()).Select(Resolve).OfType<BaseItem>()
            .Where(i => i.Id == itemId || (i is Episode e && e.SeriesId == itemId) || (i is Season se && se.SeriesId == itemId))
            .Select(i => i.Id.ToString("N", System.Globalization.CultureInfo.InvariantCulture))
            .Distinct()
            .ToList();
        if (entries.Count == 0)
        {
            return;
        }

        Interlocked.Increment(ref _playlistWrites);
        _playlists.RemoveItemFromPlaylistAsync(playlistId.ToString("N", System.Globalization.CultureInfo.InvariantCulture), entries).GetAwaiter().GetResult();
    }

    private static PlaylistInfo Info(Playlist p) =>
        new(p.Id, p.OwnerUserId, p.Name ?? string.Empty, p.OpenAccess, (p.Shares ?? Array.Empty<PlaylistUserPermissions>()).Any(s => s.UserId != p.OwnerUserId), (p.Tags ?? Array.Empty<string>()).ToList());

    private Playlist? PlaylistItem(Guid id) => _libraryManager.GetItemById(id) as Playlist;

    private static string Normalise(string path) => ShareLayout.Normalise(path);

    private static bool IsUnder(string path, string root) => ShareLayout.IsInside(path, root);

    // The library (collection folder) an item belongs to. When it is in several, a non-real one wins so that any
    // doubt is treated as a copy; Guid.Empty when it is in none.
    private Guid LibraryIdOf(BaseItem item)
    {
        var folders = _libraryManager.GetCollectionFolders(item);
        if (folders.Count == 0)
        {
            return Guid.Empty;
        }

        if (folders.Count == 1)
        {
            return folders[0].Id;
        }

        var real = GetRealLibraryIds();
        return (folders.FirstOrDefault(f => !real.Contains(f.Id)) ?? folders[0]).Id;
    }

    private UserInfo Map(Jellyfin.Database.Implementations.Entities.User user)
    {
        var policy = _userManager.GetUserDto(user).Policy
            ?? throw new InvalidOperationException($"User {user.Id} has no policy.");
        return new UserInfo(
            user.Id,
            user.Username,
            policy.IsAdministrator,
            policy.IsDisabled,
            policy.IsHidden,
            policy.MaxParentalRating,
            policy.MaxParentalSubRating,
            policy.EnableAllFolders,
            (policy.EnabledFolders ?? Array.Empty<Guid>()).ToList(),
            (policy.BlockedTags ?? Array.Empty<string>()).ToList(),
            (policy.AllowedTags ?? Array.Empty<string>()).ToList(),
            (policy.BlockUnratedItems ?? Array.Empty<UnratedItem>()).Length > 0,
            (policy.BlockUnratedItems ?? Array.Empty<UnratedItem>()).Select(k => k.ToString()).ToList());
    }
}
