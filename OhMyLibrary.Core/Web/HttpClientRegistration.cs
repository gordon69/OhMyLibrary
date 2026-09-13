using System.Net;
using Microsoft.Extensions.DependencyInjection;
using OhMyLibrary.Core.Abstractions;

namespace OhMyLibrary.Core.Web;

/// <summary>
/// Registers the Steam HTTP clients with <c>IHttpClientFactory</c>.
/// </summary>
public static class HttpClientRegistration
{
    /// <summary>Identifies the launcher to Valve's servers.</summary>
    public const string UserAgent = "OhMyLibrary/0.1 (+https://github.com/)";

    /// <summary>Per-request timeout shared by both clients.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Adds <see cref="ISteamWebApiClient"/> and <see cref="ITagDataClient"/> as typed clients.
    /// </summary>
    /// <param name="services">The container to add to.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    /// Both clients need <c>SteamOptions</c> bound and logging registered; neither reads the API key
    /// from anywhere but those options.
    /// </remarks>
    public static IServiceCollection AddOhMyLibraryHttp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddHttpClient<ISteamWebApiClient, SteamWebApiClient>(static http => Configure(http, SteamWebApiClient.BaseAddress))
            .ConfigurePrimaryHttpMessageHandler(CreateHandler);

        services
            .AddHttpClient<ITagDataClient, TagDataClient>(static http => Configure(http, TagDataClient.BaseAddress))
            .ConfigurePrimaryHttpMessageHandler(CreateHandler);

        return services;
    }

    private static void Configure(HttpClient http, Uri baseAddress)
    {
        http.BaseAddress = baseAddress;
        http.Timeout = RequestTimeout;
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        if (!http.DefaultRequestHeaders.UserAgent.TryParseAdd(UserAgent))
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        }
    }

    private static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };
}
