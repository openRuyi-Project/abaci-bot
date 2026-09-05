using abaci_bot.Services;
using abaci_bot.Modules;
using abaci_bot.Pipelines;
using Octokit.Webhooks;
using Octokit.Webhooks.AspNetCore;

// Load appsettings.json and environment variables
var builder = WebApplication.CreateBuilder(args);

// Validate required configuration — make sure appsettings.json exists and is filled in
var config = builder.Configuration;
var missingFields = new List<string>();

if (config.GetValue<int>("GitHubApp:AppId") == 0)      missingFields.Add("GitHubApp:AppId");
if (string.IsNullOrWhiteSpace(config["GitHubApp:PrivateKey"])) missingFields.Add("GitHubApp:PrivateKey");
if (config.GetValue<long>("GitHubApp:InstallationId") == 0)    missingFields.Add("GitHubApp:InstallationId");
if (string.IsNullOrWhiteSpace(config["GitHubApp:WebhookSecret"])) missingFields.Add("GitHubApp:WebhookSecret");
if (string.IsNullOrWhiteSpace(config["GitHubApp:TeamName"]))      missingFields.Add("GitHubApp:TeamName");

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
    return;
}

builder.Services.AddSingleton<IGitHubService>(new GitHubService(
    builder.Configuration.GetValue<int>("GitHubApp:AppId"),
    builder.Configuration["GitHubApp:PrivateKey"]!,
    builder.Configuration.GetValue<long>("GitHubApp:InstallationId")
));

// 注册 PR 流水线基础设施与互斥引擎
builder.Services.AddSingleton<PullRequestLockManager>();
builder.Services.AddSingleton<LabelMutexEngine>();

// 注册可插拔治理模块
builder.Services.AddSingleton<IPullRequestModule, WorkflowStateModule>();
builder.Services.AddSingleton<IPullRequestModule, UserContributionModule>();
builder.Services.AddSingleton<IPullRequestModule, BuildSystemAnalysisModule>();
builder.Services.AddSingleton<IPullRequestModule, AiAssistanceModule>();

// 注册流水线调度器
builder.Services.AddSingleton<PullRequestPipeline>();

builder.Services.AddSingleton<WebhookEventProcessor, GitHubWebhookProcessor>();

var app = builder.Build();

app.MapGitHubWebhooks("/api/webhook", config["GitHubApp:WebhookSecret"]!);

// Health check endpoint for Docker/Kubernetes
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class Program { }
