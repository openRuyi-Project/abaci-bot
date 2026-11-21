using System.Globalization;
using System.Text;
using abaci_bot.Contexts;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Implements <see cref="IBotControlPlaneService"/> for <c>@&lt;bot&gt; ignore</c>,
/// <c>unignore</c>, <c>status</c>, and <c>lang</c> mention commands.
/// </summary>
public sealed class BotControlPlaneService : IBotControlPlaneService
{
    private readonly IGitHubService _gitHub;
    private readonly IRepositoryPermissionService _permissionService;
    private readonly IBotStateStore _stateStore;
    private readonly ILocaleResolver _localeResolver;
    private readonly PackageUpdateOptions _packageUpdateOptions;
    private readonly ISpecCatalog? _specCatalog;
    private readonly IEphemeralReplyService? _ephemeralReplies;
    private readonly ILogger<BotControlPlaneService>? _logger;

    public BotControlPlaneService(
        IGitHubService gitHub,
        IRepositoryPermissionService permissionService,
        IBotStateStore stateStore,
        ILocaleResolver localeResolver,
        IOptions<PackageUpdateOptions> packageUpdateOptions,
        ISpecCatalog? specCatalog = null,
        ILogger<BotControlPlaneService>? logger = null,
        IEphemeralReplyService? ephemeralReplies = null)
    {
        _gitHub = gitHub;
        _permissionService = permissionService;
        _stateStore = stateStore;
        _localeResolver = localeResolver;
        _packageUpdateOptions = packageUpdateOptions.Value;
        _specCatalog = specCatalog;
        _logger = logger;
        _ephemeralReplies = ephemeralReplies;
    }

    public async Task HandleCommandAsync(
        PullRequestContext context,
        string botLogin,
        ParsedBotCommand command,
        string senderLogin,
        CancellationToken cancellationToken = default)
    {
        switch (command.Verb)
        {
            case "ignore":
                await HandleIgnoreAsync(context, botLogin, command.Arguments, senderLogin, cancellationToken);
                break;
            case "unignore":
                await HandleUnignoreAsync(context, botLogin, command.Arguments, senderLogin, cancellationToken);
                break;
            case "status":
                await HandleStatusAsync(context, botLogin, command.Arguments, senderLogin, cancellationToken);
                break;
            case "lang":
                await HandleLangAsync(context, botLogin, command.Arguments, senderLogin, cancellationToken);
                break;
        }
    }

    private async Task HandleIgnoreAsync(
        PullRequestContext context,
        string botLogin,
        string args,
        string senderLogin,
        CancellationToken cancellationToken)
    {
        var isZh = IsChineseReply(context, senderLogin);
        if (!await HasTriageOrHigherAsync(context.Owner, context.Repo, senderLogin, cancellationToken))
        {
            await PostEphemeralCommentAsync(
                context,
                isZh
                    ? $"@{senderLogin} 仅仓库维护者（triage 及以上权限）可执行包级忽略操作。"
                    : $"@{senderLogin} Only repository maintainers (triage+) may ignore packages.",
                cancellationToken);
            return;
        }

        var (pkg, reason) = SplitFirstTokenAndRest(args);
        if (string.IsNullOrWhiteSpace(pkg) || !UpdateAdmission.IsValidPackageName(pkg))
        {
            await PostEphemeralCommentAsync(
                context,
                isZh
                    ? $"@{senderLogin} 用法：`@{botLogin} ignore <package> [reason]`（包名格式无效或缺失）。"
                    : $"@{senderLogin} Usage: `@{botLogin} ignore <package> [reason]` (missing or invalid package name).",
                cancellationToken);
            return;
        }

        _stateStore.SetIgnoredPackage(context.Owner, context.Repo, pkg, reason, senderLogin);
        _logger?.LogInformation("Package '{Package}' ignored in {Owner}/{Repo} by {Sender} (Reason: {Reason})",
            pkg, context.Owner, context.Repo, senderLogin, reason ?? "none");

        var msg = isZh
            ? $"@{senderLogin} 已将软件包 `{pkg}` 加入自动更新忽略列表{(string.IsNullOrWhiteSpace(reason) ? "" : $"（原因：{reason}）")}。如需恢复请回复 `@{botLogin} unignore {pkg}`。"
            : $"@{senderLogin} Package `{pkg}` is now ignored for automated updates{(string.IsNullOrWhiteSpace(reason) ? "" : $" (reason: {reason})")}. Reply with `@{botLogin} unignore {pkg}` to resume.";

        await _gitHub.CreateCommentAsync(context.Owner, context.Repo, context.PrNumber, msg, cancellationToken);
    }

