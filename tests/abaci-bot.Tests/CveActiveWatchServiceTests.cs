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

    [Fact]
    public async Task StartAsync_AndStopAsync_Lifecycle()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = false,
            TargetRepository = "owner/repo",
            CheckIntervalHours = 1
        });

        var service = new CveActiveWatchService(
            Substitute.For<IGitHubService>(),
            Substitute.For<IPackageVulnerabilityScanner>(),
            options);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await service.StartAsync(cts.Token);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RunScanSweepAsync_LoadsCatalogViaSpecCatalog_WhenCatalogProviderNull()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.SearchStaleCandidatesAsync(
            "owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());

        var specCatalogMock = Substitute.For<ISpecCatalog>();
        specCatalogMock.GetAllPackageNamesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { "pkgA", "pkgB" });
        specCatalogMock.GetSpecAsync("pkgA", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("pkgA", "pkgA", "Name: pkgA\nVersion: 1.2.3\n", "SPECS/pkgA.spec"));
        specCatalogMock.GetSpecAsync("pkgB", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("pkgB", "pkgB", "No version line\n", "SPECS/pkgB.spec"));

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        var captured = new List<(string Name, string Version)>();
        scannerMock.ScanPackagesAsync(Arg.Do<IEnumerable<(string Name, string Version)>>(p => captured.AddRange(p)), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(Array.Empty<PackageVulnerabilityFinding>(), new HashSet<string>()));

        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            PackagesPerSweep = 0
        });

        // Pass specCatalog and null catalogProvider
        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, specCatalog: specCatalogMock);
        await service.RunScanSweepAsync();

        Assert.Single(captured);
        Assert.Equal("pkgA", captured[0].Name);
        Assert.Equal("1.2.3", captured[0].Version);
    }

    [Theory]
    [InlineData("invalid_repo")]
    [InlineData("")]
    public async Task RunScanSweepAsync_HandlesInvalidTargetRepo(string targetRepo)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            TargetRepository = targetRepo
        });

        var gitHubMock = Substitute.For<IGitHubService>();
        var service = new CveActiveWatchService(gitHubMock, Substitute.For<IPackageVulnerabilityScanner>(), options);

        await service.RunScanSweepAsync();
        await gitHubMock.DidNotReceive().SearchStaleCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunScanSweepAsync_DefersSweep_WhenRateLimitLow()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(100, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            TargetRepository = "owner/repo"
        });

        var service = new CveActiveWatchService(gitHubMock, scannerMock, options);
        await service.RunScanSweepAsync();

        await scannerMock.DidNotReceive().ScanPackagesAsync(
            Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunScanSweepAsync_ReturnsEarly_WhenPackageCatalogEmpty()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            TargetRepository = "owner/repo"
        });

        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<(string, string)>>(Array.Empty<(string, string)>());
        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, catalogProvider: fakeCatalog);
        await service.RunScanSweepAsync();

        await scannerMock.DidNotReceive().ScanPackagesAsync(
            Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunScanSweepAsync_UpdatesExistingVulnerabilityIssue_AndReconcilesSeverityLabels()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var existingIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(existingIssue, 88);
        typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(existingIssue, "[Security] Outstanding CVEs detected in curl");
        var oldSeverityLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(oldSeverityLabel, GitHubLabels.Severity4);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(existingIssue, new List<Label> { oldSeverityLabel });

        gitHubMock.SearchStaleCandidatesAsync("owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { existingIssue });

        var report = new CveReport("CVE-2024-9999", 9.8, GitHubLabels.Severity1, "OSV", "Critical RCE");
        var finding = new PackageVulnerabilityFinding("curl", "8.2.1", new[] { "CVE-2024-9999" }, new[] { report });

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        scannerMock.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(new[] { finding }, new HashSet<string> { "curl" }));

        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<(string, string)>>(new[] { ("curl", "8.2.1") });
        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo"
        });

        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, catalogProvider: fakeCatalog);
        await service.RunScanSweepAsync();

        await gitHubMock.Received(1).UpdateIssueAsync("owner", "repo", 88, "[Security] Outstanding CVEs detected in curl", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).RemoveLabelAsync("owner", "repo", 88, GitHubLabels.Severity4, Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).AddLabelsAsync("owner", "repo", 88, Arg.Is<string[]>(labels => labels.Contains(GitHubLabels.Severity1)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunScanSweepAsync_AutoClosesResolvedVulnerabilityIssue()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var resolvedIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(resolvedIssue, 99);
        typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(resolvedIssue, "[Security] Outstanding CVEs detected in curl");
        var vulnLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(vulnLabel, GitHubLabels.VulnerabilityDetected);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(resolvedIssue, new List<Label> { vulnLabel });

        // Search returns this issue for reconciliation
        gitHubMock.SearchStaleCandidatesAsync("owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Is<string>(q => q.Contains("is:issue is:open")), Arg.Any<CancellationToken>())
            .Returns(new[] { resolvedIssue });

        // Scanner reports curl was scanned successfully, with 0 findings (resolved!)
        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        scannerMock.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(Array.Empty<PackageVulnerabilityFinding>(), new HashSet<string> { "curl" }));

        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<(string, string)>>(new[] { ("curl", "8.2.2") });
        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo"
        });

        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, catalogProvider: fakeCatalog);
        await service.RunScanSweepAsync();

        await gitHubMock.Received(1).CreateCommentAsync("owner", "repo", 99, Arg.Is<string>(c => c.Contains("resolved")), Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).RemoveLabelAsync("owner", "repo", 99, GitHubLabels.VulnerabilityDetected, Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).AddLabelsAsync("owner", "repo", 99, Arg.Is<string[]>(labels => labels.Contains(GitHubLabels.VulnerabilityResolved)), Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).CloseIssueAsync("owner", "repo", 99, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunScanSweepAsync_WithSpecCatalogAndPackagesPerSweep_SlicesBeforeFetchingSpecs()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        gitHubMock.SearchStaleCandidatesAsync(
                "owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());

        var allNames = Enumerable.Range(0, 50).Select(i => $"pkg-{i:D2}").ToList();
        var specCatalogMock = Substitute.For<ISpecCatalog>();
        specCatalogMock.GetAllPackageNamesAsync(Arg.Any<CancellationToken>()).Returns(allNames);
        specCatalogMock.GetSpecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var name = call.Arg<string>();
                return new SpecContext(name, name, $"Name: {name}\nVersion: 1.0.0\n", $"SPECS/{name}/{name}.spec");
            });

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        var scannedBatches = new List<List<string>>();
        scannerMock.ScanPackagesAsync(Arg.Do<IEnumerable<(string Name, string Version)>>(pkgs =>
                scannedBatches.Add(pkgs.Select(p => p.Name).ToList())), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(Array.Empty<PackageVulnerabilityFinding>(), new HashSet<string>()));

        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = true,
            TargetRepository = "owner/repo",
            PackagesPerSweep = 5
        });

        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, specCatalog: specCatalogMock);

        await service.RunScanSweepAsync();
        await specCatalogMock.Received(5).GetSpecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(new[] { "pkg-00", "pkg-01", "pkg-02", "pkg-03", "pkg-04" }, scannedBatches[0]);

        await service.RunScanSweepAsync();
        await specCatalogMock.Received(10).GetSpecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(new[] { "pkg-05", "pkg-06", "pkg-07", "pkg-08", "pkg-09" }, scannedBatches[1]);
    }
}
