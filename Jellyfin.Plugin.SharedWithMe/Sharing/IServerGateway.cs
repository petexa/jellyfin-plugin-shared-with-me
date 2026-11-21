using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SharedWithMe.Model;

namespace Jellyfin.Plugin.SharedWithMe.Sharing;

/// <summary>A server user, reduced to what sharing needs.</summary>
/// <param name="Id">User id.</param>
/// <param name="Name">Display name.</param>
/// <param name="IsAdmin">Whether the user is an administrator.</param>
/// <param name="IsDisabled">Whether the account is disabled.</param>
/// <param name="IsHidden">Whether the user is hidden from login lists.</param>
/// <param name="CapScore">Parental-rating cap score, or null when uncapped.</param>
/// <param name="CapSubScore">Parental-rating cap sub score, or null for any.</param>
/// <param name="AllLibraries">Whether the user may open every library.</param>
/// <param name="EnabledLibraries">Libraries the user may open when not all.</param>
/// <param name="BlockedTags">Tags the user is blocked from.</param>
/// <param name="AllowedTags">Tags the user is limited to.</param>
/// <param name="BlocksUnrated">Whether the user is blocked from any kind of unrated item.</param>
/// <param name="BlockedUnratedKinds">The kinds of unrated item the user is blocked from (Jellyfin's UnratedItem names,
/// for example Movie and Series); null when unknown.</param>
public record UserInfo(
    Guid Id,
    string Name,
    bool IsAdmin,
    bool IsDisabled,
    bool IsHidden,
    int? CapScore,
    int? CapSubScore,
    bool AllLibraries,
    IReadOnlyList<Guid> EnabledLibraries,
    IReadOnlyList<string> BlockedTags,
    IReadOnlyList<string> AllowedTags,
    bool BlocksUnrated = false,
    IReadOnlyList<string>? BlockedUnratedKinds = null);

/// <summary>A library item, reduced to what sharing needs.</summary>
/// <param name="Id">Item id.</param>
/// <param name="Kind">Movie or series; null for anything else.</param>
/// <param name="Title">Item title.</param>
/// <param name="Year">Production year, if known.</param>
/// <param name="OfficialRating">Rating written on the item.</param>
/// <param name="ProviderIds">Provider ids of the item.</param>
/// <param name="LibraryId">Library the item lives in.</param>
/// <param name="Tags">Item tags.</param>
/// <param name="Path">Path of the item on disk.</param>
/// <param name="SeriesId">For an episode or season: its series; null for anything else.</param>
public record ItemInfo(
    Guid Id,
    ShareKind? Kind,
    string Title,
    int? Year,
    string? OfficialRating,
    Dictionary<string, string> ProviderIds,
    Guid LibraryId,
    IReadOnlyList<string> Tags,
    string? Path,
    Guid? SeriesId = null);

/// <summary>A search result, reduced to what the share pages show.</summary>
/// <param name="Id">Item id.</param>
/// <param name="Title">Item title.</param>
/// <param name="Year">Production year, if known.</param>
/// <param name="Kind">Movie or series.</param>
/// <param name="OfficialRating">Rating written on the item.</param>
/// <param name="Path">Path of the item on disk (never sent to a client).</param>
public record SearchHit(Guid Id, string Title, int? Year, ShareKind Kind, string? OfficialRating, string? Path);

/// <summary>A playlist, reduced to what the plugin's "Share with" playlists need.</summary>
/// <param name="Id">Playlist id.</param>
/// <param name="OwnerUserId">Its owner.</param>
/// <param name="Name">Its name.</param>
/// <param name="IsPublic">Whether every user may open it.</param>
/// <param name="SharedWithOthers">Whether any other user is in its share list.</param>
/// <param name="Tags">The playlist's own tags.</param>
public record PlaylistInfo(Guid Id, Guid OwnerUserId, string Name, bool IsPublic, bool SharedWithOthers, IReadOnlyList<string> Tags);

/// <summary>A playlist's entries as stored, split into those the server still resolves and those it no longer does.</summary>
/// <param name="ItemIds">Library item ids of the entries that resolve, in order (an item added twice is listed twice).</param>
/// <param name="Unresolved">The stored path of each entry whose item no longer exists (for example a file renamed and
/// scanned again); null where the entry has no path.</param>
/// <param name="CannotTell">The playlist's own file could not be read (missing, unreadable or malformed), so the server
/// cannot say whether an entry is dangling: callers must treat every title as possibly still there.</param>
public record PlaylistEntries(IReadOnlyList<Guid> ItemIds, IReadOnlyList<string?> Unresolved, bool CannotTell = false);

