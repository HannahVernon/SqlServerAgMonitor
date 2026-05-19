using SqlAgMonitor.Core.Services.Credentials;

namespace SqlAgMonitor.Tests.Credentials;

public sealed class PasswordStrengthValidatorTests
{
    private readonly PasswordStrengthValidator _validator = new();

    #region Empty and Null Passwords

    [Fact]
    public void Validate_EmptyString_ReturnsInvalid()
    {
        var result = _validator.Validate(string.Empty);

        Assert.False(result.IsValid);
        Assert.Equal(0, result.EstimatedBitsOfEntropy);
        Assert.Contains("empty", result.Feedback, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_Null_ReturnsInvalid()
    {
        var result = _validator.Validate(null!);

        Assert.False(result.IsValid);
        Assert.Equal(0, result.EstimatedBitsOfEntropy);
        Assert.Contains("empty", result.Feedback, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Insufficient Entropy

    [Fact]
    public void Validate_ShortLowercaseOnly_ReturnsInvalid()
    {
        // 8 lowercase chars = 8 * log2(26) ≈ 37.6 bits — well below 100
        var result = _validator.Validate("abcdefgh");

        Assert.False(result.IsValid);
        Assert.True(result.EstimatedBitsOfEntropy < 100);
    }

    [Fact]
    public void Validate_ShortMixedCaseAndDigits_ReturnsInvalid()
    {
        // 10 chars from [a-zA-Z0-9] = 10 * log2(62) ≈ 59.5 bits — still below 100
        var result = _validator.Validate("Abc123Xyz0");

        Assert.False(result.IsValid);
        Assert.True(result.EstimatedBitsOfEntropy < 100);
    }

    #endregion

    #region Sufficient Entropy

    [Fact]
    public void Validate_LongLowercaseOnly_ReturnsValid()
    {
        // 22 lowercase chars = 22 * log2(26) ≈ 103.4 bits — exceeds 100
        var result = _validator.Validate("abcdefghijklmnopqrstuv");

        Assert.True(result.IsValid);
        Assert.True(result.EstimatedBitsOfEntropy >= 100);
    }

    [Fact]
    public void Validate_AllCharacterTypes_HasHigherEntropy()
    {
        // Mix of lower, upper, digit, special → charset = 26+26+10+32 = 94
        var mixed = "Abcdef1!";
        var lowercaseOnly = "abcdefgh";

        var mixedResult = _validator.Validate(mixed);
        var lowerResult = _validator.Validate(lowercaseOnly);

        // Same length, but mixed charset yields more entropy per character
        Assert.True(mixedResult.EstimatedBitsOfEntropy > lowerResult.EstimatedBitsOfEntropy);
    }

    #endregion

    #region Unicode and Feedback

    [Fact]
    public void Validate_UnicodeCharacters_IncreasesCharsetSize()
    {
        // Unicode adds +100 to charset. Compare entropy of same-length strings.
        var asciiOnly = "abcdefgh";
        var withUnicode = "abcdefg\u00E9"; // 'é' is non-ASCII

        var asciiResult = _validator.Validate(asciiOnly);
        var unicodeResult = _validator.Validate(withUnicode);

        // Unicode charset (26+100=126) > lowercase-only (26), so more entropy per char
        Assert.True(unicodeResult.EstimatedBitsOfEntropy > asciiResult.EstimatedBitsOfEntropy);
    }

    [Fact]
    public void Validate_FeedbackMessage_ContainsBitCount()
    {
        var result = _validator.Validate("abcdefgh");

        Assert.Contains("bits", result.Feedback, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.EstimatedBitsOfEntropy.ToString("F1"), result.Feedback);
    }

    #endregion
}
