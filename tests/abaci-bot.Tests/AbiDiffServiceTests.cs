using abaci_bot.Contexts;
using abaci_bot.Modules;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit.Webhooks.Events.PullRequest;
using Xunit;

namespace abaci_bot.Tests;

public class AbiDiffServiceTests
{
    [Fact]
    public async Task ProcessAbiDiffRunner_MissingRpmFiles_ReturnsFailureWithoutInvokingProcess()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            AbidiffExecutablePath = "nonexistent-abidiff-bin"
        });
        var runner = new ProcessAbiDiffRunner(options);

        var result = await runner.RunDiffAsync("C:/nonexistent/old.rpm", "C:/nonexistent/new.rpm");

        Assert.False(result.Succeeded);
        Assert.False(result.ToolMissing);
        Assert.Contains("Target RPM file not found", result.ErrorMessage);
    }

    [Fact]
    public async Task ProcessAbiDiffRunner_ExecutableNotFound_ReturnsToolMissingTrue()
    {
        var tempOld = Path.GetTempFileName();
        var tempNew = Path.GetTempFileName();

        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
            {
                AbidiffExecutablePath = "nonexistent-abidiff-tool-12345XYZ"
            });
            var runner = new ProcessAbiDiffRunner(options);

            var result = await runner.RunDiffAsync(tempOld, tempNew);

            Assert.False(result.Succeeded);
            Assert.True(result.ToolMissing);
            Assert.Contains("not found", result.ErrorMessage);
        }
        finally
        {
            if (File.Exists(tempOld)) File.Delete(tempOld);
            if (File.Exists(tempNew)) File.Delete(tempNew);
        }
    }

    [Fact]
    public async Task AbiDiffService_DisabledInOptions_ReturnsFailureImmediately()
    {
        var runner = Substitute.For<IAbiDiffRunner>();
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = false
        });
        var service = new AbiDiffService(runner, options);

        var result = await service.CompareRpmsAsync("old.rpm", "new.rpm");

        Assert.False(result.Succeeded);
        Assert.Contains("disabled", result.Details);
        await runner.DidNotReceive().RunDiffAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AbiDiffService_Enabled_DelegatesToRunner()
    {
        var runner = Substitute.For<IAbiDiffRunner>();
        runner.RunDiffAsync("old.rpm", "new.rpm", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AbiDiffResult(
                Succeeded: true,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: "Clean")));

        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true
        });
        var service = new AbiDiffService(runner, options);

        var result = await service.CompareRpmsAsync("old.rpm", "new.rpm");

        Assert.True(result.Succeeded);
        Assert.Equal("Clean", result.Details);
        await runner.Received(1).RunDiffAsync("old.rpm", "new.rpm", Arg.Any<CancellationToken>());
    }
}
