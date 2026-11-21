using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SharedWithMe.Model;

/// <summary>What kind of item was shared.</summary>
public enum ShareKind
{
    /// <summary>A movie.</summary>
    Movie,

    /// <summary>A series.</summary>
    Series,
}

/// <summary>Lifecycle state of a share.</summary>
public enum ShareState
{
    /// <summary>The share is live.</summary>
    Active,

    /// <summary>The share's source item is gone or unreachable.</summary>
    Broken,

    /// <summary>The share was ended.</summary>
    Closed,
}

/// <summary>One item shared from one user to one recipient.</summary>
/// <param name="Id">Share id.</param>
/// <param name="OriginalItemId">Item id of the shared library item.</param>
/// <param name="Kind">Movie or series.</param>
/// <param name="Title">Item title.</param>
/// <param name="Year">Production year, if known.</param>
/// <param name="ProviderIds">Provider ids (Imdb, Tmdb, ...) of the item.</param>
/// <param name="SourceLibraryId">Library the item lives in.</param>
/// <param name="SharerUserId">User who shared it.</param>
/// <param name="SharerName">Display name of the sharer at share time.</param>
/// <param name="RecipientUserId">User it was shared with.</param>
/// <param name="CreatedUtc">When the share was created.</param>
/// <param name="State">Current state.</param>
/// <param name="StateReason">Why the state is not Active, if applicable.</param>
/// <param name="FolderName">Name of the folder holding the share on disk; empty when no copy is on disk.</param>
/// <param name="FailedChecks">Consecutive sync runs on which the recipient could not see the copy; 0 once they can.</param>
/// <param name="MissingChecks">Consecutive sync runs on which the server had no item for the copy (not yet scanned).</param>
/// <param name="ClosedUtc">When the share was closed; the sync deletes Closed records 30 days after this.</param>
/// <param name="EmptyChecks">Consecutive sync runs on which a series original had no episode file (one is tolerated).</param>
/// <param name="ViaPlaylist">The share is listed in the sharer's "Share with" playlist for the recipient, which then decides it:
/// removing the title there removes the share. False for a share the playlist has not taken in (yet).</param>
/// <param name="DanglingRuns">Consecutive full runs on which only a playlist entry that pointed at a missing file, with no
/// path or with no folder it could be matched to, kept this share; at <c>SharePlaylistManager.DanglingRunsLimit</c> that
/// entry is removed.</param>
/// <param name="CountedUtc">When <paramref name="FailedChecks"/> or <paramref name="MissingChecks"/> last went up; the next
/// count waits <c>ShareSynchronizer.MinCheckInterval</c> after it, so the refresh or scan it queued can land.</param>
public record ShareRecord(
    Guid Id,
    Guid OriginalItemId,
    ShareKind Kind,
    string Title,
    int? Year,
    Dictionary<string, string> ProviderIds,
    Guid SourceLibraryId,
    Guid SharerUserId,
    string SharerName,
    Guid RecipientUserId,
    DateTime CreatedUtc,
    ShareState State,
    string? StateReason,
    string FolderName,
    int FailedChecks = 0,
    int MissingChecks = 0,
    DateTime? ClosedUtc = null,
    int EmptyChecks = 0,
    bool ViaPlaylist = false,
    int DanglingRuns = 0,
    DateTime? CountedUtc = null);
