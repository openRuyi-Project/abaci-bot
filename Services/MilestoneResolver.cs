using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Octokit;

namespace abaci_bot.Services;

/// <summary>
/// Thread-safe, cached resolver for release milestones (YYYY.MM).
/// Serializes creation per repository and self-heals cached state upon HTTP 422 duplicate milestone conflicts.
/// </summary>
public partial class MilestoneResolver : IMilestoneResolver
{
    private readonly IGitHubService _gitHubService;
    private readonly ILogger<MilestoneResolver>? _logger;
    private readonly TimeSpan _cacheTtl;

    [GeneratedRegex(@"^\d{4}\.\d{2}$")]
    private static partial Regex MonthMilestoneRegex();

    private sealed record CacheEntry(DateTimeOffset CachedAt, List<(string Title, int Number)> Milestones);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public MilestoneResolver(IGitHubService gitHubService, ILogger<MilestoneResolver>? logger = null, TimeSpan? cacheTtl = null)
    {
        _gitHubService = gitHubService;
        _logger = logger;
        _cacheTtl = cacheTtl ?? TimeSpan.FromMinutes(5);
    }

    public async Task<int?> ResolveMilestoneAsync(
        string owner,
        string repo,
        DateTimeOffset targetDate,
        bool creationEnabled = false,
        int dueDayOfMonth = 25,
        string timeZoneId = "Asia/Shanghai",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new ArgumentException("TimeZoneId must be a non-empty IANA or system timezone identifier.", nameof(timeZoneId));
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
        var localDate = TimeZoneInfo.ConvertTime(targetDate, timeZone);
        var key = $"{owner}/{repo}";
        var title = localDate.ToString("yyyy.MM", System.Globalization.CultureInfo.InvariantCulture);

        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var milestones = await GetOrFetchMilestonesAsync(owner, repo, forceRefresh: false, ct);
            var existing = milestones.FirstOrDefault(m => string.Equals(m.Title, title, StringComparison.OrdinalIgnoreCase));
            if (existing.Number != 0)
            {
                return existing.Number;
            }

            if (!creationEnabled)
            {
                _logger?.LogInformation(
                    "Milestone {Title} not found in {Owner}/{Repo} and CreationEnabled is false.",
                    title, owner, repo);
                return null;
            }

            var daysInMonth = DateTime.DaysInMonth(localDate.Year, localDate.Month);
            var day = Math.Clamp(dueDayOfMonth, 1, daysInMonth);
            var dueOn = new DateTimeOffset(localDate.Year, localDate.Month, day, 23, 59, 59, TimeSpan.Zero);

            _logger?.LogInformation("Creating missing release milestone {Title} in {Owner}/{Repo} (Due: {DueOn})",
                title, owner, repo, dueOn);

            try
            {
                var created = await _gitHubService.CreateMilestoneAsync(
                    owner,
                    repo,
                    title,
                    dueOn,
                    $"Release milestone for {title}",
                    ct);

                milestones.Add((created.Title, created.Number));
                _cache[key] = new CacheEntry(DateTimeOffset.UtcNow, milestones);

                return created.Number;
            }
            catch (ApiValidationException ex)
            {
                // Self-heal: 422 usually indicates an externally created milestone after the last cache fill
                _logger?.LogInformation(
                    ex,
                    "Milestone creation for '{Title}' in {Owner}/{Repo} returned 422 validation conflict; refreshing remote milestones.",
                    title, owner, repo);

                var refreshed = await GetOrFetchMilestonesAsync(owner, repo, forceRefresh: true, ct);
                var conflictMatch = refreshed.FirstOrDefault(m => string.Equals(m.Title, title, StringComparison.OrdinalIgnoreCase));
                if (conflictMatch.Number != 0)
                {
                    return conflictMatch.Number;
                }

                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<List<(string Title, int Number)>> GetOrFetchMilestonesAsync(
        string owner,
        string repo,
        bool forceRefresh,
        CancellationToken ct)
    {
        var key = $"{owner}/{repo}";
        if (!forceRefresh && _cache.TryGetValue(key, out var entry) && (DateTimeOffset.UtcNow - entry.CachedAt) < _cacheTtl)
        {
            return entry.Milestones;
        }

        var remote = await _gitHubService.GetMilestonesAsync(owner, repo, ItemStateFilter.All, ct);
        var filtered = remote
            .Where(m => !string.IsNullOrWhiteSpace(m.Title))
            .Where(m => MonthMilestoneRegex().IsMatch(m.Title.Trim()))
            .Select(m => (m.Title.Trim(), m.Number))
            .ToList();

        _cache[key] = new CacheEntry(DateTimeOffset.UtcNow, filtered);
        return filtered;
    }
}
