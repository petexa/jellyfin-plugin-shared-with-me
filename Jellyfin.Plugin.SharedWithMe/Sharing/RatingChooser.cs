using System.Collections.Generic;

namespace Jellyfin.Plugin.SharedWithMe.Sharing;

/// <summary>A rating the server knows, with its ordering score.</summary>
/// <param name="Name">Rating name as written on items.</param>
/// <param name="Score">Rating score.</param>
/// <param name="SubScore">Rating sub score.</param>
public record RatingOption(string Name, int Score, int SubScore);

/// <summary>Picks the rating to stamp on a shared copy.</summary>
public static class RatingChooser
{
    /// <summary>Returns the name of the highest option within the cap, or null when none fits.</summary>
    /// <param name="serverRatings">All ratings known to the server.</param>
    /// <param name="capScore">The recipient's cap score.</param>
    /// <param name="capSubScore">The recipient's cap sub score, or null for any.</param>
    /// <returns>The chosen rating name, or null.</returns>
    public static string? Choose(IReadOnlyList<RatingOption> serverRatings, int capScore, int? capSubScore)
    {
        RatingOption? best = null;
        foreach (var o in serverRatings)
        {
            var fits = o.Score < capScore
                || (o.Score == capScore && (capSubScore is null || o.SubScore <= capSubScore));
            if (!fits)
            {
                continue;
            }

            if (best is null || o.Score > best.Score || (o.Score == best.Score && o.SubScore > best.SubScore))
            {
                best = o;
            }
        }

        return best?.Name;
    }
}
