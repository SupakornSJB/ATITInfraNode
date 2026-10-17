# docs/plans/phase-6-dashboard-server.md — Admin Security Dashboard Backend

> Place this file in the root of `ATITFinalProject-server/`.
> Read `docs/plans/phase-6-security.md` first — this file depends on `IIntrusionDetector`
> and `SecurityHub` defined there.
> The frontend SPA is in `ATITFinalProject-dashboard/` — see its `CLAUDE.md`.

---

## What This File Covers

The ASP.NET backend additions that power the admin security dashboard:

1. **`SecurityHub`** — SignalR hub that pushes real-time alerts and device state
   changes to the React dashboard
2. **`/api/security/*`** — REST endpoints for initial state load and manual actions
3. **JWT authentication** — dashboard login using SQLite + bcrypt, token issuance,
   protected endpoints
4. **Static file serving** — ASP.NET serves the built React SPA from `wwwroot/dashboard/`

---

## Authentication Design

The login flow uses the standard production-correct pattern:

```
Client sends plaintext password over HTTPS
      ↓
Server: BCrypt.Verify(plaintext, storedHash)
      ↓
Issue signed JWT
```

**Why plaintext over HTTPS is correct:** TLS encrypts the entire HTTP body in
transit — the password is never visible on the wire. Client-side hashing before
sending is an anti-pattern: it turns the hash into the password (if the database
leaks, the hash can be replayed directly). Do not hash on the client side.

**Password storage:** bcrypt hash in SQLite. Plaintext never touches the database.

**Session mechanism:** Stateless JWT. No server-side session store. Every request
carries the token; the server validates signature and expiry independently.

---

## Project Structure Changes

```
GameServer/
├── Program.cs                          ← UPDATED — SignalR, JWT, SQLite, static files, CORS
├── appsettings.json                    ← UPDATED — JWT secret only (no credentials)
├── dashboard.db                        ← GENERATED — SQLite database (gitignored)
├── Data/
│   ├── DashboardDbContext.cs           ← NEW — EF Core DbContext
│   └── DashboardUser.cs               ← NEW — User entity
├── Hubs/
│   └── SecurityHub.cs                 ← NEW — SignalR hub
├── Controllers/
│   ├── SecurityController.cs          ← NEW — /api/security/* endpoints
│   └── AuthController.cs              ← NEW — /api/auth/login endpoint
├── Models/
│   └── DashboardModels.cs             ← NEW — request/response DTOs
├── Scripts/
│   └── seed_admin.py                  ← NEW — one-time admin user creation
└── wwwroot/
    └── dashboard/                     ← NEW — built React SPA copied here
        └── index.html, assets/...
```

### `.gitignore` additions

```
dashboard.db
dashboard.db-shm
dashboard.db-wal
```

---

## NuGet Packages Required

```bash
dotnet add package Microsoft.EntityFrameworkCore.Sqlite
dotnet add package Microsoft.EntityFrameworkCore.Design
dotnet add package BCrypt.Net-Next
dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer
dotnet add package System.IdentityModel.Tokens.Jwt
```

---

## SQLite User Database

### `Data/DashboardUser.cs`

```csharp
public class DashboardUser
{
    public int      Id           { get; set; }
    public string   Username     { get; set; } = string.Empty;
    public string   PasswordHash { get; set; } = string.Empty;  // bcrypt hash
    public string   Role         { get; set; } = "admin";
    public DateTime CreatedAt    { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
}
```

### `Data/DashboardDbContext.cs`

```csharp
public class DashboardDbContext : DbContext
{
    public DashboardDbContext(DbContextOptions<DashboardDbContext> opts)
        : base(opts) { }

    public DbSet<DashboardUser> Users => Set<DashboardUser>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<DashboardUser>()
         .HasIndex(u => u.Username)
         .IsUnique();
    }
}
```

### `Scripts/seed_admin.py`

One-time script to create the admin user before the demo. Run once after the
server has started (so `dashboard.db` exists) or against the db file directly.

