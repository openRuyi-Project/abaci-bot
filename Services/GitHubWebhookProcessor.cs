using Octokit.Webhooks;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Events.PullRequest;
using Octokit.Webhooks.Events.PullRequestReview;
using Octokit.Webhooks.Events.IssueComment;

namespace abaci_bot.Services;

public class GitHubWebhookProcessor : WebhookEventProcessor
{
    private readonly IGitHubService _github;
    private readonly IConfiguration _config;

    public GitHubWebhookProcessor(IGitHubService github, IConfiguration config)
    {
        _github = github;
        _config = config;
    }

    protected override async ValueTask ProcessPullRequestWebhookAsync(
        WebhookHeaders headers,
        PullRequestEvent pullRequestEvent,
        PullRequestAction action,
        CancellationToken cancellationToken = default)
    {
        var prNumber = (int)pullRequestEvent.PullRequest.Number;
        if (!TryGetRepositoryContext(pullRequestEvent.Repository, out var owner, out var repo))
            return;

        var headSha = pullRequestEvent.PullRequest.Head.Sha;
        var isDraft = pullRequestEvent.PullRequest.Draft;
        var currentLabels = GetLabelNames(pullRequestEvent.PullRequest.Labels);
        var isBlocked = currentLabels.Contains(GitHubLabels.WorkflowBlocked);
        string prTitle = pullRequestEvent.PullRequest.Title;

        // Handle PR when opened or synchronized (new commit)
        if (action == PullRequestAction.Opened || 
            action == PullRequestAction.Synchronize || 
            action == PullRequestAction.Reopened ||
            action == PullRequestAction.ConvertedToDraft || 
            action == PullRequestAction.ReadyForReview)
        {
            // When PR is synchronized (new commits) and has Workflow: Blocked label, add Commits: Updated
            if (action == PullRequestAction.Synchronize && isBlocked)
            {
                await _github.AddLabelsAsync(owner, repo, prNumber, GitHubLabels.CommitsUpdated);
            }
            // Keep "AI Assistance" label in sync with the PR description.
            await AnalyzeDescriptionAndLabelPR(owner, repo, prNumber, pullRequestEvent.PullRequest.Body);

            // Get user email from PR commits (via base repo API, works for both public and private forks)
            string? userEmail = null;
            try
            {
                userEmail = await _github.GetPullRequestAuthorEmailAsync(owner, repo, prNumber);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to get commit author email for PR #{prNumber}: {ex.Message}");
            }
            
            // First, let's check if the user is using an educational email domain.
            await AnalyzeUserAndLabelPR(owner, repo, prNumber, userEmail);
            // Then, let's analyze the content of the PR and add labels accordingly.
            await AnalyzeFilesAndLabelPR(owner, repo, prNumber, headSha);

            if (isBlocked)
            {
                return;
            }

            if (isDraft || prTitle.StartsWith("WIP", StringComparison.OrdinalIgnoreCase))
            {
                // For draft PR, add "Workflow: In Dev" label to indicate it's still in development,
                // and remove "Workflow: Ready For Review" label if exists.
                await _github.AddLabelsAsync(owner, repo, prNumber, GitHubLabels.WorkflowInDev);
                await _github.RemoveLabelAsync(owner, repo, prNumber, GitHubLabels.WorkflowReadyForReview);
            }
            else
            {
                // For non-draft PR, add "Workflow: Ready For Review" label and remove "Workflow: In Dev" label if exists.
                await _github.RemoveLabelAsync(owner, repo, prNumber, GitHubLabels.WorkflowInDev);
                if (!currentLabels.Contains(GitHubLabels.WorkflowInReview))
                {
                    await _github.AddLabelsAsync(owner, repo, prNumber, GitHubLabels.WorkflowReadyForReview);
                }
                
            }
        }
        // When PR description is edited, remove/cleanup labels
        else if (action == PullRequestAction.Edited)
        {
            await AnalyzeDescriptionAndLabelPR(owner, repo, prNumber, pullRequestEvent.PullRequest.Body);
        }
        // When PR is closed, remove/cleanup labels
        else if (action == PullRequestAction.Closed)
        {
            if (pullRequestEvent.PullRequest.Merged == true)
            {
                // For merged PR, add "Workflow: Complete" label.
                await _github.AddLabelsAsync(owner, repo, prNumber, GitHubLabels.WorkflowComplete);
            }
            await _github.RemoveLabelAsync(owner, repo, prNumber, GitHubLabels.WorkflowInDev);
            await _github.RemoveLabelAsync(owner, repo, prNumber, GitHubLabels.WorkflowReadyForReview);
            await _github.RemoveLabelAsync(owner, repo, prNumber, GitHubLabels.WorkflowInReview);
        }
        // Handle PR labeled event for Workflow: Blocked management
        else if (action == PullRequestAction.Labeled)
        {
            if (pullRequestEvent is PullRequestLabeledEvent labeledEvent &&
                IsBlockedLabel(labeledEvent.Label.Name))
            {
                await HandleBlockedLabelAdded(owner, repo, prNumber);
            }
        }
        // Handle PR unlabeled event for Workflow: Blocked management
        else if (action == PullRequestAction.Unlabeled)
        {
            if (pullRequestEvent is PullRequestUnlabeledEvent unlabeledEvent &&
                IsBlockedLabel(unlabeledEvent.Label.Name))
            {
                await HandleBlockedLabelRemoved(owner, repo, prNumber);
            }
        }
    }

