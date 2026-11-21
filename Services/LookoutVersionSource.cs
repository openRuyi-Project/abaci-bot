using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Primary update candidate source consuming lookout.openruyi.cn fact read-model.
/// Evaluates 'last_known_relation == outdated', skips yanked annotations, and filters stale records.
/// </summary>
public class LookoutVersionSource : IUpdateCandidateSource
{
    private readonly HttpClient _httpClient;
    private readonly LookoutUpdateOptions _options;
    private readonly ILogger<LookoutVersionSource>? _logger;
    private readonly RuntimeHealthState? _healthState;
    private readonly string _stateFilePath;
    private readonly object _stateWriteLock = new();
    private readonly ConcurrentDictionary<string, (string Version, DateTimeOffset FirstSeen)> _firstSeenVersions = new(StringComparer.OrdinalIgnoreCase);

    public string SourceName => "Lookout";

    public LookoutVersionSource(
        HttpClient httpClient,
        IOptions<PackageUpdateOptions> options,
        ILogger<LookoutVersionSource>? logger = null,
        IBotStateStore? stateStore = null,
        RuntimeHealthState? healthState = null)
    {
        _httpClient = httpClient;
        _options = options.Value.Lookout;
        _logger = logger;
        _healthState = healthState;
        var defaultStateDir = stateStore?.StateDirectory
            ?? BotStatePathResolver.ResolveStateDirectory(null);
        _stateFilePath = string.IsNullOrWhiteSpace(_options.FirstSeenStatePath)
            ? Path.Combine(defaultStateDir, "lookout-first-seen.json")
            : _options.FirstSeenStatePath;

        LoadFirstSeenState();
    }

