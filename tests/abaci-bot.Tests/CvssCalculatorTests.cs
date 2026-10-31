using abaci_bot.Services;
using Xunit;

namespace abaci_bot.Tests;

public class CvssCalculatorTests
{
    [Theory]
    // Official FIRST CVSS v3.1 examples & standard vectors:
    [InlineData("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H", 9.8)]
    [InlineData("CVSS:3.1/AV:N/AC:L/PR:N/UI:R/S:C/C:L/I:L/A:N", 6.1)]
    [InlineData("CVSS:3.1/AV:L/AC:L/PR:N/UI:R/S:U/C:H/I:H/A:H", 7.8)]
    [InlineData("CVSS:3.1/AV:N/AC:H/PR:N/UI:N/S:U/C:L/I:N/A:N", 3.7)]
    [InlineData("CVSS:3.1/AV:P/AC:L/PR:N/UI:N/S:U/C:N/I:N/A:N", 0.0)]
    [InlineData("CVSS:3.1/AV:N/AC:L/PR:H/UI:N/S:C/C:H/I:H/A:H", 9.1)]
    [InlineData("CVSS:3.0/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H", 9.8)]
    public void CalculateBaseScore_ValidVectors_ReturnsExactScore(string vector, double expectedScore)
    {
        var score = CvssCalculator.CalculateBaseScore(vector);
        Assert.NotNull(score);
        Assert.Equal(expectedScore, score.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NOT_A_VECTOR")]
    [InlineData("CVSS:3.1/AV:N/AC:L")] // Incomplete
    [InlineData("CVSS:3.1/AV:X/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H")] // Invalid metric value
    [InlineData("CVSS:2.0/AV:N/AC:L/Au:N/C:P/I:P/A:P")] // CVSS 2.0 not supported by v3 calculator
    public void CalculateBaseScore_InvalidOrIncompleteVectors_ReturnsNull(string? vector)
    {
        var score = CvssCalculator.CalculateBaseScore(vector);
        Assert.Null(score);
    }
}
