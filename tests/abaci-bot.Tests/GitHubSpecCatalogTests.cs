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
    public async Task ArtifactDiffExtractor_ParsesRawDiffStream()
    {
        var rawDiff = """
            --- a/SPECS/curl/curl.spec
            +++ b/SPECS/curl/curl.spec
            @@ -10,2 +10,2 @@
            -#!RemoteAsset: sha256:aaa
            +#!RemoteAsset: sha256:bbb
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(rawDiff));
        var result = await ArtifactDiffExtractor.ExtractDiffContentAsync(stream);

        Assert.Contains("+#!RemoteAsset: sha256:bbb", result);
    }
}