/// <summary>Everything the plugin needs from the Jellyfin server, behind one seam so it can be faked.</summary>
public interface IServerGateway
{
    /// <summary>Gets a user.</summary>
    /// <param name="id">User id.</param>
    /// <returns>The user, or null when unknown.</returns>
    UserInfo? GetUser(Guid id);

    /// <summary>Gets every user.</summary>
    /// <returns>All users.</returns>
    IReadOnlyList<UserInfo> GetUsers();

    /// <summary>Gets an item.</summary>
    /// <param name="id">Item id.</param>
    /// <returns>The item, or null when unknown. Kind is null when it is not a movie or series.</returns>
    ItemInfo? GetItem(Guid id);

    /// <summary>Asks the server whether a user can see an item.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="itemId">The item.</param>
    /// <returns>True when the server says the user can see it.</returns>
    bool CanSee(Guid userId, Guid itemId);

    /// <summary>Gets the media files of an item.</summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The files.</returns>
    SourceMedia GetSourceMedia(Guid itemId);

    /// <summary>Gets every rating the server knows.</summary>
    /// <returns>The ratings.</returns>
    IReadOnlyList<RatingOption> GetRatings();

    /// <summary>
    /// Gets every real library. A library with any folder under the plugin's share root is never real: it is a share
    /// library when its folders have the exact layout <c>&lt;share root&gt;/&lt;32 hex&gt;/movies|shows</c>, and otherwise a
    /// misplaced library that is neither real nor a share library (never granted, never created, removed or adopted).
    /// Implementations must decide by path, not by the store.
    /// </summary>
    /// <returns>The library ids.</returns>
    IReadOnlyList<Guid> GetRealLibraryIds();

    /// <summary>Creates a library with metadata savers off.</summary>
    /// <param name="name">Library name.</param>
    /// <param name="kind">Movie or series library.</param>
    /// <param name="path">Folder the library reads.</param>
    /// <returns>The new library id.</returns>
    Guid CreateLibrary(string name, ShareKind kind, string path);

    /// <summary>Finds a library by its folder.</summary>
    /// <param name="path">The folder path.</param>
    /// <returns>The id of the library whose folder list contains exactly this path, else null.</returns>
    /// <exception cref="InvalidOperationException">The library found also reads a folder that is not a share library folder.</exception>
    Guid? FindLibraryByPath(string path);

    /// <summary>Removes a library. Removing an id that does not exist is a no-op and must not throw.</summary>
    /// <param name="libraryId">The library.</param>
    void RemoveLibrary(Guid libraryId);

    /// <summary>Sets which libraries a user may open.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="allLibraries">Whether all libraries are enabled.</param>
    /// <param name="enabled">Enabled libraries when not all.</param>
    void SetLibraryAccess(Guid userId, bool allLibraries, IReadOnlyList<Guid> enabled);

    /// <summary>Sets a user's blocked tags, keeping every other policy field.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="tags">The complete new list of blocked tags.</param>
    void SetBlockedTags(Guid userId, IReadOnlyList<string> tags);

    /// <summary>Gets every folder of every library on the server, share libraries included, unfiltered.</summary>
    /// <returns>The folder paths.</returns>
    IReadOnlyList<string> GetAllLibraryPaths();

    /// <summary>Finds items in a library by provider ids.</summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="kind">Movie or series.</param>
    /// <param name="ids">Provider ids to match.</param>
    /// <returns>Matching item ids.</returns>
    IReadOnlyList<Guid> FindByProviderIds(Guid libraryId, ShareKind kind, Dictionary<string, string> ids);

    /// <summary>Finds the item at a path.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The item id, or null.</returns>
    Guid? FindItemByPath(string path);

    /// <summary>Queues a library scan.</summary>
    /// <param name="libraryId">The library.</param>
    void QueueScan(Guid libraryId);

    /// <summary>
    /// Queues a full metadata refresh of one item that re-reads its local NFO (replacing what the server holds), as the
    /// dashboard's "Refresh metadata, replace all" does. Only enqueues; does not wait. An unknown id is a no-op.
    /// </summary>
    /// <param name="itemId">The item, for example a shared copy found with <see cref="FindItemByPath"/>.</param>
    void QueueItemRefresh(Guid itemId);

