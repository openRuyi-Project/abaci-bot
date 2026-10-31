using abaci_bot.Services;
using abaci_bot.Modules;
using abaci_bot.Modules.ChangeTypeStrategies;
using abaci_bot.Options;
using abaci_bot.Pipelines;
using Octokit.Webhooks;
using Octokit.Webhooks.AspNetCore;

// Load appsettings.json and environment variables
var builder = WebApplication.CreateBuilder(args);

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
    Console.Error.WriteLine("  cp appsettings.demo.json appsettings.json");
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

builder.Services.AddSingleton<IGitHubService>(sp => new GitHubService(
    builder.Configuration.GetValue<int>("GitHubApp:AppId"),
    builder.Configuration["GitHubApp:PrivateKey"]!,
    builder.Configuration.GetValue<long>("GitHubApp:InstallationId"),
    sp.GetService<ILogger<GitHubService>>()
));

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
builder.Services.Configure<RebuildImpactOptions>(config.GetSection("RebuildImpact"));
builder.Services.Configure<AbiComplianceOptions>(config.GetSection("AbiCompliance"));

// Register pipeline infrastructure and mutual-exclusion engine
builder.Services.AddSingleton<PullRequestLockManager>();
builder.Services.AddSingleton<LabelMutexEngine>();

// Register change type classification strategies
builder.Services.AddSingleton<IChangeTypeStrategy, OpenRuyiPackageRepoStrategy>();
builder.Services.AddSingleton<IChangeTypeStrategy, HomepageRepoStrategy>();
builder.Services.AddSingleton<IChangeTypeStrategy, DefaultChangeTypeStrategy>();

// Register Part 2.2 domain services
builder.Services.AddSingleton<ISpdxLicenseService, SpdxLicenseService>();
builder.Services.AddSingleton<ICveSecurityService, CveSecurityService>();
builder.Services.AddSingleton<IRpmDependencyService, RpmDependencyService>();
builder.Services.AddSingleton<IAbiDiffRunner, ProcessAbiDiffRunner>();
builder.Services.AddSingleton<IAbiDiffService, AbiDiffService>();
builder.Services.AddSingleton<IArtifactService, ArtifactService>();
builder.Services.AddSingleton<IGovernanceCommentFormatter, GovernanceCommentFormatter>();

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

// Register background lifecycle services
builder.Services.AddHostedService<StaleLifecycleService>();
builder.Services.AddHostedService<RebuildMetadataRefreshService>();
builder.Services.AddHostedService<SpdxMetadataRefreshService>();

// Register the pipeline orchestrator
builder.Services.AddSingleton<PullRequestPipeline>();

builder.Services.AddSingleton<WebhookEventProcessor, GitHubWebhookProcessor>();

var app = builder.Build();

app.MapGitHubWebhooks("/api/webhook", config["GitHubApp:WebhookSecret"]!);

// Health check endpoint for Docker/Kubernetes
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class Program { }
