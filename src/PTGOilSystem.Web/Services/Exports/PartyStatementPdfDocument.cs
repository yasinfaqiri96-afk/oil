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
    bool isEnglish = false,
    SupplierContractStatementViewModel? contractGrouping = null) : IDocument
{
    // عنوان‌های «رسید/برد/بیلانس» فقط از منبع مرکزی می‌آیند؛ اعداد، جهت، ترتیب ستون‌ها
    // و فرمول بیلانس با تغییر زبان دست‌نخورده می‌مانند.
    private string Flow(CompanyFlowTextKey key) => CompanyFlowText.Get(key, isEnglish);

    private enum StatementIcon
    {
        Party,
        Document,
        Wallet,
        Debit,
        Credit,
        Balance
    }

    private const string Ink = "#171923";
    private const string Muted = "#667085";
    private const string Border = "#D9DEE7";
    private const string Green = "#07883F";
    private const string Red = "#E5222A";
    private const string Purple = "#5B3FA3";

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
            // ستون‌های عملیاتی در عرض A4 عمودی جا نمی‌شوند؛ همان قاعدهٔ صفحهٔ وب.
            page.Size(statement.ColumnOptions.UseLandscape ? PageSizes.A4.Landscape() : PageSizes.A4);
            page.MarginHorizontal(28);
            page.MarginTop(22);
            page.MarginBottom(20);
            page.PageColor(Colors.White);
            page.DefaultTextStyle(style => style.FontFamily("Vazirmatn").FontSize(8).FontColor(Ink));
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
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                var logoPath = ResolveWebAsset(statement.CompanyInfo.LogoPath);
                if (logoPath is not null)
                    row.RelativeItem(1.7f).Height(58).AlignLeft().Image(logoPath).FitArea();
                else
                    row.RelativeItem(1.7f).AlignMiddle().Text(statement.CompanyInfo.Name).Bold().FontSize(15);

                row.RelativeItem().ContentFromRightToLeft().AlignRight().Column(contact =>
                {
                    contact.Item().Text(statement.CompanyInfo.Name).Bold().FontSize(10.5f);
                    ContactLine(contact.Item(), statement.CompanyInfo.Address);
                    ContactLine(contact.Item(), statement.CompanyInfo.Phone, true);
                    ContactLine(contact.Item(), statement.CompanyInfo.Email, true);
                    ContactLine(contact.Item(), statement.CompanyInfo.Website, true);
                });
            });
            column.Item().PaddingTop(8).Element(ComposeTriColorRule);
        });
    }

    private void ComposeCompactHeader(IContainer container)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Text(statement.CompanyInfo.Name).SemiBold().FontSize(8.5f);
                row.RelativeItem().AlignRight().Text(statement.DocumentInfo.StatementNumber)
                    .FontSize(7).FontColor(Muted);
            });
            column.Item().PaddingTop(5).Element(ComposeTriColorRule);
        });
    }

    private void ComposeContent(ColumnDescriptor column)
    {
        column.Spacing(10);
        column.Item().Row(row =>
        {
            row.RelativeItem().AlignRight().Column(title =>
            {
                title.Item().Text(statement.Policy.StatementTitleFa).Bold().FontSize(18);
                title.Item().PaddingTop(1).ContentFromLeftToRight()
                    .Text(statement.Policy.StatementTitleEn).FontFamily("Poppins").FontSize(7).FontColor(Muted);
            });
            row.RelativeItem().AlignLeft().AlignMiddle().Text(statement.CourtesyText).FontSize(8).FontColor(Muted);
        });
        column.Item().Row(row =>
        {
            row.RelativeItem().Element(ComposeStatementInfo);
            row.ConstantItem(10);
            row.RelativeItem().Element(ComposePartyInfo);
        });
        column.Item().Element(ComposeSummary);
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

    private void ComposeSummary(IContainer container)
    {
        container.Border(0.7f).BorderColor(Border).CornerRadius(4).Padding(8).Row(row =>
        {
            row.RelativeItem().Element(card => SummaryCard(card, StatementIcon.Wallet, Flow(CompanyFlowTextKey.OpeningBalance), OpeningBalance(), Green));
            row.ConstantItem(1).Background(Border);
            row.RelativeItem().Element(card => SummaryCard(card, StatementIcon.Debit, Flow(CompanyFlowTextKey.TotalReceipt), TotalReceipt(), Red));
            row.ConstantItem(1).Background(Border);
            row.RelativeItem().Element(card => SummaryCard(card, StatementIcon.Credit, Flow(CompanyFlowTextKey.TotalOutflow), TotalOutflow(), Green));
            row.ConstantItem(1).Background(Border);
            row.RelativeItem().Element(card => SummaryCard(card, StatementIcon.Balance, Flow(CompanyFlowTextKey.ClosingBalance), ClosingBalanceAbsolute(), Purple,
                statement.Summary.ClosingBalanceMeaningFor(isEnglish)));
        });
    }

    private void SummaryCard(
        IContainer container,
        StatementIcon icon,
        string label,
        decimal? value,
        string color,
        string? detail = null)
    {
        container.PaddingHorizontal(6).ContentFromLeftToRight().Row(row =>
        {
            row.ConstantItem(32).Height(32)
                .Background(IconBackground(icon))
                .CornerRadius(16)
                .Padding(7)
                .Element(iconContainer => ComposeIcon(iconContainer, icon, color));
            row.ConstantItem(7);
            row.RelativeItem().ContentFromRightToLeft().Column(column =>
            {
                column.Item().AlignRight().Text(label).FontSize(6.5f).FontColor(Muted);
                column.Item().ContentFromLeftToRight().Text(text =>
                {
                    text.Span(FormatMoney(value)).Bold().FontSize(9.5f).FontColor(color);
                    text.Span("  " + statement.DocumentInfo.BaseCurrencyCode).FontSize(5.5f).FontColor(Muted);
                });
                if (!string.IsNullOrWhiteSpace(detail))
                    column.Item().AlignRight().Text(detail).FontSize(5.5f).FontColor(Muted);
            });
        });
    }

    private static string IconBackground(StatementIcon icon)
        => icon switch
        {
            StatementIcon.Debit => "#FDE9E7",
            StatementIcon.Balance => "#F0EEF8",
            _ => "#E7F4EC"
        };

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
            StatementIcon.Wallet =>
                """
                <path d="M3 6.5h15a2 2 0 0 1 2 2v10.5a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>
                <path d="M5 6.5V5a2 2 0 0 1 2-2h9v3.5M15 11h6v6h-6a3 3 0 0 1 0-6z"/>
                <circle cx="16" cy="14" r=".8" fill="CURRENT_COLOR" stroke="none"/>
                """,
            StatementIcon.Debit =>
                """
                <path d="M12 3v17M5 13l7 7 7-7"/>
                """,
            StatementIcon.Credit =>
                """
                <path d="M12 21V4M5 11l7-7 7 7"/>
                """,
            StatementIcon.Balance =>
                """
                <path d="M12 3v18M7 21h10M5 6h14"/>
                <path d="M6 6 2.5 14h7zM18 6l-3.5 8h7z"/>
                <path d="M2.5 14c.7 2 2 3 3.5 3s2.8-1 3.5-3M14.5 14c.7 2 2 3 3.5 3s2.8-1 3.5-3"/>
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
            });
            if (statement.Rows.Count == 0)
            {
                table.Cell().ColumnSpan((uint)(6 + operational.Count)).Element(EmptyCellStyle)
                    .Text("\u062F\u0631 \u0627\u06CC\u0646 \u062F\u0648\u0631\u0647 \u062A\u0631\u0627\u06A9\u0646\u0634\u06CC \u062B\u0628\u062A \u0646\u0634\u062F\u0647 \u0627\u0633\u062A.").FontColor(Muted);
            }
            else
            {
                var alternate = false;
                foreach (var row in statement.Rows)
                {
                    var shade = row.IsOpeningBalance ? "#EEF7F1" : alternate ? "#FAFAFB" : Colors.White;
                    BodyCell(table.Cell(), FormatDate(row.Date), shade, true);
                    BodyCell(table.Cell(), ValueOrDash(row.Reference), shade, true);
                    BodyCell(table.Cell(), row.DescriptionFor(isEnglish), shade);
                    foreach (var column in operational)
                        NumberCell(table.Cell(), column.Value(row), shade, column.Decimals);
                    MoneyCell(table.Cell(), RowDebit(row), shade, Red);
                    MoneyCell(table.Cell(), RowCredit(row), shade, Green);
                    MoneyCell(table.Cell(), RowBalance(row), shade, Purple);
                    alternate = !alternate;
                }
            }
            table.Cell().ColumnSpan((uint)(3 + operational.Count)).Element(TotalCellStyle)
                .AlignRight().Text("\u0645\u062C\u0645\u0648\u0639 \u062F\u0648\u0631\u0647").Bold().FontSize(7.5f);
            TotalMoneyCell(table.Cell(), TotalReceipt(), Red);
            TotalMoneyCell(table.Cell(), TotalOutflow(), Green);
            TotalMoneyCell(table.Cell(), ClosingBalance(), Purple);
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
                columns.ConstantColumn(74);
                columns.ConstantColumn(74);
                columns.ConstantColumn(78);
            });
            table.Header(header =>
            {
                HeaderCell(header.Cell(), "شماره");
                HeaderCell(header.Cell(), "قرارداد");
                HeaderCell(header.Cell(), Flow(CompanyFlowTextKey.Receipt), Red, true);
                HeaderCell(header.Cell(), Flow(CompanyFlowTextKey.Outflow), Green, true);
                HeaderCell(header.Cell(), Flow(CompanyFlowTextKey.Balance), Purple, true);
            });
            if (grouping.Rows.Count == 0)
            {
                table.Cell().ColumnSpan(5).Element(EmptyCellStyle)
                    .Text("در این دوره قراردادی با گردش مالی ثبت نشده است.").FontColor(Muted);
            }
            else
            {
                var alternate = false;
                foreach (var row in grouping.Rows)
                {
                    string shade = alternate ? "#FAFAFB" : Colors.White;
                    BodyCell(table.Cell(), row.Sequence.ToString(CultureInfo.InvariantCulture), shade, true);
                    ContractTitleCell(table.Cell(), row, shade);
                    MoneyCell(table.Cell(), Money(row.Receipt, row.ReceiptRub), shade, Red);
                    MoneyCell(table.Cell(), Money(row.Outflow, row.OutflowRub), shade, Green);
                    MoneyCell(table.Cell(), Money(row.Balance, row.BalanceRub), shade, Purple);
                    alternate = !alternate;
                }
            }
            table.Cell().ColumnSpan(2).Element(TotalCellStyle)
                .AlignRight().Text(Flow(CompanyFlowTextKey.PeriodTotal)).Bold().FontSize(7.5f);
            TotalMoneyCell(table.Cell(), Money(grouping.TotalReceipt, grouping.TotalReceiptRub), Red);
            TotalMoneyCell(table.Cell(), Money(grouping.TotalOutflow, grouping.TotalOutflowRub), Green);
            TotalMoneyCell(table.Cell(), Money(grouping.ClosingBalance, grouping.ClosingBalanceRub), Purple);
        });
    }

    private static void ContractTitleCell(IContainer container, SupplierContractStatementRow row, string background)
    {
        container.ShowEntire().Background(background).BorderBottom(0.45f).BorderColor(Border)
            .PaddingVertical(5).PaddingHorizontal(4).Column(column =>
            {
                column.Item().Text(row.Title).SemiBold().FontSize(6.8f);
                if (row.ContractQuantityMt.HasValue || row.LoadedQuantityMt.HasValue)
                {
                    column.Item().Text(
                            $"قرارداد {FormatNumber(row.ContractQuantityMt, 3)} MT / بارگیری {FormatNumber(row.LoadedQuantityMt, 3)} MT")
                        .FontSize(6).FontColor(Muted);
                }
            });
    }

    private static void HeaderCell(IContainer container, string text, string color = Ink, bool ltr = false)
    {
        var cell = container.Background("#ECEFF3").BorderBottom(1).BorderColor("#C9CFD8")
            .PaddingVertical(6).PaddingHorizontal(4).AlignMiddle().AlignCenter();
        if (ltr)
            cell = cell.ContentFromLeftToRight();
        cell.Text(text).SemiBold().FontSize(7).FontColor(color);
    }

    private static void BodyCell(IContainer container, string text, string background, bool ltr = false)
    {
        var cell = container.ShowEntire().Background(background).BorderBottom(0.45f).BorderColor(Border)
            .PaddingVertical(5).PaddingHorizontal(4).AlignMiddle();
        if (ltr)
            cell = cell.ContentFromLeftToRight();
        cell.Text(text).FontSize(6.8f);
    }

    private static void MoneyCell(IContainer container, decimal? value, string background, string color)
    {
        container.ShowEntire().Background(background).BorderBottom(0.45f).BorderColor(Border)
            .PaddingVertical(5).PaddingHorizontal(4).AlignMiddle().AlignRight()
            .ContentFromLeftToRight().Text(FormatMoney(value)).FontSize(6.8f).FontColor(color);
    }

    private static void NumberCell(IContainer container, decimal? value, string background, int decimals)
    {
        container.ShowEntire().Background(background).BorderBottom(0.45f).BorderColor(Border)
            .PaddingVertical(5).PaddingHorizontal(4).AlignMiddle().AlignRight()
            .ContentFromLeftToRight().Text(FormatNumber(value, decimals)).FontSize(6.8f);
    }

    private static IContainer EmptyCellStyle(IContainer container)
        => container.BorderBottom(0.45f).BorderColor(Border).PaddingVertical(12).AlignCenter();

    private static IContainer TotalCellStyle(IContainer container)
        => container.ShowEntire().Background("#E9ECF1").BorderTop(1).BorderColor("#AEB6C2")
            .PaddingVertical(6).PaddingHorizontal(4).AlignMiddle();

    private static void TotalMoneyCell(IContainer container, decimal? value, string color)
    {
        TotalCellStyle(container).AlignRight().ContentFromLeftToRight()
            .Text(FormatMoney(value)).Bold().FontSize(7.5f).FontColor(color);
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
        container.PaddingTop(8).Column(column =>
        {
            column.Item().Element(ComposeTriColorRule);
            column.Item().PaddingTop(5).Row(row =>
            {
                row.RelativeItem().ContentFromRightToLeft().Text(statement.CompanyInfo.Name).SemiBold().FontSize(6.5f);
                row.RelativeItem().AlignRight().ContentFromLeftToRight().Text(text =>
                {
                    text.DefaultTextStyle(style => style.FontSize(6).FontColor(Muted));
                    text.Span("\u0635\u0641\u062D\u0647 ");
                    text.CurrentPageNumber();
                    text.Span(" \u0627\u0632 ");
                    text.TotalPages();
                });
            });
            column.Item().PaddingTop(2).Row(row =>
            {
                row.RelativeItem().ContentFromRightToLeft().Text(CompanyContactSummary()).FontSize(5.5f).FontColor(Muted);
                row.RelativeItem().AlignRight().ContentFromLeftToRight()
                    .Text("\u062A\u0648\u0644\u06CC\u062F: " + FormatGeneratedAt(statement.DocumentInfo.GeneratedAtUtc))
                    .FontSize(5.5f).FontColor(Muted);
            });
        });
    }

    private static void ComposeTriColorRule(IContainer container)
    {
        container.Height(2).Row(row =>
        {
            row.RelativeItem().Background(Green);
            row.RelativeItem().Background(Ink);
            row.RelativeItem().Background(Red);
        });
    }

    private static void ContactLine(IContainer container, string? value, bool ltr = false)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var target = container.PaddingTop(2).AlignRight();
        if (ltr)
            target = target.ContentFromLeftToRight();
        target.Text(value.Trim()).FontSize(6.5f).FontColor(Muted);
    }

    private string CompanyContactSummary()
        => string.Join("  -  ", new[]
        {
            statement.CompanyInfo.Address,
            statement.CompanyInfo.Phone,
            statement.CompanyInfo.Email,
            statement.CompanyInfo.Website
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

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

        var root = Path.GetFullPath(webRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var relative = configuredPath.Trim().TrimStart('~', '/', '\\')
            .Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(root, relative));
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)
            ? candidate
            : null;
    }

    private static string ValueOrDash(string? value)
        => string.IsNullOrWhiteSpace(value) ? "\u2014" : value.Trim();

    private static string FormatDate(DateTime value)
        => value.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);

    private static string FormatPeriod(DateTime? from, DateTime? to)
        => $"{(from.HasValue ? FormatDate(from.Value) : "\u0627\u0628\u062A\u062F\u0627\u06CC \u062D\u0633\u0627\u0628")} - {(to.HasValue ? FormatDate(to.Value) : "\u0627\u0645\u0631\u0648\u0632")}";

    private static string FormatGeneratedAt(DateTime value)
        => value.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    internal static string FormatMoney(decimal? value)
        => value.HasValue ? value.Value.ToString("N2", CultureInfo.InvariantCulture) : "\u2014";

    private static string FormatNumber(decimal? value, int decimals)
        => value.HasValue
            ? value.Value.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            : "\u2014";
}
