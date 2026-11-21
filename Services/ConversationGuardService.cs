using System.Text.RegularExpressions;
using abaci_bot.Contexts;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;

namespace abaci_bot.Services;

/// <summary>
/// Service implementing conversation guard functionality: mention commands (<c>@&lt;bot&gt; lock</c>, <c>unlock</c>, <c>hide</c>, <c>archive</c>)
/// authorized against authoritative GitHub repository permissions, and automated locking of inactive closed PRs.
/// </summary>
public partial class ConversationGuardService : IConversationGuardService
{
    private readonly IGitHubService _gitHub;
    private readonly IRepositoryPermissionService _permissionService;
    private readonly IBotIdentityProvider _botIdentity;
    private readonly ILocaleResolver? _localeResolver;
    private readonly IEphemeralReplyService? _ephemeralReplies;
    private readonly ConversationGuardOptions _options;
    private readonly ILogger<ConversationGuardService>? _logger;
    private readonly PendingConfirmationStore _confirmationStore;

    [GeneratedRegex(@"^@?(?<user>[a-zA-Z0-9_\-]+(?:\[bot\])?)(?:\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex HideTargetUserRegex();

    public ConversationGuardService(
        IGitHubService gitHub,
        IRepositoryPermissionService permissionService,
        IOptions<ConversationGuardOptions> options,
        ILogger<ConversationGuardService>? logger = null,
        TimeProvider? timeProvider = null,
        PendingConfirmationStore? confirmationStore = null,
        IBotIdentityProvider? botIdentity = null,
        ILocaleResolver? localeResolver = null,
        IEphemeralReplyService? ephemeralReplies = null)
    {
        _gitHub = gitHub;
        _permissionService = permissionService;
        _botIdentity = botIdentity ?? gitHub as IBotIdentityProvider
            ?? throw new ArgumentNullException(nameof(botIdentity), "IBotIdentityProvider must be provided when IGitHubService does not implement it.");
        _localeResolver = localeResolver;
        _ephemeralReplies = ephemeralReplies;
        _options = options.Value;
        _logger = logger;
        _confirmationStore = confirmationStore ?? new PendingConfirmationStore(timeProvider);
    }

    public async Task HandleCommandAsync(
        PullRequestContext context,
        string commentBody,
        string senderLogin,
        CancellationToken cancellationToken = default)
    {
        var botLogin = await _botIdentity.GetBotLoginAsync(cancellationToken);
        if (!BotCommandParser.TryParse(commentBody, botLogin, out var parsed))
        {
            return;
        }

        var isZh = IsChineseLocale(context, senderLogin);

        switch (parsed.Verb)
        {
            case "lock":
            {
                var isMaintainer = await HasTriageOrHigherPermissionAsync(context.Owner, context.Repo, senderLogin, cancellationToken);
                var isAuthor = string.Equals(senderLogin, context.AuthorLogin, StringComparison.OrdinalIgnoreCase);
                await HandleLockCommandAsync(context, botLogin, parsed.Arguments, senderLogin, isMaintainer, isAuthor, isZh, cancellationToken);
                break;
            }
            case "unlock":
            {
                var isMaintainer = await HasTriageOrHigherPermissionAsync(context.Owner, context.Repo, senderLogin, cancellationToken);
                await HandleUnlockCommandAsync(context, senderLogin, isMaintainer, isZh, cancellationToken);
                break;
            }
            case "hide":
            {
                var isMaintainer = await HasTriageOrHigherPermissionAsync(context.Owner, context.Repo, senderLogin, cancellationToken);
                var targetMatch = HideTargetUserRegex().Match(parsed.Arguments.Trim());
                var targetUser = targetMatch.Success ? targetMatch.Groups["user"].Value : string.Empty;
                await HandleHideCommandAsync(context, botLogin, targetUser, senderLogin, isMaintainer, isZh, cancellationToken);
                break;
            }
            case "archive":
            {
                var isMaintainer = await HasTriageOrHigherPermissionAsync(context.Owner, context.Repo, senderLogin, cancellationToken);
                var isAuthor = string.Equals(senderLogin, context.AuthorLogin, StringComparison.OrdinalIgnoreCase);
                await HandleArchiveCommandAsync(context, botLogin, parsed.Arguments, senderLogin, isMaintainer, isAuthor, isZh, cancellationToken);
                break;
            }
        }
    }

    private bool IsChineseLocale(PullRequestContext context, string senderLogin)
    {
        var locale = _localeResolver?.ResolveForReply(context.Owner, context.Repo, senderLogin);
        return string.Equals(locale, "zh", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> HasTriageOrHigherPermissionAsync(
        string owner,
        string repo,
        string username,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        try
        {
            var level = await _permissionService.GetUserPermissionAsync(owner, repo, username, cancellationToken);
            return level >= RepositoryAccessLevel.Triage;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(
                ex,
                "Failed to query repository collaborator permission for '{Username}' on '{Owner}/{Repo}'; denying privileged command.",
                username,
                owner,
                repo);
            return false;
        }
    }

    private async Task PostCommentOrLogDryRunAsync(
        PullRequestContext context,
        string commentBody,
        string dryRunAction,
        CancellationToken cancellationToken,
        TimeSpan? ephemeralTtl = null)
    {
        if (_options.DryRun)
        {
            _logger?.LogInformation(
                "[DryRun] {Action} on {Owner}/{Repo}#{PrNumber} (GitHub comment suppressed).",
                dryRunAction, context.Owner, context.Repo, context.PrNumber);
            return;
        }

        if (_ephemeralReplies != null)
        {
            await _ephemeralReplies.PostEphemeralReplyAsync(
                context.Owner, context.Repo, context.PrNumber, commentBody, ephemeralTtl, cancellationToken);
        }
        else
        {
            await _gitHub.CreateCommentAsync(context.Owner, context.Repo, context.PrNumber, commentBody, cancellationToken);
        }
    }

    private async Task<bool> ValidateOrRequestAuthorConfirmationAsync(
        PullRequestContext context,
        string botLogin,
        string commandName,
        string arg,
        string senderLogin,
        bool isZh,
        CancellationToken cancellationToken)
    {
        var key = new ConfirmationKey(context.Owner, context.Repo, context.PrNumber, commandName, senderLogin);
        var normalizedArg = arg.Trim().TrimEnd(':', ',', '.', '!');
        var isConfirmation = string.Equals(normalizedArg, "confirm", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(normalizedArg, "yes", StringComparison.OrdinalIgnoreCase);
        var timeout = TimeSpan.FromMinutes(_options.AuthorLockConfirmTimeoutMinutes);

        if (!isConfirmation)
        {
            _confirmationStore.Register(key, timeout);
            _logger?.LogInformation("Author {Sender} requested @{BotLogin} {Command} on {Owner}/{Repo}#{PrNumber}; awaiting confirmation within {Minutes}m.",
                senderLogin, botLogin, commandName, context.Owner, context.Repo, context.PrNumber, _options.AuthorLockConfirmTimeoutMinutes);

            var promptMsg = isZh
                ? $"@{senderLogin} 作为 PR 作者执行 `{commandName}`，请在 {_options.AuthorLockConfirmTimeoutMinutes} 分钟内回复 `@{botLogin} {commandName} confirm` 以确认。"
                : $"@{senderLogin} To confirm `{commandName}` on this pull request as the author, please reply with `@{botLogin} {commandName} confirm` within {_options.AuthorLockConfirmTimeoutMinutes} minute(s).";

            await PostCommentOrLogDryRunAsync(
                context,
                promptMsg,
                $"Would prompt author @{senderLogin} to confirm {commandName}",
                cancellationToken,
                ephemeralTtl: timeout);
            return false;
        }

        if (_confirmationStore.TryConsume(key))
        {
            return true;
        }

        _confirmationStore.Register(key, timeout);
        _logger?.LogInformation("Author {Sender} sent @{BotLogin} {Command} confirm on {Owner}/{Repo}#{PrNumber} without an active non-expired request; prompting re-confirmation.",
            senderLogin, botLogin, commandName, context.Owner, context.Repo, context.PrNumber);

        var rePromptMsg = isZh
            ? $"@{senderLogin} 未找到有效且未过期的 `{commandName}` 确认请求。请在 {_options.AuthorLockConfirmTimeoutMinutes} 分钟内回复 `@{botLogin} {commandName} confirm` 继续。"
            : $"@{senderLogin} No active `{commandName}` confirmation request was found or the previous request expired. Please reply with `@{botLogin} {commandName} confirm` within {_options.AuthorLockConfirmTimeoutMinutes} minute(s) to proceed.";

        await PostCommentOrLogDryRunAsync(
            context,
            rePromptMsg,
            $"Would prompt author @{senderLogin} to re-confirm {commandName}",
            cancellationToken,
            ephemeralTtl: timeout);
        return false;
    }

    private async Task HandleLockCommandAsync(
        PullRequestContext context,
        string botLogin,
        string arg,
        string senderLogin,
        bool isMaintainer,
        bool isAuthor,
        bool isZh,
        CancellationToken cancellationToken)
    {
        if (!isMaintainer && !isAuthor)
        {
            _logger?.LogWarning("Unauthorized lock attempt by {Sender} on {Owner}/{Repo}#{PrNumber}",
                senderLogin, context.Owner, context.Repo, context.PrNumber);
            await PostCommentOrLogDryRunAsync(
                context,
                isZh
                    ? $"@{senderLogin} 仅 PR 作者或仓库维护者（triage 及以上权限）可锁定此对话。"
                    : $"@{senderLogin} Only the pull request author or repository maintainers (triage+) may lock this conversation.",
                $"Would reject unauthorized lock attempt by @{senderLogin}",
                cancellationToken);
            return;
        }

        if (isAuthor && !isMaintainer)
        {
            var confirmed = await ValidateOrRequestAuthorConfirmationAsync(context, botLogin, "lock", arg, senderLogin, isZh, cancellationToken);
            if (!confirmed)
            {
                return;
            }
        }

        if (_options.DryRun)
        {
            _logger?.LogInformation("[DryRun] Would lock conversation for {Owner}/{Repo}#{PrNumber} by {Sender}",
                context.Owner, context.Repo, context.PrNumber, senderLogin);
            return;
        }

        if (await _gitHub.IsIssueLockedAsync(context.Owner, context.Repo, context.PrNumber, cancellationToken))
        {
            _logger?.LogInformation("Conversation for {Owner}/{Repo}#{PrNumber} is already locked; skipping lock.",
                context.Owner, context.Repo, context.PrNumber);
            return;
        }

        _logger?.LogInformation("Locking conversation for {Owner}/{Repo}#{PrNumber} requested by {Sender} (Maintainer: {IsMaintainer})",
            context.Owner, context.Repo, context.PrNumber, senderLogin, isMaintainer);

        // Post notice before locking, because GitHub rejects App comments on locked issues with 403.
        try
        {
            await _gitHub.CreateCommentAsync(
                context.Owner,
                context.Repo,
                context.PrNumber,
                isZh
                    ? $"此对话已由 @{senderLogin} 锁定。"
                    : $"This conversation has been locked by @{senderLogin}.",
                cancellationToken);
        }
        catch (ForbiddenException ex)
        {
            _logger?.LogWarning(
                ex,
                "Cannot post lock announcement on {Owner}/{Repo}#{PrNumber}; conversation may have been locked concurrently.",
                context.Owner,
                context.Repo,
                context.PrNumber);
            return;
        }

        try
        {
            await _gitHub.LockIssueAsync(context.Owner, context.Repo, context.PrNumber, LockReason.TooHeated, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to lock conversation for {Owner}/{Repo}#{PrNumber}; posting compensation comment.",
                context.Owner, context.Repo, context.PrNumber);
            await TryPostCompensationCommentAsync(
                context,
                isZh
                    ? $"@{senderLogin} 因 GitHub API 错误锁定失败，此对话仍保持未锁定状态。"
                    : $"@{senderLogin} Failed to lock this conversation due to a GitHub API error. The conversation remains unlocked.",
                cancellationToken);
        }
    }

    private async Task HandleUnlockCommandAsync(
        PullRequestContext context,
        string senderLogin,
        bool isMaintainer,
        bool isZh,
        CancellationToken cancellationToken)
    {
        if (!isMaintainer)
        {
            _logger?.LogWarning("Unauthorized unlock attempt by {Sender} on {Owner}/{Repo}#{PrNumber}",
                senderLogin, context.Owner, context.Repo, context.PrNumber);
            await PostCommentOrLogDryRunAsync(
                context,
                isZh
                    ? $"@{senderLogin} 仅仓库维护者（triage 及以上权限）可解锁此对话。"
                    : $"@{senderLogin} Only repository maintainers (triage+) may unlock this conversation.",
                $"Would reject unauthorized unlock attempt by @{senderLogin}",
                cancellationToken);
            return;
        }

        if (_options.DryRun)
        {
            _logger?.LogInformation("[DryRun] Would unlock conversation for {Owner}/{Repo}#{PrNumber} by {Sender}",
                context.Owner, context.Repo, context.PrNumber, senderLogin);
            return;
        }

        var isLocked = await _gitHub.IsIssueLockedAsync(context.Owner, context.Repo, context.PrNumber, cancellationToken);
        if (!isLocked)
        {
            _logger?.LogInformation("Conversation for {Owner}/{Repo}#{PrNumber} is not locked; skipping unlock.",
                context.Owner, context.Repo, context.PrNumber);
            await _gitHub.CreateCommentAsync(
                context.Owner,
                context.Repo,
                context.PrNumber,
                isZh
                    ? $"@{senderLogin} 此对话当前未处于锁定状态。"
                    : $"@{senderLogin} This conversation is not currently locked.",
                cancellationToken);
            return;
        }

        _logger?.LogInformation("Unlocking conversation for {Owner}/{Repo}#{PrNumber} requested by maintainer {Sender}",
            context.Owner, context.Repo, context.PrNumber, senderLogin);

        try
        {
            await _gitHub.UnarchivePullRequestAsync(context.Owner, context.Repo, context.PrNumber, null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Unarchive attempt ignored or not applicable for {Owner}/{Repo}#{PrNumber}",
                context.Owner, context.Repo, context.PrNumber);
        }

        await _gitHub.UnlockIssueAsync(context.Owner, context.Repo, context.PrNumber, cancellationToken);
        await _gitHub.CreateCommentAsync(
            context.Owner,
            context.Repo,
            context.PrNumber,
            isZh
                ? $"此对话已由 @{senderLogin} 解锁。"
                : $"This conversation has been unlocked by @{senderLogin}.",
            cancellationToken);
    }

    private async Task HandleHideCommandAsync(
        PullRequestContext context,
        string botLogin,
        string targetUser,
        string senderLogin,
        bool isMaintainer,
        bool isZh,
        CancellationToken cancellationToken)
    {
        if (!isMaintainer)
        {
            _logger?.LogWarning("Unauthorized hide attempt by {Sender} on {Owner}/{Repo}#{PrNumber}",
                senderLogin, context.Owner, context.Repo, context.PrNumber);
            await PostCommentOrLogDryRunAsync(
                context,
                isZh
                    ? $"@{senderLogin} 仅仓库维护者（triage 及以上权限）可折叠评论。"
                    : $"@{senderLogin} Only repository maintainers (triage+) may hide comments.",
                $"Would reject unauthorized hide attempt by @{senderLogin}",
                cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(targetUser))
        {
            await PostCommentOrLogDryRunAsync(
                context,
                isZh
                    ? $"@{senderLogin} 用法：`@{botLogin} hide @username` 以批量折叠指定用户的评论。"
                    : $"@{senderLogin} Usage: `@{botLogin} hide @username` to fold spam comments from a user.",
                $"Would post hide usage hint for @{senderLogin}",
                cancellationToken);
            return;
        }

        if (await HasTriageOrHigherPermissionAsync(context.Owner, context.Repo, targetUser, cancellationToken))
        {
            await PostCommentOrLogDryRunAsync(
                context,
                isZh
                    ? $"@{senderLogin} 不能折叠仓库维护者的评论。"
                    : $"@{senderLogin} Comments authored by repository maintainers cannot be hidden.",
                $"Would reject hide targeting maintainer @{targetUser} by @{senderLogin}",
                cancellationToken);
            return;
        }

        if (_options.DryRun)
        {
            _logger?.LogInformation("[DryRun] Would hide comments authored by {TargetUser} on {Owner}/{Repo}#{PrNumber}",
                targetUser, context.Owner, context.Repo, context.PrNumber);
            return;
        }

        var comments = await _gitHub.GetIssueCommentsAsync(context.Owner, context.Repo, context.PrNumber, cancellationToken);
        var targetComments = comments
            .Where(c => string.Equals(c.User?.Login, targetUser, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (targetComments.Count == 0)
        {
            _logger?.LogInformation("No comments authored by {TargetUser} found on {Owner}/{Repo}#{PrNumber}.",
                targetUser, context.Owner, context.Repo, context.PrNumber);
            await _gitHub.CreateCommentAsync(
                context.Owner,
                context.Repo,
                context.PrNumber,
                isZh
                    ? $"@{senderLogin} 本 PR 下未找到 @{targetUser} 发表的评论。"
                    : $"@{senderLogin} No comments authored by @{targetUser} were found on this pull request.",
                cancellationToken);
            return;
        }

        var hiddenCount = 0;
        var failedCount = 0;
        foreach (var c in targetComments)
        {
            try
            {
                if (await _gitHub.MinimizeCommentAsync(context.Owner, context.Repo, c.Id, c.NodeId, "SPAM", cancellationToken))
                {
                    hiddenCount++;
                }
                else
                {
                    failedCount++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedCount++;
                _logger?.LogWarning(ex, "Failed to hide comment {CommentId} by {TargetUser}", c.Id, targetUser);
            }
        }

        _logger?.LogInformation("Minimized {Count} comments ({FailedCount} failed) by {TargetUser} on {Owner}/{Repo}#{PrNumber}",
            hiddenCount, failedCount, targetUser, context.Owner, context.Repo, context.PrNumber);

        var summaryMessage = (hiddenCount, failedCount, isZh) switch
        {
            (0, > 0, true) => $"@{senderLogin} 找到 {targetComments.Count} 条来自 @{targetUser} 的评论，但因 GitHub API/GraphQL 错误未能折叠任何评论。",
            (0, > 0, false) => $"@{senderLogin} Found {targetComments.Count} comment(s) authored by @{targetUser}, but failed to minimize all of them due to GitHub API/GraphQL errors.",
            (> 0, > 0, true) => $"@{senderLogin} 已折叠 @{targetUser} 的 {hiddenCount}/{targetComments.Count} 条评论（{failedCount} 条失败）。",
            (> 0, > 0, false) => $"{hiddenCount} of {targetComments.Count} comment(s) authored by @{targetUser} have been minimized by @{senderLogin} ({failedCount} failed).",
            (_, _, true) => $"@{senderLogin} 已折叠 @{targetUser} 的 {hiddenCount} 条评论。",
            _ => $"{hiddenCount} comment(s) authored by @{targetUser} have been minimized by @{senderLogin}."
        };

        await _gitHub.CreateCommentAsync(
            context.Owner,
            context.Repo,
            context.PrNumber,
            summaryMessage,
            cancellationToken);
    }

    private async Task HandleArchiveCommandAsync(
        PullRequestContext context,
        string botLogin,
        string arg,
        string senderLogin,
        bool isMaintainer,
        bool isAuthor,
        bool isZh,
        CancellationToken cancellationToken)
    {
        if (!isMaintainer && !isAuthor)
        {
            _logger?.LogWarning("Unauthorized archive attempt by {Sender} on {Owner}/{Repo}#{PrNumber}",
                senderLogin, context.Owner, context.Repo, context.PrNumber);
            await PostCommentOrLogDryRunAsync(
                context,
                isZh
                    ? $"@{senderLogin} 仅 PR 作者或仓库维护者（triage 及以上权限）可归档 Pull Request。"
                    : $"@{senderLogin} Only the pull request author or repository maintainers (triage+) may archive pull requests.",
                $"Would reject unauthorized archive attempt by @{senderLogin}",
                cancellationToken);
            return;
        }

        if (!_options.ArchiveEnabled)
        {
            _logger?.LogInformation("Archive attempted by {Sender} but ArchiveEnabled is false.", senderLogin);
            await PostCommentOrLogDryRunAsync(
                context,
                isZh
                    ? $"@{senderLogin} 当前机器人配置未启用 Pull Request 归档功能。"
                    : $"@{senderLogin} Pull request archiving is currently disabled in the bot configuration.",
                $"Would report archive disabled to @{senderLogin}",
                cancellationToken);
            return;
        }

        if (isAuthor && !isMaintainer)
        {
            var confirmed = await ValidateOrRequestAuthorConfirmationAsync(context, botLogin, "archive", arg, senderLogin, isZh, cancellationToken);
            if (!confirmed)
            {
                return;
            }
        }

        if (_options.DryRun)
        {
            _logger?.LogInformation("[DryRun] Would archive pull request {Owner}/{Repo}#{PrNumber} by {Sender}",
                context.Owner, context.Repo, context.PrNumber, senderLogin);
            return;
        }

        _logger?.LogInformation("Archiving pull request {Owner}/{Repo}#{PrNumber} requested by {Sender}",
            context.Owner, context.Repo, context.PrNumber, senderLogin);

        var pr = await context.PullRequest.Value;

        // Post exit comment before calling archivePullRequest, since post-archive comment API returns 403.
        await _gitHub.CreateCommentAsync(
            context.Owner,
            context.Repo,
            context.PrNumber,
            isZh
                ? $"此 Pull Request 已由 @{senderLogin} 归档，对话现已永久关闭。"
                : $"This pull request has been archived by @{senderLogin}. Conversation is now permanently closed.",
            cancellationToken);

        try
        {
            await _gitHub.ArchivePullRequestAsync(context.Owner, context.Repo, context.PrNumber, pr?.NodeId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var isPermissionDenied = ex is ForbiddenException ||
                                     (ex is InvalidOperationException &&
                                      ex.Message.Contains("FORBIDDEN", StringComparison.OrdinalIgnoreCase));
            var failureKind = isPermissionDenied
                ? "permission denied (FORBIDDEN)"
                : (ex is InvalidOperationException ? "GraphQL error" : "GitHub API/network error");
            _logger?.LogWarning(ex, "Failed to archive pull request {Owner}/{Repo}#{PrNumber} ({FailureKind}); posting compensation comment.",
                context.Owner, context.Repo, context.PrNumber, failureKind);

            var compensationComment = (isPermissionDenied, isZh) switch
            {
                (true, true) => $"@{senderLogin} 归档失败：机器人缺少 GitHub 归档权限（需 GitHub App 开启 'Administration: Read & write' 或配置具备 triage+ 权限的 PAT）。请通过 GitHub 网页端手动归档。",
                (true, false) => $"@{senderLogin} Failed to archive this pull request because the bot lacks GitHub archive permission (requires GitHub App 'Administration: Read & write' or a triage+ PAT). Please archive it via the GitHub Web UI.",
                (false, true) => $"@{senderLogin} 因 GitHub API 错误归档失败，此对话仍保持未归档状态。",
                (false, false) => $"@{senderLogin} Failed to archive this pull request due to a GitHub API error. The conversation remains unarchived."
            };

            await TryPostCompensationCommentAsync(
                context,
                compensationComment,
                cancellationToken);
        }
    }

    private async Task TryPostCompensationCommentAsync(
        PullRequestContext context,
        string commentBody,
        CancellationToken cancellationToken)
    {
        try
        {
            await _gitHub.CreateCommentAsync(
                context.Owner,
                context.Repo,
                context.PrNumber,
                commentBody,
                cancellationToken);
        }
        catch (Exception compEx) when (compEx is not OperationCanceledException)
        {
            _logger?.LogWarning(
                compEx,
                "Failed to post compensation comment for {Owner}/{Repo}#{PrNumber}; conversation state may have changed concurrently.",
                context.Owner,
                context.Repo,
                context.PrNumber);
        }
    }

    public async Task LockInactivePullRequestsAsync(
        string owner,
        string repo,
        int inactiveDays,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var closedBefore = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, inactiveDays));
        var searchResult = await _gitHub.SearchClosedPullRequestsAsync(owner, repo, closedBefore, cancellationToken);

        if (searchResult.IsTruncated)
        {
            _logger?.LogWarning(
                "Inactive PR lock sweep truncated in {Owner}/{Repo}: retrieved {Retrieved} of {Total} closed pull requests; remaining items deferred to next sweep.",
                owner, repo, searchResult.Items.Count, searchResult.TotalCount);
        }

        _logger?.LogInformation(
            "Found {Count} closed pull requests inactive for >{Days} days in {Owner}/{Repo}",
            searchResult.Items.Count, inactiveDays, owner, repo);

        var maxLocks = _options.MaxLocksPerSweep;
        var processedUnlockedCandidates = 0;

        foreach (var candidate in searchResult.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (candidate.Locked)
            {
                continue;
            }

            if (processedUnlockedCandidates >= maxLocks)
            {
                _logger?.LogInformation(
                    "Reached MaxLocksPerSweep business cap ({Cap}) in {Owner}/{Repo}; deferring remaining eligible pull requests to next sweep.",
                    maxLocks, owner, repo);
                break;
            }

            processedUnlockedCandidates++;

            if (dryRun)
            {
                _logger?.LogInformation(
                    "[DryRun] Would lock inactive pull request {Owner}/{Repo}#{PrNumber} (closed: {ClosedAt:u})",
                    owner, repo, candidate.Number, candidate.ClosedAt);
                continue;
            }

            try
            {
                await _gitHub.LockIssueAsync(owner, repo, candidate.Number, LockReason.TooHeated, cancellationToken);
                _logger?.LogInformation(
                    "Locked inactive pull request {Owner}/{Repo}#{PrNumber} (closed: {ClosedAt:u})",
                    owner, repo, candidate.Number, candidate.ClosedAt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Failed to lock inactive pull request {Owner}/{Repo}#{PrNumber}",
                    owner, repo, candidate.Number);
            }
        }
    }
}
