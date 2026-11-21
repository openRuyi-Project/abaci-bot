namespace abaci_bot.Services;

/// <summary>
/// Resolves the live GitHub login/handle assigned to this bot from the GitHub API.
/// </summary>
public interface IBotIdentityProvider
{
    /// <summary>
    /// Returns the GitHub mention handle for the bot (authenticated PAT user login or GitHub App slug).
    /// </summary>
    Task<string> GetBotLoginAsync(CancellationToken cancellationToken = default);
}
