using abaci_bot.Options;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Default implementation of <see cref="ILocaleResolver"/>.
/// </summary>
public sealed class LocaleResolver : ILocaleResolver
{
    private readonly BotStateOptions _options;
    private readonly IBotStateStore _stateStore;

    public LocaleResolver(IOptions<BotStateOptions> options, IBotStateStore stateStore)
    {
        _options = options.Value;
        _stateStore = stateStore;
    }

    public bool IsUserSelectionAllowed(string owner, string repo, out string? forcedLocale)
    {
        var key = $"{owner}/{repo}";
        if (_options.RepositoryForcedLocales.TryGetValue(key, out var rawForced) &&
            !string.IsNullOrWhiteSpace(rawForced))
        {
            forcedLocale = NormalizeLocale(rawForced);
            return false;
        }

        forcedLocale = null;
        return true;
    }

    public string ResolveForReply(string owner, string repo, string? userLogin)
    {
        if (!IsUserSelectionAllowed(owner, repo, out var forcedLocale) && forcedLocale != null)
        {
            return forcedLocale;
        }

        if (!string.IsNullOrWhiteSpace(userLogin))
        {
            var userPref = _stateStore.GetUserLocale(userLogin);
            if (!string.IsNullOrWhiteSpace(userPref))
            {
                return NormalizeLocale(userPref);
            }
        }

        var key = $"{owner}/{repo}";
        if (_options.RepositoryDefaultLocales.TryGetValue(key, out var repoDefault) &&
            !string.IsNullOrWhiteSpace(repoDefault))
        {
            return NormalizeLocale(repoDefault);
        }

        return NormalizeLocale(_options.DefaultLocale);
    }

    public string ResolveForBotPullRequest(string owner, string repo)
    {
        if (!IsUserSelectionAllowed(owner, repo, out var forcedLocale) && forcedLocale != null)
        {
            return forcedLocale;
        }

        var key = $"{owner}/{repo}";
        if (_options.RepositoryDefaultLocales.TryGetValue(key, out var repoDefault) &&
            !string.IsNullOrWhiteSpace(repoDefault))
        {
            return NormalizeLocale(repoDefault);
        }

        return NormalizeLocale(_options.DefaultLocale);
    }

    public static string NormalizeLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return "en";
        }

        var trimmed = locale.Trim().ToLowerInvariant();
        if (trimmed is "zh" || trimmed.StartsWith("zh-", StringComparison.Ordinal))
        {
            return "zh";
        }

        return "en";
    }
}
