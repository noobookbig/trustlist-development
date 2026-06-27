using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Trustlist.Tests;

/// <summary>
/// MAS-718 acceptance tests — Development-mode auto-gen of an ephemeral
/// Ed25519 seed when <c>TRUSTLIST_PUBLISHER_PRIVATE_KEY</c> is missing or
/// still the <c>.env.example</c> placeholder.
///
/// Contract (see Program.cs):
///   - ASPNETCORE_ENVIRONMENT=Development → missing or placeholder seed
///     triggers an ephemeral auto-generated key + a logged warning.
///   - ASPNETCORE_ENVIRONMENT=Production/Staging/Testing → fail-closed at boot
///     (covered by <see cref="JwsResponseSigningTests.PublisherKeyMissing_BootFailsClosed"/>).
/// </summary>
public class DevPublisherAutoGenTests
{
    private const string PlaceholderSeed = "CHANGE_ME_32_byte_base64_Ed25519_seed";
    private const string DevEphemeralKid = "tl-publisher-dev-ephemeral";

    // These must be cleared at the OS level too — the host's env vars from the
    // shared fixture (TrustlistApiFactory) and from other tests in this
    // collection leak into ConfigureAppConfiguration when the default env-var
    // provider runs before our in-memory provider. The in-memory provider
    // overrides `Configuration["…"]` lookups but NOT the IConfiguration /
    // binder path that ASP.NET uses for `builder.Configuration.GetSection`.
    // Clearing the env vars directly is the same workaround the existing
    // PublisherKeyMissing_BootFailsClosed test uses.
    private static void ResetPublisherEnv(string? seed, string? kid)
    {
        Environment.SetEnvironmentVariable("TrustlistPublisher__PrivateKeySeedBase64", seed);
        Environment.SetEnvironmentVariable("TrustlistPublisher__Kid", kid);
        Environment.SetEnvironmentVariable("TRUSTLIST_PUBLISHER_PRIVATE_KEY", seed);
        Environment.SetEnvironmentVariable("TRUSTLIST_PUBLISHER_KID", kid);
    }

    private static void RestoreSharedFixtureEnv()
    {
        Environment.SetEnvironmentVariable("TrustlistPublisher__PrivateKeySeedBase64", TrustlistApiFactory.TestPublisherSeedBase64);
        Environment.SetEnvironmentVariable("TrustlistPublisher__Kid", TrustlistApiFactory.TestPublisherKid);
        Environment.SetEnvironmentVariable("TRUSTLIST_PUBLISHER_PRIVATE_KEY", TrustlistApiFactory.TestPublisherSeedBase64);
        Environment.SetEnvironmentVariable("TRUSTLIST_PUBLISHER_KID", TrustlistApiFactory.TestPublisherKid);
    }

