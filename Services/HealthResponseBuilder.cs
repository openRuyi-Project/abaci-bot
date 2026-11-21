using abaci_bot.Options;

namespace abaci_bot.Services;

/// <summary>
/// Builds the <c>/health</c> response payload, separating minimal liveness + immutable build identity
/// from optional diagnostic details (<c>?verbose=true</c>).
/// </summary>
public static class HealthResponseBuilder
{
    public static object Build(
        bool verbose,
        RuntimeHealthState runtimeHealth,
        PackageUpdateOptions packageUpdate,
        CveActiveWatchOptions cveActiveWatch,
        MilestoneSyncOptions milestoneSync,
        StaleLifecycleOptions staleLifecycle,
        ConversationGuardOptions conversationGuard,
        AbiComplianceOptions abiCompliance,
        RebuildImpactOptions rebuildImpact,
        SpdxComplianceOptions spdxCompliance,
        CveSecurityOptions cveSecurity)
    {
        var now = runtimeHealth.GetUtcNow();
        if (!verbose)
        {
            return new
            {
                status = "healthy",
                timestamp = now.UtcDateTime,
                version = BuildIdentity.Version,
                commit = BuildIdentity.Commit,
                imageDigest = BuildIdentity.ImageDigest,
                buildTime = BuildIdentity.BuildTime,
                deployment = BuildIdentity.Deployment
            };
        }

        var (lastError, lastErrorAt) = runtimeHealth.GetLastError();
        return new
        {
            status = "healthy",
            timestamp = now.UtcDateTime,
            version = BuildIdentity.Version,
            commit = BuildIdentity.Commit,
            imageDigest = BuildIdentity.ImageDigest,
            buildTime = BuildIdentity.BuildTime,
            deployment = BuildIdentity.Deployment,
            startedAt = runtimeHealth.StartedAtUtc,
            uptimeSeconds = runtimeHealth.GetUptimeSeconds(),
            capabilities = new
            {
                abiEngine = AbiEngineProbe.ActiveEngineTier,
                conversationGuardPermission = ConversationGuardPermissionProbe.ProbeStatus
            },
            modules = new
            {
                packageUpdate = new { enabled = packageUpdate.Enabled, dryRun = packageUpdate.DryRun },
                cveActiveWatch = new { enabled = cveActiveWatch.Enabled, dryRun = cveActiveWatch.DryRun },
                milestoneSync = new { enabled = milestoneSync.Enabled, dryRun = milestoneSync.DryRun },
                staleLifecycle = new { enabled = staleLifecycle.Enabled, dryRun = staleLifecycle.DryRun },
                conversationGuard = new { enabled = conversationGuard.Enabled, dryRun = conversationGuard.DryRun, archiveEnabled = conversationGuard.ArchiveEnabled },
                abiCompliance = new { enabled = abiCompliance.Enabled, allowManagedFallback = abiCompliance.AllowManagedFallback },
                rebuildImpact = new { enabled = rebuildImpact.Enabled },
                spdxCompliance = new { enabled = spdxCompliance.Enabled },
                vulnerability = new { enabled = cveSecurity.Enabled }
            },
            runtime = new
            {
                lastError,
                lastErrorAt
            }
        };
    }
}