```python
#!/usr/bin/env python3
"""
Creates the admin user in the SQLite dashboard database.
Usage: python Scripts/seed_admin.py <password>
Requires: pip install bcrypt
"""
import bcrypt
import sqlite3
import sys
from datetime import datetime, timezone

if len(sys.argv) < 2:
    print("Usage: python seed_admin.py <password>")
    sys.exit(1)

password = sys.argv[1].encode()
hashed   = bcrypt.hashpw(password, bcrypt.gensalt(rounds=12)).decode()
now      = datetime.now(timezone.utc).isoformat()

# Path relative to GameServer/ — adjust if running from elsewhere
db_path = "dashboard.db"

con = sqlite3.connect(db_path)
con.execute("""
    CREATE TABLE IF NOT EXISTS Users (
        Id           INTEGER PRIMARY KEY AUTOINCREMENT,
        Username     TEXT NOT NULL UNIQUE,
        PasswordHash TEXT NOT NULL,
        Role         TEXT NOT NULL DEFAULT 'admin',
        CreatedAt    TEXT NOT NULL,
        LastLoginAt  TEXT
    )
""")
con.execute(
    "INSERT OR REPLACE INTO Users (Username, PasswordHash, Role, CreatedAt) "
    "VALUES (?, ?, 'admin', ?)",
    ("admin", hashed, now)
)
con.commit()
con.close()

print(f"Admin user created successfully.")
print(f"Username: admin")
print(f"Hash stored: {hashed[:20]}... (bcrypt, cost=12)")
```

Run before the demo:
```bash
# From GameServer/ directory
python Scripts/seed_admin.py your-demo-password
```

---

## `appsettings.json` — JWT Config Only

Credentials are no longer in config — they live in the database.

```json
{
  "Dashboard": {
    "JwtSecret":        "minimum-32-char-secret-change-this-before-demo",
    "JwtExpiryMinutes": 60
  },
  "ConnectionStrings": {
    "Dashboard": "Data Source=dashboard.db"
  }
}
```

---

## JWT Authentication

### `AuthController.cs`

```csharp
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly DashboardDbContext _db;
    private readonly IConfiguration    _config;
    private readonly ILogger<AuthController> _logger;

    public AuthController(DashboardDbContext db,
                          IConfiguration config,
                          ILogger<AuthController> logger)
    {
        _db     = db;
        _config = config;
        _logger = logger;
    }

    // POST /api/auth/login
    // Body: { "username": "admin", "password": "plaintext" }
    // Password is sent as plaintext over HTTPS — TLS encrypts in transit.
    // Server verifies against bcrypt hash stored in SQLite.
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        // Look up user by username
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Username == req.Username);

        // Verify password against bcrypt hash
        // BCrypt.Verify is constant-time — no timing attack vulnerability
        if (user == null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
        {
            _logger.LogWarning("[Auth] Failed login attempt for username: {Username}",
                req.Username);
            // Return same error for both "user not found" and "wrong password"
            // — prevents username enumeration
            return Unauthorized(new { error = "Invalid credentials" });
        }

        // Update last login timestamp
        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("[Auth] Successful login: {Username}", user.Username);
        return Ok(new {
            token     = GenerateJwt(user.Username, user.Role),
            expiresIn = _config.GetValue<int>("Dashboard:JwtExpiryMinutes") * 60
        });
    }

    private string GenerateJwt(string username, string role)
    {
        var secret  = _config["Dashboard:JwtSecret"]!;
        var key     = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds   = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expiry  = DateTime.UtcNow.AddMinutes(
                        _config.GetValue<int>("Dashboard:JwtExpiryMinutes"));

        var token = new JwtSecurityToken(
            issuer:   "ATITFinalProject",
            audience: "dashboard",
            claims: new[]
            {
                new Claim(ClaimTypes.Name,   username),
                new Claim(ClaimTypes.Role,   role),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            },
            expires:           expiry,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public record LoginRequest(string Username, string Password);
```

### Key security decisions documented in code

- **Same error message** for wrong username and wrong password — prevents username
  enumeration attacks
- **`BCrypt.Verify` is constant-time** — no timing side channel
- **`Jti` claim** (JWT ID) — unique per token, useful if token blacklisting is
  added later
- **Role claim included** — ready for multi-role expansion without JWT schema change

---

## SignalR Hub

### `SecurityHub.cs`

