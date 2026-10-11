using System.IO.Compression;
using System.Net;
using System.Text;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace abaci_bot.Tests;

public class ArtifactServiceTests
{
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();

    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public DelegatingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    [Fact]
    public async Task DownloadAndExtractRpmArtifactAsync_PathTraversalZipEntry_IsSafelySkipped()
    {
        // Construct a zip file in memory containing a normal entry and a Zip Slip traversal entry
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var validEntry = archive.CreateEntry("valid-pkg.rpm");
            using (var writer = new StreamWriter(validEntry.Open()))
            {
                writer.Write("rpm-data");
            }

            var traversalEntry = archive.CreateEntry("../../evil.rpm");
            using (var writer = new StreamWriter(traversalEntry.Open()))
            {
                writer.Write("malicious-payload");
            }
        }
        ms.Position = 0;

        var handler = new DelegatingHandlerStub(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(ms.ToArray())
            };
            return response;
        });
        var client = new HttpClient(handler);
        _httpClientFactory.CreateClient("GitHubArtifactClient").Returns(client);

        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions());
        var service = new ArtifactService(_httpClientFactory, options);

        var tempDir = Path.Combine(Path.GetTempPath(), "abaci_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var extracted = await service.DownloadAndExtractRpmArtifactAsync("owner", "repo", 12345, tempDir);

            // Only valid-pkg.rpm should be extracted
            Assert.Single(extracted);
            Assert.EndsWith("valid-pkg.rpm", extracted[0]);
            Assert.True(File.Exists(extracted[0]));

            // Ensure evil.rpm was NOT created outside tempDir
            var outsidePath = Path.GetFullPath(Path.Combine(tempDir, "../../evil.rpm"));
            Assert.False(File.Exists(outsidePath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadBaselineRpmAsync_MissingUrlOption_ReturnsNull()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            StableRpmBaseUrl = null
        });
        var service = new ArtifactService(_httpClientFactory, options);

        var tempDir = Path.Combine(Path.GetTempPath(), "abaci_test_" + Guid.NewGuid().ToString("N"));
        var result = await service.DownloadBaselineRpmAsync("glibc", tempDir);

        Assert.Null(result);
    }
}