    private async Task HandleUnignoreAsync(
        PullRequestContext context,
        string botLogin,
        string args,
        string senderLogin,
        CancellationToken cancellationToken)
    {
        var isZh = IsChineseReply(context, senderLogin);
        if (!await HasTriageOrHigherAsync(context.Owner, context.Repo, senderLogin, cancellationToken))
        {
            await PostEphemeralCommentAsync(
                context,
                isZh
                    ? $"@{senderLogin} 仅仓库维护者（triage 及以上权限）可执行取消忽略操作。"
                    : $"@{senderLogin} Only repository maintainers (triage+) may unignore packages.",
                cancellationToken);
            return;
        }

        var (pkg, _) = SplitFirstTokenAndRest(args);
        if (string.IsNullOrWhiteSpace(pkg) || !UpdateAdmission.IsValidPackageName(pkg))
        {
            await PostEphemeralCommentAsync(
                context,
                isZh
                    ? $"@{senderLogin} 用法：`@{botLogin} unignore <package>`。"
                    : $"@{senderLogin} Usage: `@{botLogin} unignore <package>`.",
                cancellationToken);
            return;
        }

        var removed = _stateStore.RemoveIgnoredPackage(context.Owner, context.Repo, pkg);
        var msg = (removed, isZh) switch
        {
            (true, true) => $"@{senderLogin} 已将软件包 `{pkg}` 从自动更新忽略列表中移除。",
            (true, false) => $"@{senderLogin} Package `{pkg}` has been removed from the automated update ignore list.",
            (false, true) => $"@{senderLogin} 软件包 `{pkg}` 当前不在运行时忽略列表中。",
            (false, false) => $"@{senderLogin} Package `{pkg}` is not currently in the runtime ignore list."
        };

        await _gitHub.CreateCommentAsync(context.Owner, context.Repo, context.PrNumber, msg, cancellationToken);
    }

