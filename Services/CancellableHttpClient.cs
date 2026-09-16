using Octokit;
using Octokit.Internal;

namespace abaci_bot.Services;

/// <summary>
/// Delegating HTTP client that propagates an ambient or contextual <see cref="CancellationToken"/>
/// to Octokit's network transport layer.
/// Octokit client methods do not accept a CancellationToken and pass CancellationToken.None by default.
/// This adapter links the ambient token to ensure timeouts and deadlines cancel requests at the socket/HTTP layer.
/// </summary>
public sealed class CancellableHttpClient : IHttpClient
{
    private readonly IHttpClient _inner;
    private readonly Func<CancellationToken> _tokenAccessor;

    public CancellableHttpClient(IHttpClient inner, Func<CancellationToken> tokenAccessor)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _tokenAccessor = tokenAccessor ?? throw new ArgumentNullException(nameof(tokenAccessor));
    }

    public async Task<IResponse> Send(IRequest request, CancellationToken cancellationToken, Func<object, object> preprocessResponseBody)
    {
        var ambientToken = _tokenAccessor();
        if (!ambientToken.CanBeCanceled)
            return await _inner.Send(request, cancellationToken, preprocessResponseBody);

        if (!cancellationToken.CanBeCanceled)
            return await _inner.Send(request, ambientToken, preprocessResponseBody);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ambientToken, cancellationToken);
        return await _inner.Send(request, linkedCts.Token, preprocessResponseBody);
    }

    public void SetRequestTimeout(TimeSpan timeout)
    {
        _inner.SetRequestTimeout(timeout);
    }

    public void Dispose()
    {
        _inner.Dispose();
    }
}
