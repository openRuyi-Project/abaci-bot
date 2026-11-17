namespace abaci_bot.Options;

/// <summary>
/// Configuration options for bot account detection and whitelisting.
/// </summary>
public class BotDetectionOptions
{
    /// <summary>
    /// Explicit usernames of bots (case-insensitive) whose comments or actions should never trigger human workflow transitions.
    /// </summary>
    public List<string> Whitelist { get; set; } = new()
    {
        "github-actions",
        "github-actions[bot]",
        "abaci-bot",
        "abaci-bot[bot]",
        "dependabot",
        "dependabot[bot]"
    };
}
