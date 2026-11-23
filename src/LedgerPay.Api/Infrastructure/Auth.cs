using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace LedgerPay.Api.Infrastructure;

public record User(Guid Id, string Email, string FullName, string PasswordHash, string Role);
public record SessionDto(string Token, Guid UserId, string Email, string FullName, string Role);

public static class Roles
{
    public const string Customer = "customer";
    public const string Admin = "admin";
}

public class JwtOptions
{
    public string Issuer { get; set; } = "ledgerpay";
    public string Audience { get; set; } = "ledgerpay";
    public string Key { get; set; } = "development-only-signing-key-change-me-0123456789";
    public int LifetimeMinutes { get; set; } = 120;

    public TokenValidationParameters Validation() => new()
    {
        ValidIssuer = Issuer, ValidAudience = Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)),
        NameClaimType = "name", RoleClaimType = "role", ClockSkew = TimeSpan.FromSeconds(30),
    };

    public SessionDto Issue(User user)
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(Issuer, Audience,
            [new Claim("sub", user.Id.ToString()), new Claim("email", user.Email), new Claim("name", user.FullName), new Claim("role", user.Role)],
            now, now.AddMinutes(LifetimeMinutes),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256));
        return new SessionDto(new JwtSecurityTokenHandler().WriteToken(token), user.Id, user.Email, user.FullName, user.Role);
    }
}

public static class ClaimsExtensions
{
    public static Guid UserId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirst("sub")!.Value);
    public static bool IsAdmin(this ClaimsPrincipal p) => p.IsInRole(Roles.Admin);
}

public class Users(NpgsqlDataSource db)
{
    private readonly PasswordHasher<User> _hasher = new();

    public async Task<User?> FindAsync(string email)
    {
        await using var cmd = db.CreateCommand("SELECT id, email, full_name, password_hash, role FROM users WHERE email = $1");
        cmd.Parameters.AddWithValue(email.Trim().ToLowerInvariant());
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? new User(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)) : null;
    }

    public async Task<string?> NameOfAsync(Guid id)
    {
        await using var cmd = db.CreateCommand("SELECT full_name FROM users WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteScalarAsync() as string;
    }

    /// <summary>Creates the user, or returns null if the email is taken.</summary>
    public async Task<User?> CreateAsync(string email, string fullName, string password, string role)
    {
        var user = new User(Guid.NewGuid(), email.Trim().ToLowerInvariant(), fullName.Trim(), "", role);
        user = user with { PasswordHash = _hasher.HashPassword(user, password) };
        await using var cmd = db.CreateCommand(
            "INSERT INTO users (id, email, full_name, password_hash, role, created_at) VALUES ($1,$2,$3,$4,$5, now()) ON CONFLICT (email) DO NOTHING");
        cmd.Parameters.AddWithValue(user.Id);
        cmd.Parameters.AddWithValue(user.Email);
        cmd.Parameters.AddWithValue(user.FullName);
        cmd.Parameters.AddWithValue(user.PasswordHash);
        cmd.Parameters.AddWithValue(user.Role);
        return await cmd.ExecuteNonQueryAsync() == 1 ? user : null;
    }

    public bool Verify(User user, string password) =>
        _hasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;
}