    /// <summary>
    /// Searches the movies and series a user can see, with the server applying that user's own restrictions (library
    /// access, rating cap, blocked tags) in the query itself. The user's own shared copies (their <c>swm-</c> tag) are
    /// left out.
    /// </summary>
    /// <param name="userId">The user whose view is searched.</param>
    /// <param name="term">Search text.</param>
    /// <param name="limit">Most results to return.</param>
    /// <returns>The hits, or an empty list for an unknown user.</returns>
    IReadOnlyList<SearchHit> SearchVisible(Guid userId, string term, int limit);

    /// <summary>Gets the name of the library that reads a folder.</summary>
    /// <param name="folder">A library folder, as <see cref="GetAllLibraryPaths"/> lists it.</param>
    /// <returns>The library's name, or null when no library reads that folder.</returns>
    string? GetLibraryName(string folder);

    /// <summary>
    /// Creates a playlist owned by a user: media type video, private (not public, nobody in its share list), empty.
    /// The name decides the playlist's folder, and so its id, for good: pass a name nobody can guess.
    /// </summary>
    /// <param name="ownerUserId">The owner.</param>
    /// <param name="name">The name (and folder name).</param>
    /// <returns>The new playlist's id.</returns>
    Guid CreatePlaylist(Guid ownerUserId, string name);

    /// <summary>Renames a playlist; its folder and id stay. An unknown id is a no-op.</summary>
    /// <param name="playlistId">The playlist.</param>
    /// <param name="name">The new name.</param>
    void RenamePlaylist(Guid playlistId, string name);

    /// <summary>
    /// Adds a tag to a playlist's own tags (kept, and saved with the playlist so a refresh reads it back); a tag already
    /// there (any case) is a no-op, as is an unknown id. Tags on the playlist never reach the items in it.
    /// </summary>
    /// <param name="playlistId">The playlist.</param>
    /// <param name="tag">The tag.</param>
    void TagPlaylist(Guid playlistId, string tag);

    /// <summary>Makes a playlist private: not public and nobody in its share list. An unknown id is a no-op.</summary>
    /// <param name="playlistId">The playlist.</param>
    void MakePlaylistPrivate(Guid playlistId);

    /// <summary>Deletes a playlist (its folder too). An unknown id is a no-op.</summary>
    /// <param name="playlistId">The playlist.</param>
    void RemovePlaylist(Guid playlistId);

    /// <summary>Gets a playlist.</summary>
    /// <param name="playlistId">The playlist.</param>
    /// <returns>The playlist, or null when no playlist has that id.</returns>
    PlaylistInfo? GetPlaylist(Guid playlistId);

    /// <summary>Gets every playlist a user owns.</summary>
    /// <param name="ownerUserId">The owner.</param>
    /// <returns>The playlists; empty for an unknown user.</returns>
    IReadOnlyList<PlaylistInfo> GetPlaylistsOwnedBy(Guid ownerUserId);

    /// <summary>
    /// Gets a playlist's entries: the item ids of those that resolve, and the stored paths of those that do not. A
    /// dangling entry is reported, never silently dropped.
    /// </summary>
    /// <param name="playlistId">The playlist.</param>
    /// <returns>The entries, or null when no playlist has that id (which is not the same as an empty playlist).</returns>
    PlaylistEntries? GetPlaylistEntries(Guid playlistId);

    /// <summary>
    /// Removes the playlist's unresolved entries whose stored path (null when the entry has none) matches. Resolved entries,
    /// unresolved entries that do not match, and an unknown playlist are left alone.
    /// </summary>
    /// <param name="playlistId">The playlist.</param>
    /// <param name="which">Which unresolved entries go, by stored path.</param>
    /// <returns>How many entries were removed (0 when none matched or the playlist is unknown).</returns>
    int RemoveUnresolvedEntries(Guid playlistId, Func<string?, bool> which);

    /// <summary>
    /// Adds an item at the end of a playlist, as its owner. The server stores only what the owner can see, and stores a
    /// series or season as its episodes. An unknown playlist is a no-op.
    /// </summary>
    /// <param name="playlistId">The playlist.</param>
    /// <param name="itemId">The library item.</param>
    void AddToPlaylist(Guid playlistId, Guid itemId);

    /// <summary>
    /// Removes every entry of an item from a playlist; for a series, every entry of its episodes and seasons too.
    /// Nothing to remove, or an unknown playlist, is a no-op.
    /// </summary>
    /// <param name="playlistId">The playlist.</param>
    /// <param name="itemId">The library item.</param>
    void RemoveFromPlaylist(Guid playlistId, Guid itemId);
}
