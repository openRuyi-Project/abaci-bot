using Microsoft.Extensions.Options;

namespace abaci_bot.Options;

/// <summary>
/// Startup validator for <see cref="BotStateOptions"/>.
/// </summary>
public class BotStateOptionsValidator : IValidateOptions<BotStateOptions>
{
    public ValidateOptionsResult Validate(string? name, BotStateOptions options)
    {
        var failures = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.StateDirectory) &&
            !Path.IsPathRooted(options.StateDirectory.Trim()))
        {
            failures.Add($"{nameof(options.StateDirectory)} must be an absolute path when specified (got '{options.StateDirectory}').");
        }

        if (options.LocaleRetentionDays <= 0)
        {
            failures.Add($"{nameof(options.LocaleRetentionDays)} must be positive (got {options.LocaleRetentionDays}).");
        }

        if (!IsSupportedLocale(options.DefaultLocale))
        {
            failures.Add($"{nameof(options.DefaultLocale)} must be 'en' or 'zh' (got '{options.DefaultLocale}').");
        }

        if (options.EphemeralReplyDeleteMinutes < 0 || options.EphemeralReplyDeleteMinutes > 1440)
        {
            failures.Add($"{nameof(options.EphemeralReplyDeleteMinutes)} must be between 0 and 1440 (got {options.EphemeralReplyDeleteMinutes}).");
        }

        if (!Enum.IsDefined(options.EphemeralReplyAction))
        {
            failures.Add($"{nameof(options.EphemeralReplyAction)} must be 'Delete' or 'Minimize' (got '{options.EphemeralReplyAction}').");
        }

        ValidateLocaleMap(options.RepositoryForcedLocales, nameof(options.RepositoryForcedLocales), failures);
        ValidateLocaleMap(options.RepositoryDefaultLocales, nameof(options.RepositoryDefaultLocales), failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateLocaleMap(
        Dictionary<string, string>? map,
        string propertyName,
        List<string> failures)
    {
        if (map == null)
        {
            return;
        }

        foreach (var (repo, locale) in map)
        {
            if (string.IsNullOrWhiteSpace(repo) || repo.Split('/').Length != 2)
            {
                failures.Add($"{propertyName} contains invalid repository key '{repo}'; expected 'owner/repo'.");
            }

            if (!IsSupportedLocale(locale))
            {
                failures.Add($"{propertyName}['{repo}'] must be 'en' or 'zh' (got '{locale}').");
            }
        }
    }

    internal static bool IsSupportedLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return false;
        }

        var normalized = locale.Trim().ToLowerInvariant();
        return normalized is "en" or "zh" ||
               normalized.StartsWith("en-", StringComparison.Ordinal) ||
               normalized.StartsWith("zh-", StringComparison.Ordinal);
    }
}
