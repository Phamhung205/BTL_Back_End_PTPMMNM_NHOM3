using System.Text;
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

var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Missing Jwt:Key configuration.");

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
    });

builder.Services.AddAuthorization();

var origins = (builder.Configuration["FrontendOrigins"] ?? "http://localhost:3000")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
        policy.WithOrigins(origins)
              .AllowAnyHeader()
              .AllowAnyMethod());
});

var app = builder.Build();

app.UseMiddleware<ApiExceptionMiddleware>();
app.UseCors("Frontend");
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
