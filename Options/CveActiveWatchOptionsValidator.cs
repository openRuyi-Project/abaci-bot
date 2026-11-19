using Microsoft.Extensions.Options;

namespace abaci_bot.Options;

/// <summary>
/// Startup validator for <see cref="CveActiveWatchOptions"/>.
/// Enforces fail-fast bounds checking so scan parameters, batch limits, and rate limits are valid.
/// </summary>
public class CveActiveWatchOptionsValidator : IValidateOptions<CveActiveWatchOptions>
{
    public ValidateOptionsResult Validate(string? name, CveActiveWatchOptions options)
    {
        var failures = new List<string>();

        if (options.CheckIntervalHours <= 0)
        {
            failures.Add($"{nameof(options.CheckIntervalHours)} must be positive (got {options.CheckIntervalHours}).");
        }

        if (options.OsvBatchSize <= 0 || options.OsvBatchSize > 1000)
        {
            failures.Add($"{nameof(options.OsvBatchSize)} must be between 1 and 1000 (got {options.OsvBatchSize}).");
        }

        if (options.PackagesPerSweep < 0)
        {
            failures.Add($"{nameof(options.PackagesPerSweep)} cannot be negative (got {options.PackagesPerSweep}).");
        }

        if (options.MaxNvdQueriesPerDay < 0)
        {
            failures.Add($"{nameof(options.MaxNvdQueriesPerDay)} cannot be negative (got {options.MaxNvdQueriesPerDay}).");
        }

        if (options.NvdRequestDelayMilliseconds < 0)
        {
            failures.Add($"{nameof(options.NvdRequestDelayMilliseconds)} cannot be negative (got {options.NvdRequestDelayMilliseconds}).");
        }

        if (options.MaxConcurrentOsvRequests <= 0)
        {
            failures.Add($"{nameof(options.MaxConcurrentOsvRequests)} must be positive (got {options.MaxConcurrentOsvRequests}).");
        }

        if (options.CacheExpirationHours <= 0)
        {
            failures.Add($"{nameof(options.CacheExpirationHours)} must be positive (got {options.CacheExpirationHours}).");
        }

        if (string.IsNullOrWhiteSpace(options.TargetRepository) || options.TargetRepository.Split('/').Length != 2)
        {
            failures.Add($"{nameof(options.TargetRepository)} must be in 'owner/repo' format (got '{options.TargetRepository}').");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
