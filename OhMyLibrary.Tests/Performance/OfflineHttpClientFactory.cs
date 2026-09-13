using System.Net;
using System.Net.Http;

namespace OhMyLibrary.Tests.Performance;

/// <summary>
/// Hands out clients that answer every request with 404 without touching the network, so a
/// benchmark measures our own work rather than the Steam CDN's latency.
/// </summary>
public sealed class OfflineHttpClientFactory : IHttpClientFactory
{
    /// <summary>How many requests were answered.</summary>
    public int Requests => Handler.Requests;

    private NotFoundHandler Handler { get; } = new();

    /// <inheritdoc />
    public HttpClient CreateClient(string name) => new(Handler, disposeHandler: false);

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
