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
        var statePath = Path.Combine(Path.GetTempPath(), $"lookout-maxpages-{Guid.NewGuid():N}.json");
        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                Lookout = new LookoutUpdateOptions
                {
                    Enabled = true,
                    BaseUrl = "https://lookout.openruyi.cn",
                    StaleThresholdHours = 48,
                    FirstSeenStatePath = statePath
                }
            });

            var source = new LookoutVersionSource(httpClient, options);
            var candidates = await source.GetCandidatesAsync();

            // Must stop at maxPages = 50
            Assert.Equal(50, requestCount);
            Assert.Equal(50 * 200, candidates.Count);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
    }

    [Fact]
    public async Task GetCandidatesAsync_ReturnsEmpty_WhenDisabledOrBaseUrlEmpty()
    {
        var httpClient = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var optionsDisabled = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Lookout = new LookoutUpdateOptions { Enabled = false, BaseUrl = "https://lookout.openruyi.cn" }
        });
        var sourceDisabled = new LookoutVersionSource(httpClient, optionsDisabled);
        Assert.Empty(await sourceDisabled.GetCandidatesAsync());

        var optionsNoUrl = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Lookout = new LookoutUpdateOptions { Enabled = true, BaseUrl = "" }
        });
        var sourceNoUrl = new LookoutVersionSource(httpClient, optionsNoUrl);
        Assert.Empty(await sourceNoUrl.GetCandidatesAsync());
    }

    [Fact]
    public async Task GetCandidatesAsync_HandlesNetworkError_HttpError_AndBadJson()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Lookout = new LookoutUpdateOptions { Enabled = true, BaseUrl = "https://lookout.openruyi.cn" }
        });

        // 1. Network exception
        var handlerThrow = new FakeHttpMessageHandler(_ => throw new HttpRequestException("Network failure"));
        var sourceThrow = new LookoutVersionSource(new HttpClient(handlerThrow), options);
        Assert.Empty(await sourceThrow.GetCandidatesAsync());

        // 2. HTTP 500
        var handler500 = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var source500 = new LookoutVersionSource(new HttpClient(handler500), options);
        Assert.Empty(await source500.GetCandidatesAsync());

        // 3. Bad JSON
        var handlerBadJson = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ not valid json }", System.Text.Encoding.UTF8, "application/json")
        });
        var sourceBadJson = new LookoutVersionSource(new HttpClient(handlerBadJson), options);
        Assert.Empty(await sourceBadJson.GetCandidatesAsync());
    }

    [Fact]
    public async Task GetCandidatesAsync_PersistsFirstSeenAcrossRestarts_AndResetsOnVersionChangeOrPrune()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"lookout-state-{Guid.NewGuid():N}.json");
        var firstObserved = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var laterPoll = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var currentPoll = firstObserved;
        var currentLatest = "2.0.0";
        var includePkg = true;

        var handler = new FakeHttpMessageHandler(_ =>
        {
            object[] items = includePkg
                ? new object[]
                {
                    new
                    {
                        name = "dnf5",
                        monitors = new
                        {
                            version = new
                            {
                                data = new
                                {
                                    current = "1.0.0",
                                    latest = currentLatest,
                                    last_known_relation = "outdated",
                                    stale = false,
                                    updated_at = currentPoll
                                }
                            }
                        }
                    }
                }
                : Array.Empty<object>();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { items }), System.Text.Encoding.UTF8, "application/json")
            };
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Lookout = new LookoutUpdateOptions
            {
                Enabled = true,
                BaseUrl = "https://lookout.openruyi.cn",
                FirstSeenStatePath = statePath
            }
        });

        try
        {
            // 1. First instance observes dnf5@2.0.0 at firstObserved
            var source1 = new LookoutVersionSource(new HttpClient(handler), options);
            var c1 = await source1.GetCandidatesAsync();
            Assert.Single(c1);
            Assert.Equal(firstObserved, c1[0].PublishedAt);
            Assert.True(File.Exists(statePath));

            // 2. Simulated process restart with newer Lookout updated_at -> must preserve firstObserved
            currentPoll = laterPoll;
            var source2 = new LookoutVersionSource(new HttpClient(handler), options);
            var c2 = await source2.GetCandidatesAsync();
            Assert.Single(c2);
            Assert.Equal(firstObserved, c2[0].PublishedAt);

            // 3. New target version -> resets firstSeen to laterPoll
            currentLatest = "2.1.0";
            var c3 = await source2.GetCandidatesAsync();
            Assert.Single(c3);
            Assert.Equal(laterPoll, c3[0].PublishedAt);

            // 4. Full sweep without dnf5 -> prunes dnf5 from persisted state
            includePkg = false;
            Assert.Empty(await source2.GetCandidatesAsync());

            var source3 = new LookoutVersionSource(new HttpClient(handler), options);
            includePkg = true;
            var newerPoll = laterPoll.AddHours(2);
            currentPoll = newerPoll;
            var c4 = await source3.GetCandidatesAsync();
            Assert.Single(c4);
            Assert.Equal(newerPoll, c4[0].PublishedAt);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
    }

    [Fact]
    public async Task GetCandidatesAsync_PrefersUpstreamPublishedAt_AndHandlesCorruptStateFile()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"lookout-corrupt-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(statePath, "{ corrupt json !!!");

        var upstreamPublishedAt = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var pollTime = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

        var handler = new FakeHttpMessageHandler(_ =>
        {
            var payload = new
            {
                items = new object[]
                {
                    new
                    {
                        name = "etcd",
                        monitors = new
                        {
                            version = new
                            {
                                data = new
                                {
                                    current = "3.7.2",
                                    latest = "3.7.4",
                                    last_known_relation = "outdated",
                                    stale = false,
                                    updated_at = pollTime,
                                    published_at = upstreamPublishedAt
                                }
                            }
                        }
                    }
                }
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
            };
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Lookout = new LookoutUpdateOptions
            {
                Enabled = true,
                BaseUrl = "https://lookout.openruyi.cn",
                FirstSeenStatePath = statePath
            }
        });

        try
        {
            var source = new LookoutVersionSource(new HttpClient(handler), options);
            var candidates = await source.GetCandidatesAsync();
            Assert.Single(candidates);
            Assert.Equal(upstreamPublishedAt, candidates[0].PublishedAt);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
    }
}

