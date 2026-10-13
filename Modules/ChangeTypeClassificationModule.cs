using abaci_bot.Contexts;
using abaci_bot.Modules.ChangeTypeStrategies;
using abaci_bot.Options;
using abaci_bot.Pipelines;
using Microsoft.Extensions.Options;
using Octokit.Webhooks.Events.PullRequest;

namespace abaci_bot.Modules;

/// <summary>
/// Multi-repository change type classification module (Priority: 35).
/// Evaluates modified file paths using strategy pattern (IChangeTypeStrategy) to label PRs
/// with Type: Packaging, Type: Documentation, Type: Infrastructure, Type: Frontend, etc.
/// </summary>
public class ChangeTypeClassificationModule : IPullRequestModule
{
    private readonly List<IChangeTypeStrategy> _strategies;

    public string ModuleName => "ChangeTypeClassificationModule";

    public int Priority => 35;

    public ChangeTypeClassificationModule(
        IEnumerable<IChangeTypeStrategy>? strategies = null,
        IOptions<ChangeTypeClassificationOptions>? options = null)
    {
        if (strategies != null && strategies.Any())
        {
            _strategies = strategies.OrderBy(s => s.Priority).ToList();
        }
        else
        {
            _strategies = new List<IChangeTypeStrategy>
            {
                new OpenRuyiPackageRepoStrategy(options),
                new HomepageRepoStrategy(options),
                new DefaultChangeTypeStrategy(options)
            }.OrderBy(s => s.Priority).ToList();
        }
    }

    public bool ShouldProcess(PullRequestContext context)
    {
        return context.EventType == PullRequestEventType.PullRequest &&
               (context.PullRequestAction == PullRequestAction.Opened ||
                context.PullRequestAction == PullRequestAction.Synchronize ||
                context.PullRequestAction == PullRequestAction.Reopened ||
                context.PullRequestAction == PullRequestAction.ReadyForReview ||
                context.PullRequestAction == PullRequestAction.ConvertedToDraft);
    }

    public async Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken)
    {
        var files = await context.ChangedFiles.Value;
        if (files == null || files.Count == 0)
            return;

        // Select the first strategy applicable to this repository based on registration order, with fallback to DefaultChangeTypeStrategy
        var strategy = _strategies.FirstOrDefault(s => s.AppliesTo(context.Repo))
                       ?? _strategies.OfType<DefaultChangeTypeStrategy>().FirstOrDefault();

        if (strategy != null)
        {
            await strategy.EvaluateChangeTypesAsync(context, files);
        }
    }
}
