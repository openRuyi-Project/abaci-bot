using abaci_bot.Services;
using NSubstitute;
using Octokit;
using Octokit.Internal;
using Xunit;

namespace abaci_bot.Tests;

public class CancellableHttpClientTests
{
    [Fact]
    public void Constructor_ThrowsOnNullArguments()
    {
        var inner = Substitute.For<IHttpClient>();
        Assert.Throws<ArgumentNullException>(() => new CancellableHttpClient(null!, () => CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => new CancellableHttpClient(inner, null!));
    }

    [Fact]
    public async Task Send_PropagatesAmbientToken_WhenCancellationTokenNone()
    {
        var inner = Substitute.For<IHttpClient>();
        var request = Substitute.For<IRequest>();
        using var cts = new CancellationTokenSource();

        var client = new CancellableHttpClient(inner, () => cts.Token);
        Func<object, object> preprocess = o => o;

        await client.Send(request, CancellationToken.None, preprocess);

        await inner.Received(1).Send(request, cts.Token, preprocess);
    }

    [Fact]
    public async Task Send_PropagatesParameterToken_WhenAmbientTokenNone()
    {
        var inner = Substitute.For<IHttpClient>();
        var request = Substitute.For<IRequest>();
        using var paramCts = new CancellationTokenSource();

        var client = new CancellableHttpClient(inner, () => CancellationToken.None);
        Func<object, object> preprocess = o => o;

        await client.Send(request, paramCts.Token, preprocess);

        await inner.Received(1).Send(request, paramCts.Token, preprocess);
    }

    [Fact]
    public async Task Send_LinksTokens_WhenBothCanBeCanceled()
    {
        var inner = Substitute.For<IHttpClient>();
        var request = Substitute.For<IRequest>();
        using var ambientCts = new CancellationTokenSource();
        using var paramCts = new CancellationTokenSource();

        CancellationToken capturedToken = default;
        var wasCancelledInFlight = false;
        inner.Send(request, Arg.Any<CancellationToken>(), Arg.Any<Func<object, object>>())
            .Returns(ci =>
            {
                capturedToken = ci.Arg<CancellationToken>();
                Assert.True(capturedToken.CanBeCanceled);
                Assert.False(capturedToken.IsCancellationRequested);

                ambientCts.Cancel();
                wasCancelledInFlight = capturedToken.IsCancellationRequested;
                return Task.FromResult(Substitute.For<IResponse>());
            });

        var client = new CancellableHttpClient(inner, () => ambientCts.Token);
        Func<object, object> preprocess = o => o;

        await client.Send(request, paramCts.Token, preprocess);

        Assert.True(wasCancelledInFlight);
    }

    [Fact]
    public async Task Send_DisposesLinkedTokenSource_WhenBothTokensCanBeCanceled()
    {
        var inner = Substitute.For<IHttpClient>();
        var request = Substitute.For<IRequest>();
        using var ambientCts = new CancellationTokenSource();
        using var paramCts = new CancellationTokenSource();

        CancellationToken capturedToken = default;
        inner.Send(request, Arg.Any<CancellationToken>(), Arg.Any<Func<object, object>>())
            .Returns(ci =>
            {
                capturedToken = ci.Arg<CancellationToken>();
                return Task.FromResult(Substitute.For<IResponse>());
            });

        var client = new CancellableHttpClient(inner, () => ambientCts.Token);
        Func<object, object> preprocess = o => o;

        await client.Send(request, paramCts.Token, preprocess);

        Assert.Throws<ObjectDisposedException>(() => capturedToken.WaitHandle);
    }


    [Fact]
    public void SetRequestTimeout_DelegatesToInner()
    {
        var inner = Substitute.For<IHttpClient>();
        var client = new CancellableHttpClient(inner, () => CancellationToken.None);
        var timeout = TimeSpan.FromSeconds(42);

        client.SetRequestTimeout(timeout);

        inner.Received(1).SetRequestTimeout(timeout);
    }

    [Fact]
    public void Dispose_DelegatesToInner()
    {
        var inner = Substitute.For<IHttpClient>();
        var client = new CancellableHttpClient(inner, () => CancellationToken.None);

        client.Dispose();

        inner.Received(1).Dispose();
    }
}
