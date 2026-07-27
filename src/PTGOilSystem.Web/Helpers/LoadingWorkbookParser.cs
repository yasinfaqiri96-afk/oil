using System.Globalization;
using System.Runtime.CompilerServices;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Loading;

namespace PTGOilSystem.Web.Helpers;

public sealed record LoadingWorkbookImportResult(
    LoadingTransportType TransportType,
    IReadOnlyList<LoadingCreateRowViewModel> Rows,
    string? SourceSheetName = null,
    string? OriginLocationName = null,
    DateTime? ReportDate = null,
    string? VesselName = null,
    string? ProductName = null);

public static class LoadingWorkbookParser
{
    private static readonly ConditionalWeakTable<WorkbookPart, string[]> SharedStringLookups = new();
    private static readonly string[] DateAliases = ["date", "loadingdate", "dateloading", NormalizeHeader("تاریخ")];

    private static readonly string[] WagonReferenceAliases =
        ["rwbno", "rwbnumber", "rwb", "billoflading", "billofladingno", "cmr", "cmrno"];

    private static readonly string[] WagonTransportAliases =
        ["wagonno", "wagonnumber", "wagon", "wagonsno", "wagonsnumber", "wagons"];
    private static readonly string[] QuantityAliases = ["loadedquantitymt", "loadedquantity", "quantitymt", "weight", "netweight", NormalizeHeader("وزن"), NormalizeHeader("وزنسیمیر")];
    private static readonly string[] PlattsAliases = ["platts", "plattsusd"];
    private static readonly string[] LoadingPriceAliases = ["loadingprice", "loadingpriceusd"];
    private static readonly string[] LoadingAmountAliases = ["loadingamount", "amount", "total", "totalamount"];
    // ارقام روبلی فایل: قیمت فی تن و مجموع. هدرهای مختلف انگلیسی/دری شناسایی می‌شوند.
    private static readonly string[] RubUnitPriceAliases =
        ["rubprice", "pricerub", "rubpermt", "rubperton", "rubmt", "rubton", "rubpricepermt", "priceperrub",
         NormalizeHeader("قیمت روبلی"), NormalizeHeader("قیمت روبلی فی تن"), NormalizeHeader("فی تن روبل"), NormalizeHeader("روبل فی تن")];
    private static readonly string[] RubTotalAliases =
        ["totalrub", "rubtotal", "trub", "rubamount", "amountrub", "totalrubvalue", "rubvalue",
         NormalizeHeader("ارزش روبلی"), NormalizeHeader("مجموع روبل"), NormalizeHeader("مجموع روبلی")];

    private static readonly string[] ConsigneeAliases = ["consignee"];
    private static readonly string[] LogisticsAliases = ["transportationcompany", "transportcompany", "logisticscompany"];
    private static readonly string[] DestinationAliases = ["destination", "distination"];

    private static readonly string[] TruckReferenceAliases = ["cmr", "cmrno", "referenceno"];
    private static readonly string[] TruckTransportAliases = ["trucks", "truck", "trucknumber", "platenumber", NormalizeHeader("نمبردموتر"), NormalizeHeader("نمبرموتر")];
    private static readonly string[] TruckDestinationAliases = ["belongto", "destination", NormalizeHeader("مقصد")];
    private static readonly string[] TruckExpenseRateAliases = ["transshipmentrental", "transshipmentrental$", "transshipmentrentalusd", NormalizeHeader("فیتنکرایه")];

    private static readonly string[] TruckRentReferenceAliases = ["cmr", NormalizeHeader("سیمیر")];
    private static readonly string[] TruckRentTransportAliases = ["truck", NormalizeHeader("نمبرموتر")];
    private static readonly string[] TruckRentPayableAliases = ["payablerent", NormalizeHeader("کرایهپرداختی")];

    private static readonly string[] TruckReportDateAliases = ["dateofreport"];
    private static readonly string[] TruckReportVesselAliases = ["vessel"];
    private static readonly string[] TruckReportLocationAliases = ["location"];
    private static readonly string[] TruckReportProductAliases = ["product"];

    private static readonly string[] WagonRailReferenceAliases = ["cmr", "cmrno", "rwb", "rwbno", NormalizeHeader("سیمیر")];
    private static readonly string[] WagonRailTransportAliases = ["wagonno", "wagonnumber", "wagon", NormalizeHeader("نمبرواگون"), NormalizeHeader("نمبرواگن")];
    private static readonly string[] WagonRailChargeableAliases = ["chargeablequantity", "chargeableweight", NormalizeHeader("وزنمحاسبه")];
    private static readonly string[] WagonRailRateAliases = ["railwayrate", "railwayrateusd", "rate", NormalizeHeader("فیتنمحاسبه")];
    private static readonly string[] WagonRailExpenseAliases = ["railwayexpense", "railwayexpenseusd", "railwaycost", NormalizeHeader("کرایهخطآهن")];

    private static readonly string[] ExplicitDateFormats =
    [
        "d.M.yyyy",
        "dd.MM.yyyy",
        "d.MM.yyyy",
        "dd.M.yyyy",
        "yyyy-MM-dd",
        "yyyy/M/d",
        "yyyy/MM/dd"
    ];

