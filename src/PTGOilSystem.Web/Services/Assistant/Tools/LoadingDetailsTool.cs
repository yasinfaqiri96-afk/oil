using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Assistant.Tools;

/// <summary>
/// پروندهٔ کامل یک بارگیری: قرارداد، محصول، طرف حساب، مقدار، رسیدها، ضایعات،
/// هزینه‌ها، گمرک و مشکلات باز.
///
/// هر عدد اینجا در کد محاسبه می‌شود و مدل فقط همان را توضیح می‌دهد. قاعده‌ها
/// عیناً همان قاعده‌های صفحهٔ «جزئیات بارگیری» (<c>LoadingController.Details</c>)
/// هستند تا عدد دستیار با عدد صفحه یکی باشد:
///   • رسید لغوشده در هیچ جمعی شمرده نمی‌شود.
///   • باقی‌ماندهٔ دریافت = بارگیری‌شده − دریافت‌شده − کسری رسید، و هرگز منفی نیست.
///   • جمع هزینه از سطرهای هزینهٔ بارگیری می‌آید و در نبود سطر، از فیلدهای قدیمی.
/// هیچ نوشتنی انجام نمی‌شود.
/// </summary>
public sealed class LoadingDetailsTool : IAssistantTool
{
    private readonly ApplicationDbContext _db;

    public LoadingDetailsTool(ApplicationDbContext db) => _db = db;

    public string Name => "get_loading_details";

