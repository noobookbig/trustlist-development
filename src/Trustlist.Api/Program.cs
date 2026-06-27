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
using Trustlist.Api.Middleware;
using Trustlist.Api.Signing;

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

// --- Trustlist publisher signing key (MAS-696) ------------------------------
// Fail-closed at boot when the publisher key is missing — mirrors the JWT key
// pattern above. The publisher signs every /v1/{role}/... response in JWS-compact
// form (RFC 7515 + RFC 8032 Ed25519), so a missing key means the entire public
// directory surface is unverifiable and must not be served.
builder.Services.Configure<PublisherSigningOptions>(
    builder.Configuration.GetSection(PublisherSigningOptions.SectionName));
var publisherOptions = builder.Configuration.GetSection(PublisherSigningOptions.SectionName)
    .Get<PublisherSigningOptions>() ?? new PublisherSigningOptions();
if (string.IsNullOrWhiteSpace(publisherOptions.PrivateKeySeedBase64))
{
    throw new InvalidOperationException(
        "TrustlistPublisher:PrivateKeySeedBase64 is missing. " +
        "Set TRUSTLIST_PUBLISHER_PRIVATE_KEY (32-byte base64-encoded Ed25519 seed) in .env. " +
        "Generate with: openssl rand -base64 32");
}
if (string.IsNullOrWhiteSpace(publisherOptions.Kid))
{
    throw new InvalidOperationException(
        "TrustlistPublisher:Kid is missing. " +
        "Set TRUSTLIST_PUBLISHER_KID (e.g. 'tl-publisher-2026-q2') in .env.");
}
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PublisherSigningOptions>>().Value);
builder.Services.AddSingleton<TLPublisherSigner>(sp =>
    new TLPublisherSigner(sp.GetRequiredService<PublisherSigningOptions>()));

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
// In production (and the default docker compose flow) we migrate + seed. In
// tests we set TRUSTLIST_SKIP_SEEDER=1 so the WebApplicationFactory does not
// try to talk to a real SQL Server; the test fixture injects the in-memory
// provider via ConfigureTestServices and seeds its own entities.
if (!string.Equals(
        Environment.GetEnvironmentVariable("TRUSTLIST_SKIP_SEEDER"),
        "1",
        StringComparison.Ordinal))
{
    await DbSeeder.SeedAsync(app.Services, app.Logger);
}

app.UseSwagger();
app.UseSwaggerUI();

// Make the API docs discoverable: browsing the API root (e.g. http://localhost:8080/)
// previously returned a bare 404, so users reported "cannot access Swagger" even though
// the UI was served under /swagger. Redirect the root to the Swagger UI.
app.MapGet("/", () => Results.Redirect("/swagger"));

app.UseCors(CorsPolicy);
app.UseAuthentication();
app.UseAuthorization();

// Sign /v1/{role}/... responses in JWS-compact form (MAS-696). The middleware
// reads the raw JSON bytes the controller wrote, signs them, and replaces the
// response body with `header.payload.signature`. Health, JWKS, Swagger and the
// admin /api/* surfaces are explicitly excluded so they remain inspectable.
app.UseMiddleware<JwsResponseMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "trustlist-api" }));

// RFC 7517 JWKS — publishes the TL publisher's public key so Issuers / Wallets /
// Verifiers can verify signed /v1/{role}/... responses without an out-of-band
// trust anchor. Clients MUST cache for 24h per the v0 spec caching rule.
app.MapGet("/.well-known/trustlist-jwks.json", (TLPublisherSigner signer) => Results.Json(
    new { keys = new[] { signer.PublicJwk } },
    contentType: "application/json"));

app.MapControllers();

app.Run();

public partial class Program { }
