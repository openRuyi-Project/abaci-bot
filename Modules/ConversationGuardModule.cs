using abaci_bot.Contexts;
using abaci_bot.Options;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit.Webhooks.Events.IssueComment;

namespace abaci_bot.Modules;

/// <summary>
/// Conversation guard governance module (Priority: 95).
/// Processes conversational management mention commands (<c>@&lt;bot&gt; lock</c>, <c>unlock</c>, <c>hide</c>, <c>archive</c>) posted in PR issue comments.
/// </summary>
public class ConversationGuardModule : IPullRequestModule
{
    private readonly ConversationGuardOptions _options;
    private readonly IConversationGuardService _guardService;
    private readonly IBotIdentityProvider _botIdentity;
    private readonly ILogger<ConversationGuardModule>? _logger;

    public string ModuleName => "ConversationGuardModule";

    public int Priority => 95;

    public ConversationGuardModule(
        IOptions<ConversationGuardOptions> options,
        IConversationGuardService guardService,
        IBotIdentityProvider botIdentity,
        ILogger<ConversationGuardModule>? logger = null)
    {
        _options = options.Value;
        _guardService = guardService;
        _botIdentity = botIdentity;
        _logger = logger;
    }

    public bool ShouldProcess(PullRequestContext context)
    {
        if (!_options.Enabled || !RepositoryAllowList.IsAllowed(context.Owner, context.Repo, _options.RepositoryAllowList))
        {
            return false;
        }

        if (context.EventType != PullRequestEventType.IssueComment ||
            context.IssueCommentAction != IssueCommentAction.Created)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(context.Body))
        {
            return false;
        }

        return context.Body.TrimStart().StartsWith('@');
    }

    public async Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.Body) || string.IsNullOrWhiteSpace(context.SenderLogin))
        {
            return;
        }

        var botLogin = await _botIdentity.GetBotLoginAsync(cancellationToken);
        if (!BotCommandParser.TryParse(context.Body, botLogin, out var parsed) ||
            parsed.Verb is not ("lock" or "unlock" or "hide" or "archive"))
        {
            return;
        }

        _logger?.LogInformation(
            "Executing conversation guard command '{Verb}' in {Owner}/{Repo}#{PrNumber} by {Sender}",
            parsed.Verb, context.Owner, context.Repo, context.PrNumber, context.SenderLogin);

        await _guardService.HandleCommandAsync(
            context,
            context.Body,
            context.SenderLogin,
            cancellationToken);
    }
}
