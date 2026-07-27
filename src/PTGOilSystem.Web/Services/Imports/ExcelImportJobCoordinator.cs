using System.Collections.Concurrent;
using PTGOilSystem.Web.Models.ExcelImport;

namespace PTGOilSystem.Web.Services.Imports;

public sealed class ExcelImportJobCoordinator
{
    private readonly ConcurrentDictionary<Guid, JobEntry> _jobs = new();
    private readonly SemaphoreSlim _workerSlots = new(2, 2);
    private readonly ILogger<ExcelImportJobCoordinator> _logger;

    public ExcelImportJobCoordinator(ILogger<ExcelImportJobCoordinator> logger)
    {
        _logger = logger;
    }

    public Guid Create(string ownerKey, string fileName, long fileSize)
    {
        CleanupExpired();
        var id = Guid.NewGuid();
        _jobs[id] = new JobEntry(id, ownerKey, fileName, fileSize);
        return id;
    }

    public bool TryGet(Guid id, string ownerKey, out ExcelImportJobSnapshot? snapshot)
    {
        snapshot = null;
        if (!_jobs.TryGetValue(id, out var entry) || !entry.IsOwnedBy(ownerKey))
        {
            return false;
        }

        snapshot = entry.Snapshot();
        return true;
    }

    public object? GetPayload(Guid id, string ownerKey)
        => _jobs.TryGetValue(id, out var entry) && entry.IsOwnedBy(ownerKey) ? entry.Payload : null;

    public bool TryCancel(Guid id, string ownerKey)
    {
        if (!_jobs.TryGetValue(id, out var entry) || !entry.IsOwnedBy(ownerKey))
        {
            return false;
        }

        return entry.Cancel();
    }

    public bool TryStartRegistration(Guid id, string ownerKey, Func<ExcelImportJobContext, CancellationToken, Task> work)
    {
        if (!_jobs.TryGetValue(id, out var entry) || !entry.IsOwnedBy(ownerKey) || !entry.BeginRegistration())
        {
            return false;
        }

        _ = RunAsync(entry, work);
        return true;
    }

    public void StartValidation(Guid id, Func<ExcelImportJobContext, CancellationToken, Task> work)
    {
        if (_jobs.TryGetValue(id, out var entry))
        {
            _ = RunAsync(entry, work);
        }
    }

    private async Task RunAsync(JobEntry entry, Func<ExcelImportJobContext, CancellationToken, Task> work)
    {
        await _workerSlots.WaitAsync();
        try
        {
            await work(new ExcelImportJobContext(entry), entry.Token);
        }
        catch (OperationCanceledException) when (entry.Token.IsCancellationRequested)
        {
            entry.MarkCancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Excel import job {JobId} failed.", entry.Id);
            entry.Fail("عملیات Import در سرور کامل نشد. لطفاً دوباره وضعیت را بررسی یا فایل را بارگذاری کنید.");
        }
        finally
        {
            _workerSlots.Release();
        }
    }

