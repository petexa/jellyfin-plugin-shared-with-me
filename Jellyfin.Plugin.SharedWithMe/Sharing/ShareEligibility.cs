using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SharedWithMe.Configuration;
using Jellyfin.Plugin.SharedWithMe.Model;
using Jellyfin.Plugin.SharedWithMe.Storage;

namespace Jellyfin.Plugin.SharedWithMe.Sharing;

/// <summary>Why a share was refused.</summary>
public enum Refusal
{
    /// <summary>Not refused.</summary>
    None,

    /// <summary>The sharer cannot see the item.</summary>
    NotVisibleToSharer,

    /// <summary>The item is not a movie or series.</summary>
    NotShareableType,

    /// <summary>The recipient does not exist or is disabled.</summary>
    RecipientUnknown,

    /// <summary>The recipient is the sharer.</summary>
    RecipientIsSharer,

    /// <summary>The recipient can already see the item.</summary>
    RecipientAlreadySees,

    /// <summary>The item carries a tag the recipient is blocked from.</summary>
    BlockedTag,

    /// <summary>No server rating fits the recipient's cap.</summary>
    NoRatingFits,

    /// <summary>The sharer may not share.</summary>
    SharingNotAllowed,

    /// <summary>The item is a shared copy the sharer cannot see the original of.</summary>
    ResharedCopy,

    /// <summary>The item is already shared with the recipient.</summary>
    AlreadyShared,

    /// <summary>The share folder cannot be created or cannot hold symbolic links. Decided by the share service, not by <see cref="ShareEligibility"/>.</summary>
    ShareFolderUnusable,
}

/// <summary>The answer to "may this share happen".</summary>
/// <param name="Refusal">None when allowed, else why not.</param>
/// <param name="Message">One plain sentence for the person who tried to share; empty when allowed.</param>
/// <param name="CustomRating">Rating to stamp on the copy, or null for none.</param>
/// <param name="CopyTags">Tags to put on the copy.</param>
/// <param name="OriginalItemId">The original to share (a copy resolved to its original) when allowed; null when refused.</param>
public record Decision(Refusal Refusal, string Message, string? CustomRating, IReadOnlyList<string> CopyTags, Guid? OriginalItemId = null);

/// <summary>Decides whether a share may happen and with which rating override. Any doubt is a refusal.</summary>
public class ShareEligibility
{
    private readonly IServerGateway _gateway;
    private readonly ShareStore _store;
    private readonly Func<WhoMayShare> _whoMayShare;
    private readonly Func<IReadOnlyCollection<Guid>?> _allowedSharers;

    /// <summary>Initializes a new instance of the <see cref="ShareEligibility"/> class.</summary>
    /// <param name="gateway">The server.</param>
    /// <param name="store">The share store.</param>
    /// <param name="whoMayShare">Reads the current who-may-share setting.</param>
    /// <param name="allowedSharers">Reads the profiles chosen for <see cref="WhoMayShare.SelectedProfiles"/>; none when null.</param>
    public ShareEligibility(IServerGateway gateway, ShareStore store, Func<WhoMayShare> whoMayShare, Func<IReadOnlyCollection<Guid>?>? allowedSharers = null)
    {
        _gateway = gateway;
        _store = store;
        _whoMayShare = whoMayShare;
        _allowedSharers = allowedSharers ?? (() => Array.Empty<Guid>());
    }

