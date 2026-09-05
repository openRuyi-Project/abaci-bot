using abaci_bot.Contexts;
using abaci_bot.Modules;
using abaci_bot.Pipelines;
using Octokit.Webhooks;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Events.IssueComment;
using Octokit.Webhooks.Events.PullRequest;
using Octokit.Webhooks.Events.PullRequestReview;

namespace abaci_bot.Services;

/// <summary>
/// GitHub Webhook 事件接收与调度分发器。
/// 职责下沉与信息隐藏：只负责接入校验、PR 上下文装配、细粒度单 PR 并发加锁与转交 PullRequestPipeline 处理。
/// </summary>
public class GitHubWebhookProcessor : WebhookEventProcessor
{
    private readonly IGitHubService _github;
    private readonly IConfiguration _config;
    private readonly PullRequestPipeline _pipeline;
    private readonly PullRequestLockManager _lockManager;

    public GitHubWebhookProcessor(
        IGitHubService github,
        IConfiguration config,
        PullRequestPipeline pipeline,
        PullRequestLockManager lockManager)
    {
        _github = github;
        _config = config;
        _pipeline = pipeline;
        _lockManager = lockManager;
    }

    /// <summary>
    /// 便捷构造函数（用于独立测试或无 DI 容器启动时的自装配默认流水线）
    /// </summary>
    public GitHubWebhookProcessor(IGitHubService github, IConfiguration config)
        : this(github, config, CreateDefaultPipeline(github), new PullRequestLockManager())
    {
    }

    public static PullRequestPipeline CreateDefaultPipeline(IGitHubService github)
    {
        var mutexEngine = new LabelMutexEngine();
        var modules = new IPullRequestModule[]
        {
            new WorkflowStateModule(),
            new UserContributionModule(),
            new BuildSystemAnalysisModule(),
            new AiAssistanceModule()
        };
        return new PullRequestPipeline(modules, mutexEngine, github);
    }

    protected override async ValueTask ProcessPullRequestWebhookAsync(
        WebhookHeaders headers,
        PullRequestEvent pullRequestEvent,
        PullRequestAction action,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetRepositoryContext(pullRequestEvent.Repository, out var owner, out var repo))
            return;

        var teamName = _config["GitHubApp:TeamName"] ?? string.Empty;
        var context = PullRequestContext.FromPullRequestEvent(_github, teamName, headers, pullRequestEvent, action);

        if (!_pipeline.Modules.Any(m => m.ShouldProcess(context)))
            return;

        using (await _lockManager.AcquireLockAsync(owner, repo, context.PrNumber, cancellationToken))
        {
            await _pipeline.ExecuteAsync(context, cancellationToken);
        }
    }

    protected override async ValueTask ProcessPullRequestReviewWebhookAsync(
        WebhookHeaders headers,
        PullRequestReviewEvent pullRequestReviewEvent,
        PullRequestReviewAction action,
        CancellationToken cancellationToken = default)
    {
        // 过滤非 Submitted 动作（如 Dismissed, Edited 等），防止状态错乱
        if (action != PullRequestReviewAction.Submitted)
            return;

        if (!TryGetRepositoryContext(pullRequestReviewEvent.Repository, out var owner, out var repo))
            return;

        if (!TryGetSender(pullRequestReviewEvent.Sender, out _))
            return;

        var teamName = _config["GitHubApp:TeamName"] ?? string.Empty;
        var context = PullRequestContext.FromReviewEvent(_github, teamName, headers, pullRequestReviewEvent, action);

        if (!_pipeline.Modules.Any(m => m.ShouldProcess(context)))
            return;

        using (await _lockManager.AcquireLockAsync(owner, repo, context.PrNumber, cancellationToken))
        {
            await _pipeline.ExecuteAsync(context, cancellationToken);
        }
    }

    protected override async ValueTask ProcessIssueCommentWebhookAsync(
        WebhookHeaders headers,
        IssueCommentEvent issueCommentEvent,
        IssueCommentAction action,
        CancellationToken cancellationToken = default)
    {
        // 过滤非 Created 动作（如 Edited, Deleted 等），防止状态错乱
        if (action != IssueCommentAction.Created)
            return;

        if (issueCommentEvent.Issue.PullRequest == null)
            return;

        if (!TryGetRepositoryContext(issueCommentEvent.Repository, out var owner, out var repo))
            return;

        if (!TryGetSender(issueCommentEvent.Sender, out _))
            return;

        var teamName = _config["GitHubApp:TeamName"] ?? string.Empty;
        var context = PullRequestContext.FromIssueCommentEvent(_github, teamName, headers, issueCommentEvent, action);

        if (!_pipeline.Modules.Any(m => m.ShouldProcess(context)))
            return;

        using (await _lockManager.AcquireLockAsync(owner, repo, context.PrNumber, cancellationToken))
        {
            await _pipeline.ExecuteAsync(context, cancellationToken);
        }
    }

    #region 公共领域解析与辅助方法（供各治理模块复用并保证既有测试兼容性）

    public static HashSet<string> GetLabelNames(IEnumerable<Octokit.Webhooks.Models.Label>? labels)
    {
        return labels?
            .Select(label => label.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public static bool TryGetRepositoryContext(
        Octokit.Webhooks.Models.Repository? repository,
        out string owner,
        out string repo)
    {
        owner = repository?.Owner?.Login ?? string.Empty;
        repo = repository?.Name ?? string.Empty;

        return !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo);
    }

    public static bool TryGetSender(Octokit.Webhooks.Models.User? sender, out string login)
    {
        login = sender?.Login?.ToLowerInvariant() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(login);
    }

    public static bool IsBlockedLabel(string? labelName)
    {
        return string.Equals(labelName, GitHubLabels.WorkflowBlocked, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSameUser(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    public static string? ExtractEmailDomain(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        var atIndex = email.LastIndexOf('@');
        if (atIndex < 0 || atIndex == email.Length - 1)
            return null;

        return email[(atIndex + 1)..].Trim();
    }

    public static bool IsInternEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        var normalizedEmail = email.Trim();

        return normalizedEmail.EndsWith(".oerv@isrc.iscas.ac.cn", StringComparison.OrdinalIgnoreCase) ||
               normalizedEmail.EndsWith(".or@isrc.iscas.ac.cn", StringComparison.OrdinalIgnoreCase) ||
               normalizedEmail.EndsWith(".riscv@isrc.iscas.ac.cn", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAiAssistedPullRequest(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;

        const string checkboxText =
            "I have read the [AI-Assisted Contribution Policy], and this Pull Request includes non-trivial AI-assisted content.";

        var lines = body.Split('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (!line.Contains(checkboxText, StringComparison.Ordinal))
                continue;

            return line.StartsWith("- [x]", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    #endregion
}
