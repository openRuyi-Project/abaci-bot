namespace abaci_bot.Options;

/// <summary>
/// Action executed when an ephemeral bot reply exceeds its retention delay.
/// </summary>
public enum EphemeralReplyAction
{
    Delete,
    Minimize
}

/// <summary>
/// Configuration options for unified persistent bot state (ignored packages, failure cooldowns,
/// user language preferences, and repository localization policies).
/// </summary>
public class BotStateOptions
{
    /// <summary>
    /// Optional directory where persistent bot state files (<c>bot-state.json</c> and default
    /// <c>lookout-first-seen.json</c>) are stored. When empty, defaults to
    /// <c>abaci-bot</c> under the local application data directory.
    /// </summary>
    public string StateDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Retention period in days for user language preferences before inactive entries are pruned.
    /// Must be positive. Default: 180.
    /// </summary>
    public int LocaleRetentionDays { get; set; } = 180;

    /// <summary>
    /// Global fallback locale used when neither repository policy nor user preference is set.
    /// Supported values: <c>"en"</c>, <c>"zh"</c>. Default: <c>"en"</c>.
    /// </summary>
    public string DefaultLocale { get; set; } = "en";

    /// <summary>
    /// Per-repository forced locales keyed by <c>"owner/repo"</c> (values: <c>"en"</c> or <c>"zh"</c>).
    /// When configured for a repository, user language selection commands are disabled for that repository.
    /// </summary>
    public Dictionary<string, string> RepositoryForcedLocales { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-repository default locales keyed by <c>"owner/repo"</c> (values: <c>"en"</c> or <c>"zh"</c>).
    /// Used as fallback when a user has not selected a personal language preference.
    /// </summary>
    public Dictionary<string, string> RepositoryDefaultLocales { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Delay in minutes before ephemeral bot usage and rejection replies are processed.
    /// Range: 0..1440. Setting 0 disables automatic cleanup. Default: 5.
    /// </summary>
    public int EphemeralReplyDeleteMinutes { get; set; } = 5;

    /// <summary>
    /// Action taken when an ephemeral bot reply expires.
    /// Allowed values: <see cref="EphemeralReplyAction.Delete"/> (deletes the comment) or
    /// <see cref="EphemeralReplyAction.Minimize"/> (minimizes as <c>OUTDATED</c>).
    /// Default: <see cref="EphemeralReplyAction.Delete"/>.
    /// </summary>
    public EphemeralReplyAction EphemeralReplyAction { get; set; } = EphemeralReplyAction.Delete;
}
