using abaci_bot.Contexts;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using Octokit;

namespace abaci_bot.Modules.ChangeTypeStrategies;

/// <summary>
/// Change type classification strategy for the community portal and documentation homepage repository.
/// Categorizes PRs into Type: Frontend, Type: Submodule Update, and Type: Documentation.
/// </summary>
public class HomepageRepoStrategy : IChangeTypeStrategy
{
    private readonly ChangeTypeClassificationOptions _options;

    public HomepageRepoStrategy(IOptions<ChangeTypeClassificationOptions>? options = null)
    {
        _options = options?.Value ?? new ChangeTypeClassificationOptions();
    }

    public int Priority => 10;

    public bool AppliesTo(string repositoryName)
    {
        return _options.HomepageRepositories.Any(r => string.Equals(r, repositoryName, StringComparison.OrdinalIgnoreCase));
    }

    public Task EvaluateChangeTypesAsync(PullRequestContext context, IReadOnlyList<PullRequestFile> files)
    {
        if (files.Count == 0)
            return Task.CompletedTask;

        bool hasFrontendChanges = false;
        bool hasSubmoduleChanges = false;
        bool allDocs = true;

        foreach (var file in files)
        {
            var path = file.FileName;

            if (!FileClassificationHelper.IsDocumentationFile(path, _options))
            {
                allDocs = false;
            }

            bool isFrontend = _options.FrontendExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            if (isFrontend)
            {
                hasFrontendChanges = true;
            }

            // Git submodule detection: status "submodule", .gitmodules file, or submodule commit pointer
            if (path.Equals(".gitmodules", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(file.Status, "submodule", StringComparison.OrdinalIgnoreCase) ||
                (file.Patch == null && file.Changes == 1 && (file.Additions + file.Deletions == 1) && !path.Contains('.')))
            {
                hasSubmoduleChanges = true;
            }
        }

        if (hasFrontendChanges)
        {
            context.LabelsToAdd.Add(GitHubLabels.TypeFrontend);
        }

        if (hasSubmoduleChanges)
        {
            context.LabelsToAdd.Add(GitHubLabels.TypeSubmoduleUpdate);
        }

        // Pure documentation changes (exclusively docs, mutually exclusive with frontend/submodule changes)
        if (allDocs && !hasFrontendChanges && !hasSubmoduleChanges)
        {
            context.LabelsToAdd.Add(GitHubLabels.TypeDocumentation);
        }

        return Task.CompletedTask;
    }
}
