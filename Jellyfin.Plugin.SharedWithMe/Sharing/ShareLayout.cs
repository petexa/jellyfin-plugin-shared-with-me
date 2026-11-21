using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.SharedWithMe.Sharing;

/// <summary>
/// Path rules for the share root. Paths are compared ordinally after <see cref="Path.GetFullPath(string)"/>, whole
/// segments only, with no symlink resolution: a share root reached through a symlink is compared as written.
/// </summary>
public static class ShareLayout
{
    /// <summary>Full path without a trailing separator.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The normalised path.</returns>
    public static string Normalise(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>True when <paramref name="path"/> equals <paramref name="root"/> or lies under it.</summary>
    /// <param name="path">The path.</param>
    /// <param name="root">The folder.</param>
    /// <returns>Whether the path is inside the folder.</returns>
    public static bool IsInside(string path, string root)
    {
        var p = Normalise(path);
        var r = Normalise(root);
        return string.Equals(p, r, StringComparison.Ordinal) || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>True only for the exact share library layout <c>&lt;root&gt;/&lt;32 hex&gt;/movies</c> or <c>/shows</c>.</summary>
    /// <param name="path">The library folder.</param>
    /// <param name="root">The share root.</param>
    /// <returns>Whether the folder is a share library folder.</returns>
    public static bool IsShareLibraryPath(string path, string root)
    {
        var p = Normalise(path);
        var r = Normalise(root);
        if (!p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = p[(r.Length + 1)..].Split(Path.DirectorySeparatorChar);
        return parts.Length == 2
            && parts[0].Length == 32
            && parts[0].All(Uri.IsHexDigit)
            && parts[1] is "movies" or "shows";
    }

    /// <summary>The recipient a share library folder belongs to (the 32-hex segment), or null for any other path.</summary>
    /// <param name="path">The library folder.</param>
    /// <param name="root">The share root.</param>
    /// <returns>The recipient's id, or null.</returns>
    public static Guid? RecipientOf(string path, string root) =>
        IsShareLibraryPath(path, root) && Guid.TryParseExact(Normalise(path)[(Normalise(root).Length + 1)..][..32], "N", out var id) ? id : null;

    /// <summary>
    /// The share-root sanity check every change starts with: the share root must not be inside (or equal to) any library
    /// folder, and no library folder other than the share layout may be inside the share root.
    /// </summary>
    /// <param name="libraryPaths">Every folder of every library (<see cref="IServerGateway.GetAllLibraryPaths"/>).</param>
    /// <param name="shareRoot">The share root.</param>
    /// <returns>The first offending library folder, or null when the share root is usable.</returns>
    public static string? FindOverlap(IEnumerable<string> libraryPaths, string shareRoot) =>
        libraryPaths.FirstOrDefault(p => IsInside(shareRoot, p) || (IsInside(p, shareRoot) && !IsShareLibraryPath(p, shareRoot)));
}
