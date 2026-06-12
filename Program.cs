using abaci_bot.Services;
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

builder.Services.AddSingleton<WebhookEventProcessor, GitHubWebhookProcessor>();

var app = builder.Build();

app.MapGitHubWebhooks("/api/webhook", config["GitHubApp:WebhookSecret"]!);

// Health check endpoint for Docker/Kubernetes
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();
