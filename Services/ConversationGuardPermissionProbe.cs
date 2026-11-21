using abaci_bot.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Startup probe that verifies whether the GitHub App installation holds elevated permissions
/// required to query collaborator permissions when <see cref="ConversationGuardOptions.Enabled"/> is true.
/// Fails fast on startup when elevated permissions are missing so governance never silently degrades.
/// </summary>
public class ConversationGuardPermissionProbe : IHostedService
{
    private readonly ConversationGuardOptions _options;
    private readonly IRepositoryPermissionService _permissionService;
    private readonly ILogger<ConversationGuardPermissionProbe> _logger;

    public static string ProbeStatus { get; internal set; } = "disabled";

    public ConversationGuardPermissionProbe(
        IOptions<ConversationGuardOptions> options,
        IRepositoryPermissionService permissionService,
        ILogger<ConversationGuardPermissionProbe> logger)
    {
        _options = options.Value;
        _permissionService = permissionService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            ProbeStatus = "disabled";
            _logger.LogInformation("ConversationGuard permission probe: DISABLED in configuration (minimal GitHub App permissions active).");
            return;
        }

        var repositories = RepositoryAllowList.ParseRepositories(
            _options.RepositoryAllowList,
            _logger,
            "ConversationGuard RepositoryAllowList");

        if (repositories.Count == 0)
        {
            ProbeStatus = "no-repositories";
            _logger.LogWarning("ConversationGuard is enabled but RepositoryAllowList has no valid 'owner/repo' targets; skipping permission probe.");
            return;
        }

        foreach (var (owner, repo) in repositories)
        {
            try
            {
                await _permissionService.VerifyCollaboratorPermissionProbeAsync(owner, repo, cancellationToken);
                if (_options.ArchiveEnabled)
                {
                    await _permissionService.VerifyArchivePermissionProbeAsync(owner, repo, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ProbeStatus = "failed";
                var fatalMessage = _options.ArchiveEnabled
                    ? $"ConversationGuard startup permission probe failed on '{owner}/{repo}' ({ex.Message}). " +
                      "When ConversationGuard:Enabled=true and ArchiveEnabled=true, archiving pull requests requires elevated GitHub App " +
                      "permissions ('Administration: Read & write') or a triage+ PackageUpdate:BotPat, in addition to collaborator query access. " +
                      "Grant the required permission, set ConversationGuard:ArchiveEnabled=false (recommended for minimal permissions), " +
                      "or set ConversationGuard:Enabled=false."
                    : $"ConversationGuard startup permission probe failed on '{owner}/{repo}' ({ex.Message}). " +
                      "When ConversationGuard:Enabled=true (including DryRun=true so authorization simulation matches production), " +
                      "community governance requires elevated GitHub App permissions ('Administration: Read-only' or 'Contents: Read & write') " +
                      "to verify collaborator triage/write+ roles. Grant the required permission in the GitHub App settings or set " +
                      "ConversationGuard:Enabled=false to maintain baseline minimal permissions.";
                _logger.LogCritical(ex, "{FatalMessage}", fatalMessage);
                throw new InvalidOperationException(fatalMessage, ex);
            }
        }

        ProbeStatus = "verified";
        _logger.LogInformation(
            "ConversationGuard permission probe verified collaborator access across {Count} repository(ies) (DryRun={DryRun}).",
            repositories.Count,
            _options.DryRun);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
