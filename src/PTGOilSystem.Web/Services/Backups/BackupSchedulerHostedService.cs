using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Backups;

/// <summary>
/// تنها جایی که بکاپ واقعاً اجرا می‌شود — چه دستی، چه زمان‌بندی‌شده.
/// یک مصرف‌کنندهٔ تک‌نخی به‌علاوهٔ قفل اجرا تضمین می‌کند دو بکاپ هرگز هم‌زمان نشوند،
/// و درخواست دستی هم پاسخِ HTTP را قفل نمی‌کند.
/// </summary>
public sealed class BackupSchedulerHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBackupTriggerQueue _queue;
    private readonly BackupOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BackupSchedulerHostedService> _logger;

    public BackupSchedulerHostedService(
        IServiceScopeFactory scopeFactory,
        IBackupTriggerQueue queue,
        IOptions<BackupOptions> options,
        TimeProvider timeProvider,
        ILogger<BackupSchedulerHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.SchedulerEnabled)
        {
            _logger.LogInformation("Backup scheduler is disabled by configuration.");
            return;
        }

        var pollInterval = TimeSpan.FromMinutes(Math.Clamp(_options.SchedulerPollMinutes, 1, 60));

        while (!stoppingToken.IsCancellationRequested)
        {
            BackupTriggerRequest? request = null;

            using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                waitCts.CancelAfter(pollInterval);
                try
                {
                    request = await _queue.DequeueAsync(waitCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // مهلت انتظار تمام شد؛ نوبتِ بررسی زمان‌بندی است.
                }
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                if (request is not null)
                {
                    await RunAsync(request, stoppingToken);
                }
                else
                {
                    await RunScheduledIfDueAsync(stoppingToken);
                    await RetryPendingUploadsAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // حلقهٔ زمان‌بند هرگز نباید به‌خاطر یک اجرای خراب بمیرد.
                _logger.LogError(ex, "The backup scheduler loop failed.");
            }
        }
    }

    private async Task RunAsync(BackupTriggerRequest request, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IBackupService>();
        var result = await service.RunAsync(request, ct);

        if (!result.Succeeded)
        {
            _logger.LogWarning("Backup requested by {RequestedBy} did not succeed: {Message}", request.RequestedBy, result.Message);
        }
    }

    private async Task RunScheduledIfDueAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IBackupService>();
        var settings = await service.EnsureNextRunScheduledAsync(ct);

        if (!settings.AutoBackupEnabled)
        {
            return;
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        if (settings.NextRunAtUtc is null || settings.NextRunAtUtc > nowUtc)
        {
            return;
        }

        await service.RunAsync(
            new BackupTriggerRequest(BackupTriggerKind.Scheduled, "system", null),
            ct);
    }

    private async Task RetryPendingUploadsAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IBackupService>();
        await service.RetryPendingDriveUploadsAsync(ct);
    }
}