    /// <summary>Checks whether a share may happen. Gateway or store exceptions propagate.</summary>
    /// <param name="sharerId">Who is sharing.</param>
    /// <param name="itemId">What they are sharing.</param>
    /// <param name="recipientId">Who it is shared with.</param>
    /// <returns>The decision.</returns>
    public Decision Check(Guid sharerId, Guid itemId, Guid recipientId)
    {
        var sharer = _gateway.GetUser(sharerId);
        if (sharer is null || sharer.IsDisabled)
        {
            return Refuse(Refusal.SharingNotAllowed, "You are not allowed to share titles on this server.");
        }

        var recipient = _gateway.GetUser(recipientId);
        if (recipient is null || recipient.IsDisabled)
        {
            return Refuse(Refusal.RecipientUnknown, "That person could not be found, or their account is turned off.");
        }

        if (recipientId == sharerId)
        {
            return Refuse(Refusal.RecipientIsSharer, "You cannot share a title with yourself.");
        }

        if (!MayShare(sharer))
        {
            return Refuse(Refusal.SharingNotAllowed, "You are not allowed to share titles on this server.");
        }

        // Nothing below may depend on the item's data until it is known the sharer can see it: one fixed answer
        // for "does not exist" and "exists but you cannot see it", so a capped profile learns nothing by probing ids.
        var item = _gateway.GetItem(itemId);
        if (item is null || !_gateway.CanSee(sharerId, itemId))
        {
            return Refuse(Refusal.NotVisibleToSharer, CannotShareMessage);
        }

        if (item.Kind is null)
        {
            return Refuse(Refusal.NotShareableType, "Only movies and series can be shared.");
        }

        var shares = _store.All();
        var libraries = _store.Libraries();
        if (libraries.Values.SelectMany(k => k.Values).Contains(item.LibraryId)
            || !_gateway.GetRealLibraryIds().Contains(item.LibraryId))
        {
            // The sharer can see the copy, so naming the copy is fine; never name the original here.
            var copyTitle = item.Title;
            var record = ResolveCopy(item, shares, libraries);
            if (record is null || !_gateway.CanSee(sharerId, record.OriginalItemId))
            {
                return Refuse(Refusal.ResharedCopy, $"{copyTitle} was shared with you, so you can't pass it on.");
            }

            item = _gateway.GetItem(record.OriginalItemId);
            if (item is null || item.Kind is null)
            {
                return Refuse(Refusal.ResharedCopy, $"{copyTitle} was shared with you, so you can't pass it on.");
            }
        }

        if (shares.FirstOrDefault(r => r.OriginalItemId == item.Id && r.RecipientUserId == recipientId && r.State == ShareState.Active) is { } existing)
        {
            // Shared by someone else: "can already watch it" is true and names nobody (M1).
            return existing.SharerUserId == sharerId
                ? Refuse(Refusal.AlreadyShared, $"{item.Title} is already shared with {recipient.Name}.")
                : Refuse(Refusal.RecipientAlreadySees, $"{recipient.Name} can already watch {item.Title}.");
        }

        if (_gateway.CanSee(recipientId, item.Id))
        {
            return Refuse(Refusal.RecipientAlreadySees, $"{recipient.Name} can already watch {item.Title}.");
        }

        // The plugin's own swm- tags never cause or mask a refusal.
        var blocked = item.Tags.FirstOrDefault(t => !LibraryAccessManager.IsPluginTag(t)
            && recipient.BlockedTags.Where(b => !LibraryAccessManager.IsPluginTag(b)).Contains(t, StringComparer.OrdinalIgnoreCase));
        if (blocked is not null)
        {
            // The tag is not named: it would tell the sharer what the recipient's settings hold (M1).
            return Refuse(Refusal.BlockedTag, $"{recipient.Name}'s settings block {item.Title}.");
        }

        var (fits, rating) = CustomRatingFor(_gateway.GetRatings(), recipient, item.Kind.Value, item.OfficialRating);
        if (!fits)
        {
            return Refuse(Refusal.NoRatingFits, $"{item.Title} cannot be shared with {recipient.Name} because no rating fits their age limit.");
        }

        return new Decision(Refusal.None, string.Empty, rating, CopyTagsFor(recipient), item.Id);
    }

    /// <summary>
    /// The customrating a copy needs so its recipient sees it, worked out the same way at Create and on every sync. A capped
    /// recipient: the highest rating within the cap (none fits: not shareable). An uncapped recipient who blocks unrated
    /// items of this kind, for an original with no rating: the highest-scored rating the server knows, so the copy counts
    /// as rated (I7). Anyone else: none.
    /// </summary>
    /// <param name="ratings">Every rating the server knows.</param>
    /// <param name="recipient">The recipient.</param>
    /// <param name="kind">Movie or series.</param>
    /// <param name="officialRating">The original's own rating.</param>
    /// <returns>Whether a rating fits, and the rating (null for none).</returns>
    public static (bool Fits, string? Rating) CustomRatingFor(IReadOnlyList<RatingOption> ratings, UserInfo recipient, ShareKind kind, string? officialRating)
    {
        if (recipient.CapScore is { } cap)
        {
            var chosen = RatingChooser.Choose(ratings, cap, recipient.CapSubScore);
            return (chosen is not null, chosen);
        }

        var blocksUnrated = recipient.BlockedUnratedKinds?.Contains(kind.ToString(), StringComparer.OrdinalIgnoreCase) ?? recipient.BlocksUnrated;
        if (!blocksUnrated || !string.IsNullOrEmpty(officialRating))
        {
            return (true, null);
        }

        var highest = ratings.OrderByDescending(r => r.Score).ThenByDescending(r => r.SubScore).FirstOrDefault();
        return (highest is not null, highest?.Name);
    }

