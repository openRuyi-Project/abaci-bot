using Microsoft.Extensions.Options;

namespace abaci_bot.Options;

/// <summary>
/// Startup validator for <see cref="PackageUpdateOptions"/>.
/// Enforces fail-fast bounds checking so misconfigured settings do not cause destructive bulk operations.
/// </summary>
public class PackageUpdateOptionsValidator : IValidateOptions<PackageUpdateOptions>
{
    public ValidateOptionsResult Validate(string? name, PackageUpdateOptions options)
    {
        var failures = new List<string>();

        if (options.CheckIntervalHours <= 0)
        {
            failures.Add($"{nameof(options.CheckIntervalHours)} must be positive (got {options.CheckIntervalHours}).");
        }

        if (options.MaxCandidatesPerSweep <= 0)
        {
            failures.Add($"{nameof(options.MaxCandidatesPerSweep)} must be positive (got {options.MaxCandidatesPerSweep}).");
        }

        if (options.MaxConcurrentPullRequests <= 0)
        {
            failures.Add($"{nameof(options.MaxConcurrentPullRequests)} must be positive (got {options.MaxConcurrentPullRequests}).");
        }

        if (options.FailedPrTtlDays <= 0)
        {
            failures.Add($"{nameof(options.FailedPrTtlDays)} must be positive (got {options.FailedPrTtlDays}).");
        }

        if (options.CooldownHours < 0)
        {
            failures.Add($"{nameof(options.CooldownHours)} cannot be negative (got {options.CooldownHours}).");
        }

        if (options.MaxConsecutiveFailures <= 0)
        {
            failures.Add($"{nameof(options.MaxConsecutiveFailures)} must be positive (got {options.MaxConsecutiveFailures}).");
        }

        if (options.GiveUpIssueCooldownDays <= 0)
        {
            failures.Add($"{nameof(options.GiveUpIssueCooldownDays)} must be positive (got {options.GiveUpIssueCooldownDays}).");
        }

        if (options.MinRateLimitRemaining < 0)
        {
            failures.Add($"{nameof(options.MinRateLimitRemaining)} cannot be negative (got {options.MinRateLimitRemaining}).");
        }

        if (options.RequestThrottleMilliseconds < 0)
        {
            failures.Add($"{nameof(options.RequestThrottleMilliseconds)} cannot be negative (got {options.RequestThrottleMilliseconds}).");
        }

        if (options.MaxArchiveBytes < 0)
        {
            failures.Add($"{nameof(options.MaxArchiveBytes)} cannot be negative (got {options.MaxArchiveBytes}).");
        }

        if (string.IsNullOrWhiteSpace(options.TargetRepository) || options.TargetRepository.Split('/').Length != 2)
        {
            failures.Add($"{nameof(options.TargetRepository)} must be in 'owner/repo' format (got '{options.TargetRepository}').");
        }

        if (options.Lookout != null && options.Lookout.StaleThresholdHours <= 0)
        {
            failures.Add($"{nameof(options.Lookout.StaleThresholdHours)} must be positive (got {options.Lookout.StaleThresholdHours}).");
        }

        if (options.RustCratePoller != null)
        {
            if (options.RustCratePoller.MaxCratesPerSweep <= 0)
            {
                failures.Add($"{nameof(options.RustCratePoller.MaxCratesPerSweep)} must be positive (got {options.RustCratePoller.MaxCratesPerSweep}).");
            }

            if (options.RustCratePoller.RequestDelayMilliseconds < 0)
            {
                failures.Add($"{nameof(options.RustCratePoller.RequestDelayMilliseconds)} cannot be negative (got {options.RustCratePoller.RequestDelayMilliseconds}).");
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
