using abaci_bot.Contexts;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Microsoft.Extensions.Logging;
using Octokit.Webhooks.Events.IssueComment;

namespace abaci_bot.Modules;

/// <summary>
/// Control-plane mention module (Priority: 96).
/// Handles package-level commands (<c>@&lt;bot&gt; ignore</c>, <c>unignore</c>, <c>status</c>)
/// and user language preference commands (<c>@&lt;bot&gt; lang</c>).
/// </summary>
public class BotControlPlaneModule : IPullRequestModule
{
    private readonly IBotIdentityProvider _botIdentity;
    private readonly IBotControlPlaneService _controlPlaneService;
    private readonly ILogger<BotControlPlaneModule>? _logger;

    public string ModuleName => "BotControlPlaneModule";

    public int Priority => 96;

    public BotControlPlaneModule(
        IBotIdentityProvider botIdentity,
        IBotControlPlaneService controlPlaneService,
        ILogger<BotControlPlaneModule>? logger = null)
    {
        _botIdentity = botIdentity;
        _controlPlaneService = controlPlaneService;
        _logger = logger;
    }

    public bool ShouldProcess(PullRequestContext context)
    {
        if (context.EventType != PullRequestEventType.IssueComment ||
            context.IssueCommentAction != IssueCommentAction.Created)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(context.Body) &&
               context.Body.TrimStart().StartsWith('@');
    }

    public async Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.Body) || string.IsNullOrWhiteSpace(context.SenderLogin))
        {
            return;
        }

        var botLogin = await _botIdentity.GetBotLoginAsync(cancellationToken);
        if (!BotCommandParser.TryParse(context.Body, botLogin, out var parsed) ||
            parsed.Verb is not ("ignore" or "unignore" or "status" or "lang"))
        {
            return;
        }

        _logger?.LogInformation(
            "Executing bot control-plane command '{Verb}' in {Owner}/{Repo}#{PrNumber} by {Sender}",
            parsed.Verb, context.Owner, context.Repo, context.PrNumber, context.SenderLogin);

        await _controlPlaneService.HandleCommandAsync(
            context,
            botLogin,
            parsed,
            context.SenderLogin,
            cancellationToken);
    }
}