    public static LoadingWorkbookImportResult Parse(Stream stream)
    {
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = document.WorkbookPart ?? throw new InvalidDataException("ساختار فایل اکسل معتبر نیست.");
        var sheets = GetWorkbookSheets(workbookPart);

        var truckResult = TryParseTruckWorkbook(workbookPart, sheets);
        if (truckResult is not null)
        {
            return truckResult;
        }

        var wagonResult = TryParseWagonWorkbook(workbookPart, sheets);
        if (wagonResult is not null)
        {
            return wagonResult;
        }

        throw new InvalidDataException("ساختار فایل اکسل بارگیری شناسایی نشد.");
    }

    private static LoadingWorkbookImportResult? TryParseWagonWorkbook(
        WorkbookPart workbookPart,
        IReadOnlyList<WorkbookSheetContext> sheets)
    {
        var railwayLookup = TryParseWagonRailwayLookup(workbookPart, sheets);
        var importedRows = new List<LoadingCreateRowViewModel>();
        var sourceSheetNames = new List<string>();

        foreach (var sheet in sheets)
        {
            if (IsNonLoadingImportSheet(sheet.Name))
            {
                continue;
            }

            var headerRow = FindHeaderRow(
                sheet.Rows,
                workbookPart,
                DateAliases,
                QuantityAliases,
                WagonReferenceAliases,
                WagonTransportAliases);

            if (headerRow is null)
            {
                continue;
            }

            var columns = BuildWagonColumnMap(headerRow, workbookPart);
            if (!columns.ContainsKey(nameof(LoadingCreateRowViewModel.LoadingDate))
                || !columns.ContainsKey(nameof(LoadingCreateRowViewModel.BillOfLadingNumber))
                || !columns.ContainsKey(nameof(LoadingCreateRowViewModel.WagonNumber))
                || !columns.ContainsKey(nameof(LoadingCreateRowViewModel.LoadedQuantityMt)))
            {
                throw new InvalidDataException("ستون‌های اصلی فایل واگن کامل نیستند. حداقل Date، RWB No، Wagon No و Loaded quantity (MT) لازم است.");
            }

            var sheetRows = new List<LoadingCreateRowViewModel>();
            foreach (var row in sheet.Rows.Where(r => (r.RowIndex?.Value ?? 0) > (headerRow.RowIndex?.Value ?? 0)))
            {
                var cells = ToCellMap(row);
                var mappedRow = MapWagonRow(cells, columns, workbookPart, railwayLookup);
                if (IsMeaningfulWagonRow(mappedRow))
                {
                    sheetRows.Add(mappedRow);
                }
            }

            if (sheetRows.Count > 0)
            {
                importedRows.AddRange(sheetRows);
                sourceSheetNames.Add(sheet.Name);
            }
        }

        if (importedRows.Count > 0)
        {
            return new LoadingWorkbookImportResult(
                LoadingTransportType.Wagon,
                importedRows,
                SourceSheetName: string.Join(", ", sourceSheetNames));
        }

        return null;
    }

    private static LoadingWorkbookImportResult? TryParseTruckWorkbook(
        WorkbookPart workbookPart,
        IReadOnlyList<WorkbookSheetContext> sheets)
    {
        var truckFreightLookup = TryParseTruckFreightLookup(workbookPart, sheets);
        var importedRows = new List<LoadingCreateRowViewModel>();
        var sourceSheetNames = new List<string>();
        TruckWorkbookMetadata? firstMetadata = null;

        foreach (var sheet in sheets)
        {
            if (IsNonLoadingImportSheet(sheet.Name))
            {
                continue;
            }

            var headerRow = FindHeaderRow(
                sheet.Rows,
                workbookPart,
                TruckReferenceAliases,
                TruckTransportAliases,
                QuantityAliases,
                TruckDestinationAliases,
                ConsigneeAliases);

            if (headerRow is null)
            {
                continue;
            }

            var columns = BuildTruckColumnMap(headerRow, workbookPart);
            if (!columns.ContainsKey(nameof(LoadingCreateRowViewModel.LoadingDate))
                || !columns.ContainsKey(nameof(LoadingCreateRowViewModel.BillOfLadingNumber))
                || !columns.ContainsKey(nameof(LoadingCreateRowViewModel.ImportedTransportReference))
                || !columns.ContainsKey(nameof(LoadingCreateRowViewModel.LoadedQuantityMt)))
            {
                throw new InvalidDataException("ستون‌های اصلی فایل بارگیری موتر کامل نیستند. حداقل Date، CMR، Trucks و Loaded quantity (MT) لازم است.");
            }

            var metadata = ParseTruckMetadata(sheet.Rows, headerRow, workbookPart);
            firstMetadata ??= metadata;
            var sheetRows = new List<LoadingCreateRowViewModel>();

            foreach (var row in sheet.Rows.Where(r => (r.RowIndex?.Value ?? 0) > (headerRow.RowIndex?.Value ?? 0)))
            {
                var cells = ToCellMap(row);
                var mappedRow = MapTruckRow(cells, columns, workbookPart, metadata, truckFreightLookup);
                if (IsMeaningfulTruckRow(mappedRow))
                {
                    sheetRows.Add(mappedRow);
                }
            }

            if (sheetRows.Count > 0)
            {
                importedRows.AddRange(sheetRows);
                sourceSheetNames.Add(sheet.Name);
            }
        }

        if (importedRows.Count > 0)
        {
            return new LoadingWorkbookImportResult(
                LoadingTransportType.Truck,
                importedRows,
                SourceSheetName: string.Join(", ", sourceSheetNames),
                OriginLocationName: firstMetadata?.OriginLocationName,
                ReportDate: firstMetadata?.ReportDate,
                VesselName: firstMetadata?.VesselName,
                ProductName: firstMetadata?.ProductName);
        }

        return null;
    }

