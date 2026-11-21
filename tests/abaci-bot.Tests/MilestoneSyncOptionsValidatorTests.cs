using abaci_bot.Options;
using Xunit;

namespace abaci_bot.Tests;

public class MilestoneSyncOptionsValidatorTests
{
    private readonly MilestoneSyncOptionsValidator _validator = new();

    [Fact]
    public void Validate_DefaultOptions_ReturnsSuccess()
    {
        var options = new MilestoneSyncOptions();
        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(29)]
    [InlineData(31)]
    public void Validate_DueDayOfMonthOutOfRange_ReturnsFailure(int dueDay)
    {
        var options = new MilestoneSyncOptions { DueDayOfMonth = dueDay };
        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.DueDayOfMonth)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(366)]
    public void Validate_BackfillDaysOutOfRange_ReturnsFailure(int days)
    {
        var options = new MilestoneSyncOptions { BackfillDays = days };
        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.BackfillDays)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(169)]
    public void Validate_SweepIntervalHoursOutOfRange_ReturnsFailure(int hours)
    {
        var options = new MilestoneSyncOptions { SweepIntervalHours = hours };
        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.SweepIntervalHours)));
    }

    [Theory]
    [InlineData("Asia/Shanghai")]
    [InlineData("UTC")]
    [InlineData("America/New_York")]
    public void Validate_ValidTimeZoneId_ReturnsSuccess(string timeZoneId)
    {
        var options = new MilestoneSyncOptions { TimeZoneId = timeZoneId };
        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Invalid/Timezone_DoesNotExist")]
    public void Validate_InvalidTimeZoneId_ReturnsFailure(string timeZoneId)
    {
        var options = new MilestoneSyncOptions { TimeZoneId = timeZoneId };
        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.TimeZoneId)));
    }

    [Theory]
    [InlineData("openRuyi")]
    [InlineData("owner/repo/extra")]
    [InlineData("owner/")]
    public void Validate_InvalidRepositoryAllowListEntry_ReturnsFailure(string entry)
    {
        var options = new MilestoneSyncOptions { RepositoryAllowList = new List<string> { entry } };
        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.RepositoryAllowList)));
    }
}
