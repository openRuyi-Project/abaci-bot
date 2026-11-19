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
            Arg.Is<string[]>(labels => labels.Contains(GitHubLabels.VulnerabilityDetected) && labels.Contains(GitHubLabels.Severity2) && labels.Contains(GitHubLabels.StatusStaleExempt)),
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

    [Fact]
    public void CalculateMaxSeverity_ReturnsNull_WhenNoReportsHaveSeverity()
    {
        var emptyReports = Array.Empty<CveReport>();
        Assert.Null(CveActiveWatchService.CalculateMaxSeverity(emptyReports));

        var unratedReports = new[]
        {
            new CveReport("CVE-2024-9999", null, null, "OSV", "Unrated vuln")
        };
        Assert.Null(CveActiveWatchService.CalculateMaxSeverity(unratedReports));
    }

    [Fact]
    public async Task RunScanSweepAsync_ReconcilesSeverityLabels_RemovingObsoleteLabelsOnUpdate()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();

        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi"
        });

        // 1. Existing issue carrying old Severity: 3 label
        var existingIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(existingIssue, 202);
        typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(existingIssue, "[Security] Outstanding CVEs detected in python-jinja2");
        var oldSeverityLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(oldSeverityLabel, GitHubLabels.Severity3);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(existingIssue, new[] { oldSeverityLabel });

        gitHubMock.SearchStaleCandidatesAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), false, Arg.Is<string>(f => f.Contains("[Security]")), Arg.Any<CancellationToken>())
            .Returns(new[] { existingIssue });

        // 2. New scan detects upgraded Severity: 1
        var report = new CveReport("CVE-2024-9999", 9.8, GitHubLabels.Severity1, "OSV", "Critical RCE");
        var finding = new PackageVulnerabilityFinding("python-jinja2", "2.4.1", new[] { "CVE-2024-9999" }, new[] { report });

        scannerMock.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(new[] { finding }, new HashSet<string> { "python-jinja2" }));

        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<(string, string)>>(new[]
        {
            ("python-jinja2", "2.4.1")
        });

        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, catalogProvider: fakeCatalog);
        await service.RunScanSweepAsync();

        // 3. Verify Severity: 3 was removed and Severity: 1 was added
        await gitHubMock.Received(1).RemoveLabelAsync("openRuyi-Project", "openRuyi", 202, GitHubLabels.Severity3, Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).AddLabelsAsync(
            "openRuyi-Project", "openRuyi", 202, Arg.Is<string[]>(l => l.Contains(GitHubLabels.Severity1)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunScanSweepAsync_DoesNotAddSeverityLabel_WhenFindingIsUnrated()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();

        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi"
        });

        gitHubMock.SearchStaleCandidatesAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());

        var report = new CveReport("CVE-2024-0000", null, null, "OSV", "Unrated");
        var finding = new PackageVulnerabilityFinding("curl", "8.2.1", new[] { "CVE-2024-0000" }, new[] { report });

        scannerMock.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(new[] { finding }, new HashSet<string> { "curl" }));

        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<(string, string)>>(new[]
        {
            ("curl", "8.2.1")
        });

        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, catalogProvider: fakeCatalog);
        await service.RunScanSweepAsync();

        // Verify issue was created with VulnerabilityDetected and StatusStaleExempt, but NO Severity label
        await gitHubMock.Received(1).CreateIssueAsync(
            "openRuyi-Project", "openRuyi",
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<string[]>(labels => labels.Contains(GitHubLabels.VulnerabilityDetected) &&
                                       labels.Contains(GitHubLabels.StatusStaleExempt) &&
                                       !labels.Any(l => l.StartsWith("Severity:"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunScanSweepAsync_RotatesPackageCatalog_AcrossConsecutiveSweeps()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.SearchStaleCandidatesAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        var capturedBatches = new List<List<string>>();

        scannerMock.ScanPackagesAsync(Arg.Do<IEnumerable<(string Name, string Version)>>(pkgs =>
        {
            capturedBatches.Add(pkgs.Select(p => p.Name).ToList());
        }), Arg.Any<CancellationToken>())
        .Returns(new PackageVulnerabilityScanResult(Array.Empty<PackageVulnerabilityFinding>(), new HashSet<string>()));

        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi",
            PackagesPerSweep = 2
        });

        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<(string, string)>>(new[]
        {
            ("pkg1", "1.0"),
            ("pkg2", "1.0"),
            ("pkg3", "1.0"),
            ("pkg4", "1.0")
        });

        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, catalogProvider: fakeCatalog);

        // First sweep: should take pkg1, pkg2
        await service.RunScanSweepAsync();
        Assert.Single(capturedBatches);
        Assert.Equal(new[] { "pkg1", "pkg2" }, capturedBatches[0]);

        // Second sweep: should rotate and take pkg3, pkg4
        await service.RunScanSweepAsync();
        Assert.Equal(2, capturedBatches.Count);
        Assert.Equal(new[] { "pkg3", "pkg4" }, capturedBatches[1]);

        // Third sweep: should wrap around and take pkg1, pkg2
        await service.RunScanSweepAsync();
        Assert.Equal(3, capturedBatches.Count);
        Assert.Equal(new[] { "pkg1", "pkg2" }, capturedBatches[2]);
    }
}
