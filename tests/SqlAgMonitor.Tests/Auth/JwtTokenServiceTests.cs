using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using SqlAgMonitor.Service.Auth;

namespace SqlAgMonitor.Tests.Auth;

public sealed class JwtTokenServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly ILogger<JwtTokenService> _logger;

    public JwtTokenServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "JwtTests_" + Guid.NewGuid());
        _logger = Substitute.For<ILogger<JwtTokenService>>();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?>? values = null)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values ?? new Dictionary<string, string?>())
            .Build();
    }

    private JwtTokenService CreateService(IConfiguration? config = null, string? dir = null) =>
        new(_logger, config ?? BuildConfig(), dir ?? _testDir);

    [Fact]
    public void GenerateToken_ReturnsNonEmptyString()
    {
        var service = CreateService();

        var token = service.GenerateToken("testuser");

        Assert.False(string.IsNullOrWhiteSpace(token));
    }

    [Fact]
    public void GenerateToken_ContainsUsernameClaim()
    {
        var service = CreateService();

        var token = service.GenerateToken("admin");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        var nameClaim = jwt.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)
            ?? jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.UniqueName);
        Assert.NotNull(nameClaim);
        Assert.Equal("admin", nameClaim.Value);
    }

    [Fact]
    public void GenerateToken_ContainsJtiClaim()
    {
        var service = CreateService();

        var token = service.GenerateToken("user1");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        var jti = jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti);
        Assert.NotNull(jti);
        Assert.False(string.IsNullOrWhiteSpace(jti.Value));
    }

    [Fact]
    public void GenerateToken_TwoCalls_ProduceDifferentJtiValues()
    {
        var service = CreateService();

        var token1 = service.GenerateToken("user1");
        var token2 = service.GenerateToken("user1");

        var jwt1 = new JwtSecurityTokenHandler().ReadJwtToken(token1);
        var jwt2 = new JwtSecurityTokenHandler().ReadJwtToken(token2);

        var jti1 = jwt1.Claims.First(c => c.Type == JwtRegisteredClaimNames.Jti).Value;
        var jti2 = jwt2.Claims.First(c => c.Type == JwtRegisteredClaimNames.Jti).Value;
        Assert.NotEqual(jti1, jti2);
    }

    [Fact]
    public void GenerateToken_ValidatesWithGetValidationParameters()
    {
        var service = CreateService();
        var token = service.GenerateToken("user1");
        var validationParams = service.GetValidationParameters();

        var handler = new JwtSecurityTokenHandler();
        var principal = handler.ValidateToken(token, validationParams, out var validatedToken);

        Assert.NotNull(principal);
        Assert.NotNull(validatedToken);
    }

    [Fact]
    public void SigningKey_PersistsToDisk()
    {
        _ = CreateService();

        var keyFile = Path.Combine(_testDir, "jwt-signing-key.bin");
        Assert.True(File.Exists(keyFile));
        Assert.Equal(64, File.ReadAllBytes(keyFile).Length);
    }

    [Fact]
    public void SecondInstance_ReusesKey_TokenValidatesCrossInstance()
    {
        var service1 = CreateService();
        var token = service1.GenerateToken("crossuser");

        var service2 = CreateService();
        var validationParams = service2.GetValidationParameters();

        var handler = new JwtSecurityTokenHandler();
        var principal = handler.ValidateToken(token, validationParams, out _);
        Assert.NotNull(principal);
    }

    [Fact]
    public void TokenLifetime_RespectsConfiguration()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Service:Auth:TokenLifetimeMinutes"] = "60"
        });
        var service = CreateService(config);

        var beforeGeneration = DateTime.UtcNow;
        var token = service.GenerateToken("user1");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        var expiry = jwt.ValidTo;
        var lifetimeMinutes = (expiry - beforeGeneration).TotalMinutes;
        Assert.InRange(lifetimeMinutes, 59, 61);
    }

    [Fact]
    public void TokenLifetime_ClampedToMinimum15Minutes()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Service:Auth:TokenLifetimeMinutes"] = "1"
        });
        var service = CreateService(config);

        var beforeGeneration = DateTime.UtcNow;
        var token = service.GenerateToken("user1");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        var expiry = jwt.ValidTo;
        var lifetimeMinutes = (expiry - beforeGeneration).TotalMinutes;
        Assert.True(lifetimeMinutes >= 14.9, $"Expected >= 15 min but got {lifetimeMinutes}");
    }

    [Fact]
    public void GetValidationParameters_HasCorrectIssuerAndAudience()
    {
        var service = CreateService();

        var parameters = service.GetValidationParameters();

        Assert.True(parameters.ValidateIssuer);
        Assert.Equal("SqlAgMonitor.Service", parameters.ValidIssuer);
        Assert.True(parameters.ValidateAudience);
        Assert.Equal("SqlAgMonitor.Client", parameters.ValidAudience);
        Assert.Equal(TimeSpan.FromMinutes(2), parameters.ClockSkew);
    }
}
