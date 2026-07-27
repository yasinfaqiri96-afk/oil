using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Backups;

public sealed record DriveUploadResult(bool Succeeded, string? FileId, string? ResumeUri, string? Error);

public interface IGoogleDriveBackupClient
{
    /// <summary>رکورد اتصال فعلی (یا null اگر هرگز پیکربندی نشده باشد).</summary>
    Task<BackupDriveCredential?> GetCredentialAsync(CancellationToken ct = default);

    /// <summary>ذخیرهٔ Client ID/Secret و ساخت آدرس رضایت گوگل. Refresh Token هنوز وجود ندارد.</summary>
    Task<string> BeginAuthorizationAsync(string clientId, string clientSecret, string redirectUri, string state, CancellationToken ct = default);

    /// <summary>تبدیل code به Refresh Token و ذخیرهٔ رمزشدهٔ آن.</summary>
    Task<(bool Succeeded, string Message)> CompleteAuthorizationAsync(string code, string redirectUri, CancellationToken ct = default);

    /// <summary>قطع اتصال: Refresh Token پاک می‌شود. فایل‌های آپلودشده در Drive دست‌نخورده می‌مانند.</summary>
    Task DisconnectAsync(CancellationToken ct = default);

    Task SetFolderAsync(string? folderId, CancellationToken ct = default);

    /// <summary>آپلود Resumable یک فایل بکاپ. اگر resumeUri داده شود، آپلود نیمه‌تمام ادامه می‌یابد.</summary>
    Task<DriveUploadResult> UploadBackupAsync(string filePath, string fileName, string? resumeUri, CancellationToken ct = default);
}

public sealed class GoogleDriveBackupClient : IGoogleDriveBackupClient
{
    public const string DriveScope = "https://www.googleapis.com/auth/drive.file";
    public const string ProtectorPurpose = "PTGOilSystem.Backups.GoogleDrive.v1";

    private const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string ResumableUploadEndpoint = "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&supportsAllDrives=true";
    private const string UserInfoEndpoint = "https://www.googleapis.com/oauth2/v3/userinfo";

    // پروتکل Resumable گوگل قطعه‌ها را فقط در مضارب 256KB می‌پذیرد.
    private const int ChunkAlignmentBytes = 256 * 1024;

    private readonly ApplicationDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDataProtector _protector;
    private readonly BackupOptions _options;
    private readonly ILogger<GoogleDriveBackupClient> _logger;

    public GoogleDriveBackupClient(
        ApplicationDbContext db,
        IHttpClientFactory httpClientFactory,
        IDataProtectionProvider dataProtectionProvider,
        IOptions<BackupOptions> options,
        ILogger<GoogleDriveBackupClient> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        _options = options.Value;
        _logger = logger;
    }

    public Task<BackupDriveCredential?> GetCredentialAsync(CancellationToken ct = default)
        => _db.BackupDriveCredentials.OrderBy(c => c.Id).FirstOrDefaultAsync(ct);

