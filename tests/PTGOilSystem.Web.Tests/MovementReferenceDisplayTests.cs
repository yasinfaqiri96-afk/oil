using PTGOilSystem.Web.Helpers;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class MovementReferenceDisplayTests
{
    private static string Fa(string fa, string en) => fa;

    [Theory]
    [InlineData(null, "—")]
    [InlineData("  ", "—")]
    [InlineData("BULK-RCPT-2-20261027150348 / 1383461", "1383461")]
    [InlineData("BULK-RCPT-2-20261027150348", "رسید گروهی")]
    [InlineData("LOADING-RECEIPT:12:0f8fad5bd9cb469fa16570867728950e", "رسید بارگیری #12")]
    [InlineData("TRANSPORT-ALLOCATION:7", "حمل #7")]
    [InlineData("TRUCK-UNLOAD:3-CANCEL", "تخلیهٔ موتر #3 (لغو)")]
    [InlineData("INV-2026/15", "INV-2026/15")]
    [InlineData("UNKNOWN-KIND:5", "UNKNOWN-KIND:5")]
    public void Short_Label_Keeps_User_Text_And_Compacts_System_References(string? reference, string expected)
        => Assert.Equal(expected, MovementReferenceDisplay.Short(reference, Fa));
}
