namespace SqlAgMonitor.Core.Services.Credentials;

public interface IPasswordStrengthValidator
{
    PasswordStrengthResult Validate(string password);
}

public record PasswordStrengthResult(
    bool IsValid,
    double EstimatedBitsOfEntropy,
    string Feedback
);
