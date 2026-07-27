namespace PTGOilSystem.Web.Services.Exports;

public interface ITabularExportService
{
    int GetRowLimit(TabularExportFormat format);

    Task WriteAsync(
        TabularExportDocument document,
        TabularExportFormat format,
        bool isEnglish,
        Stream destination,
        CancellationToken cancellationToken);

    // چند شیت/بخش در یک فایل: Excel هر سند را یک worksheet و PDF هر سند را یک بخش می‌کند.
    Task WriteWorkbookAsync(
        IReadOnlyList<TabularExportDocument> sheets,
        TabularExportFormat format,
        bool isEnglish,
        Stream destination,
        CancellationToken cancellationToken);

    Task WritePartyStatementPdfAsync(
        Models.PartyStatements.PartyStatementResult statement,
        Stream destination,
        CancellationToken cancellationToken);

    // همان سند رسمی صورت‌حساب (لوگو، سربرگ شرکت، خلاصهٔ مالی، امضا) با جدول خلاصهٔ
    // قراردادی به‌جای جدول گردش حساب — برای تب «قراردادها».
    Task WriteSupplierContractStatementPdfAsync(
        Models.PartyStatements.PartyStatementResult statement,
        Models.PartyStatements.SupplierContractStatementViewModel contractGrouping,
        Stream destination,
        CancellationToken cancellationToken);
}

