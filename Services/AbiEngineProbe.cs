using System.Diagnostics;
using System.Globalization;
using System.Text;
using abaci_bot.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Probes the host system environment on application startup to verify ABI compliance engine capabilities.
/// In strict mode, refuses application startup if native tools are missing.
/// In standard mode, logs a clear capability diagnostic matrix and ensures fallback availability.
/// </summary>
public class AbiEngineProbe : IHostedService
{
    private static readonly char[] NewLineSeparators = ['\r', '\n'];
    private readonly AbiComplianceOptions _options;
    private readonly ILogger<AbiEngineProbe> _logger;

    public static bool NativeSupportsV6 { get; internal set; }

    public AbiEngineProbe(
        IOptions<AbiComplianceOptions> options,
        ILogger<AbiEngineProbe> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("ABI compliance engine: DISABLED in configuration.");
            return;
        }

        var (nativeAvailable, rpmSupport, diagnostic) = await ProbeNativeToolAsync(_options.AbipkgdiffExecutablePath, cancellationToken);

        if (nativeAvailable && rpmSupport)
        {
            _logger.LogInformation("ABI compliance engine: Tier 1 Native ({Path}) available with RPM support. Managed fallback enabled: {Fallback}.",
                _options.AbipkgdiffExecutablePath, _options.EnableFallback);
            return;
        }

        if (_options.StrictEngine)
        {
            var fatalMessage = $"Strict ABI engine mode is enabled, but native tool '{_options.AbipkgdiffExecutablePath}' is unavailable or lacks RPM support: {diagnostic}. Startup aborted.";
            _logger.LogCritical("{FatalMessage}", fatalMessage);
            throw new InvalidOperationException(fatalMessage);
        }

