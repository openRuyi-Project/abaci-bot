using System.Text;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit;
using Xunit;

namespace abaci_bot.Tests;

public class GitHubSpecCatalogTests
{
    [Fact]
    public async Task GetSpecAsync_ReturnsSpecFromRemoteGitTree()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi"
        });

        // Mock tree response
        var treeItem = (TreeItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TreeItem));
        typeof(TreeItem).GetProperty(nameof(TreeItem.Type))?.SetValue(treeItem, new StringEnum<TreeType>(TreeType.Blob));
        typeof(TreeItem).GetProperty(nameof(TreeItem.Path))?.SetValue(treeItem, "SPECS/curl/curl.spec");

        var treeResponse = (TreeResponse)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TreeResponse));
        typeof(TreeResponse).GetProperty(nameof(TreeResponse.Tree))?.SetValue(treeResponse, new List<TreeItem> { treeItem });

        gitHubMock.GetTreeRecursiveAsync("openRuyi-Project", "openRuyi", "main", Arg.Any<CancellationToken>())
            .Returns(treeResponse);

        gitHubMock.GetFileContentAsync("openRuyi-Project", "openRuyi", "SPECS/curl/curl.spec", "main", Arg.Any<CancellationToken>())
            .Returns("Version: 8.21.0\nName: curl\n");

        using var catalog = new GitHubSpecCatalog(gitHubMock, options);

        var spec = await catalog.GetSpecAsync("curl");

        Assert.NotNull(spec);
        Assert.Equal("curl", spec.PackageName);
        Assert.Contains("Version: 8.21.0", spec.Content);
    }

    [Fact]
    public async Task GetAllPackageNamesAsync_ReturnsDiscoveredPackagesFromRemoteTree()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi"
        });

        var treeItem1 = (TreeItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TreeItem));
        typeof(TreeItem).GetProperty(nameof(TreeItem.Type))?.SetValue(treeItem1, new StringEnum<TreeType>(TreeType.Blob));
        typeof(TreeItem).GetProperty(nameof(TreeItem.Path))?.SetValue(treeItem1, "SPECS/curl/curl.spec");

        var treeItem2 = (TreeItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TreeItem));
        typeof(TreeItem).GetProperty(nameof(TreeItem.Type))?.SetValue(treeItem2, new StringEnum<TreeType>(TreeType.Blob));
        typeof(TreeItem).GetProperty(nameof(TreeItem.Path))?.SetValue(treeItem2, "SPECS/openssl/openssl.spec");

        var treeResponse = (TreeResponse)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TreeResponse));
        typeof(TreeResponse).GetProperty(nameof(TreeResponse.Tree))?.SetValue(treeResponse, new List<TreeItem> { treeItem1, treeItem2 });

        gitHubMock.GetTreeRecursiveAsync("openRuyi-Project", "openRuyi", "main", Arg.Any<CancellationToken>())
            .Returns(treeResponse);

        using var catalog = new GitHubSpecCatalog(gitHubMock, options);

        var names = await catalog.GetAllPackageNamesAsync();

        Assert.Equal(2, names.Count);
        Assert.Contains("curl", names);
        Assert.Contains("openssl", names);
    }

    [Fact]
    public async Task GetRustSpecsAsync_DiscoversRustPrefixSpecsFromRemoteCatalog()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi"
        });

        var treeItem1 = (TreeItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TreeItem));
        typeof(TreeItem).GetProperty(nameof(TreeItem.Type))?.SetValue(treeItem1, new StringEnum<TreeType>(TreeType.Blob));
        typeof(TreeItem).GetProperty(nameof(TreeItem.Path))?.SetValue(treeItem1, "SPECS/rust-ripgrep/rust-ripgrep.spec");

        var treeItem2 = (TreeItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TreeItem));
        typeof(TreeItem).GetProperty(nameof(TreeItem.Type))?.SetValue(treeItem2, new StringEnum<TreeType>(TreeType.Blob));
        typeof(TreeItem).GetProperty(nameof(TreeItem.Path))?.SetValue(treeItem2, "SPECS/curl/curl.spec");

        var treeResponse = (TreeResponse)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TreeResponse));
        typeof(TreeResponse).GetProperty(nameof(TreeResponse.Tree))?.SetValue(treeResponse, new List<TreeItem> { treeItem1, treeItem2 });

        gitHubMock.GetTreeRecursiveAsync("openRuyi-Project", "openRuyi", "main", Arg.Any<CancellationToken>())
            .Returns(treeResponse);

        gitHubMock.GetFileContentAsync("openRuyi-Project", "openRuyi", "SPECS/rust-ripgrep/rust-ripgrep.spec", "main", Arg.Any<CancellationToken>())
            .Returns("Name: rust-ripgrep\nVersion: 14.1.0\n");

        using var catalog = new GitHubSpecCatalog(gitHubMock, options);

        var rustSpecs = await catalog.GetRustSpecsAsync();

        Assert.Single(rustSpecs);
        Assert.Equal("rust-ripgrep", rustSpecs[0].PackageName);
        Assert.Contains("Version: 14.1.0", rustSpecs[0].Content);
    }

    [Fact]
    public async Task EnsureCatalogLoadedAsync_HandlesTruncatedTreeResponseGracefully()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi"
        });

        var treeItem = (TreeItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TreeItem));
        typeof(TreeItem).GetProperty(nameof(TreeItem.Type))?.SetValue(treeItem, new StringEnum<TreeType>(TreeType.Blob));
        typeof(TreeItem).GetProperty(nameof(TreeItem.Path))?.SetValue(treeItem, "SPECS/curl/curl.spec");

        var treeResponse = (TreeResponse)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TreeResponse));
        typeof(TreeResponse).GetProperty(nameof(TreeResponse.Tree))?.SetValue(treeResponse, new List<TreeItem> { treeItem });
        typeof(TreeResponse).GetProperty(nameof(TreeResponse.Truncated))?.SetValue(treeResponse, true);

        gitHubMock.GetTreeRecursiveAsync("openRuyi-Project", "openRuyi", "main", Arg.Any<CancellationToken>())
            .Returns(treeResponse);

        gitHubMock.GetFileContentAsync("openRuyi-Project", "openRuyi", "SPECS/curl/curl.spec", "main", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.21.0\n");

        using var catalog = new GitHubSpecCatalog(gitHubMock, options);

        var spec = await catalog.GetSpecAsync("curl");

        Assert.NotNull(spec);
        Assert.Equal("curl", spec.PackageName);
    }

    [Fact]
    public async Task GetSpecAsync_UsesSharedFileContentCache_WhenAvailable()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi"
        });

        const string blobSha = "abc123456789";
        var treeItem = (TreeItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TreeItem));
        typeof(TreeItem).GetProperty(nameof(TreeItem.Type))?.SetValue(treeItem, new StringEnum<TreeType>(TreeType.Blob));
        typeof(TreeItem).GetProperty(nameof(TreeItem.Path))?.SetValue(treeItem, "SPECS/curl/curl.spec");
        typeof(TreeItem).GetProperty(nameof(TreeItem.Sha))?.SetValue(treeItem, blobSha);

        var treeResponse = (TreeResponse)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TreeResponse));
        typeof(TreeResponse).GetProperty(nameof(TreeResponse.Tree))?.SetValue(treeResponse, new List<TreeItem> { treeItem });

        gitHubMock.GetTreeRecursiveAsync("openRuyi-Project", "openRuyi", "main", Arg.Any<CancellationToken>())
            .Returns(treeResponse);

        var fileCacheMock = Substitute.For<IFileContentCache>();
        string cachedContent = "Name: curl\nVersion: 8.21.0\n# from file cache\n";
        fileCacheMock.GetOrCreateAsync(blobSha, Arg.Any<Func<Task<string>>>())
            .Returns(Task.FromResult(cachedContent));

        using var catalog = new GitHubSpecCatalog(gitHubMock, options, null, fileCacheMock);

        var spec = await catalog.GetSpecAsync("curl");

        Assert.NotNull(spec);
        Assert.Equal("curl", spec.PackageName);
        Assert.Contains("# from file cache", spec.Content);

        // Verify remote API was never queried since content was retrieved from shared blob cache
        await gitHubMock.DidNotReceive().GetFileContentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}

