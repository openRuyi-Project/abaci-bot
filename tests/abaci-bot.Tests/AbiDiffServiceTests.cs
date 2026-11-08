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
            AbipkgdiffExecutablePath = "nonexistent-abidiff-bin"
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
                AbipkgdiffExecutablePath = "nonexistent-abidiff-tool-12345XYZ"
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
        Assert.Equal(3, startInfo.ArgumentList.Count);
        Assert.Equal("--private-dso", startInfo.ArgumentList[0]);
        Assert.Equal("/tmp/old.rpm", startInfo.ArgumentList[1]);
        Assert.Equal("/tmp/new.rpm", startInfo.ArgumentList[2]);
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

    [Fact]
    public void InterpretExitCode_ExitCodeZeroWithMissingRpmSupportStderr_ReturnsToolMissingFailure()
    {
        const string missingRpmStderr = "abipkgdiff: Support for rpm hasn't been enabled.  Please consider enabling it at package configure time";
        var result = ProcessAbiDiffRunner.InterpretExitCode(0, "", missingRpmStderr);

        Assert.False(result.Succeeded);
        Assert.True(result.ToolMissing);
        Assert.False(result.HasAbiChange);
        Assert.False(result.IsBreakingChange);
        Assert.Contains("Support for rpm hasn't been enabled", result.ErrorMessage);
        Assert.Contains("missing RPM support", result.ErrorMessage);
    }

    [Fact]
    public void InterpretExitCode_Ubuntu2404MissingRpm2cpio_ReturnsToolMissingFailure()
    {
        // Real-world Ubuntu 24.04 WSL output when abipkgdiff is present but rpm2cpio is missing:
        const string ubuntu2404Stderr =
            "rpm2cpio: command not found\n" +
            "cpio: premature end of archive\n" +
            "abipkgdiff: Error while extracting package .../demo-lib-2.0-1.x86_64.rpm\n" +
            "rpm2cpio: command not found\n" +
            "cpio: premature end of archive\n" +
            "abipkgdiff: Error while extracting package .../demo-lib-1.0-1.x86_64.rpm";

        var result = ProcessAbiDiffRunner.InterpretExitCode(0, "", ubuntu2404Stderr);

        Assert.False(result.Succeeded);
        Assert.True(result.ToolMissing);
        Assert.False(result.HasAbiChange);
        Assert.False(result.IsBreakingChange);
        Assert.Contains("missing tool or package extraction failure", result.ErrorMessage);
    }

    [Fact]
    public void InterpretExitCode_ExitCodeZeroWithEmptyStdoutAndNonEmptyStderr_ReturnsFailure()
    {
        const string unknownStderr = "some internal extraction failure occurred";
        var result = ProcessAbiDiffRunner.InterpretExitCode(0, "", unknownStderr);

        Assert.False(result.Succeeded);
        Assert.Contains("produced no comparison output", result.ErrorMessage);
    }

    [Fact]
    public void InterpretExitCode_ExitCodeZeroWithGenericErrorStderr_ReturnsFailureWithoutToolMissing()
    {
        const string errorStderr = "abipkgdiff: error: failed to open archive file";
        var result = ProcessAbiDiffRunner.InterpretExitCode(0, "", errorStderr);

        Assert.False(result.Succeeded);
        Assert.False(result.ToolMissing);
        Assert.Contains("abipkgdiff reported error on stderr", result.ErrorMessage);
    }

    [Fact]
    public async Task ProcessAbiDiffRunner_RpmSupportMissing_CachesUnavailableAndShortCircuitsSubsequentCalls()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions());
        var runner = new ProcessAbiDiffRunner(options);

        Assert.False(runner.IsRpmSupportUnavailable);

        runner.MarkRpmSupportUnavailable("abipkgdiff reported missing RPM support: Support for rpm hasn't been enabled.");
        Assert.True(runner.IsRpmSupportUnavailable);

        // Subsequent calls should short-circuit immediately without needing files on disk
        var result = await runner.RunDiffAsync("/nonexistent/old.rpm", "/nonexistent/new.rpm");

        Assert.False(result.Succeeded);
        Assert.True(result.ToolMissing);
        Assert.Contains("Support for rpm hasn't been enabled", result.ErrorMessage);
    }

    [Fact]
    public void InterpretExitCode_ExitCodeOneWithMissingRpmSupportStderr_ReturnsToolMissingFailure()
    {
        // Ubuntu 22.04 exits with code 1 when libabigail lacks RPM support
        const string missingRpmStderr = "abipkgdiff: Support for rpm hasn't been enabled.  Please consider enabling it at package configure time";
        var result = ProcessAbiDiffRunner.InterpretExitCode(1, "", missingRpmStderr);

        Assert.False(result.Succeeded);
        Assert.True(result.ToolMissing);
        Assert.False(result.HasAbiChange);
        Assert.False(result.IsBreakingChange);
        Assert.Contains("Support for rpm hasn't been enabled", result.ErrorMessage);
        Assert.Contains("missing RPM support", result.ErrorMessage);
    }

    [Fact]
    public async Task AbiDiffService_PrimaryRunnerToolMissing_FallsBackToManagedRunnerWhenEnabled()
    {
        var primaryRunner = Substitute.For<IAbiDiffRunner>();
        primaryRunner.RunDiffAsync("old.rpm", "new.rpm", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: "Missing tool",
                ErrorMessage: "abipkgdiff executable not found",
                ToolMissing: true)));

        var fallbackRunner = new ManagedElfDiffRunner();
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true,
            EnableFallback = true
        });

        var service = new AbiDiffService(primaryRunner, fallbackRunner, options);

        var result = await service.CompareRpmsAsync("old.rpm", "new.rpm");

        // Fallback was invoked (it fails on nonexistent files, not on missing tool)
        Assert.False(result.Succeeded);
        Assert.False(result.ToolMissing);
        Assert.Contains("Target RPM file not found", result.ErrorMessage);
    }

    [Fact]
    public async Task AbiDiffService_PrimaryRunnerToolMissing_DoesNotFallBackWhenDisabled()
    {
        var primaryRunner = Substitute.For<IAbiDiffRunner>();
        primaryRunner.RunDiffAsync("old.rpm", "new.rpm", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: "Missing tool",
                ErrorMessage: "abipkgdiff executable not found",
                ToolMissing: true)));

        var fallbackRunner = new ManagedElfDiffRunner();
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true,
            EnableFallback = false
        });

        var service = new AbiDiffService(primaryRunner, fallbackRunner, options);

        var result = await service.CompareRpmsAsync("old.rpm", "new.rpm");

        // Returns primary result without fallback
        Assert.False(result.Succeeded);
        Assert.True(result.ToolMissing);
        Assert.Contains("abipkgdiff executable not found", result.ErrorMessage);
    }

    [Fact]
    public async Task AbiEngineProbe_StrictEngineWithUnavailableTool_ThrowsInvalidOperationException()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true,
            StrictEngine = true,
            AbipkgdiffExecutablePath = "nonexistent-tool-strictly-required-999"
        });

        var logger = Substitute.For<ILogger<AbiEngineProbe>>();
        var probe = new AbiEngineProbe(options, logger);

        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AbiEngineProbe_StandardModeWithUnavailableTool_DoesNotThrow()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true,
            StrictEngine = false,
            AbipkgdiffExecutablePath = "nonexistent-tool-strictly-required-999"
        });

        var logger = Substitute.For<ILogger<AbiEngineProbe>>();
        var probe = new AbiEngineProbe(options, logger);

        await probe.StartAsync(CancellationToken.None);
        await probe.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AbiEngineProbe_StandardModeWithFallbackDisabled_LogsErrorAndDoesNotThrow()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true,
            StrictEngine = false,
            EnableFallback = false,
            AbipkgdiffExecutablePath = "nonexistent-tool-strictly-required-999"
        });

        var logger = Substitute.For<ILogger<AbiEngineProbe>>();
        var probe = new AbiEngineProbe(options, logger);

        await probe.StartAsync(CancellationToken.None);
        await probe.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AbiEngineProbe_Disabled_DoesNotProbe()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = false,
            StrictEngine = true,
            AbipkgdiffExecutablePath = "nonexistent-tool-strictly-required-999"
        });

        var logger = Substitute.For<ILogger<AbiEngineProbe>>();
        var probe = new AbiEngineProbe(options, logger);

        await probe.StartAsync(CancellationToken.None);
    }

    [Fact]
    public void InterpretExitCode_ExitCodeZeroWithRemovedBinariesInStdout_ReturnsBreakingChange()
    {
        const string stdout =
            "Removed binaries:\n" +
            "  [D] /usr/lib64/libdemo.so.1.0, SONAME: libdemo.so.1\n" +
            "Added binaries:\n" +
            "  [A] /usr/lib64/libdemo.so.2.0, SONAME: libdemo.so.2\n";

        var result = ProcessAbiDiffRunner.InterpretExitCode(0, stdout, string.Empty);

        Assert.True(result.Succeeded);
        Assert.True(result.HasAbiChange);
        Assert.True(result.IsBreakingChange);
        Assert.Equal(stdout, result.Details);
    }

    [Fact]
    public void InterpretExitCode_ExitCodeZeroWithAddedBinariesInStdout_ReturnsAbiChange()
    {
        const string stdout =
            "Added binaries:\n" +
            "  [A] /usr/lib64/libplugin.so.1.0, SONAME: libplugin.so.1\n";

        var result = ProcessAbiDiffRunner.InterpretExitCode(0, stdout, string.Empty);

        Assert.True(result.Succeeded);
        Assert.True(result.HasAbiChange);
        Assert.False(result.IsBreakingChange);
    }

    [Fact]
    public void InterpretExitCode_ExitCodeZeroWithBenignWarningStderr_ReturnsCleanSuccess()
    {
        const string warningStderr = "abipkgdiff: warning: could not find debug info file for /usr/lib64/libfoo.so.1";
        var result = ProcessAbiDiffRunner.InterpretExitCode(0, string.Empty, warningStderr);

        Assert.True(result.Succeeded);
        Assert.False(result.HasAbiChange);
        Assert.False(result.IsBreakingChange);
        Assert.Equal(warningStderr, result.Details);
    }

    [Fact]
    public void BuildStartInfo_IncludesPrivateDsoOption()
    {
        var startInfoWithDso = ProcessAbiDiffRunner.BuildStartInfo("abipkgdiff", "old.rpm", "new.rpm", privateDso: true);
        Assert.Contains("--private-dso", startInfoWithDso.ArgumentList);

        var startInfoWithoutDso = ProcessAbiDiffRunner.BuildStartInfo("abipkgdiff", "old.rpm", "new.rpm", privateDso: false);
        Assert.DoesNotContain("--private-dso", startInfoWithoutDso.ArgumentList);
    }

    [Fact]
    public void AbiEngineProbe_CreateMinimalProbeRpm_GeneratesValidRpmBytes()
    {
        var probeRpm = AbiEngineProbe.CreateMinimalProbeRpm();
        Assert.NotNull(probeRpm);
        Assert.True(probeRpm.Length > 96);
        // Verify RPM Lead Magic: 0xED 0xAB 0xEE 0xDB
        Assert.Equal(0xED, probeRpm[0]);
        Assert.Equal(0xAB, probeRpm[1]);
        Assert.Equal(0xEE, probeRpm[2]);
        Assert.Equal(0xDB, probeRpm[3]);
    }

    [Fact]
    public async Task AbiEngineProbe_ProbeNativeToolAsync_Cancelled_ReturnsFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (available, rpm, diagnostic) = await AbiEngineProbe.ProbeNativeToolAsync("dotnet", cts.Token);
        Assert.False(available);
        Assert.False(rpm);
        Assert.Contains("timed out", diagnostic);
    }

    [Fact]
    public void AbiEngineProbe_ProbeNativeTool_SynchronousWrapper_Works()
    {
        var (available, rpm, diagnostic) = AbiEngineProbe.ProbeNativeTool("nonexistent-tool-xyz-123");
        Assert.False(available);
        Assert.False(rpm);
        Assert.Contains("cannot be started", diagnostic);
    }

    [Fact]
    public async Task AbiEngineProbe_ProbeNativeToolAsync_WithValidHostBinary_ExecutesFullProbe()
    {
        var (available, rpm, diagnostic) = await AbiEngineProbe.ProbeNativeToolAsync("dotnet");
        Assert.True(available);
        Assert.True(rpm);
        Assert.Contains("abipkgdiff available", diagnostic);
    }
}
