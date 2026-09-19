using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Security.Mobile;

public sealed record MobileClientInfo(string? DeviceId, string? DeviceName, string? IpAddress);

public sealed record MobileTokenPair(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc,
    Guid SessionId);

public enum MobileRefreshStatus
{
    Success,
    Invalid,
    Expired,
    Revoked,
    ReuseDetected,
    UserInactive
}

public sealed record MobileRefreshResult(
    MobileRefreshStatus Status,
    MobileTokenPair? Tokens = null,
    User? User = null,
    Guid? SessionId = null);

public interface IMobileTokenService
{
    Task<MobileTokenPair> IssueForLoginAsync(User user, MobileClientInfo client, CancellationToken ct = default);

    Task<MobileRefreshResult> RefreshAsync(string refreshToken, MobileClientInfo client, CancellationToken ct = default);

    /// <summary>همهٔ توکن‌های فعالِ یک نشست (یک گوشی) را باطل می‌کند.</summary>
    Task<int> RevokeSessionAsync(int userId, Guid sessionId, string reason, string? ipAddress, CancellationToken ct = default);

    /// <summary>نشستِ صاحبِ این توکن تازه‌سازی را باطل می‌کند؛ اگر توکن ناشناخته باشد null.</summary>
    Task<(int UserId, Guid SessionId)?> RevokeByRefreshTokenAsync(
        string refreshToken,
        string reason,
        string? ipAddress,
        CancellationToken ct = default);

    Task<bool> IsSessionActiveAsync(int userId, Guid sessionId, CancellationToken ct = default);

    string CreateAccessToken(User user, Guid sessionId, out DateTime expiresAtUtc);
}

/// <summary>
/// صدور و چرخش توکن موبایل. JWT فقط شناسهٔ کاربر و نشست را دارد؛ نقش و دسترسی در هر درخواست
/// از دیتابیس و با همان <see cref="UserClaimsFactory"/> وب ساخته می‌شود.
/// </summary>
public sealed class MobileTokenService : IMobileTokenService
{
    private const int RefreshTokenBytes = 64;

    private readonly ApplicationDbContext _db;
    private readonly MobileJwtKeyProvider _keys;
    private readonly MobileAuthOptions _options;
    private readonly TimeProvider _time;

    public MobileTokenService(
        ApplicationDbContext db,
        MobileJwtKeyProvider keys,
        IOptions<MobileAuthOptions> options,
        TimeProvider time)
    {
        _db = db;
        _keys = keys;
        _options = options.Value;
        _time = time;
    }

