using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SharedWithMe.Model;
using Jellyfin.Plugin.SharedWithMe.Sharing;
using Xunit;

namespace Jellyfin.Plugin.SharedWithMe.Tests;

public sealed class ShareFolderBuilderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "swm-fb-" + Guid.NewGuid().ToString("N"));
    private readonly ShareFolderBuilder _builder = new();

    public ShareFolderBuilderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string KindRoot => Path.Combine(_dir, "root", "recipient", "movies");

    private string MakeFile(string rel, string content = "x")
    {
        var p = Path.Combine(_dir, "orig", rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    private static ShareRecord Rec(string title = "Heat", int? year = 1995, ShareKind kind = ShareKind.Movie) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        kind,
        title,
        year,
        new Dictionary<string, string> { ["Imdb"] = "tt0113277", ["Tmdb"] = "949", ["Tvdb"] = "81189" },
        Guid.NewGuid(),
        Guid.NewGuid(),
        "Adult",
        Guid.NewGuid(),
        DateTime.UtcNow,
        ShareState.Active,
        null,
        string.Empty);

    private static readonly IReadOnlyList<string> NoTags = new List<string>();

    private static SourceMedia Movie(string file, params string[] subs) =>
        new(file, subs, new List<(int, int, string)>());

    [Fact]
    public void BuildCreatesSymlinkAndNfo()
    {
        var media = MakeFile("Heat (1995)/Heat (1995).mkv", "video");
        var r = Rec();
        var name = _builder.Build(KindRoot, r, Movie(media), "GB-18", "12", new[] { "Shared by Adult" });

        var folder = Path.Combine(KindRoot, name);
        Assert.True(Directory.Exists(folder));
        Assert.Null(new DirectoryInfo(folder).LinkTarget);
        var link = new FileInfo(Path.Combine(folder, name + ".mkv"));
        Assert.Equal(media, link.LinkTarget);
        Assert.Equal("video", File.ReadAllText(link.FullName));
        Assert.StartsWith("Heat (1995) [swm-" + r.Id.ToString("N")[..8] + "]", name, StringComparison.Ordinal);

        var nfo = File.ReadAllText(Path.Combine(folder, name + ".nfo"));
        Assert.Contains("<mpaa>GB-18</mpaa>", nfo, StringComparison.Ordinal);
        Assert.Contains("<customrating>12</customrating>", nfo, StringComparison.Ordinal);
        Assert.Contains("<lockedfields>OfficialRating</lockedfields>", nfo, StringComparison.Ordinal);
        Assert.Contains("<title>Heat</title>", nfo, StringComparison.Ordinal);
        Assert.Contains("<year>1995</year>", nfo, StringComparison.Ordinal);
        Assert.Contains("type=\"imdb\"", nfo, StringComparison.Ordinal);
        Assert.Contains("tt0113277", nfo, StringComparison.Ordinal);
        Assert.Contains("type=\"tmdb\"", nfo, StringComparison.Ordinal);
        Assert.Contains("type=\"tvdb\"", nfo, StringComparison.Ordinal);
        Assert.Contains("<tag>Shared by Adult</tag>", nfo, StringComparison.Ordinal);
        Assert.DoesNotContain("lockdata", nfo, StringComparison.Ordinal);
        Assert.StartsWith("<?xml", nfo, StringComparison.Ordinal);
        Assert.Contains("<movie>", nfo, StringComparison.Ordinal);
    }

    [Fact]
    public void NoCustomRatingOmitsElement()
    {
        var media = MakeFile("a/Heat.mkv");
        var name = _builder.Build(KindRoot, Rec(), Movie(media), "GB-18", string.Empty, NoTags);
        var nfo = File.ReadAllText(Path.Combine(KindRoot, name, name + ".nfo"));
        Assert.DoesNotContain("customrating", nfo, StringComparison.Ordinal);
        Assert.Contains("<mpaa>GB-18</mpaa>", nfo, StringComparison.Ordinal);
    }

    [Fact]
    public void NfoEscapesSpecialCharacters()
    {
        var media = MakeFile("a/x.mkv");
        var name = _builder.Build(KindRoot, Rec("Tom & \"Jerry\" <1>"), Movie(media), "PG", "PG", NoTags);
        var nfo = File.ReadAllText(Path.Combine(KindRoot, name, name + ".nfo"));
        Assert.Contains("Tom &amp; \"Jerry\" &lt;1&gt;", nfo, StringComparison.Ordinal);
        var doc = System.Xml.Linq.XDocument.Parse(nfo);
        Assert.Equal("Tom & \"Jerry\" <1>", doc.Root!.Element("title")!.Value);
    }

    [Fact]
    public void SameTitleTwoRecordsGetDistinctSanitisedFolders()
    {
        var media = MakeFile("a/x.mkv");
        var a = _builder.Build(KindRoot, Rec("Heat: Director's Cut?"), Movie(media), "15", "15", NoTags);
        var b = _builder.Build(KindRoot, Rec("Heat: Director's Cut?"), Movie(media), "15", "15", NoTags);
        Assert.NotEqual(a, b);
        foreach (var n in new[] { a, b })
        {
            Assert.DoesNotContain(':', n);
            Assert.DoesNotContain('?', n);
            Assert.DoesNotContain('/', n);
            Assert.True(Directory.Exists(Path.Combine(KindRoot, n)));
        }
    }

    [Fact]
    public void SanitiserHandlesAllForbiddenCharsAndTrailingDots()
    {
        var media = MakeFile("a/x.mkv");
        var name = _builder.Build(KindRoot, Rec("A/B\\C:D*E?F\"G<H>I|J\tK  L..", null), Movie(media), "PG", "PG", NoTags);
        var title = name[..name.IndexOf(" [swm-", StringComparison.Ordinal)];
        Assert.Equal("A B C D E F G H I J K L", title);
    }

    [Fact]
    public void SidecarSubtitlesAreLinkedAndOtherVideosAreNot()
    {
        var media = MakeFile("Film (2001)/Film (2001).mkv");
        var sub = MakeFile("Film (2001)/Film (2001).en.srt", "sub");
        var other = MakeFile("Film (2001)/Film (2001) - extended.mkv");
        var name = _builder.Build(KindRoot, Rec("Film", 2001), Movie(media, sub), "PG", "PG", NoTags);
        var folder = Path.Combine(KindRoot, name);
        var files = Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).OrderBy(x => x).ToList();
        Assert.Contains(name + ".en.srt", files);
        Assert.Contains(name + ".mkv", files);
        Assert.Contains(name + ".nfo", files);
        Assert.Equal(3, files.Count);
        Assert.Equal(sub, new FileInfo(Path.Combine(folder, name + ".en.srt")).LinkTarget);
        Assert.DoesNotContain(files, f => f!.Contains("extended", StringComparison.Ordinal));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void SidecarNotPrefixedByVideoNameKeepsExtension()
    {
        var media = MakeFile("a/Film.mkv");
        var sub = MakeFile("a/subs.srt");
        var name = _builder.Build(KindRoot, Rec(), Movie(media, sub), "PG", "PG", NoTags);
        Assert.True(File.Exists(Path.Combine(KindRoot, name, name + ".srt")));
    }

    [Fact]
    public void SeriesLayoutLinksEpisodesUnderSeasonFolders()
    {
        var root = Path.Combine(_dir, "root", "recipient", "shows");
        var e1 = MakeFile("Show/S01/Show - S01E01.mkv");
        var e2 = MakeFile("Show/S00/Special.mkv");
        var e3 = MakeFile("Show/S10/Show - S10E02.mkv");
        var eps = new List<(int, int, string)> { (1, 1, e1), (0, 1, e2), (10, 2, e3) };
        var r = Rec("Show", 2010, ShareKind.Series);
        var name = _builder.Build(root, r, new SourceMedia("ignored.mkv", new List<string>(), eps), "TV-MA", "12", new[] { "t" });
        var folder = Path.Combine(root, name);
        Assert.Equal(e1, new FileInfo(Path.Combine(folder, "Season 01", "Show - S01E01.mkv")).LinkTarget);
        Assert.Equal(e2, new FileInfo(Path.Combine(folder, "Season 00", "Special.mkv")).LinkTarget);
        Assert.Equal(e3, new FileInfo(Path.Combine(folder, "Season 10", "Show - S10E02.mkv")).LinkTarget);
        var nfo = File.ReadAllText(Path.Combine(folder, "tvshow.nfo"));
        Assert.Contains("<tvshow>", nfo, StringComparison.Ordinal);
        Assert.Contains("<mpaa>TV-MA</mpaa>", nfo, StringComparison.Ordinal);
        Assert.Contains("<customrating>12</customrating>", nfo, StringComparison.Ordinal);
        Assert.True(_builder.LinksResolve(root, name));
    }

    [Fact]
    public void RebuildSameShareReplacesContents()
    {
        var media = MakeFile("a/x.mkv");
        var media2 = MakeFile("b/y.mp4");
        var r = Rec();
        var name = _builder.Build(KindRoot, r, Movie(media), "PG", "PG", NoTags);
        var name2 = _builder.Build(KindRoot, r with { FolderName = name }, Movie(media2), "PG", "PG", NoTags);
        Assert.Equal(name, name2);
        var files = Directory.GetFileSystemEntries(Path.Combine(KindRoot, name)).Select(Path.GetFileName).ToList();
        Assert.Contains(name + ".mp4", files);
        Assert.DoesNotContain(name + ".mkv", files);
    }

    [Fact]
    public void RemoveDeletesOnlyNamedFolderAndNeverFollowsLinks()
    {
        var media = MakeFile("a/x.mkv", "precious");
        var a = _builder.Build(KindRoot, Rec(), Movie(media), "PG", "PG", NoTags);
        var b = _builder.Build(KindRoot, Rec("Other"), Movie(media), "PG", "PG", NoTags);
        _builder.Remove(KindRoot, a);
        Assert.False(Directory.Exists(Path.Combine(KindRoot, a)));
        Assert.True(Directory.Exists(Path.Combine(KindRoot, b)));
        Assert.True(File.Exists(media));
        Assert.Equal("precious", File.ReadAllText(media));
    }

    [Fact]
    public void RemoveMissingFolderIsNoOp() => _builder.Remove(KindRoot, "nope [swm-12345678]");

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("../x")]
    [InlineData("no-marker")]
    public void RemoveRefusesUnsafeNames(string bad)
    {
        Directory.CreateDirectory(KindRoot);
        Assert.Throws<ArgumentException>(() => _builder.Remove(KindRoot, bad));
        Assert.True(Directory.Exists(KindRoot));
    }

    [Fact]
    public void LinksResolveFalseWhenOriginalGoneOrFolderMissing()
    {
        var media = MakeFile("a/x.mkv");
        var name = _builder.Build(KindRoot, Rec(), Movie(media), "PG", "PG", NoTags);
        Assert.True(_builder.LinksResolve(KindRoot, name));
        File.Delete(media);
        Assert.False(_builder.LinksResolve(KindRoot, name));
        Assert.False(_builder.LinksResolve(KindRoot, "missing"));
    }

    [Fact]
    public void ProbeSucceedsOnWritableDirectoryAndLeavesNothing()
    {
        var root = Path.Combine(_dir, "probe");
        ShareFolderBuilder.ProbeSymlinkSupport(root);
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public void ProbeOnUncreatableDirectoryThrowsIoException()
    {
        var blocker = MakeFile("blocker");
        var ex = Assert.Throws<IOException>(() => ShareFolderBuilder.ProbeSymlinkSupport(Path.Combine(blocker, "sub")));
        Assert.Contains(blocker, ex.Message, StringComparison.Ordinal);
    }

    private string[] KindRootEntries() => Directory.Exists(KindRoot)
        ? Directory.GetFileSystemEntries(KindRoot)
        : Array.Empty<string>();

    [Fact]
    public void ControlCharsInNfoTextAreStrippedNotFatal()
    {
        var media = MakeFile("a/x.mkv");
        var name = _builder.Build(KindRoot, Rec("Bad\u0001Title\ud800"), Movie(media), "PG\u0002", "PG", new[] { "t\u0003ag" });
        var nfo = File.ReadAllText(Path.Combine(KindRoot, name, name + ".nfo"));
        Assert.Contains("<title>BadTitle</title>", nfo, StringComparison.Ordinal);
        Assert.Contains("<tag>tag</tag>", nfo, StringComparison.Ordinal);
        Assert.Equal(new[] { name }, KindRootEntries().Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void FailedBuildLeavesNoFolderBehind()
    {
        var media = MakeFile("a/x.mkv");
        var missingSub = Path.Combine(_dir, "orig", "a", "x.en.srt");
        var r = Rec();
        // A sidecar path with an embedded NUL makes CreateSymbolicLink throw after the NFO and video link exist.
        Assert.ThrowsAny<Exception>(() => _builder.Build(KindRoot, r, Movie(media, missingSub + "\0bad"), "PG", "PG", NoTags));
        Assert.Empty(KindRootEntries());
    }

    [Fact]
    public void StaleTempDirectoryIsClearedAndNoTempRemainsOnSuccess()
    {
        var media = MakeFile("a/x.mkv");
        var r = Rec();
        var stale = Path.Combine(KindRoot, ".swm-tmp-" + r.Id.ToString("N"));
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "junk"), "x");
        var name = _builder.Build(KindRoot, r, Movie(media), "PG", "PG", NoTags);
        Assert.Equal(new[] { name }, KindRootEntries().Select(Path.GetFileName).ToArray());
    }

    [Theory]
    [InlineData(300, 'a')]
    [InlineData(300, '\u4e2d')]
    public void LongTitlesAreTruncatedToByteBudget(int length, char ch)
    {
        var media = MakeFile("a/x.mkv");
        var sub = MakeFile("a/x.en.srt");
        var name = _builder.Build(KindRoot, Rec(new string(ch, length), 1995), Movie(media, sub), "PG", "PG", NoTags);
        var title = name[..name.IndexOf(" [swm-", StringComparison.Ordinal)];
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(title) <= 120);
        Assert.EndsWith(" (1995)", title, StringComparison.Ordinal);
        foreach (var f in Directory.GetFileSystemEntries(Path.Combine(KindRoot, name)))
        {
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(Path.GetFileName(f)) <= 255);
        }
    }

    [Fact]
    public void TruncationNeverSplitsSurrogatePair()
    {
        var media = MakeFile("a/x.mkv");
        var title = new string('a', 117) + "\U0001F600\U0001F600";
        var name = _builder.Build(KindRoot, Rec(title, null), Movie(media), "PG", "PG", NoTags);
        var t = name[..name.IndexOf(" [swm-", StringComparison.Ordinal)];
        Assert.Equal(new string('a', 117), t);
    }

    [Fact]
    public void OverlongSidecarIsSkippedNotFatal()
    {
        var media = MakeFile("a/x.mkv");
        var sub = MakeFile("a/x." + new string('l', 200) + ".srt");
        var name = _builder.Build(KindRoot, Rec(new string('a', 200)), Movie(media, sub), "PG", "PG", NoTags);
        Assert.False(File.Exists(Path.Combine(KindRoot, name, name + "." + new string('l', 200) + ".srt")));
        Assert.True(File.Exists(Path.Combine(KindRoot, name, name + ".mkv")));
    }

    [Fact]
    public void DuplicateEpisodeFileNameInSeasonFirstWinsAndBuildSucceeds()
    {
        var root = Path.Combine(_dir, "root", "recipient", "shows");
        var e1 = MakeFile("A/Show - S01E01.mkv", "first");
        var e2 = MakeFile("B/Show - S01E01.mkv", "second");
        var eps = new List<(int, int, string)> { (1, 1, e1), (1, 1, e2) };
        var name = _builder.Build(root, Rec("Show", 2010, ShareKind.Series), new SourceMedia("x", new List<string>(), eps), "PG", "PG", NoTags);
        var link = new FileInfo(Path.Combine(root, name, "Season 01", "Show - S01E01.mkv"));
        Assert.Equal(e1, link.LinkTarget);
    }

    private string ShowsRoot => Path.Combine(_dir, "root", "recipient", "shows");

    private static SourceMedia Show(List<(int, int, string)> eps, Dictionary<string, IReadOnlyList<string>>? subs = null) =>
        new(string.Empty, new List<string>(), eps, subs);

    [Fact]
    public void EpisodeSidecarSubtitlesAreLinkedBesideTheEpisodeWithItsBaseName()
    {
        var e1 = MakeFile("Show/S01/Show - S01E01.mkv");
        // E02's base is 251 bytes, so its NFO (255) fits but a ".subtitle" link beside it (260 bytes) does not: skipped.
        var e2 = MakeFile("Show/S01/" + new string('e', 251) + ".ts");
        var en = MakeFile("Show/S01/Show - S01E01.en.srt");
        var other = MakeFile("Show/S01/foreign.forced.ass");
        var tooLong = MakeFile("Show/S01/x.subtitle");
        var subs = new Dictionary<string, IReadOnlyList<string>> { [e1] = new[] { en, other }, [e2] = new[] { tooLong } };

        var name = _builder.Build(ShowsRoot, Rec("Show", 2010, ShareKind.Series), Show(new() { (1, 1, e1), (1, 2, e2) }, subs), "PG", "PG", NoTags);

        var season = Path.Combine(ShowsRoot, name, "Season 01");
        Assert.Equal(en, new FileInfo(Path.Combine(season, "Show - S01E01.en.srt")).LinkTarget);
        Assert.Equal(other, new FileInfo(Path.Combine(season, "Show - S01E01.ass")).LinkTarget);
        Assert.Equal(
            new[] { "Show - S01E01.ass", "Show - S01E01.en.srt", "Show - S01E01.mkv", "Show - S01E01.nfo", new string('e', 251) + ".nfo", new string('e', 251) + ".ts", "season.nfo" },
            Directory.GetFileSystemEntries(season).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(_builder.LinksResolve(ShowsRoot, name));
    }

    [Fact]
    public void AnEpisodeWhoseNfoCannotBeWrittenIsNotLinkedAtAll()
    {
        var e1 = MakeFile("Show/S01/Show - S01E01.mkv");
        // 252 bytes + ".nfo" = 256: no NFO can sit beside this episode, so it gets no link and no subtitles either.
        var tooLong = MakeFile("Show/S01/" + new string('f', 252) + ".ts");
        var sub = MakeFile("Show/S01/f.srt");
        var media = Show(new() { (1, 1, e1), (1, 2, tooLong) }, new() { [tooLong] = new[] { sub } });
        var r = Rec("Show", 2010, ShareKind.Series);

        r = r with { FolderName = _builder.Build(ShowsRoot, r, media, "GB-15", "12", CopyTags) };

        Assert.Equal(
            new[] { "Show - S01E01.mkv", "Show - S01E01.nfo", "season.nfo" },
            Directory.GetFileSystemEntries(Path.Combine(ShowsRoot, r.FolderName, "Season 01")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { Path.Combine("Season 01", "Show - S01E01.mkv") }, ShareFolderBuilder.EpisodeLinks(media));
        Assert.True(_builder.Matches(ShowsRoot, r, media, "GB-15", "12", CopyTags)); // Build and Matches agree
    }

    [Fact]
    public void AnUnreadableNfoDoesNotMatchSoTheCopyIsRebuilt()
    {
        var e1 = MakeFile("Show/S01/Show - S01E01.mkv");
        var media = Show(new() { (1, 1, e1) });
        var r = Rec("Show", 2010, ShareKind.Series);
        r = r with { FolderName = _builder.Build(ShowsRoot, r, media, "GB-15", "12", CopyTags) };
        Assert.True(_builder.Matches(ShowsRoot, r, media, "GB-15", "12", CopyTags)); // control

        var unreadable = new UnreadableNfoBuilder();
        Assert.False(unreadable.Matches(ShowsRoot, r, media, "GB-15", "12", CopyTags));
        var movie = Movie(MakeFile("Heat (1995)/Heat (1995).mkv"));
        var m = Rec();
        m = m with { FolderName = _builder.Build(KindRoot, m, movie, "GB-18", "12", CopyTags) };
        Assert.False(unreadable.Matches(KindRoot, m, movie, "GB-18", "12", CopyTags));
    }

    private sealed class UnreadableNfoBuilder : ShareFolderBuilder
    {
        protected override byte[] ReadNfoBytes(string path) => throw new UnauthorizedAccessException("Simulated unreadable " + path);
    }

    [Fact]
    public void EpisodeSubtitlesWithTheSameLinkNameFirstWinsAndMissingFilesAreSkipped()
    {
        var e1 = MakeFile("Show/S01/Show - S01E01.mkv");
        var a = MakeFile("Show/S01/a.srt", "first");
        var b = MakeFile("Show/S01/b.srt", "second");
        var gone = Path.Combine(_dir, "orig", "Show", "S01", "Show - S01E01.de.srt");
        var subs = new Dictionary<string, IReadOnlyList<string>> { [e1] = new[] { a, b, gone } };

        var name = _builder.Build(ShowsRoot, Rec("Show", 2010, ShareKind.Series), Show(new() { (1, 1, e1) }, subs), "PG", "PG", NoTags);

        var season = Path.Combine(ShowsRoot, name, "Season 01");
        Assert.Equal(a, new FileInfo(Path.Combine(season, "Show - S01E01.srt")).LinkTarget);
        Assert.False(File.Exists(Path.Combine(season, "Show - S01E01.de.srt")));
        Assert.Equal(4, Directory.GetFileSystemEntries(season).Length); // the episode, one subtitle, the episode NFO, season.nfo
    }

    private static readonly string[] CopyTags = { "swm-abc", "kids" };

    [Fact]
    public void MatchesIsTrueForWhatBuildMadeAndFalseForAnyChangeOfLinksOrNfoContent()
    {
        var e1 = MakeFile("Show/S01/Show - S01E01.mkv");
        var e2 = MakeFile("Show/S01/Show - S01E02.mkv");
        var e3 = MakeFile("Show/S01/Show - S01E03.mkv");
        var sub = MakeFile("Show/S01/Show - S01E01.en.srt");
        var subs = new Dictionary<string, IReadOnlyList<string>> { [e1] = new[] { sub } };
        var media = Show(new() { (1, 1, e1), (1, 2, e2) }, subs);
        var r = Rec("Show", 2010, ShareKind.Series);
        r = r with { FolderName = _builder.Build(ShowsRoot, r, media, "GB-15", "12", CopyTags) };
        var season = Path.Combine(ShowsRoot, r.FolderName, "Season 01");

        Assert.True(_builder.Matches(ShowsRoot, r, media, "GB-15", "12", CopyTags));
        Assert.False(_builder.Matches(ShowsRoot, r, Show(new() { (1, 1, e1), (1, 2, e2), (1, 3, e3) }, subs), "GB-15", "12", CopyTags)); // added
        Assert.False(_builder.Matches(ShowsRoot, r, Show(new() { (1, 2, e2) }), "GB-15", "12", CopyTags)); // removed
        Assert.False(_builder.Matches(ShowsRoot, r, Show(new() { (1, 1, e1), (1, 3, e2) }, subs), "GB-15", "12", CopyTags)); // renumbered
        Assert.False(_builder.Matches(ShowsRoot, r, media, "GB-15", "PG", CopyTags)); // cap changed
        Assert.False(_builder.Matches(ShowsRoot, r, media, "GB-15", "12", new[] { "swm-abc" })); // tags changed
        Assert.False(_builder.Matches(ShowsRoot, r, media, "GB-18", "12", CopyTags)); // true rating changed
        Assert.False(_builder.Matches(ShowsRoot, r with { FolderName = "No Such Folder [swm-00000000]" }, media, "GB-15", "12", CopyTags));

        File.WriteAllText(Path.Combine(season, "Show - S01E02.nfo"), File.ReadAllText(Path.Combine(season, "Show - S01E02.nfo")).Replace("<tag>kids</tag>", string.Empty, StringComparison.Ordinal));
        Assert.False(_builder.Matches(ShowsRoot, r, media, "GB-15", "12", CopyTags)); // one episode NFO lost a tag

        _builder.Build(ShowsRoot, r, media, "GB-15", "12", CopyTags);
        File.Delete(Path.Combine(season, "season.nfo"));
        Assert.False(_builder.Matches(ShowsRoot, r, media, "GB-15", "12", CopyTags)); // a lost season NFO
    }

    [Fact]
    public void MatchesForAMovieComparesTheNfoContent()
    {
        var media = Movie(MakeFile("Heat (1995)/Heat (1995).mkv"));
        var r = Rec();
        r = r with { FolderName = _builder.Build(KindRoot, r, media, "GB-18", "12", CopyTags) };

        Assert.True(_builder.Matches(KindRoot, r, media, "GB-18", "12", CopyTags));
        Assert.False(_builder.Matches(KindRoot, r, media, "GB-18", "PG", CopyTags));
        Assert.False(_builder.Matches(KindRoot, r, media, "GB-18", "12", new[] { "swm-abc" }));
        Assert.False(_builder.Matches(KindRoot, r with { Title = "Heat 2" }, media, "GB-18", "12", CopyTags));
    }

    [Fact]
    public void EveryEpisodeAndSeasonCarriesTheRatingTheOverrideTheTagsAndTheLock()
    {
        var e1 = MakeFile("Show/S01/Show - S01E01.mkv");
        var s2 = MakeFile("Show/S02/Show - S02E05.mkv");
        var name = _builder.Build(ShowsRoot, Rec("Show", 2010, ShareKind.Series), Show(new() { (1, 1, e1), (2, 5, s2) }), "GB-15", "12", CopyTags);

        foreach (var (rel, season, episode) in new[] { ("Season 01/Show - S01E01.nfo", 1, 1), ("Season 02/Show - S02E05.nfo", 2, 5) })
        {
            var nfo = File.ReadAllText(Path.Combine(ShowsRoot, name, rel));
            Assert.Contains("<episodedetails>", nfo, StringComparison.Ordinal);
            Assert.Contains($"<season>{season}</season>", nfo, StringComparison.Ordinal);
            Assert.Contains($"<episode>{episode}</episode>", nfo, StringComparison.Ordinal);
            AssertProtected(nfo, "12");
        }

        foreach (var season in new[] { 1, 2 })
        {
            var nfo = File.ReadAllText(Path.Combine(ShowsRoot, name, $"Season 0{season}", "season.nfo"));
            Assert.Contains("<season>", nfo, StringComparison.Ordinal);
            Assert.Contains($"<seasonnumber>{season}</seasonnumber>", nfo, StringComparison.Ordinal);
            AssertProtected(nfo, "12");
        }

        // Uncapped recipient: no customrating at any level.
        var bare = _builder.Build(ShowsRoot, Rec("Bare", 2010, ShareKind.Series), Show(new() { (1, 1, e1) }), "GB-15", string.Empty, CopyTags);
        Assert.DoesNotContain("customrating", File.ReadAllText(Path.Combine(ShowsRoot, bare, "Season 01", "Show - S01E01.nfo")), StringComparison.Ordinal);
        Assert.DoesNotContain("customrating", File.ReadAllText(Path.Combine(ShowsRoot, bare, "Season 01", "season.nfo")), StringComparison.Ordinal);
    }

    private static void AssertProtected(string nfo, string customRating)
    {
        Assert.Contains("<mpaa>GB-15</mpaa>", nfo, StringComparison.Ordinal);
        Assert.Contains($"<customrating>{customRating}</customrating>", nfo, StringComparison.Ordinal);
        Assert.Contains("<tag>swm-abc</tag>", nfo, StringComparison.Ordinal);
        Assert.Contains("<tag>kids</tag>", nfo, StringComparison.Ordinal);
        Assert.Contains("<lockedfields>OfficialRating|Tags</lockedfields>", nfo, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoFilesWithOneBaseNameInASeasonGiveOneLinkAndOneNfo()
    {
        var mkv = MakeFile("Show/S01/Show - S01E01.mkv");
        var mp4 = MakeFile("Show/S01/Show - S01E01.mp4");
        var name = _builder.Build(ShowsRoot, Rec("Show", 2010, ShareKind.Series), Show(new() { (1, 1, mkv), (1, 1, mp4) }), "PG", "PG", NoTags);

        Assert.Equal(
            new[] { "Show - S01E01.mkv", "Show - S01E01.nfo", "season.nfo" },
            Directory.GetFileSystemEntries(Path.Combine(ShowsRoot, name, "Season 01")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(mkv, new FileInfo(Path.Combine(ShowsRoot, name, "Season 01", "Show - S01E01.mkv")).LinkTarget);
    }

    [Fact]
    public void AnEpisodeWithoutRealNumbersIsNeverLinkedAndNumbersAreNeverGuessed()
    {
        var good = MakeFile("Show/S01/Show - S01E01.mkv");
        var noEpisode = MakeFile("Show/S01/Show - Extra.mkv");
        var noSeason = MakeFile("Show/Unknown/Mystery.mkv");
        var media = Show(new() { (1, 1, good), (1, 0, noEpisode), (-1, 3, noSeason) });

        var name = _builder.Build(ShowsRoot, Rec("Show", 2010, ShareKind.Series), media, "PG", "PG", NoTags);

        var all = Directory.EnumerateFileSystemEntries(Path.Combine(ShowsRoot, name), "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Path.Combine(ShowsRoot, name), f)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "Season 01", Path.Combine("Season 01", "Show - S01E01.mkv"), Path.Combine("Season 01", "Show - S01E01.nfo"), Path.Combine("Season 01", "season.nfo"), "tvshow.nfo" }, all);
        Assert.Equal(new[] { Path.Combine("Season 01", "Show - S01E01.mkv") }, ShareFolderBuilder.EpisodeLinks(media));
    }

    [Fact]
    public void EpisodeLinksListsEpisodesInOrderWithoutSubtitlesOrDuplicates()
    {
        var e1 = MakeFile("A/Show - S01E01.mkv");
        var dup = MakeFile("B/Show - S01E01.mkv");
        var e2 = MakeFile("A/Show - S02E01.mkv");
        var sub = MakeFile("A/Show - S01E01.srt");
        var special = MakeFile("A/Show - S00E01.mkv");
        var media = Show(new() { (0, 1, special), (1, 1, e1), (1, 1, dup), (2, 1, e2) }, new() { [e1] = new[] { sub } });

        // Regular seasons first, Season 00 specials last.
        Assert.Equal(
            new[] { Path.Combine("Season 01", "Show - S01E01.mkv"), Path.Combine("Season 02", "Show - S02E01.mkv"), Path.Combine("Season 00", "Show - S00E01.mkv") },
            ShareFolderBuilder.EpisodeLinks(media));
    }

    [Fact]
    public void LeadingDotsAndSpacesAreTrimmedFromTitle()
    {
        var media = MakeFile("a/x.mkv");
        var name = _builder.Build(KindRoot, Rec("..  .Hidden", null), Movie(media), "PG", "PG", NoTags);
        Assert.StartsWith("Hidden [swm-", name, StringComparison.Ordinal);
    }

    // ---------- I4: a delete never leaves a link without its NFO where Jellyfin scans ----------

    // Deletes the first file it meets, then fails: a recursive delete that stops part-way.
    private sealed class HalfDeleteBuilder : ShareFolderBuilder
    {
        public int Fails { get; set; } = 1;

        protected override void DeleteRecursive(string path)
        {
            if (Fails > 0)
            {
                Fails--;
                var first = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).First();
                File.Delete(first);
                throw new IOException("Simulated failure part-way through deleting " + path);
            }

            base.DeleteRecursive(path);
        }
    }

    // Every entry under the kind root Jellyfin would index (not dot-prefixed), recursively.
    private List<string> Scannable(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(e => Path.GetRelativePath(root, e))
            .Where(rel => !rel.Split(Path.DirectorySeparatorChar).Any(seg => seg.StartsWith('.')))
            .ToList();

    [Fact]
    public void I4_a_remove_that_fails_part_way_leaves_nothing_scannable_and_the_next_sweep_finishes_it()
    {
        var builder = new HalfDeleteBuilder();
        var media = MakeFile("a/x.mkv", "precious");
        var name = builder.Build(KindRoot, Rec(), Movie(media), "PG", "PG", NoTags);

        builder.Remove(KindRoot, name); // the rename succeeded, so the copy is gone even though the delete failed

        Assert.Empty(Scannable(KindRoot));
        var leftover = Assert.Single(Directory.GetDirectories(KindRoot));
        Assert.StartsWith(".swm-del-", Path.GetFileName(leftover), StringComparison.Ordinal);
        builder.SweepKindRoot(KindRoot, new HashSet<string>());
        Assert.Empty(Directory.GetFileSystemEntries(KindRoot));
        Assert.Equal("precious", File.ReadAllText(media));
    }

    [Fact]
    public void I4_a_rebuild_whose_old_folder_will_not_delete_still_ends_with_only_the_new_copy_scannable()
    {
        var builder = new HalfDeleteBuilder { Fails = 0 };
        var media = MakeFile("a/x.mkv");
        var r = Rec();
        var name = builder.Build(KindRoot, r, Movie(media), "PG", "PG", NoTags);
        builder.Fails = 1;

        builder.Build(KindRoot, r, Movie(media), "15", "12", NoTags);

        Assert.Equal(new[] { name, Path.Combine(name, name + ".mkv"), Path.Combine(name, name + ".nfo") }.Order(StringComparer.Ordinal), Scannable(KindRoot).Order(StringComparer.Ordinal));
        Assert.Contains("<mpaa>15</mpaa>", File.ReadAllText(Path.Combine(KindRoot, name, name + ".nfo")), StringComparison.Ordinal);
    }

    [Fact]
    public void I4_a_copy_folder_that_is_itself_a_link_is_removed_as_a_link_and_never_followed()
    {
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        Directory.CreateDirectory(KindRoot);
        var name = "Trap (2000) [swm-12345678]";
        Directory.CreateSymbolicLink(Path.Combine(KindRoot, name), outside);

        _builder.Remove(KindRoot, name);

        Assert.False(Path.Exists(Path.Combine(KindRoot, name)));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "keep.txt")));
    }

    // ---------- I5: copy folders no record names are swept ----------

    [Fact]
    public void I5_the_sweep_removes_marker_folders_no_record_names_and_leaves_known_and_unmarked_ones()
    {
        var media = MakeFile("a/x.mkv");
        var known = _builder.Build(KindRoot, Rec("Known"), Movie(media), "PG", "PG", NoTags);
        var stray = _builder.Build(KindRoot, Rec("Stray"), Movie(media), "PG", "PG", NoTags);
        Directory.CreateDirectory(Path.Combine(KindRoot, "Someone's Own Folder (1999)"));
        Directory.CreateDirectory(Path.Combine(KindRoot, "Looks Similar [swm-xyz]"));
        Directory.CreateDirectory(Path.Combine(KindRoot, ".swm-tmp-" + Guid.NewGuid().ToString("N")));

        _builder.SweepKindRoot(KindRoot, new HashSet<string> { known });

        Assert.True(Directory.Exists(Path.Combine(KindRoot, known)));
        Assert.False(Directory.Exists(Path.Combine(KindRoot, stray)));
        Assert.True(Directory.Exists(Path.Combine(KindRoot, "Someone's Own Folder (1999)")));
        Assert.True(Directory.Exists(Path.Combine(KindRoot, "Looks Similar [swm-xyz]")));
        Assert.Empty(Directory.GetDirectories(KindRoot, ".swm-*"));
        Assert.True(File.Exists(media));
    }
}
