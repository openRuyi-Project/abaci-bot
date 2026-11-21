namespace abaci_bot.Services;

/// <summary>
/// Resolves effective locale for bot replies and bot-created pull requests,
/// enforcing repository-level forced locales vs per-user preferences.
/// </summary>
public interface ILocaleResolver
{
    /// <summary>
    /// Returns <c>false</c> when the repository has an explicit forced locale configured,
    /// disabling user-level language selection commands.
    /// </summary>
    bool IsUserSelectionAllowed(string owner, string repo, out string? forcedLocale);

    /// <summary>
    /// Resolves the locale for user-facing replies in a repository:
    /// Forced repository locale -&gt; User preference -&gt; Repository default locale -&gt; Global default locale.
    /// </summary>
    string ResolveForReply(string owner, string repo, string? userLogin);

    /// <summary>
    /// Resolves the locale for autonomous bot-created pull requests:
    /// Forced repository locale -&gt; Repository default locale -&gt; Global default locale.
    /// </summary>
    string ResolveForBotPullRequest(string owner, string repo);
}