    public async Task<IReadOnlyList<UpdateCandidate>> GetCandidatesAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _logger?.LogInformation("Lookout version source is disabled or BaseUrl is empty");
            _healthState?.RecordSourceOutcome(SourceName, "disabled", 0);
            return Array.Empty<UpdateCandidate>();
        }

        var candidates = new List<UpdateCandidate>();
        var seenOutdatedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stateChanged = false;
        var completedFullSweep = false;
        var hadApiError = false;
        string? lastError = null;
        var baseUrl = _options.BaseUrl.TrimEnd('/');
        int page = 1;
        const int perPage = 200;
        const int maxPages = 50;

        while (!ct.IsCancellationRequested)
        {
            if (page > maxPages)
            {
                _logger?.LogInformation("Reached max page limit ({Max}) for Lookout queries", maxPages);
                break;
            }

            var url = $"{baseUrl}/api/v2/packages?per_page={perPage}&page={page}";
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.GetAsync(url, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                hadApiError = true;
                lastError = $"{ex.GetType().Name}: {ex.Message}";
                _logger?.LogWarning(ex, "Failed to query Lookout packages page {Page} from {Url}", page, url);
                break;
            }

            if (!response.IsSuccessStatusCode)
            {
                hadApiError = true;
                lastError = $"HTTP {(int)response.StatusCode} ({response.StatusCode}) on page {page}";
                _logger?.LogWarning("Lookout API returned status code {StatusCode} on page {Page}", response.StatusCode, page);
                break;
            }

            LookoutPackageResponse? pageResult;
            try
            {
                pageResult = await response.Content.ReadFromJsonAsync<LookoutPackageResponse>(cancellationToken: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                hadApiError = true;
                lastError = $"{ex.GetType().Name}: {ex.Message}";
                _logger?.LogWarning(ex, "Failed to deserialize Lookout response on page {Page}", page);
                break;
            }

            var items = pageResult?.Packages ?? pageResult?.Data ?? pageResult?.Items;
            if (items == null || items.Count == 0)
            {
                completedFullSweep = true;
                break;
            }

            foreach (var pkg in items)
            {
                if (string.IsNullOrWhiteSpace(pkg.Name))
                    continue;

                // Support both nested structure (monitors.version.data) and legacy flat structure for maximum resilience
                var versionData = pkg.Monitors?.Version?.Data;
                var currentVer = versionData?.Current ?? pkg.CurrentVersion;
                var latestVer = versionData?.Latest ?? pkg.LatestVersion;
                var relation = versionData?.LastKnownRelation ?? versionData?.Relation ?? pkg.LastKnownRelation ?? pkg.Relation;
                var isStale = versionData?.Stale ?? pkg.Stale;
                var updatedAt = versionData?.UpdatedAt ?? pkg.UpdatedAt;
                var annotations = versionData?.Annotations ?? pkg.Annotations;

                // 1. Must be outdated
                if (!string.Equals(relation, "outdated", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrWhiteSpace(currentVer) || string.IsNullOrWhiteSpace(latestVer))
                    continue;

                if (string.Equals(currentVer, latestVer, StringComparison.Ordinal))
                    continue;

                // 2. Yanked filter: skip if annotations declare monitor == "yanked"
                if (annotations != null && annotations.Any(a => string.Equals(a.Monitor, "yanked", StringComparison.OrdinalIgnoreCase)))
                {
                    _logger?.LogDebug("Skipping {Package} because it is marked as yanked in Lookout", pkg.Name);
                    continue;
                }

                // 3. Stale filter: if stale == true and older than threshold
                if (isStale && updatedAt.HasValue)
                {
                    var age = DateTimeOffset.UtcNow - updatedAt.Value;
                    if (age.TotalHours > _options.StaleThresholdHours)
                    {
                        _logger?.LogDebug("SKIP: lookout stale (last observed: {UpdatedAt}) for {Package}", updatedAt.Value, pkg.Name);
                        continue;
                    }
                }

                var evidenceUrl = !string.IsNullOrWhiteSpace(pkg.DetailUrl)
                    ? (pkg.DetailUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? pkg.DetailUrl : $"{baseUrl.TrimEnd('/')}/{pkg.DetailUrl.TrimStart('/')}")
                    : (!string.IsNullOrWhiteSpace(pkg.EvidenceUrl)
                        ? pkg.EvidenceUrl
                        : $"{baseUrl}/package/{pkg.Name}");

                // 4. Security advisory: annotations have monitor == "security"
                var hasSecurity = pkg.HasSecurityAdvisory || 
                                  (annotations != null && annotations.Any(a => string.Equals(a.Monitor, "security", StringComparison.OrdinalIgnoreCase)));

                seenOutdatedPackages.Add(pkg.Name);
                var upstreamPublishedAt = versionData?.PublishedAt ?? pkg.PublishedAt;
                DateTimeOffset effectivePublishedAt;
                if (upstreamPublishedAt.HasValue)
                {
                    effectivePublishedAt = upstreamPublishedAt.Value;
                }
                else
                {
                    var observedTime = updatedAt ?? DateTimeOffset.UtcNow;
                    if (_firstSeenVersions.TryGetValue(pkg.Name, out var existing) &&
                        string.Equals(existing.Version, latestVer, StringComparison.Ordinal))
                    {
                        effectivePublishedAt = existing.FirstSeen;
                    }
                    else
                    {
                        _firstSeenVersions[pkg.Name] = (latestVer, observedTime);
                        stateChanged = true;
                        effectivePublishedAt = observedTime;
                    }
                }

                candidates.Add(new UpdateCandidate(
                    PackageName: pkg.Name,
                    CurrentVersion: currentVer,
                    TargetVersion: latestVer,
                    Origin: CandidateOrigin.Lookout,
                    EvidenceUrl: evidenceUrl,
                    IsSecurityAdvisory: hasSecurity,
                    PublishedAt: effectivePublishedAt));
            }

            if (items.Count < perPage)
            {
                completedFullSweep = true;
                break;
            }

            await Task.Delay(50, ct);
            page++;
        }

        if (completedFullSweep)
        {
            foreach (var key in _firstSeenVersions.Keys)
            {
                if (!seenOutdatedPackages.Contains(key) && _firstSeenVersions.TryRemove(key, out _))
                {
                    stateChanged = true;
                }
            }
        }

        if (stateChanged)
        {
            SaveFirstSeenState();
        }

        if (hadApiError && candidates.Count == 0)
        {
            _logger?.LogError("Lookout version source failed with API/parse errors and returned 0 candidates. Last error: {Error}", lastError);
            _healthState?.RecordSourceOutcome(SourceName, "failed", 0, lastError);
        }
        else if (hadApiError)
        {
            _logger?.LogWarning("Lookout version source returned partial results ({Count} candidates) due to page fetch errors. Last error: {Error}", candidates.Count, lastError);
            _healthState?.RecordSourceOutcome(SourceName, "degraded", candidates.Count, lastError);
        }
        else
        {
            _healthState?.RecordSourceOutcome(SourceName, "healthy", candidates.Count);
        }

        _logger?.LogInformation("Lookout version source discovered {Count} outdated candidates", candidates.Count);
        return candidates;
    }

    private void LoadFirstSeenState()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
            {
                return;
            }

            var json = File.ReadAllText(_stateFilePath);
            var persisted = JsonSerializer.Deserialize<Dictionary<string, PersistedFirstSeenEntry>>(json);
            if (persisted == null)
            {
                return;
            }

            foreach (var (pkgName, entry) in persisted)
            {
                if (!string.IsNullOrWhiteSpace(pkgName) && !string.IsNullOrWhiteSpace(entry.Version))
                {
                    _firstSeenVersions[pkgName] = (entry.Version, entry.FirstSeen);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load Lookout first-seen state from {Path}; starting with empty state", _stateFilePath);
        }
    }

    private void SaveFirstSeenState()
    {
        lock (_stateWriteLock)
        {
            try
            {
                var directory = Path.GetDirectoryName(_stateFilePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var snapshot = new Dictionary<string, PersistedFirstSeenEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var (pkgName, entry) in _firstSeenVersions)
                {
                    snapshot[pkgName] = new PersistedFirstSeenEntry(entry.Version, entry.FirstSeen);
                }

                var json = JsonSerializer.Serialize(snapshot);
                var tempPath = _stateFilePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _stateFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to persist Lookout first-seen state to {Path}", _stateFilePath);
            }
        }
    }

    private sealed record PersistedFirstSeenEntry(string Version, DateTimeOffset FirstSeen);

    public class LookoutPackageResponse
    {
        [JsonPropertyName("packages")]
        public List<LookoutPackageItem>? Packages { get; set; }

        [JsonPropertyName("data")]
        public List<LookoutPackageItem>? Data { get; set; }

        [JsonPropertyName("items")]
        public List<LookoutPackageItem>? Items { get; set; }
    }

    public class LookoutPackageItem
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("detail_url")]
        public string? DetailUrl { get; set; }

        [JsonPropertyName("monitors")]
        public LookoutMonitors? Monitors { get; set; }

        [JsonPropertyName("current_version")]
        public string? CurrentVersion { get; set; }

        [JsonPropertyName("latest_version")]
        public string? LatestVersion { get; set; }

        [JsonPropertyName("relation")]
        public string? Relation { get; set; }

        [JsonPropertyName("last_known_relation")]
        public string? LastKnownRelation { get; set; }

        [JsonPropertyName("stale")]
        public bool Stale { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset? UpdatedAt { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("has_security_advisory")]
        public bool HasSecurityAdvisory { get; set; }

        [JsonPropertyName("evidence_url")]
        public string? EvidenceUrl { get; set; }

        [JsonPropertyName("annotations")]
        public List<LookoutAnnotation>? Annotations { get; set; }
    }

    public class LookoutMonitors
    {
        [JsonPropertyName("version")]
        public LookoutMonitorVersion? Version { get; set; }
    }

    public class LookoutMonitorVersion
    {
        [JsonPropertyName("data")]
        public LookoutMonitorVersionData? Data { get; set; }
    }

    public class LookoutMonitorVersionData
    {
        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("current")]
        public string? Current { get; set; }

        [JsonPropertyName("latest")]
        public string? Latest { get; set; }

        [JsonPropertyName("relation")]
        public string? Relation { get; set; }

        [JsonPropertyName("last_known_relation")]
        public string? LastKnownRelation { get; set; }

        [JsonPropertyName("stale")]
        public bool Stale { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset? UpdatedAt { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("annotations")]
        public List<LookoutAnnotation>? Annotations { get; set; }
    }

    public class LookoutAnnotation
    {
        [JsonPropertyName("monitor")]
        public string? Monitor { get; set; }

        [JsonPropertyName("label")]
        public string? Label { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }
    }
}

