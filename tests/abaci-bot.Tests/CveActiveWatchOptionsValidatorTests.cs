using abaci_bot.Options;
using Xunit;

namespace abaci_bot.Tests;

public class CveActiveWatchOptionsValidatorTests
{
    private readonly CveActiveWatchOptionsValidator _validator = new();

    [Fact]
    public void Validate_DefaultOptions_ReturnsSuccess()
    {
        var options = new CveActiveWatchOptions();
        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    [InlineData(-10)]
    public void Validate_InvalidOsvBatchSize_ReturnsFailure(int batchSize)
    {
        var options = new CveActiveWatchOptions { OsvBatchSize = batchSize };
        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.OsvBatchSize)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid-repo")]
    public void Validate_InvalidTargetRepository_ReturnsFailure(string repo)
    {
        var options = new CveActiveWatchOptions { TargetRepository = repo };
        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.TargetRepository)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveMaxConcurrentOsvRequests_ReturnsFailure(int max)
    {
        var options = new CveActiveWatchOptions { MaxConcurrentOsvRequests = max };
        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.MaxConcurrentOsvRequests)));
    }
}
