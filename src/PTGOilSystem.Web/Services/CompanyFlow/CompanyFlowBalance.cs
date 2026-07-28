namespace PTGOilSystem.Web.Services.CompanyFlow;

/// <summary>
/// یک ردیف گردش پس از تعیین جهت. مبلغ همیشه مثبت است؛ علامت از جهت می‌آید.
/// </summary>
public readonly record struct CompanyFlowAmount(CompanyFlowDirection Direction, decimal Amount)
{
    public decimal Receipt => Direction == CompanyFlowDirection.Receipt ? Amount : 0m;
    public decimal Outflow => Direction == CompanyFlowDirection.Outflow ? Amount : 0m;
}

/// <summary>
/// نتیجهٔ محاسبهٔ بیلانس. تنها شکل مجاز برای کارت‌های آماری و جمع‌های صورت‌حساب.
/// </summary>
public readonly record struct CompanyFlowBalance
{
    public required CompanyFlowAccountKind AccountKind { get; init; }
    public required decimal OpeningBalance { get; init; }
    public required decimal TotalReceipt { get; init; }
    public required decimal TotalOutflow { get; init; }
    public required decimal ClosingBalance { get; init; }

    public decimal ClosingBalanceAbsolute => Math.Abs(ClosingBalance);

    /// <summary>اثر خالص دوره — همان چیزی که به مانده اول دوره اضافه شده است.</summary>
    public decimal NetMovement => ClosingBalance - OpeningBalance;
}
