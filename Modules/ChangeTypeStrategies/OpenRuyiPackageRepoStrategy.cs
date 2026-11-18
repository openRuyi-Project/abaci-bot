using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using Octokit;

namespace abaci_bot.Modules.ChangeTypeStrategies;

/// <summary>
/// Change type classification strategy for package repositories (such as openRuyi).
/// Identifies Packaging changes (spec files, patches), Documentation, and CI modifications.
/// </summary>
public class OpenRuyiPackageRepoStrategy : IChangeTypeStrategy
{
    private readonly ChangeTypeClassificationOptions _options;

    public OpenRuyiPackageRepoStrategy(IOptions<ChangeTypeClassificationOptions>? options = null)
    {
        _options = options?.Value ?? new ChangeTypeClassificationOptions();
    }

    public int Priority => 10;

    public bool AppliesTo(string repositoryName)
    {
        return _options.PackageRepositories.Any(r => string.Equals(r, repositoryName, StringComparison.OrdinalIgnoreCase));
    }

    public Task<IReadOnlyCollection<string>> ClassifyAsync(IReadOnlyList<PullRequestFile> files)
    {
        if (files.Count == 0)
            return Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());

        var labels = new List<string>();
        bool hasPackagingChanges = false;
        bool hasCiChanges = false;
        bool allDocs = true;

        foreach (var file in files)
        {
            var path = file.FileName;

            if (!FileClassificationHelper.IsDocumentationFile(path, _options))
            {
                allDocs = false;
            }

            bool isPkg = _options.PackagingExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) ||
                         _options.PackagingDirectories.Any(dir => path.StartsWith(dir, StringComparison.OrdinalIgnoreCase));

            if (isPkg)
            {
                hasPackagingChanges = true;
            }

            if (FileClassificationHelper.IsCiFile(path))
            {
                hasCiChanges = true;
            }
        }

        // 1. Packaging takes priority
        if (hasPackagingChanges)
        {
            labels.Add(GitHubLabels.TypePackaging);
        }
        // 2. Pure documentation changes (exclusively docs, mutually exclusive with Packaging and Infrastructure)
        else if (allDocs)
        {
            labels.Add(GitHubLabels.TypeDocumentation);
        }
        // 3. Infrastructure changes (.github/ without spec or pure documentation changes)
        else if (hasCiChanges)
        {
            labels.Add(GitHubLabels.TypeInfrastructure);
        }

        return Task.FromResult<IReadOnlyCollection<string>>(labels);
    }
}
