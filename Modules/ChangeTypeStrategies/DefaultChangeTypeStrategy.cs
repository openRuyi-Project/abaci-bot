using abaci_bot.Contexts;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using Octokit;

namespace abaci_bot.Modules.ChangeTypeStrategies;

/// <summary>
/// Default fallback strategy for change type classification across generic repositories.
/// </summary>
public class DefaultChangeTypeStrategy : IChangeTypeStrategy
{
    private readonly ChangeTypeClassificationOptions _options;

    public DefaultChangeTypeStrategy(IOptions<ChangeTypeClassificationOptions>? options = null)
    {
        _options = options?.Value ?? new ChangeTypeClassificationOptions();
    }

    public int Priority => 1000;

    public bool AppliesTo(string repositoryName) => true; // Always matches as fallback

    public Task EvaluateChangeTypesAsync(PullRequestContext context, IReadOnlyList<PullRequestFile> files)
    {
        if (files.Count == 0)
            return Task.CompletedTask;

        bool hasCiChanges = false;
        bool allDocs = true;

        foreach (var file in files)
        {
            var path = file.FileName;

            if (!FileClassificationHelper.IsDocumentationFile(path, _options))
            {
                allDocs = false;
            }

            if (FileClassificationHelper.IsCiFile(path))
            {
                hasCiChanges = true;
            }
        }

        if (allDocs)
        {
            context.LabelsToAdd.Add(GitHubLabels.TypeDocumentation);
        }
        else if (hasCiChanges)
        {
            context.LabelsToAdd.Add(GitHubLabels.TypeInfrastructure);
        }

        return Task.CompletedTask;
    }
}
