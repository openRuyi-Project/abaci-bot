using abaci_bot.Options;
using Xunit;

namespace abaci_bot.Tests;

public class RebuildImpactOptionsValidatorTests
{
    private readonly RebuildImpactOptionsValidator _validator = new();

    [Fact]
    public void Validate_DefaultOptions_ReturnsSuccess()
    {
        var result = _validator.Validate(null, new RebuildImpactOptions());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_NullOrEmptyVerificationGuideLinks_ReturnsSuccess()
    {
        Assert.True(_validator.Validate(null, new RebuildImpactOptions { VerificationGuideLinks = null! }).Succeeded);
        Assert.True(_validator.Validate(null, new RebuildImpactOptions { VerificationGuideLinks = new List<VerificationReferenceLink>() }).Succeeded);
    }

    [Fact]
    public void Validate_ValidVerificationGuideLinks_ReturnsSuccess()
    {
        var options = new RebuildImpactOptions
        {
            VerificationGuideLinks = new List<VerificationReferenceLink>
            {
                new()
                {
                    Title = "OBS Reverse-Dependency Workflow",
                    TitleZh = "OBS 反向依赖验证指南",
                    Url = "https://homepage.openruyi.cn/guide/build-platform-tips"
                },
                new()
                {
                    Title = "Build Policy",
                    Url = "http://docs.example.org/policy"
                }
            }
        };

        var result = _validator.Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ExceedingMaxVerificationGuideLinks_ReturnsFailure()
    {
        var links = Enumerable.Range(1, RebuildImpactOptionsValidator.MaxVerificationGuideLinks + 1)
            .Select(i => new VerificationReferenceLink
            {
                Title = $"Guide {i}",
                Url = $"https://docs.example.org/guide-{i}"
            })
            .ToList();

        var options = new RebuildImpactOptions { VerificationGuideLinks = links };
        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.VerificationGuideLinks)));
    }

    [Fact]
    public void Validate_NullVerificationGuideLinkEntry_ReturnsFailure()
    {
        var options = new RebuildImpactOptions
        {
            VerificationGuideLinks = new List<VerificationReferenceLink> { null! }
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("cannot be null"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Line1\nLine2")]
    [InlineData("Has\tTab")]
    public void Validate_InvalidLinkTitle_ReturnsFailure(string invalidTitle)
    {
        var options = new RebuildImpactOptions
        {
            VerificationGuideLinks = new List<VerificationReferenceLink>
            {
                new() { Title = invalidTitle, Url = "https://docs.example.org/guide" }
            }
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(".Title"));
    }

    [Fact]
    public void Validate_OverlongTitleAndInvalidTitleZh_ReturnsFailure()
    {
        var tooLongTitle = new string('A', RebuildImpactOptionsValidator.MaxLinkTitleLength + 1);
        var options = new RebuildImpactOptions
        {
            VerificationGuideLinks = new List<VerificationReferenceLink>
            {
                new() { Title = tooLongTitle, TitleZh = "Line1\nLine2", Url = "https://docs.example.org/a" },
                new() { Title = "Valid", TitleZh = tooLongTitle, Url = "https://docs.example.org/b" }
            }
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(".Title "));
        Assert.Contains(result.Failures, f => f.Contains(".TitleZh "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://docs.example.org/with space")]
    [InlineData("not-a-url")]
    [InlineData("/relative/guide.md")]
    [InlineData("ftp://example.com/guide")]
    public void Validate_InvalidLinkUrl_ReturnsFailure(string invalidUrl)
    {
        var options = new RebuildImpactOptions
        {
            VerificationGuideLinks = new List<VerificationReferenceLink>
            {
                new() { Title = "Valid Title", Url = invalidUrl }
            }
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(".Url"));
    }

    [Fact]
    public void Validate_DuplicateNormalizedUrls_ReturnsFailure()
    {
        var options = new RebuildImpactOptions
        {
            VerificationGuideLinks = new List<VerificationReferenceLink>
            {
                new() { Title = "Guide 1", Url = "https://Docs.Example.org/guide" },
                new() { Title = "Guide 2", Url = "https://docs.example.org/guide" }
            }
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("duplicates"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveRefreshInterval_ReturnsFailure(double hours)
    {
        var options = new RebuildImpactOptions { RefreshIntervalHours = hours };
        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.RefreshIntervalHours)));
    }

    [Fact]
    public void Validate_NegativeBlockThreshold_ReturnsFailure()
    {
        var options = new RebuildImpactOptions { BlockThreshold = -1 };
        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.BlockThreshold)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void Validate_InvalidMaxDepth_ReturnsFailure(int maxDepth)
    {
        var options = new RebuildImpactOptions { MaxDepth = maxDepth };
        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.MaxDepth)));
    }

    [Fact]
    public void Validate_UnsupportedCalculationMode_ReturnsFailure()
    {
        var options = new RebuildImpactOptions { CalculationMode = "Approximate" };
        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.CalculationMode)));
    }

    [Theory]
    [InlineData("Direct", 3)]
    [InlineData("transitive", 1)]
    public void Validate_ValidCalculationModeAndMaxDepth_ReturnsSuccess(string mode, int maxDepth)
    {
        var options = new RebuildImpactOptions { CalculationMode = mode, MaxDepth = maxDepth };
        var result = _validator.Validate(null, options);
        Assert.True(result.Succeeded);
    }
}
