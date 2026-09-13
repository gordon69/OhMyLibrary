using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OhMyLibrary.Core;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Options;
using OhMyLibrary.Core.Services;

namespace OhMyLibrary.Tests.Services;

/// <summary>
/// The Core composition root, resolved for real.
/// </summary>
/// <remarks>
/// A registration that cannot be satisfied is a startup crash, not a compile error, so the graph the
/// art cache now depends on — resolver to watcher to path resolver — is worth resolving in a test
/// rather than discovering when the window fails to open.
/// </remarks>
public sealed class CoreCompositionTests
{
    [Fact]
    public void TheArtResolverAndTheWatcherResolveAsSharedSingletons()
    {
        using var provider = BuildProvider();

        var resolver = provider.GetRequiredService<ILibraryAssetResolver>();
        var watcher = provider.GetRequiredService<ISteamWatcherService>();

        Assert.Same(resolver, provider.GetRequiredService<ILibraryAssetResolver>());
        Assert.Same(watcher, provider.GetRequiredService<ISteamWatcherService>());
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<IOptions<SteamOptions>>(Microsoft.Extensions.Options.Options.Create(new SteamOptions()));
        services.AddOhMyLibraryCore();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = false,
            ValidateScopes = true,
        });
    }
}
