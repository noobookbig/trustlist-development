using System.Security.Cryptography;
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

// Hold an ephemeral dev-only publisher seed until the logger is wired up. We
// resolve it lazily in the host-startup phase (after `var app = builder.Build()`
// runs) so we can log a loud warning through the configured ILogger pipeline.
string? ephemeralDevPublisherSeedBase64 = null;
string ephemeralDevPublisherKid = "tl-publisher-dev-ephemeral";

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

var seedLooksLikeEnvExamplePlaceholder = publisherOptions.PrivateKeySeedBase64
    .StartsWith("CHANGE_ME_", StringComparison.Ordinal);

byte[]? existingSeedBytes = null;
string? existingSeedError = null;
if (!string.IsNullOrWhiteSpace(publisherOptions.PrivateKeySeedBase64) && !seedLooksLikeEnvExamplePlaceholder)
{
    try
    {
        existingSeedBytes = Convert.FromBase64String(publisherOptions.PrivateKeySeedBase64);
        if (existingSeedBytes.Length != 32)
        {
            existingSeedError =
                $"TrustlistPublisher:PrivateKeySeedBase64 must decode to exactly 32 bytes (Ed25519 seed). Got {existingSeedBytes.Length}.";
            existingSeedBytes = null;
        }
    }
    catch (FormatException ex)
    {
        existingSeedError = "TrustlistPublisher:PrivateKeySeedBase64 is not valid base64. " + ex.Message;
    }
}

if (existingSeedBytes is null && existingSeedError is not null)
{
    // Seed was provided but is malformed. Production/Staging/Testing fail
    // closed; Development regenerates a fresh ephemeral key so the dev loop
    // does not require manual intervention.
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(existingSeedError);
    }
    var seedBytes = new byte[32];
    RandomNumberGenerator.Fill(seedBytes);
    ephemeralDevPublisherSeedBase64 = Convert.ToBase64String(seedBytes);
    publisherOptions.PrivateKeySeedBase64 = ephemeralDevPublisherSeedBase64;
}
else if (existingSeedBytes is null)
{
    // No usable seed (missing, empty, or the .env.example placeholder).
    //
    // MAS-718: dev-mode auto-gen so a clean `dotnet run` / `docker compose up`
    // works out of the box without requiring a real `.env` first. The
    // placeholder ("CHANGE_ME_...") from `.env.example` is treated as "missing"
    // here — copying the example to `.env` and running compose should just
    // work, not fail with a stack trace.
    //
    // Production / Staging / Testing are unchanged: a real TL signing key is a
    // hard deployment prerequisite (one-way-door — see ARCHITECTURE.md §7) and
    // the API refuses to boot so a misconfigured prod can't publish under an
    // ephemeral key.
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "TrustlistPublisher:PrivateKeySeedBase64 is missing. " +
            "Set TRUSTLIST_PUBLISHER_PRIVATE_KEY (32-byte base64-encoded Ed25519 seed) in .env. " +
            "Generate with: openssl rand -base64 32");
    }

    var seedBytes = new byte[32];
    RandomNumberGenerator.Fill(seedBytes);
    ephemeralDevPublisherSeedBase64 = Convert.ToBase64String(seedBytes);
    publisherOptions.PrivateKeySeedBase64 = ephemeralDevPublisherSeedBase64;
}
else if (string.IsNullOrWhiteSpace(publisherOptions.Kid))
{
    throw new InvalidOperationException(
        "TrustlistPublisher:Kid is missing. " +
        "Set TRUSTLIST_PUBLISHER_KID (e.g. 'tl-publisher-2026-q2') in .env.");
}

if (ephemeralDevPublisherSeedBase64 is not null && string.IsNullOrWhiteSpace(publisherOptions.Kid))
{
    publisherOptions.Kid = ephemeralDevPublisherKid;
}

builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PublisherSigningOptions>>().Value);
// Construct the signer directly with the dev-mode seed when present, so the
// value path is unambiguous regardless of IOptions / PostConfigure ordering.
builder.Services.AddSingleton<TLPublisherSigner>(_ =>
    new TLPublisherSigner(new PublisherSigningOptions
    {
        PrivateKeySeedBase64 = ephemeralDevPublisherSeedBase64 ?? publisherOptions.PrivateKeySeedBase64,
        Kid = ephemeralDevPublisherSeedBase64 is not null && string.IsNullOrWhiteSpace(publisherOptions.Kid)
            ? ephemeralDevPublisherKid
            : publisherOptions.Kid,
    }));

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
builder.Services.AddSwaggerGen(o =>
{
    // MAS-727: document the /v1 directory responses as signed (application/jwt)
    // so Swagger stops implying they are unsigned JSON. Documentation-only.
    o.OperationFilter<Trustlist.Api.Swagger.SignedResponseOperationFilter>();

    // MAS-727 (user request, accepted 2026-06-29): hide the legacy /api/* admin +
    // auth surface from Swagger UI so the published docs show only the signed
    // public /v1 directory surface. The endpoints REMAIN fully routable — the
    // Blazor frontend still calls /api/auth/* and /api/trustlist/* — they are just
    // not listed in the OpenAPI document. Documentation-only; no runtime change.
    o.DocInclusionPredicate((_, apiDesc) =>
    {
        var path = "/" + (apiDesc.RelativePath ?? string.Empty);
        return !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
    });
});

var app = builder.Build();

if (ephemeralDevPublisherSeedBase64 is not null)
{
    // MAS-718: surface a loud, unmissable warning so dev never mistakes an
    // ephemeral key for a real TL publisher identity. Also pin the kid so the
    // JWKS advertises a recognizable value, not the empty one from
    // appsettings.json.
    app.Logger.LogWarning(
        "MAS-718: TRUSTLIST_PUBLISHER_PRIVATE_KEY is missing or set to the " +
        ".env.example placeholder. Generating an EPHEMERAL dev-only Ed25519 " +
        "seed at startup (kid={Kid}). This key is regenerated on every " +
        "restart, so /v1/{{role}} signatures and /.well-known/trustlist-jwks.json " +
        "will not survive a process bounce. NEVER deploy this code path to a " +
        "shared or production environment — set TRUSTLIST_PUBLISHER_PRIVATE_KEY " +
        "(openssl rand -base64 32) and TRUSTLIST_PUBLISHER_KID in .env.",
        publisherOptions.Kid);
}

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

// MAS-696 follow-up — version endpoint. Returns the API's <Version> from the
// csproj plus the build SHA when set via the GIT_SHA env var. Convention: every
// code change bumps the version, see AGENTS.md for the rule.
app.MapGet("/version", Trustlist.Api.VersionEndpoint.Handler);

// RFC 7517 JWKS — publishes the TL publisher's public key so Issuers / Wallets /
// Verifiers can verify signed /v1/{role}/... responses without an out-of-band
// trust anchor. Clients MUST cache for 24h per the v0 spec caching rule.
app.MapGet("/.well-known/trustlist-jwks.json", (TLPublisherSigner signer) => Results.Json(
    new { keys = new[] { signer.PublicJwk } },
    contentType: "application/json"));

app.MapControllers();

app.Run();

public partial class Program { }
