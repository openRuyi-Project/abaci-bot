using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit;
using Xunit;

namespace abaci_bot.Tests;

public class CveActiveWatchServiceTests
{
    [Fact]
    public async Task RunScanSweepAsync_CreatesRollupIssue_WhenVulnerabilitiesFound()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.SearchStaleCandidatesAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        var sampleReport = new CveReport("CVE-2020-28493", 7.5, GitHubLabels.Severity2, "OSV", "ReDoS in Jinja2");
        var finding = new PackageVulnerabilityFinding("python-jinja2", "2.4.1", new[] { "CVE-2020-28493" }, new[] { sampleReport });

        scannerMock.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(new[] { finding }, new HashSet<string> { "python-jinja2" }));

        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi"
        });

        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<(string, string)>>(new[]
        {
            ("python-jinja2", "2.4.1")
        });

        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, catalogProvider: fakeCatalog);
        await service.RunScanSweepAsync();

        // Verify issue creation with correct title and labels
        await gitHubMock.Received(1).CreateIssueAsync(
            "openRuyi-Project",
            "openRuyi",
            "[Security] Outstanding CVEs detected in python-jinja2",
            Arg.Is<string>(b => b.Contains("CVE-2020-28493") && b.Contains("ReDoS in Jinja2")),
            Arg.Is<string[]>(labels => labels.Contains(GitHubLabels.VulnerabilityDetected) && labels.Contains(GitHubLabels.Severity2) && labels.Contains(GitHubLabels.StatusPinned)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunScanSweepAsync_AutoClosesIssue_WhenVulnerabilityResolvedInLaterScan()
    {
        var gitHubMock = Substitute.For<IGitHubService>();

        // An existing open issue for python-jinja2
        var existingIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(existingIssue, 101);
        typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(existingIssue, "[Security] Outstanding CVEs detected in python-jinja2");

        gitHubMock.SearchStaleCandidatesAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), false, Arg.Is<string>(f => f.Contains(GitHubLabels.VulnerabilityDetected)), Arg.Any<CancellationToken>())
            .Returns(new[] { existingIssue });

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        // Scan returns 0 findings now! (Vulnerability has been patched, but package was successfully scanned)
        scannerMock.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(Array.Empty<PackageVulnerabilityFinding>(), new HashSet<string> { "python-jinja2" }));

        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi"
        });

        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<(string, string)>>(new[]
        {
            ("python-jinja2", "2.11.3")
        });

        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, catalogProvider: fakeCatalog);
        await service.RunScanSweepAsync();

        // Verify issue closure and label transitions
        await gitHubMock.Received(1).RemoveLabelAsync("openRuyi-Project", "openRuyi", 101, GitHubLabels.VulnerabilityDetected, Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).AddLabelsAsync("openRuyi-Project", "openRuyi", 101, Arg.Is<string[]>(l => l.Contains(GitHubLabels.VulnerabilityResolved)), Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).CloseIssueAsync("openRuyi-Project", "openRuyi", 101, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("CVE-2024-1234", "https://nvd.nist.gov/vuln/detail/CVE-2024-1234")]
    [InlineData("GHSA-xxxx-yyyy-zzzz", "https://github.com/advisories/GHSA-xxxx-yyyy-zzzz")]
    [InlineData("CGA-1234-5678", "https://osv.dev/vulnerability/CGA-1234-5678")]
    public void FormatVulnerabilityUrl_RoutesCorrectly(string id, string expectedUrl)
    {
        var url = CveActiveWatchService.FormatVulnerabilityUrl(id);
        Assert.Equal(expectedUrl, url);
    }

    [Fact]
    public void GenerateIssueBody_FormatsAdvisoryLinksProperly()
    {
        var cveReport = new CveReport("CVE-2024-1234", 9.8, GitHubLabels.Severity1, "OSV", "RCE in curl");
        var ghsaReport = new CveReport("GHSA-xxxx-yyyy-zzzz", 7.5, GitHubLabels.Severity2, "OSV", "Prototype pollution");
        var finding = new PackageVulnerabilityFinding("curl", "8.2.1", new[] { "CVE-2024-1234", "GHSA-xxxx-yyyy-zzzz" }, new[] { cveReport, ghsaReport });

        var body = CveActiveWatchService.GenerateIssueBody(finding);

        Assert.Contains("[CVE-2024-1234](https://nvd.nist.gov/vuln/detail/CVE-2024-1234)", body);
        Assert.Contains("[GHSA-xxxx-yyyy-zzzz](https://github.com/advisories/GHSA-xxxx-yyyy-zzzz)", body);
        Assert.DoesNotContain("nvd.nist.gov/vuln/detail/GHSA", body);
    }
}
