namespace PTGOilSystem.Web.Services.Exports;

public sealed class TabularExportOptions
{
    public const string SectionName = "Exports";

    public int ExcelMaxRows { get; set; } = 50_000;
    public int PdfMaxRows { get; set; } = 10_000;
    public string CompanyNameFa { get; set; } = "PTG Oil System";
    public string CompanyNameEn { get; set; } = "PTG Oil System";
    public string CompanyLogoPath { get; set; } = "/images/logo1-sidebar.png";
    public string CompanyPhone { get; set; } = "+92 21 711 722 399";
    public string CompanyEmail { get; set; } = "info@saddiqigroup.com";
    public string CompanyWebsite { get; set; } = "www.saddiqigroup.com";
    public string QuestPdfLicense { get; set; } = "Community";
}

