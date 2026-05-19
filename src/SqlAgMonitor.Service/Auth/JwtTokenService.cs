using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace SqlAgMonitor.Service.Auth;

/// <summary>
/// Generates and validates JWT bearer tokens for SignalR authentication.
/// The signing key is auto-generated on first run and persisted to disk.
/// </summary>
public sealed class JwtTokenService
{
    private const string Issuer = "SqlAgMonitor.Service";
    private const string Audience = "SqlAgMonitor.Client";

    private readonly SymmetricSecurityKey _signingKey;
    private readonly ILogger<JwtTokenService> _logger;
    private readonly int _tokenLifetimeMinutes;

    public JwtTokenService(ILogger<JwtTokenService> logger, IConfiguration configuration, string? keyDirectory = null)
    {
        _logger = logger;
        _tokenLifetimeMinutes = Math.Clamp(
            configuration.GetValue("Service:Auth:TokenLifetimeMinutes", 480), 15, 10080);

        _signingKey = LoadOrCreateSigningKey(keyDirectory);
    }

    public TokenValidationParameters GetValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidIssuer = Issuer,
        ValidateAudience = true,
        ValidAudience = Audience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = _signingKey,
        ClockSkew = TimeSpan.FromMinutes(2)
    };

    public string GenerateToken(string username)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_tokenLifetimeMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private SymmetricSecurityKey LoadOrCreateSigningKey(string? keyDirectory = null)
    {
        keyDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SqlAgMonitor", "service");
        Directory.CreateDirectory(keyDirectory);

        var keyPath = Path.Combine(keyDirectory, "jwt-signing-key.bin");

        byte[] keyBytes;
        if (File.Exists(keyPath))
        {
            keyBytes = File.ReadAllBytes(keyPath);
            _logger.LogInformation("Loaded JWT signing key from {Path}", keyPath);
        }
        else
        {
            keyBytes = new byte[64]; // 512-bit key
            RandomNumberGenerator.Fill(keyBytes);
            File.WriteAllBytes(keyPath, keyBytes);
            FileAccessHelper.RestrictToCurrentUser(keyPath, _logger);
            _logger.LogInformation("Generated new JWT signing key at {Path}", keyPath);
        }

        return new SymmetricSecurityKey(keyBytes);
    }
}
