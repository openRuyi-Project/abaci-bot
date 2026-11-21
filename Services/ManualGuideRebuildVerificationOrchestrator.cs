using abaci_bot.Options;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Interim <see cref="IRebuildVerificationOrchestrator"/> implementation returning
/// <see cref="RebuildVerificationState.ManualVerificationRequired"/> with contributor guidance
/// toward isolated reverse-dependency verification and optional configured reference links.
/// </summary>
public class ManualGuideRebuildVerificationOrchestrator : IRebuildVerificationOrchestrator
{
    private readonly IGovernanceCommentFormatter _formatter;
    private readonly RebuildImpactOptions _options;

    public ManualGuideRebuildVerificationOrchestrator(
        IGovernanceCommentFormatter formatter,
        IOptions<RebuildImpactOptions> options)
    {
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    }

    public Task<RebuildVerificationResult> RequestVerificationAsync(
        RebuildVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var advisoryComment = _formatter.FormatRebuildGateBlocked(
            request.ChangedPackages,
            request.AffectedCount,
            request.Threshold,
            request.TargetBranch,
            locale: request.Locale,
            referenceLinks: _options.VerificationGuideLinks);

        return Task.FromResult(new RebuildVerificationResult(
            State: RebuildVerificationState.ManualVerificationRequired,
            AdvisoryComment: advisoryComment));
    }
}
