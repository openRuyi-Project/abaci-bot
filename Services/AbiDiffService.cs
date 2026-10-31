using System.Diagnostics;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Default runner that executes the system 'abidiff' command-line binary.
/// Gracefully falls back if abidiff is not installed or when running on non-Linux platforms.
/// </summary>
public class ProcessAbiDiffRunner : IAbiDiffRunner
{
    private readonly AbiComplianceOptions _options;
    private readonly ILogger<ProcessAbiDiffRunner>? _logger;

    public ProcessAbiDiffRunner(
        IOptions<AbiComplianceOptions> options,
        ILogger<ProcessAbiDiffRunner>? logger = null)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AbiDiffResult> RunDiffAsync(string oldRpmPath, string newRpmPath, CancellationToken ct = default)
    {
        if (!File.Exists(oldRpmPath) || !File.Exists(newRpmPath))
        {
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: string.Empty,
                ErrorMessage: $"Target RPM file not found: old='{oldRpmPath}', new='{newRpmPath}'");
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds > 0 ? _options.TimeoutSeconds : 300));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var effectiveCt = linkedCts.Token;

        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _options.AbidiffExecutablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(oldRpmPath);
            startInfo.ArgumentList.Add(newRpmPath);

            process = new Process { StartInfo = startInfo };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(effectiveCt);
            var stderrTask = process.StandardError.ReadToEndAsync(effectiveCt);

            await process.WaitForExitAsync(effectiveCt);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var exitCode = process.ExitCode;
            return InterpretExitCode(exitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore kill errors
            }

            _logger?.LogWarning("abidiff process timed out after {Timeout} seconds.", _options.TimeoutSeconds);
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: string.Empty,
                ErrorMessage: $"abidiff process timed out after {_options.TimeoutSeconds} seconds.");
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore kill errors
            }
            throw;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger?.LogError(ex, "Libabigail abidiff executable '{Path}' not found on host system. ABI compliance checks cannot run.", _options.AbidiffExecutablePath);
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: string.Empty,
                ErrorMessage: $"abidiff executable not found: {ex.Message}",
                ToolMissing: true);
        }
        catch (FileNotFoundException ex)
        {
            _logger?.LogError(ex, "Libabigail abidiff executable '{Path}' not found on host system. ABI compliance checks cannot run.", _options.AbidiffExecutablePath);
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: string.Empty,
                ErrorMessage: $"abidiff executable not found: {ex.Message}",
                ToolMissing: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to run abidiff executable '{Path}'. Skipping ABI analysis.", _options.AbidiffExecutablePath);
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: string.Empty,
                ErrorMessage: $"abidiff execution failed: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// Interprets the bitmask return code from Libabigail abidiff.
    /// </summary>
    public static AbiDiffResult InterpretExitCode(int exitCode, string stdout, string stderr = "")
    {
        // Libabigail abidiff return code bitmask:
        // 0 = no change
        // 1 = error (ABIDIFF_ERROR)
        // 2 = usage error (ABIDIFF_USAGE_ERROR)
        // 4 = ABI change detected (ABIDIFF_ABI_CHANGE)
        // 8 = ABI incompatible / breaking change (ABIDIFF_ABI_INCOMPATIBLE_CHANGE)
        const int abidiffError = 1;
        const int abidiffUsage = 2;
        const int abidiffChange = 4;
        const int abidiffIncompatible = 8;

        if ((exitCode & abidiffError) != 0 || (exitCode & abidiffUsage) != 0)
        {
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: string.IsNullOrWhiteSpace(stdout) ? stderr : stdout,
                ErrorMessage: $"abidiff reported tool failure (exit code: {exitCode})");
        }

        var hasChange = (exitCode & (abidiffChange | abidiffIncompatible)) != 0;
        var isBreaking = (exitCode & abidiffIncompatible) != 0;

        return new AbiDiffResult(
            Succeeded: true,
            HasAbiChange: hasChange,
            IsBreakingChange: isBreaking,
            Details: string.IsNullOrWhiteSpace(stdout) ? stderr : stdout);
    }
}

/// <summary>
/// Deep module coordinating binary ABI comparison and reporting.
/// </summary>
public class AbiDiffService : IAbiDiffService
{
    private readonly IAbiDiffRunner _runner;
    private readonly AbiComplianceOptions _options;
    private readonly ILogger<AbiDiffService>? _logger;

    public AbiDiffService(
        IAbiDiffRunner runner,
        IOptions<AbiComplianceOptions> options,
        ILogger<AbiDiffService>? logger = null)
    {
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AbiDiffResult> CompareRpmsAsync(string oldRpmPath, string newRpmPath, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: "ABI compliance scanning is disabled in configuration.");
        }

        return await _runner.RunDiffAsync(oldRpmPath, newRpmPath, ct);
    }
}
