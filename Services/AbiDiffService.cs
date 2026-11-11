using System.Diagnostics;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Default runner that executes the system 'abipkgdiff' command-line binary.
/// Gracefully falls back if abipkgdiff is not installed or when running on non-Linux platforms.
/// </summary>
public class ProcessAbiDiffRunner : IAbiDiffRunner
{
    private readonly AbiComplianceOptions _options;
    private readonly ILogger<ProcessAbiDiffRunner>? _logger;
    private volatile bool _rpmSupportUnavailable;
    private string? _cachedRpmErrorMessage;

    internal bool IsRpmSupportUnavailable => _rpmSupportUnavailable;

    internal void MarkRpmSupportUnavailable(string reason)
    {
        _rpmSupportUnavailable = true;
        _cachedRpmErrorMessage = reason;
    }

    public ProcessAbiDiffRunner(
        IOptions<AbiComplianceOptions> options,
        ILogger<ProcessAbiDiffRunner>? logger = null)
    {
        _options = options.Value;
        _logger = logger;
    }

    private bool _privateDsoSupported = true;

    /// <summary>
    /// Builds the ProcessStartInfo configured to execute abipkgdiff.
    /// Exposes process construction for testing CLI contracts without starting external processes.
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(
        string executablePath,
        string oldRpmPath,
        string newRpmPath,
        bool privateDso = true,
        bool dsoOnly = true,
        string? oldDebuginfoPath = null,
        string? newDebuginfoPath = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (privateDso)
        {
            startInfo.ArgumentList.Add("--private-dso");
        }
        if (dsoOnly)
        {
            startInfo.ArgumentList.Add("--dso-only");
        }
        if (!string.IsNullOrWhiteSpace(oldDebuginfoPath))
        {
            startInfo.ArgumentList.Add("--d1");
            startInfo.ArgumentList.Add(oldDebuginfoPath);
        }
        if (!string.IsNullOrWhiteSpace(newDebuginfoPath))
        {
            startInfo.ArgumentList.Add("--d2");
            startInfo.ArgumentList.Add(newDebuginfoPath);
        }
        startInfo.ArgumentList.Add(oldRpmPath);
        startInfo.ArgumentList.Add(newRpmPath);
        return startInfo;
    }

    public Task<AbiDiffResult> RunDiffAsync(string oldRpmPath, string newRpmPath, CancellationToken ct = default)
        => RunDiffAsync(oldRpmPath, newRpmPath, null, null, ct);

    public async Task<AbiDiffResult> RunDiffAsync(
        string oldRpmPath,
        string newRpmPath,
        string? oldDebuginfoPath,
        string? newDebuginfoPath,
        CancellationToken ct = default)
    {
        if (_rpmSupportUnavailable)
        {
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: _cachedRpmErrorMessage ?? "abipkgdiff binary lacks RPM package support.",
                ErrorMessage: _cachedRpmErrorMessage ?? "abipkgdiff binary lacks RPM package support.",
                ToolMissing: true);
        }

