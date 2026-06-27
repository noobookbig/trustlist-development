using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Trustlist.Api.Auth;
using Trustlist.Api.Data;
using Trustlist.Api.Dtos;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration -------------------------------------------------------
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

var connectionString = builder.Configuration.GetConnectionString("Default");

// --- Fail-closed secret validation (STRIDE: Information Disclosure / Spoofing) ---
// Refuse to boot when JWT signing key is missing or too short: HS256 requires
// >=32 bytes of key material. Also refuse to boot when the DB connection
// string is empty (no fallback password baked into source).
const int MinJwtKeyBytes = 32;
var jwtKeyBytes = string.IsNullOrEmpty(jwtOptions.Key) ? 0 : Encoding.UTF8.GetByteCount(jwtOptions.Key);
if (jwtKeyBytes < MinJwtKeyBytes)
{
    throw new InvalidOperationException(
        $"Jwt:Key is missing or too short ({jwtKeyBytes} bytes; HS256 requires >= {MinJwtKeyBytes}). " +
        "Set JWT_SIGNING_KEY in the environment (see .env.example).");
}
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:Default is missing. " +
        "Set ConnectionStrings__Default (or MSSQL_SA_PASSWORD for docker compose) in the environment.");
}

// --- Database + Identity --------------------------------------------------
builder.Services.AddDbContext<AppDbContext>(opt => opt.UseSqlServer(connectionString));

builder.Services
    .AddIdentityCore<IdentityUser>(opt =>
    {
        opt.Password.RequiredLength = 8;
        opt.Password.RequireNonAlphanumeric = false;
        opt.User.RequireUniqueEmail = true;
    })
    .AddSignInManager()
    .AddEntityFrameworkStores<AppDbContext>();

builder.Services.AddScoped<TokenService>();

// --- Auth -----------------------------------------------------------------
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key))
        };
    });

builder.Services.AddAuthorization();

// --- CORS (frontend container talks to API) -------------------------------
const string CorsPolicy = "frontend";
builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p =>
    p.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true)));

builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        // Align wire format with openapi-trustlist-directory.yaml — snake_case
        // property names + snake_case enum values (status: "valid", format: "jwk", ...).
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        o.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        o.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// --- Migrate + seed on startup -------------------------------------------
await DbSeeder.SeedAsync(app.Services, app.Logger);

app.UseSwagger();
app.UseSwaggerUI();

app.UseCors(CorsPolicy);
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "trustlist-api" }));
app.MapControllers();

app.Run();

public partial class Program { }