```csharp
[Authorize]   // requires valid JWT
public class SecurityHub : Hub
{
    // Server-to-client push methods (called from RuleBasedIntrusionDetector):
    // OnAlert(IntrusionAlert alert)
    // OnDeviceStateChanged(DeviceSecurityState state)

    // Client connects with JWT in query string (WebSocket limitation):
    // new HubConnectionBuilder()
    //   .withUrl("/hubs/security", { accessTokenFactory: () => token })
    //   .build()
}
```

### JWT extraction for SignalR WebSocket connections

```csharp
// In Program.cs — JWT middleware must extract token from query string
// because WebSocket connections cannot set Authorization headers in browsers
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                var path  = ctx.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(token) &&
                    path.StartsWithSegments("/hubs/security"))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer              = "ATITFinalProject",
            ValidAudience            = "dashboard",
            IssuerSigningKey         = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(
                    builder.Configuration["Dashboard:JwtSecret"]!))
        };
    });
```

---

## Security REST API

### `SecurityController.cs`

All endpoints require `[Authorize]`.

```csharp
[ApiController]
[Route("api/security")]
[Authorize]
public class SecurityController : ControllerBase
{
    private readonly IIntrusionDetector      _ids;
    private readonly IHubContext<SecurityHub> _hub;

    // GET /api/security/status
    // Full snapshot: device states + recent alert feed
    // Used on dashboard initial load
    [HttpGet("status")]
    public IActionResult GetStatus() => Ok(new {
        devices   = _ids.GetAllDeviceStates(),
        alerts    = _ids.GetRecentAlerts(),
        timestamp = DateTimeOffset.UtcNow
    });

    // GET /api/security/devices
    [HttpGet("devices")]
    public IActionResult GetDevices() => Ok(_ids.GetAllDeviceStates());

    // GET /api/security/alerts
    [HttpGet("alerts")]
    public IActionResult GetAlerts() => Ok(_ids.GetRecentAlerts());

    // POST /api/security/devices/{deviceId}/quarantine
    [HttpPost("devices/{deviceId}/quarantine")]
    public async Task<IActionResult> Quarantine(string deviceId)
    {
        await _ids.ReportAsync(new IntrusionAlert {
            DeviceId         = deviceId,
            AlertType        = AlertType.RateExceeded,
            Detail           = "Manually quarantined via dashboard",
            Timestamp        = DateTimeOffset.UtcNow,
            ShouldQuarantine = true
        });
        return Ok(new { deviceId, quarantined = true });
    }

    // POST /api/security/devices/{deviceId}/unquarantine
    [HttpPost("devices/{deviceId}/unquarantine")]
    public async Task<IActionResult> Unquarantine(string deviceId)
    {
        _ids.Unquarantine(deviceId);
        await _hub.Clients.All.SendAsync("OnDeviceStateChanged",
            _ids.GetAllDeviceStates()
                .FirstOrDefault(d => d.DeviceId == deviceId));
        return Ok(new { deviceId, quarantined = false });
    }

    // POST /api/security/alerts/clear
    [HttpPost("alerts/clear")]
    public IActionResult ClearAlerts()
    {
        _ids.ClearAlerts();
        return Ok();
    }
}
```

---

## Static File Serving (React SPA)

```csharp
// In Program.cs
app.UseStaticFiles(new StaticFileOptions {
    FileProvider = new PhysicalFileProvider(
        Path.Combine(builder.Environment.ContentRootPath, "wwwroot/dashboard")),
    RequestPath = "/dashboard"
});

// Serve index.html for all /dashboard/* client-side routes
app.MapFallbackToFile("/dashboard/{**path}", "dashboard/index.html");
```

React app `vite.config.ts` must set `base: '/dashboard/'`.

### Build and copy script

```bash
#!/bin/bash
# Run from ATITFinalProject-dashboard/
npm run build
cp -r dist/* ../ATITFinalProject-server/GameServer/wwwroot/dashboard/
echo "Dashboard deployed"
```

---

## CORS (Development Only)

```csharp
if (app.Environment.IsDevelopment())
{
    builder.Services.AddCors(opts => opts.AddDefaultPolicy(p =>
        p.WithOrigins("http://localhost:5173")  // Vite dev server
         .AllowAnyHeader()
         .AllowAnyMethod()
         .AllowCredentials()));  // required for SignalR
}
```

