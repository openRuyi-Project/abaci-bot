using System.Diagnostics;
using abaci_bot.Contexts;
using abaci_bot.Modules;
using abaci_bot.Options;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit;
using Octokit.Webhooks.Events.PullRequest;
using Xunit;

namespace abaci_bot.Tests;

public class SpdxComplianceModuleTests
{
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();

    private SpdxLicenseService CreateService(bool allowLegacy = true)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new SpdxComplianceOptions
        {
            AllowLegacyAliases = allowLegacy,
            UpdateRemoteUrl = string.Empty
        });
        return new SpdxLicenseService(_httpClientFactory, options);
    }

    private PullRequestContext CreateContext(
        IReadOnlyList<PullRequestFile> files,
        PullRequestAction? action = null,
        IEnumerable<string>? existingLabels = null)
    {
        var effectiveAction = action ?? PullRequestAction.Opened;
        _gitHub.GetPullRequestFilesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(files);

        return new PullRequestContext(
            gitHubService: _gitHub,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.PullRequest,
            owner: "openRuyi-Project",
            repo: "openRuyi",
            prNumber: 42,
            headSha: "head123",
            baseBranch: "main",
            isDraft: false,
            isMerged: null,
            title: "Update spec",
            body: "PR body",
            senderLogin: "contributor",
            authorLogin: "contributor",
            action: effectiveAction.ToString(),
            existingLabels: existingLabels ?? Array.Empty<string>(),
            prAction: effectiveAction);
    }

    private static PullRequestFile CreateFile(string filename, string status = "modified")
    {
        var file = (PullRequestFile)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequestFile));
        typeof(PullRequestFile).GetProperty(nameof(PullRequestFile.FileName))?.SetValue(file, filename);
        typeof(PullRequestFile).GetProperty(nameof(PullRequestFile.Status))?.SetValue(file, status);
        return file;
    }

    [Theory]
    [InlineData("Apache-2.0")]
    [InlineData("MIT")]
    [InlineData("GPL-2.0-only")]
    [InlineData("GPL-2.0-or-later")]
    [InlineData("BSD-3-Clause")]
    [InlineData("GPL-2.0+")]
    public void SpdxLicenseService_ValidStandardLicenses_ReturnsValid(string expression)
    {
        var service = CreateService();
        var result = service.ValidateExpression(expression);

        Assert.True(result.IsValid, $"Expected '{expression}' to be valid.");
        Assert.Empty(result.InvalidIdentifiers);
    }

    [Theory]
    [InlineData("GPLv2+", "GPL-2.0-or-later")]
    [InlineData("LGPLv2.1+", "LGPL-2.1-or-later")]
    [InlineData("ASL 2.0", "Apache-2.0")]
    public void SpdxLicenseService_LegacyAliases_AcceptedWhenEnabled(string legacy, string expectedCanonical)
    {
        var service = CreateService(allowLegacy: true);
        var result = service.ValidateExpression(legacy);

        Assert.True(result.IsValid, $"Expected legacy alias '{legacy}' to be valid.");
        Assert.NotNull(expectedCanonical);
    }

    [Theory]
    [InlineData("GPL-2.0-or-later OR Apache-2.0")]
    [InlineData("MIT AND BSD-3-Clause")]
    [InlineData("(MIT AND Apache-2.0) OR GPL-3.0-or-later")]
    [InlineData("GPL-2.0-only WITH Classpath-exception-2.0")]
    [InlineData("(GPL-2.0-or-later OR BSD-2-Clause) AND MIT")]
    public void SpdxLicenseService_ComplexExpressions_ValidatesSuccessfully(string expression)
    {
        var service = CreateService();
        // Warm up JIT/data loading before timing to prevent intermittent CI/cold-start jitter
        service.ValidateExpression("MIT");

        var sw = Stopwatch.StartNew();
        var result = service.ValidateExpression(expression);
        sw.Stop();

        Assert.True(result.IsValid, $"Expected expression '{expression}' to be valid: {result.ErrorMessage}");
        Assert.True(sw.ElapsedMilliseconds < 20, $"Validation took {sw.ElapsedMilliseconds}ms, exceeding 20ms requirement.");
    }

    [Theory]
    [InlineData("CustomFakeLicenseXYZ")]
    [InlineData("GPL-2.0-only OR NotARealLicense")]
    [InlineData("MIT AND (BSD-3-Clause OR BadId)")]
    public void SpdxLicenseService_InvalidIdentifiers_ReturnsInvalid(string expression)
    {
        var service = CreateService();
        var result = service.ValidateExpression(expression);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.InvalidIdentifiers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SpdxLicenseService_MissingOrEmptyExpression_ReturnsInvalid(string? expression)
    {
        var service = CreateService();
        var result = service.ValidateExpression(expression);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void SpdxLicenseService_SyntaxError_MismatchedParentheses_ReturnsInvalid()
    {
        var service = CreateService();
        var result = service.ValidateExpression("(MIT AND Apache-2.0");

        Assert.False(result.IsValid);
        Assert.Contains("Missing closing parenthesis", result.ErrorMessage);
    }

    [Fact]
    public async Task ProcessAsync_SpecWithValidLicense_RemovesLicenseIssueLabel()
    {
        var service = CreateService();
        var options = Microsoft.Extensions.Options.Options.Create(new SpdxComplianceOptions());
        var module = new SpdxComplianceModule(service, options);

        var file = CreateFile("SPECS/test/test.spec");
        var context = CreateContext(new[] { file }, existingLabels: new[] { GitHubLabels.ComplianceLicenseIssue });

        _gitHub.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), "SPECS/test/test.spec", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Name: test\nVersion: 1.0\nLicense: Apache-2.0\n");

        Assert.True(module.ShouldProcess(context));
        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.ComplianceLicenseIssue, context.LabelsToRemove);
        Assert.DoesNotContain(GitHubLabels.ComplianceLicenseIssue, context.LabelsToAdd);
        Assert.Empty(context.PendingComments);
    }

    [Fact]
    public async Task ProcessAsync_SpecWithInvalidLicense_AddsLicenseIssueLabelAndComment()
    {
        var service = CreateService();
        var options = Microsoft.Extensions.Options.Options.Create(new SpdxComplianceOptions());
        var module = new SpdxComplianceModule(service, options);

        var file = CreateFile("SPECS/pkg/pkg.spec");
        var context = CreateContext(new[] { file });

        _gitHub.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), "SPECS/pkg/pkg.spec", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Name: pkg\nVersion: 2.0\nLicense: ProprietaryNonStandard\n");

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.ComplianceLicenseIssue, context.LabelsToAdd);
        Assert.NotEmpty(context.PendingComments);
        Assert.Contains("SPDX License Compliance Check Failed", context.PendingComments[0]);
        Assert.Contains("ProprietaryNonStandard", context.PendingComments[0]);
    }

    [Fact]
    public async Task ProcessAsync_SpecWithoutLicenseTag_AddsLicenseIssueLabel()
    {
        var service = CreateService();
        var options = Microsoft.Extensions.Options.Options.Create(new SpdxComplianceOptions());
        var module = new SpdxComplianceModule(service, options);

        var file = CreateFile("SPECS/pkg/pkg.spec");
        var context = CreateContext(new[] { file });

        _gitHub.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), "SPECS/pkg/pkg.spec", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Name: pkg\nVersion: 1.0\nSummary: A test package without license\n");

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.ComplianceLicenseIssue, context.LabelsToAdd);
        Assert.NotEmpty(context.PendingComments);
        Assert.Contains("Missing required `License:` field declaration", context.PendingComments[0]);
    }

    [Fact]
    public async Task ProcessAsync_NonSpecFiles_SkipsEvaluation()
    {
        var service = CreateService();
        var options = Microsoft.Extensions.Options.Options.Create(new SpdxComplianceOptions());
        var module = new SpdxComplianceModule(service, options);

        var file = CreateFile("docs/README.md");
        var context = CreateContext(new[] { file });

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Empty(context.LabelsToAdd);
        Assert.Empty(context.LabelsToRemove);
        Assert.Empty(context.PendingComments);
    }

    [Fact]
    public void SpdxLicenseService_EmbeddedLicensesDictionary_LoadsComprehensiveLicenses()
    {
        var service = CreateService(allowLegacy: false);

        // Licenses from the official 740+ SPDX list embedded resource:
        Assert.True(service.IsValidSpdxIdentifier("FSFULLRWD"));
        Assert.True(service.IsValidSpdxIdentifier("0BSD"));
        Assert.True(service.IsValidSpdxIdentifier("CECILL-2.1"));
        Assert.True(service.IsValidSpdxIdentifier("Zlib"));
        Assert.True(service.IsValidSpdxIdentifier("Hippocratic-2.1"));
    }

    [Fact]
    public void SpdxLicenseService_AmbiguousBsd_IsInvalid()
    {
        var service = CreateService(allowLegacy: true);
        // Bare "BSD" was removed from legacy aliases because it is ambiguous and disallowed in openRuyi/Fedora
        var result = service.ValidateExpression("BSD");
        Assert.False(result.IsValid);
        Assert.Contains("BSD", result.InvalidIdentifiers);
    }

    [Fact]
    public async Task ProcessAsync_SpecWithSubpackages_ValidatesAllLicenseTags()
    {
        var service = CreateService();
        var options = Microsoft.Extensions.Options.Options.Create(new SpdxComplianceOptions());
        var module = new SpdxComplianceModule(service, options);

        var file = CreateFile("SPECS/multilib/multilib.spec");
        var context = CreateContext(new[] { file });

        var specContent = """
            Name: multilib
            Version: 1.0
            License: Apache-2.0

            %package devel
            Summary: Devel package
            License: InvalidFakeLicense123

            %package static
            Summary: Static package
            License: MIT
            """;

        _gitHub.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), "SPECS/multilib/multilib.spec", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(specContent);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.ComplianceLicenseIssue, context.LabelsToAdd);
        Assert.NotEmpty(context.PendingComments);
        Assert.Contains("InvalidFakeLicense123", context.PendingComments[0]);
    }

    [Fact]
    public async Task ProcessAsync_SpecWithIssues_AlreadyFlagged_DoesNotDuplicateComment()
    {
        var service = CreateService();
        var options = Microsoft.Extensions.Options.Options.Create(new SpdxComplianceOptions());
        var module = new SpdxComplianceModule(service, options);

        var file = CreateFile("SPECS/multilib/multilib.spec");
        var context = CreateContext(
            new[] { file },
            action: PullRequestAction.Edited,
            existingLabels: new[] { GitHubLabels.ComplianceLicenseIssue });

        var specContent = """
            Name: multilib
            Version: 1.0
            License: InvalidFakeLicense123
            """;

        _gitHub.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), "SPECS/multilib/multilib.spec", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(specContent);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.ComplianceLicenseIssue, context.LabelsToAdd);
        Assert.Empty(context.PendingComments);
    }

    [Fact]
    public async Task ProcessAsync_NoSpecFilesRemaining_RemovesComplianceLicenseIssueLabel()
    {
        var service = CreateService();
        var options = Microsoft.Extensions.Options.Options.Create(new SpdxComplianceOptions());
        var module = new SpdxComplianceModule(service, options);

        // PR with a non-spec file, and previously had Compliance: License Issue
        var file = CreateFile("README.md");
        var context = CreateContext(new[] { file }, existingLabels: new[] { GitHubLabels.ComplianceLicenseIssue });

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.DoesNotContain(GitHubLabels.ComplianceLicenseIssue, context.LabelsToAdd);
        Assert.Contains(GitHubLabels.ComplianceLicenseIssue, context.LabelsToRemove);
        Assert.Empty(context.PendingComments);
    }

    [Theory]
    [InlineData("LGPL-3.0-only WITH Qt-GPL-exception-1.0")]
    [InlineData("GPL-3.0-only WITH Qt-GPL-exception-1.0")]
    [InlineData("LGPL-2.1-only WITH Qt-LGPL-exception-1.1")]
    [InlineData("BSD-3-Clause WITH mif-exception")]
    [InlineData("GPL-3.0-or-later WITH Texinfo-exception")]
    [InlineData("GPL-2.0-or-later WITH GNAT-exception")]
    [InlineData("Apache-2.0 WITH mxml-exception")]
    [InlineData("GPL-2.0-only WITH PS-or-PDF-font-exception-20170817")]
    [InlineData("GPL-3.0-only WITH GNOME-examples-exception")]
    public void SpdxLicenseService_OfficialExceptions_QtAndOtherExceptions_Valid(string expression)
    {
        var service = CreateService();
        var result = service.ValidateExpression(expression);

        Assert.True(result.IsValid, $"Expected '{expression}' to be valid with official exception.");
        Assert.Empty(result.InvalidIdentifiers);
    }

    [Theory]
    [InlineData("GPL-2.0-only WITH SWIG-exception")]
    [InlineData("GPL-2.0-only WITH Rust-license-exception")]
    [InlineData("GPL-2.0-only WITH PS-or-PDF-font-exception-20170904")]
    [InlineData("GPL-2.0-only WITH Qhull-exception")]
    [InlineData("GPL-2.0-only WITH tcsh-exception")]
    public void SpdxLicenseService_FabricatedExceptions_AreRejectedAsInvalid(string expression)
    {
        var service = CreateService();
        var result = service.ValidateExpression(expression);

        Assert.False(result.IsValid, $"Expected fabricated expression '{expression}' to be invalid.");
        Assert.NotEmpty(result.InvalidIdentifiers);
    }

    [Fact]
    public void SpdxExceptionsResource_ContainsExactOfficialV326List()
    {
        var service = CreateService();
        var asm = typeof(SpdxLicenseService).Assembly;
        var resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("spdx-exceptions.json", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(resName);

        using var stream = asm.GetManifestResourceStream(resName!);
        Assert.NotNull(stream);
        using var doc = System.Text.Json.JsonDocument.Parse(stream!);

        Assert.Equal("3.26.0", doc.RootElement.GetProperty("licenseListVersion").GetString());
        var exceptionsArray = doc.RootElement.GetProperty("exceptions");
        Assert.Equal(77, exceptionsArray.GetArrayLength());

        // Official items present
        Assert.True(service.IsValidSpdxException("Autoconf-exception-generic-3.0"));
        Assert.True(service.IsValidSpdxException("SANE-exception"));
        Assert.True(service.IsValidSpdxException("harbour-exception"));
        Assert.True(service.IsValidSpdxException("LLVM-exception"));

        // Non-standard items removed
        Assert.False(service.IsValidSpdxException("Digia-Qt-LGPL-exception-1.1"));
        Assert.False(service.IsValidSpdxException("rsync-linking-exception"));
    }

    [Fact]
    public async Task SpdxLicenseService_RefreshRemoteMetadataAsync_LoadsLicensesAndExceptions()
    {
        var licenseJson = """
        {
          "licenses": [
            { "licenseId": "Custom-Test-Lic-999" }
          ]
        }
        """;

        var exceptionJson = """
        {
          "exceptions": [
            { "licenseExceptionId": "Custom-Test-Ex-888" }
          ]
        }
        """;

        var handler = new SimpleHttpHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("exceptions.json"))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(exceptionJson, System.Text.Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(licenseJson, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var client = new HttpClient(handler);
        var clientFactory = Substitute.For<IHttpClientFactory>();
        clientFactory.CreateClient("SpdxLicenseClient").Returns(client);

        var options = Microsoft.Extensions.Options.Options.Create(new SpdxComplianceOptions
        {
            UpdateRemoteUrl = "https://example.org/licenses.json",
            UpdateExceptionsRemoteUrl = "https://example.org/exceptions.json"
        });

        var service = new SpdxLicenseService(clientFactory, options);

        Assert.False(service.IsValidSpdxIdentifier("Custom-Test-Lic-999"));
        Assert.False(service.IsValidSpdxException("Custom-Test-Ex-888"));

        var success = await service.RefreshRemoteMetadataAsync(CancellationToken.None);
        Assert.True(success);

        Assert.True(service.IsValidSpdxIdentifier("Custom-Test-Lic-999"));
        Assert.True(service.IsValidSpdxException("Custom-Test-Ex-888"));
    }

    [Fact]
    public async Task SpdxLicenseService_RefreshRemoteMetadataAsync_HttpError_ReturnsFalseGracefully()
    {
        var handler = new SimpleHttpHandler(req =>
            new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));

        var client = new HttpClient(handler);
        var clientFactory = Substitute.For<IHttpClientFactory>();
        clientFactory.CreateClient("SpdxLicenseClient").Returns(client);

        var options = Microsoft.Extensions.Options.Options.Create(new SpdxComplianceOptions
        {
            UpdateRemoteUrl = "https://example.org/licenses.json",
            UpdateExceptionsRemoteUrl = "https://example.org/exceptions.json"
        });

        var service = new SpdxLicenseService(clientFactory, options);
        var result = await service.RefreshRemoteMetadataAsync(CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task ProcessAsync_WhenGetFileContentThrows_DoesNotRemoveExistingLicenseIssueLabel()
    {
        var service = CreateService();
        var options = Microsoft.Extensions.Options.Options.Create(new SpdxComplianceOptions());
        var module = new SpdxComplianceModule(service, options);

        var file = CreateFile("SPECS/test/test.spec");
        var context = CreateContext(
            new[] { file },
            existingLabels: new[] { GitHubLabels.ComplianceLicenseIssue });

        _gitHub.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), "SPECS/test/test.spec", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new HttpRequestException("404 Not Found"));

        await module.ProcessAsync(context, CancellationToken.None);

        // Fail safe: must NOT remove Compliance: License Issue when content was unverified
        Assert.DoesNotContain(GitHubLabels.ComplianceLicenseIssue, context.LabelsToRemove);
        Assert.DoesNotContain(GitHubLabels.ComplianceLicenseIssue, context.LabelsToAdd);
        Assert.Empty(context.PendingComments);
    }

    [Fact]
    public async Task SpdxLicenseService_RefreshRemoteMetadataAsync_WhenRemoteRemovesLicense_ReconcilesAndRemovesLocally()
    {
        var licenseJson = """
        {
          "licenses": [
            { "licenseId": "Apache-2.0" },
            { "licenseId": "MIT" }
          ]
        }
        """;

        var exceptionJson = """
        {
          "exceptions": [
            { "licenseExceptionId": "LLVM-exception" }
          ]
        }
        """;

        var handler = new SimpleHttpHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("exceptions.json"))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(exceptionJson, System.Text.Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(licenseJson, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var client = new HttpClient(handler);
        var clientFactory = Substitute.For<IHttpClientFactory>();
        clientFactory.CreateClient("SpdxLicenseClient").Returns(client);

        var options = Microsoft.Extensions.Options.Options.Create(new SpdxComplianceOptions
        {
            UpdateRemoteUrl = "https://example.org/licenses.json",
            UpdateExceptionsRemoteUrl = "https://example.org/exceptions.json"
        });

        var service = new SpdxLicenseService(clientFactory, options);

        // Initial state before refresh: contains 0BSD from embedded baseline
        Assert.True(service.IsValidSpdxIdentifier("0BSD"));
        Assert.True(service.IsValidSpdxException("GCC-exception-3.1"));

        var success = await service.RefreshRemoteMetadataAsync(CancellationToken.None);
        Assert.True(success);

        // After refresh: only items in new remote list exist; removed items are reconciled
        Assert.True(service.IsValidSpdxIdentifier("Apache-2.0"));
        Assert.True(service.IsValidSpdxIdentifier("MIT"));
        Assert.False(service.IsValidSpdxIdentifier("0BSD")); // Reconciled and removed

        Assert.True(service.IsValidSpdxException("LLVM-exception"));
        Assert.False(service.IsValidSpdxException("GCC-exception-3.1")); // Reconciled and removed
    }

    [Fact]
    public async Task SpdxLicenseService_RefreshRemoteMetadataAsync_WhenRemoteReturnsEmpty_RejectsAndRetainsExisting()
    {
        var emptyLicenseJson = """
        {
          "licenses": []
        }
        """;

        var handler = new SimpleHttpHandler(req =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(emptyLicenseJson, System.Text.Encoding.UTF8, "application/json")
            });

        var client = new HttpClient(handler);
        var clientFactory = Substitute.For<IHttpClientFactory>();
        clientFactory.CreateClient("SpdxLicenseClient").Returns(client);

        var options = Microsoft.Extensions.Options.Options.Create(new SpdxComplianceOptions
        {
            UpdateRemoteUrl = "https://example.org/licenses.json",
            UpdateExceptionsRemoteUrl = string.Empty // disabled exceptions
        });

        var service = new SpdxLicenseService(clientFactory, options);
        Assert.True(service.IsValidSpdxIdentifier("Apache-2.0"));

        var result = await service.RefreshRemoteMetadataAsync(CancellationToken.None);
        Assert.False(result);

        // Existing dictionary retained, not emptied!
        Assert.True(service.IsValidSpdxIdentifier("Apache-2.0"));
    }

    private sealed class SimpleHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public SimpleHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }
}

