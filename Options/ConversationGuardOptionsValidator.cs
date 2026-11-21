using abaci_bot.Services;
using Microsoft.Extensions.Options;

namespace abaci_bot.Options;

/// <summary>
/// Startup validator for <see cref="ConversationGuardOptions"/>.
/// </summary>
public class ConversationGuardOptionsValidator : IValidateOptions<ConversationGuardOptions>
{
    public ValidateOptionsResult Validate(string? name, ConversationGuardOptions options)
    {
        var failures = new List<string>();

        if (options.InactiveCloseDays is < 7 or > 365)
        {
            failures.Add($"{nameof(options.InactiveCloseDays)} must be between 7 and 365 (got {options.InactiveCloseDays}).");
        }

        if (options.SweepIntervalHours is < 1 or > 72)
        {
            failures.Add($"{nameof(options.SweepIntervalHours)} must be between 1 and 72 (got {options.SweepIntervalHours}).");
        }

        if (options.AuthorLockConfirmTimeoutMinutes is < 1 or > 60)
        {
            failures.Add($"{nameof(options.AuthorLockConfirmTimeoutMinutes)} must be between 1 and 60 (got {options.AuthorLockConfirmTimeoutMinutes}).");
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
