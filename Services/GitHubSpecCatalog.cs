using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Deep module implementing ISpecCatalog.
/// Bridges remote GitHub Git Trees API with caching and falls back to local SPECS/ directory if present.
/// Shields callers from filesystem assumptions in containerized Docker runtime.
/// </summary>
public class GitHubSpecCatalog : ISpecCatalog, IDisposable
{
    private readonly IGitHubService _gitHubService;
    private readonly IOptions<PackageUpdateOptions> _options;
    private readonly ILogger<GitHubSpecCatalog>? _logger;

    private readonly ConcurrentDictionary<string, (SpecContext Context, DateTimeOffset Expiry)> _specCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _specPathMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _specBlobMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly IFileContentCache? _fileCache;
    private DateTimeOffset _lastCatalogRefresh = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public GitHubSpecCatalog(
        IGitHubService gitHubService,
        IOptions<PackageUpdateOptions> options,
        ILogger<GitHubSpecCatalog>? logger = null)
        : this(gitHubService, options, logger, null)
    {
    }

    public GitHubSpecCatalog(
        IGitHubService gitHubService,
        IOptions<PackageUpdateOptions> options,
        ILogger<GitHubSpecCatalog>? logger,
        IFileContentCache? fileCache)
    {
        _gitHubService = gitHubService;
        _options = options;
        _logger = logger;
        _fileCache = fileCache;
    }

    private (string Owner, string Repo) GetTargetOwnerRepo()
    {
        var targetRepo = _options.Value.TargetRepository;
        var parts = targetRepo.Split('/');
        return parts.Length == 2 ? (parts[0].Trim(), parts[1].Trim()) : ("openRuyi-Project", "openRuyi");
    }

    public async Task<SpecContext?> GetSpecAsync(string packageName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return null;

        await EnsureCatalogLoadedAsync(ct);

        if (_specCache.TryGetValue(packageName, out var cached) && cached.Expiry > DateTimeOffset.UtcNow)
            return cached.Context;

        // Check local disk fallback if present
        var localDir = Path.Combine("SPECS", packageName);
        if (Directory.Exists(localDir))
        {
            var specFiles = Directory.GetFiles(localDir, "*.spec");
            if (specFiles.Length > 0)
            {
                var content = await File.ReadAllTextAsync(specFiles[0], ct);
                var relPath = specFiles[0].Replace('\\', '/');
                var ctx = new SpecContext(packageName, packageName, content, relPath);
                _specCache[packageName] = (ctx, DateTimeOffset.UtcNow.AddHours(6));
                return ctx;
            }
        }

        // Fetch via GitHub API if path is known
        if (_specPathMap.TryGetValue(packageName, out var remotePath))
        {
            var (owner, repo) = GetTargetOwnerRepo();
            const string baseBranch = "main";

            try
            {
                string content;
                if (_fileCache != null && _specBlobMap.TryGetValue(packageName, out var sha) && !string.IsNullOrWhiteSpace(sha))
                {
                    content = await _fileCache.GetOrCreateAsync(sha, () => _gitHubService.GetFileContentAsync(owner, repo, remotePath, baseBranch, ct));
                }
                else
                {
                    content = await _gitHubService.GetFileContentAsync(owner, repo, remotePath, baseBranch, ct);
                }

                var dirName = Path.GetFileName(Path.GetDirectoryName(remotePath)) ?? packageName;
                var ctx = new SpecContext(packageName, dirName, content, remotePath);
                _specCache[packageName] = (ctx, DateTimeOffset.UtcNow.AddHours(6));
                return ctx;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to fetch remote spec file for {Package} at {Path}", packageName, remotePath);
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<SpecContext>> GetRustSpecsAsync(CancellationToken ct = default)
    {
        await EnsureCatalogLoadedAsync(ct);

        // 1. If local SPECS/ exists, load local specs
        if (Directory.Exists("SPECS"))
        {
            var list = new List<SpecContext>();
            foreach (var dir in Directory.GetDirectories("SPECS", "rust-*"))
            {
                if (ct.IsCancellationRequested)
                    break;

                var dirName = Path.GetFileName(dir);
                var specFiles = Directory.GetFiles(dir, "*.spec");
                if (specFiles.Length == 0)
                    continue;

                try
                {
                    var content = await File.ReadAllTextAsync(specFiles[0], ct);
                    var relPath = specFiles[0].Replace('\\', '/');
                    list.Add(new SpecContext(dirName, dirName, content, relPath));
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to read local SPEC file {File}", specFiles[0]);
                }
            }

            if (list.Count > 0)
                return list;
        }

        // 2. Load from cached remote catalog
        var rustPackages = _specPathMap.Keys
            .Where(k => k.StartsWith("rust-", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var rustSpecs = new List<SpecContext>();
        foreach (var pkg in rustPackages)
        {
            if (ct.IsCancellationRequested)
                break;

            var spec = await GetSpecAsync(pkg, ct);
            if (spec != null)
            {
                rustSpecs.Add(spec);
            }
        }

        return rustSpecs;
    }

    public async Task<IReadOnlyList<string>> GetAllPackageNamesAsync(CancellationToken ct = default)
    {
        await EnsureCatalogLoadedAsync(ct);

        if (!_specPathMap.IsEmpty)
        {
            return _specPathMap.Keys.ToList();
        }

        if (Directory.Exists("SPECS"))
        {
            return Directory.GetDirectories("SPECS")
                .Select(Path.GetFileName)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList()!;
        }

        return Array.Empty<string>();
    }

    private async Task EnsureCatalogLoadedAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _lastCatalogRefresh < TimeSpan.FromHours(6) && !_specPathMap.IsEmpty)
            return;

        await _refreshLock.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow - _lastCatalogRefresh < TimeSpan.FromHours(6) && !_specPathMap.IsEmpty)
                return;

            var (owner, repo) = GetTargetOwnerRepo();
            const string baseBranch = "main";

            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
                return;

            _logger?.LogInformation("Discovering repository spec tree from GitHub for {Owner}/{Repo} on branch {Branch}", owner, repo, baseBranch);
            var tree = await _gitHubService.GetTreeRecursiveAsync(owner, repo, baseBranch, ct);
            if (tree?.Tree != null)
            {
                if (tree.Truncated)
                {
                    _logger?.LogWarning("Remote spec tree from GitHub for {Owner}/{Repo} was truncated by GitHub API (>100k items)", owner, repo);
                }

                _specCache.Clear();
                _specBlobMap.Clear();
                foreach (var item in tree.Tree)
                {
                    if (item.Type == Octokit.TreeType.Blob && item.Path.StartsWith("SPECS/", StringComparison.OrdinalIgnoreCase) && item.Path.EndsWith(".spec", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = item.Path.Split('/');
                        if (parts.Length >= 3)
                        {
                            var pkgName = parts[1];
                            _specPathMap[pkgName] = item.Path;
                            if (!string.IsNullOrWhiteSpace(item.Sha))
                            {
                                _specBlobMap[pkgName] = item.Sha;
                            }
                        }
                    }
                }
                _logger?.LogInformation("Discovered {Count} specs from remote GitHub tree", _specPathMap.Count);
            }

            _lastCatalogRefresh = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to refresh remote spec catalog from GitHub tree");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Dispose()
    {
        _refreshLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
