using System.IO.Compression;
using System.Text;
using abaci_bot.Services;
using Xunit;

namespace abaci_bot.Tests;

public class ArtifactDiffExtractorTests
{
    [Fact]
    public async Task ExtractDiffContentAsync_ParsesRawDiffStream()
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

    [Fact]
    public async Task ExtractDiffContentAsync_ParsesZipArchiveDiffStream()
    {
        var rawDiff = """
            --- a/SPECS/foo/foo.spec
            +++ b/SPECS/foo/foo.spec
            @@ -1,2 +1,2 @@
            -#!RemoteAsset: sha256:old
            +#!RemoteAsset: sha256:new
            """;

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("pr-123-remoteassets.diff");
            using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync(rawDiff);
        }
        ms.Position = 0;

        var result = await ArtifactDiffExtractor.ExtractDiffContentAsync(ms);

        Assert.Contains("+#!RemoteAsset: sha256:new", result);
    }

    [Fact]
    public void ExtractFileRemoteAssetChanges_OnlyExtractsTargetFileHunks()
    {
        var multiDiff = """
            diff --git a/SPECS/curl/curl.spec b/SPECS/curl/curl.spec
            --- a/SPECS/curl/curl.spec
            +++ b/SPECS/curl/curl.spec
            @@ -10,2 +10,2 @@
            -#!RemoteAsset:sha256:curl_old
            +#!RemoteAsset:sha256:curl_new
            diff --git a/SPECS/git/git.spec b/SPECS/git/git.spec
            --- a/SPECS/git/git.spec
            +++ b/SPECS/git/git.spec
            @@ -20,2 +20,2 @@
            -#!RemoteAsset:sha256:git_old
            +#!RemoteAsset:sha256:git_new
            """;

        var curlChanges = ArtifactDiffExtractor.ExtractFileRemoteAssetChanges(multiDiff, "SPECS/curl/curl.spec");
        Assert.Single(curlChanges);
        Assert.Equal("#!RemoteAsset:sha256:curl_old", curlChanges[0].OldLine);
        Assert.Equal("#!RemoteAsset:sha256:curl_new", curlChanges[0].NewLine);

        var gitChanges = ArtifactDiffExtractor.ExtractFileRemoteAssetChanges(multiDiff, "SPECS/git/git.spec");
        Assert.Single(gitChanges);
        Assert.Equal("#!RemoteAsset:sha256:git_old", gitChanges[0].OldLine);
        Assert.Equal("#!RemoteAsset:sha256:git_new", gitChanges[0].NewLine);

        var otherChanges = ArtifactDiffExtractor.ExtractFileRemoteAssetChanges(multiDiff, "SPECS/other/other.spec");
        Assert.Empty(otherChanges);
    }
}
