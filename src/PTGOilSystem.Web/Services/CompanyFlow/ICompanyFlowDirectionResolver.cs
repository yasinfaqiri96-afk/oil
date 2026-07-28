namespace PTGOilSystem.Web.Services.CompanyFlow;

/// <summary>
/// تعیین‌کنندهٔ مرکزی «رسید یا برد». تنها مرجعِ مجاز برای این تصمیم در کل سیستم.
/// هیچ صفحه، گزارش، PDF یا Excel اجازه ندارد جهت را خودش از Debit/Credit بسازد.
/// </summary>
public interface ICompanyFlowDirectionResolver
{
    CompanyFlowDirection Resolve(in CompanyFlowEvent flowEvent);
}
