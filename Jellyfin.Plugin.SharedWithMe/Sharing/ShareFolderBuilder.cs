using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Jellyfin.Plugin.SharedWithMe.Model;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SharedWithMe.Sharing;

/// <summary>The files of the original item that a share links to.</summary>
/// <param name="PrimaryFile">Absolute path of the movie file (ignored for series).</param>
/// <param name="SidecarSubtitles">Absolute paths of subtitle files beside the movie.</param>
/// <param name="Episodes">Series episode files with season and episode numbers (episode 0 when unknown), in order; only files that exist.</param>
/// <param name="EpisodeSubtitles">Sidecar subtitle files of each episode, keyed by the episode file; null for none.</param>
/// <param name="EpisodeDetails">What each episode carries itself (its item id, its own rating and its own tags), keyed by
/// the episode file; an episode without an entry is never shared.</param>
public record SourceMedia(
    string PrimaryFile,
    IReadOnlyList<string> SidecarSubtitles,
    IReadOnlyList<(int Season, int Episode, string File)> Episodes,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? EpisodeSubtitles = null,
    IReadOnlyDictionary<string, EpisodeDetail>? EpisodeDetails = null);

/// <summary>What one original episode carries itself, not what it inherits from its series, season or library.</summary>
/// <param name="Id">The episode's item id.</param>
/// <param name="OfficialRating">Its own rating; empty when it has none (it is then judged by its series).</param>
/// <param name="Tags">Its own tags.</param>
public record EpisodeDetail(Guid Id, string OfficialRating, IReadOnlyList<string> Tags);

/// <summary>Builds and removes the on-disk folder (symlinks plus NFO) that holds one share.</summary>
public class ShareFolderBuilder
{
    private const int MaxNameBytes = 255;
    private const int TitleBudgetBytes = 120;

    private static readonly char[] Forbidden = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

    private readonly ILogger? _logger;

    /// <summary>Initializes a new instance of the <see cref="ShareFolderBuilder"/> class, without logging.</summary>
    public ShareFolderBuilder()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ShareFolderBuilder"/> class.</summary>
    /// <param name="logger">Logs episodes left out of a series copy.</param>
    public ShareFolderBuilder(ILogger<ShareFolderBuilder> logger)
    {
        _logger = logger;
    }

    /// <summary>Creates the share folder and returns its name.</summary>
    /// <param name="kindRoot">The recipient's movies or shows directory.</param>
    /// <param name="r">The share.</param>
    /// <param name="media">Original files to link.</param>
    /// <param name="officialRating">The item's true rating (an episode with a rating of its own carries that one instead).</param>
    /// <param name="customRating">Rating that lifts the copy over the recipient's cap; empty for none.</param>
    /// <param name="tags">Tags to write.</param>
    /// <returns>The folder name, unique within <paramref name="kindRoot"/>.</returns>
    /// <remarks>
    /// Every level of a series copy carries its own protection: <c>tvshow.nfo</c>, one <c>season.nfo</c> per season folder
    /// and one NFO per episode link, each with the true rating, the customrating and the tags (season and episode NFOs
    /// also lock OfficialRating and Tags). Episode NFOs carry the original's season and episode numbers, which a
    /// replace-all refresh would otherwise strip. Episodes sharing a base name within one season (whatever the
    /// extension): the first wins and later ones are skipped, with no link and no NFO. Each episode's sidecar
    /// subtitles are linked beside it with the episode's base name. A sidecar subtitle whose link name would exceed 255
    /// UTF-8 bytes, would repeat a name already linked, or whose file no longer exists is skipped.
    /// </remarks>
    public virtual string Build(string kindRoot, ShareRecord r, SourceMedia media, string officialRating, string customRating, IReadOnlyList<string> tags)
    {
        var name = FolderName(r);
        Directory.CreateDirectory(kindRoot);
        var folder = Path.Combine(kindRoot, name);

        // Build in a dot-prefixed sibling (Jellyfin ignores it), NFO first, then links, then rename into place,
        // so a failure never leaves a half-built folder that Jellyfin could scan without its rating stamp.
        var temp = Path.Combine(kindRoot, TempPrefix + r.Id.ToString("N"));
        DeleteTree(temp);

        try
        {
            Directory.CreateDirectory(temp);
            foreach (var (rel, content) in NfoPlan(r, name, media, officialRating, customRating, tags))
            {
                var path = Path.Combine(temp, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, content);
            }

            if (r.Kind == ShareKind.Series)
            {
                var skipped = new List<string>();
                var links = SeriesPlan(media, skipped);
                foreach (var file in skipped)
                {
                    _logger?.LogWarning("Shared With Me: episode {File} is not shared: the NFO beside its link would need a name over 255 bytes", file);
                }

                foreach (var link in links)
                {
                    File.CreateSymbolicLink(Path.Combine(temp, link.Path), link.Target);
                }
            }
            else
            {
                var primaryBase = Path.GetFileNameWithoutExtension(media.PrimaryFile);
                File.CreateSymbolicLink(Path.Combine(temp, LinkName(name, media.PrimaryFile)), Path.GetFullPath(media.PrimaryFile));
                foreach (var sub in media.SidecarSubtitles)
                {
                    var subName = Path.GetFileName(sub);
                    var suffix = subName.StartsWith(primaryBase, StringComparison.OrdinalIgnoreCase)
                        ? subName[primaryBase.Length..]
                        : Path.GetExtension(subName);
                    var linkName = name + suffix;
                    if (Encoding.UTF8.GetByteCount(linkName) > MaxNameBytes)
                    {
                        continue;
                    }

                    File.CreateSymbolicLink(Path.Combine(temp, linkName), Path.GetFullPath(sub));
                }
            }

            // The live copy goes out of view in one rename, the new one comes in with another, and only then is the old
            // one deleted: a delete that stops part-way never leaves a link without its NFO where Jellyfin scans.
            string? old = null;
            if (IsLink(folder))
            {
                File.Delete(folder);
            }
            else if (Directory.Exists(folder))
            {
                old = Path.Combine(kindRoot, DeletePrefix + Guid.NewGuid().ToString("N"));
                Directory.Move(folder, old);
            }

            Directory.Move(temp, folder);
            if (old is not null)
            {
                DeleteTree(old);
            }
        }
        catch
        {
            DeleteTree(temp);
            throw;
        }

        return name;
    }

