using System.Text;
using System.Security.Cryptography;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using TranslationManager.Api.Data;
using TranslationManager.Api.Services;
using TranslationManager.Api.Support;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();

builder.Services.AddControllers();
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddScoped<ProjectAccessService>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<LocaleService>();
builder.Services.AddScoped<SourceVersionService>();
builder.Services.AddScoped<TranslationService>();
builder.Services.AddScoped<ReleaseService>();
builder.Services.AddScoped<AdminService>();
builder.Services.AddScoped<AccessService>();
builder.Services.AddScoped<TranslationMemoryService>();

var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey) && builder.Environment.IsDevelopment())
{
    // F5 Development: keep a persistent random key outside source control.
    var privateDirectory = Path.Combine(builder.Environment.ContentRootPath, ".local");
    Directory.CreateDirectory(privateDirectory);
    var keyPath = Path.Combine(privateDirectory, "jwt.key");
    try
    {
        using var keyFile = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var keyBytes = Encoding.UTF8.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)));
        keyFile.Write(keyBytes);
    }
    catch (IOException) when (File.Exists(keyPath)) { }
    jwtKey = File.ReadAllText(keyPath).Trim();
    builder.Configuration["Jwt:Key"] = jwtKey;
}
if (string.IsNullOrWhiteSpace(jwtKey) || Encoding.UTF8.GetByteCount(jwtKey) < 32)
    throw new InvalidOperationException("Jwt:Key must be configured with at least 32 bytes.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var principal = context.Principal!;
                if (!long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) { context.Fail("Invalid user."); return; }
                var db = context.HttpContext.RequestServices.GetRequiredService<Db>();
                await using var cn = await db.OpenAsync(context.HttpContext.RequestAborted);
                await using var cmd = cn.CreateCommand();
                cmd.CommandText = "SELECT system_role FROM users WHERE id=@id AND is_active=1";
                cmd.Parameters.AddWithValue("@id", userId);
                var role = await cmd.ExecuteScalarAsync(context.HttpContext.RequestAborted);
                if (role is null) { context.Fail("Account unavailable."); return; }
                var identity = (ClaimsIdentity)principal.Identity!;
                foreach (var claim in identity.FindAll("system_role").ToArray()) identity.RemoveClaim(claim);
                identity.AddClaim(new Claim("system_role", Convert.ToString(role)!));
            }
        };
    });

builder.Services.AddAuthorization(options => options.AddPolicy("SystemAdmin", policy => policy.RequireAuthenticatedUser().RequireClaim("system_role", "admin")));

var origins = (builder.Configuration["FrontendOrigins"] ?? "http://localhost:8081")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(origins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .WithExposedHeaders("Content-Disposition", "X-Content-SHA256"));
    // Public translation is already-approved immutable content; keys can read only one release.
    options.AddPolicy("PublicTranslationApi", policy =>
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod()
              .WithExposedHeaders("X-Content-SHA256"));
});

var app = builder.Build();

if (args.Contains("--bootstrap-admin", StringComparer.Ordinal) || args.Contains("--create-local-admin", StringComparer.Ordinal))
{
    var additional = args.Contains("--create-local-admin", StringComparer.Ordinal);
    if (additional)
    {
        var connection = new MySqlConnector.MySqlConnectionStringBuilder(builder.Configuration.GetConnectionString("Default") ?? throw new InvalidOperationException("Missing Default connection string."));
        if (connection.Server is not ("127.0.0.1" or "localhost" or "::1"))
            throw new InvalidOperationException("--create-local-admin is restricted to a local database.");
    }
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AdminService>().BootstrapAsync(builder.Configuration, allowAdditional: additional);
    Console.WriteLine("System admin bootstrap completed.");
    return;
}

app.UseMiddleware<ApiExceptionMiddleware>();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new
{
    app = "Translation Manager Backend",
    backend = "C# / ASP.NET Core",
    database = "MySQL/MariaDB (XAMPP)",
    status = "READY"
}));

app.MapGet("/health", async (Db db) =>
{
    await using var connection = await db.OpenAsync();
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT 1";
    await cmd.ExecuteScalarAsync();
    return Results.Ok(new { status = "OK", database = "CONNECTED" });
});

app.MapControllers();
app.Run();
