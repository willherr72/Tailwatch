using Tailwatch.Core;
using Xunit;
namespace Tailwatch.Tests;
public class ContractTests
{
    [Fact] public void ValidZeroIsDifferentFromMissingReading()
    {
        Assert.True(new Reading(0, "ok", 0).IsFresh);
        Assert.False(new Reading(null, "unsupported", 0).IsFresh);
    }
    [Theory]
    [InlineData(-1)] [InlineData(101)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidPercentagesAreRejected(double value) => Assert.False(Protocol.ValidPercentage(value));
    [Fact] public void OldOrFailedValuesAreNotFresh()
    {
        Assert.False(new Reading(45, "ok", 10001).IsFresh);
        Assert.False(new Reading(45, "collection_failed", 0).IsFresh);
    }
}

