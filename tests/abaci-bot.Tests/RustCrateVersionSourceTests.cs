using System.Net;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
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
            {"name":"anstream","vers":"0.6.22","yanked":false}
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
            new SpecContext("rust-anstream-0.6", "rust-anstream-0.6", specContent)
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
    }
}

