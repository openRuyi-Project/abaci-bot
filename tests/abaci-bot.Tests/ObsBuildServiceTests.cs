using System.Net;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace abaci_bot.Tests;

public class ObsBuildServiceTests
{
    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    [Fact]
    public async Task GetPackageServiceRevisionAsync_UsesPublicEndpoint_AndExtractsRevision()
    {
        var serviceXml = """
            <services>
              <service name="obs_scm">
                <param name="url">https://github.com/openRuyi-Project/openRuyi.git</param>
                <param name="scm">git</param>
                <param name="revision">74616099acaf074402544dde03bc896514bce241</param>
              </service>
            </services>
            """;

        string? requestedUrl = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            requestedUrl = req.RequestUri?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(serviceXml)
            };
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Obs = new ObsOptions
            {
                BaseUrl = "https://build.openruyi.cn",
                Project = "openruyi"
            }
        });

        var service = new ObsBuildService(new HttpClient(handler), options);
        var revision = await service.GetPackageServiceRevisionAsync("openruyi", "curl");

        Assert.Equal("https://build.openruyi.cn/public/source/openruyi/curl/_service", requestedUrl);
        Assert.Equal("74616099acaf074402544dde03bc896514bce241", revision);
    }

    [Fact]
    public async Task GetPackageBuildResultAsync_UsesPublicEndpointWithPackageQuery_AndParsesResult()
    {
        var resultXml = """
            <result repository="riscv64" arch="riscv64" code="published">
              <status package="curl" code="succeeded" />
            </result>
            """;

        string? requestedUrl = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            requestedUrl = req.RequestUri?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(resultXml)
            };
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Obs = new ObsOptions
            {
                BaseUrl = "https://build.openruyi.cn",
                Project = "openruyi"
            }
        });

        var service = new ObsBuildService(new HttpClient(handler), options);
        var result = await service.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64");

        Assert.Equal("https://build.openruyi.cn/public/build/openruyi/_result?package=curl", requestedUrl);
        Assert.Equal(ObsStatusCode.Succeeded, result.Code);
        Assert.Equal("curl", result.Package);
        Assert.Equal("riscv64", result.Arch);
    }
}

