using System.Globalization;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.CompanyFlow;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PTGOilSystem.Web.Services.Exports;

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

    private enum StatementIcon
    {
        Party,
        Document
    }

    private const string Ink = PdfDesignSystem.Ink;
    private const string Muted = PdfDesignSystem.Muted;
    private const string Border = PdfDesignSystem.Border;
    private const string Green = PdfDesignSystem.Positive;
    private const string Red = PdfDesignSystem.Negative;
    private const string Purple = PdfDesignSystem.Balance;

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
            page.DefaultTextStyle(style => PdfDesignSystem.DefaultTextStyle(style, isEnglish));
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
        PdfDesignSystem.ComposeReportHeader(
            container,
            isEnglish ? statement.Policy.StatementTitleEn : statement.Policy.StatementTitleFa,
            statement.DocumentInfo.GeneratedAtUtc,
            FormatPeriod(statement.DocumentInfo.PeriodFrom, statement.DocumentInfo.PeriodTo),
            BuildSummaryMetrics(),
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
        column.Spacing(10);
        column.Item().Row(row =>
        {
            row.RelativeItem().AlignRight().ContentFromLeftToRight()
                .Text(statement.DocumentInfo.StatementNumber).FontSize(7).FontColor(Muted);
            row.RelativeItem().AlignLeft().AlignMiddle().Text(statement.CourtesyText).FontSize(8).FontColor(Muted);
        });
        column.Item().Row(row =>
        {
            row.RelativeItem().Element(ComposeStatementInfo);
            row.ConstantItem(10);
            row.RelativeItem().Element(ComposePartyInfo);
        });
        // تب «قراردادها» جدول خلاصهٔ قراردادی دارد، بقیهٔ تب‌ها جدول گردش حساب. بقیهٔ سند
        // (سربرگ، اطلاعات طرف‌حساب، خلاصهٔ مالی، امضا، فوتر) در هر دو حالت یکی است.
        column.Item().Element(contractGrouping is null ? ComposeLedgerTable : ComposeContractTable);
        column.Item().ShowEntire().Element(ComposeClosingSection);
    }

    private void ComposePartyInfo(IContainer container)
    {
        container.Border(0.7f).BorderColor(Border).CornerRadius(4).Padding(10)
            .ContentFromRightToLeft().Column(ComposePartyInfoBody);
    }

    private void ComposePartyInfoBody(ColumnDescriptor column)
    {
        SectionTitle(column, statement.Policy.PartyInformationTitleFa, Green, StatementIcon.Party);
        InfoLine(column, "\u0646\u0627\u0645", statement.PartyInfo.Name);
        InfoLine(column, "\u06A9\u062F \u062D\u0633\u0627\u0628", ValueOrDash(statement.PartyInfo.Code), true);
        InfoLine(column, "\u062A\u0644\u0641\u0646", ValueOrDash(statement.PartyInfo.Phone), true);
        InfoLine(column, "\u0622\u062F\u0631\u0633", ValueOrDash(statement.PartyInfo.Address));
    }

    private void ComposeStatementInfo(IContainer container)
    {
        container.Border(0.7f).BorderColor(Border).CornerRadius(4).Padding(10)
            .ContentFromRightToLeft().Column(column =>
            {
                SectionTitle(column, "\u0627\u0637\u0644\u0627\u0639\u0627\u062A \u0635\u0648\u0631\u062A \u062D\u0633\u0627\u0628", Red, StatementIcon.Document);
                InfoLine(column, "\u0634\u0645\u0627\u0631\u0647", statement.DocumentInfo.StatementNumber, true);
                InfoLine(column, "\u062A\u0627\u0631\u06CC\u062E", FormatDate(statement.DocumentInfo.StatementDate), true);
                InfoLine(column, "\u062F\u0648\u0631\u0647", FormatPeriod(statement.DocumentInfo.PeriodFrom, statement.DocumentInfo.PeriodTo), true);
                InfoLine(column, "\u0627\u0631\u0632", statement.DocumentInfo.BaseCurrencyCode, true);
            });
    }

    private static void SectionTitle(ColumnDescriptor column, string text, string color, StatementIcon icon)
    {
        column.Item().PaddingBottom(6).Row(row =>
        {
            row.ConstantItem(17).Height(17).Element(container => ComposeIcon(container, icon, color));
            row.ConstantItem(7);
            row.RelativeItem().Text(text).SemiBold().FontSize(8.5f).FontColor(color);
        });
    }

    private static void InfoLine(ColumnDescriptor column, string label, string value, bool ltr = false)
    {
        column.Item().PaddingBottom(3).Row(row =>
        {
            row.ConstantItem(55).Text(label).SemiBold().FontSize(7);
            var target = row.RelativeItem().AlignRight();
            if (ltr)
                target = target.ContentFromLeftToRight();
            target.Text(value).FontSize(7).FontColor("#3D4655");
        });
    }

    private IReadOnlyList<PdfSummaryMetric> BuildSummaryMetrics()
        =>
        [
            new(
                Flow(CompanyFlowTextKey.OpeningBalance),
                FormatMoney(OpeningBalance()) + " " + statement.DocumentInfo.BaseCurrencyCode,
                BalanceColor(OpeningBalance())),
            new(
                Flow(CompanyFlowTextKey.TotalReceipt),
                FormatMoney(TotalReceipt()) + " " + statement.DocumentInfo.BaseCurrencyCode,
                Red),
            new(
                Flow(CompanyFlowTextKey.TotalOutflow),
                FormatMoney(TotalOutflow()) + " " + statement.DocumentInfo.BaseCurrencyCode,
                Green),
            new(
                Flow(CompanyFlowTextKey.ClosingBalance),
                FormatMoney(ClosingBalanceAbsolute()) + " " + statement.DocumentInfo.BaseCurrencyCode,
                BalanceColor(ClosingBalance()),
                statement.Summary.ClosingBalanceMeaningFor(isEnglish))
        ];

    private static string BalanceColor(decimal? value)
        => value < 0 ? Red : value > 0 ? Green : Ink;

    private static void ComposeIcon(IContainer container, StatementIcon icon, string color)
        => container.Svg(IconSvg(icon, color));

    private static string IconSvg(StatementIcon icon, string color)
    {
        var body = icon switch
        {
            StatementIcon.Party =>
                """
                <circle cx="12" cy="7" r="4"/>
                <path d="M4 22v-2c0-4 3.6-7 8-7s8 3 8 7v2"/>
                """,
            StatementIcon.Document =>
                """
                <path d="M6 2h8l4 4v16H6z"/>
                <path d="M14 2v5h5M9 12h6M9 16h6"/>
                """,
            _ => string.Empty
        };

        return $"""
            <svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg"
                 fill="none" stroke="{color}" stroke-width="1.8"
                 stroke-linecap="round" stroke-linejoin="round">
                {body.Replace("CURRENT_COLOR", color, StringComparison.Ordinal)}
            </svg>
            """;
    }

    // \u0633\u062A\u0648\u0646\u200C\u0647\u0627\u06CC \u0639\u0645\u0644\u06CC\u0627\u062A\u06CC \u0647\u0645\u0627\u0646\u06CC \u0647\u0633\u062A\u0646\u062F \u06A9\u0647 \u062A\u0628 \u00AB\u0628\u0627\u0631\u06AF\u06CC\u0631\u06CC\u200C\u0647\u0627\u00BB \u062F\u0631 \u0635\u0641\u062D\u0647 \u0646\u0634\u0627\u0646 \u0645\u06CC\u200C\u062F\u0647\u062F. \u0648\u0642\u062A\u06CC \u0635\u0641\u062D\u0647
    // \u0622\u0646\u200C\u0647\u0627 \u0631\u0627 \u062E\u0648\u0627\u0633\u062A\u0647 \u0628\u0627\u0634\u062F\u060C PDF \u0647\u0645 \u0628\u0627\u06CC\u062F \u0647\u0645\u0627\u0646 \u0645\u062D\u062A\u0648\u0627 \u0631\u0627 \u0628\u062F\u0647\u062F \u0646\u0647 \u06AF\u0631\u062F\u0634 \u062D\u0633\u0627\u0628 \u0633\u0627\u062F\u0647\u0654 \u0634\u0634\u200C\u0633\u062A\u0648\u0646\u06CC.
    private sealed record OperationalColumn(
        string Title,
        Func<PartyStatementRow, decimal?> Value,
        int Decimals,
        bool Ltr);

    private IReadOnlyList<OperationalColumn> BuildOperationalColumns()
    {
        var options = statement.ColumnOptions;
        var columns = new List<OperationalColumn>();
        if (options.ShowQuantity)
            columns.Add(new OperationalColumn("M-Tone", row => row.Quantity, 3, true));
        if (options.ShowPlatts)
            columns.Add(new OperationalColumn("Platts", row => row.PlattsPrice, 2, true));
        if (options.ShowPremiumOrDiscount)
            columns.Add(new OperationalColumn("Premium / Discount", row => row.PremiumOrDiscount, 2, true));
        if (options.ShowUnitPrice)
            columns.Add(new OperationalColumn("\u0646\u0631\u062E \u0648\u0627\u062D\u062F", row => row.UnitPrice, 2, false));
        return columns;
    }

    private void ComposeLedgerTable(IContainer container)
    {
        var operational = BuildOperationalColumns();
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(58);
                columns.ConstantColumn(66);
                columns.RelativeColumn(2.2f);
                for (var i = 0; i < operational.Count; i++)
                    columns.ConstantColumn(62);
                columns.ConstantColumn(70);
                columns.ConstantColumn(70);
                columns.ConstantColumn(72);
            });
            table.Header(header =>
            {
                HeaderCell(header.Cell(), "\u062A\u0627\u0631\u06CC\u062E");
                HeaderCell(header.Cell(), "\u0645\u0631\u062C\u0639");
                HeaderCell(header.Cell(), "\u0634\u0631\u062D");
                foreach (var column in operational)
                    HeaderCell(header.Cell(), column.Title, Ink, column.Ltr);
                HeaderCell(header.Cell(), Flow(CompanyFlowTextKey.Receipt), Red, true);
                HeaderCell(header.Cell(), Flow(CompanyFlowTextKey.Outflow), Green, true);
                HeaderCell(header.Cell(), Flow(CompanyFlowTextKey.Balance), Purple, true);
                header.Cell().ColumnSpan((uint)(6 + operational.Count))
                    .Element(container => PdfDesignSystem.TableSeparator(container, 1.5f));
            });
            if (statement.Rows.Count == 0)
            {
                table.Cell().ColumnSpan((uint)(6 + operational.Count)).Element(EmptyCellStyle)
                    .Text("\u062F\u0631 \u0627\u06CC\u0646 \u062F\u0648\u0631\u0647 \u062A\u0631\u0627\u06A9\u0646\u0634\u06CC \u062B\u0628\u062A \u0646\u0634\u062F\u0647 \u0627\u0633\u062A.").FontColor(Muted);
                table.Cell().ColumnSpan((uint)(6 + operational.Count))
                    .Element(container => PdfDesignSystem.TableSeparator(container));
            }
            else
            {
                foreach (var row in statement.Rows)
                {
                    var shade = Colors.White;
                    BodyCell(table.Cell(), FormatDate(row.Date), shade, true);
                    BodyCell(table.Cell(), ValueOrDash(row.Reference), shade, true);
                    BodyCell(table.Cell(), row.DescriptionFor(isEnglish), shade);
                    foreach (var column in operational)
                        NumberCell(table.Cell(), column.Value(row), shade, column.Decimals);
                    MoneyCell(table.Cell(), RowDebit(row), shade, Red);
                    MoneyCell(table.Cell(), RowCredit(row), shade, Green);
                    MoneyCell(table.Cell(), RowBalance(row), shade, Purple);
                    table.Cell().ColumnSpan((uint)(6 + operational.Count))
                        .Element(container => PdfDesignSystem.TableSeparator(container));
                }
            }
            table.Cell().ColumnSpan((uint)(3 + operational.Count)).Element(TotalCellStyle)
                .AlignRight().Text("\u0645\u062C\u0645\u0648\u0639 \u062F\u0648\u0631\u0647").Bold().FontSize(7.5f);
            TotalMoneyCell(table.Cell(), TotalReceipt(), Red);
            TotalMoneyCell(table.Cell(), TotalOutflow(), Green);
            TotalMoneyCell(table.Cell(), ClosingBalance(), Purple);
            table.Cell().ColumnSpan((uint)(6 + operational.Count))
                .Element(container => PdfDesignSystem.TableSeparator(container));
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
                columns.ConstantColumn(34);
                columns.RelativeColumn(2.4f);
                columns.ConstantColumn(76);
                columns.ConstantColumn(76);
                columns.ConstantColumn(76);
                columns.ConstantColumn(54);
                // جا برای عنوانِ معنای مانده («قابل پرداخت به تأمین‌کننده») در یک خط.
                columns.ConstantColumn(100);
            });
            table.Header(header =>
            {
                HeaderCell(header.Cell(), "شماره");
                HeaderCell(header.Cell(), "قرارداد");
                HeaderCell(header.Cell(), "مبلغ کل قرارداد (USD)", Ink, true);
                HeaderCell(header.Cell(), "ارزش قطعی", Red, true);
                HeaderCell(header.Cell(), "پرداخت / دریافت", Green, true);
                HeaderCell(header.Cell(), "تعداد", Ink, true);
                HeaderCell(header.Cell(), "مانده قرارداد", Purple, true);
                header.Cell().ColumnSpan(7)
                    .Element(container => PdfDesignSystem.TableSeparator(container, 1.5f));
            });
            if (grouping.Rows.Count == 0)
            {
                table.Cell().ColumnSpan(7).Element(EmptyCellStyle)
                    .Text("در این دوره قراردادی با گردش مالی ثبت نشده است.").FontColor(Muted);
                table.Cell().ColumnSpan(7)
                    .Element(container => PdfDesignSystem.TableSeparator(container));
            }
            else
            {
                foreach (var row in grouping.Rows)
                {
                    var shade = Colors.White;
                    BodyCell(table.Cell(), row.Sequence.ToString(CultureInfo.InvariantCulture), shade, true);
                    ContractTitleCell(table.Cell(), row, shade);
                    MoneyCell(table.Cell(), row.ContractValueUsd, shade, Ink);
                    MoneyCell(table.Cell(), Money(row.ConfirmedValue, row.ConfirmedValueRub), shade, Red);
                    MoneyCell(table.Cell(), Money(row.SettlementTotal, row.SettlementTotalRub), shade, Green);
                    NumberCell(table.Cell(), row.LoadingCount, shade, 0);
                    ContractBalanceCell(
                        table.Cell(),
                        row.BalanceAbsoluteFor(grouping.IsRub),
                        row.BalanceTitleFor(grouping.IsRub, isEnglish),
                        shade,
                        Purple);
                    table.Cell().ColumnSpan(7)
                        .Element(container => PdfDesignSystem.TableSeparator(container));
                }
            }
            table.Cell().ColumnSpan(2).Element(TotalCellStyle)
                .AlignRight().Text(Flow(CompanyFlowTextKey.PeriodTotal)).Bold().FontSize(7.5f);
            TotalMoneyCell(table.Cell(), null, Ink);
            TotalMoneyCell(table.Cell(), Money(grouping.TotalConfirmedValue, grouping.TotalConfirmedValueRub), Red);
            TotalMoneyCell(table.Cell(), Money(grouping.TotalSettlement, grouping.TotalSettlementRub), Green);
            TotalMoneyCell(table.Cell(), grouping.TotalLoadingCount, Ink);
            // جمع دوره ماندهٔ حسابِ طرف است؛ بدون علامت، با معنای مرکزیِ خودش.
            TotalCellStyle(table.Cell()).Column(column =>
            {
                column.Item().AlignRight().ContentFromLeftToRight()
                    .Text(FormatMoney(grouping.IsRub
                        ? statement.Summary.ClosingBalanceRubAbsolute
                        : statement.Summary.ClosingBalanceAbsolute))
                    .Bold().FontSize(PdfDesignSystem.NumericTableSize).FontColor(Purple);
                column.Item().AlignRight()
                    .Text(statement.Summary.ClosingBalanceMeaningFor(isEnglish))
                    .FontSize(6.5f).FontColor(Muted);
            });
            table.Cell().ColumnSpan(7)
                .Element(container => PdfDesignSystem.TableSeparator(container));
        });
    }

    private void ContractTitleCell(IContainer container, SupplierContractStatementRow row, string background)
    {
        container.ShowEntire().Element(cell => PdfDesignSystem.BodyCell(cell, background)).Column(column =>
            {
                column.Item().Text(PdfDesignSystem.ToEnglishDigits(row.Title))
                    .Bold().FontSize(PdfDesignSystem.TableSize);
                if (row.ContractQuantityMt.HasValue || row.LoadedQuantityMt.HasValue)
                {
                    column.Item().Text(
                            $"قرارداد {FormatNumber(row.ContractQuantityMt, 3)} MT / بارگیری {FormatNumber(row.LoadedQuantityMt, 3)} MT")
                        .FontSize(6.5f).FontColor(Muted);
                }
            });
    }

    private static void HeaderCell(IContainer container, string text, string color = Ink, bool ltr = false)
    {
        var cell = container.Element(target => PdfDesignSystem.HeaderCell(target)).AlignCenter();
        if (ltr)
            cell = cell.ContentFromLeftToRight();
        cell.Text(text).Bold().FontSize(PdfDesignSystem.TableSize).FontColor(color);
    }

    private static void BodyCell(IContainer container, string text, string background, bool ltr = false)
    {
        var cell = container.ShowEntire().Element(target => PdfDesignSystem.BodyCell(target, background));
        if (ltr)
            cell = cell.ContentFromLeftToRight();
        cell.Text(PdfDesignSystem.ToEnglishDigits(text))
            .FontSize(ltr ? PdfDesignSystem.NumericTableSize : PdfDesignSystem.TableSize);
    }

    private void MoneyCell(IContainer container, decimal? value, string background, string color)
    {
        container.ShowEntire().Element(target => PdfDesignSystem.BodyCell(target, background))
            .AlignRight().ContentFromLeftToRight().Text(FormatMoney(value))
            .FontSize(PdfDesignSystem.NumericTableSize).FontColor(color);
    }

    /// <summary>
    /// مانده قرارداد: عدد بدون علامت + عنوانِ معنا — همان چیزی که خلاصهٔ قراردادها،
    /// صورت‌حساب و Excel نشان می‌دهند.
    /// </summary>
    private void ContractBalanceCell(
        IContainer container,
        decimal? absoluteValue,
        string? title,
        string background,
        string color)
    {
        container.ShowEntire().Element(target => PdfDesignSystem.BodyCell(target, background)).Column(column =>
        {
            column.Item().AlignRight().ContentFromLeftToRight().Text(FormatMoney(absoluteValue))
                .FontSize(PdfDesignSystem.NumericTableSize).FontColor(color);
            if (!string.IsNullOrWhiteSpace(title))
            {
                column.Item().AlignRight().Text(title).FontSize(6.5f).FontColor(Muted);
            }
        });
    }

    private void NumberCell(IContainer container, decimal? value, string background, int decimals)
    {
        container.ShowEntire().Element(target => PdfDesignSystem.BodyCell(target, background))
            .AlignRight().ContentFromLeftToRight().Text(FormatNumber(value, decimals))
            .FontSize(PdfDesignSystem.NumericTableSize);
    }

    private static IContainer EmptyCellStyle(IContainer container)
        => PdfDesignSystem.BodyCell(container).PaddingVertical(12).AlignCenter();

    private static IContainer TotalCellStyle(IContainer container)
        => container.ShowEntire().Element(target => PdfDesignSystem.TotalCell(target));

    private void TotalMoneyCell(IContainer container, decimal? value, string color)
    {
        TotalCellStyle(container).AlignRight().ContentFromLeftToRight()
            .Text(FormatMoney(value)).Bold().FontSize(PdfDesignSystem.NumericTableSize).FontColor(color);
    }

    private void ComposeClosingSection(IContainer container)
    {
        container.PaddingTop(2).Row(row =>
        {
            row.RelativeItem(1.55f).Border(0.7f).BorderColor(Border).CornerRadius(4).Padding(9)
                .ContentFromRightToLeft().Column(note =>
                {
                    note.Item().Text("\u06CC\u0627\u062F\u062F\u0627\u0634\u062A").SemiBold().FontColor(Green);
                    note.Item().PaddingTop(5).Text(ValueOrDash(statement.Note)).FontSize(7).FontColor(Muted);
                });
            row.ConstantItem(12);
            row.RelativeItem().BorderLeft(0.7f).BorderColor(Border).PaddingLeft(12)
                .ContentFromRightToLeft().Column(signature =>
                {
                    signature.Item().Text("\u062A\u0623\u06CC\u06CC\u062F \u0628\u062E\u0634 \u0645\u0627\u0644\u06CC").SemiBold().FontSize(7.5f).FontColor(Red);
                    var signaturePath = ResolveWebAsset(statement.Authorization.SignatureImagePath);
                    if (signaturePath is not null)
                        signature.Item().PaddingTop(3).Height(28).AlignRight().Image(signaturePath).FitArea();
                    else
                        signature.Item().PaddingTop(20);
                    signature.Item().PaddingTop(2).LineHorizontal(0.7f).LineColor("#B7BEC8");
                    signature.Item().PaddingTop(3).Text(ValueOrDash(statement.Authorization.AuthorizedByName)).SemiBold().FontSize(7);
                    signature.Item().Text(ValueOrDash(statement.Authorization.AuthorizedByTitle)).FontSize(6).FontColor(Muted);
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
        => string.IsNullOrWhiteSpace(value) ? "\u2014" : value.Trim();

    private string FormatDate(DateTime value)
        => PdfDesignSystem.FormatPdfDate(value, isEnglish);

    private string FormatPeriod(DateTime? from, DateTime? to)
        => $"{(from.HasValue ? FormatDate(from.Value) : "\u0627\u0628\u062A\u062F\u0627\u06CC \u062D\u0633\u0627\u0628")} - {(to.HasValue ? FormatDate(to.Value) : "\u0627\u0645\u0631\u0648\u0632")}";

    private string FormatMoney(decimal? value)
        => value.HasValue
            ? PdfDesignSystem.FormatPdfNumber(value.Value, isEnglish)
            : "\u2014";

    private string FormatNumber(decimal? value, int decimals)
        => value.HasValue
            ? PdfDesignSystem.FormatPdfNumber(value.Value, isEnglish, decimals)
            : "\u2014";
}
