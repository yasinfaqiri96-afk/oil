namespace PTGOilSystem.Web.Models.Documents;

public sealed class DocumentInvoiceViewModel
{
    public string TitleFa { get; init; } = "";
    public string TitleEn { get; init; } = "";
    public string BreadcrumbFa { get; init; } = "";
    public string BreadcrumbEn { get; init; } = "";
    public string DocumentNumber { get; init; } = "";
    public DateTime DocumentDate { get; init; }
    public string StatusFa { get; init; } = "";
    public string StatusEn { get; init; } = "";
    public string Tone { get; init; } = "neutral";
    public string BrandName { get; init; } = "PTG Oil System";
    public string? BrandSubtitleFa { get; init; }
    public string? BrandSubtitleEn { get; init; }
    public DocumentInvoicePartyViewModel FromParty { get; init; } = new();
    public DocumentInvoicePartyViewModel ToParty { get; init; } = new();
    public DocumentInvoicePaymentBoxViewModel PaymentBox { get; init; } = new();
    public IReadOnlyList<DocumentInvoiceLineViewModel> Lines { get; init; } = [];
    public IReadOnlyList<DocumentInvoiceTotalRowViewModel> Totals { get; init; } = [];
    public string? NotesFa { get; init; }
    public string? NotesEn { get; init; }
    public string? SourceReference { get; init; }
    public string BackController { get; init; } = "Home";
    public string BackAction { get; init; } = "Index";
    public Dictionary<string, string> BackRouteValues { get; init; } = [];
}

public sealed class DocumentInvoicePartyViewModel
{
    public string HeadingFa { get; init; } = "";
    public string HeadingEn { get; init; } = "";
    public string Name { get; init; } = "";
    public IReadOnlyList<string> Details { get; init; } = [];
}

public sealed class DocumentInvoicePaymentBoxViewModel
{
    public string HeadingFa { get; init; } = "";
    public string HeadingEn { get; init; } = "";
    public string AmountText { get; init; } = "";
    public string ReferenceText { get; init; } = "";
    public string? NoteFa { get; init; }
    public string? NoteEn { get; init; }
}

public sealed class DocumentInvoiceLineViewModel
{
    public string Number { get; init; } = "";
    public string Item { get; init; } = "";
    public string Description { get; init; } = "";
    public string UnitCost { get; init; } = "";
    public string Quantity { get; init; } = "";
    public string Total { get; init; } = "";
}

public sealed class DocumentInvoiceTotalRowViewModel
{
    public string LabelFa { get; init; } = "";
    public string LabelEn { get; init; } = "";
    public string Value { get; init; } = "";
    public bool IsGrandTotal { get; init; }
}
