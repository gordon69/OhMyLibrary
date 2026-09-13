using Microsoft.Extensions.DependencyInjection;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Services;
using OhMyLibrary.Core.Steam;
using OhMyLibrary.Core.Vdf;

namespace OhMyLibrary.Core;

/// <summary>
/// Composition root for everything in <c>OhMyLibrary.Core</c>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Core parsers, resolvers, clients and services.
    /// </summary>
    /// <remarks>
    /// Everything here is a singleton: the implementations are stateless or hold caches that are
    /// meant to be shared, and none of them own a scoped resource. Options
    /// (<see cref="Options.SteamOptions"/> and friends) are bound by the host, not here, so this
    /// method stays usable from tests with a bare <see cref="ServiceCollection"/>.
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddOhMyLibraryCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Vdf/ — parsers over ValveKeyValue. Stateless, safe to share.
        services.AddSingleton<IAcfReader, AcfReader>();
        services.AddSingleton<IAppInfoReader, AppInfoReader>();
        services.AddSingleton<ILibraryFoldersReader, LibraryFoldersReader>();
        services.AddSingleton<ILoginUsersReader, LoginUsersReader>();

        // Steam/ — installation discovery, shell hand-off and local library art.
        services.AddSingleton<ISteamPathResolver, SteamPathResolver>();
        services.AddSingleton<ISteamUriLauncher, SteamUriLauncher>();

        // One singleton, reached only through its interface. It caches a folder scan per app and
        // keeps that cache honest itself: its constructor subscribes to ISteamWatcherService.Changed
        // and drops the entry for every app named by a LibraryCache change, so no other component
        // has to remember to invalidate it. The container disposes it, which unsubscribes.
        services.AddSingleton<ILibraryAssetResolver, LibraryAssetResolver>();

        // ---------------------------------------------------------------------------------------
        // Web/ — Steam Web API and store tag clients. OWNER: web-api stage, add registrations here.
        // These take an HttpClient, so they are registered with AddHttpClient rather than
        // AddSingleton (the typed client itself is transient over a pooled handler):
        //
        //   services.AddHttpClient<ISteamWebApiClient, SteamWebApiClient>();
        //   services.AddHttpClient<ITagDataClient, TagDataClient>();
        // ---------------------------------------------------------------------------------------

        // Services/ — orchestration over the parsers, clients and repositories. Singletons because
        // they hold the shared library cache, the debounced watcher and the refresh gates. They also
        // depend on the OhMyLibrary.Data repositories, which the host registers.
        services.AddSingleton<IGameLibraryService, GameLibraryService>();
        services.AddSingleton<IInstallStateService, InstallStateService>();
        services.AddSingleton<ITagService, TagService>();
        services.AddSingleton<ICollectionService, CollectionService>();
        services.AddSingleton<IFriendsService, FriendsService>();
        services.AddSingleton<ISteamWatcherService, SteamWatcherService>();

        return services;
    }
}