    public async Task<string> BeginAuthorizationAsync(
        string clientId,
        string clientSecret,
        string redirectUri,
        string state,
        CancellationToken ct = default)
    {
        var credential = await GetOrCreateCredentialAsync(ct);
        credential.ClientId = clientId.Trim();
        credential.ProtectedClientSecret = _protector.Protect(clientSecret);
        credential.LastError = null;
        await _db.SaveChangesAsync(ct);

        var query = new Dictionary<string, string?>
        {
            ["client_id"] = credential.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = DriveScope,
            // بدون این دو، گوگل Refresh Token نمی‌دهد و بکاپ خودکار پس از یک ساعت می‌میرد.
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["include_granted_scopes"] = "true",
            ["state"] = state
        };

        var builder = new StringBuilder(AuthorizationEndpoint).Append('?');
        builder.AppendJoin('&', query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value ?? "")}"));
        return builder.ToString();
    }

    public async Task<(bool Succeeded, string Message)> CompleteAuthorizationAsync(
        string code,
        string redirectUri,
        CancellationToken ct = default)
    {
        var credential = await GetCredentialAsync(ct);
        if (credential is null || string.IsNullOrWhiteSpace(credential.ClientId) || string.IsNullOrWhiteSpace(credential.ProtectedClientSecret))
        {
            return (false, "ابتدا Client ID و Client Secret را ثبت کنید.");
        }

        var clientSecret = Unprotect(credential.ProtectedClientSecret);
        if (clientSecret is null)
        {
            return (false, "Client Secret ذخیره‌شده قابل خواندن نیست. دوباره آن را ثبت کنید.");
        }

        using var client = _httpClientFactory.CreateClient(nameof(GoogleDriveBackupClient));
        using var response = await client.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = credential.ClientId!,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code"
        }), ct);

        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            credential.LastError = Truncate(payload, 1000);
            await _db.SaveChangesAsync(ct);
            return (false, "گوگل درخواست را رد کرد. Client ID/Secret و Redirect URI را بررسی کنید.");
        }

        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("refresh_token", out var refreshTokenElement))
        {
            return (false, "گوگل Refresh Token نداد. دسترسی قبلی را از حساب گوگل حذف و دوباره تلاش کنید.");
        }

        var accessToken = document.RootElement.TryGetProperty("access_token", out var accessTokenElement)
            ? accessTokenElement.GetString()
            : null;

        credential.ProtectedRefreshToken = _protector.Protect(refreshTokenElement.GetString() ?? "");
        credential.ConnectedAtUtc = DateTime.UtcNow;
        credential.LastTokenRefreshAtUtc = DateTime.UtcNow;
        credential.LastError = null;
        credential.AccountEmail = await TryReadAccountEmailAsync(client, accessToken, ct);
        await _db.SaveChangesAsync(ct);

        return (true, "اتصال Google Drive برقرار شد.");
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        var credential = await GetCredentialAsync(ct);
        if (credential is null)
        {
            return;
        }

        credential.ProtectedRefreshToken = null;
        credential.AccountEmail = null;
        credential.ConnectedAtUtc = null;
        credential.LastTokenRefreshAtUtc = null;
        credential.LastError = null;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetFolderAsync(string? folderId, CancellationToken ct = default)
    {
        var credential = await GetOrCreateCredentialAsync(ct);
        credential.FolderId = string.IsNullOrWhiteSpace(folderId) ? null : folderId.Trim();
        await _db.SaveChangesAsync(ct);
    }

    public async Task<DriveUploadResult> UploadBackupAsync(
        string filePath,
        string fileName,
        string? resumeUri,
        CancellationToken ct = default)
    {
        var credential = await GetCredentialAsync(ct);
        if (credential is null || !credential.IsConnected)
        {
            return new DriveUploadResult(false, null, resumeUri, "Google Drive متصل نیست.");
        }

        if (!File.Exists(filePath))
        {
            return new DriveUploadResult(false, null, null, "فایل بکاپ روی سرور پیدا نشد.");
        }

        var accessToken = await TryGetAccessTokenAsync(credential, ct);
        if (accessToken is null)
        {
            return new DriveUploadResult(false, null, resumeUri, credential.LastError ?? "دریافت توکن دسترسی گوگل ناموفق بود.");
        }

        using var client = _httpClientFactory.CreateClient(nameof(GoogleDriveBackupClient));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var totalBytes = new FileInfo(filePath).Length;
        var sessionUri = resumeUri;
        long offset = 0;

        if (!string.IsNullOrWhiteSpace(sessionUri))
        {
            var probe = await ProbeSessionAsync(client, sessionUri!, totalBytes, ct);
            if (probe.Completed)
            {
                return new DriveUploadResult(true, probe.FileId, null, null);
            }

            if (probe.SessionExpired)
            {
                sessionUri = null;
            }
            else
            {
                offset = probe.NextOffset;
            }
        }

        if (string.IsNullOrWhiteSpace(sessionUri))
        {
            var (createdUri, createError) = await StartSessionAsync(client, credential, fileName, totalBytes, ct);
            if (createdUri is null)
            {
                return new DriveUploadResult(false, null, null, createError);
            }

            sessionUri = createdUri;
            offset = 0;
        }

        return await UploadChunksAsync(client, sessionUri!, filePath, totalBytes, offset, ct);
    }

    // ---- Resumable protocol -------------------------------------------------

    private async Task<(string? SessionUri, string? Error)> StartSessionAsync(
        HttpClient client,
        BackupDriveCredential credential,
        string fileName,
        long totalBytes,
        CancellationToken ct)
    {
        var metadata = new Dictionary<string, object>
        {
            ["name"] = fileName,
            ["mimeType"] = "application/zip"
        };

        if (!string.IsNullOrWhiteSpace(credential.FolderId))
        {
            metadata["parents"] = new[] { credential.FolderId! };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, ResumableUploadEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(metadata), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "application/zip");
        request.Headers.TryAddWithoutValidation("X-Upload-Content-Length", totalBytes.ToString());

        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return (null, $"شروع آپلود Resumable ناموفق بود ({(int)response.StatusCode}): {Truncate(body, 300)}");
        }

        var location = response.Headers.Location?.ToString();
        if (string.IsNullOrWhiteSpace(location) && response.Headers.TryGetValues("Location", out var values))
        {
            location = values.FirstOrDefault();
        }

        return string.IsNullOrWhiteSpace(location)
            ? (null, "گوگل آدرس ادامهٔ آپلود را برنگرداند.")
            : (location, null);
    }

    private async Task<(bool Completed, bool SessionExpired, long NextOffset, string? FileId)> ProbeSessionAsync(
        HttpClient client,
        string sessionUri,
        long totalBytes,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, sessionUri)
        {
            Content = new ByteArrayContent([])
        };
        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(totalBytes) { Unit = "bytes" };

        try
        {
            using var response = await client.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                return (true, false, totalBytes, ReadFileId(body));
            }

            if ((int)response.StatusCode == 308)
            {
                return (false, false, ReadResumeOffset(response), null);
            }

            // 404/410 یعنی نشست آپلود منقضی شده و باید از صفر شروع شود.
            return (false, true, 0, null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Probing the Google Drive resumable session failed.");
            return (false, false, 0, null);
        }
    }

    private async Task<DriveUploadResult> UploadChunksAsync(
        HttpClient client,
        string sessionUri,
        string filePath,
        long totalBytes,
        long offset,
        CancellationToken ct)
    {
        var chunkSize = NormalizeChunkSize(_options.DriveUploadChunkSizeBytes);
        var maxAttempts = Math.Max(1, _options.DriveUploadMaxAttempts);
        var buffer = new byte[chunkSize];

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        while (offset < totalBytes)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(chunkSize, totalBytes - offset)), ct);
            if (read <= 0)
            {
                return new DriveUploadResult(false, null, sessionUri, "خواندن فایل بکاپ برای آپلود ناموفق بود.");
            }

            var lastError = "";
            var chunkUploaded = false;

            for (var attempt = 1; attempt <= maxAttempts && !chunkUploaded; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Put, sessionUri)
                    {
                        Content = new ByteArrayContent(buffer, 0, read)
                    };
                    request.Content.Headers.ContentRange =
                        new ContentRangeHeaderValue(offset, offset + read - 1, totalBytes) { Unit = "bytes" };

                    using var response = await client.SendAsync(request, ct);

                    if (response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync(ct);
                        return new DriveUploadResult(true, ReadFileId(body), null, null);
                    }

                    if ((int)response.StatusCode == 308)
                    {
                        offset = ReadResumeOffset(response, fallback: offset + read);
                        chunkUploaded = true;
                        continue;
                    }

                    lastError = $"HTTP {(int)response.StatusCode}: {Truncate(await response.Content.ReadAsStringAsync(ct), 300)}";

                    // خطای دائمی (مثل توکن باطل) با تلاش دوباره درست نمی‌شود.
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                    {
                        return new DriveUploadResult(false, null, sessionUri, lastError);
                    }
                }
                catch (HttpRequestException ex)
                {
                    // قطع اینترنت: نشست و مقدار offset حفظ می‌شود تا دفعهٔ بعد از همین‌جا ادامه یابد.
                    lastError = ex.Message;
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    lastError = "مهلت آپلود قطعه تمام شد.";
                }

                if (!chunkUploaded && attempt < maxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt))), ct);
                }
            }

            if (!chunkUploaded)
            {
                return new DriveUploadResult(false, null, sessionUri, $"آپلود قطعه ناموفق ماند: {lastError}");
            }
        }

        return new DriveUploadResult(false, null, sessionUri, "آپلود بدون تأیید نهایی گوگل تمام شد.");
    }

    // ---- Tokens -------------------------------------------------------------

    private async Task<string?> TryGetAccessTokenAsync(BackupDriveCredential credential, CancellationToken ct)
    {
        var clientSecret = Unprotect(credential.ProtectedClientSecret);
        var refreshToken = Unprotect(credential.ProtectedRefreshToken);
        if (string.IsNullOrWhiteSpace(credential.ClientId) || clientSecret is null || refreshToken is null)
        {
            credential.LastError = "اطلاعات اتصال Google Drive ناقص یا غیرقابل‌خواندن است.";
            await _db.SaveChangesAsync(ct);
            return null;
        }

        using var client = _httpClientFactory.CreateClient(nameof(GoogleDriveBackupClient));

        try
        {
            using var response = await client.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = credential.ClientId!,
                ["client_secret"] = clientSecret,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token"
            }), ct);

            var payload = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                credential.LastError = Truncate(payload, 1000);
                await _db.SaveChangesAsync(ct);
                return null;
            }

            using var document = JsonDocument.Parse(payload);
            var accessToken = document.RootElement.TryGetProperty("access_token", out var element)
                ? element.GetString()
                : null;

            credential.LastTokenRefreshAtUtc = DateTime.UtcNow;
            credential.LastError = accessToken is null ? "پاسخ گوگل access_token نداشت." : null;
            await _db.SaveChangesAsync(ct);
            return accessToken;
        }
        catch (HttpRequestException ex)
        {
            credential.LastError = Truncate(ex.Message, 1000);
            await _db.SaveChangesAsync(ct);
            return null;
        }
    }

    private async Task<string?> TryReadAccountEmailAsync(HttpClient client, string? accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return document.RootElement.TryGetProperty("email", out var element) ? element.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return null;
        }
    }

    private async Task<BackupDriveCredential> GetOrCreateCredentialAsync(CancellationToken ct)
    {
        var credential = await GetCredentialAsync(ct);
        if (credential is not null)
        {
            return credential;
        }

        credential = new BackupDriveCredential();
        _db.BackupDriveCredentials.Add(credential);
        await _db.SaveChangesAsync(ct);
        return credential;
    }

    private string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (Exception ex)
        {
            // معمولاً یعنی کلیدهای DataProtection عوض شده‌اند؛ باید دوباره متصل شود.
            _logger.LogWarning(ex, "Stored Google Drive secret could not be decrypted.");
            return null;
        }
    }

    // ---- Helpers ------------------------------------------------------------

    internal static int NormalizeChunkSize(int requested)
    {
        var minimum = ChunkAlignmentBytes;
        var value = Math.Max(minimum, requested);
        var aligned = value / ChunkAlignmentBytes * ChunkAlignmentBytes;
        return Math.Max(minimum, aligned);
    }

    internal static long ReadResumeOffset(HttpResponseMessage response, long fallback = 0)
    {
        if (!response.Headers.TryGetValues("Range", out var values))
        {
            return fallback;
        }

        // قالب پاسخ گوگل: "bytes=0-262143" یعنی تا بایت 262143 دریافت شده است.
        var range = values.FirstOrDefault();
        var separatorIndex = range?.LastIndexOf('-') ?? -1;
        if (range is null || separatorIndex < 0 || separatorIndex == range.Length - 1)
        {
            return fallback;
        }

        return long.TryParse(range[(separatorIndex + 1)..], out var lastByte) ? lastByte + 1 : fallback;
    }

    internal static string? ReadFileId(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("id", out var element) ? element.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength ? value ?? "" : value[..maxLength];
}
