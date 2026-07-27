using System.Threading.Channels;

namespace PTGOilSystem.Web.Services.Backups;

/// <summary>
/// صف تک‌ظرفیتیِ درخواست بکاپ دستی. ظرفیت ۱ عمدی است: کلیک دوم روی «ایجاد بکاپ اکنون»
/// نباید یک اجرای اضافه در صف بگذارد؛ همان یک درخواستِ در انتظار کافی است.
/// </summary>
public interface IBackupTriggerQueue
{
    /// <summary>false یعنی درخواستی از قبل در صف است.</summary>
    bool TryEnqueue(BackupTriggerRequest request);

    /// <summary>درخواستی در صف هست ولی هنوز شروع نشده. برای نشان‌دادن وضعیت «در صف» در صفحه.</summary>
    bool HasPending { get; }

    ValueTask<BackupTriggerRequest> DequeueAsync(CancellationToken ct);
}

public sealed class BackupTriggerQueue : IBackupTriggerQueue
{
    private readonly Channel<BackupTriggerRequest> _channel =
        Channel.CreateBounded<BackupTriggerRequest>(new BoundedChannelOptions(1)
        {
            // Wait لازم است تا TryWrite روی صفِ پر false برگرداند؛ DropWrite درخواست را
            // خاموش دور می‌ریزد و به کاربر «شروع شد» می‌گوید در حالی که چیزی در صف ننشسته.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

    public bool HasPending => _channel.Reader.Count > 0;

    public bool TryEnqueue(BackupTriggerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _channel.Writer.TryWrite(request);
    }

    public ValueTask<BackupTriggerRequest> DequeueAsync(CancellationToken ct)
        => _channel.Reader.ReadAsync(ct);
}
