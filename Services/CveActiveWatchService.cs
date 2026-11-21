using System.Text.RegularExpressions;
using abaci_bot.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Background orchestration worker for active CVE monitoring and vulnerability issue tracking.
/// Dispatches batch OSV scanning across package catalog, aggregates outstanding CVEs per package,
/// and closes issues automatically when subsequent scans confirm the vulnerabilities are resolved.
/// </summary>
public class CveActiveWatchService : BackgroundService
{
    private readonly IGitHubService _gitHubService;
    private readonly IPackageVulnerabilityScanner _scanner;
    private readonly CveActiveWatchOptions _options;
    private readonly ILogger<CveActiveWatchService>? _logger;
    private readonly ISpecCatalog? _specCatalog;
    private readonly Func<CancellationToken, Task<IReadOnlyList<(string PackageName, string Version)>>>? _catalogProvider;
    private int _catalogOffset;

    private static readonly Regex PackageIssueTitleRegex = new(
        @"^\[Security\]\s*Outstanding CVEs detected in\s+(?<pkg>\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VersionRegex = new(
        @"^Version:\s*(?<ver>\S+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public CveActiveWatchService(
        IGitHubService gitHubService,
        IPackageVulnerabilityScanner scanner,
        IOptions<CveActiveWatchOptions> options,
        ILogger<CveActiveWatchService>? logger = null,
        ISpecCatalog? specCatalog = null,
        Func<CancellationToken, Task<IReadOnlyList<(string PackageName, string Version)>>>? catalogProvider = null)
    {
        _gitHubService = gitHubService;
        _scanner = scanner;
        _options = options.Value;
        _logger = logger;
        _specCatalog = specCatalog;
        _catalogProvider = catalogProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("CveActiveWatchService background worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_options.Enabled && !string.IsNullOrWhiteSpace(_options.TargetRepository))
                {
                    await RunScanSweepAsync(stoppingToken);
                }
                else
                {
                    _logger?.LogDebug("CveActiveWatchService is disabled or TargetRepository is unconfigured; skipping sweep");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unexpected error occurred during CveActiveWatchService sweep");
            }

            var interval = TimeSpan.FromHours(Math.Max(1, _options.CheckIntervalHours));
            await Task.Delay(interval, stoppingToken);
        }

        _logger?.LogInformation("CveActiveWatchService background worker stopping");
    }

