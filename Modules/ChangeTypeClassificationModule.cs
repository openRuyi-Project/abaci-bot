using abaci_bot.Contexts;
using abaci_bot.Modules.ChangeTypeStrategies;
using abaci_bot.Options;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using Octokit.Webhooks.Events.PullRequest;

namespace abaci_bot.Modules;

/// <summary>
/// Multi-repository change type classification module (Priority: 35).
/// Evaluates modified file paths using strategy pattern (<see cref="IChangeTypeStrategy"/>) to label PRs
/// with Type: Packaging, Type: Documentation, Type: Infrastructure, Type: Frontend, etc.
/// <para>
/// Architecture note on label consumers: These labels currently serve as human triage and search
/// discovery indexing for maintainers, allowing dashboard filtering and domain-specific code reviews.
/// They also serve as an architectural extension point for future automated actions (e.g. automated reviewer
/// assignment by change type or conditional CI triggering).
/// </para>
/// </summary>
public class ChangeTypeClassificationModule : IPullRequestModule
{
    private readonly List<IChangeTypeStrategy> _strategies;

    public string ModuleName => "ChangeTypeClassificationModule";

    public int Priority => 35;

    private static readonly string[] TypeLabels =
    [
        GitHubLabels.TypePackaging,
        GitHubLabels.TypeDocumentation,
        GitHubLabels.TypeInfrastructure,
        GitHubLabels.TypeFrontend
    ];

    public IReadOnlyCollection<string> ManagedLabels => TypeLabels;

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
        if (files == null)
        {
            context.ReconcileLabels(ManagedLabels, _ => false);
            return;
        }

        // Select the first strategy applicable to this repository based on registration order, with fallback to DefaultChangeTypeStrategy
        var strategy = _strategies.FirstOrDefault(s => s.AppliesTo(context.Repo))
                       ?? _strategies.OfType<DefaultChangeTypeStrategy>().FirstOrDefault();

        // The module owns the managed label set: a strategy only reports the labels the current
        // diff implies, so a classification that moved from packaging to documentation also
        // retires the stale Type: Packaging. An empty diff therefore retires everything.
        var desired = strategy == null
            ? Array.Empty<string>()
            : await strategy.ClassifyAsync(files);
        var desiredSet = new HashSet<string>(desired, StringComparer.OrdinalIgnoreCase);
        context.ReconcileLabels(ManagedLabels, desiredSet.Contains);
    }
}
