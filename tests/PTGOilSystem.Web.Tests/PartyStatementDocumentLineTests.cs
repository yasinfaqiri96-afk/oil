using PTGOilSystem.Web.Models.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>خط سوم شرح صورت‌حساب: مرجع سند و موتر کنار هم؛ بدون موتر فقط مرجع، بدون جداکنندهٔ اضافه.</summary>
public class PartyStatementDocumentLineTests
{
    [Fact]
    public void Sale_Row_Shows_Invoice_And_Truck_On_One_Line()
    {
        var row = new PartyStatementRow { DocumentLabel = "فاکتور INV001", VehicleLabel = "موتر 12345" };

        Assert.Equal("فاکتور INV001 · موتر 12345", row.DocumentLine);
    }

    [Fact]
    public void Row_Without_Truck_Shows_Only_The_Document()
    {
        var row = new PartyStatementRow { DocumentLabel = "رسید P-4" };

        Assert.Equal("رسید P-4", row.DocumentLine);
    }

    [Fact]
    public void Opening_Balance_Has_No_Document_Line()
    {
        var row = new PartyStatementRow { IsOpeningBalance = true, DocumentLabel = "فاکتور INV001", VehicleLabel = "موتر 1" };

        Assert.Null(row.DocumentLine);
    }
}
