using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Data.Repositories;

namespace OhMyLibrary.Data;

/// <summary>
/// Container registration for the storage layer.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the connection factory and every repository as singletons.
    /// </summary>
    /// <remarks>
    /// Repositories are stateless and open a connection per call, so a singleton lifetime costs
    /// nothing and keeps them injectable into the singleton services and view models above them.
    /// The connection factory is a singleton because it owns the one-shot schema creation and, for
    /// an in-memory database, the connection that keeps that database alive.
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="databasePath">
    /// Explicit database location, <see cref="SqliteConnectionFactory.InMemoryPath"/> for an
    /// in-memory database, or <see langword="null"/> for the default under <c>%LOCALAPPDATA%</c>.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddOhMyLibraryData(
        this IServiceCollection services,
        string? databasePath = null)
    {
        SqliteTypeHandlers.Register();

        services.TryAddSingleton<IDbConnectionFactory>(_ => new SqliteConnectionFactory(databasePath));
        services.TryAddSingleton<IGameRepository, GameRepository>();
        services.TryAddSingleton<ITagRepository, TagRepository>();
        services.TryAddSingleton<IFriendRepository, FriendRepository>();
        services.TryAddSingleton<ICollectionRepository, CollectionRepository>();
        services.TryAddSingleton<ISyncMetaRepository, SyncMetaRepository>();

        return services;
    }
}