    public async Task RunScanSweepAsync(CancellationToken ct = default)
    {
        var targetRepo = _options.TargetRepository.Trim();
        var parts = targetRepo.Split('/');
        if (parts.Length != 2)
        {
            _logger?.LogError("Invalid TargetRepository format '{Repo}'; expected 'owner/repo'", targetRepo);
            return;
        }

        var owner = parts[0];
        var repo = parts[1];

        _logger?.LogInformation("Starting CveActiveWatchService scan sweep for {TargetRepo} (DryRun={DryRun})", targetRepo, _options.DryRun);

        // Rate limit snapshot check before starting expensive scan
        var rateLimit = await _gitHubService.GetRateLimitSnapshotAsync(ct);
        if (rateLimit != null && rateLimit.CoreRemaining.HasValue && rateLimit.CoreRemaining < 500)
        {
            _logger?.LogWarning("Rate limit remaining ({Remaining}) is low; deferring CVE scan sweep",
                rateLimit.CoreRemaining.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return;
        }

        IReadOnlyList<(string PackageName, string Version)> packagesToScan = Array.Empty<(string PackageName, string Version)>();
        var slicedUpstream = false;

        // Slice package names BEFORE calling GetSpecAsync so fallback mode never fetches O(N) specs.
        if (_catalogProvider == null && _specCatalog != null && _options.PackagesPerSweep > 0)
        {
            var allNames = await _specCatalog.GetAllPackageNamesAsync(ct);
            if (allNames.Count > 0)
            {
                packagesToScan = await FetchSlicedSpecCatalogAsync(allNames, _options.PackagesPerSweep, ct);
                slicedUpstream = true;
            }
        }

        if (!slicedUpstream)
        {
            var packages = await LoadPackageCatalogAsync(ct);
            if (packages.Count == 0)
            {
                _logger?.LogInformation("No packages found in catalog to scan for vulnerabilities");
                return;
            }

            if (_options.PackagesPerSweep > 0 && _options.PackagesPerSweep < packages.Count)
            {
                var offset = _catalogOffset % packages.Count;
                var chunk = new List<(string PackageName, string Version)>(_options.PackagesPerSweep);
                for (int i = 0; i < _options.PackagesPerSweep; i++)
                {
                    chunk.Add(packages[(offset + i) % packages.Count]);
                }
                _catalogOffset = (offset + _options.PackagesPerSweep) % packages.Count;
                packagesToScan = chunk;
            }
            else
            {
                packagesToScan = packages;
            }
        }

        if (packagesToScan.Count == 0)
        {
            _logger?.LogInformation("No packages found in catalog to scan for vulnerabilities");
            return;
        }

        _logger?.LogInformation("Dispatching OSV scan for {Count} packages", packagesToScan.Count);

        var scanResult = await _scanner.ScanPackagesAsync(packagesToScan, ct);
        var findings = scanResult.Findings;
        var vulnerablePackages = findings.ToDictionary(f => f.PackageName, f => f, StringComparer.OrdinalIgnoreCase);

        _logger?.LogInformation("Vulnerability scan identified {Count} packages with outstanding CVEs", findings.Count);

        // 1. Process new or existing findings (Rollup Issue per package)
        foreach (var finding in findings)
        {
            if (ct.IsCancellationRequested)
                break;

            await ProcessPackageFindingAsync(owner, repo, finding, ct);
        }

        // 2. Pure scan-based auto-close: ONLY check existing open CVE issues whose package was successfully scanned in this sweep
        await CheckAndCloseResolvedIssuesAsync(owner, repo, scanResult.SuccessfullyScannedPackages, vulnerablePackages, ct);
    }

    private async Task ProcessPackageFindingAsync(
        string owner, string repo, PackageVulnerabilityFinding finding, CancellationToken ct)
    {
        var issueTitle = $"[Security] Outstanding CVEs detected in {finding.PackageName}";
        var body = GenerateIssueBody(finding);
        var maxSeverity = CalculateMaxSeverity(finding.DetailedReports);

        var issueLabel = !string.IsNullOrWhiteSpace(_options.IssueLabel)
            ? _options.IssueLabel
            : GitHubLabels.VulnerabilityDetected;

        var labels = new List<string>
        {
            issueLabel,
            GitHubLabels.StatusStaleExempt
        };
        if (!string.IsNullOrEmpty(maxSeverity))
        {
            labels.Add(maxSeverity);
        }

        try
        {
            var openIssues = await _gitHubService.SearchStaleCandidatesAsync(
                owner, repo, DateTimeOffset.UtcNow, isPr: false, labelFilter: $"\"{issueTitle}\" is:issue is:open", ct: ct);

            var existingIssue = openIssues.FirstOrDefault(i => string.Equals(i.Title, issueTitle, StringComparison.OrdinalIgnoreCase));

            if (existingIssue != null)
            {
                _logger?.LogInformation("Updating existing vulnerability issue #{Number} for {Package}", existingIssue.Number, finding.PackageName);
                if (!_options.DryRun)
                {
                    await _gitHubService.UpdateIssueAsync(owner, repo, existingIssue.Number, issueTitle, body, ct);

                    // Reconcile severity labels: remove obsolete Severity:* labels
                    var currentSeverityLabels = existingIssue.Labels
                        .Where(l => l.Name != null && l.Name.StartsWith("Severity:", StringComparison.OrdinalIgnoreCase))
                        .Select(l => l.Name)
                        .ToList();

                    foreach (var oldLabel in currentSeverityLabels)
                    {
                        if (!string.Equals(oldLabel, maxSeverity, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                await _gitHubService.RemoveLabelAsync(owner, repo, existingIssue.Number, oldLabel, ct);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogDebug(ex, "Failed to remove obsolete severity label {Label} from issue #{Number}", oldLabel, existingIssue.Number);
                            }
                        }
                    }

                    await _gitHubService.AddLabelsAsync(owner, repo, existingIssue.Number, labels.ToArray(), ct);
                }
            }
            else
            {
                _logger?.LogInformation("Creating new vulnerability issue for {Package} ({Count} CVEs, {Severity})",
                    finding.PackageName, finding.CveIds.Count, maxSeverity ?? "Unrated");

                if (!_options.DryRun)
                {
                    await _gitHubService.CreateIssueAsync(owner, repo, issueTitle, body, labels.ToArray(), ct);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to create or update vulnerability issue for {Package}", finding.PackageName);
        }
    }

    private async Task CheckAndCloseResolvedIssuesAsync(
        string owner, string repo, IReadOnlySet<string> successfullyScannedPackages, Dictionary<string, PackageVulnerabilityFinding> currentVulnerabilities, CancellationToken ct)
    {
        try
        {
            var openVulnerabilityIssues = await _gitHubService.SearchStaleCandidatesAsync(
                owner, repo, DateTimeOffset.UtcNow, isPr: false, labelFilter: $"label:\"{GitHubLabels.VulnerabilityDetected}\" is:issue is:open", ct: ct);

            foreach (var issue in openVulnerabilityIssues)
            {
                if (ct.IsCancellationRequested)
                    break;

                var match = PackageIssueTitleRegex.Match(issue.Title ?? string.Empty);
                if (!match.Success)
                    continue;

                var packageName = match.Groups["pkg"].Value.Trim();

                // Only auto-close if this package was verified in a successful scan batch without outstanding CVEs
                if (!successfullyScannedPackages.Contains(packageName))
                    continue;

                if (!currentVulnerabilities.ContainsKey(packageName))
                {
                    // Vulnerabilities for this package were cleared!
                    _logger?.LogInformation("All CVEs resolved for {Package}; closing issue #{Number}", packageName, issue.Number);

                    if (!_options.DryRun)
                    {
                        var resolutionComment = "All previously detected CVE vulnerabilities have been resolved in the current package version. Closing issue.";
                        await _gitHubService.CreateCommentAsync(owner, repo, issue.Number, resolutionComment, ct);
                        await _gitHubService.RemoveLabelAsync(owner, repo, issue.Number, GitHubLabels.VulnerabilityDetected, ct);
                        await _gitHubService.AddLabelsAsync(owner, repo, issue.Number, new[] { GitHubLabels.VulnerabilityResolved }, ct);
                        await _gitHubService.CloseIssueAsync(owner, repo, issue.Number, ct);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to verify and close resolved vulnerability issues");
        }
    }

    public static string? CalculateMaxSeverity(IEnumerable<CveReport> reports)
    {
        var severities = reports
            .Select(r => r.SeverityLabel)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        if (severities.Contains(GitHubLabels.Severity1)) return GitHubLabels.Severity1;
        if (severities.Contains(GitHubLabels.Severity2)) return GitHubLabels.Severity2;
        if (severities.Contains(GitHubLabels.Severity3)) return GitHubLabels.Severity3;
        if (severities.Contains(GitHubLabels.Severity4)) return GitHubLabels.Severity4;
        return null;
    }

    public static string? FormatVulnerabilityUrl(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var trimmed = id.Trim();
        if (trimmed.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase))
            return $"https://nvd.nist.gov/vuln/detail/{trimmed}";

        if (trimmed.StartsWith("GHSA-", StringComparison.OrdinalIgnoreCase))
            return $"https://github.com/advisories/{trimmed}";

        return $"https://osv.dev/vulnerability/{Uri.EscapeDataString(trimmed)}";
    }

    public static string GenerateIssueBody(PackageVulnerabilityFinding finding)
    {
        const int maxDisplayCount = 100;
        var totalCount = finding.DetailedReports.Count;
        var displayReports = finding.DetailedReports
            .OrderByDescending(r => r.CvssScore ?? 0)
            .Take(maxDisplayCount)
            .ToList();

        var rows = string.Join("\n", displayReports.Select(r =>
        {
            var linkUrl = FormatVulnerabilityUrl(r.CveId);
            var idCell = linkUrl != null ? $"[{r.CveId}]({linkUrl})" : $"`{r.CveId}`";
            return $"| {idCell} | `{r.SeverityLabel ?? "Unknown"}` | {r.CvssScore?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "N/A"} | {EscapeMarkdown(r.Summary ?? "Vulnerability details unavailable")} |";
        }));

        var truncationNotice = totalCount > maxDisplayCount
            ? $"\n\n> ⚠️ **Notice**: Truncated. Displaying top {maxDisplayCount} highest severity CVEs out of {totalCount} total vulnerabilities."
            : string.Empty;

        var body = $"""
            ## Vulnerability Assessment for `{finding.PackageName}` ({finding.Version})

            The following unpatched CVE vulnerabilities were actively detected in `{finding.PackageName}`:

            | CVE ID | Severity | CVSS Score | Summary |
            |---|---|---|---|
            {rows}{truncationNotice}

            > Automatically tracked by abaci-bot. When this package is updated and CVEs are cleared, this issue will close automatically.
            """;

        // Hard cap at 60,000 characters to prevent GitHub 422 limit (65536 max)
        if (body.Length > 60000)
        {
            body = string.Concat(body.AsSpan(0, 59900), "\n\n> ⚠️ [Content truncated due to size limits]");
        }

        return body;
    }

    private static string EscapeMarkdown(string text)
    {
        return text.Replace("|", "\\|").Replace("\r\n", " ").Replace("\n", " ").Trim();
    }

    private async Task<IReadOnlyList<(string PackageName, string Version)>> FetchSlicedSpecCatalogAsync(
        IReadOnlyList<string> allNames,
        int packagesPerSweep,
        CancellationToken ct)
    {
        var sortedNames = allNames
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sliceCount = Math.Min(packagesPerSweep, sortedNames.Count);
        var offset = _catalogOffset % sortedNames.Count;
        var selectedNames = new List<string>(sliceCount);
        for (int i = 0; i < sliceCount; i++)
        {
            selectedNames.Add(sortedNames[(offset + i) % sortedNames.Count]);
        }

        _catalogOffset = (offset + sliceCount) % sortedNames.Count;

        var result = new List<(string PackageName, string Version)>(sliceCount);
        foreach (var name in selectedNames)
        {
            if (ct.IsCancellationRequested)
                break;

            var spec = await _specCatalog!.GetSpecAsync(name, ct);
            if (spec != null)
            {
                var match = VersionRegex.Match(spec.Content);
                if (match.Success)
                {
                    result.Add((name, match.Groups["ver"].Value.Trim()));
                }
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<(string PackageName, string Version)>> LoadPackageCatalogAsync(CancellationToken ct)
    {
        if (_catalogProvider != null)
        {
            return await _catalogProvider(ct);
        }

        if (_specCatalog != null)
        {
            var names = await _specCatalog.GetAllPackageNamesAsync(ct);
            var result = new List<(string PackageName, string Version)>();
            foreach (var name in names)
            {
                if (ct.IsCancellationRequested)
                    break;

                var spec = await _specCatalog.GetSpecAsync(name, ct);
                if (spec != null)
                {
                    var match = VersionRegex.Match(spec.Content);
                    if (match.Success)
                    {
                        result.Add((name, match.Groups["ver"].Value.Trim()));
                    }
                }
            }
            if (result.Count > 0)
                return result;
        }

        var list = new List<(string PackageName, string Version)>();
        const string specsDir = "SPECS";
        if (!Directory.Exists(specsDir))
            return list;

        foreach (var dir in Directory.GetDirectories(specsDir))
        {
            if (ct.IsCancellationRequested)
                break;

            var dirName = Path.GetFileName(dir);
            var specFiles = Directory.GetFiles(dir, "*.spec");
            if (specFiles.Length == 0)
                continue;

            try
            {
                var content = await File.ReadAllTextAsync(specFiles[0], ct);
                var match = VersionRegex.Match(content);
                if (match.Success)
                {
                    list.Add((dirName, match.Groups["ver"].Value.Trim()));
                }
            }
            catch
            {
                // Ignore read errors
            }
        }

        return list;
    }
}
