using Jellyfin.Plugin.WatchParty.Commands;
using Jellyfin.Plugin.WatchParty.Configuration;
using Jellyfin.Plugin.WatchParty.Discord;
using Jellyfin.Plugin.WatchParty.Playback;
using Jellyfin.Plugin.WatchParty.Voting;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.WatchParty;

/// <summary>Registers Watch Party services with Jellyfin's container.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        Func<PluginConfiguration> config = () => Plugin.Instance?.Configuration ?? new PluginConfiguration();

        serviceCollection.AddSingleton(config);
        serviceCollection.AddSingleton(sp => new VoteEngine(
            TimeProvider.System,
            new Random(),
            () => VoteSettings.FromConfig(config())));
        serviceCollection.AddSingleton<Ballot>();
        serviceCollection.AddSingleton<IHostController, HostController>();
        serviceCollection.AddSingleton<ILibrarySearch, LibrarySearch>();
        serviceCollection.AddSingleton<CommandRouter>();

        serviceCollection.AddHttpClient(DiscordRest.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        serviceCollection.AddSingleton<DiscordRest>();
        serviceCollection.AddSingleton(sp => new GatewayClient(
            () => new ClientWebSocketGatewaySocket(),
            TimeProvider.System,
            new Random(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<GatewayClient>>()));
        serviceCollection.AddSingleton<InteractionHandler>();
        serviceCollection.AddSingleton<DiscordBotService>();
        serviceCollection.AddSingleton<IBotStatus>(sp => sp.GetRequiredService<DiscordBotService>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<DiscordBotService>());
    }
}
