using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SharedWithMe.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SharedWithMe;

/// <summary>The Shared With Me plugin.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the running plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets or sets how a configuration save learns whether the plugin is in charge. Set by the plugin's hosted service
    /// once the server's services exist; it runs the given action under the plugin's gate with the current answer.
    /// While unset (very early in startup) a save is treated as in charge, the strictest choice.
    /// </summary>
    public Action<Action<bool>>? InChargeGate { get; set; }

    /// <inheritdoc />
    public override string Name => "Shared With Me";

    /// <summary>The plugin's id. Servers and repositories know the plugin by it: it must never change once published.</summary>
    public const string PluginGuid = "4069397e-cf0d-460a-9a00-e5175c137516";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginGuid);

    /// <summary>
    /// Gets or sets the forced teardown run when the plugin is uninstalled. Set by the plugin's hosted service once the
    /// server's services exist. Jellyfin 12.4 calls <see cref="OnUninstalling"/> on the uninstall request itself, before
    /// it answers and before any restart, with every service still running (api-notes, Task 10).
    /// </summary>
    public Action? Uninstalling { get; set; }

    /// <summary>
    /// Runs the forced teardown (<see cref="Uninstalling"/>). Nothing escapes: the uninstall cannot be retried, so a
    /// failure is logged and the server goes on removing the plugin.
    /// </summary>
    public override void OnUninstalling()
    {
        if (Uninstalling is null)
        {
            // The plugin's hosted service never started, so the server's services were not reached: nothing was put back.
            var warning = "Shared With Me: uninstalled before its background service started, so no clean-up ran. If it was ever "
                + "used, check every profile's library access and blocked tags (swm-...), and remove any \"Shared with\" library "
                + "and \"Share with\" playlist by hand.";
            if (Logger is { } log)
            {
                log(warning);
            }
            else
            {
                Console.Error.WriteLine(warning);
            }

            base.OnUninstalling();
            return;
        }

        try
        {
            Uninstalling.Invoke();
        }
        catch (Exception ex)
        {
            Logger?.Invoke("Shared With Me: the uninstall clean-up failed: " + ex);
        }

        base.OnUninstalling();
    }

    /// <summary>
    /// Saves a new configuration (the dashboard and <c>POST /Plugins/{id}/Configuration</c> both end here). While the
    /// plugin is in charge a changed ShareRoot is refused: the old one is kept, every other field is applied. The list of
    /// chosen sharers is tidied (no empty ids, no repeats). A notification address that is not empty and not an absolute
    /// http:// or https:// address is refused: the previous one is kept. Every save queues the playlists pass, so a change of who may
    /// share, or of the chosen sharers, adds and removes the "Share with" playlists to match.
    /// </summary>
    /// <param name="configuration">The new configuration.</param>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is not PluginConfiguration next)
        {
            base.UpdateConfiguration(configuration);
            return;
        }

        next.AllowedSharers = (next.AllowedSharers ?? Array.Empty<Guid>()).Where(id => id != Guid.Empty).Distinct().ToArray();
        next.NotifyUrl = next.NotifyUrl?.Trim() ?? string.Empty;
        if (!PluginConfiguration.IsValidNotifyUrl(next.NotifyUrl))
        {
            // The address is not echoed: it may carry a secret. The previous one stays.
            Logger?.Invoke("Shared With Me: the notification address was not changed: only an absolute http:// or https:// address is accepted.");
            next.NotifyUrl = PluginConfiguration.IsValidNotifyUrl(Configuration.NotifyUrl) ? Configuration.NotifyUrl : string.Empty;
        }

        void Save(bool inCharge)
        {
            if (ShareRootGuard.KeepWhileInCharge(Configuration, next, inCharge, ApplicationPaths.DataPath))
            {
                Logger?.Invoke("Shared With Me: the share folder was not changed because shares exist. Remove every share and restore everyone first.");
            }

            base.UpdateConfiguration(next);
            ConfigurationSaved?.Invoke();
        }

        if (InChargeGate is { } gate)
        {
            gate(Save);
        }
        else
        {
            Save(true);
        }
    }

    /// <summary>Gets or sets what runs after a configuration save (queues the playlists pass, once services exist).</summary>
    public Action? ConfigurationSaved { get; set; }

    /// <summary>Gets or sets where the configuration guard writes its warning (the server log, once services exist).</summary>
    public Action<string>? Logger { get; set; }

    /// <summary>
    /// The audit page (the plugin's dashboard page, administrators only) and its script, loaded by the page through
    /// <c>data-controller="__plugin/SharedWithMeAudit.js"</c>. The share page for every user is served by the controller
    /// at <c>/SharedWithMe/Page</c>: Jellyfin 12.4 lists plugin pages to administrators only.
    /// </summary>
    /// <returns>The pages.</returns>
    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = "SharedWithMe",
            DisplayName = "Shared With Me",
            EmbeddedResourcePath = GetType().Namespace + ".Web.audit.html",
            EnableInMainMenu = true,
        },
        new PluginPageInfo
        {
            Name = "SharedWithMeAudit.js",
            EmbeddedResourcePath = GetType().Namespace + ".Web.audit.js",
        },
    };
}
