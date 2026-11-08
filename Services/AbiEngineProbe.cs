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
    private readonly AbiComplianceOptions _options;
    private readonly ILogger<AbiEngineProbe> _logger;

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
            _logger.LogWarning("ABI compliance engine: Tier 1 Native unavailable ({Diagnostic}). Active engine: Tier 3 Pure Managed C# Fallback (RPM/Cpio/Zstd/GZip/ELF64).",
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

            // Probe package extraction capability using minimal synthetic RPM self-diff
            string? tempRpmPath = null;
            try
            {
                tempRpmPath = Path.Combine(Path.GetTempPath(), $"probe_{Guid.NewGuid():N}.rpm");
                await File.WriteAllBytesAsync(tempRpmPath, CreateMinimalProbeRpm(), ct);

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
                dryRunProc.StartInfo.ArgumentList.Add(tempRpmPath);
                dryRunProc.StartInfo.ArgumentList.Add(tempRpmPath);
                dryRunProc.Start();

                var dryStderrTask = dryRunProc.StandardError.ReadToEndAsync(ct);
                await dryRunProc.WaitForExitAsync(ct);
                var dryStderr = await dryStderrTask;

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
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Probe self-diff error
            }
            finally
            {
                if (tempRpmPath != null)
                {
                    try { File.Delete(tempRpmPath); } catch { }
                }
            }

            return (true, true, $"abipkgdiff available ({stdout.Trim()})");
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

    internal static byte[] CreateMinimalProbeRpm()
    {
        using var ms = new MemoryStream();

        // 1. Lead: 96 bytes
        var lead = new byte[96];
        lead[0] = 0xED; lead[1] = 0xAB; lead[2] = 0xEE; lead[3] = 0xDB;
        ms.Write(lead);

        // 2. Signature Header: 16 bytes intro + 0 entries
        var sigIntro = new byte[16];
        sigIntro[0] = 0x8E; sigIntro[1] = 0xAD; sigIntro[2] = 0xE8; sigIntro[3] = 0x01;
        ms.Write(sigIntro);

        // 3. Main Header: 16 bytes intro + 0 entries
        var mainIntro = new byte[16];
        mainIntro[0] = 0x8E; mainIntro[1] = 0xAD; mainIntro[2] = 0xE8; mainIntro[3] = 0x01;
        ms.Write(mainIntro);

        // 4. Payload: GZip with empty CPIO archive (only TRAILER!!!)
        using var payloadMs = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(payloadMs, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            var trailerName = "TRAILER!!!\0";
            var trailerNameBytes = Encoding.ASCII.GetBytes(trailerName);
            var header = new StringBuilder();
            header.Append("070701");
            header.Append("00000000"); // ino
            header.Append("00000000"); // mode
            header.Append("00000000"); // uid
            header.Append("00000000"); // gid
            header.Append("00000001"); // nlink
            header.Append("00000000"); // mtime
            header.Append("00000000"); // filesize
            header.Append("00000000"); // maj
            header.Append("00000000"); // min
            header.Append("00000000"); // rmaj
            header.Append("00000000"); // rmin
            header.Append(trailerNameBytes.Length.ToString("X8", CultureInfo.InvariantCulture));
            header.Append("00000000"); // chksum

            var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
            gzip.Write(headerBytes);
            gzip.Write(trailerNameBytes);

            var namePad = (4 - ((110 + trailerNameBytes.Length) % 4)) % 4;
            if (namePad > 0) gzip.Write(new byte[namePad]);
        }

        ms.Write(payloadMs.ToArray());
        return ms.ToArray();
    }
}
