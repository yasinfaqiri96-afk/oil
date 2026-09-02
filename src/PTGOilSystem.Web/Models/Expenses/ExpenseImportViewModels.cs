using Microsoft.AspNetCore.Http;

namespace PTGOilSystem.Web.Models.Expenses;

/// <summary>
/// Bulk import of expense rows from an Excel file. Two-step flow:
/// 1) upload + preview (parse + validation, no save),
/// 2) confirm (save validated rows as ExpenseTransaction + LedgerEntry).
/// Logic mirrors the single-expense create path; no parallel financial logic.
/// </summary>
public class ExpenseImportViewModel
{
    public IFormFile? ImportFile { get; set; }

    public List<ExpenseImportRowViewModel> Rows { get; set; } = new();

    public string? ReturnUrl { get; set; }

    public int ValidCount { get; set; }
    public int ErrorCount { get; set; }

    /// <summary>PTG-P2-02 — سطرهایی که قبلاً از همین فایل (یا فایلی مشابه) ثبت شده‌اند.</summary>
    public int DuplicateCount { get; set; }

    /// <summary>
    /// PTG-P2-02 — انتخابِ صریحِ کاربر: «فقط سطرهای سالم را ثبت کن».
    ///
    /// پیش‌فرض <c>false</c> است، یعنی رفتار قبلی و امن‌ترین حالت: یا همه ثبت می‌شوند یا
    /// هیچ‌کدام. این گزینه هرگز خودکار روشن نمی‌شود و سطرهای ردشده هم بی‌صدا کنار
    /// نمی‌روند: در پیش‌نمایش با دلیلشان دیده می‌شوند و گزارششان قابل دانلود است.
    /// </summary>
    public bool ImportValidRowsOnly { get; set; }

    public bool HasRows => Rows.Count > 0;
    public bool HasErrors => ErrorCount > 0;

    /// <summary>سطرهایی که واقعاً ثبت می‌شوند: سالم، و قبلاً ثبت‌نشده.</summary>
    public int ImportableCount => Rows.Count(r => r.IsImportable);

    /// <summary>سطرهایی که ثبت نمی‌شوند — چه به‌خاطر خطا، چه به‌خاطر تکراری‌بودن.</summary>
    public int SkippedCount => Rows.Count - ImportableCount;

    public bool CanConfirm => ImportValidRowsOnly
        ? ImportableCount > 0
        : HasRows && ErrorCount == 0 && ImportableCount == Rows.Count;
}

/// <summary>
/// One imported expense row. Raw text fields are carried between the preview
/// and confirm steps via hidden inputs so the user does not re-upload the file.
/// All parsing + validation happens server-side in a single shared routine.
/// </summary>
public class ExpenseImportRowViewModel
{
    public int ExcelRowNumber { get; set; }

    // Raw (canonical) values carried in hidden inputs between preview and confirm.
    public string? ExpenseDateText { get; set; }
    public string? ExpenseTypeName { get; set; }
    public string? AmountText { get; set; }
    public string? Currency { get; set; }
    public string? RatePerUsdText { get; set; }
    public string? ContractNumber { get; set; }
    public string? Description { get; set; }

    // Parsed / resolved values (recomputed on every validation pass; not trusted from the client).
    public DateTime? ExpenseDate { get; set; }
    public decimal? Amount { get; set; }
    public decimal? RatePerUsd { get; set; }
    public decimal? AmountUsd { get; set; }

    public int? ResolvedExpenseTypeId { get; set; }
    public string? ResolvedExpenseTypeName { get; set; }
    public int? ResolvedContractId { get; set; }

    public List<string> Errors { get; set; } = new();

    /// <summary>PTG-P2-02 — هویتِ canonical سطر؛ همان چیزی که تکراری‌بودن با آن سنجیده می‌شود.</summary>
    public string? ImportUniqueKey { get; set; }

    /// <summary>این سطر با همین هویت قبلاً ثبت شده است.</summary>
    public bool IsDuplicate { get; set; }

    /// <summary>چرا این سطر ثبت نمی‌شود (خطا یا تکرار). خالی یعنی ثبت می‌شود.</summary>
    public string? SkipReason => Errors.Count > 0
        ? string.Join(" | ", Errors)
        : IsDuplicate
            ? "این سطر قبلاً از فایل اکسل ثبت شده است."
            : null;

    public bool IsValid => Errors.Count == 0;

    /// <summary>سالم و تکراری‌نبودن — تنها شرطِ ثبت شدن.</summary>
    public bool IsImportable => Errors.Count == 0 && !IsDuplicate;
}
