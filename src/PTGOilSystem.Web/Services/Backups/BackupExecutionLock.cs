namespace PTGOilSystem.Web.Services.Backups;

/// <summary>
/// قفل تک‌نمونه‌ای اجرای بکاپ. دو بکاپ هم‌زمان می‌توانند یک فایل را نیمه‌کاره بنویسند
/// و pg_dump را روی یک اتصالِ مشترک به تنگنا ببرند؛ پس فقط یکی در هر لحظه اجازه دارد.
/// عمداً منتظر نمی‌ماند: تلاش دوم فوراً «در حال اجراست» می‌گیرد.
/// </summary>
public interface IBackupExecutionLock
{
    bool IsRunning { get; }
    string? CurrentOwner { get; }
    DateTime? StartedAtUtc { get; }

    /// <summary>در صورت آزاد بودن قفل، آن را می‌گیرد؛ در غیر این صورت null برمی‌گرداند.</summary>
    IDisposable? TryAcquire(string owner);
}

public sealed class BackupExecutionLock : IBackupExecutionLock
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _timeProvider;

    public BackupExecutionLock(TimeProvider timeProvider) => _timeProvider = timeProvider;

    public bool IsRunning => _gate.CurrentCount == 0;
    public string? CurrentOwner { get; private set; }
    public DateTime? StartedAtUtc { get; private set; }

    public IDisposable? TryAcquire(string owner)
    {
        if (!_gate.Wait(0))
        {
            return null;
        }

        CurrentOwner = owner;
        StartedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        return new Releaser(this);
    }

    private void Release()
    {
        CurrentOwner = null;
        StartedAtUtc = null;
        _gate.Release();
    }

    private sealed class Releaser : IDisposable
    {
        private BackupExecutionLock? _owner;

        public Releaser(BackupExecutionLock owner) => _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release();
        }
    }
}
