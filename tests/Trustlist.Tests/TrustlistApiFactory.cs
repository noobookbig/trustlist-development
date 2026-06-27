using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trustlist.Api.Data;

namespace Trustlist.Tests;

/// <summary>
/// Test fixture: spins up the real <see cref="Program"/> host with a
/// deterministic publisher signing key, an in-memory EF Core database, and the
/// production seeder skipped. Tests share one fixture per xUnit collection so
/// we boot once per test class (cheap; in-memory only).
///
/// Configuration is set via environment variables BEFORE the host builds, which
/// mirrors how the docker-compose stack provisions the API in production. The
/// JWT key, publisher seed, and publisher kid are all deterministic so the
/// JWS-signed responses are reproducible byte-for-byte.
/// </summary>
public class TrustlistApiFactory : WebApplicationFactory<Program>
{
    public const string TestJwtKey = "test-jwt-signing-key-at-least-32-bytes-long-1234";
    public const string TestPublisherKid = "tl-publisher-test-2026-q2";

    /// <summary>
    /// Deterministic 32-byte Ed25519 seed, base64-encoded. Constant so the
    /// fixture, the signer tests, and any client verifier all use the same key.
    /// DO NOT use this seed in any non-test environment.
    /// </summary>
    public static readonly string TestPublisherSeedBase64 =
        Convert.ToBase64String(new byte[]
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
            0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10,
            0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
            0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f, 0x20,
        });

    public const string TestConnectionString = "Server=(localdb)\\MSSQLLocalDB;Database=Trustlist-Tests;Trusted_Connection=True;TrustServerCertificate=True;";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Wire test config before the host builds. We set env vars so Program.cs
        // sees them on first read (its initial `builder.Configuration` lookup is
        // env-first).
        Environment.SetEnvironmentVariable("TRUSTLIST_SKIP_SEEDER", "1");
        Environment.SetEnvironmentVariable("Jwt__Key", TestJwtKey);
        Environment.SetEnvironmentVariable("Jwt__Issuer", "trustlist-api");
        Environment.SetEnvironmentVariable("Jwt__Audience", "trustlist-web");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", TestConnectionString);
        Environment.SetEnvironmentVariable("TrustlistPublisher__PrivateKeySeedBase64", TestPublisherSeedBase64);
        Environment.SetEnvironmentVariable("TrustlistPublisher__Kid", TestPublisherKid);

        builder.ConfigureAppConfiguration((ctx, cfg) =>
        {
            // Make absolutely sure the test values win over appsettings.json
            // and over any prior env var from the developer shell.
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = TestJwtKey,
                ["Jwt:Issuer"] = "trustlist-api",
                ["Jwt:Audience"] = "trustlist-web",
                ["ConnectionStrings:Default"] = TestConnectionString,
                ["TrustlistPublisher:PrivateKeySeedBase64"] = TestPublisherSeedBase64,
                ["TrustlistPublisher:Kid"] = TestPublisherKid,
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Replace the SQL Server DbContext with the in-memory provider.
            // The production registration (UseSqlServer) ran first; this
            // removes every descriptor for AppDbContext and re-registers it
            // against the in-memory provider.
            var toRemove = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>))
                .ToList();
            foreach (var d in toRemove) services.Remove(d);

            var dbName = $"trustlist-tests-{Guid.NewGuid()}";
            services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        });
    }
}