        if (!File.Exists(oldRpmPath) || !File.Exists(newRpmPath))
        {
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: string.Empty,
                ErrorMessage: $"Target RPM file not found: old='{oldRpmPath}', new='{newRpmPath}'");
        }

        if (!string.IsNullOrWhiteSpace(oldDebuginfoPath) && !File.Exists(oldDebuginfoPath))
        {
            _logger?.LogWarning("Specified old debuginfo RPM was not found on disk: {Path}", oldDebuginfoPath);
        }
        if (!string.IsNullOrWhiteSpace(newDebuginfoPath) && !File.Exists(newDebuginfoPath))
        {
            _logger?.LogWarning("Specified new debuginfo RPM was not found on disk: {Path}", newDebuginfoPath);
        }

        var effectiveOldDebug = !string.IsNullOrWhiteSpace(oldDebuginfoPath) && File.Exists(oldDebuginfoPath) ? oldDebuginfoPath : null;
        var effectiveNewDebug = !string.IsNullOrWhiteSpace(newDebuginfoPath) && File.Exists(newDebuginfoPath) ? newDebuginfoPath : null;

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds > 0 ? _options.TimeoutSeconds : 300));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var effectiveCt = linkedCts.Token;

        Process? process = null;
        try
        {
            var usePrivateDso = _privateDsoSupported;
            var startInfo = BuildStartInfo(_options.AbipkgdiffExecutablePath, oldRpmPath, newRpmPath, usePrivateDso, dsoOnly: true, effectiveOldDebug, effectiveNewDebug);

            process = new Process { StartInfo = startInfo };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(effectiveCt);
            var stderrTask = process.StandardError.ReadToEndAsync(effectiveCt);

            await process.WaitForExitAsync(effectiveCt);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (usePrivateDso && (process.ExitCode & 2) != 0 &&
                (stderr.Contains("private-dso", StringComparison.OrdinalIgnoreCase) || stderr.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase)))
            {
                _privateDsoSupported = false;
                _logger?.LogWarning("abipkgdiff executable does not support --private-dso; retrying without this option");
                process.Dispose();

                startInfo = BuildStartInfo(_options.AbipkgdiffExecutablePath, oldRpmPath, newRpmPath, privateDso: false, dsoOnly: true, effectiveOldDebug, effectiveNewDebug);
                process = new Process { StartInfo = startInfo };
                process.Start();

                stdoutTask = process.StandardOutput.ReadToEndAsync(effectiveCt);
                stderrTask = process.StandardError.ReadToEndAsync(effectiveCt);

                await process.WaitForExitAsync(effectiveCt);
                stdout = await stdoutTask;
                stderr = await stderrTask;
            }

            var exitCode = process.ExitCode;
            var result = InterpretExitCode(exitCode, stdout, stderr);
            if (result.ToolMissing && result.ErrorMessage?.Contains("rpm", StringComparison.OrdinalIgnoreCase) == true)
            {
                _rpmSupportUnavailable = true;
                _cachedRpmErrorMessage = result.ErrorMessage;
                _logger?.LogError("abipkgdiff on host system lacks RPM support. Short-circuiting future diffs: {Error}", result.ErrorMessage);
            }
            return result;
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

            _logger?.LogWarning("abipkgdiff process timed out after {Timeout} seconds.", _options.TimeoutSeconds);
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: string.Empty,
                ErrorMessage: $"abipkgdiff process timed out after {_options.TimeoutSeconds} seconds.");
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
            _logger?.LogError(ex, "Libabigail abipkgdiff executable '{Path}' not found on host system. ABI compliance checks cannot run.", _options.AbipkgdiffExecutablePath);
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: string.Empty,
                ErrorMessage: $"abipkgdiff executable not found: {ex.Message}",
                ToolMissing: true);
        }
        catch (FileNotFoundException ex)
        {
            _logger?.LogError(ex, "Libabigail abipkgdiff executable '{Path}' not found on host system. ABI compliance checks cannot run.", _options.AbipkgdiffExecutablePath);
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: string.Empty,
                ErrorMessage: $"abipkgdiff executable not found: {ex.Message}",
                ToolMissing: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to run abipkgdiff executable '{Path}'. Skipping ABI analysis.", _options.AbipkgdiffExecutablePath);
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: string.Empty,
                ErrorMessage: $"abipkgdiff execution failed: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// Interprets the bitmask return code from Libabigail abipkgdiff / abidiff.
    /// Prioritizes stderr capability checks (e.g. missing RPM support) before bitmask evaluation
    /// to accommodate differences across Linux distributions (such as exit code 1 on Ubuntu).
    /// </summary>
    public static AbiDiffResult InterpretExitCode(int exitCode, string stdout, string stderr = "")
    {
        // Libabigail abipkgdiff return code bitmask:
        // 0 = no change
        // 1 = error (ABIDIFF_ERROR / ABIPKGDIFF_ERROR)
        // 2 = usage error (ABIDIFF_USAGE_ERROR)
        // 4 = ABI change detected (ABIDIFF_ABI_CHANGE)
        // 8 = ABI incompatible / breaking change (ABIDIFF_ABI_INCOMPATIBLE_CHANGE)
        const int abidiffError = 1;
        const int abidiffUsage = 2;
        const int abidiffChange = 4;
        const int abidiffIncompatible = 8;

        // 1. Inspect stderr for missing tools / extraction failure / crashes first.
        // Ubuntu 22.04 exits with code 1 when libabigail lacks RPM support.
        // Ubuntu 24.04 exits with code 0 when rpm2cpio is missing, writing:
        // "rpm2cpio: command not found", "cpio: premature end of archive", "abipkgdiff: Error while extracting package".
        // Fatal aborts (e.g. exit code 134 SIGABRT when $HOME is missing) are also classified as tool/environment unavailable.
        // Identifying these as tool/engine missing allows automatic fallback to the managed ELF runner.
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            if (stderr.Contains("Support for rpm hasn't been enabled", StringComparison.OrdinalIgnoreCase))
            {
                return new AbiDiffResult(
                    Succeeded: false,
                    HasAbiChange: false,
                    IsBreakingChange: false,
                    Details: stderr.Trim(),
                    ErrorMessage: "abipkgdiff reported missing RPM support: Support for rpm hasn't been enabled.",
                    ToolMissing: true);
            }

            var isToolOrExtractionMissing =
                stderr.Contains("rpm2cpio: command not found", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("command not found", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("Error while extracting package", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("premature end of archive", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("Malformed number", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("07070X", StringComparison.OrdinalIgnoreCase);

            if (isToolOrExtractionMissing)
            {
                return new AbiDiffResult(
                    Succeeded: false,
                    HasAbiChange: false,
                    IsBreakingChange: false,
                    Details: stderr.Trim(),
                    ErrorMessage: $"abipkgdiff reported missing tool or package extraction failure: {stderr.Trim()}",
                    ToolMissing: true);
            }

            // Fatal process crashes or environment errors (e.g. missing home directory causing mkdir failure and abort)
            var isCrashOrEnvironmentFailure =
                exitCode >= 128 ||
                stderr.Contains("Aborted", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("cannot create directory", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("Assertion `__abg_cond__'", StringComparison.OrdinalIgnoreCase);

            if (isCrashOrEnvironmentFailure)
            {
                return new AbiDiffResult(
                    Succeeded: false,
                    HasAbiChange: false,
                    IsBreakingChange: false,
                    Details: stderr.Trim(),
                    ErrorMessage: $"abipkgdiff process crashed or aborted (exit code: {exitCode}): {stderr.Trim()}",
                    ToolMissing: true);
            }

            // Explicit input file error (e.g. source RPM or missing file); must NOT fallback to avoid false-breaking reports
            var isInputError =
                stderr.Contains("should be an RPM file", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("The input file", StringComparison.OrdinalIgnoreCase);

            if (isInputError)
            {
                return new AbiDiffResult(
                    Succeeded: false,
                    HasAbiChange: false,
                    IsBreakingChange: false,
                    Details: stderr.Trim(),
                    ErrorMessage: $"abipkgdiff reported input error (exit code: {exitCode}): {stderr.Trim()}",
                    ToolMissing: false);
            }
        }
        else if (exitCode >= 128)
        {
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: string.Empty,
                ErrorMessage: $"abipkgdiff process terminated by signal (exit code: {exitCode})",
                ToolMissing: true);
        }

        // 2. Handle execution errors and usage errors
        if ((exitCode & abidiffError) != 0 || (exitCode & abidiffUsage) != 0)
        {
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: string.IsNullOrWhiteSpace(stdout) ? stderr : stdout,
                ErrorMessage: $"abipkgdiff reported tool failure (exit code: {exitCode})");
        }

        // 3. Handle zero exit code with non-empty stderr
        if (exitCode == 0 && !string.IsNullOrWhiteSpace(stderr))
        {
            var hasExplicitError =
                stderr.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("fatal", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("fail", StringComparison.OrdinalIgnoreCase);

            if (hasExplicitError)
            {
                var msg = string.IsNullOrWhiteSpace(stdout)
                    ? $"abipkgdiff reported error on stderr: produced no comparison output ({stderr.Trim()})"
                    : $"abipkgdiff reported error on stderr: {stderr.Trim()}";

                return new AbiDiffResult(
                    Succeeded: false,
                    HasAbiChange: false,
                    IsBreakingChange: false,
                    Details: stderr.Trim(),
                    ErrorMessage: msg);
            }
        }

        var hasChange = (exitCode & (abidiffChange | abidiffIncompatible)) != 0;
        var isBreaking = (exitCode & abidiffIncompatible) != 0;

        // Inspect stdout for SONAME changes, removed binaries, and added binaries.
        // Libabigail abipkgdiff exits with code 0 on removed or added binaries,
        // but removes are breaking changes and additions are ABI changes.
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            if (stdout.Contains("Removed binaries:", StringComparison.OrdinalIgnoreCase))
            {
                isBreaking = true;
                hasChange = true;
            }
            else if (stdout.Contains("Added binaries:", StringComparison.OrdinalIgnoreCase))
            {
                hasChange = true;
            }
        }

        return new AbiDiffResult(
            Succeeded: true,
            HasAbiChange: hasChange,
            IsBreakingChange: isBreaking,
            Details: string.IsNullOrWhiteSpace(stdout) ? stderr : stdout);
    }
}

/// <summary>
/// Deep module coordinating binary ABI comparison and reporting.
/// Transparently handles native abipkgdiff execution and falls back to
/// managed ELF/RPM symbol analysis when native tools or RPM support are unavailable.
/// </summary>
public class AbiDiffService : IAbiDiffService
{
    private readonly IAbiDiffRunner _runner;
    private readonly IAbiDiffRunner? _fallbackRunner;
    private readonly AbiComplianceOptions _options;
    private readonly ILogger<AbiDiffService>? _logger;

    public AbiDiffService(
        IAbiDiffRunner runner,
        IOptions<AbiComplianceOptions> options,
        ILogger<AbiDiffService>? logger = null)
        : this(runner, null, options, logger)
    {
    }

    public AbiDiffService(
        IAbiDiffRunner runner,
        ManagedElfDiffRunner? fallbackRunner,
        IOptions<AbiComplianceOptions> options,
        ILogger<AbiDiffService>? logger = null)
    {
        _runner = runner;
        _fallbackRunner = fallbackRunner;
        _options = options.Value;
        _logger = logger;
    }

    public Task<AbiDiffResult> CompareRpmsAsync(string oldRpmPath, string newRpmPath, CancellationToken ct = default)
        => CompareRpmsAsync(oldRpmPath, newRpmPath, null, null, ct);

    public async Task<AbiDiffResult> CompareRpmsAsync(
        string oldRpmPath,
        string newRpmPath,
        string? oldDebuginfoPath,
        string? newDebuginfoPath,
        CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: "ABI compliance scanning is disabled in configuration.");
        }

        // Check if either RPM is v6 (lead major version >= 4) and native engine is not verified for v6 support
        var isV6Package = IsRpmV6(oldRpmPath) || IsRpmV6(newRpmPath);
        if (isV6Package && !AbiEngineProbe.NativeSupportsV6 && _options.EnableFallback && _fallbackRunner != null)
        {
            _logger?.LogInformation("Package is RPM v6 and native runner lacks v6 support. Falling back to managed ELF symbol diff runner directly.");
            return await _fallbackRunner.RunDiffAsync(oldRpmPath, newRpmPath, ct);
        }

        var result = (!string.IsNullOrWhiteSpace(oldDebuginfoPath) || !string.IsNullOrWhiteSpace(newDebuginfoPath))
            ? await _runner.RunDiffAsync(oldRpmPath, newRpmPath, oldDebuginfoPath, newDebuginfoPath, ct)
            : await _runner.RunDiffAsync(oldRpmPath, newRpmPath, ct);

        // Fall back to managed ELF runner if primary runner lacks tool or RPM capability
        if (_options.EnableFallback && _fallbackRunner != null && (!result.Succeeded && result.ToolMissing))
        {
            _logger?.LogInformation("Primary ABI runner unavailable ({Reason}). Falling back to managed ELF symbol diff runner.", result.ErrorMessage);
            var fallbackResult = await _fallbackRunner.RunDiffAsync(oldRpmPath, newRpmPath, ct);
            return fallbackResult;
        }

        return result;
    }

    /// <summary>
    /// Inspects the RPM package lead to determine if it is formatted according to RPM v6 specifications (lead major >= 4).
    /// Returns false if the file is missing, inaccessible, or not a verified RPM v6 package, delegating comprehensive
    /// file validation and I/O error logging to the downstream diff pipeline.
    /// </summary>
    private static bool IsRpmV6(string rpmPath)
    {
        try
        {
            if (!File.Exists(rpmPath)) return false;
            using var fs = new FileStream(rpmPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16);
            var header = new byte[5];
            var read = fs.Read(header, 0, 5);
            return read >= 5 && header[0] == 0xED && header[1] == 0xAB && header[2] == 0xEE && header[3] == 0xDB && header[4] >= 4;
        }
        catch
        {
            return false;
        }
    }
}
