using System.Globalization;
using PTGOilSystem.Web.Services.CompanyFlow;

namespace PTGOilSystem.Web.Models.PartyStatements;

public enum PartyStatementRowKind
{
    /// <summary>حرکت پول یا اصلاح مالی (دریافت، پرداخت، تسویه، تفاوت نرخ و ...).</summary>
    Money = 0,

    /// <summary>معاملهٔ کالا یا خدمت (فروش، بارگیری، مصرف، معاش).</summary>
    Trade = 1
}

/// <summary>
/// ستون‌های تجارتیِ صورت‌حساب: «مبلغ معامله»، «دریافت» و «پرداخت». هیچ مبلغ یا بیلانسی اینجا
/// ساخته نمی‌شود؛ فقط همان اثر سطر (<see cref="PartyStatementRow.SignedAmount"/> = برد − رسید)
/// در ستونی نشانده می‌شود که برای تاجر قابل فهم است. سطر برگشت در همان ستونِ سند اصلی
/// با علامت منفی می‌نشیند تا اصل و برگشت کنار هم صفر شوند.
///
/// رابطهٔ همیشه برقرار (با s = +1 وقتی معامله «برد» است و −1 وقتی «رسید»):
///   بیلانس = اول دوره + s × Σمعامله + Σپرداخت − Σدریافت
/// که دقیقاً همان «اول دوره + Σبرد − Σرسید» است.
/// </summary>
public static class PartyStatementPresentation
{
    private static readonly HashSet<string> TradeSourceTypes = new(StringComparer.Ordinal)
    {
        CompanyFlowSourceTypes.Sale,
        CompanyFlowSourceTypes.Loading,
        CompanyFlowSourceTypes.Expense,
        CompanyFlowSourceTypes.ShortageCharge,
        // سطر فشردهٔ بارگیری/فروشِ یک قرارداد (SupplierContractStatementBuilder).
        "ContractOperations",
        "AssetRent",
        "AssetRentTransaction",
        CompanyFlowSourceTypes.SalaryAccrual,
        CompanyFlowSourceTypes.Bonus,
        CompanyFlowSourceTypes.SalaryDeduction
    };

    public static PartyStatementRowKind KindOf(string? sourceType)
        => sourceType is not null && TradeSourceTypes.Contains(sourceType)
            ? PartyStatementRowKind.Trade
            : PartyStatementRowKind.Money;

    public static PartyStatementColumnLabels ColumnsFor(PartyStatementPartyType partyType) => partyType switch
    {
        PartyStatementPartyType.Customer => new(CompanyFlowDirection.Outflow, "مبلغ معامله", "Trade amount", "مجموع فروش", "Total sales"),
        PartyStatementPartyType.Supplier => new(CompanyFlowDirection.Receipt, "مبلغ معامله", "Trade amount", "مجموع خرید", "Total purchases"),
        PartyStatementPartyType.ServiceProvider => new(CompanyFlowDirection.Receipt, "مبلغ خدمات", "Services", "مجموع خدمات", "Total services"),
        PartyStatementPartyType.Driver => new(CompanyFlowDirection.Receipt, "مبلغ کرایه", "Freight", "مجموع کرایه", "Total freight"),
        PartyStatementPartyType.Employee => new(CompanyFlowDirection.Receipt, "معاش و بونس", "Salary & bonus", "مجموع معاش و بونس", "Total salary & bonus"),
        PartyStatementPartyType.Sarraf => new(null, null, null, null, null, "دریافت از صراف", "From sarraf", "ارسال به صراف", "To sarraf"),
        PartyStatementPartyType.Partner => new(null, null, null, null, null, "به شریک رسیده", "To partner", "شریک آورده", "From partner"),
        // حساب جواز هم بارگیری (رسید) و هم فروش (برد) دارد؛ ستون «معامله» یک‌جهته نمی‌شود.
        _ => new(null, null, null, null, null)
    };