    protected override async ValueTask ProcessPullRequestReviewWebhookAsync(
    WebhookHeaders headers,
    PullRequestReviewEvent pullRequestReviewEvent,
    PullRequestReviewAction action,
    CancellationToken cancellationToken = default)
    {
        if (action == PullRequestReviewAction.Submitted)
        {
            var prNumber = (int)pullRequestReviewEvent.PullRequest.Number;
            if (!TryGetRepositoryContext(pullRequestReviewEvent.Repository, out var owner, out var repo))
                return;

            if (!TryGetSender(pullRequestReviewEvent.Sender, out var sender))
                return;

            var captains = await _github.GetTeamMembersAsync(owner, _config["GitHubApp:TeamName"]!);
            string? author = pullRequestReviewEvent.PullRequest.User?.Login;
            var currentLabels = GetLabelNames(pullRequestReviewEvent.PullRequest.Labels);
            var isBlocked = currentLabels.Contains(GitHubLabels.WorkflowBlocked);

            // If the review is submitted by a captain, add "Workflow: In Review" label.
            if (captains.Contains(sender) && !isBlocked)
            {
                await _github.RemoveLabelAsync(owner, repo, prNumber, GitHubLabels.WorkflowReadyForReview);
                await _github.AddLabelsAsync(owner, repo, prNumber, GitHubLabels.WorkflowInReview);
            }

            // A non-author captain response acknowledges the author's update.
            if (captains.Contains(sender) && !IsSameUser(sender, author))
            {
                await _github.RemoveLabelAsync(owner, repo, prNumber, GitHubLabels.CommitsUpdated);
            }
        }
    }

