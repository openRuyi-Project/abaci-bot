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
}
