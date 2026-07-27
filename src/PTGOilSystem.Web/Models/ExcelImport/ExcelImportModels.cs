namespace PTGOilSystem.Web.Models.ExcelImport;

public enum ExcelImportJobStage
{
    Upload = 1,
    Structure = 2,
    Validation = 3,
    Registration = 4,
    Completed = 5,
    Failed = 6
}

public enum ExcelImportJobState
{
    Processing = 1,
    ReadyForConfirmation = 2,
    Registering = 3,
    Completed = 4,
    Failed = 5,
    Cancelled = 6
}

public sealed record ExcelImportIssue(int Row, string Column, string Message, string Severity = "error");

public sealed record ExcelImportPreviewRow(int Row, IReadOnlyDictionary<string, string> Values);

public sealed class ExcelImportJobSnapshot
{
    public Guid Id { get; init; }
    public ExcelImportJobState State { get; init; }
    public ExcelImportJobStage Stage { get; init; }
    public string FileName { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public int? SheetCount { get; init; }
    public string? SelectedSheet { get; init; }
    public string Message { get; init; } = string.Empty;
    public int ProcessedRows { get; init; }
    public int TotalRows { get; init; }
    public int ValidRows { get; init; }
    public int WarningRows { get; init; }
    public int ErrorRows { get; init; }
    public int DuplicateRows { get; init; }
    public int CreatedRows { get; init; }
    public int UpdatedRows { get; init; }
    public int RejectedRows { get; init; }
    public double? Percent => TotalRows > 0 ? Math.Round(ProcessedRows * 100d / TotalRows, 1) : null;
    public long ElapsedSeconds { get; init; }
    public bool CanCancel { get; init; }
    public string? RedirectUrl { get; init; }
    public IReadOnlyList<ExcelImportIssue> Issues { get; init; } = [];
    public IReadOnlyList<ExcelImportPreviewRow> PreviewRows { get; init; } = [];
}

public sealed class ExcelImportComponentOptions
{
    public string InputId { get; init; } = "excelImportFile";
    public string FileFieldName { get; init; } = "ImportWorkbookFile";
    public string Mode { get; init; } = "job";
    public string AcceptExtensions { get; init; } = ".xlsx,.xls";
    public string Title { get; init; } = "وارد کردن اطلاعات از اکسل";
    public string Description { get; init; } = "فایل اکسل موردنظر را انتخاب کنید یا داخل این بخش رها کنید.";
    public string StartButtonText { get; init; } = "بررسی و ادامه";
    public string ConfirmButtonText { get; init; } = "وارد کردن ردیف‌های معتبر";
    public string SuccessTitle { get; init; } = "اطلاعات با موفقیت وارد شد";
    public string CreatedLabel { get; init; } = "ثبت‌شده";
    public int MaxFileSizeMb { get; init; } = 100;
    public bool RequiresConfirmation { get; init; } = true;
    public bool AllowValidRowsWithErrors { get; init; }
    public bool Compact { get; init; }

    // Inside a data-entry form the uploader is a side task, so it collapses behind a single
    // green button and only unfolds on click instead of occupying half of the first screen.
    public bool Collapsible { get; init; }
    public string ToggleText { get; init; } = "وارد کردن از اکسل";
    public bool ShowResultDetails { get; init; } = true;
    public string? ViewResultsUrl { get; init; }
    public string StartUrl { get; init; } = string.Empty;
    public string StatusUrlTemplate { get; init; } = string.Empty;
    public string ConfirmUrlTemplate { get; init; } = string.Empty;
    public string CancelUrlTemplate { get; init; } = string.Empty;
    public string ErrorReportUrlTemplate { get; init; } = string.Empty;
    public string ImportReportUrlTemplate { get; init; } = string.Empty;
    public string? SampleUrl { get; init; }
}