    public static string PartyTypeLabel(PartyStatementPartyType partyType, bool isEnglish = false) => partyType switch
    {
        PartyStatementPartyType.Customer => isEnglish ? "Customer" : "مشتری",
        PartyStatementPartyType.Supplier => isEnglish ? "Supplier" : "تأمین‌کننده",
        PartyStatementPartyType.ServiceProvider => isEnglish ? "Service provider" : "شرکت خدماتی",
        PartyStatementPartyType.Sarraf => isEnglish ? "Sarraf" : "صراف",
        PartyStatementPartyType.Employee => isEnglish ? "Employee" : "کارمند",
        PartyStatementPartyType.Partner => isEnglish ? "Partner" : "شریک",
        PartyStatementPartyType.Driver => isEnglish ? "Driver" : "راننده",
        PartyStatementPartyType.Company => isEnglish ? "Company" : "شرکت",
        _ => isEnglish ? "Party" : "طرف‌حساب"
    };

    /// <summary>مبلغ هر ستون تجارتی برای یک سطر (USD یا ارزش روبلی). null یعنی آن ستون خالی است.</summary>
    public static PartyStatementAmounts AmountsFor(
        PartyStatementRow row,
        PartyStatementPartyType partyType,
        bool isRub = false)
    {
        if (row.IsOpeningBalance)
        {
            return default;
        }

        decimal effect;
        if (isRub)
        {
            if (!row.SignedAmountRub.HasValue)
            {
                return default;
            }
            effect = row.SignedAmountRub.Value;
        }
        else
        {
            if (!row.ReceiptBase.HasValue && !row.OutflowBase.HasValue)
            {
                return default;
            }
            effect = row.SignedAmount;
        }

        var tradeDirection = ColumnsFor(partyType).TradeDirection;
        if (row.Kind == PartyStatementRowKind.Trade && tradeDirection.HasValue)
        {
            // معامله در جهتِ معمولِ خودش مثبت است (فروش به مشتری، بارگیری از تأمین‌کننده).
            var trade = tradeDirection == CompanyFlowDirection.Outflow ? effect : -effect;
            return new PartyStatementAmounts(trade, null, null);
        }

        if (effect == 0m)
        {
            return row.FlowDirection == CompanyFlowDirection.Receipt
                ? new PartyStatementAmounts(null, 0m, null)
                : new PartyStatementAmounts(null, null, 0m);
        }

        // دریافت = اثر منفی روی بیلانس، پرداخت = اثر مثبت؛ برگشتِ هر کدام در همان ستون با علامت منفی.
        var isReceivedColumn = (effect < 0m) != row.IsReversalRow;
        return isReceivedColumn
            ? new PartyStatementAmounts(null, -effect, null)
            : new PartyStatementAmounts(null, null, effect);
    }

    /// <summary>کمیتِ «۲۰.۰۰۰ MT»؛ صفرهای اعشار برای مقایسهٔ آسان ثابت می‌مانند.</summary>
    public static string Quantity(decimal value, string? unit, bool isEnglish = false)
        => $"{value.ToString("#,##0.000", CultureInfo.InvariantCulture)} {UnitLabel(unit, isEnglish)}";

    /// <summary>واحد مقدار به زبان مشتری: MT در دری «تن» خوانده می‌شود.</summary>
    public static string UnitLabel(string? unit, bool isEnglish = false)
        => string.IsNullOrWhiteSpace(unit) || string.Equals(unit, "MT", StringComparison.OrdinalIgnoreCase)
            ? (isEnglish ? "MT" : "تن")
            : unit;

    /// <summary>نرخ یا مبلغ با دست‌کم دو رقم اعشار (تا چهار رقم وقتی خود نرخ دارد).</summary>
    public static string Rate(decimal value)
        => value.ToString("#,##0.00##", CultureInfo.InvariantCulture);

    public static string Money(decimal value)
        => value.ToString("#,##0.00", CultureInfo.InvariantCulture);

    public const string DetailSeparator = " • ";

    /// <summary>جداکنندهٔ مرجع سند و موتر در خط سوم شرح.</summary>
    public const string DocumentVehicleSeparator = " · ";

    /// <summary>
    /// خط دوم شرح در سند رسمی (مطابق نمونهٔ مرجع، فقط یک خط کوتاه):
    /// معامله → «20.000 تن × 728.00 USD/تن»؛ حرکت پول به ارز دیگر → «49,000.00 AFN»؛ بقیه هیچ.
    /// موتر، یادداشت و توضیح سند در سند رسمی نمی‌آیند تا ستون شرح شلوغ نشود.
    /// </summary>
    public static string? DescriptionSecondLine(PartyStatementRow row, string statementCurrency, bool isEnglish = false)
    {
        if (row.IsOpeningBalance)
        {
            return null;
        }
        if (row.TradeQuantity.HasValue)
        {
            return QuantityTimesRate(row, isEnglish);
        }

        return row.OriginalAmount.HasValue
            && !string.Equals(row.OriginalCurrency, statementCurrency, StringComparison.OrdinalIgnoreCase)
                ? $"{Money(Math.Abs(row.OriginalAmount.Value))} {row.OriginalCurrency}"
                : null;
    }