        if (_options.EnableFallback)
        {
            _logger.LogWarning("ABI compliance engine: Tier 1 Native unavailable ({Diagnostic}). Active engine: Tier 3 Pure Managed C# Fallback (RPM/Cpio/Zstd/GZip/XZ/BZip2/LZMA/ELF64).",
                diagnostic);
        }
        else
        {
            _logger.LogError("ABI compliance engine: Tier 1 Native unavailable ({Diagnostic}) and fallback is disabled. ABI checks will fail on PRs.",
                diagnostic);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static (bool NativeAvailable, bool RpmSupport, string Diagnostic) ProbeNativeTool(string executablePath)
    {
        return ProbeNativeToolAsync(executablePath).GetAwaiter().GetResult();
    }

    public static async Task<(bool NativeAvailable, bool RpmSupport, string Diagnostic)> ProbeNativeToolAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var ct = linkedCts.Token;

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            proc.Start();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            await proc.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (stderr.Contains("Support for rpm hasn't been enabled", StringComparison.OrdinalIgnoreCase))
            {
                return (true, false, "abipkgdiff exists but was compiled without RPM support");
            }

            // Probe package extraction capability using real RPM fixtures
            string? tempRpmPathV4 = null;
            string? tempRpmPathV6 = null;
            try
            {
                tempRpmPathV4 = Path.Combine(Path.GetTempPath(), $"probe_v4_{Guid.NewGuid():N}.rpm");
                await File.WriteAllBytesAsync(tempRpmPathV4, GetProbeRpmV4(), ct);

                using var dryRunProc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = executablePath,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                dryRunProc.StartInfo.ArgumentList.Add(tempRpmPathV4);
                dryRunProc.StartInfo.ArgumentList.Add(tempRpmPathV4);
                dryRunProc.Start();

                var dryStderrTask = dryRunProc.StandardError.ReadToEndAsync(ct);
                var dryStdoutTask = dryRunProc.StandardOutput.ReadToEndAsync(ct);
                await dryRunProc.WaitForExitAsync(ct);
                var dryStderr = await dryStderrTask;
                var dryStdout = await dryStdoutTask;

                var hasExplicitError =
                    dryStderr.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    dryStderr.Contains("Support for rpm hasn't been enabled", StringComparison.OrdinalIgnoreCase) ||
                    dryStderr.Contains("rpm2cpio: command not found", StringComparison.OrdinalIgnoreCase) ||
                    dryStderr.Contains("command not found", StringComparison.OrdinalIgnoreCase) ||
                    dryStderr.Contains("Error while extracting package", StringComparison.OrdinalIgnoreCase) ||
                    dryStderr.Contains("premature end of archive", StringComparison.OrdinalIgnoreCase);

                if (dryRunProc.ExitCode != 0 || hasExplicitError)
                {
                    NativeSupportsV6 = false;
                    if (dryStderr.Contains("Support for rpm hasn't been enabled", StringComparison.OrdinalIgnoreCase))
                    {
                        return (true, false, "abipkgdiff exists but was compiled without RPM support");
                    }

                    if (dryStderr.Contains("rpm2cpio: command not found", StringComparison.OrdinalIgnoreCase) ||
                        dryStderr.Contains("Error while extracting package", StringComparison.OrdinalIgnoreCase) ||
                        dryStderr.Contains("command not found", StringComparison.OrdinalIgnoreCase))
                    {
                        return (true, false, "abipkgdiff exists but host lacks rpm2cpio extraction tool");
                    }

                    var firstErrLine = dryStderr.Split(NewLineSeparators, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "unknown error";
                    return (true, false, $"abipkgdiff execution probe failed (exit code: {dryRunProc.ExitCode}): {firstErrLine}");
                }

                // Native v4 diff succeeded; now probe v6 capability
                try
                {
                    tempRpmPathV6 = Path.Combine(Path.GetTempPath(), $"probe_v6_{Guid.NewGuid():N}.rpm");
                    await File.WriteAllBytesAsync(tempRpmPathV6, GetProbeRpmV6(), ct);

                    using var v6Proc = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = executablePath,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };
                    v6Proc.StartInfo.ArgumentList.Add(tempRpmPathV6);
                    v6Proc.StartInfo.ArgumentList.Add(tempRpmPathV6);
                    v6Proc.Start();

                    var v6StderrTask = v6Proc.StandardError.ReadToEndAsync(ct);
                    var v6StdoutTask = v6Proc.StandardOutput.ReadToEndAsync(ct);
                    await v6Proc.WaitForExitAsync(ct);
                    var v6Stderr = await v6StderrTask;
                    var v6Stdout = await v6StdoutTask;

                    var hasV6Error =
                        v6Stderr.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                        v6Stderr.Contains("Malformed number", StringComparison.OrdinalIgnoreCase) ||
                        v6Stderr.Contains("07070X", StringComparison.OrdinalIgnoreCase) ||
                        v6Stderr.Contains("Error while extracting package", StringComparison.OrdinalIgnoreCase);

                    if (v6Proc.ExitCode == 0 && !hasV6Error)
                    {
                        NativeSupportsV6 = true;
                        return (true, true, $"abipkgdiff available with RPM v4 and v6 support ({stdout.Trim()})");
                    }
                    else
                    {
                        NativeSupportsV6 = false;
                        return (true, true, $"abipkgdiff available with RPM v4 support (RPM v6 native extraction unsupported, managed fallback active)");
                    }
                }
                catch
                {
                    NativeSupportsV6 = false;
                    return (true, true, $"abipkgdiff available with RPM v4 support (RPM v6 probe skipped, managed fallback active)");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                NativeSupportsV6 = false;
                return (true, false, $"abipkgdiff dry-run probe failed: {ex.Message}");
            }
            finally
            {
                if (tempRpmPathV4 != null)
                {
                    try { File.Delete(tempRpmPathV4); } catch { }
                }
                if (tempRpmPathV6 != null)
                {
                    try { File.Delete(tempRpmPathV6); } catch { }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return (false, false, $"executable '{executablePath}' timed out during capability probe");
        }
        catch (Exception ex)
        {
            return (false, false, $"executable '{executablePath}' cannot be started: {ex.Message}");
        }
    }

    internal static byte[] GetProbeRpmV4()
    {
        var asm = typeof(AbiEngineProbe).Assembly;
        using var stream = asm.GetManifestResourceStream("abaci_bot.Resources.fixtures.demo-lib-1.0-1.x86_64.rpm")
            ?? throw new InvalidOperationException("Embedded fixture demo-lib-1.0-1.x86_64.rpm not found");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    internal static byte[] GetProbeRpmV6()
    {
        var asm = typeof(AbiEngineProbe).Assembly;
        using var stream = asm.GetManifestResourceStream("abaci_bot.Resources.fixtures.v6demo-1.0-1.x86_64.rpm")
            ?? throw new InvalidOperationException("Embedded fixture v6demo-1.0-1.x86_64.rpm not found");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