    private async Task HandleStatusAsync(
        PullRequestContext context,
        string botLogin,
        string args,
        string senderLogin,
        CancellationToken cancellationToken)
    {
        var isZh = IsChineseReply(context, senderLogin);
        var (pkg, _) = SplitFirstTokenAndRest(args);

        if (string.IsNullOrWhiteSpace(pkg))
        {
            var pr = await context.PullRequest.Value;
            var headRef = pr?.Head?.Ref;
            var prefix = _packageUpdateOptions.BranchPrefix ?? "autoupdate/";
            if (!string.IsNullOrWhiteSpace(headRef) &&
                headRef.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                pkg = headRef[prefix.Length..].Trim('/');
            }
        }

        if (string.IsNullOrWhiteSpace(pkg))
        {
            var ignored = _stateStore.GetIgnoredPackages(context.Owner, context.Repo);
            var sb = new StringBuilder();
            if (isZh)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"@{senderLogin} 用法：`@{botLogin} status <package>`。");
                sb.Append(ignored.Count == 0
                    ? "当前仓库暂无运行时忽略的软件包。"
                    : $"当前已忽略软件包（{ignored.Count}）：{string.Join(", ", ignored.Select(i => $"`{i.PackageName}`"))}");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"@{senderLogin} Usage: `@{botLogin} status <package>`.");
                sb.Append(ignored.Count == 0
                    ? "No packages are currently ignored at runtime in this repository."
                    : $"Currently ignored packages ({ignored.Count}): {string.Join(", ", ignored.Select(i => $"`{i.PackageName}`"))}");
            }

            await _gitHub.CreateCommentAsync(context.Owner, context.Repo, context.PrNumber, sb.ToString(), cancellationToken);
            return;
        }

        if (!UpdateAdmission.IsValidPackageName(pkg))
        {
            await PostEphemeralCommentAsync(
                context,
                isZh ? $"@{senderLogin} 包名 `{pkg}` 格式无效。" : $"@{senderLogin} Invalid package name `{pkg}`.",
                cancellationToken);
            return;
        }

        var ignoredEntry = _stateStore.GetIgnoredPackage(context.Owner, context.Repo, pkg);
        var failureEntry = _stateStore.GetPackageFailure(context.Owner, context.Repo, pkg);
        string? specNoAutoReason = null;
        if (_specCatalog != null)
        {
            try
            {
                var spec = await _specCatalog.GetSpecAsync(pkg, cancellationToken);
                if (spec != null && NoAutoUpdateMarker.HasNoAutoUpdate(spec.Content, out var reason))
                {
                    specNoAutoReason = string.IsNullOrWhiteSpace(reason) ? "(no reason)" : reason;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "Could not inspect SPEC for status query on {Package}", pkg);
            }
        }

        var report = new StringBuilder();
        if (isZh)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"@{senderLogin} 软件包 `{pkg}` 自动更新状态：");
            report.AppendLine(ignoredEntry != null
                ? FormattableString.Invariant($"- **运行时忽略**：是（由 @{ignoredEntry.IgnoredBy} 于 {ignoredEntry.IgnoredAt:u} 设置{(ignoredEntry.Reason != null ? $"，原因：{ignoredEntry.Reason}" : "")}）")
                : "- **运行时忽略**：否");
            report.AppendLine(specNoAutoReason != null
                ? $"- **SPEC `#!NoAutoUpdate`**：是（{specNoAutoReason}）"
                : "- **SPEC `#!NoAutoUpdate`**：否");
            report.Append(failureEntry != null
                ? FormattableString.Invariant($"- **失败冷却**：连续失败 {failureEntry.ConsecutiveFailures} 次{(failureEntry.CooldownUntil.HasValue ? $"，冷却至 {failureEntry.CooldownUntil.Value:u}" : "")}")
                : "- **失败冷却**：无");
        }
        else
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"@{senderLogin} Auto-update status for `{pkg}`:");
            report.AppendLine(ignoredEntry != null
                ? FormattableString.Invariant($"- **Runtime ignore**: Yes (set by @{ignoredEntry.IgnoredBy} at {ignoredEntry.IgnoredAt:u}{(ignoredEntry.Reason != null ? $", reason: {ignoredEntry.Reason}" : "")})")
                : "- **Runtime ignore**: No");
            report.AppendLine(specNoAutoReason != null
                ? $"- **SPEC `#!NoAutoUpdate`**: Yes ({specNoAutoReason})"
                : "- **SPEC `#!NoAutoUpdate`**: No");
            report.Append(failureEntry != null
                ? FormattableString.Invariant($"- **Failure cooldown**: {failureEntry.ConsecutiveFailures} consecutive failure(s){(failureEntry.CooldownUntil.HasValue ? $", cooldown until {failureEntry.CooldownUntil.Value:u}" : "")}")
                : "- **Failure cooldown**: None");
        }

        await _gitHub.CreateCommentAsync(context.Owner, context.Repo, context.PrNumber, report.ToString(), cancellationToken);
    }

    private async Task HandleLangAsync(
        PullRequestContext context,
        string botLogin,
        string args,
        string senderLogin,
        CancellationToken cancellationToken)
    {
        if (!_localeResolver.IsUserSelectionAllowed(context.Owner, context.Repo, out var forcedLocale))
        {
            var forcedIsZh = string.Equals(forcedLocale, "zh", StringComparison.OrdinalIgnoreCase);
            await PostEphemeralCommentAsync(
                context,
                forcedIsZh
                    ? $"@{senderLogin} 当前仓库已强制配置全局语言为 `{forcedLocale}`，已停用个人语言选择。"
                    : $"@{senderLogin} This repository enforces a global language (`{forcedLocale}`); personal language selection is disabled.",
                cancellationToken);
            return;
        }

        var (arg, _) = SplitFirstTokenAndRest(args);
        var normalizedArg = arg.Trim().ToLowerInvariant();
        if (normalizedArg == "reset")
        {
            _stateStore.ClearUserLocale(senderLogin);
            var effective = _localeResolver.ResolveForReply(context.Owner, context.Repo, senderLogin);
            var isZh = string.Equals(effective, "zh", StringComparison.OrdinalIgnoreCase);
            await _gitHub.CreateCommentAsync(
                context.Owner, context.Repo, context.PrNumber,
                isZh
                    ? $"@{senderLogin} 已清除您的个人语言偏好，现恢复为默认语言（`{effective}`）。"
                    : $"@{senderLogin} Your personal language preference has been cleared; reverting to default (`{effective}`).",
                cancellationToken);
            return;
        }

        if (normalizedArg is not ("en" or "zh"))
        {
            var isZh = IsChineseReply(context, senderLogin);
            await PostEphemeralCommentAsync(
                context,
                isZh
                    ? $"@{senderLogin} 用法：`@{botLogin} lang <en|zh|reset>`。"
                    : $"@{senderLogin} Usage: `@{botLogin} lang <en|zh|reset>`.",
                cancellationToken);
            return;
        }

        _stateStore.SetUserLocale(senderLogin, normalizedArg);
        var confirmMsg = normalizedArg == "zh"
            ? $"@{senderLogin} 已将您的回复语言偏好设置为 `zh`（中文）。"
            : $"@{senderLogin} Your reply language preference has been set to `en` (English).";

        await _gitHub.CreateCommentAsync(context.Owner, context.Repo, context.PrNumber, confirmMsg, cancellationToken);
    }

    private Task PostEphemeralCommentAsync(
        PullRequestContext context,
        string body,
        CancellationToken cancellationToken)
    {
        return _ephemeralReplies != null
            ? _ephemeralReplies.PostEphemeralReplyAsync(context.Owner, context.Repo, context.PrNumber, body, ct: cancellationToken)
            : _gitHub.CreateCommentAsync(context.Owner, context.Repo, context.PrNumber, body, cancellationToken);
    }

    private bool IsChineseReply(PullRequestContext context, string senderLogin)
    {
        var locale = _localeResolver.ResolveForReply(context.Owner, context.Repo, senderLogin);
        return string.Equals(locale, "zh", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> HasTriageOrHigherAsync(string owner, string repo, string username, CancellationToken ct)
    {
        try
        {
            var level = await _permissionService.GetUserPermissionAsync(owner, repo, username, ct);
            return level >= RepositoryAccessLevel.Triage;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to verify permission for '{Username}' on '{Owner}/{Repo}'", username, owner, repo);
            return false;
        }
    }

    private static (string First, string Remainder) SplitFirstTokenAndRest(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return (string.Empty, string.Empty);
        }

        var trimmed = input.Trim();
        var sep = trimmed.IndexOfAny([' ', '\t']);
        return sep < 0
            ? (trimmed, string.Empty)
            : (trimmed[..sep].Trim(), trimmed[(sep + 1)..].Trim());
    }
}