    private static WebApplicationFactory<Program> BuildFactory(
        string environment,
        string? seedValue,
        string? kidValue = null)
    {
        ResetPublisherEnv(seedValue, kidValue);

        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment(environment);
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    var dict = new Dictionary<string, string?>
                    {
                        ["Jwt:Key"] = TrustlistApiFactory.TestJwtKey,
                        ["Jwt:Issuer"] = "trustlist-api",
                        ["Jwt:Audience"] = "trustlist-web",
                        ["ConnectionStrings:Default"] = TrustlistApiFactory.TestConnectionString,
                        ["TrustlistPublisher:PrivateKeySeedBase64"] = seedValue,
                        ["TrustlistPublisher:Kid"] = kidValue,
                    };
                    cfg.AddInMemoryCollection(dict);
                });
            });
        return factory;
    }

    // ------------------------------------------------------------------ Test 1
    [Fact]
    public async Task Dev_MissingSeed_Boots_WithEphemeralKid()
    {
        await using var factory = BuildFactory("Development", seedValue: "");
        try
        {
            using var client = factory.CreateClient();

            // /health succeeds — the API booted and served traffic.
            var health = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            // JWKS advertises the well-known ephemeral kid.
            var jwksResp = await client.GetAsync("/.well-known/trustlist-jwks.json");
            Assert.Equal(HttpStatusCode.OK, jwksResp.StatusCode);

            using var doc = JsonDocument.Parse(await jwksResp.Content.ReadAsStringAsync());
            var keys = doc.RootElement.GetProperty("keys");
            Assert.Equal(1, keys.GetArrayLength());
            Assert.Equal(DevEphemeralKid, keys[0].GetProperty("kid").GetString());
        }
        finally
        {
            RestoreSharedFixtureEnv();
        }
    }

    // ------------------------------------------------------------------ Test 2
    [Fact]
    public async Task Dev_PlaceholderSeed_Boots_WithEphemeralKid()
    {
        // The .env.example ships the value "CHANGE_ME_..." — copying the
        // example to .env and running compose MUST NOT fail.
        await using var factory = BuildFactory("Development", seedValue: PlaceholderSeed);
        try
        {
            using var client = factory.CreateClient();

            var jwksResp = await client.GetAsync("/.well-known/trustlist-jwks.json");
            Assert.Equal(HttpStatusCode.OK, jwksResp.StatusCode);

            using var doc = JsonDocument.Parse(await jwksResp.Content.ReadAsStringAsync());
            var keys = doc.RootElement.GetProperty("keys");
            Assert.Equal(1, keys.GetArrayLength());
            Assert.Equal(DevEphemeralKid, keys[0].GetProperty("kid").GetString());
        }
        finally
        {
            RestoreSharedFixtureEnv();
        }
    }

    // ------------------------------------------------------------------ Test 3
    [Fact]
    public async Task Dev_EphemeralSeedIsFreshPerRestart()
    {
        // The auto-gen seed is regenerated on every restart, so the public key
        // in the JWKS differs between two consecutive factory instantiations.
        try
        {
            string x1, x2;
            await using (var f1 = BuildFactory("Development", seedValue: ""))
            {
                using var c1 = f1.CreateClient();
                var r1 = await c1.GetAsync("/.well-known/trustlist-jwks.json");
                using var d1 = JsonDocument.Parse(await r1.Content.ReadAsStringAsync());
                x1 = d1.RootElement.GetProperty("keys")[0].GetProperty("x").GetString()!;
                Assert.False(string.IsNullOrEmpty(x1));
            }

            await using (var f2 = BuildFactory("Development", seedValue: ""))
            {
                using var c2 = f2.CreateClient();
                var r2 = await c2.GetAsync("/.well-known/trustlist-jwks.json");
                using var d2 = JsonDocument.Parse(await r2.Content.ReadAsStringAsync());
                x2 = d2.RootElement.GetProperty("keys")[0].GetProperty("x").GetString()!;
            }

            Assert.NotEqual(x1, x2);
        }
        finally
        {
            RestoreSharedFixtureEnv();
        }
    }

    // ------------------------------------------------------------------ Test 4
    [Fact]
    public async Task Dev_WithRealSeed_PreservesUserKid()
    {
        // When the operator sets a real seed AND a real kid, dev mode must
        // pass them through verbatim — auto-gen must NOT clobber them.
        var userKid = "tl-publisher-test-user-2026-q2";
        await using var factory = BuildFactory(
            "Development",
            seedValue: TrustlistApiFactory.TestPublisherSeedBase64,
            kidValue: userKid);
        try
        {
            using var client = factory.CreateClient();

            var jwksResp = await client.GetAsync("/.well-known/trustlist-jwks.json");
            Assert.Equal(HttpStatusCode.OK, jwksResp.StatusCode);

            using var doc = JsonDocument.Parse(await jwksResp.Content.ReadAsStringAsync());
            var keys = doc.RootElement.GetProperty("keys");
            Assert.Equal(1, keys.GetArrayLength());
            Assert.Equal(userKid, keys[0].GetProperty("kid").GetString());
        }
        finally
        {
            RestoreSharedFixtureEnv();
        }
    }

    // ------------------------------------------------------------------ Test 5
    [Fact]
    public async Task Production_MissingSeed_FailsClosed()
    {
        // Mirrors JwsResponseSigningTests.PublisherKeyMissing_BootFailsClosed but
        // explicitly under Production (rather than Testing) to lock down the
        // one-way-door contract: a misconfigured prod MUST NOT boot.
        await using var factory = BuildFactory("Production", seedValue: "");
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                using var c = factory.CreateClient();
                await c.GetAsync("/health");
            });
            Assert.Contains("TrustlistPublisher", ex.Message);
        }
        finally
        {
            RestoreSharedFixtureEnv();
        }
    }
}