using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SharedWithMe.Model;

/// <summary>A user's library-access policy as it was before the plugin changed it.</summary>
/// <param name="UserId">The user.</param>
/// <param name="HadAllLibraries">Whether the user had access to all libraries.</param>
/// <param name="ChangedUtc">When the plugin changed the policy.</param>
/// <param name="GivenLibraries">The real libraries the plugin itself last gave the user (its explicit list, share libraries
/// left out); null in a snapshot written before this was recorded. A user whose list no longer holds every one of these
/// that still exists was narrowed by someone else, so the plugin neither adds to that list nor puts it back on "all
/// libraries".</param>
public record PolicySnapshot(Guid UserId, bool HadAllLibraries, DateTime ChangedUtc, IReadOnlyList<Guid>? GivenLibraries = null);
