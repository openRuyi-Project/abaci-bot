using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Deep module implementing dual-source CVE querying (OSV.dev + NVD fallback)
/// with in-memory caching and graceful network degradation.
/// </summary>
public class CveSecurityService : ICveSecurityService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CveSecurityOptions _options;
    private readonly ILogger<CveSecurityService>? _logger;

    private readonly ConcurrentDictionary<string, (CveReport Report, DateTime Expiry)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyDictionary<string, (CveReport Report, DateTime Expiry)> Cache => _cache;

    private static readonly string[] ParagraphSeparators = ["\r\n\r\n", "\n\n"];

    public CveSecurityService(
        IHttpClientFactory httpClientFactory,
        IOptions<CveSecurityOptions> options,
        ILogger<CveSecurityService>? logger = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<CveReport> QueryCveAsync(string cveId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cveId))
            return new CveReport(cveId, null, null, CveReport.SourceUnknown);

        var normalizedCve = cveId.Trim().ToUpperInvariant();

        if (_cache.TryGetValue(normalizedCve, out var cached) && cached.Expiry > DateTime.UtcNow)
        {
            return cached.Report;
        }

        // 1. Try OSV.dev first
        bool osvFailed = false;
        CveReport? report = null;
        try
        {
            report = await TryQueryOsvAsync(normalizedCve, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            osvFailed = true;
        }

        // 2. If OSV didn't provide a score, try NVD
        bool nvdFailed = false;
        if (report == null || report.CvssScore == null)
        {
            try
            {
                var nvdReport = await TryQueryNvdAsync(normalizedCve, ct);
                if (nvdReport != null && nvdReport.CvssScore != null)
                {
                    report = nvdReport;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                nvdFailed = true;
            }
        }

        // If upstream failed with network/timeout/rate-limiting errors, report transient failure and do not cache long-term
        if ((report == null || report.CvssScore == null) && (osvFailed || nvdFailed))
        {
            return new CveReport(normalizedCve, null, null, CveReport.SourceQueryFailed, "Query timed out or failed to reach vulnerability databases.");
        }

        report ??= new CveReport(normalizedCve, null, null, CveReport.SourceUnknown, "Vulnerability details unavailable.");

        // SourceUnknown (embargoed or unindexed) uses a shorter TTL so disclosure is detected promptly;
        // confirmed CVE details are cached for full CacheExpirationHours.
        var cacheDuration = string.Equals(report.Source, CveReport.SourceUnknown, StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromHours(_options.UnknownCacheExpirationHours)
            : TimeSpan.FromHours(_options.CacheExpirationHours);

        _cache[normalizedCve] = (report, DateTime.UtcNow.Add(cacheDuration));
        return report;
    }

    public async Task<IReadOnlyList<CveReport>> QueryMultipleCvesAsync(IEnumerable<string> cveIds, CancellationToken ct = default)
    {
        var distinctIds = cveIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var throttler = new SemaphoreSlim(4);
        var tasks = distinctIds.Select(async id =>
        {
            await throttler.WaitAsync(ct);
            try
            {
                return await QueryCveAsync(id, ct);
            }
            finally
            {
                throttler.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results;
    }

    private async Task<CveReport?> TryQueryOsvAsync(string cveId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.OsvApiBaseUrl))
            return null;

        try
        {
            var client = _httpClientFactory.CreateClient("OsvClient");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));

            var url = $"{_options.OsvApiBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(cveId)}";
            var response = await client.GetAsync(url, timeoutCts.Token);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"OSV returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);

            var root = doc.RootElement;
            string? summary = null;
            if (root.TryGetProperty("summary", out var summaryProp))
            {
                summary = summaryProp.GetString();
            }

            // Fall back to first paragraph of details if summary is empty
            if (string.IsNullOrWhiteSpace(summary) && root.TryGetProperty("details", out var detailsProp))
            {
                var details = detailsProp.GetString();
                if (!string.IsNullOrWhiteSpace(details))
                {
                    var firstPara = details.Split(ParagraphSeparators, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    summary = firstPara?.Trim();
                }
            }

            double? cvssScore = null;

            // Check database_specific for direct score
            if (root.TryGetProperty("database_specific", out var dbSpecific) &&
                dbSpecific.TryGetProperty("severity", out var dbSeverityProp))
            {
                if (dbSeverityProp.ValueKind == JsonValueKind.Number)
                    cvssScore = dbSeverityProp.GetDouble();
                else if (dbSeverityProp.ValueKind == JsonValueKind.String &&
                         double.TryParse(dbSeverityProp.GetString(), out var parsedScore))
                    cvssScore = parsedScore;
            }

            // Check severity array
            if (cvssScore == null && root.TryGetProperty("severity", out var severityArray) &&
                severityArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in severityArray.EnumerateArray())
                {
                    if (item.TryGetProperty("score", out var scoreProp))
                    {
                        var scoreStr = scoreProp.GetString();
                        if (!string.IsNullOrWhiteSpace(scoreStr))
                        {
                            // Could be direct number or CVSS vector like CVSS:3.1/AV:N/AC:L/...
                            if (double.TryParse(scoreStr, out var num))
                            {
                                cvssScore = num;
                                break;
                            }
                            else if (scoreStr.StartsWith("CVSS:3", StringComparison.OrdinalIgnoreCase))
                            {
                                var calculatedScore = CvssCalculator.CalculateBaseScore(scoreStr);
                                if (calculatedScore.HasValue)
                                {
                                    cvssScore = calculatedScore.Value;
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            var severityLabel = MapCvssToSeverity(cvssScore);
            return new CveReport(cveId, cvssScore, severityLabel, "OSV.dev", summary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger?.LogDebug(ex, "Failed to query OSV for {CveId}. Falling back.", cveId);
            throw;
        }
    }

    private async Task<CveReport?> TryQueryNvdAsync(string cveId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.NvdApiBaseUrl))
            return null;

        try
        {
            var client = _httpClientFactory.CreateClient("NvdClient");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));

            var url = $"{_options.NvdApiBaseUrl}?cveId={Uri.EscapeDataString(cveId)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(_options.NvdApiKey))
            {
                request.Headers.Add("apiKey", _options.NvdApiKey);
            }

            var response = await client.SendAsync(request, timeoutCts.Token);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"NVD returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);

            var root = doc.RootElement;
            if (!root.TryGetProperty("vulnerabilities", out var vulns) || vulns.GetArrayLength() == 0)
                return null;

            var firstVuln = vulns[0];
            if (!firstVuln.TryGetProperty("cve", out var cveObj))
                return null;

            string? summary = null;
            if (cveObj.TryGetProperty("descriptions", out var descs) && descs.GetArrayLength() > 0)
            {
                summary = descs[0].GetProperty("value").GetString();
            }

            double? cvssScore = null;
            if (cveObj.TryGetProperty("metrics", out var metrics))
            {
                // Check cvssMetricV31
                if (metrics.TryGetProperty("cvssMetricV31", out var v31) && v31.GetArrayLength() > 0)
                {
                    cvssScore = v31[0].GetProperty("cvssData").GetProperty("baseScore").GetDouble();
                }
                // Check cvssMetricV30
                else if (metrics.TryGetProperty("cvssMetricV30", out var v30) && v30.GetArrayLength() > 0)
                {
                    cvssScore = v30[0].GetProperty("cvssData").GetProperty("baseScore").GetDouble();
                }
                // Check cvssMetricV2
                else if (metrics.TryGetProperty("cvssMetricV2", out var v2) && v2.GetArrayLength() > 0)
                {
                    cvssScore = v2[0].GetProperty("cvssData").GetProperty("baseScore").GetDouble();
                }
            }

            var severityLabel = MapCvssToSeverity(cvssScore);
            return new CveReport(cveId, cvssScore, severityLabel, "NVD", summary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger?.LogDebug(ex, "Failed to query NVD for {CveId}.", cveId);
            throw;
        }
    }

    public static string? MapCvssToSeverity(double? score)
    {
        if (score == null)
            return null;

        var val = score.Value;
        if (val >= 9.0)
            return GitHubLabels.Severity1;
        if (val >= 7.0)
            return GitHubLabels.Severity2;
        if (val >= 4.0)
            return GitHubLabels.Severity3;
        return GitHubLabels.Severity4;
    }
}
