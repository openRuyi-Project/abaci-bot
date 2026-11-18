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

    public Task<IReadOnlyCollection<string>> ClassifyAsync(IReadOnlyList<PullRequestFile> files)
    {
        if (files.Count == 0)
            return Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());

        var labels = new List<string>();
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
            labels.Add(GitHubLabels.TypeDocumentation);
        }
        else if (hasCiChanges)
        {
            labels.Add(GitHubLabels.TypeInfrastructure);
        }

        return Task.FromResult<IReadOnlyCollection<string>>(labels);
    }
}
