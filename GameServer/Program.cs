using System.Text;
using GameServer.Data;
using GameServer.Hubs;
using GameServer.Mapping;
using GameServer.Security;
using IoTGame.Core.Abstractions;
using IoTGame.Core.Auth;
using IoTGame.Core.Endpoints;
using IoTGame.Core.Extensions;
using IoTGame.Core.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ── Required secrets (see docs/setup.md in ATITFinalProject) ──────────────────
// HS256 needs a key of at least 256 bits; fail at startup instead of on first login.
var jwtSecret = builder.Configuration["Dashboard:JwtSecret"];
if (string.IsNullOrWhiteSpace(jwtSecret) || Encoding.UTF8.GetByteCount(jwtSecret) < 32)
    throw new InvalidOperationException(
        "Dashboard:JwtSecret is missing or shorter than 32 bytes. Set it with " +
        "`dotnet user-secrets set \"Dashboard:JwtSecret\" \"$(openssl rand -base64 48)\"` " +
        "or the Dashboard__JwtSecret environment variable.");

// Relative cert paths in appsettings.json resolve against the project folder, not the shell's cwd.
string? ResolvePath(string key) =>
    builder.Configuration[key] is { Length: > 0 } p
        ? Path.GetFullPath(p, builder.Environment.ContentRootPath)
        : null;

// ── IoTGame core ──────────────────────────────────────────────────────────────
builder.Services.AddIoTGame(options =>
{
    options.MqttHost           = builder.Configuration["Mqtt:Host"]     ?? "localhost";
    options.MqttPort           = builder.Configuration.GetValue<int>("Mqtt:Port", 8883);
    options.MqttUser           = builder.Configuration["Mqtt:Username"];
    options.MqttPassword       = builder.Configuration["Mqtt:Password"];
    options.BufferWindowMs     = builder.Configuration.GetValue<int>("IoTGame:BufferWindowMs", 0);
    options.MaxEventsPerSecond = builder.Configuration.GetValue<int>("IoTGame:MaxEventsPerSecond", 60);
    options.AdminApiKey         = builder.Configuration["IoTGame:AdminApiKey"];
    options.MqttUseTls          = builder.Configuration.GetValue<bool>("Mqtt:UseTls", false);
    options.MqttCaFile          = ResolvePath("Mqtt:CaFile");
    options.MqttClientCertFile  = ResolvePath("Mqtt:ClientCertFile");
    options.MqttClientKeyFile   = ResolvePath("Mqtt:ClientKeyFile");
    options.GrpcClientCertFile    = ResolvePath("Unity:GrpcClientCertFile");
    options.GrpcClientKeyFile     = ResolvePath("Unity:GrpcClientKeyFile");
    options.DefaultUnityEndpoint  = builder.Configuration["Unity:GrpcEndpoint"];
})
.WithSessionStore<InMemorySessionStore>()
.WithDeviceAuthenticator<AllowAllDeviceAuthenticator>()
.WithAdminAuthenticator<ApiKeyAdminAuthenticator>()
.WithPlayerMapper<StaticDeviceRegistryMapper>()
.WithSessionResolver<StaticSessionResolver>()
.WithIntrusionDetector<RuleBasedIntrusionDetector>()
.WithSessionTokenAuth();

// ── Phase 6: SQLite + EF Core ─────────────────────────────────────────────────
builder.Services.AddDbContext<DashboardDbContext>(opts =>
    opts.UseSqlite(builder.Configuration.GetConnectionString("Dashboard")!));

// ── Phase 6: SignalR ──────────────────────────────────────────────────────────
builder.Services.AddSignalR();

// ── Phase 6: JWT authentication ───────────────────────────────────────────────
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        // WebSocket connections cannot set Authorization headers in browsers,
        // so SignalR passes the JWT as a query parameter instead.
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
                Encoding.UTF8.GetBytes(jwtSecret))
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddControllers();

// ── Phase 6: CORS (dev only — allow Vite dev server) ─────────────────────────
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(opts => opts.AddDefaultPolicy(p =>
        p.WithOrigins("http://localhost:5173")
         .AllowAnyHeader()
         .AllowAnyMethod()
         .AllowCredentials()));
}

// ─────────────────────────────────────────────────────────────────────────────
var app = builder.Build();

// Phase 6: Auto-create SQLite database on startup
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider
         .GetRequiredService<DashboardDbContext>()
         .Database.EnsureCreated();

// ── Middleware (order matters) ────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
    app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

// Phase 6: Serve React SPA from wwwroot/dashboard/ at /dashboard
var dashboardPath = Path.Combine(builder.Environment.ContentRootPath, "wwwroot", "dashboard");
if (Directory.Exists(dashboardPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(dashboardPath),
        RequestPath  = "/dashboard"
    });
    app.MapFallbackToFile("/dashboard/{**path}", "dashboard/index.html");
}

// ── Endpoints ─────────────────────────────────────────────────────────────────
app.MapHub<SecurityHub>("/hubs/security");
app.MapControllers();
app.MapIoTGameEndpoints();

app.Run();