    public string Description =>
        "پروندهٔ کامل یک بارگیری با شناسهٔ آن: قرارداد، محصول، تأمین‌کننده/مشتری، مقدار بارگیری‌شده، "
        + "رسیدهای دریافت، مقدار باقی‌مانده برای دریافت، ضایعات، هزینهٔ حمل و انبار، اظهارنامهٔ گمرکی، "
        + "تاریخ آخرین فعالیت و مشکلات باز. برای «همین بارگیری را بررسی کن» با شناسهٔ همان صفحه صدا زده شود.";

    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "loading_id": {
              "type": ["integer", "string"],
              "description": "شناسه عددی بارگیری. اگر کاربر گفت «همین بارگیری»، شناسهٔ رکورد باز در صفحه را بفرست. شناسه را از خودت نساز."
            }
          },
          "required": ["loading_id"]
        }
        """;

    /// <summary>بارگیری در بخش «عملیات» دیده می‌شود.</summary>
    public string RequiredController => "Loading";

    public async Task<string> ExecuteAsync(JsonElement arguments, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var loadingId = AssistantToolArgs.GetInt(arguments, "loading_id");
        if (loadingId is not > 0)
        {
            return "شناسه بارگیری داده نشد. اگر کاربر روی صفحهٔ یک بارگیری است، شناسهٔ همان صفحه را بفرست.";
        }

        var loading = await _db.LoadingRegisters.AsNoTracking()
            .Where(register => register.Id == loadingId.Value)
            .Select(register => new
            {
                register.Id,
                register.LoadingDate,
                register.LoadedQuantityMt,
                register.ContractId,
                ContractNumber = register.Contract != null ? register.Contract.ContractNumber : null,
                ContractName = register.Contract != null ? register.Contract.ContractName : null,
                ContractQuantityMt = register.Contract != null ? register.Contract.QuantityMt : (decimal?)null,
                SupplierId = register.Contract != null ? register.Contract.SupplierId : null,
                SupplierName = register.Contract != null && register.Contract.Supplier != null ? register.Contract.Supplier.Name : null,
                CustomerId = register.Contract != null ? register.Contract.CustomerId : null,
                CustomerName = register.Contract != null && register.Contract.Customer != null ? register.Contract.Customer.Name : null,
                ProductName = register.Product != null ? register.Product.Name : null,
                OriginName = register.OriginLocation != null ? register.OriginLocation.Name : null,
                register.DestinationName,
                register.BillOfLadingNumber,
                register.WagonNumber,
                register.TransportType,
                register.LoadingPriceUsd,
                register.TransportExpenseUsd,
                register.WarehouseExpenseUsd,
                register.OtherExpenseUsd,
                register.RailwayExpenseUsd,
                register.SettlementCurrencyCode,
                register.Notes,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (loading is null)
        {
            return $"بارگیری با شناسه {loadingId} یافت نشد. این یعنی شناسه اشتباه است، نه اینکه بارگیری خالی باشد.";
        }

        var receipts = await _db.LoadingReceipts.AsNoTracking()
            .Where(receipt => receipt.LoadingRegisterId == loading.Id)
            .OrderBy(receipt => receipt.ReceiptDate)
            .Select(receipt => new
            {
                receipt.Id,
                receipt.ReceiptDate,
                receipt.ReceivedQuantityMt,
                receipt.IsCancelled,
                TerminalName = receipt.Terminal != null ? receipt.Terminal.Name : null,
            })
            .ToListAsync(cancellationToken);

        // رسید لغوشده دیده می‌شود ولی در هیچ جمعی نمی‌آید — همان قاعدهٔ صفحهٔ بارگیری.
        var active = receipts.Where(receipt => !receipt.IsCancelled).ToList();
        var receivedMt = active.Sum(receipt => receipt.ReceivedQuantityMt);

        var losses = await _db.LossEvents.AsNoTracking()
            .Where(loss => loss.LoadingRegisterId == loading.Id && !loss.IsCancelled)
            .Select(loss => new
            {
                loss.Stage,
                loss.EventDate,
                loss.DifferenceQuantityMt,
                loss.ChargeableLossMt,
                loss.ResponsiblePartyName,
            })
            .ToListAsync(cancellationToken);

        var receiptShortageMt = losses
            .Where(loss => loss.Stage == LossEventStage.ReceiptShortage)
            .Sum(loss => loss.DifferenceQuantityMt > 0m ? loss.DifferenceQuantityMt : Math.Max(loss.ChargeableLossMt, 0m));

        var remainingToReceiveMt = Math.Max(loading.LoadedQuantityMt - receivedMt - receiptShortageMt, 0m);
        var chargeableLossMt = losses.Sum(loss => loss.ChargeableLossMt);

        var expenseLinesTotalUsd = await _db.LoadingExpenseLines.AsNoTracking()
            .Where(line => line.LoadingRegisterId == loading.Id)
            .SumAsync(line => (decimal?)line.AmountUsd, cancellationToken) ?? 0m;

        // در نبود سطر هزینه، همان فیلدهای قدیمیِ خود بارگیری جمع می‌شوند.
        var expenseTotalUsd = expenseLinesTotalUsd > 0m
            ? expenseLinesTotalUsd
            : (loading.TransportExpenseUsd ?? 0m)
              + (loading.WarehouseExpenseUsd ?? 0m)
              + (loading.OtherExpenseUsd ?? 0m)
              + (loading.RailwayExpenseUsd ?? 0m);

        var customs = await _db.CustomsDeclarations.AsNoTracking()
            .Where(declaration => declaration.LoadingRegisterId == loading.Id)
            .Select(declaration => new
            {
                declaration.DeclarationDate,
                declaration.DeclarationReference,
                TotalUsd = declaration.Items.Sum(item => item.AmountUsd ?? 0m),
                TotalAfn = declaration.Items.Sum(item => item.AmountAfn),
            })
            .ToListAsync(cancellationToken);

        var builder = new StringBuilder();
        builder.Append("بارگیری شناسه=").Append(loading.Id)
            .Append(" | تاریخ=").Append(Date(loading.LoadingDate))
            .Append(" | محصول=").Append(loading.ProductName ?? "-")
            .Append(" | حمل=").Append(loading.TransportType)
            .AppendLine();

        builder.Append("قرارداد شناسه=").Append(loading.ContractId)
            .Append(" | شماره=").Append(loading.ContractNumber ?? "-")
            .Append(" | نام=").Append(loading.ContractName ?? "-")
            .Append(" | مقدار قراردادی=").Append(Qty(loading.ContractQuantityMt))
            .AppendLine();

        if (loading.SupplierId is > 0)
        {
            builder.Append("تأمین‌کننده شناسه=").Append(loading.SupplierId).Append(" | نام=").Append(loading.SupplierName ?? "-").AppendLine();
        }

        if (loading.CustomerId is > 0)
        {
            builder.Append("مشتری شناسه=").Append(loading.CustomerId).Append(" | نام=").Append(loading.CustomerName ?? "-").AppendLine();
        }

        builder.Append("مبدأ=").Append(loading.OriginName ?? "-")
            .Append(" | مقصد=").Append(loading.DestinationName ?? "-")
            .Append(" | بارنامه=").Append(loading.BillOfLadingNumber ?? "-")
            .Append(" | واگن=").Append(loading.WagonNumber ?? "-")
            .AppendLine();

        builder.Append("مقدار بارگیری‌شده=").Append(Qty(loading.LoadedQuantityMt)).Append(" MT")
            .Append(" | دریافت‌شده=").Append(Qty(receivedMt)).Append(" MT")
            .Append(" | کسری رسید=").Append(Qty(receiptShortageMt)).Append(" MT")
            .Append(" | باقی‌مانده برای دریافت=").Append(Qty(remainingToReceiveMt)).Append(" MT")
            .AppendLine();

        builder.Append("قیمت بارگیری=").Append(Money(loading.LoadingPriceUsd)).Append(" USD/MT")
            .Append(" | جمع هزینه‌های بارگیری=").Append(Money(expenseTotalUsd)).Append(" USD")
            .Append(" | ارز تسویه=").Append(loading.SettlementCurrencyCode)
            .AppendLine();

        builder.Append("رسیدها (").Append(active.Count).Append(" فعال از ").Append(receipts.Count).Append("): ");
        if (receipts.Count == 0)
        {
            builder.AppendLine("هیچ رسیدی ثبت نشده است.");
        }
        else
        {
            builder.AppendLine();
            foreach (var receipt in receipts.Take(20))
            {
                builder.Append("  - تاریخ=").Append(Date(receipt.ReceiptDate))
                    .Append(" | ترمینال=").Append(receipt.TerminalName ?? "-")
                    .Append(" | مقدار=").Append(Qty(receipt.ReceivedQuantityMt)).Append(" MT")
                    .Append(receipt.IsCancelled ? " | لغوشده (در جمع‌ها شمرده نشده)" : string.Empty)
                    .AppendLine();
            }
        }

        builder.Append("ضایعات: ");
        if (losses.Count == 0)
        {
            builder.AppendLine("هیچ ضایعاتی ثبت نشده است.");
        }
        else
        {
            builder.Append("جمع قابل‌مطالبه=").Append(Qty(chargeableLossMt)).AppendLine(" MT");
            foreach (var loss in losses.Take(15))
            {
                builder.Append("  - مرحله=").Append(loss.Stage)
                    .Append(" | تاریخ=").Append(Date(loss.EventDate))
                    .Append(" | اختلاف=").Append(Qty(loss.DifferenceQuantityMt)).Append(" MT")
                    .Append(" | قابل‌مطالبه=").Append(Qty(loss.ChargeableLossMt)).Append(" MT")
                    .Append(" | مسئول=").Append(loss.ResponsiblePartyName ?? "-")
                    .AppendLine();
            }
        }

        builder.Append("گمرک: ");
        if (customs.Count == 0)
        {
            builder.AppendLine("هیچ اظهارنامه‌ای ثبت نشده است.");
        }
        else
        {
            builder.Append(customs.Count).Append(" اظهارنامه | جمع=")
                .Append(Money(customs.Sum(item => item.TotalUsd))).Append(" USD / ")
                .Append(Money(customs.Sum(item => item.TotalAfn))).AppendLine(" AFN");
        }

        var lastActivity = new[]
            {
                loading.LoadingDate,
                active.Count > 0 ? active.Max(receipt => receipt.ReceiptDate) : loading.LoadingDate,
                losses.Count > 0 ? losses.Max(loss => loss.EventDate) : loading.LoadingDate,
            }
            .Max();

        builder.Append("آخرین فعالیت=").Append(Date(lastActivity)).AppendLine();

        // مشکلات باز — همان چیزهایی که در صفحه هم به چشم می‌آید. تصمیم و تحلیل با
        // مدل است، ولی تشخیص خودِ مشکل اینجا و روی داده انجام می‌شود.
        var issues = new List<string>();
        if (remainingToReceiveMt > 0m)
        {
            issues.Add($"{Qty(remainingToReceiveMt)} MT هنوز رسید نخورده است");
        }

        if (chargeableLossMt > 0m)
        {
            issues.Add($"{Qty(chargeableLossMt)} MT ضایعات قابل‌مطالبه ثبت شده است");
        }

        if (loading.LoadingPriceUsd is null or 0m)
        {
            issues.Add("قیمت بارگیری ثبت نشده است");
        }

        if (customs.Count == 0)
        {
            issues.Add("اظهارنامهٔ گمرکی ثبت نشده است");
        }

        if (receipts.Any(receipt => receipt.IsCancelled))
        {
            issues.Add("این بارگیری رسید لغوشده دارد");
        }

        builder.Append("مشکلات باز: ").AppendLine(issues.Count == 0 ? "موردی دیده نشد." : string.Join("؛ ", issues));

        if (!string.IsNullOrWhiteSpace(loading.Notes))
        {
            builder.Append("یادداشت: ").AppendLine(loading.Notes);
        }

        return builder.ToString();
    }

    private static string Date(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Qty(decimal? value) => (value ?? 0m).ToString("N3", CultureInfo.InvariantCulture);

    private static string Money(decimal? value) => value.HasValue ? value.Value.ToString("N2", CultureInfo.InvariantCulture) : "-";
}
