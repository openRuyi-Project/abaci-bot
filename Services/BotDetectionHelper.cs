namespace abaci_bot.Services;

/// <summary>
/// Centralized bot detection utility that pulls complexity downward,
/// eliminating duplicate bot checks across webhooks, modules, and pipeline contexts.
/// </summary>
public static class BotDetectionHelper
{
    /// <summary>
    /// Evaluates whether a user is a bot account based on account type,
    /// standard GitHub bot username suffixes, or a configured whitelist.
    /// </summary>
    public static bool IsBot(string? senderLogin, string? senderType, IEnumerable<string>? whitelist = null)
    {
        if (string.IsNullOrWhiteSpace(senderLogin))
            return false;

        // 1. Explicit GitHub user account type declared as "Bot"
        if (string.Equals(senderType, "Bot", StringComparison.OrdinalIgnoreCase))
            return true;

        // 2. Standard GitHub App bot naming suffix
        if (senderLogin.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase))
            return true;

        // 3. Configured bot whitelist
        if (whitelist != null && whitelist.Any(b => string.Equals(b, senderLogin, StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }
}
