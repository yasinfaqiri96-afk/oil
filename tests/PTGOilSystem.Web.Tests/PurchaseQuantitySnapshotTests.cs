using PTGOilSystem.Web.Services.Accounting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class PurchaseQuantitySnapshotTests
{
    [Theory]
    [InlineData("Purchase [QuantityMt:v1=100.0999]", "100.0999")]
    [InlineData("Purchase [QuantityMt:v1=0.0001]", "0.0001")]
    public void Valid_Immutable_Metadata_RoundTrips(string description, string expected)
    {
        Assert.True(PurchaseQuantitySnapshot.TryRead(description, out var quantity));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), quantity);
        Assert.Equal(description, PurchaseQuantitySnapshot.Append("Purchase", quantity));
    }

    [Theory]
    [InlineData("Purchase")]
    [InlineData("Purchase [QuantityMt:v2=100.0000]")]
    [InlineData("Purchase [QuantityMt:v1=0.0000]")]
    [InlineData("Purchase [QuantityMt:v1=-10.0000]")]
    [InlineData("Purchase [QuantityMt:v1=10.12345]")]
    [InlineData("Purchase [QuantityMt:v1=10.0000] [QuantityMt:v1=20.0000]")]
    public void Untrusted_Or_Unknown_Metadata_Is_Not_A_Financial_Basis(string description)
        => Assert.False(PurchaseQuantitySnapshot.TryRead(description, out _));

    [Fact]
    public void New_Snapshot_Rejects_Invalid_Quantity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PurchaseQuantitySnapshot.Append("Purchase", 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => PurchaseQuantitySnapshot.Append("Purchase", 10.12345m));
    }
}
