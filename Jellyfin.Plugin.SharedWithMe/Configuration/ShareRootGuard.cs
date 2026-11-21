using System;
using System.IO;
using Jellyfin.Plugin.SharedWithMe.Sharing;

namespace Jellyfin.Plugin.SharedWithMe.Configuration;

/// <summary>
/// Keeps the share root fixed while the plugin manages library access. Under a new root the existing share libraries
/// would count as real libraries and be granted to everyone, so a change is refused (the old root is kept) until every
/// share is removed and everyone is restored.
/// </summary>
public static class ShareRootGuard
{
    /// <summary>The share root a setting means: the folder given, or &lt;data path&gt;/shared-with-me when empty. Normalised.</summary>
    /// <param name="configured">The ShareRoot setting.</param>
    /// <param name="dataPath">The server's data path.</param>
    /// <returns>The full path without a trailing separator.</returns>
    public static string Effective(string? configured, string dataPath) =>
        ShareLayout.Normalise(string.IsNullOrWhiteSpace(configured) ? Path.Combine(dataPath, "shared-with-me") : configured);

    /// <summary>Puts the current ShareRoot back into <paramref name="next"/> when it would change the share root while in charge.</summary>
    /// <param name="current">The configuration in force.</param>
    /// <param name="next">The configuration being saved; changed in place.</param>
    /// <param name="inCharge">Whether the plugin manages library access right now.</param>
    /// <param name="dataPath">The server's data path.</param>
    /// <returns>True when the requested ShareRoot was refused.</returns>
    public static bool KeepWhileInCharge(PluginConfiguration current, PluginConfiguration next, bool inCharge, string dataPath)
    {
        if (!inCharge || string.Equals(Effective(current.ShareRoot, dataPath), Effective(next.ShareRoot, dataPath), StringComparison.Ordinal))
        {
            return false;
        }

        next.ShareRoot = current.ShareRoot;
        return true;
    }
}