---

## `Program.cs` Full Addition Summary

```csharp
// Services
builder.Services.AddDbContext<DashboardDbContext>(opts =>
    opts.UseSqlite(builder.Configuration
        .GetConnectionString("Dashboard")!));

builder.Services.AddSignalR();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(/* config above */);
builder.Services.AddAuthorization();
builder.Services.AddControllers();

// Middleware (order matters)
app.UseAuthentication();
app.UseAuthorization();
app.UseStaticFiles(/* dashboard */);

// Database auto-create on startup
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider
         .GetRequiredService<DashboardDbContext>()
         .Database.EnsureCreated();

// Endpoints
app.MapHub<SecurityHub>("/hubs/security");
app.MapControllers();
app.MapFallbackToFile("/dashboard/{**path}", "dashboard/index.html");
app.MapIoTGameEndpoints();
```

---

## Known Limitations (Production Hardening)

This implementation is production-correct for password handling but intentionally
simplified in two areas:

| Limitation | Production solution |
|---|---|
| No MFA | Add TOTP (Google Authenticator) as second factor |
| JWT not revocable | Add token blacklist in Redis or reduce expiry + use refresh tokens |
| Single admin user | Add user management endpoints with role-based access |
| No login rate limiting | Add `AspNetCoreRateLimit` package, limit to 5 attempts/minute |

These are documented as known limitations, not omissions. The bcrypt + JWT pattern
itself is production-correct.

---

## Testing Checklist

```
[ ] python Scripts/seed_admin.py creates dashboard.db with hashed password
[ ] POST /api/auth/login correct credentials → JWT returned
[ ] POST /api/auth/login wrong password → 401, same error as wrong username
[ ] POST /api/auth/login wrong username → 401, same error as wrong password
[ ] GET /api/security/status without JWT → 401
[ ] GET /api/security/status with valid JWT → device list + alert feed
[ ] POST /api/security/devices/esp32-device-1/quarantine → quarantines
[ ] POST /api/security/devices/esp32-device-1/unquarantine → restores
[ ] SignalR connects with JWT → receives OnAlert push
[ ] SignalR connects without JWT → rejected
[ ] GET /dashboard/ → serves React index.html
[ ] Database persists across server restarts (dashboard.db file exists)
[ ] BCrypt.Verify called — not string comparison (verify in debugger if needed)
```


---

## SignalR Hub

### `SecurityHub.cs`

```csharp
[Authorize]   // requires valid JWT in connection query string
public class SecurityHub : Hub
{
    // Server pushes to clients — no client-to-server methods needed
    // Push methods (called from RuleBasedIntrusionDetector):

    // OnAlert(IntrusionAlert alert)
    // OnDeviceStateChanged(DeviceSecurityState state)
    // OnDeviceConnected(string deviceId)

    // Client connects with: new HubConnectionBuilder()
    //   .withUrl("/hubs/security", { accessTokenFactory: () => token })
    //   .build()
}
```

SignalR JWT authentication — the React client sends the JWT as a query parameter
since WebSocket headers are not supported in browsers:

```csharp
// In Program.cs — SignalR JWT extraction
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                // Extract token from query string for SignalR WebSocket connections
                var token = ctx.Request.Query["access_token"];
                var path  = ctx.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(token) &&
                    path.StartsWithSegments("/hubs/security"))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer              = "ATITFinalProject",
            ValidAudience            = "dashboard",
            IssuerSigningKey         = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(
                    builder.Configuration["Dashboard:JwtSecret"]!))
        };
    });
```

---

## Security REST API

### `SecurityController.cs`

All endpoints require `[Authorize]`.

