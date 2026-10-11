using abaci_bot.Contexts;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Octokit.Webhooks.Events.PullRequest;

namespace abaci_bot.Modules;

/// <summary>
/// RPM Spec build system and CI file analysis module (Priority: 30).
/// Inspects modified file paths to assign CI, build system (autotools, cmake, meson, etc.),
/// and target release branch labels. Reconciles its complete managed-label set to clear stale classifications.
/// </summary>
public class BuildSystemAnalysisModule : IPullRequestModule
{
    public string ModuleName => "BuildSystemAnalysisModule";

    public int Priority => 30;

    /// <summary>
    /// Complete set of labels managed by this module across CI and build systems.
    /// Excludes Target:* release branch labels, which are governed by TargetClassificationModule.
    /// </summary>
    public static readonly IReadOnlyCollection<string> ManagedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        GitHubLabels.Ci,
        GitHubLabels.BuildSystemAutotools,
        GitHubLabels.BuildSystemCmake,
        GitHubLabels.BuildSystemGolangModule,
        GitHubLabels.BuildSystemGolang,
        GitHubLabels.BuildSystemRustCrate,
        GitHubLabels.BuildSystemRust,
        GitHubLabels.BuildSystemMeson,
        GitHubLabels.BuildSystemPyproject,
        GitHubLabels.BuildSystemMisc
    };

    IReadOnlyCollection<string> IPullRequestModule.ManagedLabels => ManagedLabels;

    /// <summary>
    /// Evaluates file changes whenever pull request contents are created or updated.
    /// </summary>
    public bool ShouldProcess(PullRequestContext context)
    {
        return context.EventType == PullRequestEventType.PullRequest &&
               (context.PullRequestAction == PullRequestAction.Opened ||
                context.PullRequestAction == PullRequestAction.Synchronize ||
                context.PullRequestAction == PullRequestAction.Reopened ||
                context.PullRequestAction == PullRequestAction.ConvertedToDraft ||
                context.PullRequestAction == PullRequestAction.ReadyForReview);
    }

    public async Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken)
    {
        var files = await context.ChangedFiles.Value;
        var desiredLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            // Workflows and CI configuration modifications
            if (file.FileName.StartsWith(".github/", StringComparison.Ordinal))
            {
                desiredLabels.Add(GitHubLabels.Ci);
            }

            // Only inspect file contents if the file was not deleted in this PR
            if (!string.Equals(file.Status, "removed", StringComparison.OrdinalIgnoreCase))
            {
                if (file.FileName.EndsWith(".spec", StringComparison.Ordinal))
                {
                    var content = await context.GetFileContentAsync(file.FileName, context.HeadSha, cancellationToken);

                    // Identify declared build systems in RPM spec files
                    if (content.Contains("BuildSystem:    autotools", StringComparison.OrdinalIgnoreCase))
                        desiredLabels.Add(GitHubLabels.BuildSystemAutotools);
                    else if (content.Contains("BuildSystem:    cmake", StringComparison.OrdinalIgnoreCase))
                        desiredLabels.Add(GitHubLabels.BuildSystemCmake);
                    else if (content.Contains("BuildSystem:    golangmodule", StringComparison.OrdinalIgnoreCase))
                        desiredLabels.Add(GitHubLabels.BuildSystemGolangModule);
                    else if (content.Contains("BuildSystem:    golang", StringComparison.OrdinalIgnoreCase))
                        desiredLabels.Add(GitHubLabels.BuildSystemGolang);
                    else if (content.Contains("BuildSystem:    rustcrate", StringComparison.OrdinalIgnoreCase))
                        desiredLabels.Add(GitHubLabels.BuildSystemRustCrate);
                    else if (content.Contains("BuildSystem:    rust", StringComparison.OrdinalIgnoreCase))
                        desiredLabels.Add(GitHubLabels.BuildSystemRust);
                    else if (content.Contains("BuildSystem:    meson", StringComparison.OrdinalIgnoreCase))
                        desiredLabels.Add(GitHubLabels.BuildSystemMeson);
                    else if (content.Contains("BuildSystem:    pyproject", StringComparison.OrdinalIgnoreCase))
                        desiredLabels.Add(GitHubLabels.BuildSystemPyproject);
                    else
                        desiredLabels.Add(GitHubLabels.BuildSystemMisc);
                }
            }

            // Standard package specification directory: legacy compatibility write for Target: Rolling.
            // Full release target lifecycle governance is owned exclusively by TargetClassificationModule.
            if (file.FileName.StartsWith("SPECS/", StringComparison.Ordinal))
            {
                context.LabelsToAdd.Add(GitHubLabels.TargetRolling);
            }
        }

        // Reconcile complete managed label set: add current desired labels, remove stale ones
        context.ReconcileLabels(ManagedLabels, label => desiredLabels.Contains(label));
    }
}
