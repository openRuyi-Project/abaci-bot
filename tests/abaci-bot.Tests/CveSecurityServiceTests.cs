using System.Net;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace abaci_bot.Tests;

public class CveSecurityServiceTests
{
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();

    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public DelegatingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    private CveSecurityService CreateService(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var stub = new DelegatingHandlerStub(handler);
        var client = new HttpClient(stub);
        _httpClientFactory.CreateClient("OsvClient").Returns(client);
        _httpClientFactory.CreateClient("NvdClient").Returns(client);

        var options = Microsoft.Extensions.Options.Options.Create(new CveSecurityOptions
        {
            OsvApiBaseUrl = "https://api.osv.dev/v1/vulns",
            NvdApiBaseUrl = "https://services.nvd.nist.gov/rest/json/cves/2.0",
            CacheExpirationHours = 1,
            RequestTimeoutSeconds = 5
        });

        return new CveSecurityService(_httpClientFactory, options);
    }

    [Fact]
    public void DefaultOptions_ContainsCorrectNvdHostnameAndProductionTimeout()
    {
        var options = new CveSecurityOptions();
        Assert.Equal("https://services.nvd.nist.gov/rest/json/cves/2.0", options.NvdApiBaseUrl);
        Assert.Equal("https://api.osv.dev/v1/vulns/", options.OsvApiBaseUrl);
        Assert.True(options.RequestTimeoutSeconds >= 15, "RequestTimeoutSeconds must be at least 15s for NIST NVD latency.");

        var uri = new Uri(options.NvdApiBaseUrl);
        Assert.Equal("services.nvd.nist.gov", uri.Host);
    }

    [Fact]
    public async Task QueryCveAsync_OsvReturnsDirectScore_ParsesAccurately()
    {
        var json = """
        {
            "id": "CVE-2024-3094",
            "summary": "XZ backdoor vulnerability",
            "database_specific": {
                "severity": 10.0
            }
        }
        """;

        var service = CreateService(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });

        var report = await service.QueryCveAsync("CVE-2024-3094");

        Assert.Equal("CVE-2024-3094", report.CveId);
        Assert.Equal(10.0, report.CvssScore);
        Assert.Equal(GitHubLabels.Severity1, report.SeverityLabel);
        Assert.Equal("XZ backdoor vulnerability", report.Summary);
        Assert.Equal("OSV.dev", report.Source);
    }

    [Fact]
    public async Task QueryCveAsync_OsvReturnsCvssVector_CalculatesViaCvssCalculator()
    {
        var json = """
        {
            "id": "CVE-2023-44487",
            "summary": "HTTP/2 Rapid Reset Attack",
            "severity": [
                {
                    "type": "CVSS_V3",
                    "score": "CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:N/I:N/A:H"
                }
            ]
        }
        """;

        var service = CreateService(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });

        var report = await service.QueryCveAsync("CVE-2023-44487");

        Assert.Equal("CVE-2023-44487", report.CveId);
        Assert.Equal(7.5, report.CvssScore);
        Assert.Equal(GitHubLabels.Severity2, report.SeverityLabel);
    }

    [Fact]
    public async Task QueryCveAsync_OsvMissingSummary_FallsBackToDetailsFirstParagraph()
    {
        var json = """
        {
            "id": "CVE-2021-9999",
            "details": "First paragraph describing the critical vulnerability in component.\r\n\r\nSecond paragraph with technical breakdown and reproducer.",
            "database_specific": {
                "severity": 8.5
            }
        }
        """;

        var service = CreateService(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });

        var report = await service.QueryCveAsync("CVE-2021-9999");

        Assert.Equal("First paragraph describing the critical vulnerability in component.", report.Summary);
    }

    [Fact]
    public async Task QueryCveAsync_OsvFails_FallsBackToNvdSuccessfully()
    {
        var nvdJson = """
        {
            "vulnerabilities": [
                {
                    "cve": {
                        "id": "CVE-2022-1234",
                        "descriptions": [
                            { "lang": "en", "value": "NVD description of vulnerability" }
                        ],
                        "metrics": {
                            "cvssMetricV31": [
                                {
                                    "cvssData": {
                                        "baseScore": 6.5
                                    }
                                }
                            ]
                        }
                    }
                }
            ]
        }
        """;

        var service = CreateService(req =>
        {
            if (req.RequestUri!.Host.Contains("osv.dev"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(nvdJson)
            };
        });

        var report = await service.QueryCveAsync("CVE-2022-1234");

        Assert.Equal("CVE-2022-1234", report.CveId);
        Assert.Equal(6.5, report.CvssScore);
        Assert.Equal(GitHubLabels.Severity3, report.SeverityLabel);
        Assert.Equal("NVD", report.Source);
        Assert.Equal("NVD description of vulnerability", report.Summary);
    }

    [Fact]
    public async Task QueryCveAsync_BothSourcesFail_GracefullyReturnsUnknownWithoutThrowing()
    {
        var service = CreateService(req => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var report = await service.QueryCveAsync("CVE-2099-0001");

        Assert.Equal("CVE-2099-0001", report.CveId);
        Assert.Null(report.CvssScore);
        Assert.Equal("Unknown", report.Source);
        Assert.Null(report.SeverityLabel);
        Assert.Equal("Vulnerability details unavailable.", report.Summary);
    }

    [Fact]
    public async Task QueryCveAsync_SubsequentCalls_HitsInMemoryCache()
    {
        var callCount = 0;
        var json = """
        {
            "id": "CVE-2020-0001",
            "summary": "Cached CVE",
            "database_specific": { "severity": 5.0 }
        }
        """;

        var service = CreateService(req =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        });

        var rep1 = await service.QueryCveAsync("CVE-2020-0001");
        var rep2 = await service.QueryCveAsync("CVE-2020-0001");

        Assert.Equal(1, callCount);
        Assert.Same(rep1, rep2);
    }

    [Fact]
    public async Task QueryMultipleCvesAsync_ThrottlesAndReturnsAllDistinctReports()
    {
        var json = """
        {
            "id": "CVE-MULTI",
            "database_specific": { "severity": 4.5 }
        }
        """;

        var service = CreateService(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });

        var reports = await service.QueryMultipleCvesAsync(new[]
        {
            "CVE-2024-0001", "CVE-2024-0002", "CVE-2024-0003", "CVE-2024-0001"
        });

        // 3 distinct CVEs queried
        Assert.Equal(3, reports.Count);
    }
}
