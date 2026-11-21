using System.Formats.Tar;
using System.IO.Compression;
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
        Assert.Equal("SPECS/curl/curl.spec", spec.RelativePath);
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

    [Fact]
    public async Task TarballCatalog_Loads5811SpecsInSingleArchiveCall_WithZeroPerFileApiCalls()
    {
        const int totalSpecs = 5811;
        const int rustSpecsCount = 500;

        var entries = new List<(string Path, string Content)>(totalSpecs + 2)
        {
            ("5teve2NT-openRuyi-deadbeef/README.md", "# openRuyi\n"),
            ("5teve2NT-openRuyi-deadbeef/SPECS/curl/fix-build.patch", "diff --git a/x b/x\n")
        };

        for (int i = 0; i < totalSpecs; i++)
        {
            var pkg = i < rustSpecsCount
                ? $"rust-crate-{i:D4}"
                : $"pkg-{i:D4}";
            var content = pkg.StartsWith("rust-", StringComparison.Ordinal)
                ? $"%global crate_name crate-{i}\n%global full_version 1.{i % 10}.0\nName: {pkg}\nVersion: 1.{i % 10}.0\n"
                : $"Name: {pkg}\nVersion: 2.{i % 10}.0\n";
            entries.Add(($"5teve2NT-openRuyi-deadbeef/SPECS/{pkg}/{pkg}.spec", content));
        }

        var tarballBytes = BuildTarGz(entries);

        var gitHubMock = Substitute.For<IGitHubService>();
        var archiveGateway = Substitute.For<IRepositoryArchiveGateway>();
        archiveGateway.GetRepositoryTarballAsync("5teve2NT", "openRuyi", "main", Arg.Any<CancellationToken>())
            .Returns(tarballBytes);

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "5teve2NT/openRuyi"
        });

        using var catalog = new GitHubSpecCatalog(gitHubMock, options, archiveGateway: archiveGateway);

        var names = await catalog.GetAllPackageNamesAsync();
        var rustSpecs = await catalog.GetRustSpecsAsync();
        var sampledSpec = await catalog.GetSpecAsync("pkg-5810");

        Assert.Equal(totalSpecs, names.Count);
        Assert.Equal(rustSpecsCount, rustSpecs.Count);
        Assert.NotNull(sampledSpec);
        Assert.Contains("Name: pkg-5810", sampledSpec!.Content, StringComparison.Ordinal);

        await archiveGateway.Received(1).GetRepositoryTarballAsync("5teve2NT", "openRuyi", "main", Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceive().GetTreeRecursiveAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceive().GetFileContentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TarballCatalog_WhenTarballFails_FallsBackToGitTree()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var archiveGateway = Substitute.For<IRepositoryArchiveGateway>();
        archiveGateway.GetRepositoryTarballAsync("owner", "repo", "main", Arg.Any<CancellationToken>())
            .Returns<Task<byte[]?>>(_ => throw new HttpRequestException("tarball download failed"));

        var treeItem = (TreeItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TreeItem));
        typeof(TreeItem).GetProperty(nameof(TreeItem.Type))?.SetValue(treeItem, new StringEnum<TreeType>(TreeType.Blob));
        typeof(TreeItem).GetProperty(nameof(TreeItem.Path))?.SetValue(treeItem, "SPECS/zlib/zlib.spec");

        var treeResponse = (TreeResponse)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TreeResponse));
        typeof(TreeResponse).GetProperty(nameof(TreeResponse.Tree))?.SetValue(treeResponse, new List<TreeItem> { treeItem });

        gitHubMock.GetTreeRecursiveAsync("owner", "repo", "main", Arg.Any<CancellationToken>())
            .Returns(treeResponse);
        gitHubMock.GetFileContentAsync("owner", "repo", "SPECS/zlib/zlib.spec", "main", Arg.Any<CancellationToken>())
            .Returns("Name: zlib\nVersion: 1.3.1\n");

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo"
        });

        using var catalog = new GitHubSpecCatalog(gitHubMock, options, archiveGateway: archiveGateway);
        var spec = await catalog.GetSpecAsync("zlib");

        Assert.NotNull(spec);
        Assert.Equal("zlib", spec!.PackageName);
        Assert.Contains("1.3.1", spec.Content, StringComparison.Ordinal);
        await gitHubMock.Received(1).GetTreeRecursiveAsync("owner", "repo", "main", Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).GetFileContentAsync("owner", "repo", "SPECS/zlib/zlib.spec", "main", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TarballCatalog_WhenArchiveExceedsMaxArchiveBytes_FallsBackToGitTree()
    {
        var oversizedTarball = BuildTarGz(new[]
        {
            ("owner-repo-sha/SPECS/zlib/zlib.spec", "Name: zlib\nVersion: 1.3.1\n")
        });
        Assert.True(oversizedTarball.Length > 10);

        var gitHubMock = Substitute.For<IGitHubService>();
        var archiveGateway = Substitute.For<IRepositoryArchiveGateway>();
        archiveGateway.GetRepositoryTarballAsync("owner", "repo", "main", Arg.Any<CancellationToken>())
            .Returns(oversizedTarball);

        var treeItem = (TreeItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TreeItem));
        typeof(TreeItem).GetProperty(nameof(TreeItem.Type))?.SetValue(treeItem, new StringEnum<TreeType>(TreeType.Blob));
        typeof(TreeItem).GetProperty(nameof(TreeItem.Path))?.SetValue(treeItem, "SPECS/zlib/zlib.spec");

        var treeResponse = (TreeResponse)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TreeResponse));
        typeof(TreeResponse).GetProperty(nameof(TreeResponse.Tree))?.SetValue(treeResponse, new List<TreeItem> { treeItem });

        gitHubMock.GetTreeRecursiveAsync("owner", "repo", "main", Arg.Any<CancellationToken>())
            .Returns(treeResponse);
        gitHubMock.GetFileContentAsync("owner", "repo", "SPECS/zlib/zlib.spec", "main", Arg.Any<CancellationToken>())
            .Returns("Name: zlib\nVersion: 1.3.1\n");

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo",
            MaxArchiveBytes = 10
        });

        using var catalog = new GitHubSpecCatalog(gitHubMock, options, archiveGateway: archiveGateway);
        var spec = await catalog.GetSpecAsync("zlib");

        Assert.NotNull(spec);
        Assert.Contains("1.3.1", spec!.Content, StringComparison.Ordinal);
        await archiveGateway.Received(1).GetRepositoryTarballAsync("owner", "repo", "main", Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).GetTreeRecursiveAsync("owner", "repo", "main", Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).GetFileContentAsync("owner", "repo", "SPECS/zlib/zlib.spec", "main", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureCatalogLoadedAsync_WhenRefreshFails_AppliesBackoffWithoutRepeatedDownloads()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var archiveGateway = Substitute.For<IRepositoryArchiveGateway>();
        archiveGateway.GetRepositoryTarballAsync("owner", "repo", "main", Arg.Any<CancellationToken>())
            .Returns<Task<byte[]?>>(_ => throw new HttpRequestException("tarball unavailable"));
        gitHubMock.GetTreeRecursiveAsync("owner", "repo", "main", Arg.Any<CancellationToken>())
            .Returns<Task<TreeResponse?>>(_ => throw new HttpRequestException("tree API unavailable"));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo"
        });

        using var catalog = new GitHubSpecCatalog(gitHubMock, options, archiveGateway: archiveGateway);

        for (int i = 0; i < 5; i++)
        {
            Assert.Null(await catalog.GetSpecAsync($"pkg-{i}"));
        }

        // Backoff prevents 5 consecutive failed refreshes from hammering tarball/tree endpoints
        await archiveGateway.Received(1).GetRepositoryTarballAsync("owner", "repo", "main", Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).GetTreeRecursiveAsync("owner", "repo", "main", Arg.Any<CancellationToken>());
    }

    private static byte[] BuildTarGz(IEnumerable<(string Path, string Content)> entries)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            foreach (var (path, content) in entries)
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                using var dataStream = new MemoryStream(bytes, writable: false);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = dataStream
                };
                writer.WriteEntry(entry);
            }
        }

        return output.ToArray();
    }
}

