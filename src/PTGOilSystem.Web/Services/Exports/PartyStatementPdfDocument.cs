using System.Globalization;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.CompanyFlow;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PTGOilSystem.Web.Services.Exports;

/// <summary>
/// صورت‌حساب رسمی طرف حساب (PDF). چیدمان، رنگ‌بندی، شبکه و ریتم سطرها همان
/// قرارداد Excel است (<see cref="ExcelDesignSystem"/>) تا PDF و XLSX یک سند
/// خوانده شوند: شبکهٔ نازک روی همهٔ خانه‌ها، سربرگ آبی روشن با متن سرمه‌ای،
/// و رنگ پس‌زمینهٔ هر ستون بر اساس معنای همان ستون (مقدار، مبلغ، رسید، بیلانس).
/// اعداد، جهت، ترتیب ستون‌ها و فرمول بیلانس دست‌نخورده‌اند.
/// </summary>
internal sealed class PartyStatementPdfDocument(
    PartyStatementResult statement,
    string webRootPath,
    PdfBrandHeader brandHeader,
    bool isEnglish = false,
    SupplierContractStatementViewModel? contractGrouping = null) : IDocument
{
    // عنوان‌های «رسید/برد/بیلانس» فقط از منبع مرکزی می‌آیند؛ اعداد، جهت، ترتیب ستون‌ها
    // و فرمول بیلانس با تغییر زبان دست‌نخورده می‌مانند.
    private string Flow(CompanyFlowTextKey key) => CompanyFlowText.Get(key, isEnglish);

    private const string Ink = PdfDesignSystem.Ink;
    private const string Muted = PdfDesignSystem.Muted;
    private const string Grid = PdfDesignSystem.SheetGrid;
    private const string HeaderInk = PdfDesignSystem.SheetHeaderInk;
    private const string HeaderFill = PdfDesignSystem.SheetHeaderFill;
    private const string LabelFill = PdfDesignSystem.SheetLabelFill;
    private const string TotalFill = PdfDesignSystem.SheetTotalFill;
    private const string PlainFill = PdfDesignSystem.SheetBodyFill;
    private const string QuantityFill = PdfDesignSystem.SheetQuantityFill;
    private const string AmountFill = PdfDesignSystem.SheetAmountFill;
    private const string PositiveFill = PdfDesignSystem.SheetPositiveFill;
    private const string BalanceFill = PdfDesignSystem.SheetBalanceFill;

    private const float BodySize = PdfDesignSystem.SheetBodySize;
    private const float HeaderSize = PdfDesignSystem.SheetHeaderSize;
    private const float CaptionSize = PdfDesignSystem.SheetCaptionSize;

    public DocumentMetadata GetMetadata() => new()
    {
        Title = statement.Policy.StatementTitleFa,
        Author = statement.CompanyInfo.Name,
        Subject = statement.DocumentInfo.StatementNumber
    };

    public DocumentSettings GetSettings() => DocumentSettings.Default;

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            // ستون‌های عملیاتی در عرض Letter عمودی جا نمی‌شوند؛ همان قاعدهٔ صفحهٔ وب.
            page.Size(statement.ColumnOptions.UseLandscape || contractGrouping is not null
                ? PageSizes.Letter.Landscape()
                : PageSizes.Letter);
            page.MarginHorizontal(PdfDesignSystem.HorizontalMargin);
            page.MarginTop(PdfDesignSystem.TopMargin);
            page.MarginBottom(PdfDesignSystem.BottomMargin);
            page.PageColor(Colors.White);
            page.DefaultTextStyle(style => PdfDesignSystem.SheetTextStyle(style, isEnglish));
            page.Header().Column(column =>
            {
                column.Item().ShowOnce().Element(ComposeBrandHeader);
                column.Item().SkipOnce().Element(ComposeCompactHeader);
            });
            page.Content().PaddingTop(10).ContentFromRightToLeft().Column(ComposeContent);
            page.Footer().Element(ComposeFooter);
        });
    }

    private void ComposeBrandHeader(IContainer container)
    {
        // نوارِ متریک‌ها اینجا خالی می‌ماند: خلاصهٔ مالی در بدنه و با همان شبکهٔ
        // Excel می‌آید تا سربرگ شلوغ نشود.
        PdfDesignSystem.ComposeReportHeader(
            container,
            isEnglish ? statement.Policy.StatementTitleEn : statement.Policy.StatementTitleFa,
            statement.DocumentInfo.GeneratedAtUtc,
            FormatPeriod(statement.DocumentInfo.PeriodFrom, statement.DocumentInfo.PeriodTo),
            metrics: [],
            isEnglish,
            brandHeader);
    }

    private void ComposeCompactHeader(IContainer container)
    {
        PdfDesignSystem.ComposeReportHeader(
            container,
            isEnglish ? statement.Policy.StatementTitleEn : statement.Policy.StatementTitleFa,
            statement.DocumentInfo.GeneratedAtUtc,
            filters: null,
            metrics: [],
            isEnglish: isEnglish,
            brand: brandHeader,
            compact: true);
    }

    private void ComposeContent(ColumnDescriptor column)
    {
        column.Spacing(9);
        column.Item().Element(ComposeSummaryBand);
        column.Item().Row(row =>
        {
            row.RelativeItem().Element(ComposeStatementInfo);
            row.ConstantItem(9);
            row.RelativeItem().Element(ComposePartyInfo);
        });
        // تب «قراردادها» جدول خلاصهٔ قراردادی دارد، بقیهٔ تب‌ها جدول گردش حساب. بقیهٔ سند
        // (سربرگ، اطلاعات طرف‌حساب، خلاصهٔ مالی، امضا، فوتر) در هر دو حالت یکی است.
        column.Item().Element(contractGrouping is null ? ComposeLedgerTable : ComposeContractTable);
        column.Item().ShowEntire().Element(ComposeClosingSection);
    }

    /* ------------------------------------------------------------------
       خلاصهٔ مالی: یک نوار دو سطری با همان شبکه و رنگ‌های Excel — سطر عنوان
       آبی روشن، سطر عدد با رنگ معنایی همان ستون.
       ------------------------------------------------------------------ */
    private sealed record SummaryMetric(string Label, string Value, string Fill, string? Detail = null);

    private void ComposeSummaryBand(IContainer container)
    {
        var metrics = BuildSummaryMetrics();
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                for (var index = 0; index < metrics.Count; index++)
                    columns.RelativeColumn();
            });

            foreach (var metric in metrics)
                HeaderCell(table.Cell(), metric.Label);

            foreach (var metric in metrics)
            {
                table.Cell().Element(cell => PdfDesignSystem.SheetCell(cell, metric.Fill)).Column(cellColumn =>
                {
                    cellColumn.Item().AlignCenter().ContentFromLeftToRight()
                        .Text(metric.Value).Bold().FontSize(BodySize).FontColor(Ink);
                    if (!string.IsNullOrWhiteSpace(metric.Detail))
                    {
                        cellColumn.Item().AlignCenter().Text(metric.Detail)
                            .FontSize(CaptionSize).FontColor(Muted);
                    }
                });
            }
        });
    }

    private IReadOnlyList<SummaryMetric> BuildSummaryMetrics()
    {
        var currency = statement.DocumentInfo.BaseCurrencyCode;
        return
        [
            new(Flow(CompanyFlowTextKey.OpeningBalance), FormatMoney(OpeningBalance()) + " " + currency, BalanceFill),
            new(Flow(CompanyFlowTextKey.TotalReceipt), FormatMoney(TotalReceipt()) + " " + currency, PositiveFill),
            new(Flow(CompanyFlowTextKey.TotalOutflow), FormatMoney(TotalOutflow()) + " " + currency, AmountFill),
            new(
                Flow(CompanyFlowTextKey.ClosingBalance),
                FormatMoney(ClosingBalanceAbsolute()) + " " + currency,
                BalanceFill,
                statement.Summary.ClosingBalanceMeaningFor(isEnglish))
        ];
    }

    /* ------------------------------------------------------------------
       اطلاعات طرف حساب و سند: دو بلوکِ برچسب/مقدار با همان شبکه.
       ------------------------------------------------------------------ */
    private void ComposePartyInfo(IContainer container)
    {
        ComposeInfoBlock(
            container,
            statement.Policy.PartyInformationTitleFa,
            [
                ("نام", statement.PartyInfo.Name, false),
                ("کد حساب", ValueOrDash(statement.PartyInfo.Code), true),
                ("تلفن", ValueOrDash(statement.PartyInfo.Phone), true),
                ("آدرس", ValueOrDash(statement.PartyInfo.Address), false)
            ]);
    }

    private void ComposeStatementInfo(IContainer container)
    {
        ComposeInfoBlock(
            container,
            "اطلاعات صورت حساب",
            [
                ("شماره", statement.DocumentInfo.StatementNumber, true),
                ("تاریخ", FormatDate(statement.DocumentInfo.StatementDate), true),
                ("دوره", FormatPeriod(statement.DocumentInfo.PeriodFrom, statement.DocumentInfo.PeriodTo), true),
                ("ارز", statement.DocumentInfo.BaseCurrencyCode, true)
            ]);
    }

    private static void ComposeInfoBlock(
        IContainer container,
        string title,
        IReadOnlyList<(string Label, string Value, bool Ltr)> lines)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(74);
                columns.RelativeColumn();
            });

            table.Cell().ColumnSpan(2)
                .Element(cell => PdfDesignSystem.HeaderCell(cell, HeaderFill, 5f))
                .Text(title).Bold().FontSize(HeaderSize).FontColor(HeaderInk);

            foreach (var (label, value, ltr) in lines)
            {
                table.Cell().Element(cell => PdfDesignSystem.SheetCell(cell, LabelFill))
                    .Text(label).SemiBold().FontSize(BodySize).FontColor(HeaderInk);

                // مقدارهای لاتین (کد، تاریخ، ارز) هم کنار برچسب خودشان می‌مانند؛
                // فقط ترتیب نویسه‌ها چپ‌به‌راست می‌شود، نه جای خانه.
                var target = table.Cell().Element(cell => PdfDesignSystem.SheetCell(cell, PlainFill));
                if (ltr)
                    target = target.AlignRight().ContentFromLeftToRight();
                target.Text(PdfDesignSystem.ToEnglishDigits(value)).FontSize(BodySize);
            }
        });
    }

    // ستون‌های عملیاتی همانی هستند که تب «بارگیری‌ها» در صفحه نشان می‌دهد. وقتی صفحه
    // آن‌ها را خواسته باشد، PDF هم باید همان محتوا را بدهد نه گردش حساب سادهٔ شش‌ستونی.
    private sealed record OperationalColumn(
        string Title,
        Func<PartyStatementRow, decimal?> Value,
        int Decimals,
        string Fill);

    private IReadOnlyList<OperationalColumn> BuildOperationalColumns()
    {
        var options = statement.ColumnOptions;
        var columns = new List<OperationalColumn>();
        if (options.ShowQuantity)
            columns.Add(new OperationalColumn("M-Tone", row => row.Quantity, 3, QuantityFill));
        if (options.ShowPlatts)
            columns.Add(new OperationalColumn("Platts", row => row.PlattsPrice, 2, AmountFill));
        if (options.ShowPremiumOrDiscount)
            columns.Add(new OperationalColumn("Premium / Discount", row => row.PremiumOrDiscount, 2, AmountFill));
        if (options.ShowUnitPrice)
            columns.Add(new OperationalColumn("نرخ واحد", row => row.UnitPrice, 2, AmountFill));
        return columns;
    }

    private void ComposeLedgerTable(IContainer container)
    {
        var operational = BuildOperationalColumns();
        var columnCount = (uint)(6 + operational.Count);
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(60);
                columns.ConstantColumn(66);
                columns.RelativeColumn(2.2f);
                for (var i = 0; i < operational.Count; i++)
                    columns.ConstantColumn(60);
                columns.ConstantColumn(74);
                columns.ConstantColumn(74);
                columns.ConstantColumn(76);
            });
            table.Header(header =>
            {
                HeaderCell(header.Cell(), "تاریخ");
                HeaderCell(header.Cell(), "مرجع");
                HeaderCell(header.Cell(), "شرح");
                foreach (var column in operational)
                    HeaderCell(header.Cell(), column.Title);
                HeaderCell(header.Cell(), Flow(CompanyFlowTextKey.Receipt));
                HeaderCell(header.Cell(), Flow(CompanyFlowTextKey.Outflow));
                HeaderCell(header.Cell(), Flow(CompanyFlowTextKey.Balance));
            });
            if (statement.Rows.Count == 0)
            {
                table.Cell().ColumnSpan(columnCount)
                    .Element(cell => PdfDesignSystem.SheetCell(cell, PlainFill))
                    .PaddingVertical(10).AlignCenter()
                    .Text("در این دوره تراکنشی ثبت نشده است.")
                    .FontSize(BodySize).FontColor(Muted);
            }
            else
            {
                foreach (var row in statement.Rows)
                {
                    TextCell(table.Cell(), FormatDate(row.Date), PlainFill, center: true, ltr: true);
                    TextCell(table.Cell(), ValueOrDash(row.Reference), PlainFill, center: true, ltr: true);
                    TextCell(table.Cell(), row.DescriptionFor(isEnglish), PlainFill);
                    foreach (var column in operational)
                        NumberCell(table.Cell(), column.Value(row), column.Fill, column.Decimals);
                    NumberCell(table.Cell(), RowDebit(row), PositiveFill);
                    NumberCell(table.Cell(), RowCredit(row), AmountFill);
                    NumberCell(table.Cell(), RowBalance(row), BalanceFill);
                }
            }
            table.Cell().ColumnSpan((uint)(3 + operational.Count)).Element(TotalCellStyle)
                .AlignCenter().Text(Flow(CompanyFlowTextKey.PeriodTotal))
                .Bold().FontSize(BodySize).FontColor(HeaderInk);
            TotalMoneyCell(table.Cell(), TotalReceipt());
            TotalMoneyCell(table.Cell(), TotalOutflow());
            TotalMoneyCell(table.Cell(), ClosingBalance());
        });
    }

    // جدول تب «قراردادها»: هر سطر یک قرارداد، دقیقاً همان ستون‌هایی که خود تب نشان می‌دهد.
    private void ComposeContractTable(IContainer container)
    {
        var grouping = contractGrouping!;
        decimal? Money(decimal usd, decimal? rub) => grouping.IsRub ? rub : usd;

        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(36);
                columns.RelativeColumn(2.4f);
                columns.ConstantColumn(78);
                columns.ConstantColumn(78);
                columns.ConstantColumn(78);
                columns.ConstantColumn(52);
                // جا برای عنوانِ معنای مانده («قابل پرداخت به تأمین‌کننده») در یک خط.
                columns.ConstantColumn(102);
            });
            table.Header(header =>
            {
                HeaderCell(header.Cell(), "شماره");
                HeaderCell(header.Cell(), "قرارداد");
                HeaderCell(header.Cell(), "مبلغ کل قرارداد (USD)");
                HeaderCell(header.Cell(), "ارزش قطعی");
                HeaderCell(header.Cell(), "پرداخت / دریافت");
                HeaderCell(header.Cell(), "تعداد");
                HeaderCell(header.Cell(), "مانده قرارداد");
            });
            if (grouping.Rows.Count == 0)
            {
                table.Cell().ColumnSpan(7)
                    .Element(cell => PdfDesignSystem.SheetCell(cell, PlainFill))
                    .PaddingVertical(10).AlignCenter()
                    .Text("در این دوره قراردادی با گردش مالی ثبت نشده است.")
                    .FontSize(BodySize).FontColor(Muted);
            }
            else
            {
                foreach (var row in grouping.Rows)
                {
                    TextCell(
                        table.Cell(),
                        row.Sequence.ToString(CultureInfo.InvariantCulture),
                        PlainFill,
                        center: true,
                        ltr: true);
                    ContractTitleCell(table.Cell(), row);
                    NumberCell(table.Cell(), row.ContractValueUsd, AmountFill);
                    NumberCell(table.Cell(), Money(row.ConfirmedValue, row.ConfirmedValueRub), AmountFill);
                    NumberCell(table.Cell(), Money(row.SettlementTotal, row.SettlementTotalRub), PositiveFill);
                    NumberCell(table.Cell(), row.LoadingCount, PlainFill, 0);
                    ContractBalanceCell(
                        table.Cell(),
                        row.BalanceAbsoluteFor(grouping.IsRub),
                        row.BalanceTitleFor(grouping.IsRub, isEnglish));
                }
            }
            table.Cell().ColumnSpan(2).Element(TotalCellStyle)
                .AlignCenter().Text(Flow(CompanyFlowTextKey.PeriodTotal))
                .Bold().FontSize(BodySize).FontColor(HeaderInk);
            TotalMoneyCell(table.Cell(), null);
            TotalMoneyCell(table.Cell(), Money(grouping.TotalConfirmedValue, grouping.TotalConfirmedValueRub));
            TotalMoneyCell(table.Cell(), Money(grouping.TotalSettlement, grouping.TotalSettlementRub));
            TotalMoneyCell(table.Cell(), grouping.TotalLoadingCount, 0);
            // جمع دوره ماندهٔ حسابِ طرف است؛ بدون علامت، با معنای مرکزیِ خودش.
            TotalCellStyle(table.Cell()).Column(column =>
            {
                column.Item().AlignCenter().ContentFromLeftToRight()
                    .Text(FormatMoney(grouping.IsRub
                        ? statement.Summary.ClosingBalanceRubAbsolute
                        : statement.Summary.ClosingBalanceAbsolute))
                    .Bold().FontSize(BodySize).FontColor(HeaderInk);
                column.Item().AlignCenter()
                    .Text(statement.Summary.ClosingBalanceMeaningFor(isEnglish))
                    .FontSize(CaptionSize).FontColor(Muted);
            });
        });
    }

    private void ContractTitleCell(IContainer container, SupplierContractStatementRow row)
    {
        container.ShowEntire().Element(cell => PdfDesignSystem.SheetCell(cell, PlainFill)).Column(column =>
            {
                column.Item().Text(PdfDesignSystem.ToEnglishDigits(row.Title))
                    .SemiBold().FontSize(BodySize);
                if (row.ContractQuantityMt.HasValue || row.LoadedQuantityMt.HasValue)
                {
                    column.Item().Text(
                            $"قرارداد {FormatNumber(row.ContractQuantityMt, 3)} MT / بارگیری {FormatNumber(row.LoadedQuantityMt, 3)} MT")
                        .FontSize(CaptionSize).FontColor(Muted);
                }
            });
    }

    private static void HeaderCell(IContainer container, string text)
    {
        PdfDesignSystem.HeaderCell(container, HeaderFill, 6f)
            .AlignCenter()
            .Text(PdfDesignSystem.ToEnglishDigits(text))
            .Bold().FontSize(HeaderSize).FontColor(HeaderInk);
    }

    private static void TextCell(
        IContainer container,
        string text,
        string background,
        bool center = false,
        bool ltr = false)
    {
        var cell = container.ShowEntire().Element(target => PdfDesignSystem.SheetCell(target, background));
        if (center)
            cell = cell.AlignCenter();
        if (ltr)
            cell = cell.ContentFromLeftToRight();
        cell.Text(PdfDesignSystem.ToEnglishDigits(text)).FontSize(BodySize);
    }

    private void NumberCell(IContainer container, decimal? value, string background, int? decimals = null)
    {
        container.ShowEntire().Element(target => PdfDesignSystem.SheetCell(target, background))
            .AlignCenter().ContentFromLeftToRight()
            .Text(decimals.HasValue ? FormatNumber(value, decimals.Value) : FormatMoney(value))
            .FontSize(BodySize);
    }

    /// <summary>
    /// مانده قرارداد: عدد بدون علامت + عنوانِ معنا — همان چیزی که خلاصهٔ قراردادها،
    /// صورت‌حساب و Excel نشان می‌دهند.
    /// </summary>
    private void ContractBalanceCell(IContainer container, decimal? absoluteValue, string? title)
    {
        container.ShowEntire().Element(target => PdfDesignSystem.SheetCell(target, BalanceFill)).Column(column =>
        {
            column.Item().AlignCenter().ContentFromLeftToRight().Text(FormatMoney(absoluteValue))
                .FontSize(BodySize);
            if (!string.IsNullOrWhiteSpace(title))
            {
                column.Item().AlignCenter().Text(title).FontSize(CaptionSize).FontColor(Muted);
            }
        });
    }

    private static IContainer TotalCellStyle(IContainer container)
        => container.ShowEntire().Element(target => PdfDesignSystem.SheetCell(target, TotalFill));

    private void TotalMoneyCell(IContainer container, decimal? value, int? decimals = null)
    {
        TotalCellStyle(container).AlignCenter().ContentFromLeftToRight()
            .Text(decimals.HasValue ? FormatNumber(value, decimals.Value) : FormatMoney(value))
            .Bold().FontSize(BodySize).FontColor(HeaderInk);
    }

    private void ComposeClosingSection(IContainer container)
    {
        container.PaddingTop(2).Row(row =>
        {
            row.RelativeItem(1.55f).Border(PdfDesignSystem.SheetGridThickness).BorderColor(Grid).Padding(9)
                .ContentFromRightToLeft().Column(note =>
                {
                    note.Item().Text("یادداشت")
                        .Bold().FontSize(HeaderSize).FontColor(HeaderInk);
                    note.Item().PaddingTop(5).Text(ValueOrDash(statement.Note))
                        .FontSize(BodySize).FontColor(Muted);
                });
            row.ConstantItem(9);
            row.RelativeItem().Border(PdfDesignSystem.SheetGridThickness).BorderColor(Grid).Padding(9)
                .ContentFromRightToLeft().Column(signature =>
                {
                    signature.Item().Text("تأیید بخش مالی")
                        .Bold().FontSize(HeaderSize).FontColor(HeaderInk);
                    var signaturePath = ResolveWebAsset(statement.Authorization.SignatureImagePath);
                    if (signaturePath is not null)
                        signature.Item().PaddingTop(3).Height(28).AlignRight().Image(signaturePath).FitArea();
                    else
                        signature.Item().PaddingTop(20);
                    signature.Item().PaddingTop(2).LineHorizontal(0.7f).LineColor("#B7BEC8");
                    signature.Item().PaddingTop(3).Text(ValueOrDash(statement.Authorization.AuthorizedByName))
                        .SemiBold().FontSize(BodySize);
                    signature.Item().Text(ValueOrDash(statement.Authorization.AuthorizedByTitle))
                        .FontSize(CaptionSize).FontColor(Muted);
                });
        });
    }

    private void ComposeFooter(IContainer container)
    {
        PdfDesignSystem.ComposeFooter(container.PaddingTop(8), statement.CompanyInfo.Name, isEnglish);
    }

    private decimal? OpeningBalance() => statement.Summary.IsRubPresentation
        ? statement.Summary.OpeningBalanceRub
        : statement.Summary.OpeningBalance;

    private decimal? TotalReceipt() => statement.Summary.IsRubPresentation
        ? statement.Summary.TotalReceiptRub
        : statement.Summary.TotalReceipt;

    private decimal? TotalOutflow() => statement.Summary.IsRubPresentation
        ? statement.Summary.TotalOutflowRub
        : statement.Summary.TotalOutflow;

    private decimal? ClosingBalance() => statement.Summary.IsRubPresentation
        ? statement.Summary.ClosingBalanceRub
        : statement.Summary.ClosingBalance;

    private decimal? ClosingBalanceAbsolute() => statement.Summary.IsRubPresentation
        ? statement.Summary.ClosingBalanceRubAbsolute
        : statement.Summary.ClosingBalanceAbsolute;

    private decimal? RowDebit(PartyStatementRow row)
        => statement.Summary.IsRubPresentation ? row.ReceiptRub : row.ReceiptBase;

    private decimal? RowCredit(PartyStatementRow row)
        => statement.Summary.IsRubPresentation ? row.OutflowRub : row.OutflowBase;

    private decimal? RowBalance(PartyStatementRow row)
        => statement.Summary.IsRubPresentation ? row.RunningBalanceRub : row.RunningBalance;

    private string? ResolveWebAsset(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath) || string.IsNullOrWhiteSpace(webRootPath))
            return null;

        return PdfDesignSystem.ResolveWebAsset(webRootPath, configuredPath);
    }

    private static string ValueOrDash(string? value)
        => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private string FormatDate(DateTime value)
        => PdfDesignSystem.FormatPdfDate(value, isEnglish);

    private string FormatPeriod(DateTime? from, DateTime? to)
        => $"{(from.HasValue ? FormatDate(from.Value) : "ابتدای حساب")} - {(to.HasValue ? FormatDate(to.Value) : "امروز")}";

    private string FormatMoney(decimal? value)
        => value.HasValue
            ? PdfDesignSystem.FormatPdfNumber(value.Value, isEnglish)
            : "—";

    private string FormatNumber(decimal? value, int decimals)
        => value.HasValue
            ? PdfDesignSystem.FormatPdfNumber(value.Value, isEnglish, decimals)
            : "—";
}
