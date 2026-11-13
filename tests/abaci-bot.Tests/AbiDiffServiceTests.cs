using abaci_bot.Contexts;
using abaci_bot.Modules;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task AbiDiffService_WithDebuginfo_DelegatesToDebuginfoRunnerOverload()
    {
        var runner = Substitute.For<IAbiDiffRunner>();
        runner.RunDiffAsync("old.rpm", "new.rpm", "old-dbg.rpm", "new-dbg.rpm", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AbiDiffResult(
                Succeeded: true,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: "Clean with DWARF")));

        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true
        });
        var service = new AbiDiffService(runner, options);

        var result = await service.CompareRpmsAsync("old.rpm", "new.rpm", "old-dbg.rpm", "new-dbg.rpm");

        Assert.True(result.Succeeded);
        Assert.Equal("Clean with DWARF", result.Details);
        await runner.Received(1).RunDiffAsync("old.rpm", "new.rpm", "old-dbg.rpm", "new-dbg.rpm", Arg.Any<CancellationToken>());
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
        Assert.Equal(4, startInfo.ArgumentList.Count);
        Assert.Equal("--private-dso", startInfo.ArgumentList[0]);
        Assert.Equal("--dso-only", startInfo.ArgumentList[1]);
        Assert.Equal("/tmp/old.rpm", startInfo.ArgumentList[2]);
        Assert.Equal("/tmp/new.rpm", startInfo.ArgumentList[3]);
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
    public void InterpretExitCode_ExitCode134AbortCrash_ReturnsToolMissingFailure()
    {
        const string abortStderr =
            "mkdir: cannot create directory '/home/abaci': Permission denied\n" +
            "abipkgdiff: ../../tools/abipkgdiff.cc:811: package::extracted_packages_parent_dir(): Assertion `__abg_cond__' failed.\n" +
            "Aborted (core dumped)";

        var result = ProcessAbiDiffRunner.InterpretExitCode(134, "", abortStderr);

        Assert.False(result.Succeeded);
        Assert.True(result.ToolMissing);
        Assert.Contains("crashed or aborted", result.ErrorMessage);
    }

    [Fact]
    public void InterpretExitCode_ExitCode137OomKilledWithoutAbortKeyword_ReturnsToolMissingFailure()
    {
        // Tests pure exitCode >= 128 branch without any "Aborted" or "Assertion" keywords in stderr
        const string killedStderr = "Killed\n";
        var result = ProcessAbiDiffRunner.InterpretExitCode(137, "", killedStderr);

        Assert.False(result.Succeeded);
        Assert.True(result.ToolMissing);
        Assert.Contains("crashed or aborted (exit code: 137)", result.ErrorMessage);
    }

    [Fact]
    public void InterpretExitCode_ExitCode3InputError_ReturnsFailureWithoutToolMissing()
    {
        const string inputErrorStderr = "abipkgdiff: Catch2-3.14.0-999.or.src.rpm should be an RPM file";

        var result = ProcessAbiDiffRunner.InterpretExitCode(3, "", inputErrorStderr);

        Assert.False(result.Succeeded);
        Assert.False(result.ToolMissing); // Must NOT trigger fallback on invalid input
        Assert.Contains("input error", result.ErrorMessage);
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
    public async Task AbiDiffService_PrimaryRunnerToolMissingWithDebuginfo_PassesDebuginfoToFallbackRunner()
    {
        var primaryRunner = Substitute.For<IAbiDiffRunner>();
        primaryRunner.RunDiffAsync("old.rpm", "new.rpm", "old-dbg.rpm", "new-dbg.rpm", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: "Missing tool",
                ErrorMessage: "abipkgdiff executable not found",
                ToolMissing: true)));

        var fallbackRunner = Substitute.For<IAbiDiffRunner>();
        fallbackRunner.RunDiffAsync("old.rpm", "new.rpm", "old-dbg.rpm", "new-dbg.rpm", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AbiDiffResult(
                Succeeded: true,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: "Managed symbol diff clean",
                IsIncomplete: true)));

        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true,
            EnableFallback = true
        });

        var service = new AbiDiffService(primaryRunner, fallbackRunner, options);

        var result = await service.CompareRpmsAsync("old.rpm", "new.rpm", "old-dbg.rpm", "new-dbg.rpm");

        Assert.True(result.Succeeded);
        Assert.True(result.IsIncomplete);
        await fallbackRunner.Received(1).RunDiffAsync("old.rpm", "new.rpm", "old-dbg.rpm", "new-dbg.rpm", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void DependencyInjection_ResolvesAbiDiffServiceWithManagedFallback()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddOptions();
        services.AddLogging();
        services.AddSingleton<ManagedElfDiffRunner>();
        services.AddSingleton<ProcessAbiDiffRunner>();
        services.AddSingleton<IAbiDiffRunner>(sp => sp.GetRequiredService<ProcessAbiDiffRunner>());
        services.AddSingleton<IAbiDiffService>(sp =>
            new AbiDiffService(
                sp.GetRequiredService<ProcessAbiDiffRunner>(),
                sp.GetService<ManagedElfDiffRunner>(),
                sp.GetRequiredService<IOptions<AbiComplianceOptions>>(),
                sp.GetService<ILogger<AbiDiffService>>()));

        using var sp = services.BuildServiceProvider();
        var service = sp.GetRequiredService<IAbiDiffService>() as AbiDiffService;

        Assert.NotNull(service);
        Assert.IsType<ProcessAbiDiffRunner>(service.PrimaryRunner);
        Assert.IsType<ManagedElfDiffRunner>(service.FallbackRunner);
    }

    [Fact]
    public async Task AbiEngineProbe_MissingNativeWithoutAllowManagedFallback_ThrowsInvalidOperationException()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true,
            AbipkgdiffExecutablePath = "nonexistent-abipkgdiff-tool-xyz",
            EnableFallback = true,
            AllowManagedFallback = false
        });
        var logger = Substitute.For<ILogger<AbiEngineProbe>>();
        var probe = new AbiEngineProbe(options, logger);

        var prevEnv = Environment.GetEnvironmentVariable("ABACI_ALLOW_MANAGED_FALLBACK");
        try
        {
            Environment.SetEnvironmentVariable("ABACI_ALLOW_MANAGED_FALLBACK", null);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => probe.StartAsync(CancellationToken.None));
            Assert.Contains("unavailable or lacks RPM support", ex.Message);
            Assert.Contains("AbiCompliance:AllowManagedFallback=true", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ABACI_ALLOW_MANAGED_FALLBACK", prevEnv);
        }
    }

    [Fact]
    public async Task AbiEngineProbe_MissingNativeWithAllowManagedFallback_SetsActiveEngineTierManaged()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true,
            AbipkgdiffExecutablePath = "nonexistent-abipkgdiff-tool-xyz",
            EnableFallback = true,
            AllowManagedFallback = true
        });
        var logger = Substitute.For<ILogger<AbiEngineProbe>>();
        var probe = new AbiEngineProbe(options, logger);

        await probe.StartAsync(CancellationToken.None);

        Assert.Equal("managed", AbiEngineProbe.ActiveEngineTier);
    }

    [Fact]
    public async Task AbiEngineProbe_MissingNativeWithEnvOverride_SetsActiveEngineTierManaged()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true,
            AbipkgdiffExecutablePath = "nonexistent-abipkgdiff-tool-xyz",
            EnableFallback = true,
            AllowManagedFallback = false
        });
        var logger = Substitute.For<ILogger<AbiEngineProbe>>();
        var probe = new AbiEngineProbe(options, logger);

        var prevEnv = Environment.GetEnvironmentVariable("ABACI_ALLOW_MANAGED_FALLBACK");
        try
        {
            Environment.SetEnvironmentVariable("ABACI_ALLOW_MANAGED_FALLBACK", "1");
            await probe.StartAsync(CancellationToken.None);
            Assert.Equal("managed", AbiEngineProbe.ActiveEngineTier);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ABACI_ALLOW_MANAGED_FALLBACK", prevEnv);
        }
    }

    [Fact]
    public async Task AbiEngineProbe_Disabled_SetsActiveEngineTierDisabled()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = false
        });
        var logger = Substitute.For<ILogger<AbiEngineProbe>>();
        var probe = new AbiEngineProbe(options, logger);

        await probe.StartAsync(CancellationToken.None);

        Assert.Equal("disabled", AbiEngineProbe.ActiveEngineTier);
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
            AllowManagedFallback = true,
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
            AllowManagedFallback = true,
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
    public void BuildStartInfo_IncludesPrivateDsoAndDsoOnlyAndDebugOptions()
    {
        var startInfoWithDso = ProcessAbiDiffRunner.BuildStartInfo("abipkgdiff", "old.rpm", "new.rpm", privateDso: true, dsoOnly: true);
        Assert.Contains("--private-dso", startInfoWithDso.ArgumentList);
        Assert.Contains("--dso-only", startInfoWithDso.ArgumentList);

        var startInfoWithoutDso = ProcessAbiDiffRunner.BuildStartInfo("abipkgdiff", "old.rpm", "new.rpm", privateDso: false, dsoOnly: false);
        Assert.DoesNotContain("--private-dso", startInfoWithoutDso.ArgumentList);
        Assert.DoesNotContain("--dso-only", startInfoWithoutDso.ArgumentList);

        var startInfoWithDebug = ProcessAbiDiffRunner.BuildStartInfo(
            "abipkgdiff", "old.rpm", "new.rpm",
            privateDso: true, dsoOnly: true,
            oldDebuginfoPath: "old-debug.rpm", newDebuginfoPath: "new-debug.rpm");
        Assert.Contains("--d1", startInfoWithDebug.ArgumentList);
        Assert.Contains("old-debug.rpm", startInfoWithDebug.ArgumentList);
        Assert.Contains("--d2", startInfoWithDebug.ArgumentList);
        Assert.Contains("new-debug.rpm", startInfoWithDebug.ArgumentList);

        var startInfoSingleOld = ProcessAbiDiffRunner.BuildStartInfo(
            "abipkgdiff", "old.rpm", "new.rpm",
            oldDebuginfoPath: "old-debug.rpm", newDebuginfoPath: null);
        Assert.Contains("--d1", startInfoSingleOld.ArgumentList);
        Assert.DoesNotContain("--d2", startInfoSingleOld.ArgumentList);

        var startInfoSingleNew = ProcessAbiDiffRunner.BuildStartInfo(
            "abipkgdiff", "old.rpm", "new.rpm",
            oldDebuginfoPath: null, newDebuginfoPath: "new-debug.rpm");
        Assert.DoesNotContain("--d1", startInfoSingleNew.ArgumentList);
        Assert.Contains("--d2", startInfoSingleNew.ArgumentList);
    }

    [Fact]
    public void AbiEngineProbe_GetProbeRpmV4AndV6_ReturnsValidRpmBytes()
    {
        var probeRpmV4 = AbiEngineProbe.GetProbeRpmV4();
        Assert.NotNull(probeRpmV4);
        Assert.True(probeRpmV4.Length > 96);
        Assert.Equal(0xED, probeRpmV4[0]);
        Assert.Equal(0xAB, probeRpmV4[1]);
        Assert.Equal(0xEE, probeRpmV4[2]);
        Assert.Equal(0xDB, probeRpmV4[3]);
        Assert.Equal(3, probeRpmV4[4]); // RPM v4 major version in lead is 3

        var probeRpmV6 = AbiEngineProbe.GetProbeRpmV6();
        Assert.NotNull(probeRpmV6);
        Assert.True(probeRpmV6.Length > 96);
        Assert.Equal(0xED, probeRpmV6[0]);
        Assert.Equal(0xAB, probeRpmV6[1]);
        Assert.Equal(0xEE, probeRpmV6[2]);
        Assert.Equal(0xDB, probeRpmV6[3]);
        Assert.Equal(4, probeRpmV6[4]); // RPM v6 major version in lead is 4
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
    public async Task AbiEngineProbe_ProbeNativeToolAsync_WithNonAbipkgdiffBinary_ReportsRpmSupportUnavailable()
    {
        var (available, rpm, diagnostic) = await AbiEngineProbe.ProbeNativeToolAsync("dotnet");
        Assert.True(available);
        Assert.False(rpm); // dotnet does not support abipkgdiff RPM self-diff!
        Assert.Contains("probe failed", diagnostic);
    }

    private static string CreateFakeExecutableScript(string windowsBatchContent, string unixShellContent)
    {
        var isWindows = OperatingSystem.IsWindows();
        var scriptPath = Path.Combine(Path.GetTempPath(), $"fake_tool_{Guid.NewGuid():N}" + (isWindows ? ".cmd" : ".sh"));
        if (isWindows)
        {
            File.WriteAllText(scriptPath, windowsBatchContent);
        }
        else
        {
            File.WriteAllText(scriptPath, unixShellContent);
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return scriptPath;
    }

    [Fact]
    public async Task AbiEngineProbe_NativeToolWithV6Support_DetectsSuccessfully()
    {
        var originalV6 = AbiEngineProbe.NativeSupportsV6;
        var scriptPath = CreateFakeExecutableScript(
            "@echo off\r\nif \"%1\"==\"--version\" (echo abipkgdiff 2.4 & exit /b 0)\r\nexit /b 0\r\n",
            "#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then\n  echo \"abipkgdiff 2.4\"\n  exit 0\nfi\nexit 0\n");

        try
        {
            var (available, rpm, diagnostic) = await AbiEngineProbe.ProbeNativeToolAsync(scriptPath);
            Assert.True(available);
            Assert.True(rpm);
            Assert.True(AbiEngineProbe.NativeSupportsV6);
            Assert.Contains("v4 and v6 support", diagnostic);

            var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
            {
                Enabled = true,
                AbipkgdiffExecutablePath = scriptPath
            });
            var logger = Substitute.For<ILogger<AbiEngineProbe>>();
            var probe = new AbiEngineProbe(options, logger);
            await probe.StartAsync(CancellationToken.None);
        }
        finally
        {
            AbiEngineProbe.NativeSupportsV6 = originalV6;
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task AbiEngineProbe_NativeToolWithoutRpmSupport_DetectsDisabledRpm()
    {
        var scriptPath = CreateFakeExecutableScript(
            "@echo off\r\necho Support for rpm hasn't been enabled 1>&2\r\nexit /b 1\r\n",
            "#!/bin/sh\necho \"Support for rpm hasn't been enabled\" >&2\nexit 1\n");

        try
        {
            var (available, rpm, diagnostic) = await AbiEngineProbe.ProbeNativeToolAsync(scriptPath);
            Assert.True(available);
            Assert.False(rpm);
            Assert.Contains("without RPM support", diagnostic);
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task AbiEngineProbe_NativeToolMissingRpm2Cpio_DetectsMissingExtractionTool()
    {
        var scriptPath = CreateFakeExecutableScript(
            "@echo off\r\nif \"%1\"==\"--version\" (echo abipkgdiff 2.4 & exit /b 0)\r\necho rpm2cpio: command not found 1>&2\r\nexit /b 127\r\n",
            "#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then\n  echo \"abipkgdiff 2.4\"\n  exit 0\nfi\necho \"rpm2cpio: command not found\" >&2\nexit 127\n");

        try
        {
            var (available, rpm, diagnostic) = await AbiEngineProbe.ProbeNativeToolAsync(scriptPath);
            Assert.True(available);
            Assert.False(rpm);
            Assert.Contains("lacks rpm2cpio extraction tool", diagnostic);
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task ProcessAbiDiffRunner_NonExistentDebuginfoPaths_LogsWarning()
    {
        var tempOld = Path.GetTempFileName();
        var tempNew = Path.GetTempFileName();
        var logger = Substitute.For<ILogger<ProcessAbiDiffRunner>>();
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            AbipkgdiffExecutablePath = "nonexistent-abipkgdiff-tool"
        });
        var runner = new ProcessAbiDiffRunner(options, logger);

        try
        {
            var result = await runner.RunDiffAsync(tempOld, tempNew, "missing-old-debug.rpm", "missing-new-debug.rpm");

            logger.Received().Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("missing-old-debug.rpm")),
                Arg.Any<Exception?>(),
                Arg.Any<Func<object, Exception?, string>>());

            logger.Received().Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("missing-new-debug.rpm")),
                Arg.Any<Exception?>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            if (File.Exists(tempOld)) File.Delete(tempOld);
            if (File.Exists(tempNew)) File.Delete(tempNew);
        }
    }
}