    /// <summary>Tags a copy needs so a recipient limited to allowed tags can see it: their first allowed tag, if any.</summary>
    /// <param name="recipient">The recipient.</param>
    /// <returns>The tags, without the plugin's own recipient tag.</returns>
    public static List<string> CopyTagsFor(UserInfo recipient) =>
        recipient.AllowedTags.Count > 0 ? new List<string> { recipient.AllowedTags[0] } : new List<string>();

    /// <summary>
    /// The episodes of a series share: those with a file whose own (non-plugin) tags the recipient does not block and that
    /// the sharer can see now. With the sharer gone, an episode qualifies only when it has no own rating that differs from
    /// the series' rating and no own tag the recipient blocks: nothing beyond what was visible when it was shared can be
    /// taken as approved. An episode with no details is left out. Create, Build, Matches, the sync's rules, the audit and
    /// the playlist repair all use this one set.
    /// </summary>
    /// <param name="gateway">The server.</param>
    /// <param name="media">The original series' media (<see cref="IServerGateway.GetSourceMedia"/>).</param>
    /// <param name="sharerId">Who shared it.</param>
    /// <param name="recipient">Who it is shared with.</param>
    /// <param name="seriesRating">The original series' own rating.</param>
    /// <returns>The same media with only the shareable episodes.</returns>
    public static SourceMedia ShareableEpisodes(IServerGateway gateway, SourceMedia media, Guid sharerId, UserInfo recipient, string? seriesRating)
    {
        if (media.Episodes.Count == 0)
        {
            return media;
        }

        var sharerExists = gateway.GetUser(sharerId) is not null;
        var blocked = recipient.BlockedTags.Where(t => !LibraryAccessManager.IsPluginTag(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = media.Episodes.Where(e =>
        {
            if (media.EpisodeDetails is null || !media.EpisodeDetails.TryGetValue(e.File, out var d))
            {
                return false;
            }

            if (d.Tags.Any(t => !LibraryAccessManager.IsPluginTag(t) && blocked.Contains(t)))
            {
                return false;
            }

            return sharerExists
                ? gateway.CanSee(sharerId, d.Id)
                : string.IsNullOrEmpty(d.OfficialRating) || string.Equals(d.OfficialRating, seriesRating ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }).ToList();
        return media with { Episodes = kept };
    }

    /// <summary>The one message for an item that does not exist or that the sharer cannot see.</summary>
    public const string CannotShareMessage = "You can't share that title.";

    /// <summary>
    /// Whether a user may share at all: exists, enabled, and allowed by the who-may-share setting. With
    /// <see cref="WhoMayShare.SelectedProfiles"/> an administrator always may, anyone else only when listed. The API, the
    /// share page's list of people and the playlists all ask this one check.
    /// </summary>
    /// <param name="sharerId">The user.</param>
    /// <returns>True when the user may share.</returns>
    public bool MayShare(Guid sharerId) => _gateway.GetUser(sharerId) is { } u && MayShare(u);

    // Resolves a copy to the record of the library it lives in: same recipient and kind as the library that
    // holds it, and a folder name that is a whole path segment. Record state is ignored. Null means unresolvable.
    private static ShareRecord? ResolveCopy(ItemInfo item, IReadOnlyList<ShareRecord> shares, Dictionary<Guid, Dictionary<string, Guid>> libraries)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return null;
        }

        var segments = item.Path.Split('/', '\\');
        foreach (var (recipient, kinds) in libraries)
        {
            foreach (var (kindName, libraryId) in kinds)
            {
                if (libraryId != item.LibraryId || !Enum.TryParse<ShareKind>(kindName, out var kind))
                {
                    continue;
                }

                var match = shares.FirstOrDefault(r => r.RecipientUserId == recipient
                    && r.Kind == kind
                    && !string.IsNullOrEmpty(r.FolderName)
                    && segments.Contains(r.FolderName, StringComparer.Ordinal));
                if (match is not null)
                {
                    return match;
                }
            }
        }

        return null;
    }

    private bool MayShare(UserInfo sharer) => !sharer.IsDisabled && _whoMayShare() switch
    {
        WhoMayShare.Everyone => true,
        WhoMayShare.AdminsOnly => sharer.IsAdmin,
        WhoMayShare.UncappedOnly => sharer.CapScore is null,
        WhoMayShare.SelectedProfiles => sharer.IsAdmin || (_allowedSharers()?.Contains(sharer.Id) ?? false),
        _ => false,
    };

    private static Decision Refuse(Refusal reason, string message) => new(reason, message, null, new List<string>());
}
