using System;
using System.IO;
using System.Net.Http;
using System.Text;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.SharedWithMe.Configuration;
using Jellyfin.Plugin.SharedWithMe.Server;
using Jellyfin.Plugin.SharedWithMe.Sharing;
using Jellyfin.Plugin.SharedWithMe.Storage;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SharedWithMe;

/// <summary>Registers the plugin's services with the server.</summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<JellyfinServerGateway>();
        serviceCollection.AddSingleton<IServerGateway>(sp => sp.GetRequiredService<JellyfinServerGateway>());

        // Outside the versioned plugin folder so the records and policy snapshots survive an upgrade.
        serviceCollection.AddSingleton(sp => new ShareStore(
            Path.Combine(sp.GetRequiredService<IApplicationPaths>().PluginConfigurationsPath, "SharedWithMe")));

        // Unknown configuration is treated as the strictest setting.
        serviceCollection.AddSingleton(sp => new ShareEligibility(
            sp.GetRequiredService<IServerGateway>(),
            sp.GetRequiredService<ShareStore>(),
            () => Plugin.Instance?.Configuration.WhoMayShare ?? WhoMayShare.AdminsOnly,
            () => Plugin.Instance?.Configuration.AllowedSharers));
        serviceCollection.AddSingleton<LibraryAccessManager>();
        serviceCollection.AddSingleton<ShareFolderBuilder>();
        serviceCollection.AddSingleton<PluginGate>();

        // Notifications: one client of the server's own factory that never follows a redirect (the address is the
        // administrator's, but a redirect could send the body somewhere they did not choose) and gives up after 5 s.
        serviceCollection.AddHttpClient(ShareNotifier.HttpClientName, client => client.Timeout = ShareNotifier.Timeout)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = ShareNotifier.Timeout,
            });
        serviceCollection.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new ShareNotifier(
                () => Plugin.Instance?.Configuration.NotifyUrl,
                () => Plugin.Instance?.Configuration.NotifyOn ?? NotifyOn.Nothing,
                async (uri, body, cancel) =>
                {
                    var client = factory.CreateClient(ShareNotifier.HttpClientName);
                    using var content = new StringContent(body, Encoding.UTF8, "application/json");
                    using var response = await client.PostAsync(uri, content, cancel).ConfigureAwait(false);
                    return (int)response.StatusCode;
                },
                sp.GetRequiredService<ILogger<ShareNotifier>>());
        });
        serviceCollection.AddSingleton(sp =>
        {
            var gateway = sp.GetRequiredService<JellyfinServerGateway>();
            return new ShareService(
                gateway,
                sp.GetRequiredService<ShareStore>(),
                sp.GetRequiredService<ShareEligibility>(),
                sp.GetRequiredService<LibraryAccessManager>(),
                sp.GetRequiredService<ShareFolderBuilder>(),
                () => gateway.ShareRoot,
                sp.GetRequiredService<PluginGate>(),
                sp.GetRequiredService<ILogger<ShareService>>(),
                null,
                null,
                () => Plugin.Instance?.Configuration.ApprovalMode ?? ApprovalMode.None,
                sp.GetRequiredService<ShareNotifier>());
        });
        serviceCollection.AddSingleton(sp =>
        {
            var gateway = sp.GetRequiredService<JellyfinServerGateway>();
            return new ShareSynchronizer(
                gateway,
                sp.GetRequiredService<ShareStore>(),
                sp.GetRequiredService<LibraryAccessManager>(),
                sp.GetRequiredService<ShareFolderBuilder>(),
                () => gateway.ShareRoot,
                sp.GetRequiredService<PluginGate>(),
                sp.GetRequiredService<ILogger<ShareSynchronizer>>());
        });

        serviceCollection.AddSingleton(sp =>
        {
            var sync = sp.GetRequiredService<ShareSynchronizer>();
            return new SharePlaylistManager(
                sp.GetRequiredService<IServerGateway>(),
                sp.GetRequiredService<ShareStore>(),
                sp.GetRequiredService<ShareService>(),
                sp.GetRequiredService<ShareEligibility>(),
                sp.GetRequiredService<LibraryAccessManager>(),
                () => Plugin.Instance?.Configuration.TvSharingPlaylists ?? false,
                sync.CurrentSetupProblem,
                sp.GetRequiredService<PluginGate>(),
                sp.GetRequiredService<ILogger<SharePlaylistManager>>(),
                null,
                () => Plugin.Instance?.Configuration.TvShareDays ?? 0);
        });

        serviceCollection.AddSingleton(sp =>
        {
            var gateway = sp.GetRequiredService<JellyfinServerGateway>();
            return new AuditService(
                gateway,
                sp.GetRequiredService<ShareStore>(),
                sp.GetRequiredService<LibraryAccessManager>(),
                sp.GetRequiredService<ShareFolderBuilder>(),
                sp.GetRequiredService<ShareSynchronizer>(),
                () => gateway.ShareRoot,
                sp.GetRequiredService<PluginGate>(),
                sp.GetRequiredService<ILogger<AuditService>>(),
                null,
                sp.GetRequiredService<SharePlaylistManager>(),
                () =>
                {
                    if (Plugin.Instance is { Configuration.TvSharingPlaylists: true } plugin)
                    {
                        plugin.Configuration.TvSharingPlaylists = false;
                        plugin.SaveConfiguration();
                        return true;
                    }

                    return false;
                },
                () => ShareRootGuard.Effective(null, sp.GetRequiredService<IApplicationPaths>().DataPath));
        });

        // One queue for every trigger. Event handlers only queue: the gateway's own policy writes block on the server
        // and raise user-updated, so a handler that waited for the gate could deadlock.
        serviceCollection.AddSingleton(sp =>
        {
            var sync = sp.GetRequiredService<ShareSynchronizer>();
            var playlists = sp.GetRequiredService<SharePlaylistManager>();
            var logger = sp.GetRequiredService<ILogger<SyncWorker>>();

            // A playlists failure is logged and never hides the synchronizer's report.
            void Playlists(bool full)
            {
                try
                {
                    playlists.Run(full);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Shared With Me: the playlists pass failed");
                }
            }

            return new SyncWorker(
                (work, progress) =>
                {
                    if (work == SyncWork.Full)
                    {
                        var report = sync.Run(progress);
                        if (report.SetupProblem is null)
                        {
                            Playlists(true);
                        }

                        return report;
                    }

                    if (work.HasFlag(SyncWork.Isolation))
                    {
                        sync.EnsureIsolation();
                    }

                    if (work.HasFlag(SyncWork.Playlists))
                    {
                        Playlists(false);
                    }

                    return (SyncReport?)null;
                },
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromSeconds(1),
                logger);
        });
        serviceCollection.AddSingleton(sp => new ExpiryWatch(sp.GetRequiredService<ShareStore>(), sp.GetRequiredService<ILogger<ExpiryWatch>>()));
        serviceCollection.AddHostedService<SyncHost>();
        serviceCollection.AddScoped<IEventConsumer<UserCreatedEventArgs>, UserEventConsumer>();
        serviceCollection.AddScoped<IEventConsumer<UserDeletedEventArgs>, UserEventConsumer>();
        serviceCollection.AddScoped<IEventConsumer<UserUpdatedEventArgs>, UserEventConsumer>();
    }
}
