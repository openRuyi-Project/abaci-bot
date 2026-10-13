namespace abaci_bot.Services;

/// <summary>
/// Helper predicates for extracting email domains and detecting student intern email patterns.
/// Follows the static helper pattern established by <see cref="BotDetectionHelper"/>.
/// </summary>
public static class EmailDomainHelper
{
    /// <summary>
    /// Extracts the domain portion of an email address. Returns null if invalid or missing.
    /// </summary>
    public static string? ExtractEmailDomain(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        var atIndex = email.LastIndexOf('@');
        if (atIndex < 0 || atIndex == email.Length - 1)
            return null;

        return email[(atIndex + 1)..].Trim();
    }

    /// <summary>
    /// Determines whether the email address belongs to recognized student intern subdomains.
    /// </summary>
    public static bool IsInternEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        var normalizedEmail = email.Trim();

        return normalizedEmail.EndsWith(".oerv@isrc.iscas.ac.cn", StringComparison.OrdinalIgnoreCase) ||
               normalizedEmail.EndsWith(".or@isrc.iscas.ac.cn", StringComparison.OrdinalIgnoreCase) ||
               normalizedEmail.EndsWith(".riscv@isrc.iscas.ac.cn", StringComparison.OrdinalIgnoreCase);
    }
}
