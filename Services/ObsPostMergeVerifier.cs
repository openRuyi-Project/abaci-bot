using System.Text.RegularExpressions;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Service performing Post-Merge OBS build verification for merged abaci-bot update PRs.
/// Validates that OBS _service has synchronized the merge commit SHA before asserting _result.
/// </summary>
public class ObsPostMergeVerifier
{
    private readonly IGitHubService _gitHubService;
    private readonly IObsBuildService _obsBuildService;
    private readonly PackageUpdateOptions _options;
    private readonly ILogger<ObsPostMergeVerifier>? _logger;

    private static readonly Regex PackageTitleRegex = new(
        @"^SPECS:\s*bump\s+(?<pkg>\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ObsPostMergeVerifier(
        IGitHubService gitHubService,
        IObsBuildService obsBuildService,
        IOptions<PackageUpdateOptions> options,
        ILogger<ObsPostMergeVerifier>? logger = null)
    {
        _gitHubService = gitHubService;
        _obsBuildService = obsBuildService;
        _options = options.Value;
        _logger = logger;
    }

    private static readonly string[] BlockedLabels = { GitHubLabels.AutoUpdateBlocked };

    public async Task VerifyMergedPullRequestsAsync(string targetRepo, CancellationToken ct = default)
    {
        var parts = targetRepo.Split('/');
        if (parts.Length != 2)
            return;

        var owner = parts[0].Trim();
        var repo = parts[1].Trim();

        var checkSince = DateTimeOffset.UtcNow.AddDays(-7);

        IReadOnlyList<Octokit.PullRequest> mergedPrs;
        try
        {
            mergedPrs = await _gitHubService.SearchMergedPullRequestsAsync(owner, repo, string.Empty, checkSince, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to search merged pull requests for {TargetRepo}", targetRepo);
            return;
        }

        if (mergedPrs.Count == 0)
            return;

        _logger?.LogInformation("Evaluating {Count} merged bot PRs for OBS build synchronization", mergedPrs.Count);

        foreach (var pr in mergedPrs)
        {
            if (ct.IsCancellationRequested)
                break;

            var match = PackageTitleRegex.Match(pr.Title ?? string.Empty);
            if (!match.Success)
                continue;

            var packageName = match.Groups["pkg"].Value.Trim();
            var mergeSha = pr.MergeCommitSha;
            if (string.IsNullOrWhiteSpace(mergeSha))
                continue;

            var serviceRevision = await _obsBuildService.GetPackageServiceRevisionAsync(
                _options.Obs.Project, packageName, ct);

            if (string.IsNullOrEmpty(serviceRevision))
            {
                _logger?.LogDebug("OBS _service not found or no revision for {Package}", packageName);
                continue;
            }

            if (!string.Equals(serviceRevision, mergeSha, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogDebug("OBS _service revision ({ObsRev}) does not match PR merge SHA ({MergeSha}) for {Package}; pending sync",
                    serviceRevision, mergeSha, packageName);
                continue;
            }

            // Revision matches: OBS has synchronized this merged commit! Now check _result
            var buildResult = await _obsBuildService.GetPackageBuildResultAsync(
                _options.Obs.Project, packageName, _options.Obs.TargetRepository, _options.Obs.TargetArch, ct);

            _logger?.LogInformation("OBS build result for merged PR #{PrNumber} ({Package}): {Status}",
                pr.Number, packageName, buildResult.Code);

            if (buildResult.Code == ObsStatusCode.Succeeded)
            {
                if (!_options.DryRun)
                {
                    if (pr.Labels != null && pr.Labels.Any(l => string.Equals(l.Name, GitHubLabels.AutoUpdateBlocked, StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            await _gitHubService.RemoveLabelAsync(owner, repo, pr.Number, GitHubLabels.AutoUpdateBlocked, ct);
                            _logger?.LogInformation("Removed {BlockedLabel} from merged PR #{PrNumber} after successful OBS build",
                                GitHubLabels.AutoUpdateBlocked, pr.Number);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogDebug(ex, "Failed to remove blocked label from PR #{Number}", pr.Number);
                        }
                    }

                    // Reclaim merged branch if it belongs to this repository and matches the prefix
                    var branchName = pr.Head?.Ref;
                    var headRepo = pr.Head?.Repository?.FullName;
                    var isSameRepo = headRepo != null && string.Equals(headRepo, $"{owner}/{repo}", StringComparison.OrdinalIgnoreCase);
                    var prefix = !string.IsNullOrEmpty(_options.BranchPrefix) ? _options.BranchPrefix : "autoupdate/";

                    if (isSameRepo && !string.IsNullOrWhiteSpace(branchName) && branchName.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        // 1. Check whether a newer open PR is already reusing this branch
                        var openPr = await _gitHubService.FindOpenPullRequestByHeadAsync(owner, repo, branchName, ct);
                        if (openPr != null)
                        {
                            _logger?.LogDebug("Skipping branch {Branch} deletion: active open PR #{OpenPr} is currently reusing it", branchName, openPr.Number);
                            continue;
                        }

                        // 2. Check whether the branch reference has moved forward past the merged PR's head SHA
                        var currentSha = await _gitHubService.GetRefShaAsync(owner, repo, branchName, ct);
                        if (!string.IsNullOrEmpty(pr.Head?.Sha) && !string.Equals(currentSha, pr.Head.Sha, StringComparison.Ordinal))
                        {
                            _logger?.LogDebug("Skipping branch {Branch} deletion: branch head has moved (current: {CurrentSha}, merged PR: {PrSha})",
                                branchName, currentSha, pr.Head.Sha);
                            continue;
                        }

                        try
                        {
                            await _gitHubService.DeleteRefAsync(owner, repo, branchName, ct);
                            _logger?.LogInformation("Reclaimed merged branch {Branch} for PR #{PrNumber} after successful OBS build verification",
                                branchName, pr.Number);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogDebug(ex, "Failed to reclaim merged branch {Branch} for PR #{PrNumber}", branchName, pr.Number);
                        }
                    }
                }
            }
            else if (_options.Obs.RequireObsSuccessAfterMerge &&
                     (buildResult.Code == ObsStatusCode.Failed || buildResult.Code == ObsStatusCode.Unresolvable))
            {
                _logger?.LogWarning("Merged PR #{PrNumber} failed post-merge OBS gate requirement ({Status})", pr.Number, buildResult.Code);
                if (!_options.DryRun)
                {
                    try
                    {
                        await _gitHubService.AddLabelsAsync(owner, repo, pr.Number, BlockedLabels, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Failed to apply blocked label to merged PR #{Number}", pr.Number);
                    }
                }
            }

            var comment = $"""
                ### OBS Build Verification
                - **Project**: `{buildResult.Project}`
                - **Package**: `{buildResult.Package}`
                - **Repository / Arch**: `{buildResult.Repository}` / `{buildResult.Arch}`
                - **Status**: `{buildResult.Code}` (`{buildResult.RawCode}`)
                - **Synchronized Commit**: `{mergeSha}`

                > Verified via `build.openruyi.cn/public` read-model.
                """;

            Octokit.IssueComment? existingComment = null;
            try
            {
                var existingComments = await _gitHubService.GetIssueCommentsAsync(owner, repo, pr.Number, ct);
                existingComment = existingComments.FirstOrDefault(c => c.Body != null && c.Body.Contains("### OBS Build Verification"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "Failed to check existing comments on PR #{PrNumber}", pr.Number);
            }

            if (existingComment != null)
            {
                // If comment already reflects current status and commit, skip
                if (existingComment.Body != null &&
                    existingComment.Body.Contains($"- **Status**: `{buildResult.Code}`") &&
                    existingComment.Body.Contains($"- **Synchronized Commit**: `{mergeSha}`"))
                {
                    _logger?.LogDebug("OBS build verification comment is already up-to-date on PR #{PrNumber}; skipping", pr.Number);
                    continue;
                }

                if (!_options.DryRun)
                {
                    _logger?.LogInformation("Updating OBS build verification comment on PR #{PrNumber} to status {Status}", pr.Number, buildResult.Code);
                    try
                    {
                        await _gitHubService.UpdateCommentAsync(owner, repo, existingComment.Id, comment, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger?.LogDebug(ex, "Failed to update OBS build verification comment on PR #{PrNumber}", pr.Number);
                    }
                }
                continue;
            }

            if (!_options.DryRun)
            {
                try
                {
                    await _gitHubService.CreateCommentAsync(owner, repo, pr.Number, comment, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogDebug(ex, "Failed to post OBS build verification comment on PR #{PrNumber}", pr.Number);
                }
            }
        }
    }
}

