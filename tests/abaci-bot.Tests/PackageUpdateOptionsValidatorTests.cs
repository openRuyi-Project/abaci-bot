using abaci_bot.Options;
using Xunit;

namespace abaci_bot.Tests;

public class PackageUpdateOptionsValidatorTests
{
    private readonly PackageUpdateOptionsValidator _validator = new();

    [Fact]
    public void Validate_DefaultOptions_ReturnsSuccess()
    {
        var options = new PackageUpdateOptions();
        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveCheckIntervalHours_ReturnsFailure(int hours)
    {
        var options = new PackageUpdateOptions { CheckIntervalHours = hours };
        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.CheckIntervalHours)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_NonPositiveMaxCandidatesPerSweep_ReturnsFailure(int max)
    {
        var options = new PackageUpdateOptions { MaxCandidatesPerSweep = max };
        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.MaxCandidatesPerSweep)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid-repo")]
    [InlineData("too/many/parts")]
    public void Validate_InvalidTargetRepository_ReturnsFailure(string repo)
    {
        var options = new PackageUpdateOptions { TargetRepository = repo };
        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.TargetRepository)));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1024, true)]
    public void Validate_MaxArchiveBytes_RejectsNegativeAndAllowsZeroOrPositive(long maxArchiveBytes, bool expectedSuccess)
    {
        var options = new PackageUpdateOptions { MaxArchiveBytes = maxArchiveBytes };
        var result = _validator.Validate(null, options);

        Assert.Equal(expectedSuccess, result.Succeeded);
        if (!expectedSuccess)
        {
            Assert.Contains(result.Failures!, f => f.Contains(nameof(options.MaxArchiveBytes)));
        }
    }
}