    // When a new comment is created on the PR, if the commenter is a captain, we can also add "Workflow: In Review" label.
    protected override async ValueTask ProcessIssueCommentWebhookAsync(
        WebhookHeaders headers,
        IssueCommentEvent issueCommentEvent,
        IssueCommentAction action,
        CancellationToken cancellationToken = default)
    {
        if (action == IssueCommentAction.Created)
        {
            if (issueCommentEvent.Issue.PullRequest != null)
            {
                var prNumber = (int)issueCommentEvent.Issue.Number;
                if (!TryGetRepositoryContext(issueCommentEvent.Repository, out var owner, out var repo))
                    return;

                if (!TryGetSender(issueCommentEvent.Sender, out var sender))
                    return;

                var captains = await _github.GetTeamMembersAsync(owner, _config["GitHubApp:TeamName"]!);
                var pullRequest = await _github.GetPullRequestAsync(owner, repo, prNumber);
                string? author = issueCommentEvent.Issue.User?.Login ?? pullRequest.User?.Login;
                var prTitle = issueCommentEvent.Issue.Title;
                var currentLabels = GetLabelNames(issueCommentEvent.Issue.Labels);
                var isBlocked = currentLabels.Contains(GitHubLabels.WorkflowBlocked);

                // Only when the commenter is a captain, the PR is not in draft,
                // and the title doesn't start with "WIP", we consider it as "In Review" and add the label.
                if (captains.Contains(sender) &&
                    !isBlocked &&
                    !pullRequest.Draft &&
                    !prTitle.StartsWith("WIP", StringComparison.OrdinalIgnoreCase))
                {
                    await _github.RemoveLabelAsync(owner, repo, prNumber, GitHubLabels.WorkflowReadyForReview);
                    await _github.AddLabelsAsync(owner, repo, prNumber, GitHubLabels.WorkflowInReview);
                }

                // A non-author captain response acknowledges the author's update.
                if (captains.Contains(sender) && !IsSameUser(sender, author))
                {
                    await _github.RemoveLabelAsync(owner, repo, prNumber, GitHubLabels.CommitsUpdated);
                }
            }
        }
    }

    // When Workflow: Blocked label is added, remove In Review and Ready For Review labels
    private async Task HandleBlockedLabelAdded(string owner, string repo, int prNumber)
    {
        await _github.RemoveLabelAsync(owner, repo, prNumber, GitHubLabels.WorkflowInReview);
        await _github.RemoveLabelAsync(owner, repo, prNumber, GitHubLabels.WorkflowReadyForReview);
    }

    // When Workflow: Blocked label is removed, restore In Review label
    private async Task HandleBlockedLabelRemoved(string owner, string repo, int prNumber)
    {
        await _github.AddLabelsAsync(owner, repo, prNumber, GitHubLabels.WorkflowInReview);
    }

    private async Task AnalyzeUserAndLabelPR(string owner, string repo, int prNumber, string? userEmail)
    {
        var domain = ExtractEmailDomain(userEmail);
        if (string.IsNullOrWhiteSpace(domain))
            return;

        // Staff are excluded
        if (domain.Equals("iscas.ac.cn", StringComparison.OrdinalIgnoreCase))
            return;

        var labelsToAdd = new List<string>();

        // For intern, there are several types of E-mail:
        // 1) xxx.oerv@isrc.iscas.ac.cn
        // 2) xxx.or@isrc.iscas.ac.cn
        // 3) xxx.riscv@isrc.iscas.ac.cn
        if (IsInternEmail(userEmail))
            labelsToAdd.Add("Community: Student contribution");
        else
        // Any other domain counted as community contribution.
            labelsToAdd.Add("Community: Contribution");

        if (labelsToAdd.Any())
        {
            await _github.AddLabelsAsync(owner, repo, prNumber, labelsToAdd.Distinct().ToArray());
        }

    }

