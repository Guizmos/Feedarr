using System.Security.Cryptography;
using System.Text;
using Feedarr.Api.Models.Settings;

namespace Feedarr.Api.Services.Security;

// Shared legacy PBKDF2 validation for Basic and web login.
internal static class CredentialValidator
{
    internal static bool Validate(string user, string pass, SecuritySettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Username)) return false;

        // Constant-time username comparison with a fixed 512-byte buffer — prevents both
        // username enumeration and length-based timing leaks (no Math.Max branch on lengths).
        const int UsernameFixedBytes = 512;
        Span<byte> userBuf = stackalloc byte[UsernameFixedBytes];     // zero-initialised
        Span<byte> expectedBuf = stackalloc byte[UsernameFixedBytes]; // zero-initialised
        var userEncoded = Encoding.UTF8.GetBytes(user);
        var expectedEncoded = Encoding.UTF8.GetBytes(settings.Username);
        userEncoded.AsSpan(0, Math.Min(userEncoded.Length, UsernameFixedBytes)).CopyTo(userBuf);
        expectedEncoded.AsSpan(0, Math.Min(expectedEncoded.Length, UsernameFixedBytes)).CopyTo(expectedBuf);
        var usernameMatch = CryptographicOperations.FixedTimeEquals(userBuf, expectedBuf);

        if (string.IsNullOrWhiteSpace(settings.PasswordHash) || string.IsNullOrWhiteSpace(settings.PasswordSalt))
            return false;

        // Always verify the password (even when the username didn't match) so that an
        // attacker cannot enumerate valid usernames by timing the PBKDF2 computation.
        var passwordMatch = VerifyPassword(pass, settings.PasswordHash, settings.PasswordSalt);

        return usernameMatch && passwordMatch;
    }

    private static bool VerifyPassword(string password, string hashBase64, string saltBase64)
    {
        try
        {
            var salt = Convert.FromBase64String(saltBase64);
            var expected = Convert.FromBase64String(hashBase64);
            using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
            var actual = pbkdf2.GetBytes(expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    internal static (string hash, string salt) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
        var hash = pbkdf2.GetBytes(32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    internal static bool TryValidatePasswordStrength(string password, out string error, out object requirements)
    {
        const int minLength = 12;
        error = "";
        requirements = new
        {
            minLength,
            requireUpper = true,
            requireLower = true,
            requireDigit = true,
            requireSymbol = true
        };

        if (password.Length < minLength)
        {
            error = $"Password too simple: minimum {minLength} characters with uppercase, lowercase, digit and symbol.";
            return false;
        }

        var hasLower = password.Any(char.IsLower);
        var hasUpper = password.Any(char.IsUpper);
        var hasDigit = password.Any(char.IsDigit);
        var hasSymbol = password.Any(ch => !char.IsLetterOrDigit(ch));

        if (!hasLower || !hasUpper || !hasDigit || !hasSymbol)
        {
            error = "Password too simple: minimum 12 characters with uppercase, lowercase, digit and symbol.";
            return false;
        }

        return true;
    }

}
