using System.IO.Compression;
using System.Text;
using abaci_bot.Services;
using NSubstitute;
using Octokit;
using Xunit;

namespace abaci_bot.Tests;

public class RemoteAssetBackfillerTests
{
    [Fact]
    public async Task TryBackfillAsync_ReturnsFalse_WhenNoMatchingArtifactFound()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.ListWorkflowArtifactsAsync("owner", "repo", 123L, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Artifact>());

        var backfiller = new RemoteAssetBackfiller(gitHubMock);
        var result = await backfiller.TryBackfillAsync("owner", "repo", "autoupdate/curl", 42, 123L);

        Assert.False(result);
        await gitHubMock.DidNotReceive().DownloadArtifactAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryBackfillAsync_ExtractsDiffAndPushesCommit_WhenValidArtifactExists()
    {
        var gitHubMock = Substitute.For<IGitHubService>();

        var artifact = (Artifact)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Artifact));
        typeof(Artifact).GetProperty(nameof(Artifact.Id))?.SetValue(artifact, 777L);
        typeof(Artifact).GetProperty(nameof(Artifact.Name))?.SetValue(artifact, "remoteasset-diff");

        gitHubMock.ListWorkflowArtifactsAsync("owner", "repo", 123L, Arg.Any<CancellationToken>())
            .Returns(new[] { artifact });

        var diffText = """
            --- a/SPECS/curl/curl.spec
            +++ b/SPECS/curl/curl.spec
            @@ -10,2 +10,2 @@
            -#!RemoteAsset: sha256:oldsha123456
            +#!RemoteAsset: sha256:newsha789012
            """;

        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("diff.patch");
            using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync(diffText);
        }
        ms.Position = 0;

        gitHubMock.DownloadArtifactAsync("owner", "repo", 777L, Arg.Any<CancellationToken>())
            .Returns(ms);

        gitHubMock.GetFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("Name: curl\n#!RemoteAsset: sha256:oldsha123456\nVersion: 8.2.1\n");

        var backfiller = new RemoteAssetBackfiller(gitHubMock);
        var result = await backfiller.TryBackfillAsync("owner", "repo", "autoupdate/curl", 42, 123L);

        Assert.True(result);
        await gitHubMock.Received(1).UpdateFileContentAsync(
            "owner",
            "repo",
            "SPECS/curl/curl.spec",
            "chore: update #!RemoteAsset sha256 checksums",
            "Name: curl\n#!RemoteAsset: sha256:newsha789012\nVersion: 8.2.1\n",
            "autoupdate/curl",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryBackfillAsync_ReturnsFalse_WhenSpecContentUnchanged()
    {
        var gitHubMock = Substitute.For<IGitHubService>();

        var artifact = (Artifact)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Artifact));
        typeof(Artifact).GetProperty(nameof(Artifact.Id))?.SetValue(artifact, 777L);
        typeof(Artifact).GetProperty(nameof(Artifact.Name))?.SetValue(artifact, "remoteasset-diff");

        gitHubMock.ListWorkflowArtifactsAsync("owner", "repo", 123L, Arg.Any<CancellationToken>())
            .Returns(new[] { artifact });

        // Diff changes line that does NOT exist in spec
        var diffText = """
            --- a/SPECS/curl/curl.spec
            +++ b/SPECS/curl/curl.spec
            @@ -10,2 +10,2 @@
            -#!RemoteAsset: sha256:differentoldsha
            +#!RemoteAsset: sha256:newsha789012
            """;

        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("diff.patch");
            using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync(diffText);
        }
        ms.Position = 0;

        gitHubMock.DownloadArtifactAsync("owner", "repo", 777L, Arg.Any<CancellationToken>())
            .Returns(ms);

        gitHubMock.GetFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("Name: curl\n#!RemoteAsset: sha256:alreadyupgraded\nVersion: 8.2.1\n");

        var backfiller = new RemoteAssetBackfiller(gitHubMock);
        var result = await backfiller.TryBackfillAsync("owner", "repo", "autoupdate/curl", 42, 123L);

        Assert.False(result);
        await gitHubMock.DidNotReceive().UpdateFileContentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
