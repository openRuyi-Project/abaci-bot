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

    [Fact]
    public void ProcessAbiDiffRunner_BuildStartInfo_ConstructsAccurateAbipkgdiffCliContract()
    {
        var startInfo = ProcessAbiDiffRunner.BuildStartInfo("abipkgdiff", "/tmp/old.rpm", "/tmp/new.rpm");

        Assert.Equal("abipkgdiff", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(2, startInfo.ArgumentList.Count);
        Assert.Equal("/tmp/old.rpm", startInfo.ArgumentList[0]);
        Assert.Equal("/tmp/new.rpm", startInfo.ArgumentList[1]);
    }

    [Fact]
    public void InterpretExitCode_BitmaskInterpretations_AccuratelyDifferentiatesAbiChanges()
    {
        // 0 = clean
        var res0 = ProcessAbiDiffRunner.InterpretExitCode(0, "clean");
        Assert.True(res0.Succeeded);
        Assert.False(res0.HasAbiChange);
        Assert.False(res0.IsBreakingChange);

        // 4 = ABI change
        var res4 = ProcessAbiDiffRunner.InterpretExitCode(4, "diff");
        Assert.True(res4.Succeeded);
        Assert.True(res4.HasAbiChange);
        Assert.False(res4.IsBreakingChange);

        // 8 = Breaking change
        var res8 = ProcessAbiDiffRunner.InterpretExitCode(8, "breaking");
        Assert.True(res8.Succeeded);
        Assert.True(res8.HasAbiChange);
        Assert.True(res8.IsBreakingChange);

        // 1 or 2 = tool error
        var res1 = ProcessAbiDiffRunner.InterpretExitCode(1, "", "error");
        Assert.False(res1.Succeeded);
        Assert.Contains("abipkgdiff reported tool failure", res1.ErrorMessage);
    }
}