    private static IReadOnlyList<WorkbookSheetContext> GetWorkbookSheets(WorkbookPart workbookPart)
    {
        var workbookSheets = workbookPart.Workbook.Sheets?.Elements<Sheet>().ToList()
            ?? throw new InvalidDataException("در فایل اکسل هیچ شیتی پیدا نشد.");

        var sheets = new List<WorkbookSheetContext>(workbookSheets.Count);
        foreach (var sheet in workbookSheets)
        {
            var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!);
            var sheetData = worksheetPart.Worksheet.GetFirstChild<SheetData>();
            if (sheetData is null)
            {
                continue;
            }

            sheets.Add(new WorkbookSheetContext(
                sheet.Name?.Value ?? string.Empty,
                sheetData.Elements<Row>().ToList()));
        }

        return sheets;
    }

    private static Dictionary<string, string> BuildWagonColumnMap(Row headerRow, WorkbookPart workbookPart)
    {
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in headerRow.Elements<Cell>())
        {
            if (cell.CellReference is null)
            {
                continue;
            }

            var normalizedHeader = NormalizeHeader(ReadCellText(cell, workbookPart));
            if (string.IsNullOrWhiteSpace(normalizedHeader))
            {
                continue;
            }

            var column = GetColumnName(cell.CellReference?.Value ?? string.Empty);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.LoadingDate), column, normalizedHeader, DateAliases);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.BillOfLadingNumber), column, normalizedHeader, WagonReferenceAliases);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.WagonNumber), column, normalizedHeader, WagonTransportAliases);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.LoadedQuantityMt), column, normalizedHeader, QuantityAliases);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.PlattsUsd), column, normalizedHeader, PlattsAliases);
            AssignPlattsColumn(columns, column, normalizedHeader);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.LoadingPriceUsd), column, normalizedHeader, LoadingPriceAliases);
            AssignLoadingPriceColumn(columns, column, normalizedHeader);
            AssignNumericPriceColumn(columns, column, normalizedHeader);
            AssignColumn(columns, "LoadingAmount", column, normalizedHeader, LoadingAmountAliases);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.SettlementUnitPriceRub), column, normalizedHeader, RubUnitPriceAliases);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.SettlementValueRub), column, normalizedHeader, RubTotalAliases);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.ConsigneeName), column, normalizedHeader, ConsigneeAliases);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.LogisticsCompanyName), column, normalizedHeader, LogisticsAliases);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.DestinationName), column, normalizedHeader, DestinationAliases);
        }

        return columns;
    }

    private static Dictionary<string, string> BuildTruckColumnMap(Row headerRow, WorkbookPart workbookPart)
    {
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in headerRow.Elements<Cell>())
        {
            if (cell.CellReference is null)
            {
                continue;
            }

            var normalizedHeader = NormalizeHeader(ReadCellText(cell, workbookPart));
            if (string.IsNullOrWhiteSpace(normalizedHeader))
            {
                continue;
            }

            var column = GetColumnName(cell.CellReference?.Value ?? string.Empty);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.LoadingDate), column, normalizedHeader, DateAliases);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.BillOfLadingNumber), column, normalizedHeader, TruckReferenceAliases);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.ImportedTransportReference), column, normalizedHeader, TruckTransportAliases);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.LoadedQuantityMt), column, normalizedHeader, QuantityAliases);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.SettlementUnitPriceRub), column, normalizedHeader, RubUnitPriceAliases);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.SettlementValueRub), column, normalizedHeader, RubTotalAliases);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.DestinationName), column, normalizedHeader, TruckDestinationAliases);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.ConsigneeName), column, normalizedHeader, ConsigneeAliases);

            if (!columns.ContainsKey("TruckExpenseFallback")
                && TruckExpenseRateAliases.Contains(normalizedHeader, StringComparer.Ordinal))
            {
                columns["TruckExpenseFallback"] = IncrementColumnName(column);
            }
        }

        return columns;
    }

    private static Dictionary<string, string> BuildTruckFreightColumnMap(Row headerRow, WorkbookPart workbookPart)
    {
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in headerRow.Elements<Cell>())
        {
            if (cell.CellReference is null)
            {
                continue;
            }

            var normalizedHeader = NormalizeHeader(ReadCellText(cell, workbookPart));
            if (string.IsNullOrWhiteSpace(normalizedHeader))
            {
                continue;
            }

            var column = GetColumnName(cell.CellReference?.Value ?? string.Empty);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.BillOfLadingNumber), column, normalizedHeader, TruckRentReferenceAliases);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.ImportedTransportReference), column, normalizedHeader, TruckRentTransportAliases);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.TransportExpenseUsd), column, normalizedHeader, TruckRentPayableAliases);
        }

        return columns;
    }

    private static Dictionary<string, string> BuildWagonRailwayColumnMap(Row headerRow, WorkbookPart workbookPart)
    {
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in headerRow.Elements<Cell>())
        {
            if (cell.CellReference is null)
            {
                continue;
            }

            var normalizedHeader = NormalizeHeader(ReadCellText(cell, workbookPart));
            if (string.IsNullOrWhiteSpace(normalizedHeader))
            {
                continue;
            }

            var column = GetColumnName(cell.CellReference?.Value ?? string.Empty);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.BillOfLadingNumber), column, normalizedHeader, WagonRailReferenceAliases);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.WagonNumber), column, normalizedHeader, WagonRailTransportAliases);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.ChargeableQuantityMt), column, normalizedHeader, WagonRailChargeableAliases);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.RailwayRateUsd), column, normalizedHeader, WagonRailRateAliases);
            AssignColumn(columns, nameof(LoadingCreateRowViewModel.RailwayExpenseUsd), column, normalizedHeader, WagonRailExpenseAliases);
        }

        return columns;
    }

    private static Dictionary<string, WagonRailwayCost> TryParseWagonRailwayLookup(
        WorkbookPart workbookPart,
        IReadOnlyList<WorkbookSheetContext> sheets)
    {
        var lookup = new Dictionary<string, WagonRailwayCost>(StringComparer.Ordinal);

        foreach (var sheet in sheets)
        {
            var headerRow = FindHeaderRow(
                sheet.Rows,
                workbookPart,
                WagonRailReferenceAliases,
                WagonRailTransportAliases,
                WagonRailExpenseAliases);

            if (headerRow is null)
            {
                continue;
            }

            var columns = BuildWagonRailwayColumnMap(headerRow, workbookPart);
            if (!columns.ContainsKey(nameof(LoadingCreateRowViewModel.BillOfLadingNumber))
                || !columns.ContainsKey(nameof(LoadingCreateRowViewModel.WagonNumber))
                || !columns.ContainsKey(nameof(LoadingCreateRowViewModel.RailwayExpenseUsd)))
            {
                continue;
            }

            foreach (var row in sheet.Rows.Where(r => (r.RowIndex?.Value ?? 0) > (headerRow.RowIndex?.Value ?? 0)))
            {
                var cells = ToCellMap(row);
                var reference = ReadText(cells, columns, nameof(LoadingCreateRowViewModel.BillOfLadingNumber), workbookPart);
                var wagon = ReadText(cells, columns, nameof(LoadingCreateRowViewModel.WagonNumber), workbookPart);
                var railwayExpense = TryReadDecimal(cells, columns, nameof(LoadingCreateRowViewModel.RailwayExpenseUsd), workbookPart);

                if (string.IsNullOrWhiteSpace(reference)
                    || string.IsNullOrWhiteSpace(wagon)
                    || !railwayExpense.HasValue)
                {
                    continue;
                }

                var chargeableQuantity = TryReadDecimal(cells, columns, nameof(LoadingCreateRowViewModel.ChargeableQuantityMt), workbookPart);
                var railwayRate = TryReadDecimal(cells, columns, nameof(LoadingCreateRowViewModel.RailwayRateUsd), workbookPart);
                var key = ComposeTransportKey(reference, wagon);

                lookup[key] = lookup.TryGetValue(key, out var existing)
                    ? existing.Add(chargeableQuantity, railwayRate, railwayExpense.Value)
                    : WagonRailwayCost.Create(chargeableQuantity, railwayRate, railwayExpense.Value);
            }
        }

        return lookup;
    }

    private static Dictionary<string, decimal> TryParseTruckFreightLookup(
        WorkbookPart workbookPart,
        IReadOnlyList<WorkbookSheetContext> sheets)
    {
        var lookup = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var sheet in sheets)
        {
            var headerRow = FindHeaderRow(
                sheet.Rows,
                workbookPart,
                TruckRentReferenceAliases,
                TruckRentTransportAliases,
                TruckRentPayableAliases);

            if (headerRow is null)
            {
                continue;
            }

            var columns = BuildTruckFreightColumnMap(headerRow, workbookPart);
            if (!columns.ContainsKey(nameof(LoadingCreateRowViewModel.BillOfLadingNumber))
                || !columns.ContainsKey(nameof(LoadingCreateRowViewModel.ImportedTransportReference))
                || !columns.ContainsKey(nameof(LoadingCreateRowViewModel.TransportExpenseUsd)))
            {
                continue;
            }

            foreach (var row in sheet.Rows.Where(r => (r.RowIndex?.Value ?? 0) > (headerRow.RowIndex?.Value ?? 0)))
            {
                var cells = ToCellMap(row);
                var reference = ReadText(cells, columns, nameof(LoadingCreateRowViewModel.BillOfLadingNumber), workbookPart);
                var transport = ReadText(cells, columns, nameof(LoadingCreateRowViewModel.ImportedTransportReference), workbookPart);
                var payable = TryReadDecimal(cells, columns, nameof(LoadingCreateRowViewModel.TransportExpenseUsd), workbookPart);

                if (string.IsNullOrWhiteSpace(reference)
                    || string.IsNullOrWhiteSpace(transport)
                    || !payable.HasValue)
                {
                    continue;
                }

                lookup[ComposeTransportKey(reference, transport)] = payable.Value;
            }
        }

        return lookup;
    }

    private static LoadingCreateRowViewModel MapWagonRow(
        IReadOnlyDictionary<string, Cell> cells,
        IReadOnlyDictionary<string, string> columns,
        WorkbookPart workbookPart,
        IReadOnlyDictionary<string, WagonRailwayCost> railwayLookup)
    {
        var row = new LoadingCreateRowViewModel
        {
            LoadingDate = TryReadDate(cells, columns, nameof(LoadingCreateRowViewModel.LoadingDate), workbookPart) ?? DateTime.UtcNow.Date,
            BillOfLadingNumber = ReadText(cells, columns, nameof(LoadingCreateRowViewModel.BillOfLadingNumber), workbookPart),
            WagonNumber = ReadText(cells, columns, nameof(LoadingCreateRowViewModel.WagonNumber), workbookPart),
            LogisticsCompanyName = ReadText(cells, columns, nameof(LoadingCreateRowViewModel.LogisticsCompanyName), workbookPart),
            ConsigneeName = ReadText(cells, columns, nameof(LoadingCreateRowViewModel.ConsigneeName), workbookPart),
            DestinationName = ReadText(cells, columns, nameof(LoadingCreateRowViewModel.DestinationName), workbookPart)
        };

        row.LoadedQuantityMt = TryReadDecimal(cells, columns, nameof(LoadingCreateRowViewModel.LoadedQuantityMt), workbookPart) ?? 0m;
        row.PlattsUsd = TryReadDecimal(cells, columns, nameof(LoadingCreateRowViewModel.PlattsUsd), workbookPart);
        row.LoadingPriceUsd = TryReadDecimal(cells, columns, nameof(LoadingCreateRowViewModel.LoadingPriceUsd), workbookPart);
        ApplyFileRubFigures(row, cells, columns, workbookPart);

        if (!row.LoadingPriceUsd.HasValue)
        {
            var amount = TryReadDecimal(cells, columns, "LoadingAmount", workbookPart);
            if (amount.HasValue && row.LoadedQuantityMt > 0)
            {
                row.LoadingPriceUsd = decimal.Round(amount.Value / row.LoadedQuantityMt, 4, MidpointRounding.AwayFromZero);
            }
        }

        if (!string.IsNullOrWhiteSpace(row.BillOfLadingNumber)
            && !string.IsNullOrWhiteSpace(row.WagonNumber)
            && railwayLookup.TryGetValue(ComposeTransportKey(row.BillOfLadingNumber, row.WagonNumber), out var railwayCost))
        {
            row.ChargeableQuantityMt = railwayCost.ChargeableQuantityMt;
            row.RailwayRateUsd = railwayCost.EffectiveRateUsd;
            row.FreightRateUsdPerMt = railwayCost.EffectiveRateUsd;
            row.RailwayExpenseUsd = railwayCost.RailwayExpenseUsd;
        }

        return row;
    }

    private static LoadingCreateRowViewModel MapTruckRow(
        IReadOnlyDictionary<string, Cell> cells,
        IReadOnlyDictionary<string, string> columns,
        WorkbookPart workbookPart,
        TruckWorkbookMetadata metadata,
        IReadOnlyDictionary<string, decimal> truckFreightLookup)
    {
        var billOfLadingNumber = ReadText(cells, columns, nameof(LoadingCreateRowViewModel.BillOfLadingNumber), workbookPart);
        var importedTransportReference = ReadText(cells, columns, nameof(LoadingCreateRowViewModel.ImportedTransportReference), workbookPart);
        var destinationName = ReadText(cells, columns, nameof(LoadingCreateRowViewModel.DestinationName), workbookPart);

        var row = new LoadingCreateRowViewModel
        {
            LoadingDate = TryReadDate(cells, columns, nameof(LoadingCreateRowViewModel.LoadingDate), workbookPart) ?? metadata.ReportDate ?? DateTime.UtcNow.Date,
            BillOfLadingNumber = billOfLadingNumber,
            ImportedTransportReference = importedTransportReference,
            DestinationName = destinationName,
            ConsigneeName = ReadText(cells, columns, nameof(LoadingCreateRowViewModel.ConsigneeName), workbookPart),
            RouteDescription = BuildRouteDescription(metadata.OriginLocationName, destinationName)
        };

        row.LoadedQuantityMt = TryReadDecimal(cells, columns, nameof(LoadingCreateRowViewModel.LoadedQuantityMt), workbookPart) ?? 0m;
        ApplyFileRubFigures(row, cells, columns, workbookPart);

        if (!string.IsNullOrWhiteSpace(billOfLadingNumber)
            && !string.IsNullOrWhiteSpace(importedTransportReference)
            && truckFreightLookup.TryGetValue(ComposeTransportKey(billOfLadingNumber, importedTransportReference), out var freight))
        {
            row.TransportExpenseUsd = freight;
            row.FreightRateUsdPerMt = row.LoadedQuantityMt > 0m
                ? decimal.Round(freight / row.LoadedQuantityMt, 4, MidpointRounding.AwayFromZero)
                : null;
        }
        else
        {
            row.TransportExpenseUsd = TryReadDecimal(cells, columns, "TruckExpenseFallback", workbookPart);
            row.FreightRateUsdPerMt = row.TransportExpenseUsd.HasValue && row.LoadedQuantityMt > 0m
                ? decimal.Round(row.TransportExpenseUsd.Value / row.LoadedQuantityMt, 4, MidpointRounding.AwayFromZero)
                : null;
        }

        return row;
    }

    // ارقام روبلی فایل را می‌خواند و کمبود را محاسبه می‌کند:
    //  - اگر هر دو موجود بود: همان مقادیر فایل.
    //  - اگر فقط RUB/MT بود: TotalRUB = Quantity × RUB/MT.
    //  - اگر فقط TotalRUB بود: RUB/MT = TotalRUB ÷ Quantity.
    //  - اگر هیچ‌کدام نبود: هر دو null (رفتار فعلی نمی‌شکند).
    // مقادیر غیرعددی (مثل فرمول خراب #REF!) توسط TryReadDecimal نادیده گرفته می‌شوند.
    private static void ApplyFileRubFigures(
        LoadingCreateRowViewModel row,
        IReadOnlyDictionary<string, Cell> cells,
        IReadOnlyDictionary<string, string> columns,
        WorkbookPart workbookPart)
    {
        var unitPriceRub = NormalizeNonNegative(
            TryReadDecimal(cells, columns, nameof(LoadingCreateRowViewModel.SettlementUnitPriceRub), workbookPart));
        var totalRub = NormalizeNonNegative(
            TryReadDecimal(cells, columns, nameof(LoadingCreateRowViewModel.SettlementValueRub), workbookPart));

        if (!unitPriceRub.HasValue && totalRub.HasValue && row.LoadedQuantityMt > 0m)
        {
            unitPriceRub = decimal.Round(totalRub.Value / row.LoadedQuantityMt, 4, MidpointRounding.AwayFromZero);
        }
        else if (unitPriceRub.HasValue && !totalRub.HasValue && row.LoadedQuantityMt > 0m)
        {
            totalRub = decimal.Round(row.LoadedQuantityMt * unitPriceRub.Value, 4, MidpointRounding.AwayFromZero);
        }

        row.SettlementUnitPriceRub = unitPriceRub;
        row.SettlementValueRub = totalRub;
    }

    private static decimal? NormalizeNonNegative(decimal? value)
        => value.HasValue && value.Value > 0m ? value.Value : null;

    private static TruckWorkbookMetadata ParseTruckMetadata(
        IReadOnlyList<Row> rows,
        Row headerRow,
        WorkbookPart workbookPart)
    {
        string? originLocationName = null;
        string? vesselName = null;
        string? productName = null;
        DateTime? reportDate = null;

        foreach (var row in rows.Where(r => (r.RowIndex?.Value ?? 0) < (headerRow.RowIndex?.Value ?? 0)))
        {
            var orderedCells = row.Elements<Cell>()
                .Where(c => c.CellReference is not null)
                .OrderBy(c => GetColumnOrdinal(GetColumnName(c.CellReference?.Value ?? string.Empty)))
                .ToList();

            if (orderedCells.Count == 0)
            {
                continue;
            }

            var label = NormalizeHeader(ReadCellText(orderedCells[0], workbookPart));
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            var valueCell = orderedCells.Skip(1)
                .FirstOrDefault(cell => !string.IsNullOrWhiteSpace(ReadCellText(cell, workbookPart)));

            if (valueCell is null)
            {
                continue;
            }

            if (TruckReportDateAliases.Contains(label, StringComparer.Ordinal))
            {
                reportDate = TryReadDate(valueCell, workbookPart) ?? reportDate;
                continue;
            }

            var value = ReadCellText(valueCell, workbookPart).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (TruckReportLocationAliases.Contains(label, StringComparer.Ordinal))
            {
                originLocationName = value;
            }
            else if (TruckReportVesselAliases.Contains(label, StringComparer.Ordinal))
            {
                vesselName = value;
            }
            else if (TruckReportProductAliases.Contains(label, StringComparer.Ordinal))
            {
                productName = value;
            }
        }

        return new TruckWorkbookMetadata(originLocationName, vesselName, productName, reportDate);
    }

    private static Row? FindHeaderRow(
        IEnumerable<Row> rows,
        WorkbookPart workbookPart,
        params string[][] requiredAliasGroups)
    {
        foreach (var row in rows)
        {
            var normalizedValues = row.Elements<Cell>()
                .Select(cell => NormalizeHeader(ReadCellText(cell, workbookPart)))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();

            if (normalizedValues.Count == 0)
            {
                continue;
            }

            if (requiredAliasGroups.All(group => normalizedValues.Any(value => group.Contains(value, StringComparer.Ordinal))))
            {
                return row;
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, Cell> ToCellMap(Row row)
        => row.Elements<Cell>()
            .Where(c => c.CellReference is not null)
            .ToDictionary(
                c => GetColumnName(c.CellReference?.Value ?? string.Empty),
                c => c,
                StringComparer.OrdinalIgnoreCase);

    private static void AssignColumn(
        IDictionary<string, string> columns,
        string fieldName,
        string column,
        string normalizedHeader,
        IEnumerable<string> aliases)
    {
        if (!columns.ContainsKey(fieldName) && aliases.Contains(normalizedHeader, StringComparer.Ordinal))
        {
            columns[fieldName] = column;
        }
    }

    private static void AssignPlattsColumn(
        IDictionary<string, string> columns,
        string column,
        string normalizedHeader)
    {
        if (!columns.ContainsKey(nameof(LoadingCreateRowViewModel.PlattsUsd))
            && normalizedHeader.StartsWith("platts", StringComparison.Ordinal))
        {
            columns[nameof(LoadingCreateRowViewModel.PlattsUsd)] = column;
        }
    }

    private static void AssignLoadingPriceColumn(
        IDictionary<string, string> columns,
        string column,
        string normalizedHeader)
    {
        if (columns.ContainsKey(nameof(LoadingCreateRowViewModel.LoadingPriceUsd)))
        {
            return;
        }

        if (normalizedHeader.StartsWith("discount", StringComparison.Ordinal))
        {
            columns[nameof(LoadingCreateRowViewModel.LoadingPriceUsd)] = column;
        }
    }

    private static void AssignNumericPriceColumn(
        IDictionary<string, string> columns,
        string column,
        string normalizedHeader)
    {
        if (columns.ContainsKey(nameof(LoadingCreateRowViewModel.LoadingPriceUsd))
            || string.IsNullOrWhiteSpace(normalizedHeader))
        {
            return;
        }

        if (decimal.TryParse(normalizedHeader, NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
            && price > 0)
        {
            columns[nameof(LoadingCreateRowViewModel.LoadingPriceUsd)] = column;
        }
    }

    private static string? ReadText(
        IReadOnlyDictionary<string, Cell> cells,
        IReadOnlyDictionary<string, string> columns,
        string fieldName,
        WorkbookPart workbookPart)
    {
        if (!columns.TryGetValue(fieldName, out var column)
            || !cells.TryGetValue(column, out var cell))
        {
            return null;
        }

        var value = ReadCellText(cell, workbookPart);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
    }

    private static DateTime? TryReadDate(
        IReadOnlyDictionary<string, Cell> cells,
        IReadOnlyDictionary<string, string> columns,
        string fieldName,
        WorkbookPart workbookPart)
    {
        if (!columns.TryGetValue(fieldName, out var column)
            || !cells.TryGetValue(column, out var cell))
        {
            return null;
        }

        return TryReadDate(cell, workbookPart);
    }

    private static DateTime? TryReadDate(Cell cell, WorkbookPart workbookPart)
    {
        var text = ReadCellText(cell, workbookPart);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var serial) && serial is > 1 and < 80000)
        {
            return DateTime.FromOADate(serial).Date;
        }

        if (DateTime.TryParseExact(
                text.Trim(),
                ExplicitDateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var exactDate))
        {
            return exactDate.Date;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var invariantDate))
        {
            return invariantDate.Date;
        }

        if (DateTime.TryParse(text, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces, out var usDate))
        {
            return usDate.Date;
        }

        return null;
    }

    private static decimal? TryReadDecimal(
        IReadOnlyDictionary<string, Cell> cells,
        IReadOnlyDictionary<string, string> columns,
        string fieldName,
        WorkbookPart workbookPart)
    {
        if (!columns.TryGetValue(fieldName, out var column)
            || !cells.TryGetValue(column, out var cell))
        {
            return null;
        }

        return TryReadDecimal(cell, workbookPart);
    }

    private static decimal? TryReadDecimal(Cell cell, WorkbookPart workbookPart)
    {
        var text = ReadCellText(cell, workbookPart);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = text
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("$", string.Empty, StringComparison.Ordinal)
            .Replace("€", string.Empty, StringComparison.Ordinal)
            .Trim();

        return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string ReadCellText(Cell cell, WorkbookPart workbookPart)
    {
        if (cell.DataType?.Value == CellValues.SharedString)
        {
            var sharedStrings = SharedStringLookups.GetValue(
                workbookPart,
                static part => part.SharedStringTablePart?.SharedStringTable?
                    .Elements<SharedStringItem>()
                    .Select(item => item.InnerText)
                    .ToArray()
                    ?? []);

            return int.TryParse(cell.CellValue?.Text, out var index)
                   && (uint)index < (uint)sharedStrings.Length
                ? sharedStrings[index]
                : string.Empty;
        }

        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }

        return cell.CellValue?.Text ?? cell.InnerText ?? string.Empty;
    }

    private static bool IsNonLoadingImportSheet(string sheetName)
    {
        var normalized = NormalizeHeader(sheetName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return normalized.Contains("account", StringComparison.Ordinal)
            || normalized.Contains("balance", StringComparison.Ordinal)
            || normalized.Contains("sale", StringComparison.Ordinal)
            || normalized.Contains("sales", StringComparison.Ordinal)
            || normalized.Contains(NormalizeHeader("بیلانس"), StringComparison.Ordinal)
            || normalized.Contains(NormalizeHeader("فروش"), StringComparison.Ordinal)
            || normalized.Contains(NormalizeHeader("فروشات"), StringComparison.Ordinal)
            || normalized.Contains(NormalizeHeader("مصارف"), StringComparison.Ordinal)
            || normalized.Contains(NormalizeHeader("محصولی"), StringComparison.Ordinal)
            || normalized.Contains(NormalizeHeader("کرایه"), StringComparison.Ordinal)
            || normalized.Contains(NormalizeHeader("خطآهن"), StringComparison.Ordinal)
            || normalized.Contains(NormalizeHeader("خطاهن"), StringComparison.Ordinal);
    }

    private static bool IsMeaningfulWagonRow(LoadingCreateRowViewModel row)
        => row.LoadedQuantityMt > 0
           && !string.IsNullOrWhiteSpace(row.BillOfLadingNumber)
           && !string.IsNullOrWhiteSpace(row.WagonNumber);

    private static bool IsMeaningfulTruckRow(LoadingCreateRowViewModel row)
        => row.LoadedQuantityMt > 0
           && (!string.IsNullOrWhiteSpace(row.BillOfLadingNumber)
               || !string.IsNullOrWhiteSpace(row.ImportedTransportReference));

    private static string ComposeTransportKey(string billOfLadingNumber, string importedTransportReference)
        => $"{NormalizeLookupText(billOfLadingNumber)}|{NormalizeLookupText(importedTransportReference)}";

    private static string? BuildRouteDescription(string? originLocationName, string? destinationName)
    {
        originLocationName = string.IsNullOrWhiteSpace(originLocationName) ? null : originLocationName.Trim().Trim('"');
        destinationName = string.IsNullOrWhiteSpace(destinationName) ? null : destinationName.Trim().Trim('"');

        return originLocationName switch
        {
            not null when destinationName is not null => $"{originLocationName} -> {destinationName}",
            not null => originLocationName,
            _ => destinationName
        };
    }

    private static string IncrementColumnName(string column)
    {
        var ordinal = GetColumnOrdinal(column) + 1;
        return GetColumnName(ordinal);
    }

    private static int GetColumnOrdinal(string column)
    {
        var ordinal = 0;
        foreach (var ch in column.ToUpperInvariant())
        {
            ordinal = (ordinal * 26) + (ch - 'A' + 1);
        }

        return ordinal;
    }

    private static string GetColumnName(int ordinal)
    {
        if (ordinal <= 0)
        {
            return string.Empty;
        }

        var chars = new Stack<char>();
        while (ordinal > 0)
        {
            ordinal--;
            chars.Push((char)('A' + (ordinal % 26)));
            ordinal /= 26;
        }

        return new string(chars.ToArray());
    }

    private static string GetColumnName(string cellReference)
        => new(cellReference.TakeWhile(char.IsLetter).ToArray());

    private static string NormalizeLookupText(string? value)
        => new((value ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

    private static string NormalizeHeader(string? value)
        => new((value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

    private sealed record WorkbookSheetContext(string Name, IReadOnlyList<Row> Rows);

    private sealed record TruckWorkbookMetadata(
        string? OriginLocationName,
        string? VesselName,
        string? ProductName,
        DateTime? ReportDate);

    private sealed record WagonRailwayCost(
        decimal? ChargeableQuantityMt,
        decimal? RailwayRateUsd,
        decimal RailwayExpenseUsd)
    {
        public decimal? EffectiveRateUsd
            => ChargeableQuantityMt is > 0m
                ? decimal.Round(RailwayExpenseUsd / ChargeableQuantityMt.Value, 4, MidpointRounding.AwayFromZero)
                : RailwayRateUsd;

        public static WagonRailwayCost Create(decimal? chargeableQuantityMt, decimal? railwayRateUsd, decimal railwayExpenseUsd)
            => new(NormalizePositive(chargeableQuantityMt), NormalizePositive(railwayRateUsd), railwayExpenseUsd);

        public WagonRailwayCost Add(decimal? chargeableQuantityMt, decimal? railwayRateUsd, decimal railwayExpenseUsd)
        {
            var normalizedChargeableQuantity = NormalizePositive(chargeableQuantityMt);
            var effectiveChargeableQuantity = ChargeableQuantityMt;
            if (normalizedChargeableQuantity.HasValue
                && (!effectiveChargeableQuantity.HasValue || normalizedChargeableQuantity.Value > effectiveChargeableQuantity.Value))
            {
                effectiveChargeableQuantity = normalizedChargeableQuantity;
            }

            return new WagonRailwayCost(
                effectiveChargeableQuantity,
                NormalizePositive(railwayRateUsd) ?? RailwayRateUsd,
                RailwayExpenseUsd + railwayExpenseUsd);
        }

        private static decimal? NormalizePositive(decimal? value)
            => value.HasValue && value.Value > 0m ? value.Value : null;
    }
}
