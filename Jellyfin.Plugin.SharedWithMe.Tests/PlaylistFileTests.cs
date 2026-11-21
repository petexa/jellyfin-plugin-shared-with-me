using System;
using System.IO;
using Jellyfin.Plugin.SharedWithMe.Server;
using Xunit;

namespace Jellyfin.Plugin.SharedWithMe.Tests;

public sealed class PlaylistFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "swm-plx-" + Guid.NewGuid().ToString("N"));

    public PlaylistFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string xml)
    {
        var file = Path.Combine(_dir, "playlist.xml");
        File.WriteAllText(file, xml);
        return file;
    }

    [Fact]
    public void Paths_are_read_in_file_order_and_empty_ones_skipped()
    {
        var file = Write("""
            <?xml version="1.0" encoding="utf-8" standalone="yes"?>
            <Item>
              <OwnerUserId>4d3e2f100b9c4d8e9f706b5c4d3e2f04</OwnerUserId>
              <PlaylistItems>
                <PlaylistItem><Path>/media/shows/Spike Show (2010)/Season 01/Spike Show S01E02.mp4</Path></PlaylistItem>
                <PlaylistItem><Path></Path></PlaylistItem>
                <PlaylistItem><Path>/media/movies/Spike PG (2001)/Spike PG (2001).mp4</Path></PlaylistItem>
              </PlaylistItems>
            </Item>
            """);

        Assert.Equal(
            new[] { "/media/shows/Spike Show (2010)/Season 01/Spike Show S01E02.mp4", "/media/movies/Spike PG (2001)/Spike PG (2001).mp4" },
            JellyfinServerGateway.ReadPlaylistFilePaths(file));
    }

    [Fact]
    public void A_file_with_no_entries_is_an_empty_list_not_cannot_tell()
    {
        Assert.Empty(JellyfinServerGateway.ReadPlaylistFilePaths(Write("<Item><PlaylistItems /></Item>"))!);
    }

    [Fact]
    public void A_malformed_file_is_cannot_tell()
    {
        Assert.Null(JellyfinServerGateway.ReadPlaylistFilePaths(Write("<Item><PlaylistItems><PlaylistItem><Path>/a.mp4</Path>")));
    }

    [Fact]
    public void A_missing_file_is_cannot_tell()
    {
        Assert.Null(JellyfinServerGateway.ReadPlaylistFilePaths(Path.Combine(_dir, "nope", "playlist.xml")));
    }

    [Fact]
    public void A_file_with_a_dtd_is_refused_and_is_cannot_tell()
    {
        var secret = Path.Combine(_dir, "secret.txt");
        File.WriteAllText(secret, "/leaked/path.mp4");
        var file = Write($"""
            <?xml version="1.0"?>
            <!DOCTYPE Item [ <!ENTITY x SYSTEM "file://{secret}"> ]>
            <Item><PlaylistItems><PlaylistItem><Path>&x;</Path></PlaylistItem></PlaylistItems></Item>
            """);

        Assert.Null(JellyfinServerGateway.ReadPlaylistFilePaths(file));
    }
}
