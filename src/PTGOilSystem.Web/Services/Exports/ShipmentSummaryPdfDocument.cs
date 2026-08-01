using PTGOilSystem.Web.Models.ShipmentPnl;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PTGOilSystem.Web.Services.Exports;

/// <summary>
/// Formal shipment summary using the shared PDF design system. The document only
/// presents values prepared by ShipmentPnlDetailsViewModel and performs no queries
/// or business calculations.
/// </summary>
internal sealed class ShipmentSummaryPdfDocument(
    ShipmentSummaryPdfModel model,
    PdfBrandHeader brandHeader,
    bool isEnglish = false) : IDocument
{
    private const float SectionTitleSize = PdfDesignSystem.MetaSize;
    private const float LabelSize = PdfDesignSystem.ReportMetaSize;
    private const float ValueSize = PdfDesignSystem.NumericTableSize;

    public DocumentMetadata GetMetadata() => new()
    {
        Title = Label("خلاصه محموله", "Shipment summary"),
        Author = string.IsNullOrWhiteSpace(model.CompanyName) ? brandHeader.CompanyName : model.CompanyName,
        Subject = model.ShipmentCode
    };

    public DocumentSettings GetSettings() => DocumentSettings.Default;

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.MarginHorizontal(PdfDesignSystem.HorizontalMargin);
            page.MarginTop(PdfDesignSystem.TopMargin);
            page.MarginBottom(PdfDesignSystem.BottomMargin);
            page.PageColor(Colors.White);
            page.DefaultTextStyle(style => PdfDesignSystem.DefaultTextStyle(style, isEnglish)
                .FontFamily(isEnglish
                    ? PdfDesignSystem.EnglishFallbackFont
                    : PdfDesignSystem.PersianFallbackFont));
            page.Content().Element(Direct).Column(ComposePage);
            page.Footer().PaddingTop(8).Element(footer => PdfDesignSystem.ComposeFooter(
                footer,
                string.IsNullOrWhiteSpace(model.CompanyName) ? brandHeader.CompanyName : model.CompanyName,
                isEnglish));
        });
    }

    private IContainer Direct(IContainer container)
        => isEnglish ? container.ContentFromLeftToRight() : container.ContentFromRightToLeft();

    private static IContainer Ltr(IContainer container) => container.ContentFromLeftToRight();

    private void ComposePage(ColumnDescriptor column)
    {
        column.Spacing(9);
        column.Item().Element(header => PdfDesignSystem.ComposeBrandHeader(header, brandHeader));
        column.Item().Element(ComposeDocumentTitle);
        column.Item().Element(ComposeShipmentIdentity);
        column.Item().PaddingTop(2).Element(container => ComposeSectionTitle(
            container,
            Label("خلاصه عملیات محموله", "Shipment operations summary")));
        column.Item().Element(ComposeStagesTable);
    }

    private void ComposeDocumentTitle(IContainer container)
    {
        container.ContentFromLeftToRight().Column(column =>
        {
            column.Item().Row(row =>
            {
                if (isEnglish)
                {
                    row.RelativeItem().AlignLeft().Text(Label("خلاصه محموله", "Shipment summary"))
                        .Bold().FontSize(PdfDesignSystem.TitleSize).FontColor(PdfDesignSystem.Ink);
                    row.ConstantItem(160).AlignRight().Element(Ltr)
                        .Text(FormatGregorianPrintDate(model.GeneratedAt))
                        .FontSize(PdfDesignSystem.ReportMetaSize).FontColor(PdfDesignSystem.Muted);
                }
                else
                {
                    row.ConstantItem(160).AlignLeft().ContentFromRightToLeft()
                        .Text(FormatGregorianPrintDate(model.GeneratedAt))
                        .FontSize(PdfDesignSystem.ReportMetaSize).FontColor(PdfDesignSystem.Muted);
                    row.RelativeItem().AlignRight().ContentFromRightToLeft()
                        .Text(Label("خلاصه محموله", "Shipment summary"))
                        .Bold().FontSize(PdfDesignSystem.TitleSize).FontColor(PdfDesignSystem.Ink);
                }
            });
            column.Item().PaddingTop(4).Height(0.8f).Background(PdfDesignSystem.StrongRule);
        });
    }

    private void ComposeShipmentIdentity(IContainer container)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(72);
                columns.RelativeColumn();
                columns.ConstantColumn(72);
                columns.RelativeColumn();
            });

            MetaRow(table,
                Label("نام کشتی", "Vessel"), model.VesselName, false,
                Label("وضعیت", "Status"), model.StatusText, false);
            MetaRow(table,
                Label("کد محموله", "Shipment code"), model.ShipmentCode, true,
                Label("محصول", "Product"), model.ProductName, false);
            MetaRow(table,
                Label("قرارداد", "Contract"), model.ContractNumber, true,
                Label("مسیر", "Route"), $"{model.Origin} - {model.Destination}", false);
            MetaRow(table,
                Label("تاریخ حرکت", "Departure"), model.DepartureDateText, true,
                Label("تاریخ رسیدن", "Arrival"), model.ArrivalDateText, true);
        });
    }

    private void MetaRow(
        TableDescriptor table,
        string firstLabel,
        string firstValue,
        bool firstIsLtr,
        string secondLabel,
        string secondValue,
        bool secondIsLtr)
    {
        table.Cell().Element(MetaLabelCell).Text(firstLabel).SemiBold().FontSize(LabelSize);
        table.Cell().Element(cell => MetaValueCell(cell, firstIsLtr)).Text(firstValue).FontSize(ValueSize);
        table.Cell().Element(MetaLabelCell).Text(secondLabel).SemiBold().FontSize(LabelSize);
        table.Cell().Element(cell => MetaValueCell(cell, secondIsLtr)).Text(secondValue).FontSize(ValueSize);
    }

    private static IContainer MetaLabelCell(IContainer container)
        => PdfDesignSystem.HeaderCell(container)
            .BorderBottom(0.75f).BorderColor(PdfDesignSystem.Border)
            .AlignRight();

    private IContainer MetaValueCell(IContainer container, bool ltr)
    {
        var target = PdfDesignSystem.BodyCell(container)
            .BorderBottom(0.75f).BorderColor(PdfDesignSystem.Border);
        return ltr ? target.Element(Ltr).AlignRight() : target.AlignRight();
    }

    private static void ComposeSectionTitle(IContainer container, string title)
    {
        container.PaddingBottom(3).Text(title)
            .SemiBold().FontSize(SectionTitleSize).FontColor(PdfDesignSystem.Ink);
    }

    private void ComposeStagesTable(IContainer container)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(1.2f);
                columns.RelativeColumn(1.05f);
                columns.RelativeColumn();
                columns.RelativeColumn();
                columns.RelativeColumn();
            });

            table.Header(header =>
            {
                HeaderCell(header.Cell(), Label("مرحله", "Stage"));
                HeaderCell(header.Cell(), Label("وضعیت", "Status"));
                HeaderCell(header.Cell().ColumnSpan(3), Label("جزئیات جریان", "Flow details"));
                header.Cell().ColumnSpan(5).Element(cell => PdfDesignSystem.TableSeparator(cell, 1.5f));
            });

            for (var index = 0; index < model.Stages.Count; index++)
            {
                var stage = model.Stages[index];
                var isFinancialResult = index == model.Stages.Count - 1;
                string background = isFinancialResult ? PdfDesignSystem.TotalsBackground : "#FFFFFF";

                StageTextCell(table.Cell(), stage.Title, background, semiBold: true);
                StageStatusCell(table.Cell(), stage, background);

                for (var metricIndex = 0; metricIndex < 3; metricIndex++)
                {
                    if (metricIndex < stage.Metrics.Count)
                    {
                        StageMetricCell(table.Cell(), stage.Metrics[metricIndex], background, isFinancialResult);
                    }
                    else
                    {
                        StageTextCell(table.Cell(), "-", background);
                    }
                }

                table.Cell().ColumnSpan(5).Element(cell => PdfDesignSystem.TableSeparator(
                    cell,
                    isFinancialResult ? 1.5f : 0.75f));
            }
        });
    }

    private static void HeaderCell(IContainer container, string text)
        => container.Element(cell => PdfDesignSystem.HeaderCell(cell)).AlignCenter()
            .Text(text).SemiBold().FontSize(PdfDesignSystem.TableSize).FontColor(PdfDesignSystem.Ink);

    private static void StageTextCell(
        IContainer container,
        string text,
        string background,
        bool semiBold = false)
    {
        var value = container.Element(cell => PdfDesignSystem.BodyCell(cell, background)).AlignRight();
        var descriptor = value.Text(text).FontSize(PdfDesignSystem.TableSize).FontColor(PdfDesignSystem.Ink);
        if (semiBold)
        {
            descriptor.SemiBold();
        }
    }

    private static void StageStatusCell(
        IContainer container,
        ShipmentSummaryPdfStage stage,
        string background)
        => container.Element(cell => PdfDesignSystem.BodyCell(cell, background)).AlignRight()
            .Text(stage.StatusText).SemiBold().FontSize(PdfDesignSystem.TableSize)
            .FontColor(ToneColor(stage.Tone));

    private static void StageMetricCell(
        IContainer container,
        ShipmentSummaryPdfMetric metric,
        string background,
        bool semiBold)
    {
        container.Element(cell => PdfDesignSystem.BodyCell(cell, background)).AlignRight().Column(column =>
        {
            column.Item().Text(metric.Label).FontSize(LabelSize).FontColor(PdfDesignSystem.Muted);
            column.Item().PaddingTop(1).Element(Ltr).AlignRight().Text(text =>
            {
                var value = text.Span(PdfDesignSystem.ToEnglishDigits(metric.Value))
                    .FontSize(ValueSize).FontColor(ToneColor(metric.Tone));
                if (semiBold)
                {
                    value.SemiBold();
                }
                if (!string.IsNullOrWhiteSpace(metric.Unit))
                {
                    text.Span(" " + metric.Unit).FontSize(LabelSize).FontColor(PdfDesignSystem.Muted);
                }
            });
        });
    }

    private string Label(string fa, string en) => isEnglish ? en : fa;

    private string FormatGregorianPrintDate(DateTime value)
        => (isEnglish ? "Print date: " : "تاریخ چاپ: ")
            + value.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture);

    private static string ToneColor(ShipmentSummaryPdfTone tone) => tone switch
    {
        ShipmentSummaryPdfTone.Positive => PdfDesignSystem.Positive,
        ShipmentSummaryPdfTone.Negative => PdfDesignSystem.Negative,
        ShipmentSummaryPdfTone.Warning => PdfDesignSystem.Muted,
        _ => PdfDesignSystem.Ink
    };
}
