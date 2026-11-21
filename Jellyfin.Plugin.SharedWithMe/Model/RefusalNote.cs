using System;

namespace Jellyfin.Plugin.SharedWithMe.Model;

/// <summary>
/// A title a user put in one of their "Share with" playlists that was not shared, and why. Shown only to that user
/// (the sharer) and to administrators.
/// </summary>
/// <param name="SharerUserId">Whose playlist it was.</param>
/// <param name="RecipientUserId">Who the playlist is for.</param>
/// <param name="Title">The title, only when the sharer could see it; empty otherwise.</param>
/// <param name="Message">One plain sentence saying why.</param>
/// <param name="AtUtc">When.</param>
public record RefusalNote(Guid SharerUserId, Guid RecipientUserId, string Title, string Message, DateTime AtUtc);
