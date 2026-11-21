using abaci_bot.Services;
using abaci_bot.Modules;
using abaci_bot.Modules.ChangeTypeStrategies;
using abaci_bot.Options;
using abaci_bot.Pipelines;
using Microsoft.Extensions.Options;
using Octokit.Webhooks;
using Octokit.Webhooks.AspNetCore;

// Load appsettings.json and environment variables
var builder = WebApplication.CreateBuilder(args);
var runtimeHealth = new RuntimeHealthState(TimeProvider.System);
builder.Services.AddSingleton(runtimeHealth);
builder.Logging.AddProvider(new RuntimeErrorLoggerProvider(runtimeHealth));

// Validate required configuration — make sure appsettings.json exists and is filled in
var config = builder.Configuration;
var missingFields = new List<string>();

if (config.GetValue<int>("GitHubApp:AppId") == 0) missingFields.Add("GitHubApp:AppId");
if (string.IsNullOrWhiteSpace(config["GitHubApp:PrivateKey"])) missingFields.Add("GitHubApp:PrivateKey");
if (config.GetValue<long>("GitHubApp:InstallationId") == 0) missingFields.Add("GitHubApp:InstallationId");
if (string.IsNullOrWhiteSpace(config["GitHubApp:WebhookSecret"])) missingFields.Add("GitHubApp:WebhookSecret");
if (string.IsNullOrWhiteSpace(config["GitHubApp:TeamName"])) missingFields.Add("GitHubApp:TeamName");

if (missingFields.Count > 0)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine("ERROR: The following required configuration fields are missing or empty:");
    foreach (var field in missingFields)
        Console.Error.WriteLine($"  - {field}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Did you forget to create appsettings.json?");
    Console.Error.WriteLine("  cp appsettings.demo.jsonc appsettings.json");
    Console.Error.WriteLine("Then fill in your GitHub App credentials.");
    Console.ResetColor();
    Environment.ExitCode = 1;
    return;
}

// Fail-fast validation of GitHub App RSA private key
var privateKey = config["GitHubApp:PrivateKey"]!;
if (privateKey.Contains("\\n") && !privateKey.Contains('\n'))
{
    privateKey = privateKey.Replace("\\n", "\n");
    config["GitHubApp:PrivateKey"] = privateKey;
}
try
{
    using var rsa = System.Security.Cryptography.RSA.Create();
    rsa.ImportFromPem(privateKey);
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"ERROR: GitHubApp:PrivateKey could not be parsed as RSA PEM: {ex.Message}");
    Console.Error.WriteLine("Hint: Ensure the private key preserves newlines or uses standard PEM format.");
    Console.ResetColor();
    Environment.ExitCode = 1;
    return;
}

builder.Services.AddSingleton(sp => new GitHubService(
    builder.Configuration.GetValue<int>("GitHubApp:AppId"),
    builder.Configuration["GitHubApp:PrivateKey"]!,
    builder.Configuration.GetValue<long>("GitHubApp:InstallationId"),
    builder.Configuration["PackageUpdate:BotPat"],
    sp.GetService<ILogger<GitHubService>>()
));
builder.Services.AddSingleton<IGitHubService>(sp => sp.GetRequiredService<GitHubService>());
builder.Services.AddSingleton<IRepositoryPermissionService>(sp => sp.GetRequiredService<GitHubService>());
builder.Services.AddSingleton<IBotIdentityProvider>(sp => sp.GetRequiredService<GitHubService>());
builder.Services.AddSingleton<IEphemeralCommentGateway>(sp => sp.GetRequiredService<GitHubService>());
builder.Services.AddSingleton<IRepositoryArchiveGateway>(sp => sp.GetRequiredService<GitHubService>());
builder.Services.AddSingleton<IPagedIssueSearchGateway>(sp => sp.GetRequiredService<GitHubService>());

builder.Services.AddHttpClient();

builder.Services.Configure<TargetClassificationOptions>(config.GetSection("TargetClassification"));
builder.Services.Configure<ChangeTypeClassificationOptions>(config.GetSection("ChangeTypeClassification"));
builder.Services.AddOptions<StaleLifecycleOptions>()
    .Bind(config.GetSection("StaleLifecycle"))
    .ValidateOnStart();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<StaleLifecycleOptions>, StaleLifecycleOptionsValidator>();
builder.Services.Configure<BotDetectionOptions>(config.GetSection("BotDetection"));
builder.Services.Configure<SpdxComplianceOptions>(config.GetSection("SpdxCompliance"));
builder.Services.Configure<CveSecurityOptions>(config.GetSection("CveSecurity"));
builder.Services.AddOptions<RebuildImpactOptions>()
    .Bind(config.GetSection("RebuildImpact"))
    .ValidateOnStart();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<RebuildImpactOptions>, RebuildImpactOptionsValidator>();
