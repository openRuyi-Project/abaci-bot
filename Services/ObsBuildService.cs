using System.Xml;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Implementation of OBS REST API queries using public endpoints.
/// Strictly appends '?package=' to prevent massive whole-project 6000+ package XML downloads.
/// </summary>
public class ObsBuildService : IObsBuildService
{
    private readonly HttpClient _httpClient;
    private readonly ObsOptions _options;
    private readonly ILogger<ObsBuildService>? _logger;

    public ObsBuildService(
        HttpClient httpClient,
        IOptions<PackageUpdateOptions> options,
        ILogger<ObsBuildService>? logger = null)
    {
        _httpClient = httpClient;
        _options = options.Value.Obs;
        _logger = logger;
    }

    public async Task<string?> GetPackageServiceRevisionAsync(string project, string package, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(package))
            return null;

        var baseUrl = _options.BaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/public/source/{Uri.EscapeDataString(project)}/{Uri.EscapeDataString(package)}/_service";

        try
        {
            using var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogDebug("OBS _service returned {StatusCode} for {Project}/{Package}", response.StatusCode, project, package);
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true, IgnoreWhitespace = true });

            bool insideParam = false;
            string? currentParamName = null;

            while (await reader.ReadAsync())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "param")
                {
                    insideParam = true;
                    currentParamName = reader.GetAttribute("name");
                }
                else if (reader.NodeType == XmlNodeType.Text && insideParam)
                {
                    if (string.Equals(currentParamName, "revision", StringComparison.OrdinalIgnoreCase))
                    {
                        var revision = reader.Value?.Trim();
                        if (!string.IsNullOrEmpty(revision))
                        {
                            return revision;
                        }
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "param")
                {
                    insideParam = false;
                    currentParamName = null;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to fetch or parse OBS _service for {Project}/{Package}", project, package);
        }

        return null;
    }

    public async Task<ObsPackageBuildResult> GetPackageBuildResultAsync(
        string project, string package, string repository, string arch, CancellationToken ct = default)
    {
        var defaultResult = new ObsPackageBuildResult(
            project, package, repository, arch, ObsStatusCode.Unknown, "unknown", "No response or failed to query");

        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(package))
            return defaultResult;

        var baseUrl = _options.BaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/public/build/{Uri.EscapeDataString(project)}/_result?package={Uri.EscapeDataString(package)}";

        try
        {
            using var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogDebug("OBS _result returned {StatusCode} for {Project}/{Package}", response.StatusCode, project, package);
                return defaultResult with { Details = $"HTTP {response.StatusCode}" };
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true, IgnoreWhitespace = true });

            string? currentRepo = null;
            string? currentArch = null;

            while (await reader.ReadAsync())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "result")
                {
                    currentRepo = reader.GetAttribute("repository");
                    currentArch = reader.GetAttribute("arch");
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.Name == "status")
                {
                    var statusPkg = reader.GetAttribute("package");
                    var statusCodeStr = reader.GetAttribute("code") ?? string.Empty;
                    var details = reader.GetAttribute("details");

                    // Check if matches the desired repository and arch if specified
                    bool repoMatch = string.IsNullOrEmpty(repository) || string.Equals(currentRepo, repository, StringComparison.OrdinalIgnoreCase);
                    bool archMatch = string.IsNullOrEmpty(arch) || string.Equals(currentArch, arch, StringComparison.OrdinalIgnoreCase);
                    bool pkgMatch = string.IsNullOrEmpty(statusPkg) || string.Equals(statusPkg, package, StringComparison.OrdinalIgnoreCase);

                    if (repoMatch && archMatch && pkgMatch)
                    {
                        var code = ParseStatusCode(statusCodeStr);
                        return new ObsPackageBuildResult(
                            project,
                            package,
                            currentRepo ?? repository,
                            currentArch ?? arch,
                            code,
                            statusCodeStr,
                            details);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to query OBS _result for {Project}/{Package}", project, package);
        }

        return defaultResult;
    }

    private static ObsStatusCode ParseStatusCode(string rawCode)
    {
        return rawCode.Trim().ToLowerInvariant() switch
        {
            "succeeded" => ObsStatusCode.Succeeded,
            "failed" => ObsStatusCode.Failed,
            "building" => ObsStatusCode.Building,
            "scheduled" => ObsStatusCode.Scheduled,
            "unresolvable" => ObsStatusCode.Unresolvable,
            "disabled" => ObsStatusCode.Disabled,
            "excluded" => ObsStatusCode.Excluded,
            _ => ObsStatusCode.Unknown
        };
    }
}

