---
name: ptg-stock-inventory-guard
description: Use whenever PTG work touches StockService, InventoryMovement, receipts, dispatch, sales, DirectSale, DirectDispatchFromReceipt, allocations, tank stock, shortage, loss, reversal, or quantity posting.
---

# PTG Stock and Inventory Guard

قبل از تغییر، مسیر واقعی مقدار را از Controller تا Service، Entity و تست‌ها دنبال کن.

- منبع رسمی موجودی فقط `StockService` و `InventoryMovement` است.
- Receipt با `ToInventory` باید دقیقاً یک حرکت ورودی معتبر بسازد.
- `DirectSale` نباید حرکت موجودی جعلی بسازد.
- `DirectDispatchFromReceipt` باید trace/allocation-based بماند و `StockService` را صدا نزند.
- جلوی posting تکراری، موجودی منفی و mismatch واحد/محصول/شرکت/ترمینال/تانک را بگیر.
- مقدار، وزن، نرخ و تبدیل واحد را `decimal/numeric` نگه دار.
- اگر پول یا حسابداری دخیل است، هم‌زمان `ptg-finance-ledger-guard` را اجرا کن.
- تغییر منطق فقط با اجازه واضح کاربر؛ ابتدا تست‌های هدفمند همان جریان را اجرا کن.

