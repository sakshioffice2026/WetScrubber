using System.Security.Cryptography;
using System.Text;

namespace WetScrubber.Helpers
{
    public static class PasswordHelper
    {
        // ── Create a random salt ──────────────────────────────────
        public static string GenerateSalt()
        {
            byte[] saltBytes = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(saltBytes);
            return Convert.ToBase64String(saltBytes);
        }

        // ── Hash password with salt using HMACSHA512 ──────────────
        public static string HashPassword(string password, string salt)
        {
            using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(salt));
            byte[] hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(password));
            return Convert.ToBase64String(hashBytes);
        }

        // ── Verify entered password against stored hash ───────────
        public static bool VerifyPassword(string enteredPassword, string storedHash, string storedSalt)
        {
            string hashOfEntered = HashPassword(enteredPassword, storedSalt);
            return hashOfEntered == storedHash;
        }
    }
}
