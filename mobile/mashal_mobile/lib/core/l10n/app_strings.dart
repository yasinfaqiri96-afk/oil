/// Dari/Persian UI strings. Kept in one place so a later ARB migration is mechanical.
abstract final class AppStrings {
  static const appName = 'مشعل';
  static const appSubtitle = 'سامانهٔ عملیات و مالی';

  // Navigation
  static const navHome = 'خانه';
  static const navOperations = 'عملیات';
  static const navSales = 'فروش';
  static const navFinance = 'مالی';
  static const navMore = 'بیشتر';

  // Authentication
  static const signIn = 'ورود';
  static const signOut = 'خروج';
  static const signOutConfirm = 'از نشست این دستگاه خارج می‌شوید.';
  static const cancel = 'انصراف';
  static const username = 'نام کاربری';
  static const password = 'رمز عبور';
  static const usernameRequired = 'نام کاربری را وارد کنید.';
  static const passwordRequired = 'رمز عبور را وارد کنید.';
  static const showPassword = 'نمایش رمز';
  static const hidePassword = 'پنهان کردن رمز';
  static const apiNotConfigured =
      'آدرس سرور تنظیم نشده است. برنامه باید با MASHAL_API_BASE_URL (HTTPS) ساخته شود.';
  static const sessionExpired = 'نشست شما پایان یافته است. دوباره وارد شوید.';
  static const invalidCredentials = 'نام کاربری یا رمز عبور نادرست است.';

  // Home
  static const businessDate = 'تاریخ کاری';
  static const updatedAt = 'به‌روزرسانی';
  static const offlineStale = 'اتصال برقرار نیست؛ آخرین دادهٔ دریافت‌شده نمایش داده می‌شود.';
  static const refreshFailed = 'به‌روزرسانی ناموفق بود؛ آخرین دادهٔ دریافت‌شده نمایش داده می‌شود.';
  static const kpiInventory = 'موجودی کل';
  static const kpiGoodsInTransit = 'بار در مسیر';
  static const kpiTodaySales = 'فروش امروز';
  static const kpiReceivables = 'طلبات';
  static const kpiCashPosition = 'وضعیت نقدی';
  static const kpiTodayProfit = 'مفاد ناخالص امروز';
  static const lowStock = 'مخزن کم‌موجودی';
  static const liveLoads = 'بار زنده';
  static const delayed = 'تأخیردار';
  static const salesCount = 'فروش';
  static const debtorCustomers = 'مشتری بدهکار';
  static const cashAccounts = 'حساب نقدی';
  static const costedSales = 'فروش با بهای تمام‌شده';
  static const alertsAndPending = 'هشدارها و کارهای معلق';
  static const nothingPending = 'موردی برای رسیدگی نیست.';
  static const activeShipments = 'محموله‌های فعال';
  static const noActiveShipments = 'محمولهٔ فعالی وجود ندارد.';

  // P&L confidence
  static const confidenceNeedsReview = 'نیازمند بررسی';
  static const confidenceEstimated = 'تخمینی';
  static const confidenceLegacy = 'قدیمی';

  // Units
  static const unitMt = 'MT';
  static const unitUsd = 'USD';

  // Operations
  static const loadFlow = 'جریان بار';
  static const loading = 'بارگیری';
  static const loadingSubtitle = 'بارگیری‌ها و وضعیت رسید';
  static const transport = 'حمل';
  static const transportSubtitle = 'موتر، واگن و کشتی در مسیر';
  static const receipt = 'رسید';
  static const receiptSubtitle = 'رسید ورود به موجودی';
  static const stock = 'موجودی';
  static const inventoryTanks = 'موجودی و مخازن';
  static const inventoryTanksSubtitle = 'موجودی آزاد به تفکیک مخزن و ترمینال';
  static const losses = 'ضایعات';
  static const lossesSubtitle = 'کسری بارگیری، مسیر، رسید و مخزن';

  // Sales
  static const salesList = 'لیست فروش';
  static const salesListSubtitle = 'فروش‌ها و جزئیات';
  static const newSale = 'فروش جدید';
  static const newSaleSubtitle = 'فقط برای کاربران دارای دسترسی ثبت';
  static const customerBalance = 'بیلانس مشتری';
  static const customerBalanceSubtitle = 'مانده و آخرین گردش';

  // Finance
  static const finance = 'مالی';
  static const receivables = 'طلبات مشتریان';
  static const receivablesSubtitle = 'مانده به تفکیک طرف‌حساب';
  static const cashPosition = 'وضعیت نقدی';
  static const cashPositionSubtitle = 'مانده حساب‌های نقدی';
  static const payments = 'روزنامچه و پرداخت‌ها';
  static const paymentsSubtitle = 'دریافت‌ها و پرداخت‌ها';

  // More
  static const inbox = 'کارتابل';
  static const approvals = 'تأییدها';
  static const approvalsSubtitle = 'پرداخت، مصرف، ضایعات و استثنای اعتبار';
  static const approvalsBackendMissing =
      'گردش کار تأیید در سیستم فعلی وجود ندارد و هنوز طراحی نشده است. این بخش فعال نیست.';
  static const notifications = 'اعلان‌ها';
  static const notificationsSubtitle = 'هشدارهای محاسبه‌شدهٔ داشبورد';
  static const notificationsBackendMissing =
      'هشدارهای محاسبه‌شده در صفحهٔ خانه نمایش داده می‌شوند. اعلان فوری (Push) هنوز فعال نیست.';
  static const account = 'حساب کاربری';

  // Common
  static const comingSoon = 'به‌زودی';
  static const retry = 'تلاش دوباره';
  static const noAccessSection = 'به این بخش دسترسی ندارید. در صورت نیاز با مدیر سیستم تماس بگیرید.';
  static const genericError = 'خطایی رخ داد. دوباره تلاش کنید.';
  static const networkError = 'اتصال به سرور برقرار نشد. اتصال اینترنت را بررسی کنید.';
  static const accessDenied = 'به این بخش دسترسی ندارید.';
  static const conflictError = 'این رکورد هم‌زمان تغییر کرده است. صفحه را تازه کنید.';
  static const validationError = 'اطلاعات واردشده معتبر نیست.';
  static const businessRuleError = 'این عملیات با قواعد سیستم سازگار نیست.';
  static const notFoundError = 'مورد درخواست‌شده پیدا نشد.';
  static const rateLimitedError = 'درخواست‌ها بیش از حد مجاز است. کمی بعد دوباره تلاش کنید.';
  static const unavailableError = 'سرویس موبایل روی سرور در دسترس نیست.';
  static const serverError = 'خطای سرور رخ داد. لطفاً کمی بعد دوباره تلاش کنید.';
}
