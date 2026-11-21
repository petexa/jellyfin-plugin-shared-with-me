using System;
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

    /// <summary>Administrators, and the profiles listed in <see cref="PluginConfiguration.AllowedSharers"/>.</summary>
    SelectedProfiles,
}

/// <summary>Plugin settings.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>The longest a share may be kept for when it has an end date, in days.</summary>
    public const int MaxShareDays = 366;

    private int _tvShareDays;

    /// <summary>Gets or sets the folder that holds the share libraries. Empty means &lt;data path&gt;/shared-with-me.</summary>
    public string ShareRoot { get; set; } = string.Empty;

    /// <summary>Gets or sets who may share.</summary>
    public WhoMayShare WhoMayShare { get; set; } = WhoMayShare.Everyone;

    /// <summary>
    /// Gets or sets the profiles (user ids) that may share when <see cref="WhoMayShare"/> is
    /// <see cref="WhoMayShare.SelectedProfiles"/>. Ignored for the other values; administrators never need to be listed.
    /// </summary>
    public Guid[] AllowedSharers { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets a value indicating whether every user who may share gets a private playlist "Share with &lt;name&gt;"
    /// per restricted profile, so sharing works from any app (adding a title there shares it). Off removes the playlists;
    /// shares stay.
    /// </summary>
    public bool TvSharingPlaylists { get; set; } = true;

    /// <summary>
    /// Gets or sets how many days a share made from a "Share with" playlist lasts (a TV app has no way to choose); 0, the
    /// default, keeps it until it is removed. Any value is held to 0 to <see cref="MaxShareDays"/>, whether it comes from a
    /// save or from the settings file.
    /// </summary>
    public int TvShareDays
    {
        get => _tvShareDays;
        set => _tvShareDays = Math.Clamp(value, 0, MaxShareDays);
    }
}