    private async Task AnalyzeFilesAndLabelPR(string owner, string repo, int prNumber, string sha)
    {
        var files = await _github.GetPullRequestFilesAsync(owner, repo, prNumber);
        var labelsToAdd = new List<string>();

        foreach (var file in files)
        {
            if (file.FileName.StartsWith(".github/"))
                labelsToAdd.Add("CI");

            // Check the file content only when the file is not removed.
            if (file.Status != "removed")
            {
                var content = await _github.GetFileContentAsync(owner, repo, file.FileName, sha);
                // Check if the file have "BuildSystem" line.
                // If it does, Add the corresponding label.
                // Otherwise, Add the "BuildSystem: misc" tag instead.
                // If it's not a RPM spec file, don't check the content.
                if (file.FileName.EndsWith(".spec"))
                {
                    if (content.Contains("BuildSystem:    autotools", StringComparison.OrdinalIgnoreCase))
                        labelsToAdd.Add("BuildSystem: autotools");
                    else if (content.Contains("BuildSystem:    cmake", StringComparison.OrdinalIgnoreCase))
                        labelsToAdd.Add("BuildSystem: cmake");
                    else if (content.Contains("BuildSystem:    golangmodule", StringComparison.OrdinalIgnoreCase))
                        labelsToAdd.Add("BuildSystem: golangmodule");
                    else if (content.Contains("BuildSystem:    golang", StringComparison.OrdinalIgnoreCase))
                        labelsToAdd.Add("BuildSystem: golang");
                    else if (content.Contains("BuildSystem:    rustcrate", StringComparison.OrdinalIgnoreCase))
                        labelsToAdd.Add("BuildSystem: rustcrate");
                    else if (content.Contains("BuildSystem:    rust", StringComparison.OrdinalIgnoreCase))
                        labelsToAdd.Add("BuildSystem: rust");
                    else if (content.Contains("BuildSystem:    meson", StringComparison.OrdinalIgnoreCase))
                        labelsToAdd.Add("BuildSystem: meson");
                    else if (content.Contains("BuildSystem:    pyproject", StringComparison.OrdinalIgnoreCase))
                        labelsToAdd.Add("BuildSystem: pyproject");
                    else
                        labelsToAdd.Add("BuildSystem: misc");
                }
            }

            if (file.FileName.StartsWith("SPECS/"))
            {
                // TODO: What if, LTS.
                labelsToAdd.Add("Target: Rolling");
            }
        }

        if (labelsToAdd.Any())
        {
            await _github.AddLabelsAsync(owner, repo, prNumber, labelsToAdd.Distinct().ToArray());
        }
    }
    
    // Analyze the PR description and update labels accordingly.
    private async Task AnalyzeDescriptionAndLabelPR(string owner, string repo, int prNumber, string? body)
    {
        // The PR description is the source of truth for the "AI Assistance" label.
        // If the checkbox is checked, add the label.
        // Otherwise, remove it to avoid stale label state.
        if (IsAiAssistedPullRequest(body))
            await _github.AddLabelsAsync(owner, repo, prNumber, GitHubLabels.AiAssistance);
        else
            await _github.RemoveLabelAsync(owner, repo, prNumber, GitHubLabels.AiAssistance);
    }

    private static HashSet<string> GetLabelNames(IEnumerable<Octokit.Webhooks.Models.Label>? labels)
    {
        return labels?
            .Select(label => label.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryGetRepositoryContext(
        Octokit.Webhooks.Models.Repository? repository,
        out string owner,
        out string repo)
    {
        owner = repository?.Owner?.Login ?? string.Empty;
        repo = repository?.Name ?? string.Empty;

        return !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo);
    }

    private static bool TryGetSender(Octokit.Webhooks.Models.User? sender, out string login)
    {
        login = sender?.Login?.ToLowerInvariant() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(login);
    }

    private static bool IsBlockedLabel(string? labelName)
    {
        return string.Equals(labelName, GitHubLabels.WorkflowBlocked, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameUser(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractEmailDomain(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        var atIndex = email.LastIndexOf('@');
        if (atIndex < 0 || atIndex == email.Length - 1)
            return null;

        return email[(atIndex + 1)..].Trim();
    }

    private static bool IsInternEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        var normalizedEmail = email.Trim();

        return normalizedEmail.EndsWith(".oerv@isrc.iscas.ac.cn", StringComparison.OrdinalIgnoreCase) ||
               normalizedEmail.EndsWith(".or@isrc.iscas.ac.cn", StringComparison.OrdinalIgnoreCase) ||
               normalizedEmail.EndsWith(".riscv@isrc.iscas.ac.cn", StringComparison.OrdinalIgnoreCase);
    }

    // Check whether the AI-assisted contribution checkbox is checked in the PR description.
    private static bool IsAiAssistedPullRequest(string? body)
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
}
