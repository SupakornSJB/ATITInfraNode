using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using GameServer.Data;
using GameServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace GameServer.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly DashboardDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        DashboardDbContext db,
        IConfiguration config,
        ILogger<AuthController> logger)
    {
        _db     = db;
        _config = config;
        _logger = logger;
    }

    // POST /api/auth/login
    // Password is sent as plaintext over HTTPS — TLS encrypts in transit.
    // BCrypt.Verify is constant-time — no timing attack. Same error message for
    // wrong username and wrong password — prevents username enumeration.
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == req.Username);

        if (user == null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
        {
            _logger.LogWarning("[Auth] Failed login attempt for username: {Username}", req.Username);
            return Unauthorized(new { error = "Invalid credentials" });
        }

        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("[Auth] Successful login: {Username}", user.Username);
        var expiryMinutes = _config.GetValue<int>("Dashboard:JwtExpiryMinutes", 60);
        return Ok(new LoginResponse(
            Token:     GenerateJwt(user.Username, user.Role, expiryMinutes),
            ExpiresIn: expiryMinutes * 60));
    }

    private string GenerateJwt(string username, string role, int expiryMinutes)
    {
        var secret = _config["Dashboard:JwtSecret"]!;
        var key    = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds  = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer:   "ATITFinalProject",
            audience: "dashboard",
            claims: new[]
            {
                new Claim(ClaimTypes.Name, username),
                new Claim(ClaimTypes.Role, role),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            },
            expires:            DateTime.UtcNow.AddMinutes(expiryMinutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
