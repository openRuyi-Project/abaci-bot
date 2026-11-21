using System.Net;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Octokit;
using Xunit;

namespace abaci_bot.Tests;

public class ConversationGuardPermissionProbeTests
{
    private readonly IRepositoryPermissionService _permissionService = Substitute.For<IRepositoryPermissionService>();

    [Fact]
    public async Task StartAsync_WhenDisabled_SetsDisabledAndSkipsProbe()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
        {
            Enabled = false,
            RepositoryAllowList = new List<string> { "owner/repo" }
        });

        var probe = new ConversationGuardPermissionProbe(
            options,
            _permissionService,
            NullLogger<ConversationGuardPermissionProbe>.Instance);

        await probe.StartAsync(CancellationToken.None);
        await probe.StopAsync(CancellationToken.None);

        Assert.Equal("disabled", ConversationGuardPermissionProbe.ProbeStatus);
        await _permissionService.DidNotReceiveWithAnyArgs().VerifyCollaboratorPermissionProbeAsync(default!, default!, default);
    }

    [Fact]
    public async Task StartAsync_WhenEnabledWithNoRepositories_SetsNoRepositories()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string>()
        });

        var probe = new ConversationGuardPermissionProbe(
            options,
            _permissionService,
            NullLogger<ConversationGuardPermissionProbe>.Instance);

        await probe.StartAsync(CancellationToken.None);

        Assert.Equal("no-repositories", ConversationGuardPermissionProbe.ProbeStatus);
        await _permissionService.DidNotReceiveWithAnyArgs().VerifyCollaboratorPermissionProbeAsync(default!, default!, default);
    }

    [Fact]
    public async Task StartAsync_WhenProbeSucceeds_SetsVerified()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "owner/repo" }
        });

        var probe = new ConversationGuardPermissionProbe(
            options,
            _permissionService,
            NullLogger<ConversationGuardPermissionProbe>.Instance);

        await probe.StartAsync(CancellationToken.None);

        Assert.Equal("verified", ConversationGuardPermissionProbe.ProbeStatus);
        await _permissionService.Received(1).VerifyCollaboratorPermissionProbeAsync("owner", "repo", Arg.Any<CancellationToken>());
        await _permissionService.DidNotReceiveWithAnyArgs().VerifyArchivePermissionProbeAsync(default!, default!, default);
    }

    [Fact]
    public async Task StartAsync_WhenArchiveEnabledAndProbeSucceeds_VerifiesArchivePermissionAndSetsVerified()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
        {
            Enabled = true,
            ArchiveEnabled = true,
            RepositoryAllowList = new List<string> { "owner/repo" }
        });

        var probe = new ConversationGuardPermissionProbe(
            options,
            _permissionService,
            NullLogger<ConversationGuardPermissionProbe>.Instance);

        await probe.StartAsync(CancellationToken.None);

        Assert.Equal("verified", ConversationGuardPermissionProbe.ProbeStatus);
        await _permissionService.Received(1).VerifyCollaboratorPermissionProbeAsync("owner", "repo", Arg.Any<CancellationToken>());
        await _permissionService.Received(1).VerifyArchivePermissionProbeAsync("owner", "repo", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_WhenArchiveEnabledAndArchiveProbeFails_AbortsStartupWithFatalException()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
        {
            Enabled = true,
            ArchiveEnabled = true,
            RepositoryAllowList = new List<string> { "owner/repo" }
        });

        _permissionService.VerifyArchivePermissionProbeAsync("owner", "repo", Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Administration='read'"));

        var probe = new ConversationGuardPermissionProbe(
            options,
            _permissionService,
            NullLogger<ConversationGuardPermissionProbe>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => probe.StartAsync(CancellationToken.None));

        Assert.Equal("failed", ConversationGuardPermissionProbe.ProbeStatus);
        Assert.Contains("Administration: Read & write", ex.Message);
        Assert.Contains("ArchiveEnabled=false", ex.Message);
    }

    [Fact]
    public async Task StartAsync_WhenProbeThrowsForbidden_AbortsStartupWithFatalException()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "owner/repo" }
        });

        _permissionService.VerifyCollaboratorPermissionProbeAsync("owner", "repo", Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new ForbiddenException(CreateResponse(HttpStatusCode.Forbidden)));

        var probe = new ConversationGuardPermissionProbe(
            options,
            _permissionService,
            NullLogger<ConversationGuardPermissionProbe>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => probe.StartAsync(CancellationToken.None));

        Assert.Equal("failed", ConversationGuardPermissionProbe.ProbeStatus);
        Assert.Contains("Administration: Read-only", ex.Message);
    }

    [Fact]
    public async Task StartAsync_WhenCancelled_RethrowsOperationCanceledException()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "owner/repo" }
        });

        _permissionService.VerifyCollaboratorPermissionProbeAsync("owner", "repo", Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new OperationCanceledException());

        var probe = new ConversationGuardPermissionProbe(
            options,
            _permissionService,
            NullLogger<ConversationGuardPermissionProbe>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() => probe.StartAsync(CancellationToken.None));
    }

    private static IResponse CreateResponse(HttpStatusCode statusCode)
    {
        var response = Substitute.For<IResponse>();
        response.StatusCode.Returns(statusCode);
        return response;
    }
}