    private void CleanupExpired()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-2);
        foreach (var pair in _jobs.Where(pair => pair.Value.UpdatedAt < cutoff).ToArray())
        {
            if (_jobs.TryRemove(pair.Key, out var removed))
            {
                removed.Dispose();
            }
        }
    }

    internal sealed class JobEntry : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cts = new();
        private ExcelImportJobState _state = ExcelImportJobState.Processing;
        private ExcelImportJobStage _stage = ExcelImportJobStage.Upload;
        private string _message = "فایل با موفقیت به سرور رسید.";
        private int _processedRows;
        private int _totalRows;
        private int _validRows;
        private int _warningRows;
        private int _errorRows;
        private int _duplicateRows;
        private int _createdRows;
        private int _updatedRows;
        private int _rejectedRows;
        private int? _sheetCount;
        private string? _selectedSheet;
        private string? _redirectUrl;
        private IReadOnlyList<ExcelImportIssue> _issues = [];
        private IReadOnlyList<ExcelImportPreviewRow> _previewRows = [];

        public JobEntry(Guid id, string ownerKey, string fileName, long fileSize)
        {
            Id = id;
            OwnerKey = ownerKey;
            FileName = fileName;
            FileSize = fileSize;
            StartedAt = DateTimeOffset.UtcNow;
            UpdatedAt = StartedAt;
        }

        public Guid Id { get; }
        public string OwnerKey { get; }
        public string FileName { get; }
        public long FileSize { get; }
        public DateTimeOffset StartedAt { get; }
        public DateTimeOffset UpdatedAt { get; private set; }
        public object? Payload { get; private set; }
        public CancellationToken Token => _cts.Token;

        public bool IsOwnedBy(string ownerKey) => string.Equals(OwnerKey, ownerKey, StringComparison.Ordinal);

        public ExcelImportJobSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new ExcelImportJobSnapshot
                {
                    Id = Id,
                    State = _state,
                    Stage = _stage,
                    FileName = FileName,
                    FileSize = FileSize,
                    SheetCount = _sheetCount,
                    SelectedSheet = _selectedSheet,
                    Message = _message,
                    ProcessedRows = _processedRows,
                    TotalRows = _totalRows,
                    ValidRows = _validRows,
                    WarningRows = _warningRows,
                    ErrorRows = _errorRows,
                    DuplicateRows = _duplicateRows,
                    CreatedRows = _createdRows,
                    UpdatedRows = _updatedRows,
                    RejectedRows = _rejectedRows,
                    ElapsedSeconds = Math.Max(0, (long)(DateTimeOffset.UtcNow - StartedAt).TotalSeconds),
                    CanCancel = _state == ExcelImportJobState.Processing,
                    RedirectUrl = _redirectUrl,
                    Issues = _issues,
                    PreviewRows = _previewRows
                };
            }
        }

        public void Update(ExcelImportJobStage stage, string message, int processedRows = 0, int totalRows = 0,
            int? sheetCount = null, string? selectedSheet = null)
        {
            lock (_gate)
            {
                _stage = stage;
                _message = message;
                _processedRows = processedRows;
                _totalRows = totalRows;
                _sheetCount = sheetCount ?? _sheetCount;
                _selectedSheet = selectedSheet ?? _selectedSheet;
                UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        public void Ready(object payload, int totalRows, int validRows, int warningRows, int errorRows,
            int duplicateRows, IReadOnlyList<ExcelImportIssue> issues, IReadOnlyList<ExcelImportPreviewRow> previewRows,
            int? sheetCount, string? selectedSheet)
        {
            lock (_gate)
            {
                Payload = payload;
                _state = ExcelImportJobState.ReadyForConfirmation;
                _stage = ExcelImportJobStage.Validation;
                _message = errorRows > 0 ? "بررسی فایل کامل شد؛ خطاها را اصلاح کنید." : "فایل آماده تأیید نهایی است.";
                _processedRows = totalRows;
                _totalRows = totalRows;
                _validRows = validRows;
                _warningRows = warningRows;
                _errorRows = errorRows;
                _duplicateRows = duplicateRows;
                _issues = issues;
                _previewRows = previewRows;
                _sheetCount = sheetCount;
                _selectedSheet = selectedSheet;
                UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        public bool BeginRegistration()
        {
            lock (_gate)
            {
                if (_state != ExcelImportJobState.ReadyForConfirmation || _errorRows > 0)
                {
                    return false;
                }

                _state = ExcelImportJobState.Registering;
                _stage = ExcelImportJobStage.Registration;
                _message = "در حال ثبت اطلاعات داخل Transaction…";
                _processedRows = 0;
                UpdatedAt = DateTimeOffset.UtcNow;
                return true;
            }
        }

        public void Complete(int createdRows, int updatedRows, int rejectedRows, string? redirectUrl)
        {
            lock (_gate)
            {
                _state = ExcelImportJobState.Completed;
                _stage = ExcelImportJobStage.Completed;
                _message = "اطلاعات با موفقیت وارد شد.";
                _processedRows = _totalRows;
                _createdRows = createdRows;
                _updatedRows = updatedRows;
                _rejectedRows = rejectedRows;
                _redirectUrl = redirectUrl;
                UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        public void Fail(string message, IReadOnlyList<ExcelImportIssue>? issues = null)
        {
            lock (_gate)
            {
                _state = ExcelImportJobState.Failed;
                _stage = ExcelImportJobStage.Failed;
                _message = message;
                if (issues is not null)
                {
                    _issues = issues;
                    _errorRows = issues.Select(issue => issue.Row).Where(row => row > 0).Distinct().Count();
                }
                UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        public bool Cancel()
        {
            lock (_gate)
            {
                if (_state != ExcelImportJobState.Processing)
                {
                    return false;
                }

                _cts.Cancel();
                return true;
            }
        }

        public void MarkCancelled()
        {
            lock (_gate)
            {
                _state = ExcelImportJobState.Cancelled;
                _message = "عملیات پیش از ثبت اطلاعات لغو شد.";
                UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        public void Dispose() => _cts.Dispose();
    }
}

public sealed class ExcelImportJobContext
{
    private readonly ExcelImportJobCoordinator.JobEntry _entry;

    internal ExcelImportJobContext(ExcelImportJobCoordinator.JobEntry entry) => _entry = entry;

    public void Update(ExcelImportJobStage stage, string message, int processedRows = 0, int totalRows = 0,
        int? sheetCount = null, string? selectedSheet = null)
        => _entry.Update(stage, message, processedRows, totalRows, sheetCount, selectedSheet);

    public void Ready(object payload, int totalRows, int validRows, int warningRows, int errorRows,
        int duplicateRows, IReadOnlyList<ExcelImportIssue> issues, IReadOnlyList<ExcelImportPreviewRow> previewRows,
        int? sheetCount, string? selectedSheet)
        => _entry.Ready(payload, totalRows, validRows, warningRows, errorRows, duplicateRows, issues, previewRows, sheetCount, selectedSheet);

    public void Complete(int createdRows, int updatedRows, int rejectedRows, string? redirectUrl)
        => _entry.Complete(createdRows, updatedRows, rejectedRows, redirectUrl);

    public void Fail(string message, IReadOnlyList<ExcelImportIssue>? issues = null) => _entry.Fail(message, issues);
}
