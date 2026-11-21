using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Built-in fallback update candidate source querying the index.crates.io sparse index.
/// Operates independently of external trackers, using 4-tier hashing and NDJSON streaming.
/// Filters yanked and pre-release versions, strictly enforcing branch constraints.
/// </summary>
public class RustCrateVersionSource : IUpdateCandidateSource
{
    private readonly HttpClient _httpClient;
    private readonly RustCratePollerOptions _options;
    private readonly ILogger<RustCrateVersionSource>? _logger;
    private readonly ISpecCatalog? _specCatalog;
    private readonly Func<CancellationToken, Task<IReadOnlyList<SpecContext>>>? _specCatalogProvider;
    private readonly RuntimeHealthState? _healthState;
    private int _crateCursor;

    private static readonly Regex CrateNameRegex = new(
        @"^%global\s+crate_name\s+(?<name>\S+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex FullVersionRegex = new(
        @"^%global\s+full_version\s+(?<ver>\S+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex VersionRegex = new(
        @"^Version:\s*(?<ver>\S+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public string SourceName => "RustCrates";

    public RustCrateVersionSource(
        HttpClient httpClient,
        IOptions<PackageUpdateOptions> options,
        ILogger<RustCrateVersionSource>? logger = null,
        ISpecCatalog? specCatalog = null,
        Func<CancellationToken, Task<IReadOnlyList<SpecContext>>>? specCatalogProvider = null,
        RuntimeHealthState? healthState = null)
    {
        _httpClient = httpClient;
        _options = options.Value.RustCratePoller;
        _logger = logger;
        _specCatalog = specCatalog;
        _specCatalogProvider = specCatalogProvider;
        _healthState = healthState;
    }

    /// <summary>
    /// Generates the canonical sparse index path using crates.io 4-tier hashing rules.
    /// 1 char: 1/{name}
    /// 2 chars: 2/{name}
    /// 3 chars: 3/{first_char}/{name}
    /// 4+ chars: {first_2}/{next_2}/{name}
    /// </summary>
    public static string GetSparseIndexPath(string crateName)
    {
        if (string.IsNullOrWhiteSpace(crateName))
            throw new ArgumentException("Crate name cannot be empty", nameof(crateName));

        var name = crateName.Trim().ToLowerInvariant();
        return name.Length switch
        {
            1 => $"1/{name}",
            2 => $"2/{name}",
            3 => $"3/{name[0]}/{name}",
            _ => $"{name[..2]}/{name[2..4]}/{name}"
        };
    }

    public async Task<IReadOnlyList<UpdateCandidate>> GetCandidatesAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.IndexBaseUrl))
        {
            _logger?.LogInformation("Rust crates poller is disabled or IndexBaseUrl is empty");
            _healthState?.RecordSourceOutcome(SourceName, "disabled", 0);
            return Array.Empty<UpdateCandidate>();
        }

        var (specs, totalSpecs, startIndex, nextCursorStart, alreadySliced) = await LoadRustSpecsForSweepAsync(ct);
        if (specs.Count == 0)
        {
            _logger?.LogInformation("No Rust crate SPEC files found to evaluate");
            _healthState?.RecordSourceOutcome(SourceName, "healthy", 0);
            return Array.Empty<UpdateCandidate>();
        }

        var candidates = new List<UpdateCandidate>();
        var baseUrl = _options.IndexBaseUrl.TrimEnd('/');
        int processedCount = 0;
        int queriedCount = 0;
        int failedCount = 0;
        int iterated = 0;
        string? lastError = null;

        var loopCount = alreadySliced ? specs.Count : totalSpecs;
        for (int i = 0; i < loopCount; i++)
        {
            if (ct.IsCancellationRequested)
                break;

            if (_options.MaxCratesPerSweep > 0 && processedCount >= _options.MaxCratesPerSweep)
            {
                _logger?.LogInformation("Reached MaxCratesPerSweep limit ({Limit}) for this sweep", _options.MaxCratesPerSweep);
                break;
            }

            iterated++;
            var spec = alreadySliced ? specs[i] : specs[(startIndex + i) % totalSpecs];

            var crateMatch = CrateNameRegex.Match(spec.Content);
            if (!crateMatch.Success)
                continue;

            var crateName = crateMatch.Groups["name"].Value.Trim();
            var currentVersion = ExtractCurrentVersion(spec.Content);
            if (string.IsNullOrWhiteSpace(currentVersion))
                continue;

            // Deduce branch via unified UpdateAdmission helper
            var branch = UpdateAdmission.DeduceBranch(spec.DirectoryName, crateName);

            var indexPath = GetSparseIndexPath(crateName);
            var url = $"{baseUrl}/{indexPath}";

            processedCount++;
            if (_options.RequestDelayMilliseconds > 0)
            {
                await Task.Delay(_options.RequestDelayMilliseconds, ct);
            }

            queriedCount++;
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.GetAsync(url, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedCount++;
                lastError = $"{crateName}: {ex.GetType().Name}: {ex.Message}";
                _logger?.LogWarning(ex, "Failed to query sparse index for crate {Crate} at {Url}", crateName, url);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                failedCount++;
                lastError = $"{crateName}: HTTP {(int)response.StatusCode} ({response.StatusCode})";
                _logger?.LogWarning("Sparse index returned status {StatusCode} for crate {Crate} at {Url}", response.StatusCode, crateName, url);
                continue;
            }

            var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            var versions = new List<CrateIndexEntry>();

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var entry = JsonSerializer.Deserialize<CrateIndexEntry>(line);
                    if (entry != null && !entry.Yanked && !string.IsNullOrWhiteSpace(entry.Version))
                    {
                        // Exclude pre-releases (hyphens in Version tag are forbidden in RPM)
                        if (!entry.Version.Contains('-'))
                        {
                            versions.Add(entry);
                        }
                    }
                }
                catch
                {
                    // Ignore malformed line
                }
            }

            if (versions.Count == 0)
                continue;

            // Filter compatible with branch
            var compatibleVersions = versions
                .Where(v => RustCrateSpecRewriter.IsVersionCompatibleWithBranch(v.Version, branch))
                .ToList();

            if (compatibleVersions.Count == 0)
                continue;

            // Pick latest based on semver / appearance in index
            var targetEntry = SelectLatestVersion(compatibleVersions, currentVersion);
            if (targetEntry == null)
                continue;

            candidates.Add(new UpdateCandidate(
                PackageName: spec.PackageName,
                CurrentVersion: currentVersion,
                TargetVersion: targetEntry.Version,
                Origin: CandidateOrigin.RustCrate,
                EvidenceUrl: $"https://crates.io/crates/{crateName}/{targetEntry.Version}",
                PublishedAt: targetEntry.Pubtime));
        }

        _crateCursor = alreadySliced
            ? nextCursorStart
            : (startIndex + iterated) % Math.Max(1, totalSpecs);

        if (queriedCount > 0 && failedCount == queriedCount)
        {
            _logger?.LogError(
                "Rust crate version source failed all {Failed}/{Queried} sparse index queries. Last error: {Error}",
                failedCount, queriedCount, lastError);
            _healthState?.RecordSourceOutcome(SourceName, "failed", 0, lastError);
        }
        else if (failedCount > 0)
        {
            _logger?.LogWarning(
                "Rust crate version source degraded ({Failed}/{Queried} sparse index queries failed, {Candidates} candidates discovered). Last error: {Error}",
                failedCount, queriedCount, candidates.Count, lastError);
            _healthState?.RecordSourceOutcome(SourceName, "degraded", candidates.Count, lastError);
        }
        else
        {
            _healthState?.RecordSourceOutcome(SourceName, "healthy", candidates.Count);
        }

        _logger?.LogInformation("Rust crates poller discovered {Count} candidates from {Processed} queried crates (cursor moved to {Cursor})", candidates.Count, processedCount, _crateCursor);
        return candidates;
    }

    /// <summary>
    /// Selects the highest/latest version strictly newer than currentVersion.
    /// Sparse index orders versions chronologically; validates target is strictly newer.
    /// </summary>
    public static CrateIndexEntry? SelectLatestVersion(List<CrateIndexEntry> entries, string currentVersion)
    {
        if (entries == null || entries.Count == 0)
            return null;

        // Try SemVer parse or fallback to index chronological position
        CrateIndexEntry? latest = null;
        Version? latestParsed = null;

        foreach (var entry in entries)
        {
            if (Version.TryParse(entry.Version, out var parsed))
            {
                if (latestParsed == null || parsed > latestParsed)
                {
                    latestParsed = parsed;
                    latest = entry;
                }
            }
            else
            {
                // Fallback to chronological last
                latest = entry;
            }
        }

        if (latest == null)
            return null;

        if (string.Equals(latest.Version, currentVersion, StringComparison.Ordinal))
            return null; // Already latest

        if (Version.TryParse(currentVersion, out var curParsed) && latestParsed != null)
        {
            if (latestParsed <= curParsed)
                return null; // Target is not newer
        }

        return latest;
    }

    private static string ExtractCurrentVersion(string specContent)
    {
        var fullVerMatch = FullVersionRegex.Match(specContent);
        if (fullVerMatch.Success)
            return fullVerMatch.Groups["ver"].Value.Trim();

        var verMatch = VersionRegex.Match(specContent);
        if (verMatch.Success)
            return verMatch.Groups["ver"].Value.Trim();

        return string.Empty;
    }

    private async Task<(IReadOnlyList<SpecContext> Specs, int TotalSpecs, int StartIndex, int NextCursorStart, bool AlreadySliced)>
        LoadRustSpecsForSweepAsync(CancellationToken ct)
    {
        if (_specCatalogProvider != null)
        {
            var providerSpecs = await _specCatalogProvider(ct);
            var total = providerSpecs.Count;
            var start = total > 0 ? Math.Abs(_crateCursor) % total : 0;
            return (providerSpecs, total, start, 0, false);
        }

        if (_specCatalog != null)
        {
            // Slice rust-* package names BEFORE calling GetSpecAsync to avoid O(N) fallback fetches.
            var allNames = await _specCatalog.GetAllPackageNamesAsync(ct);
            var rustNames = allNames
                .Where(n => n.StartsWith("rust-", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (rustNames.Count == 0)
            {
                return (Array.Empty<SpecContext>(), 0, 0, 0, true);
            }

            var total = rustNames.Count;
            var startIndex = Math.Abs(_crateCursor) % total;
            var limit = _options.MaxCratesPerSweep > 0 ? Math.Min(_options.MaxCratesPerSweep, total) : total;
            var slicedSpecs = new List<SpecContext>(limit);
            int inspected = 0;

            for (int i = 0; i < total && slicedSpecs.Count < limit; i++)
            {
                if (ct.IsCancellationRequested)
                    break;

                inspected++;
                var name = rustNames[(startIndex + i) % total];
                var spec = await _specCatalog.GetSpecAsync(name, ct);
                if (spec != null)
                {
                    slicedSpecs.Add(spec);
                }
            }

            var nextCursor = (startIndex + inspected) % total;
            return (slicedSpecs, total, 0, nextCursor, true);
        }

        var localSpecs = await LoadLocalRustSpecsAsync(ct);
        var localTotal = localSpecs.Count;
        var localStart = localTotal > 0 ? Math.Abs(_crateCursor) % localTotal : 0;
        return (localSpecs, localTotal, localStart, 0, false);
    }

    private async Task<IReadOnlyList<SpecContext>> LoadLocalRustSpecsAsync(CancellationToken ct)
    {
        // Default: discover from local SPECS/ directory if available
        var list = new List<SpecContext>();
        const string specsDir = "SPECS";
        if (!Directory.Exists(specsDir))
            return list;

        foreach (var dir in Directory.GetDirectories(specsDir, "rust-*"))
        {
            if (ct.IsCancellationRequested)
                break;

            var dirName = Path.GetFileName(dir);
            var specFiles = Directory.GetFiles(dir, "*.spec");
            if (specFiles.Length == 0)
                continue;

            var specFile = specFiles[0];
            try
            {
                var content = await File.ReadAllTextAsync(specFile, ct);
                var relPath = specFile.Replace('\\', '/');
                list.Add(new SpecContext(dirName, dirName, content, relPath));
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to read SPEC file {File}", specFile);
            }
        }

        return list;
    }

    public class CrateIndexEntry
    {
        [JsonPropertyName("vers")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("yanked")]
        public bool Yanked { get; set; }

        [JsonPropertyName("pubtime")]
        public DateTimeOffset? Pubtime { get; set; }
    }
}

