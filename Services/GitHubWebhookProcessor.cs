using Octokit.Webhooks;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Events.PullRequest;
using Octokit.Webhooks.Events.PullRequestReview;
using Octokit.Webhooks.Events.IssueComment;

namespace abaci_bot.Services;

public class GitHubWebhookProcessor : WebhookEventProcessor
{
    private readonly GitHubService _github;
    private readonly IConfiguration _config;

    public GitHubWebhookProcessor(GitHubService github, IConfiguration config)
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
        var owner = pullRequestEvent.Repository.Owner.Login;
        var repo = pullRequestEvent.Repository.Name;
        var headSha = pullRequestEvent.PullRequest.Head.Sha;
        var isDraft = pullRequestEvent.PullRequest.Draft;
        var currentLabels = pullRequestEvent.PullRequest.Labels.Select(l => l.Name).ToList();

        // Handle PR when opened or synchronized (new commit)
        if (action == PullRequestAction.Opened || 
            action == PullRequestAction.Synchronize || 
            action == PullRequestAction.Reopened ||
            action == PullRequestAction.ConvertedToDraft || 
            action == PullRequestAction.ReadyForReview)
        {
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

            if (isDraft)
            {
                // For draft PR, add "Workflow: In Dev" label to indicate it's still in development,
                // and remove "Workflow: Ready For Review" label if exists.
                await _github.AddLabelsAsync(owner, repo, prNumber, "Workflow: In Dev");
                await _github.RemoveLabelAsync(owner, repo, prNumber, "Workflow: Ready For Review");
            }
            else
            {
                // For non-draft PR, add "Workflow: Ready For Review" label and remove "Workflow: In Dev" label if exists.
                await _github.RemoveLabelAsync(owner, repo, prNumber, "Workflow: In Dev");
                if (!currentLabels.Contains("Workflow: In Review"))
                {
                    await _github.AddLabelsAsync(owner, repo, prNumber, "Workflow: Ready For Review");
                }
                
            }
        }
        // When PR is closed, remove/cleanup labels
        else if (action == PullRequestAction.Closed)
        {
            if (pullRequestEvent.PullRequest.Merged == true)
            {
                // For merged PR, add "Workflow: Complete" label.
                await _github.AddLabelsAsync(owner, repo, prNumber, "Workflow: Complete");
            }
            await _github.RemoveLabelAsync(owner, repo, prNumber, "Workflow: In Dev");
            await _github.RemoveLabelAsync(owner, repo, prNumber, "Workflow: Ready For Review");
            await _github.RemoveLabelAsync(owner, repo, prNumber, "Workflow: In Review");
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
            var owner = pullRequestReviewEvent.Repository.Owner.Login;
            var repo = pullRequestReviewEvent.Repository.Name;
            var captains = await _github.GetTeamMembersAsync(owner, _config["GitHubApp:TeamName"]!);
            string sender = pullRequestReviewEvent.Sender.Login.ToLowerInvariant();

            // If the review is submitted by a captain, add "Workflow: In Review" label.
            if (captains.Contains(sender))
            {
                await _github.RemoveLabelAsync(owner, repo, prNumber, "Workflow: Ready For Review");
                await _github.AddLabelsAsync(owner, repo, prNumber, "Workflow: In Review");
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
                var owner = issueCommentEvent.Repository.Owner.Login;
                var repo = issueCommentEvent.Repository.Name;
                var captains = await _github.GetTeamMembersAsync(owner, _config["GitHubApp:TeamName"]!);
                string sender = issueCommentEvent.Sender.Login.ToLowerInvariant();
                var pullRequest = await _github.GetPullRequestAsync(owner, repo, prNumber);
                var prTitle = issueCommentEvent.Issue.Title;

                // Only when the commenter is a captain, the PR is not in draft,
                // and the title doesn't start with "WIP", we consider it as "In Review" and add the label.
                if (captains.Contains(sender) &&
                    !pullRequest.Draft &&
                    !prTitle.StartsWith("WIP", StringComparison.OrdinalIgnoreCase))
                {
                    await _github.RemoveLabelAsync(owner, repo, prNumber, "Workflow: Ready For Review");
                    await _github.AddLabelsAsync(owner, repo, prNumber, "Workflow: In Review");
                }
            }
        }
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
}
