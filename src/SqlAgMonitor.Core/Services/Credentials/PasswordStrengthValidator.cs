using System.Text.RegularExpressions;

namespace SqlAgMonitor.Core.Services.Credentials;

public class PasswordStrengthValidator : IPasswordStrengthValidator
{
    private const double MinimumBitsOfEntropy = 100.0;

    public PasswordStrengthResult Validate(string password)
    {
        if (string.IsNullOrEmpty(password))
            return new PasswordStrengthResult(false, 0, "Password cannot be empty.");

        var charsetSize = CalculateCharsetSize(password);
        var entropy = password.Length * Math.Log2(charsetSize);

        if (entropy >= MinimumBitsOfEntropy)
        {
            return new PasswordStrengthResult(true, entropy,
                $"Password meets strength requirements ({entropy:F1} bits of entropy).");
        }

        var minLength = (int)Math.Ceiling(MinimumBitsOfEntropy / Math.Log2(charsetSize));
        return new PasswordStrengthResult(false, entropy,
            $"Password has {entropy:F1} bits of entropy, but {MinimumBitsOfEntropy} bits are required. " +
            $"Try a longer password (at least {minLength} characters with your current character set) or use more character types.");
    }

    private static int CalculateCharsetSize(string password)
    {
        var size = 0;
        if (Regex.IsMatch(password, "[a-z]")) size += 26;
        if (Regex.IsMatch(password, "[A-Z]")) size += 26;
        if (Regex.IsMatch(password, "[0-9]")) size += 10;
        if (Regex.IsMatch(password, @"[!@#$%^&*()\-_=+\[\]{};:'"",.<>?/\\|`~]")) size += 32;
        if (Regex.IsMatch(password, @"[^\x00-\x7F]")) size += 100;

        return Math.Max(size, 1);
    }
}
