using abaci_bot.Contexts;
using abaci_bot.Pipelines;
using Octokit.Webhooks.Events.PullRequest;

namespace abaci_bot.Modules;

/// <summary>
/// RPM Spec 编译系统与 CI/Target 文件分析治理模块 (Priority: 30)
/// 治理范围：识别 .github/ (CI)、SPECS/ (Target: Rolling) 以及 .spec 文件中的 BuildSystem 声明。
/// </summary>
public class BuildSystemAnalysisModule : IPullRequestModule
{
    public string ModuleName => "BuildSystemAnalysisModule";

    public int Priority => 30;

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
            if (file.FileName.StartsWith(".github/"))
            {
                context.LabelsToAdd.Add("CI");
            }

            // Check the file content only when the file is not removed
            if (file.Status != "removed")
            {
                if (file.FileName.EndsWith(".spec"))
                {
                    var content = await context.GetFileContentAsync(file.FileName, context.HeadSha);

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

            if (file.FileName.StartsWith("SPECS/"))
            {
                context.LabelsToAdd.Add("Target: Rolling");
            }
        }
    }
}
