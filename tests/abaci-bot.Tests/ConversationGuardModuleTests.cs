using abaci_bot.Contexts;
using abaci_bot.Modules;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit.Webhooks.Events.IssueComment;

namespace abaci_bot.Tests;

public class ConversationGuardModuleTests
{
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly IConversationGuardService _guardService = Substitute.For<IConversationGuardService>();
    private readonly IBotIdentityProvider _botIdentity = Substitute.For<IBotIdentityProvider>();

    public ConversationGuardModuleTests()
    {
        _botIdentity.GetBotLoginAsync(Arg.Any<CancellationToken>()).Returns("abaci-bot");
    }

    private PullRequestContext CreateContext(
        string body,
        string senderLogin = "commenter",
        IssueCommentAction? commentAction = null,
        PullRequestEventType eventType = PullRequestEventType.IssueComment,
        string owner = "openRuyi-Project",
        string repo = "openRuyi")
    {
        var action = commentAction ?? IssueCommentAction.Created;
        return new PullRequestContext(
            gitHubService: _gitHub,
            teamName: "captains",
            headers: null,
            eventType: eventType,
            owner: owner,
            repo: repo,
            prNumber: 42,
            headSha: "head123",
            baseBranch: "main",
            isDraft: false,
            isMerged: false,
            title: "Test PR",
            body: body,
            senderLogin: senderLogin,
            authorLogin: "pr-author",
            action: action.ToString(),
            existingLabels: Array.Empty<string>(),
            commentAction: action);
    }

    private ConversationGuardModule CreateModule(ConversationGuardOptions options) =>
        new(Microsoft.Extensions.Options.Options.Create(options), _guardService, _botIdentity, NullLogger<ConversationGuardModule>.Instance);

    [Fact]
    public void Properties_CorrectValues()
    {
        var module = CreateModule(new ConversationGuardOptions());

        Assert.Equal(95, module.Priority);
        Assert.Equal("ConversationGuardModule", module.ModuleName);
    }

    [Fact]
    public void ShouldProcess_Disabled_ReturnsFalse()
    {
        var module = CreateModule(new ConversationGuardOptions
        {
            Enabled = false,
            RepositoryAllowList = new List<string> { "openRuyi-Project/openRuyi" }
        });
        var context = CreateContext("@abaci-bot lock");

        Assert.False(module.ShouldProcess(context));
    }

    [Fact]
    public void ShouldProcess_EmptyAllowList_ReturnsFalse()
    {
        var module = CreateModule(new ConversationGuardOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string>()
        });
        var context = CreateContext("@abaci-bot lock");

        Assert.False(module.ShouldProcess(context));
    }

    [Theory]
    [InlineData("/lock")]
    [InlineData("/unlock")]
    [InlineData("/hide @troll")]
    [InlineData("/archive")]
    [InlineData("LGTM! Great work.")]
    [InlineData("   ")]
    public void ShouldProcess_NonMentionOrLegacySlashBody_ReturnsFalse(string body)
    {
        var module = CreateModule(new ConversationGuardOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "openRuyi-Project/openRuyi" }
        });
        var context = CreateContext(body);

        Assert.False(module.ShouldProcess(context));
    }

    [Fact]
    public void ShouldProcess_NotAllowListedRepo_ReturnsFalse()
    {
        var module = CreateModule(new ConversationGuardOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "other/repo" }
        });
        var context = CreateContext("@abaci-bot lock");

        Assert.False(module.ShouldProcess(context));
    }

    [Fact]
    public void ShouldProcess_ShortRepoNameWithoutOwner_ReturnsFalse()
    {
        var module = CreateModule(new ConversationGuardOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "openRuyi" }
        });
        var context = CreateContext("@abaci-bot lock");

        Assert.False(module.ShouldProcess(context));
    }

    [Fact]
    public void ShouldProcess_WrongAction_ReturnsFalse()
    {
        var module = CreateModule(new ConversationGuardOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "openRuyi-Project/openRuyi" }
        });
        var context = CreateContext("@abaci-bot lock", commentAction: IssueCommentAction.Edited);

        Assert.False(module.ShouldProcess(context));
    }

    [Fact]
    public void ShouldProcess_WrongEventType_ReturnsFalse()
    {
        var module = CreateModule(new ConversationGuardOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "openRuyi-Project/openRuyi" }
        });
        var context = CreateContext("@abaci-bot lock", eventType: PullRequestEventType.PullRequest);

        Assert.False(module.ShouldProcess(context));
    }

    [Theory]
    [InlineData("@abaci-bot lock")]
    [InlineData("@abaci-bot unlock")]
    [InlineData("@abaci-bot hide @troll")]
    [InlineData("@abaci-bot archive")]
    public void ShouldProcess_ValidMentionCommands_ReturnsTrue(string command)
    {
        var module = CreateModule(new ConversationGuardOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "openRuyi-Project/openRuyi" }
        });
        var context = CreateContext(command);

        Assert.True(module.ShouldProcess(context));
    }

    [Fact]
    public async Task ProcessAsync_DelegatesToService_WhenBotMentionMatches()
    {
        var module = CreateModule(new ConversationGuardOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "openRuyi-Project/openRuyi" }
        });
        var context = CreateContext("@abaci-bot lock confirm", senderLogin: "alice");

        await module.ProcessAsync(context, CancellationToken.None);

        await _guardService.Received(1).HandleCommandAsync(context, "@abaci-bot lock confirm", "alice", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("@otherbot lock")]
    [InlineData("@abaci-bot ignore dnf5")]
    [InlineData("@abaci-bot lockfoo")]
    public async Task ProcessAsync_OtherBotOrControlPlaneCommand_DoesNotInvokeGuardService(string body)
    {
        var module = CreateModule(new ConversationGuardOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "openRuyi-Project/openRuyi" }
        });
        var context = CreateContext(body, senderLogin: "alice");

        await module.ProcessAsync(context, CancellationToken.None);

        await _guardService.DidNotReceiveWithAnyArgs().HandleCommandAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task ProcessAsync_NullBodyOrSender_DoesNotInvokeService()
    {
        var module = CreateModule(new ConversationGuardOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "openRuyi-Project/openRuyi" }
        });
        var context = CreateContext(string.Empty, senderLogin: string.Empty);

        await module.ProcessAsync(context, CancellationToken.None);

        await _guardService.DidNotReceiveWithAnyArgs().HandleCommandAsync(default!, default!, default!, default);
    }
}
