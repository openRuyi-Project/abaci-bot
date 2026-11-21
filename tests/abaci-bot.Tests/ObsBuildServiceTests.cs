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

    [Theory]
    [InlineData("", "curl")]
    [InlineData("openruyi", "")]
    [InlineData("   ", "curl")]
    public async Task GetPackageServiceRevisionAsync_ReturnsNull_WhenInputsEmpty(string project, string package)
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions());
        var service = new ObsBuildService(new HttpClient(handler), options);

        var rev = await service.GetPackageServiceRevisionAsync(project, package);
        Assert.Null(rev);
    }

    [Fact]
    public async Task GetPackageServiceRevisionAsync_ReturnsNull_WhenHttpErrorOrMissingParamOrInvalidXml()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions());

        // 1. HTTP Error
        var handler404 = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var service404 = new ObsBuildService(new HttpClient(handler404), options);
        Assert.Null(await service404.GetPackageServiceRevisionAsync("openruyi", "curl"));

        // 2. Missing revision param
        var noRevXml = "<services><service><param name=\"url\">http://foo</param></service></services>";
        var handlerNoRev = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(noRevXml)
        });
        var serviceNoRev = new ObsBuildService(new HttpClient(handlerNoRev), options);
        Assert.Null(await serviceNoRev.GetPackageServiceRevisionAsync("openruyi", "curl"));

        // 3. Invalid XML
        var badXmlHandler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<services><unclosed>")
        });
        var serviceBadXml = new ObsBuildService(new HttpClient(badXmlHandler), options);
        Assert.Null(await serviceBadXml.GetPackageServiceRevisionAsync("openruyi", "curl"));
    }

    [Theory]
    [InlineData("succeeded", ObsStatusCode.Succeeded)]
    [InlineData("failed", ObsStatusCode.Failed)]
    [InlineData("building", ObsStatusCode.Building)]
    [InlineData("scheduled", ObsStatusCode.Scheduled)]
    [InlineData("unresolvable", ObsStatusCode.Unresolvable)]
    [InlineData("disabled", ObsStatusCode.Disabled)]
    [InlineData("excluded", ObsStatusCode.Excluded)]
    [InlineData("blocked", ObsStatusCode.Unknown)]
    [InlineData("broken", ObsStatusCode.Unknown)]
    [InlineData("custom_unknown_code", ObsStatusCode.Unknown)]
    public async Task GetPackageBuildResultAsync_ParsesAllStatusCodes(string rawCode, ObsStatusCode expectedCode)
    {
        var xml = $"""
            <resultlist>
              <result project="openruyi" repository="riscv64" arch="riscv64" code="finished">
                <status package="curl" code="{rawCode}" details="Build detail note" />
              </result>
            </resultlist>
            """;

        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml)
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions());
        var service = new ObsBuildService(new HttpClient(handler), options);

        var result = await service.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64");

        Assert.Equal(expectedCode, result.Code);
        Assert.Equal(rawCode, result.RawCode);
        Assert.Equal("curl", result.Package);
        Assert.Equal("riscv64", result.Repository);
        Assert.Equal("riscv64", result.Arch);
        Assert.Equal("Build detail note", result.Details);
    }

    [Fact]
    public async Task GetPackageBuildResultAsync_ReturnsDefault_WhenEmptyInputsOrHttpErrorOrBadXml()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions());

        // 1. Empty inputs
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = new ObsBuildService(new HttpClient(handler), options);
        var emptyRes = await service.GetPackageBuildResultAsync("", "curl", "riscv64", "riscv64");
        Assert.Equal(ObsStatusCode.Unknown, emptyRes.Code);

        // 2. HTTP error
        var handler502 = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var service502 = new ObsBuildService(new HttpClient(handler502), options);
        var errRes = await service502.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64");
        Assert.Equal(ObsStatusCode.Unknown, errRes.Code);
        Assert.Contains("BadGateway", errRes.Details);

        // 3. Bad XML
        var handlerBad = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<bad xml>>>")
        });
        var serviceBad = new ObsBuildService(new HttpClient(handlerBad), options);
        var badRes = await serviceBad.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64");
        Assert.Equal(ObsStatusCode.Unknown, badRes.Code);
    }

    [Fact]
    public async Task GetPackageBuildResultAsync_FiltersByRepoAndArch_WhenMultiplePresent()
    {
        var xml = """
            <resultlist>
              <result project="openruyi" repository="x86_64" arch="x86_64">
                <status package="curl" code="failed" />
              </result>
              <result project="openruyi" repository="riscv64" arch="riscv64">
                <status package="curl" code="succeeded" />
              </result>
            </resultlist>
            """;

        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml)
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions());
        var service = new ObsBuildService(new HttpClient(handler), options);
        var res = await service.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64");

        Assert.Equal(ObsStatusCode.Succeeded, res.Code);
        Assert.Equal("riscv64", res.Repository);
        Assert.Equal("riscv64", res.Arch);
    }
}