builder.Services.Configure<AbiComplianceOptions>(config.GetSection("AbiCompliance"));
builder.Services.Configure<FileCacheOptions>(config.GetSection("FileCache"));
builder.Services.AddSingleton<IFileContentCache, FileContentCache>();
builder.Services.AddOptions<PackageUpdateOptions>()
    .Bind(config.GetSection("PackageUpdate"))
    .ValidateOnStart();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<PackageUpdateOptions>, PackageUpdateOptionsValidator>();
builder.Services.AddOptions<CveActiveWatchOptions>()
    .Bind(config.GetSection("CveActiveWatch"))
    .ValidateOnStart();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<CveActiveWatchOptions>, CveActiveWatchOptionsValidator>();
builder.Services.AddOptions<MilestoneSyncOptions>()
    .Bind(config.GetSection("MilestoneSync"))
    .ValidateOnStart();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<MilestoneSyncOptions>, MilestoneSyncOptionsValidator>();
builder.Services.AddOptions<BotStateOptions>()
    .Bind(config.GetSection("BotState"))
    .ValidateOnStart();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<BotStateOptions>, BotStateOptionsValidator>();
builder.Services.AddSingleton<IBotStateStore, JsonBotStateStore>();
builder.Services.AddSingleton<ILocaleResolver, LocaleResolver>();
builder.Services.AddSingleton<EphemeralReplyService>();
builder.Services.AddSingleton<IEphemeralReplyService>(sp => sp.GetRequiredService<EphemeralReplyService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<EphemeralReplyService>());

builder.Services.AddOptions<ConversationGuardOptions>()
    .Bind(config.GetSection("ConversationGuard"))
    .ValidateOnStart();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<ConversationGuardOptions>, ConversationGuardOptionsValidator>();

// Register pipeline infrastructure and mutual-exclusion engine
builder.Services.AddSingleton<PullRequestLockManager>();
builder.Services.AddSingleton<LabelMutexEngine>();

// Register change type classification strategies
builder.Services.AddSingleton<IChangeTypeStrategy, OpenRuyiPackageRepoStrategy>();
builder.Services.AddSingleton<IChangeTypeStrategy, HomepageRepoStrategy>();
builder.Services.AddSingleton<IChangeTypeStrategy, DefaultChangeTypeStrategy>();

// Register compliance, security, rebuild impact, and ABI domain services
builder.Services.AddSingleton<ISpdxLicenseService, SpdxLicenseService>();
builder.Services.AddSingleton<ICveSecurityService, CveSecurityService>();
builder.Services.AddSingleton<IRpmDependencyService, RpmDependencyService>();
builder.Services.AddSingleton<ManagedElfDiffRunner>();
builder.Services.AddSingleton<ProcessAbiDiffRunner>();
builder.Services.AddSingleton<IAbiDiffRunner>(sp => sp.GetRequiredService<ProcessAbiDiffRunner>());
builder.Services.AddSingleton<IAbiDiffService>(sp =>
    new AbiDiffService(
        sp.GetRequiredService<ProcessAbiDiffRunner>(),
        sp.GetService<ManagedElfDiffRunner>(),
        sp.GetRequiredService<IOptions<AbiComplianceOptions>>(),
        sp.GetService<ILogger<AbiDiffService>>()));
builder.Services.AddSingleton<IArtifactService, ArtifactService>();
builder.Services.AddSingleton<IGovernanceCommentFormatter, GovernanceCommentFormatter>();
builder.Services.AddSingleton<IRebuildVerificationOrchestrator, ManualGuideRebuildVerificationOrchestrator>();

// Register package update, CVE watch, milestone, and conversation governance services
builder.Services.AddSingleton<ISpecCatalog, GitHubSpecCatalog>();
builder.Services.AddSingleton<ISpecVersionRewriter, PlainSpecVersionRewriter>();
builder.Services.AddSingleton<ISpecVersionRewriter, RustCrateSpecRewriter>();
builder.Services.AddSingleton<IUpdateCandidateSource, LookoutVersionSource>();
builder.Services.AddSingleton<IUpdateCandidateSource, RustCrateVersionSource>();
builder.Services.AddSingleton<IObsBuildService, ObsBuildService>();
builder.Services.AddSingleton<IRemoteAssetBackfiller, RemoteAssetBackfiller>();
builder.Services.AddSingleton<IPackageUpdatePublisher, GitHubPackageUpdatePublisher>();
builder.Services.AddSingleton<ObsPostMergeVerifier>();
builder.Services.AddSingleton<IPackageVulnerabilityScanner, PackageVulnerabilityScanner>();
builder.Services.AddSingleton<IMilestoneResolver, MilestoneResolver>();
builder.Services.AddSingleton(_ => new PendingConfirmationStore());
builder.Services.AddSingleton<IConversationGuardService, ConversationGuardService>();
builder.Services.AddSingleton<IBotControlPlaneService, BotControlPlaneService>();

