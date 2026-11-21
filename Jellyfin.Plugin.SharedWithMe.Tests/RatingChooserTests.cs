using System.Collections.Generic;
using Jellyfin.Plugin.SharedWithMe.Sharing;
using Xunit;

namespace Jellyfin.Plugin.SharedWithMe.Tests;

public class RatingChooserTests
{
    private static readonly List<RatingOption> Ratings = new()
    {
        new("PG", 8, 0),
        new("12A", 12, 0),
        new("12", 12, 1),
        new("15", 15, 3),
    };

    [Fact]
    public void CapWithSubScoreAllowsMatchingSubScore() => Assert.Equal("12", RatingChooser.Choose(Ratings, 12, 1));

    [Fact]
    public void CapWithoutSubScoreAllowsAnySubScoreAtScore() => Assert.Equal("12", RatingChooser.Choose(Ratings, 12, null));

    [Fact]
    public void CapSubScoreExcludesHigherSubScore() => Assert.Equal("12A", RatingChooser.Choose(Ratings, 12, 0));

    [Fact]
    public void NothingFitsReturnsNull() => Assert.Null(RatingChooser.Choose(Ratings, 3, null));

    [Fact]
    public void TieKeepsFirstInList()
    {
        var tied = new List<RatingOption> { new("A", 10, 0), new("B", 10, 0) };
        Assert.Equal("A", RatingChooser.Choose(tied, 10, null));
    }
}
