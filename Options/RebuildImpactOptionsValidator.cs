using Microsoft.Extensions.Options;

namespace abaci_bot.Options;

/// <summary>
/// Startup validator for <see cref="RebuildImpactOptions"/>.
/// Ensures rebuild thresholds and optional verification guide links are well-formed before startup completes.
/// </summary>
public class RebuildImpactOptionsValidator : IValidateOptions<RebuildImpactOptions>
{
    public const int MaxVerificationGuideLinks = 5;
    public const int MaxLinkTitleLength = 120;

    public ValidateOptionsResult Validate(string? name, RebuildImpactOptions options)
    {
        var failures = new List<string>();

        if (options.RefreshIntervalHours <= 0)
        {
            failures.Add($"{nameof(options.RefreshIntervalHours)} must be positive (got {options.RefreshIntervalHours}).");
        }

        if (options.BlockThreshold < 0)
        {
            failures.Add($"{nameof(options.BlockThreshold)} cannot be negative (got {options.BlockThreshold}).");
        }

        if (options.MaxDepth is <= 0)
        {
            failures.Add($"{nameof(options.MaxDepth)} must be null or >= 1 (got {options.MaxDepth}).");
        }

        if (!string.Equals(options.CalculationMode, "Transitive", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.CalculationMode, "Direct", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{nameof(options.CalculationMode)} must be 'Transitive' or 'Direct' (got '{options.CalculationMode}').");
        }

        ValidateReferenceLinks(options.VerificationGuideLinks, failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateReferenceLinks(
        IReadOnlyList<VerificationReferenceLink>? links,
        List<string> failures)
    {
        if (links == null)
        {
            return;
        }

        if (links.Count > MaxVerificationGuideLinks)
        {
            failures.Add($"{nameof(RebuildImpactOptions.VerificationGuideLinks)} supports at most {MaxVerificationGuideLinks} entries (got {links.Count}).");
            return;
        }

        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < links.Count; i++)
        {
            var link = links[i];
            if (link == null)
            {
                failures.Add($"{nameof(RebuildImpactOptions.VerificationGuideLinks)}[{i}] cannot be null.");
                continue;
            }

            ValidateLinkTitle(link.Title, $"{nameof(RebuildImpactOptions.VerificationGuideLinks)}[{i}].{nameof(link.Title)}", required: true, failures);
            ValidateLinkTitle(link.TitleZh, $"{nameof(RebuildImpactOptions.VerificationGuideLinks)}[{i}].{nameof(link.TitleZh)}", required: false, failures);

            if (!VerificationReferenceLink.TryNormalizeHttpUrl(link.Url, out var uri))
            {
                failures.Add($"{nameof(RebuildImpactOptions.VerificationGuideLinks)}[{i}].{nameof(link.Url)} must be an absolute HTTP or HTTPS URL without spaces or control characters (got '{link.Url}').");
            }
            else if (!seenUrls.Add(uri.AbsoluteUri))
            {
                failures.Add($"{nameof(RebuildImpactOptions.VerificationGuideLinks)}[{i}].{nameof(link.Url)} duplicates an existing URL ('{uri.AbsoluteUri}').");
            }
        }
    }

    private static void ValidateLinkTitle(string? title, string path, bool required, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            if (required)
            {
                failures.Add($"{path} is required and cannot be empty.");
            }

            return;
        }

        if (title.Any(char.IsControl))
        {
            failures.Add($"{path} cannot contain newlines or control characters.");
            return;
        }

        if (title.Trim().Length > MaxLinkTitleLength)
        {
            failures.Add($"{path} must be at most {MaxLinkTitleLength} characters.");
        }
    }
}