// Register pluggable pull request governance modules (executed in priority order)
builder.Services.AddSingleton<IPullRequestModule, WorkflowStateModule>();
builder.Services.AddSingleton<IPullRequestModule, StaleUnstaleModule>();
builder.Services.AddSingleton<IPullRequestModule, ShinyNewPrModule>();
builder.Services.AddSingleton<IPullRequestModule, UserContributionModule>();
builder.Services.AddSingleton<IPullRequestModule, TargetClassificationModule>();
builder.Services.AddSingleton<IPullRequestModule, BuildSystemAnalysisModule>();
builder.Services.AddSingleton<IPullRequestModule, ChangeTypeClassificationModule>();
builder.Services.AddSingleton<IPullRequestModule, AiAssistanceModule>();
builder.Services.AddSingleton<IPullRequestModule, SpdxComplianceModule>();
builder.Services.AddSingleton<IPullRequestModule, VulnerabilityModule>();
builder.Services.AddSingleton<IPullRequestModule, RebuildImpactModule>();
builder.Services.AddSingleton<IPullRequestModule, AbiComplianceModule>();
builder.Services.AddSingleton<IPullRequestModule, MilestoneSyncModule>();
builder.Services.AddSingleton<IPullRequestModule, ConversationGuardModule>();
builder.Services.AddSingleton<IPullRequestModule, BotControlPlaneModule>();

// Register background lifecycle services
builder.Services.AddHostedService<AbiEngineProbe>();
builder.Services.AddHostedService<ConversationGuardPermissionProbe>();
builder.Services.AddHostedService<StaleLifecycleService>();
builder.Services.AddHostedService<RebuildMetadataRefreshService>();
builder.Services.AddHostedService<SpdxMetadataRefreshService>();
builder.Services.AddHostedService<PackageUpdateService>();
builder.Services.AddHostedService<CveActiveWatchService>();
builder.Services.AddHostedService<MilestoneBackfillSweepService>();
builder.Services.AddHostedService<LockSweepService>();

// Register the pipeline orchestrator
builder.Services.AddSingleton<PullRequestPipeline>();

builder.Services.AddSingleton<WebhookEventProcessor, GitHubWebhookProcessor>();

var app = builder.Build();

// Eagerly initialize persistent state store at startup so unwritable state directories
// fail fast and expired user preferences are pruned immediately.
app.Services.GetRequiredService<IBotStateStore>();

app.MapGitHubWebhooks("/api/webhook", config["GitHubApp:WebhookSecret"]!);

// Health check endpoint for Docker/Kubernetes (minimal by default; ?verbose=true for diagnostics)
app.MapGet("/health", (
    HttpContext httpContext,
    RuntimeHealthState healthState,
    IOptions<PackageUpdateOptions> packageUpdate,
    IOptions<CveActiveWatchOptions> cveActiveWatch,
    IOptions<MilestoneSyncOptions> milestoneSync,
    IOptions<StaleLifecycleOptions> staleLifecycle,
    IOptions<ConversationGuardOptions> conversationGuard,
    IOptions<AbiComplianceOptions> abiCompliance,
    IOptions<RebuildImpactOptions> rebuildImpact,
    IOptions<SpdxComplianceOptions> spdxCompliance,
    IOptions<CveSecurityOptions> cveSecurity) =>
{
    var verboseParam = httpContext.Request.Query["verbose"].ToString();
    var isVerbose = httpContext.Request.Query.ContainsKey("verbose") &&
                    !string.Equals(verboseParam, "false", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(verboseParam, "0", StringComparison.Ordinal);

    return Results.Ok(HealthResponseBuilder.Build(
        isVerbose,
        healthState,
        packageUpdate.Value,
        cveActiveWatch.Value,
        milestoneSync.Value,
        staleLifecycle.Value,
        conversationGuard.Value,
        abiCompliance.Value,
        rebuildImpact.Value,
        spdxCompliance.Value,
        cveSecurity.Value));
});

app.Run();

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class Program { }