    public static string HashRefreshToken(string refreshToken)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken))).ToLowerInvariant();

    public async Task<MobileTokenPair> IssueForLoginAsync(User user, MobileClientInfo client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        EnsureCanIssue();

        var now = UtcNow();
        var rawRefreshToken = NewRefreshToken();
        var entity = new MobileRefreshToken
        {
            UserId = user.Id,
            TokenHash = HashRefreshToken(rawRefreshToken),
            SessionId = Guid.NewGuid(),
            ExpiresAtUtc = now.AddDays(_options.RefreshTokenDays),
            DeviceId = Truncate(client.DeviceId, 100),
            DeviceName = Truncate(client.DeviceName, 100),
            CreatedByIp = Truncate(client.IpAddress, 64)
        };

        _db.MobileRefreshTokens.Add(entity);
        await _db.SaveChangesAsync(ct);

        var accessToken = CreateAccessToken(user, entity.SessionId, out var accessExpiresAtUtc);
        return new MobileTokenPair(accessToken, accessExpiresAtUtc, rawRefreshToken, AsUtc(entity.ExpiresAtUtc), entity.SessionId);
    }

    public async Task<MobileRefreshResult> RefreshAsync(string refreshToken, MobileClientInfo client, CancellationToken ct = default)
    {
        EnsureCanIssue();
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return new MobileRefreshResult(MobileRefreshStatus.Invalid);
        }

        var hash = HashRefreshToken(refreshToken.Trim());
        var now = UtcNow();

        var current = await _db.MobileRefreshTokens
            .AsNoTracking()
            .Include(t => t.User)
            .ThenInclude(u => u!.Role)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (current is null)
        {
            return new MobileRefreshResult(MobileRefreshStatus.Invalid);
        }

        if (current.RevokedAtUtc is not null)
        {
            if (current.RevokedReason == MobileRefreshTokenRevocationReasons.Rotated)
            {
                // توکنِ چرخیده دوباره آمده: یا دزدی شده یا تکرار شده. کل نشست باطل می‌شود.
                await RevokeSessionAsync(
                    current.UserId,
                    current.SessionId,
                    MobileRefreshTokenRevocationReasons.ReuseDetected,
                    client.IpAddress,
                    ct);
                return new MobileRefreshResult(MobileRefreshStatus.ReuseDetected, User: current.User, SessionId: current.SessionId);
            }

            return new MobileRefreshResult(MobileRefreshStatus.Revoked, User: current.User, SessionId: current.SessionId);
        }

        if (AsUtc(current.ExpiresAtUtc) <= now)
        {
            return new MobileRefreshResult(MobileRefreshStatus.Expired, User: current.User, SessionId: current.SessionId);
        }

        if (current.User is null || !current.User.IsActive)
        {
            await RevokeSessionAsync(
                current.UserId,
                current.SessionId,
                MobileRefreshTokenRevocationReasons.UserInactive,
                client.IpAddress,
                ct);
            return new MobileRefreshResult(MobileRefreshStatus.UserInactive, User: current.User, SessionId: current.SessionId);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        // ادعای اتمیِ همین ردیف: درخواستِ هم‌زمانِ دوم صفر ردیف می‌گیرد و تکرار شمرده می‌شود.
        var claimed = await _db.MobileRefreshTokens
            .Where(t => t.Id == current.Id && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.RevokedAtUtc, (DateTime?)now)
                .SetProperty(t => t.RevokedReason, MobileRefreshTokenRevocationReasons.Rotated)
                .SetProperty(t => t.RevokedByIp, Truncate(client.IpAddress, 64))
                .SetProperty(t => t.LastUsedAtUtc, (DateTime?)now)
                .SetProperty(t => t.UpdatedAtUtc, (DateTime?)now), ct);

        if (claimed == 0)
        {
            await transaction.RollbackAsync(ct);
            await RevokeSessionAsync(
                current.UserId,
                current.SessionId,
                MobileRefreshTokenRevocationReasons.ReuseDetected,
                client.IpAddress,
                ct);
            return new MobileRefreshResult(MobileRefreshStatus.ReuseDetected, User: current.User, SessionId: current.SessionId);
        }

        var rawRefreshToken = NewRefreshToken();
        var replacement = new MobileRefreshToken
        {
            UserId = current.UserId,
            TokenHash = HashRefreshToken(rawRefreshToken),
            SessionId = current.SessionId,
            ExpiresAtUtc = AsUtc(current.ExpiresAtUtc),
            DeviceId = Truncate(client.DeviceId, 100) ?? current.DeviceId,
            DeviceName = current.DeviceName,
            CreatedByIp = Truncate(client.IpAddress, 64)
        };

        _db.MobileRefreshTokens.Add(replacement);
        await _db.SaveChangesAsync(ct);

        await _db.MobileRefreshTokens
            .Where(t => t.Id == current.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.ReplacedByTokenId, (int?)replacement.Id), ct);

        await transaction.CommitAsync(ct);

        var accessToken = CreateAccessToken(current.User, current.SessionId, out var accessExpiresAtUtc);
        return new MobileRefreshResult(
            MobileRefreshStatus.Success,
            new MobileTokenPair(accessToken, accessExpiresAtUtc, rawRefreshToken, AsUtc(replacement.ExpiresAtUtc), current.SessionId),
            current.User,
            current.SessionId);
    }

    public Task<int> RevokeSessionAsync(int userId, Guid sessionId, string reason, string? ipAddress, CancellationToken ct = default)
    {
        var now = UtcNow();
        var revokedByIp = Truncate(ipAddress, 64);

        return _db.MobileRefreshTokens
            .Where(t => t.UserId == userId && t.SessionId == sessionId && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.RevokedAtUtc, (DateTime?)now)
                .SetProperty(t => t.RevokedReason, reason)
                .SetProperty(t => t.RevokedByIp, revokedByIp)
                .SetProperty(t => t.UpdatedAtUtc, (DateTime?)now), ct);
    }

    public async Task<(int UserId, Guid SessionId)?> RevokeByRefreshTokenAsync(
        string refreshToken,
        string reason,
        string? ipAddress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        var hash = HashRefreshToken(refreshToken.Trim());
        var owner = await _db.MobileRefreshTokens
            .AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Select(t => new { t.UserId, t.SessionId })
            .FirstOrDefaultAsync(ct);

        if (owner is null)
        {
            return null;
        }

        await RevokeSessionAsync(owner.UserId, owner.SessionId, reason, ipAddress, ct);
        return (owner.UserId, owner.SessionId);
    }

    public Task<bool> IsSessionActiveAsync(int userId, Guid sessionId, CancellationToken ct = default)
    {
        var now = UtcNow();
        return _db.MobileRefreshTokens
            .AsNoTracking()
            .AnyAsync(t => t.UserId == userId
                && t.SessionId == sessionId
                && t.RevokedAtUtc == null
                && t.ExpiresAtUtc > now, ct);
    }

    public string CreateAccessToken(User user, Guid sessionId, out DateTime expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(user);
        EnsureCanIssue();

        var now = UtcNow();
        expiresAtUtc = now.AddMinutes(_options.AccessTokenMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expiresAtUtc,
            SigningCredentials = new SigningCredentials(_keys.Key, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                [MobileClaimTypes.Subject] = user.Id.ToString(CultureInfo.InvariantCulture),
                [MobileClaimTypes.SessionId] = sessionId.ToString("D"),
                [MobileClaimTypes.TokenId] = Guid.NewGuid().ToString("N")
            }
        };

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

    private void EnsureCanIssue()
    {
        if (!_keys.CanIssueTokens)
        {
            throw new InvalidOperationException("Mobile token signing key is not configured.");
        }
    }

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;

    private static DateTime AsUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string NewRefreshToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(RefreshTokenBytes));

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