    /// <summary>«20.000 تن × 728.00 USD/تن»؛ نرخ به ارز خودِ سند و فقط وقتی تأییدشده است.</summary>
    public static string? QuantityTimesRate(PartyStatementRow row, bool isEnglish = false)
    {
        if (row.IsOpeningBalance || !row.TradeQuantity.HasValue)
        {
            return null;
        }

        var text = Quantity(row.TradeQuantity.Value, row.TradeQuantityUnit, isEnglish);
        return row.TradeUnitPrice.HasValue
            ? $"{text} × {Rate(row.TradeUnitPrice.Value)} {row.TradeUnitPriceCurrency}/{UnitLabel(row.TradeQuantityUnit, isEnglish)}"
            : text;
    }

    /// <summary>
    /// برای نمایش در متن راست‌به‌چپ: هر بخشِ جزئیات که با عدد یا حرف لاتین شروع می‌شود
    /// («20.000 MT × 728.00 USD») در یک LTR isolate (U+2066…U+2069) پیچیده می‌شود تا
    /// الگوریتم bidi ترتیب عدد و واحد را جابه‌جا نکند. دادهٔ ذخیره‌شده تغییر نمی‌کند.
    /// </summary>
    public static string IsolateLeftToRightSegments(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var segments = text.Split(DetailSeparator);
        for (var i = 0; i < segments.Length; i++)
        {
            if (StartsLeftToRight(segments[i]))
            {
                segments[i] = "⁦" + MarkRightToLeftWords(segments[i]) + "⁩";
            }
        }
        return string.Join(DetailSeparator, segments);
    }

    // داخل بخش چپ‌به‌راست، بعد از هر واژهٔ فارسی (مثل «تن») یک LRM می‌آید تا عددِ بعدی
    // به آن واژه نچسبد و با آن وارونه نشود («20.000 تن × 728.00 USD/تن»).
    private static string MarkRightToLeftWords(string segment)
    {
        var builder = new System.Text.StringBuilder(segment.Length + 4);
        for (var i = 0; i < segment.Length; i++)
        {
            builder.Append(segment[i]);
            if (IsRightToLeftLetter(segment[i]) && (i + 1 == segment.Length || !IsRightToLeftLetter(segment[i + 1])))
            {
                builder.Append('‎');
            }
        }
        return builder.ToString();
    }

    private static bool IsRightToLeftLetter(char ch)
        => ch is >= '؀' and <= 'ۿ' or >= 'ﭐ' and <= '﻿';

    private static bool StartsLeftToRight(string segment)
    {
        foreach (var ch in segment)
        {
            if (char.IsDigit(ch) || ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                return true;
            }
            if (char.IsLetter(ch))
            {
                return false;
            }
        }
        return false;
    }
}

public readonly record struct PartyStatementAmounts(decimal? Trade, decimal? Received, decimal? Paid);

public sealed record PartyStatementColumnLabels(
    CompanyFlowDirection? TradeDirection,
    string? TradeFa,
    string? TradeEn,
    string? TradeTotalFa,
    string? TradeTotalEn,
    string ReceivedFa = "دریافت",
    string ReceivedEn = "Received",
    string PaidFa = "پرداخت",
    string PaidEn = "Paid")
{
    public bool HasTradeColumn => TradeDirection.HasValue;

    public string? Trade(bool isEnglish) => isEnglish ? TradeEn : TradeFa;
    public string? TradeTotal(bool isEnglish) => isEnglish ? TradeTotalEn : TradeTotalFa;
    public string Received(bool isEnglish) => isEnglish ? ReceivedEn : ReceivedFa;
    public string Paid(bool isEnglish) => isEnglish ? PaidEn : PaidFa;
    public string ReceivedTotal(bool isEnglish) => isEnglish ? $"Total {ReceivedEn.ToLowerInvariant()}" : $"مجموع {ReceivedFa}";
    public string PaidTotal(bool isEnglish) => isEnglish ? $"Total {PaidEn.ToLowerInvariant()}" : $"مجموع {PaidFa}";
}
