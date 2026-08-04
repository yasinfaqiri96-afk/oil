namespace PTGOilSystem.Web.Helpers;

/// <summary>معنای «مانده قرارداد» — همان چیزی که کاربر باید بخواند، نه علامت ریاضی عدد.</summary>
public enum ContractBalanceMeaning
{
    /// <summary>پرداخت و ارزش قرارداد برابرند.</summary>
    Settled,

    /// <summary>بیش از ارزش قرارداد پرداخت شده است.</summary>
    Overpaid,

    /// <summary>ارزش قرارداد بیشتر از پرداخت است؛ هنوز بدهی داریم.</summary>
    Payable
}

/// <summary>
/// نمایش یکسانِ «مانده قرارداد» در همهٔ صفحات: خلاصهٔ قراردادها، صورت‌حساب، PDF و Excel.
///
/// عدد همیشه بدون علامت نشان داده می‌شود و جهتِ آن در «عنوان» می‌آید. علتش این است که
/// دو صفحه دو کنوانسیون علامت متفاوت داشتند (یکی «ارزش − پرداخت» و دیگری «پرداخت − ارزش»)،
/// و همان یک مانده در دو جا با دو علامت دیده می‌شد.
///
/// ورودی همه‌جا یک چیز است: <c>پرداخت − ارزش قرارداد</c>. مثبت یعنی اضافه‌پرداخت.
/// اینجا هیچ محاسبهٔ مالی انجام نمی‌شود؛ فقط قدرمطلق و برچسب.
/// </summary>
public static class ContractBalanceText
{
    /// <summary>زیر این مقدار، اختلاف فقط گِرد کردن است و «تسویه» خوانده می‌شود.</summary>
    public const decimal SettledTolerance = 0.005m;

    public static ContractBalanceMeaning Meaning(decimal paidMinusContractValue)
        => paidMinusContractValue > SettledTolerance
            ? ContractBalanceMeaning.Overpaid
            : paidMinusContractValue < -SettledTolerance
                ? ContractBalanceMeaning.Payable
                : ContractBalanceMeaning.Settled;

    public static ContractBalanceMeaning? Meaning(decimal? paidMinusContractValue)
        => paidMinusContractValue.HasValue ? Meaning(paidMinusContractValue.Value) : null;

    /// <param name="hasContract">
    /// گروهِ «بدون قرارداد» قراردادی ندارد که اضافه‌پرداختش باشد؛ همان پیش‌پرداخت آزاد است.
    /// </param>
    public static string Title(ContractBalanceMeaning meaning, bool isEnglish = false, bool hasContract = true)
        => meaning switch
        {
            ContractBalanceMeaning.Overpaid when !hasContract => isEnglish ? "Free prepayment" : "پیش‌پرداخت آزاد",
            ContractBalanceMeaning.Overpaid => isEnglish ? "Contract overpayment" : "اضافه‌پرداخت قرارداد",
            ContractBalanceMeaning.Payable => isEnglish ? "Payable to supplier" : "قابل پرداخت به تأمین‌کننده",
            _ => isEnglish ? "Settled" : "تسویه"
        };

    public static string Title(decimal paidMinusContractValue, bool isEnglish = false, bool hasContract = true)
        => Title(Meaning(paidMinusContractValue), isEnglish, hasContract);

    public static string? Title(decimal? paidMinusContractValue, bool isEnglish = false, bool hasContract = true)
        => paidMinusContractValue.HasValue
            ? Title(paidMinusContractValue.Value, isEnglish, hasContract)
            : null;

    /// <summary>عدد بدون علامت. جهت در عنوان می‌آید، نه در علامت رقم.</summary>
    public static decimal Absolute(decimal paidMinusContractValue)
        => Math.Abs(paidMinusContractValue);

    public static decimal? Absolute(decimal? paidMinusContractValue)
        => paidMinusContractValue.HasValue ? Math.Abs(paidMinusContractValue.Value) : null;
}
