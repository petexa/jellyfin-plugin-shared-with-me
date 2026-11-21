using System;
using System.Linq;
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

/// <summary>Whether shares wait for an administrator's approval.</summary>
public enum ApprovalMode
{
    /// <summary>No approval: every allowed share is made at once (the 0.1 behaviour).</summary>
    None,

    /// <summary>
    /// A share by a profile that is not an administrator and is restricted (the test the playlists use:
    /// <c>SharePlaylistManager.IsRestricted</c>) waits as Pending until an administrator approves or declines it.
    /// </summary>
    LimitedProfiles,
}

/// <summary>Which events are sent to <see cref="PluginConfiguration.NotifyUrl"/>.</summary>
public enum NotifyOn
{
    /// <summary>None.</summary>
    Nothing,

    /// <summary>A share waiting for approval, and its approval or decline.</summary>
    ApprovalsOnly,

    /// <summary>The three approval events, and every share made at once.</summary>
    EverySharing,
}

/// <summary>Plugin settings.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>The longest a share may be kept for when it has an end date, in days.</summary>
    public const int MaxShareDays = 366;

    /// <summary>The longest notification address accepted.</summary>
    public const int MaxNotifyUrlLength = 2048;

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

    /// <summary>Gets or sets whether shares by restricted profiles wait for an administrator's approval.</summary>
    public ApprovalMode ApprovalMode { get; set; } = ApprovalMode.None;

    /// <summary>
    /// Gets or sets the address the server POSTs a small JSON message to when something is shared (see <see cref="NotifyOn"/>);
    /// empty is off. Only an absolute http:// or https:// address is accepted (<see cref="IsValidNotifyUrl"/>): a save with
    /// anything else keeps the previous address, and an address that is not valid is never sent to.
    /// </summary>
    public string NotifyUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets which events are sent to <see cref="NotifyUrl"/>.</summary>
    public NotifyOn NotifyOn { get; set; } = NotifyOn.Nothing;

    /// <summary>
    /// Whether a notification address may be stored and sent to: empty (off), or an absolute http:// or https:// address with a
    /// host, at most <see cref="MaxNotifyUrlLength"/> characters, with no spaces or control characters.
    /// </summary>
    /// <param name="value">The address, already trimmed.</param>
    /// <returns>True when it is empty or usable.</returns>
    public static bool IsValidNotifyUrl(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (value.Length > MaxNotifyUrlLength || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return false;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(uri.Host);
    }
}
