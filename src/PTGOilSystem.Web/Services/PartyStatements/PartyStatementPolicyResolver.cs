using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.CompanyFlow;

namespace PTGOilSystem.Web.Services.PartyStatements;

/// <summary>
/// قرارداد نمایشیِ یک‌دستِ صورت‌حساب همهٔ طرف‌حساب‌ها — مطابق فایل‌های کاری شرکت
/// (BNK - Solvex و Petrogas، شیت «بیلانس»):
///
///   ستون رسید = آنچه شرکت از طرف مقابل گرفته (بار، کالا، خدمت، پول برگشتی)،
///   ستون برد  = آنچه شرکت به طرف مقابل داده (پرداخت، تحویل کالا، معاش)،
///   بیلانس     = بیلانس اول دوره + Σبرد − Σرسید،
///   بیلانس مثبت = شرکت بیشتر داده و طلب/پیش‌پرداخت دارد،
///   بیلانس منفی = شرکت بیشتر گرفته و بدهکار است.
///
/// این معنی برای همهٔ طرف‌حساب‌ها یکسان است. پیش‌تر صراف و کارمند علامت وارونه داشتند
/// (بیلانس مثبتشان یعنی «ما بدهکاریم») و مشتری هم جهت فروش/دریافتش برعکس تأمین‌کننده بود؛
/// حالا هر سه از همان لایهٔ مرکزی <see cref="ICompanyFlowDirectionResolver"/> عبور می‌کنند.
///
/// این فقط لایهٔ نمایش است. جدول LedgerEntry و دفتر کل جدید (JournalEntryLine) با
/// قرارداد حسابداری استاندارد خود دست‌نخورده باقی می‌مانند.
/// </summary>
public sealed class PartyStatementPolicyResolver : IPartyStatementPolicyResolver
{
    private static readonly IReadOnlyDictionary<PartyStatementPartyType, PartyStatementPolicy> Policies =
        new Dictionary<PartyStatementPartyType, PartyStatementPolicy>
        {
            [PartyStatementPartyType.Customer] = Build(
                PartyStatementPartyType.Customer,
                CompanyFlowPartyRole.Customer,
                "صورت‌حساب مشتری",
                "Customer Statement",
                "اطلاعات مشتری",
                "Customer Information",
                receiptMeaning: "دریافت پول از مشتری یا برگشت کالا",
                receiptMeaningEn: "Cash received from the customer or goods returned",
                outflowMeaning: "فروش و تحویل کالا به مشتری",
                outflowMeaningEn: "Sale and delivery of goods to the customer",
                accountTypeFa: "حساب مشتری",
                accountTypeEn: "Customer account",
                supportsOperationalColumns: true),
            [PartyStatementPartyType.Supplier] = Build(
                PartyStatementPartyType.Supplier,
                CompanyFlowPartyRole.Supplier,
                "صورت‌حساب تأمین‌کننده",
                "Supplier Statement",
                "اطلاعات تأمین‌کننده",
                "Supplier Information",
                receiptMeaning: "دریافت بارگیری یا کالا و برگشت پول از تأمین‌کننده",
                receiptMeaningEn: "Loadings or goods received and cash returned by the supplier",
                outflowMeaning: "پرداخت به تأمین‌کننده (مستقیم یا از طریق صراف)",
                outflowMeaningEn: "Payment to the supplier (direct or through a sarraf)",
                accountTypeFa: "حساب تأمین‌کننده",
                accountTypeEn: "Supplier account",
                supportsOperationalColumns: true),
            [PartyStatementPartyType.ServiceProvider] = Build(
                PartyStatementPartyType.ServiceProvider,
                CompanyFlowPartyRole.ServiceProvider,
                "صورت‌حساب شرکت خدماتی",
                "Service Provider Statement",
                "اطلاعات شرکت خدماتی",
                "Service Provider Information",
                receiptMeaning: "دریافت خدمت و ایجاد تعهد",
                receiptMeaningEn: "Service received and obligation created",
                outflowMeaning: "پرداخت به شرکت خدماتی",
                outflowMeaningEn: "Payment to the service provider",
                accountTypeFa: "حساب شرکت خدماتی",
                accountTypeEn: "Service provider account"),
            [PartyStatementPartyType.Sarraf] = Build(
                PartyStatementPartyType.Sarraf,
                CompanyFlowPartyRole.Sarraf,
                "صورت‌حساب صراف",
                "Sarraf Statement",
                "اطلاعات صراف",
                "Sarraf Information",
                receiptMeaning: "پرداخت صراف به نمایندگی از شرکت یا دریافت پول از صراف",
                receiptMeaningEn: "Sarraf paying on behalf of the company, or cash received from the sarraf",
                outflowMeaning: "ارسال پول شرکت به صراف و تسویه بدهی",
                outflowMeaningEn: "Company cash sent to the sarraf and debt settlement",
                accountTypeFa: "حساب صراف",
                accountTypeEn: "Sarraf account"),
            [PartyStatementPartyType.Employee] = Build(
                PartyStatementPartyType.Employee,
                CompanyFlowPartyRole.Employee,
                "صورت‌حساب کارمند",
                "Employee Statement",
                "اطلاعات کارمند",
                "Employee Information",
                receiptMeaning: "ثبت معاش و بونس (خدمت دریافت‌شده) یا برگشت وجه از کارمند",
                receiptMeaningEn: "Salary and bonus accrued (service received) or cash returned by the employee",
                outflowMeaning: "پرداخت معاش، مساعده و کسر معاش",
                outflowMeaningEn: "Salary payment, advance and salary deduction",
                accountTypeFa: "حساب کارمند",
                accountTypeEn: "Employee account"),
            [PartyStatementPartyType.Partner] = Build(
                PartyStatementPartyType.Partner,
                CompanyFlowPartyRole.Partner,
                "صورت‌حساب سهم شریک",
                "Partner Share Statement",
                "اطلاعات شریک",
                "Partner Information",
                receiptMeaning: "سهم شریک از ارزشی که شرکت دریافت کرده",
                receiptMeaningEn: "Partner share of the value the company received",
                outflowMeaning: "سهم شریک از ارزشی که شرکت داده",
                outflowMeaningEn: "Partner share of the value the company gave",
                accountTypeFa: "حساب سهم و سرمایه شریک",
                accountTypeEn: "Partner share and capital account",
                supportsOperationalColumns: true),
            [PartyStatementPartyType.Driver] = Build(
                PartyStatementPartyType.Driver,
                CompanyFlowPartyRole.Driver,
                "صورت‌حساب راننده",
                "Driver Statement",
                "اطلاعات راننده",
                "Driver Information",
                receiptMeaning: "دریافت خدمت حمل و ایجاد تعهد",
                receiptMeaningEn: "Transport service received and obligation created",
                outflowMeaning: "پرداخت کرایه و مساعده به راننده",
                outflowMeaningEn: "Freight payment and advance to the driver",
                accountTypeFa: "حساب راننده",
                accountTypeEn: "Driver account"),
            [PartyStatementPartyType.Company] = Build(
                PartyStatementPartyType.Company,
                CompanyFlowPartyRole.Company,
                "صورت‌حساب شرکت",
                "Company Statement",
                "اطلاعات شرکت",
                "Company Information",
                receiptMeaning: "ارزشی که شرکت دریافت کرده",
                receiptMeaningEn: "Value the company received",
                outflowMeaning: "ارزشی که شرکت داده",
                outflowMeaningEn: "Value the company gave",
                accountTypeFa: "حساب جاری شرکت",
                accountTypeEn: "Company current account",
                supportsOperationalColumns: true)
        };