```csharp
[ApiController]
[Route("api/security")]
[Authorize]
public class SecurityController : ControllerBase
{
    private readonly IIntrusionDetector  _ids;
    private readonly IHubContext<SecurityHub> _hub;

    // GET /api/security/status
    // Returns full snapshot: all device states + recent alert feed
    // Used on dashboard initial load (polling fallback also available)
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new {
            devices = _ids.GetAllDeviceStates(),
            alerts  = _ids.GetRecentAlerts(),
            timestamp = DateTimeOffset.UtcNow
        });
    }

    // GET /api/security/devices
    // Returns device state list only
    [HttpGet("devices")]
    public IActionResult GetDevices()
        => Ok(_ids.GetAllDeviceStates());

    // GET /api/security/alerts
    // Returns recent alert feed
    [HttpGet("alerts")]
    public IActionResult GetAlerts()
        => Ok(_ids.GetRecentAlerts());

    // POST /api/security/devices/{deviceId}/quarantine
    // Manually quarantine a device
    [HttpPost("devices/{deviceId}/quarantine")]
    public async Task<IActionResult> Quarantine(string deviceId)
    {
        await _ids.ReportAsync(new IntrusionAlert {
            DeviceId         = deviceId,
            AlertType        = AlertType.RateExceeded,
            Detail           = "Manually quarantined via dashboard",
            Timestamp        = DateTimeOffset.UtcNow,
            ShouldQuarantine = true
        });
        return Ok(new { deviceId, quarantined = true });
    }

    // POST /api/security/devices/{deviceId}/unquarantine
    // Lift quarantine on a device
    [HttpPost("devices/{deviceId}/unquarantine")]
    public async Task<IActionResult> Unquarantine(string deviceId)
    {
        _ids.Unquarantine(deviceId);
        // Notify dashboard via SignalR
        await _hub.Clients.All.SendAsync("OnDeviceStateChanged",
            _ids.GetAllDeviceStates().FirstOrDefault(d => d.DeviceId == deviceId));
        return Ok(new { deviceId, quarantined = false });
    }

    // POST /api/security/alerts/clear
    // Clear the alert feed (cosmetic — does not affect device state)
    [HttpPost("alerts/clear")]
    public IActionResult ClearAlerts()
    {
        _ids.ClearAlerts();
        return Ok();
    }

    // POST /api/admin/start  (existing endpoint — re-exposed here for dashboard)
    // POST /api/admin/end
    // These already exist in AdminEndpoints.cs — dashboard calls them directly
}
```

---

## Static File Serving (React SPA)

The React build output is served by ASP.NET from `wwwroot/dashboard/`. The SPA
handles its own client-side routing. ASP.NET must serve `index.html` for any
path under `/dashboard/*` that isn't a known API route.

```csharp
// In Program.cs
app.UseStaticFiles(new StaticFileOptions {
    FileProvider = new PhysicalFileProvider(
        Path.Combine(builder.Environment.ContentRootPath, "wwwroot/dashboard")),
    RequestPath = "/dashboard"
});

// SPA fallback — serve index.html for all /dashboard/* routes
app.MapFallbackToFile("/dashboard/{**path}", "dashboard/index.html");
```

### Build and copy script (`scripts/deploy-dashboard.sh`)

```bash
#!/bin/bash
# Run from ATITFinalProject-dashboard/
npm run build
cp -r dist/* ../ATITFinalProject-server/GameServer/wwwroot/dashboard/
echo "Dashboard deployed to GameServer/wwwroot/dashboard/"
```

The React app's `vite.config.ts` must set `base: '/dashboard/'` so all asset
paths are prefixed correctly.

---

## CORS Configuration

The React dev server runs on a different port during development. Allow it:

```csharp
// In Program.cs — dev only
if (app.Environment.IsDevelopment())
{
    builder.Services.AddCors(opts => opts.AddDefaultPolicy(p =>
        p.WithOrigins("http://localhost:5173")   // Vite dev server
         .AllowAnyHeader()
         .AllowAnyMethod()
         .AllowCredentials()));   // required for SignalR cookies/auth
}
```

---

## `Program.cs` Full Addition Summary

```csharp
// Services
builder.Services.AddSignalR();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(/* config above */);
builder.Services.AddAuthorization();

// NuGet packages required:
// Microsoft.AspNetCore.SignalR
// Microsoft.AspNetCore.Authentication.JwtBearer
// System.IdentityModel.Tokens.Jwt

// Middleware (order matters)
app.UseAuthentication();
app.UseAuthorization();
app.UseStaticFiles(/* dashboard static files */);

// Endpoints
app.MapHub<SecurityHub>("/hubs/security");
app.MapControllers();
app.MapFallbackToFile("/dashboard/{**path}", "dashboard/index.html");

// Existing IoTGame endpoints
app.MapIoTGameEndpoints();
```
