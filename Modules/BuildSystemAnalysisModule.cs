using abaci_bot.Contexts;
using abaci_bot.Pipelines;
using Octokit.Webhooks.Events.PullRequest;

namespace abaci_bot.Modules;

/// <summary>
/// RPM Spec build system and CI file analysis module (Priority: 30).
/// Inspects modified file paths to assign CI, build system (autotools, cmake, meson, etc.),
/// and target release branch labels.
/// </summary>
public class BuildSystemAnalysisModule : IPullRequestModule
{
    public string ModuleName => "BuildSystemAnalysisModule";

    public int Priority => 30;

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

    public async Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken = default)
    {
        var files = await context.ChangedFiles.Value;

        foreach (var file in files)
        {
            // Workflows and CI configuration modifications
            if (file.FileName.StartsWith(".github/"))
            {
                context.LabelsToAdd.Add("CI");
            }

            // Only inspect file contents if the file was not deleted in this PR
            if (file.Status != "removed")
            {
                if (file.FileName.EndsWith(".spec"))
                {
                    var content = await context.GetFileContentAsync(file.FileName, context.HeadSha, cancellationToken);

                    // Identify declared build systems in RPM spec files
                    if (content.Contains("BuildSystem:    autotools", StringComparison.OrdinalIgnoreCase))
                        context.LabelsToAdd.Add("BuildSystem: autotools");
                    else if (content.Contains("BuildSystem:    cmake", StringComparison.OrdinalIgnoreCase))
                        context.LabelsToAdd.Add("BuildSystem: cmake");
                    else if (content.Contains("BuildSystem:    golangmodule", StringComparison.OrdinalIgnoreCase))
                        context.LabelsToAdd.Add("BuildSystem: golangmodule");
                    else if (content.Contains("BuildSystem:    golang", StringComparison.OrdinalIgnoreCase))
                        context.LabelsToAdd.Add("BuildSystem: golang");
                    else if (content.Contains("BuildSystem:    rustcrate", StringComparison.OrdinalIgnoreCase))
                        context.LabelsToAdd.Add("BuildSystem: rustcrate");
                    else if (content.Contains("BuildSystem:    rust", StringComparison.OrdinalIgnoreCase))
                        context.LabelsToAdd.Add("BuildSystem: rust");
                    else if (content.Contains("BuildSystem:    meson", StringComparison.OrdinalIgnoreCase))
                        context.LabelsToAdd.Add("BuildSystem: meson");
                    else if (content.Contains("BuildSystem:    pyproject", StringComparison.OrdinalIgnoreCase))
                        context.LabelsToAdd.Add("BuildSystem: pyproject");
                    else
                        context.LabelsToAdd.Add("BuildSystem: misc");
                }
            }

            // Standard package specification directory
            if (file.FileName.StartsWith("SPECS/"))
            {
                context.LabelsToAdd.Add("Target: Rolling");
            }
        }
    }
}