    public PartyStatementPolicy Resolve(PartyStatementPartyType partyType)
        => Policies.TryGetValue(partyType, out var policy)
            ? policy
            : throw new ArgumentOutOfRangeException(nameof(partyType), partyType, "نوع طرف‌حساب پشتیبانی نمی‌شود.");

    private static PartyStatementPolicy Build(
        PartyStatementPartyType type,
        CompanyFlowPartyRole flowRole,
        string titleFa,
        string titleEn,
        string infoTitleFa,
        string infoTitleEn,
        string receiptMeaning,
        string receiptMeaningEn,
        string outflowMeaning,
        string outflowMeaningEn,
        string accountTypeFa,
        string accountTypeEn,
        bool supportsOperationalColumns = false)
        => new()
        {
            PartyType = type,
            FlowRole = flowRole,
            StatementTitleFa = titleFa,
            StatementTitleEn = titleEn,
            PartyInformationTitleFa = infoTitleFa,
            PartyInformationTitleEn = infoTitleEn,
            AccountTypeFa = accountTypeFa,
            AccountTypeEn = accountTypeEn,
            ReceiptMeaningFa = receiptMeaning,
            ReceiptMeaningEn = receiptMeaningEn,
            OutflowMeaningFa = outflowMeaning,
            OutflowMeaningEn = outflowMeaningEn,
            // معنی علامت بیلانس برای همهٔ طرف‌حساب‌ها یکسان است و از لایهٔ مرکزی
            // (CompanyFlowText) در همان زبان نمایش خوانده می‌شود.
            SupportsOperationalColumns = supportsOperationalColumns
        };
}
