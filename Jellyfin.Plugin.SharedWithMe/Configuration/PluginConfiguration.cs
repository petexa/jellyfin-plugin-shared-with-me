using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SharedWithMe.Configuration;

/// <summary>Who is allowed to create shares.</summary>
public enum WhoMayShare
{
    /// <summary>Any user.</summary>
    Everyone,

    /// <summary>Only users without a parental-rating cap.</summary>
    UncappedOnly,

    /// <summary>Only administrators.</summary>
    AdminsOnly,
}

/// <summary>Plugin settings.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the folder that holds the share libraries. Empty means &lt;data path&gt;/shared-with-me.</summary>
    public string ShareRoot { get; set; } = string.Empty;

    /// <summary>Gets or sets who may share.</summary>
    public WhoMayShare WhoMayShare { get; set; } = WhoMayShare.Everyone;

    /// <summary>
    /// Gets or sets a value indicating whether every user who may share gets a private playlist "Share with &lt;name&gt;"
    /// per restricted profile, so sharing works from any app (adding a title there shares it). Off removes the playlists;
    /// shares stay.
    /// </summary>
    public bool TvSharingPlaylists { get; set; } = true;
}
