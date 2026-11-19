using System.Net;
using System.Text.Json;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace abaci_bot.Tests;

public class LookoutVersionSourceTests
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
    public async Task GetCandidatesAsync_DiscoversOutdatedPackages_AndSkipsYankedAndStale()
    {
        var responsePayload = new
        {
            packages = new object[]
            {
                // Normal outdated package
                new
                {
                    name = "curl",
                    current_version = "8.21.0",
                    latest_version = "8.22.0",
                    last_known_relation = "outdated",
                    stale = false,
                    updated_at = DateTimeOffset.UtcNow
                },
                // Yanked annotation package -> MUST BE SKIPPED
                new
                {
                    name = "go-github-azuread",
                    current_version = "1.10.0",
                    latest_version = "1.10.1",
                    last_known_relation = "outdated",
                    stale = false,
                    annotations = new[]
                    {
                        new { monitor = "yanked", label = "Yanked", scope = "current" }
                    }
                },
                // Stale package older than 48 hours -> MUST BE SKIPPED
                new
                {
                    name = "old-stale-pkg",
                    current_version = "1.0.0",
                    latest_version = "2.0.0",
                    last_known_relation = "outdated",
                    stale = true,
                    updated_at = DateTimeOffset.UtcNow.AddHours(-72)
                },
                // Current package (not outdated) -> MUST BE SKIPPED
                new
                {
                    name = "up-to-date-pkg",
                    current_version = "3.0.0",
                    latest_version = "3.0.0",
                    last_known_relation = "current",
                    stale = false
                }
            }
        };

        var handler = new FakeHttpMessageHandler(req =>
        {
            var json = JsonSerializer.Serialize(responsePayload);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Lookout = new LookoutUpdateOptions
            {
                Enabled = true,
                BaseUrl = "https://lookout.openruyi.cn",
                StaleThresholdHours = 48
            }
        });

        var source = new LookoutVersionSource(httpClient, options);
        var candidates = await source.GetCandidatesAsync();

        Assert.Single(candidates);
        Assert.Equal("curl", candidates[0].PackageName);
        Assert.Equal("8.21.0", candidates[0].CurrentVersion);
        Assert.Equal("8.22.0", candidates[0].TargetVersion);
        Assert.Equal(CandidateOrigin.Lookout, candidates[0].Origin);
    }

    [Fact]
    public async Task GetCandidatesAsync_ParsesNestedMonitorsVersionData_AndDetectsSecurityAdvisory()
    {
        var responsePayload = new
        {
            items = new object[]
            {
                new
                {
                    name = "curl",
                    detail_url = "https://lookout.openruyi.cn/package/curl",
                    monitors = new
                    {
                        version = new
                        {
                            data = new
                            {
                                kind = "release",
                                current = "8.21.0",
                                latest = "8.22.0",
                                last_known_relation = "outdated",
                                stale = false,
                                updated_at = DateTimeOffset.UtcNow,
                                annotations = new object[]
                                {
                                    new { monitor = "security", label = "CVE-2024-1234", scope = "latest" }
                                }
                            }
                        }
                    }
                }
            }
        };

        var handler = new FakeHttpMessageHandler(req =>
        {
            var json = JsonSerializer.Serialize(responsePayload);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Lookout = new LookoutUpdateOptions
            {
                Enabled = true,
                BaseUrl = "https://lookout.openruyi.cn",
                StaleThresholdHours = 48
            }
        });

        var source = new LookoutVersionSource(httpClient, options);
        var candidates = await source.GetCandidatesAsync();

        Assert.Single(candidates);
        Assert.Equal("curl", candidates[0].PackageName);
        Assert.Equal("8.21.0", candidates[0].CurrentVersion);
        Assert.Equal("8.22.0", candidates[0].TargetVersion);
        Assert.True(candidates[0].IsSecurityAdvisory);
        Assert.Equal("https://lookout.openruyi.cn/package/curl", candidates[0].EvidenceUrl);
        Assert.True(candidates[0].PublishedAt.HasValue);
    }

    [Fact]
    public async Task GetCandidatesAsync_RespectsMaxPagesLimit()
    {
        int requestCount = 0;
        var handler = new FakeHttpMessageHandler(req =>
        {
            requestCount++;
            // Always return a full page of 200 items
            var fullPage = new
            {
                packages = Enumerable.Range(1, 200).Select(i => new
                {
                    name = $"pkg-{requestCount}-{i}",
                    current_version = "1.0",
                    latest_version = "1.1",
                    last_known_relation = "outdated",
                    stale = false
                }).ToArray()
            };

            var json = JsonSerializer.Serialize(fullPage);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Lookout = new LookoutUpdateOptions
            {
                Enabled = true,
                BaseUrl = "https://lookout.openruyi.cn",
                StaleThresholdHours = 48
            }
        });

        var source = new LookoutVersionSource(httpClient, options);
        var candidates = await source.GetCandidatesAsync();

        // Must stop at maxPages = 50
        Assert.Equal(50, requestCount);
        Assert.Equal(50 * 200, candidates.Count);
    }
}

