using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

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
/// <param name="ExpiresUtc">When the share ends by itself (UTC); null until someone removes it. A value that is present but
/// cannot be read loads as long past, so the share ends (see <see cref="ExpiryJsonConverter"/>).</param>
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
    DateTime? CountedUtc = null,
    [property: JsonConverter(typeof(ExpiryJsonConverter))] DateTime? ExpiresUtc = null)
{
    /// <summary>
    /// Whether the share's time is up at <paramref name="nowUtc"/>: it has an expiry at or before that moment and is in a
    /// state the check-shares run ends (Active or Broken). A Closed share has ended already. The one test every caller uses.
    /// </summary>
    /// <param name="nowUtc">The current time (UTC).</param>
    /// <returns>True when the share must end now.</returns>
    public bool IsExpired(DateTime nowUtc) =>
        ExpiresUtc is { } until && until <= nowUtc && State is ShareState.Active or ShareState.Broken;
}

/// <summary>
/// Reads and writes <see cref="ShareRecord.ExpiresUtc"/>. A JSON null (or no member at all) is "until removed". A timestamp
/// is read as UTC: one with an offset is converted, one without is taken as UTC. Anything else (a string that is not a
/// date, a number, an object) is read as <see cref="DateTime.MinValue"/>, so an expiry that was set but cannot be read
/// ends the share rather than keeping it for ever. Written as a round-trip UTC timestamp.
/// </summary>
public sealed class ExpiryJsonConverter : JsonConverter<DateTime?>
{
    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String when reader.TryGetDateTime(out var value):
                return value.Kind switch
                {
                    DateTimeKind.Utc => value,
                    DateTimeKind.Local => value.ToUniversalTime(),
                    _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
                };
            default:
                reader.Skip();
                return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is { } v)
        {
            writer.WriteStringValue(DateTime.SpecifyKind(v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v, DateTimeKind.Utc));
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
