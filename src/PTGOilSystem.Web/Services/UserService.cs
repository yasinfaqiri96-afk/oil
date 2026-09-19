using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services;

public class UserService : IUserService
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 100_000;
    private const string HashPrefix = "PBKDF2-SHA256";

    private readonly ApplicationDbContext _db;

    public UserService(ApplicationDbContext db) => _db = db;

    public async Task<User> CreateUserAsync(
        string username,
        string fullName,
        string password,
        int? roleId = null,
        string? email = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new BusinessRuleException("USER_USERNAME_REQUIRED", "نام کاربری اجباری است.");
        if (string.IsNullOrWhiteSpace(fullName))
            throw new BusinessRuleException("USER_FULLNAME_REQUIRED", "نام کامل اجباری است.");
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            throw new BusinessRuleException("USER_PASSWORD_WEAK", "رمز عبور باید حداقل ۸ کاراکتر باشد.");

        username = username.Trim();

        var exists = await _db.Users.AnyAsync(u => u.Username == username, ct);
        if (exists)
        {
            throw new BusinessRuleException(
                "USER_USERNAME_TAKEN",
                $"نام کاربری «{username}» قبلاً ثبت شده است.");
        }

        if (roleId.HasValue)
        {
            var roleExists = await _db.Roles.AnyAsync(r => r.Id == roleId.Value, ct);
            if (!roleExists)
            {
                throw new BusinessRuleException(
                    "USER_ROLE_NOT_FOUND",
                    $"نقش با شناسهٔ {roleId.Value} یافت نشد.");
            }
        }

        var user = new User
        {
            Username = username,
            FullName = fullName.Trim(),
            Email = email?.Trim(),
            RoleId = roleId,
            IsActive = true,
            PasswordHash = HashPassword(password),
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<User?> VerifyPasswordAsync(
        string username,
        string password,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return null;

        var user = await _db.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Username == username && u.IsActive, ct);

        if (user is null) return null;
        return VerifyHash(password, user.PasswordHash) ? user : null;
    }

    public string HashPassword(string password)
    {
        if (password is null) throw new ArgumentNullException(nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        // Self-contained: <prefix>$<iterations>$<salt-b64>$<hash-b64>
        return string.Join('$',
            HashPrefix,
            Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    public bool VerifyHash(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(storedHash)) return false;

        var parts = storedHash.Split('$');
        if (parts.Length != 4 || parts[0] != HashPrefix) return false;

        if (!int.TryParse(parts[1],
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var iters))
            return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException) { return false; }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iters, HashAlgorithmName.SHA256, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public async Task ChangePasswordAsync(
        int userId,
        string currentPassword,
        string newPassword,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(currentPassword))
            throw new BusinessRuleException("USER_PASSWORD_CURRENT_REQUIRED", "رمز عبور فعلی اجباری است.");

        ValidateNewPassword(newPassword);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            throw new BusinessRuleException("USER_NOT_FOUND", "کاربر موردنظر یافت نشد.");
        if (!user.IsActive)
            throw new BusinessRuleException("USER_INACTIVE", "حساب کاربری غیرفعال است.");
        if (!VerifyHash(currentPassword, user.PasswordHash))
            throw new BusinessRuleException("USER_PASSWORD_INVALID", "رمز عبور فعلی نادرست است.");
        if (VerifyHash(newPassword, user.PasswordHash))
            throw new BusinessRuleException("USER_PASSWORD_UNCHANGED", "رمز عبور جدید باید با رمز عبور فعلی متفاوت باشد.");

        user.PasswordHash = HashPassword(newPassword);
        await RevokeMobileSessionsAsync(user.Id, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task ResetPasswordAsync(
        int userId,
        string newPassword,
        CancellationToken ct = default)
    {
        ValidateNewPassword(newPassword);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            throw new BusinessRuleException("USER_NOT_FOUND", "کاربر موردنظر یافت نشد.");

        user.PasswordHash = HashPassword(newPassword);
        await RevokeMobileSessionsAsync(user.Id, ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// تغییر یا بازنشانی رمز همهٔ نشست‌های موبایلِ همان کاربر را در همان ذخیره باطل می‌کند.
    /// نشست کوکیِ وب مثل قبل دست نمی‌خورد.
    /// </summary>
    private async Task RevokeMobileSessionsAsync(int userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var activeTokens = await _db.MobileRefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAtUtc == null)
            .ToListAsync(ct);

        foreach (var token in activeTokens)
        {
            token.RevokedAtUtc = now;
            token.RevokedReason = MobileRefreshTokenRevocationReasons.PasswordChanged;
        }
    }

    private static void ValidateNewPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            throw new BusinessRuleException("USER_PASSWORD_WEAK", "رمز عبور باید حداقل 8 کاراکتر باشد.");
    }
}