    /// <summary>Deletes one share folder. Links are removed, never followed.</summary>
    /// <param name="kindRoot">The recipient's movies or shows directory.</param>
    /// <param name="folderName">Name of the share folder, a single path segment.</param>
    public virtual void Remove(string kindRoot, string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)
            || folderName is "." or ".."
            || !folderName.Contains(" [swm-", StringComparison.Ordinal)
            || folderName.IndexOfAny(new[] { '/', '\\' }) >= 0
            || folderName.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || folderName.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Folder name must be a single plain name containing the share marker.", nameof(folderName));
        }

        DeleteTree(Path.Combine(kindRoot, folderName));
    }

    /// <summary>True when the folder exists, holds at least one link, and every link still points at an existing file.</summary>
    /// <param name="kindRoot">The recipient's movies or shows directory.</param>
    /// <param name="folderName">Name of the share folder.</param>
    /// <returns>Whether all links resolve.</returns>
    public bool LinksResolve(string kindRoot, string folderName)
    {
        var folder = Path.Combine(kindRoot, folderName);
        if (!Directory.Exists(folder))
        {
            return false;
        }

        var links = Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .Where(f => f.LinkTarget is not null)
            .ToList();

        // File.Exists is true for a dangling link on Unix, so resolve to the final target and test that.
        return links.Count > 0 && links.All(l => l.ResolveLinkTarget(returnFinalTarget: true)?.Exists == true);
    }

    /// <summary>
    /// Whether a copy is exactly what <see cref="Build"/> would make now. Every NFO's content must equal what would be
    /// written (so a changed rating, tag, title or episode number is seen at every level); for a series the links must
    /// also be exactly the episode and subtitle links (same paths, same targets), so an added or removed episode is seen.
    /// For a movie the links are not compared (<see cref="LinksResolve"/> does that).
    /// </summary>
    /// <param name="kindRoot">The recipient's movies or shows directory.</param>
    /// <param name="r">The share; its <see cref="ShareRecord.FolderName"/> names the folder.</param>
    /// <param name="media">The original's current files.</param>
    /// <param name="officialRating">The item's true rating.</param>
    /// <param name="customRating">The customrating the copy needs now; empty for none.</param>
    /// <param name="tags">The tags the copy needs now.</param>
    /// <returns>True when nothing needs rewriting or relinking.</returns>
    public bool Matches(string kindRoot, ShareRecord r, SourceMedia media, string officialRating, string customRating, IReadOnlyList<string> tags)
    {
        if (string.IsNullOrEmpty(r.FolderName))
        {
            return false;
        }

        var folder = Path.Combine(kindRoot, r.FolderName);
        if (!Directory.Exists(folder))
        {
            return false;
        }

        foreach (var (rel, content) in NfoPlan(r, r.FolderName, media, officialRating, customRating, tags))
        {
            var path = Path.Combine(folder, rel);
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                if (!ReadNfoBytes(path).AsSpan().SequenceEqual(content))
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false; // unreadable: rebuild it
            }
        }

        if (r.Kind != ShareKind.Series)
        {
            return true;
        }

        var actual = Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .Where(f => f.LinkTarget is not null)
            .ToDictionary(f => Path.GetRelativePath(folder, f.FullName), f => f.LinkTarget!, StringComparer.Ordinal);
        var wanted = SeriesPlan(media).ToDictionary(l => l.Path, l => l.Target, StringComparer.Ordinal);
        return actual.Count == wanted.Count && wanted.All(w => actual.TryGetValue(w.Key, out var t) && t == w.Value);
    }

    /// <summary>Deletes a folder and everything in it; a seam so tests can make a delete fail part-way.</summary>
    /// <param name="path">The folder.</param>
    protected virtual void DeleteRecursive(string path) => Directory.Delete(path, recursive: true);

    /// <summary>The name prefix of a folder being deleted: dot-prefixed, so Jellyfin never indexes it.</summary>
    public const string DeletePrefix = ".swm-del-";

    /// <summary>The name prefix of a copy being built (dot-prefixed, never indexed).</summary>
    public const string TempPrefix = ".swm-tmp-";

    private static readonly Regex Marker = new(@" \[swm-[0-9a-f]{8}\]$", RegexOptions.CultureInvariant);

    /// <summary>Whether a folder name carries the share marker the builder gives every copy (<c> [swm-&lt;8 hex&gt;]</c> at the end).</summary>
    /// <param name="name">A folder name.</param>
    /// <returns>True for a copy folder name.</returns>
    public static bool IsCopyFolderName(string name) => Marker.IsMatch(name);

    /// <summary>
    /// Deletes a folder (or a file) so that nothing scannable is ever left half-deleted: a link is removed itself, never
    /// followed; a folder is first renamed to a dot-prefixed sibling (<see cref="DeletePrefix"/>, which Jellyfin does not
    /// index) and then deleted. A delete that fails after the rename is logged and finished by the next sweep
    /// (<see cref="SweepKindRoot"/>); a rename that fails throws, and the folder is still whole.
    /// </summary>
    /// <param name="path">The folder.</param>
    public void DeleteTree(string path)
    {
        if (IsLink(path))
        {
            File.Delete(path);
            return;
        }

        if (File.Exists(path))
        {
            File.Delete(path);
            return;
        }

        if (!Directory.Exists(path))
        {
            return;
        }

        var doomed = path;
        if (!Path.GetFileName(path).StartsWith('.'))
        {
            doomed = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, DeletePrefix + Guid.NewGuid().ToString("N"));
            Directory.Move(path, doomed);
        }

        try
        {
            // Directory.Delete removes links inside, never what they point at.
            DeleteRecursive(doomed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Shared With Me: {Folder} is out of view but could not be deleted; the next sweep retries", doomed);
        }
    }

    /// <summary>
    /// Clears one kind root (<c>&lt;share root&gt;/&lt;recipient&gt;/movies|shows</c>): every copy folder (share marker)
    /// whose name is not in <paramref name="keep"/>, and every left-over delete or build folder. Nothing without the marker
    /// is touched; a kind root that is a link is never followed. One entry that cannot be removed is logged and the rest go on.
    /// </summary>
    /// <param name="kindRoot">The kind root.</param>
    /// <param name="keep">Copy folder names a record still names.</param>
    public void SweepKindRoot(string kindRoot, ISet<string> keep)
    {
        if (IsLink(kindRoot) || !Directory.Exists(kindRoot))
        {
            return;
        }

        foreach (var entry in Directory.GetFileSystemEntries(kindRoot))
        {
            var name = Path.GetFileName(entry);
            var leftover = name.StartsWith(DeletePrefix, StringComparison.Ordinal) || name.StartsWith(TempPrefix, StringComparison.Ordinal);
            if (!leftover && (!IsCopyFolderName(name) || keep.Contains(name)))
            {
                continue;
            }

            try
            {
                if (!leftover)
                {
                    _logger?.LogInformation("Shared With Me: removing the copy folder {Folder}, which no share names", entry);
                }

                DeleteTree(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogError(ex, "Shared With Me: could not remove {Folder}", entry);
            }
        }
    }

    /// <summary>Whether a path is a symbolic link (dangling or not).</summary>
    /// <param name="path">The path.</param>
    /// <returns>True for a link.</returns>
    public static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Reads an NFO of an existing copy; a seam so tests can make it unreadable.</summary>
    /// <param name="path">The NFO path.</param>
    /// <returns>Its bytes.</returns>
    protected virtual byte[] ReadNfoBytes(string path) => File.ReadAllBytes(path);

    /// <summary>
    /// The paths, relative to a series copy's folder, of its episode links (subtitles left out): regular seasons first,
    /// in season and episode order, then Season 00 specials.
    /// </summary>
    /// <param name="media">The original's files.</param>
    /// <returns>For example <c>Season 01/Show S01E01.mkv</c>.</returns>
    public static IReadOnlyList<string> EpisodeLinks(SourceMedia media) =>
        SeriesPlan(media).Where(l => l.IsEpisode).OrderBy(l => l.Season == 0 ? 1 : 0).Select(l => l.Path).ToList();

    // The NFO beside an episode link (same relative folder), or null when its name would exceed 255 bytes.
    private static string? EpisodeNfoName(string linkPath)
    {
        var name = Path.GetFileNameWithoutExtension(linkPath) + ".nfo";
        return Encoding.UTF8.GetByteCount(name) > MaxNameBytes ? null : Path.Combine(Path.GetDirectoryName(linkPath) ?? string.Empty, name);
    }

    // Every NFO of a copy, by path relative to its folder, with the exact bytes Build writes.
    private static List<(string Path, byte[] Content)> NfoPlan(ShareRecord r, string folderName, SourceMedia media, string officialRating, string customRating, IReadOnlyList<string> tags)
    {
        if (r.Kind != ShareKind.Series)
        {
            return new() { (folderName + ".nfo", NfoBytes(w => TitleNfo(w, "movie", r, officialRating, customRating, tags))) };
        }

        var plan = new List<(string Path, byte[] Content)> { ("tvshow.nfo", NfoBytes(w => TitleNfo(w, "tvshow", r, officialRating, customRating, tags))) };
        var links = SeriesPlan(media).Where(l => l.IsEpisode).ToList();
        foreach (var season in links.Select(l => l.Season).Distinct())
        {
            plan.Add((Path.Combine(SeasonFolder(season), "season.nfo"), NfoBytes(w =>
            {
                w.WriteStartElement("season");
                w.WriteElementString("seasonnumber", season.ToString(CultureInfo.InvariantCulture));
                Protection(w, officialRating, customRating, tags);
                w.WriteEndElement();
            })));
        }

        foreach (var link in links)
        {
            // An episode's own rating wins in Jellyfin, so its copy carries that one; without one, the series' rating.
            var rating = media.EpisodeDetails is not null && media.EpisodeDetails.TryGetValue(link.Source, out var own) && !string.IsNullOrEmpty(own.OfficialRating)
                ? own.OfficialRating
                : officialRating;

            // SeriesPlan only links an episode whose NFO name fits, so every link has one.
            plan.Add((EpisodeNfoName(link.Path)!, NfoBytes(w =>
            {
                w.WriteStartElement("episodedetails");
                w.WriteElementString("title", XmlSafe(Path.GetFileNameWithoutExtension(link.Path)));
                w.WriteElementString("season", link.Season.ToString(CultureInfo.InvariantCulture));
                w.WriteElementString("episode", link.Episode.ToString(CultureInfo.InvariantCulture));
                Protection(w, rating, customRating, tags);
                w.WriteEndElement();
            })));
        }

        return plan;
    }

    // The true rating, the lifting customrating (when any), the tags, and the lock. 12.4's MetadataField has no
    // CustomRating member, so only OfficialRating and Tags can be locked; a locked Tags field keeps the tags the item
    // first read (later NFO tag changes do not reach it), while customrating changes still do (api-notes, Task 9).
    private static void Protection(XmlWriter w, string officialRating, string customRating, IReadOnlyList<string> tags)
    {
        w.WriteElementString("mpaa", XmlSafe(officialRating));
        if (!string.IsNullOrEmpty(customRating))
        {
            w.WriteElementString("customrating", XmlSafe(customRating));
        }

        foreach (var t in tags)
        {
            w.WriteElementString("tag", XmlSafe(t));
        }

        w.WriteElementString("lockedfields", "OfficialRating|Tags");
    }

    private static string SeasonFolder(int season) => "Season " + season.ToString("00", CultureInfo.InvariantCulture);

    /// <summary>Gets the name of a movie copy's link: the folder name plus the primary file's extension, as written.</summary>
    /// <param name="folderName">The share folder name.</param>
    /// <param name="primaryFile">The original's primary file (<see cref="SourceMedia.PrimaryFile"/>).</param>
    /// <returns>The link's file name.</returns>
    public static string LinkName(string folderName, string primaryFile) => folderName + Path.GetExtension(primaryFile);

    /// <summary>Gets the path of a share's NFO: <c>tvshow.nfo</c> for a series, <c>&lt;folder&gt;.nfo</c> for a movie.</summary>
    /// <param name="kindRoot">The recipient's movies or shows directory.</param>
    /// <param name="r">The share; its <see cref="ShareRecord.FolderName"/> names the folder.</param>
    /// <returns>The NFO path.</returns>
    public static string NfoPath(string kindRoot, ShareRecord r) =>
        Path.Combine(kindRoot, r.FolderName, r.Kind == ShareKind.Series ? "tvshow.nfo" : r.FolderName + ".nfo");

    /// <summary>Rewrites only the NFO of an existing share folder, atomically (temporary dot file, then a rename).</summary>
    /// <param name="kindRoot">The recipient's movies or shows directory.</param>
    /// <param name="r">The share; its folder must exist.</param>
    /// <param name="officialRating">The item's true rating.</param>
    /// <param name="customRating">Rating that lifts the copy over the recipient's cap; empty for none.</param>
    /// <param name="tags">Tags to write.</param>
    public virtual void RewriteNfo(string kindRoot, ShareRecord r, string officialRating, string customRating, IReadOnlyList<string> tags)
    {
        var path = NfoPath(kindRoot, r);
        var folder = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException("Share folder " + folder + " does not exist.");
        }

        var temp = Path.Combine(folder, ".swm-nfo-" + r.Id.ToString("N"));
        try
        {
            File.WriteAllBytes(temp, NfoBytes(w => TitleNfo(w, r.Kind == ShareKind.Series ? "tvshow" : "movie", r, officialRating, customRating, tags)));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>Checks that the share root can be created and can hold symlinks.</summary>
    /// <param name="shareRoot">The root folder shares are created under.</param>
    /// <exception cref="IOException">The folder cannot hold links; the message says so plainly.</exception>
    public static void ProbeSymlinkSupport(string shareRoot)
    {
        var probeDir = Path.Combine(shareRoot, ".swm-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(shareRoot);
            var target = Path.Combine(shareRoot, ".swm-probe-target-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(target, string.Empty);
            try
            {
                File.CreateSymbolicLink(probeDir, target);
            }
            finally
            {
                File.Delete(probeDir);
                File.Delete(target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new IOException(
                "The share folder '" + shareRoot + "' cannot be created or cannot hold symbolic links, so Shared With Me cannot work there. Choose a folder on a local disk the server can write to.",
                ex);
        }
    }

    /// <summary>Gets the folder name <see cref="Build"/> gives a share: cleaned title, year and the share id marker.</summary>
    /// <param name="r">The share.</param>
    /// <returns>The folder name.</returns>
    public static string FolderName(ShareRecord r)
    {
        var sb = new StringBuilder(r.Title.Length);
        foreach (var c in r.Title)
        {
            sb.Append(char.IsControl(c) || Array.IndexOf(Forbidden, c) >= 0 ? ' ' : c);
        }

        var yearPart = r.Year is { } y ? " (" + y.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty;
        var budget = TitleBudgetBytes - Encoding.UTF8.GetByteCount(yearPart);
        var clean = TrimEdges(string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)));

        // Truncate by UTF-8 bytes on whole runes so a surrogate pair or multi-byte character is never cut.
        var cut = new StringBuilder();
        var bytes = 0;
        foreach (var rune in clean.EnumerateRunes())
        {
            bytes += rune.Utf8SequenceLength;
            if (bytes > budget)
            {
                break;
            }

            cut.Append(rune.ToString());
        }

        var title = TrimEdges(cut.ToString());
        if (title.Length == 0)
        {
            title = "Untitled";
        }

        return title + yearPart + " [swm-" + r.Id.ToString("N")[..8] + "]";
    }

    private static string TrimEdges(string s) => s.Trim('.', ' ');

    // Every link of a series copy: Season NN/<episode file name> (first wins per base name within a season, so two
    // files differing only by extension never share one NFO), then that episode's subtitles beside it named
    // <episode base><suffix>. An episode without real numbers (season below 0, episode below 1) is never linked: the
    // gateway logs and drops those, and numbers are never guessed. An episode whose NFO cannot be written (its name would
    // exceed 255 bytes) is not linked either, nor its subtitles: no level of a copy is ever left without its own
    // protection. (A season's NFO is always "season.nfo", which always fits.) Build and Matches both use this; the files
    // left out for the NFO name are added to <paramref name="skipped"/>.
    private static List<(string Path, string Target, bool IsEpisode, int Season, int Episode, string Source)> SeriesPlan(SourceMedia media, List<string>? skipped = null)
    {
        var plan = new List<(string Path, string Target, bool IsEpisode, int Season, int Episode, string Source)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var bases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (season, number, file) in media.Episodes)
        {
            var seasonName = SeasonFolder(season);
            var fileName = Path.GetFileName(file);
            var rel = Path.Combine(seasonName, fileName);
            if (season < 0 || number < 1 || !bases.Add(Path.Combine(seasonName, Path.GetFileNameWithoutExtension(fileName))) || !seen.Add(rel))
            {
                continue;
            }

            if (EpisodeNfoName(rel) is null)
            {
                skipped?.Add(file);
                continue;
            }

            plan.Add((rel, Path.GetFullPath(file), true, season, number, file));
            if (media.EpisodeSubtitles is null || !media.EpisodeSubtitles.TryGetValue(file, out var subs))
            {
                continue;
            }

            var episodeBase = Path.GetFileNameWithoutExtension(fileName);
            foreach (var sub in subs)
            {
                var subName = Path.GetFileName(sub);
                var linkName = subName.StartsWith(episodeBase, StringComparison.OrdinalIgnoreCase)
                    ? episodeBase + subName[episodeBase.Length..]
                    : episodeBase + Path.GetExtension(subName);
                var subRel = Path.Combine(seasonName, linkName);
                if (Encoding.UTF8.GetByteCount(linkName) > MaxNameBytes || !File.Exists(sub) || !seen.Add(subRel))
                {
                    continue;
                }

                plan.Add((subRel, Path.GetFullPath(sub), false, season, number, file));
            }
        }

        return plan;
    }

    private static string XmlSafe(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                sb.Append(c).Append(s[i + 1]);
                i++;
            }
            else if (!char.IsSurrogate(c) && XmlConvert.IsXmlChar(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static byte[] NfoBytes(Action<XmlWriter> body)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using var stream = new MemoryStream();
        using (var w = XmlWriter.Create(stream, settings))
        {
            w.WriteStartDocument(true);
            body(w);
            w.WriteEndDocument();
        }

        return stream.ToArray();
    }

    // The movie or tvshow NFO: only OfficialRating is locked, so a rewritten tag still reaches the item.
    private static void TitleNfo(XmlWriter w, string root, ShareRecord r, string officialRating, string customRating, IReadOnlyList<string> tags)
    {
        w.WriteStartElement(root);
        w.WriteElementString("title", XmlSafe(r.Title));
        if (r.Year is { } year)
        {
            w.WriteElementString("year", year.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var provider in new[] { "tmdb", "imdb", "tvdb" })
        {
            var hit = r.ProviderIds.FirstOrDefault(p => string.Equals(p.Key, provider, StringComparison.OrdinalIgnoreCase));
            if (hit.Key is not null && !string.IsNullOrEmpty(hit.Value))
            {
                w.WriteStartElement("uniqueid");
                w.WriteAttributeString("type", provider);
                w.WriteString(XmlSafe(hit.Value));
                w.WriteEndElement();
            }
        }

        w.WriteElementString("mpaa", XmlSafe(officialRating));
        if (!string.IsNullOrEmpty(customRating))
        {
            w.WriteElementString("customrating", XmlSafe(customRating));
        }

        foreach (var t in tags)
        {
            w.WriteElementString("tag", XmlSafe(t));
        }

        w.WriteElementString("lockedfields", "OfficialRating");
        w.WriteEndElement();
    }
}
