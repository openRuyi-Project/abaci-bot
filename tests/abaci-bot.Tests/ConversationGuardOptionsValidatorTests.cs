using abaci_bot.Options;
using Xunit;

namespace abaci_bot.Tests;

public class ConversationGuardOptionsValidatorTests
{
    private readonly ConversationGuardOptionsValidator _validator = new();

    [Fact]
    public void Validate_DefaultOptions_ReturnsSuccess()
    {
        var options = new ConversationGuardOptions();
        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(366)]
    public void Validate_InactiveCloseDaysOutOfRange_ReturnsFailure(int days)
    {
        var options = new ConversationGuardOptions { InactiveCloseDays = days };
        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.InactiveCloseDays)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(73)]
    public void Validate_SweepIntervalHoursOutOfRange_ReturnsFailure(int hours)
    {
        var options = new ConversationGuardOptions { SweepIntervalHours = hours };
        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.SweepIntervalHours)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void Validate_AuthorLockConfirmTimeoutMinutesOutOfRange_ReturnsFailure(int minutes)
    {
        var options = new ConversationGuardOptions { AuthorLockConfirmTimeoutMinutes = minutes };
        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.AuthorLockConfirmTimeoutMinutes)));
    }

    [Theory]
    [InlineData("openRuyi")]
    [InlineData("bad/repo/path")]
    [InlineData("/repo")]
    public void Validate_InvalidRepositoryAllowListEntry_ReturnsFailure(string entry)
    {
        var options = new ConversationGuardOptions { RepositoryAllowList = new List<string> { entry } };
        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.RepositoryAllowList)));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(501, false)]
    [InlineData(1, true)]
    [InlineData(500, true)]
    public void Validate_MaxLocksPerSweep_EnforcesBounds1To500(int maxLocks, bool expectedSuccess)
    {
        var options = new ConversationGuardOptions { MaxLocksPerSweep = maxLocks };
        var result = _validator.Validate(null, options);

        Assert.Equal(expectedSuccess, result.Succeeded);
        if (!expectedSuccess)
        {
            Assert.Contains(result.Failures!, f => f.Contains(nameof(options.MaxLocksPerSweep)));
        }
    }
}
