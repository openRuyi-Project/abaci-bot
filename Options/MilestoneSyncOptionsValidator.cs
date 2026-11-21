using abaci_bot.Services;
using Microsoft.Extensions.Options;

namespace abaci_bot.Options;

/// <summary>
/// Startup validator for <see cref="MilestoneSyncOptions"/>.
/// </summary>
public class MilestoneSyncOptionsValidator : IValidateOptions<MilestoneSyncOptions>
{
    public ValidateOptionsResult Validate(string? name, MilestoneSyncOptions options)
    {
        var failures = new List<string>();

        if (options.DueDayOfMonth is < 1 or > 28)
        {
            failures.Add($"{nameof(options.DueDayOfMonth)} must be between 1 and 28 (got {options.DueDayOfMonth}).");
        }

        if (options.BackfillDays is < 1 or > 365)
        {
            failures.Add($"{nameof(options.BackfillDays)} must be between 1 and 365 (got {options.BackfillDays}).");
        }

        if (options.SweepIntervalHours is < 1 or > 168)
        {
            failures.Add($"{nameof(options.SweepIntervalHours)} must be between 1 and 168 (got {options.SweepIntervalHours}).");
        }

        if (string.IsNullOrWhiteSpace(options.TimeZoneId) ||
            !TimeZoneInfo.TryFindSystemTimeZoneById(options.TimeZoneId.Trim(), out _))
        {
            failures.Add($"{nameof(options.TimeZoneId)} must be a valid IANA or system timezone ID (got '{options.TimeZoneId}').");
        }

        foreach (var invalid in RepositoryAllowList.FindInvalidEntries(options.RepositoryAllowList))
        {
            failures.Add($"{nameof(options.RepositoryAllowList)} entry '{invalid}' must be formatted as 'owner/repo'.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
