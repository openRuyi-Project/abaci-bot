using System.Net;
using abaci_bot.Options;
using abaci_bot.Services;
using static abaci_bot.Services.RustCrateVersionSource;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace abaci_bot.Tests;

public class RustCrateVersionSourceTests
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

    [Theory]
    [InlineData("a", "1/a")]
    [InlineData("ab", "2/ab")]
    [InlineData("abc", "3/a/abc")]
    [InlineData("serde", "se/rd/serde")]
    [InlineData("aes-gcm", "ae/s-/aes-gcm")]
    public void GetSparseIndexPath_FollowsFourTierRules(string crateName, string expectedPath)
    {
        var path = RustCrateVersionSource.GetSparseIndexPath(crateName);
        Assert.Equal(expectedPath, path);
    }

    [Fact]
    public async Task GetCandidatesAsync_RespectsBranchConstraint_AndFiltersYankedAndPrereleases()
    {
        // Simulated crates.io NDJSON index for 'anstream'
        var ndjson = """
            {"name":"anstream","vers":"0.6.20","yanked":false}
            {"name":"anstream","vers":"0.6.21","yanked":false}
            {"name":"anstream","vers":"0.6.22","yanked":false,"pubtime":"2026-03-01T10:00:00Z"}
            {"name":"anstream","vers":"0.6.23-rc.1","yanked":false}
            {"name":"anstream","vers":"0.6.24","yanked":true}
            {"name":"anstream","vers":"1.0.0","yanked":false}
            """;

        var handler = new FakeHttpMessageHandler(req =>
        {
            Assert.Contains("/an/st/anstream", req.RequestUri?.AbsolutePath ?? string.Empty);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ndjson)
            };
        });

        var httpClient = new HttpClient(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            RustCratePoller = new RustCratePollerOptions
            {
                Enabled = true,
                IndexBaseUrl = "https://index.crates.io",
                MaxCratesPerSweep = 10,
                RequestDelayMilliseconds = 0
            }
        });

        var specContent = """
            %global crate_name anstream
            %global full_version 0.6.21
            Name:           rust-anstream-0.6
            Version:        0.6.21
            """;

        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<SpecContext>>(new[]
        {
            new SpecContext("rust-anstream-0.6", "rust-anstream-0.6", specContent, "SPECS/rust-anstream-0.6/rust-anstream-0.6.spec")
        });

        var source = new RustCrateVersionSource(httpClient, options, specCatalogProvider: fakeCatalog);
        var candidates = await source.GetCandidatesAsync();

        Assert.Single(candidates);
        var cand = candidates[0];
        Assert.Equal("rust-anstream-0.6", cand.PackageName);
        Assert.Equal("0.6.21", cand.CurrentVersion);
        // 0.6.22 selected (0.6.23-rc.1 rejected due to hyphen, 0.6.24 rejected due to yanked, 1.0.0 rejected due to 0.6 branch constraint)
        Assert.Equal("0.6.22", cand.TargetVersion);
        Assert.Equal(CandidateOrigin.RustCrate, cand.Origin);
        Assert.Equal("https://crates.io/crates/anstream/0.6.22", cand.EvidenceUrl);
        Assert.True(cand.PublishedAt.HasValue);
        Assert.Equal(DateTimeOffset.Parse("2026-03-01T10:00:00Z"), cand.PublishedAt.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetSparseIndexPath_ThrowsOnEmpty(string? name)
    {
        Assert.Throws<ArgumentException>(() => RustCrateVersionSource.GetSparseIndexPath(name!));
    }

    [Fact]
    public void SelectLatestVersion_HandlesEdgeCases()
    {
        Assert.Null(RustCrateVersionSource.SelectLatestVersion(null!, "1.0.0"));
        Assert.Null(RustCrateVersionSource.SelectLatestVersion(new List<CrateIndexEntry>(), "1.0.0"));

        // Already at latest version
        var entries = new List<CrateIndexEntry> { new() { Version = "1.0.0" } };
        Assert.Null(RustCrateVersionSource.SelectLatestVersion(entries, "1.0.0"));

        // Target version is older than current
        var olderEntries = new List<CrateIndexEntry> { new() { Version = "0.9.0" } };
        Assert.Null(RustCrateVersionSource.SelectLatestVersion(olderEntries, "1.0.0"));

        // Chronological fallback for custom non-System.Version strings
        var customEntries = new List<CrateIndexEntry>
        {
            new() { Version = "custom-v1" },
            new() { Version = "custom-v2" }
        };
        var latestCustom = RustCrateVersionSource.SelectLatestVersion(customEntries, "custom-v1");
        Assert.NotNull(latestCustom);
        Assert.Equal("custom-v2", latestCustom.Version);
    }

    [Fact]
    public async Task GetCandidatesAsync_HandlesDisabledAndNetworkErrors()
    {
        var optionsDisabled = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            RustCratePoller = new RustCratePollerOptions { Enabled = false }
        });
        var sourceDisabled = new RustCrateVersionSource(new HttpClient(), optionsDisabled);
        var resDisabled = await sourceDisabled.GetCandidatesAsync();
        Assert.Empty(resDisabled);

        // Network error handling (404 / 500 / network exception)
        var handlerErr = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            RustCratePoller = new RustCratePollerOptions { Enabled = true, MaxCratesPerSweep = 10 }
        });
        var specContent = "%global crate_name foo\nVersion: 1.0.0\n";
        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<SpecContext>>(new[]
        {
            new SpecContext("rust-foo", "rust-foo", specContent, "SPECS/rust-foo/rust-foo.spec")
        });
        var sourceErr = new RustCrateVersionSource(new HttpClient(handlerErr), options, specCatalogProvider: fakeCatalog);
        var resErr = await sourceErr.GetCandidatesAsync();
        Assert.Empty(resErr);
    }

    [Fact]
    public async Task GetCandidatesAsync_SkipsSpecsMissingCrateNameOrVersion()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") });
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            RustCratePoller = new RustCratePollerOptions { Enabled = true }
        });

        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<SpecContext>>(new[]
        {
            new SpecContext("rust-nocrate", "rust-nocrate", "Version: 1.0.0\n", "SPECS/rust-nocrate/rust-nocrate.spec"),
            new SpecContext("rust-nover", "rust-nover", "%global crate_name bar\n", "SPECS/rust-nover/rust-nover.spec")
        });

        var source = new RustCrateVersionSource(new HttpClient(handler), options, specCatalogProvider: fakeCatalog);
        var candidates = await source.GetCandidatesAsync();
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task GetCandidatesAsync_WithSpecCatalog_SlicesBeforeFetchingRustSpecs()
    {
        var allNames = Enumerable.Range(0, 20).Select(i => $"rust-crate{i:D2}")
            .Concat(Enumerable.Range(0, 10).Select(i => $"c-pkg{i:D2}"))
            .ToList();

        var specCatalogMock = Substitute.For<ISpecCatalog>();
        specCatalogMock.GetAllPackageNamesAsync(Arg.Any<CancellationToken>()).Returns(allNames);
        specCatalogMock.GetSpecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var pkg = call.Arg<string>();
                return new SpecContext(
                    pkg, pkg,
                    $"%global crate_name {pkg}\nVersion: 1.0.0\n",
                    $"SPECS/{pkg}/{pkg}.spec");
            });

        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"name":"crate","vers":"1.0.1","yanked":false}""")
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            RustCratePoller = new RustCratePollerOptions
            {
                Enabled = true,
                IndexBaseUrl = "https://index.crates.io",
                MaxCratesPerSweep = 3,
                RequestDelayMilliseconds = 0
            }
        });

        var health = new RuntimeHealthState();
        var source = new RustCrateVersionSource(new HttpClient(handler), options, specCatalog: specCatalogMock, healthState: health);

        var candidates = await source.GetCandidatesAsync();
        Assert.Equal(3, candidates.Count);
        await specCatalogMock.DidNotReceive().GetRustSpecsAsync(Arg.Any<CancellationToken>());
        await specCatalogMock.Received(3).GetSpecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal("healthy", health.GetSourceHealth()["RustCrates"].Status);
        Assert.Equal(0, health.GetSourceHealth()["RustCrates"].ConsecutiveFailures);
    }

    [Fact]
    public async Task GetCandidatesAsync_AllCratesFailAndPartialFailure_RecordsSourceHealthAndLogsError()
    {
        var runtimeHealth = new RuntimeHealthState();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new RuntimeErrorLoggerProvider(runtimeHealth));
        });
        var logger = loggerFactory.CreateLogger<RustCrateVersionSource>();

        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<SpecContext>>(new[]
        {
            new SpecContext("rust-alpha", "rust-alpha", "%global crate_name alpha\nVersion: 1.0.0\n", "SPECS/rust-alpha/rust-alpha.spec"),
            new SpecContext("rust-beta", "rust-beta", "%global crate_name beta\nVersion: 1.0.0\n", "SPECS/rust-beta/rust-beta.spec")
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            RustCratePoller = new RustCratePollerOptions
            {
                Enabled = true,
                IndexBaseUrl = "https://index.crates.io",
                MaxCratesPerSweep = 10,
                RequestDelayMilliseconds = 0
            }
        });

        // 1. All queries fail -> status "failed", ConsecutiveFailures == 1, LogError captured in RuntimeHealthState
        var allFailHandler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var failingSource = new RustCrateVersionSource(
            new HttpClient(allFailHandler), options, logger: logger, specCatalogProvider: fakeCatalog, healthState: runtimeHealth);

        Assert.Empty(await failingSource.GetCandidatesAsync());
        var snap1 = runtimeHealth.GetSourceHealth()["RustCrates"];
        Assert.Equal("failed", snap1.Status);
        Assert.Equal(1, snap1.ConsecutiveFailures);
        Assert.Null(snap1.LastSuccessAt);
        Assert.Contains("503", snap1.LastError!, StringComparison.Ordinal);
        Assert.NotNull(runtimeHealth.GetLastError().LastError);

        // 2. Partial failure (1 ok, 1 HTTP 500) -> status "degraded", LastSuccessAt remains null (no clean healthy sweep yet)
        var partialHandler = new FakeHttpMessageHandler(req =>
            req.RequestUri!.AbsolutePath.Contains("alpha", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"name":"alpha","vers":"1.0.1","yanked":false}""") }
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var partialSource = new RustCrateVersionSource(
            new HttpClient(partialHandler), options, logger: logger, specCatalogProvider: fakeCatalog, healthState: runtimeHealth);

        var partialCandidates = await partialSource.GetCandidatesAsync();
        Assert.Single(partialCandidates);
        var snap2 = runtimeHealth.GetSourceHealth()["RustCrates"];
        Assert.Equal("degraded", snap2.Status);
        Assert.Equal(2, snap2.ConsecutiveFailures);
        Assert.NotNull(snap2.LastAttemptAt);
        Assert.Null(snap2.LastSuccessAt);
    }
}

