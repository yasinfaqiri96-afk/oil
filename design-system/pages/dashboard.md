# Dashboard

این فایل فقط قواعد اختصاصی Dashboard را override می‌کند؛ سایر قواعد از `../MASTER.md` می‌آیند.

## ساختار

1. خلاصه هشدار/وضعیت بحرانی، فقط اگر واقعی باشد
2. یک ردیف 4 KPI اصلی
3. حداکثر دو visualization تصمیم‌ساز
4. یک خلاصه عملیاتی یا activity

مرجع فعلی: `Views/Home/Index.cshtml` و `wwwroot/css/ptg/12-dashboard.css`.

## Header و Actions

- عنوان dashboard می‌تواند از chrome صفحه کم‌رنگ‌تر باشد؛ primary action معمولاً لازم نیست.
- انتخاب بازه/سال مالی در control موجود shell یا toolbar انجام شود.
- actionهای module-specific به hub مربوط هدایت شوند؛ shortcut تزئینی نساز.

## KPI

- تعداد مطلوب: 4؛ حداکثر 5.
- فروش، موجودی، حمل جاری و قرارداد فعال نمونه‌های معتبرند.
- مقدار بزرگ، label کوتاه، unit روشن و مقایسه فقط با داده واقعی.
- کارت KPI نباید به dashboard کوچک مستقل تبدیل شود.

## Grid و چگالی

- desktop: چهار KPI در یک ردیف؛ visualizationها دو ستون.
- tablet: دو ستون؛ mobile: یک ستون.
- چگالی متوسط؛ فضای سفید کنترل‌شده و بدون hero.
- پنل منفرد باقی‌مانده تمام‌عرض شود.

## Chart

- نمودار برای trend/composition واقعی؛ نه تزئین.
- palette از `--ptg-chart-*`.
- legend و label خوانا؛ tooltip مکمل است، نه تنها منبع مقدار.
- برای داده خالی از empty state موجود استفاده کن.
- animation کوتاه و reduced-motion-safe.

## Reuse

- `StatCard`
- `.ak-stat-grid`
- `.dash-analytics`, `.dash-insights`
- `_EmptyState`
- `dashboard.js`

## Anti-pattern

- بیش از دو نمودار بالای fold
- KPI جعلی یا تکرار همان عدد در چند کارت
- gradient/glow/bento نمایشی
- actionهای فراوان در هر کارت
- رنگ متفاوت برای هر metric بدون semantics

