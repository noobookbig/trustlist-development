using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Trustlist.Api.Data;
using Trustlist.Api.Domain;
using Xunit;
using Xunit.Abstractions;

namespace Trustlist.Tests;

/// <summary>
/// MAS-725 — smoke harness that boots <see cref="TrustlistApiFactory"/> against
/// a seeded DB and dumps the wire responses from /v1/trustlist, /v1/issuers,
/// /v1/verifiers, /v1/wallet-providers, and /version to the xUnit output
/// stream. The captured output is the evidence we paste into the MAS-725
/// issue comment so reviewers can see the exact shape without spinning up
/// docker compose.
///
/// This is a regular <see cref="FactAttribute"/> test (not a smoke script)
/// because it has to run inside the test assembly to reach the in-memory
/// factory + EF Core wiring; an ad-hoc console project would have to
/// duplicate that wiring.
/// </summary>
public class TrustlistVersionSmokeHarness
{
    private readonly ITestOutputHelper _output;

    public TrustlistVersionSmokeHarness(ITestOutputHelper output) => _output = output;

    [Fact(DisplayName = "MAS-725 smoke: dump /v1/trustlist + /v1/{role} responses")]
    public async Task Dump_Wire_Responses()
    {
        await using var fresh = new TrustlistApiFactory();

        // Seed a deterministic entity set.
        using (var scope = fresh.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TrustlistEntities.AddRange(
                new TrustlistEntity
                {
                    Role = EntityRole.Issuer, EntityId = "https://issuer.mhesi.go.th",
                    EntityName = "MHESI Issuer",
                    EntityLegalName = "Ministry of Higher Education, Science, Research and Innovation",
                    Jurisdiction = "TH", Status = EntityStatus.Valid,
                    CertificationScheme = "ETSI", CertificationSchemeVersion = "1.0",
                    CertificationIssuedAt = DateTimeOffset.UtcNow.AddDays(-30),
                    CertificationExpiresAt = DateTimeOffset.UtcNow.AddDays(335),
                    CertificationIssuingBody = "ETDA",
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                },
                new TrustlistEntity
                {
                    Role = EntityRole.Verifier, EntityId = "https://verifier.example.go.th",
                    EntityName = "Example Verifier",
                    EntityLegalName = "Example Verification Co., Ltd.",
                    Jurisdiction = "TH", Status = EntityStatus.Valid,
                    CertificationScheme = "ETSI", CertificationSchemeVersion = "1.0",
                    CertificationIssuedAt = DateTimeOffset.UtcNow.AddDays(-15),
                    CertificationExpiresAt = DateTimeOffset.UtcNow.AddDays(350),
                    CertificationIssuingBody = "ETDA",
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                },
                new TrustlistEntity
                {
                    Role = EntityRole.WalletProvider, EntityId = "https://wallet.example.go.th",
                    EntityName = "Example Wallet Provider",
                    EntityLegalName = "Example Wallet Solutions Co., Ltd.",
                    Jurisdiction = "TH", Status = EntityStatus.Valid,
                    CertificationScheme = "ETSI", CertificationSchemeVersion = "1.0",
                    CertificationIssuedAt = DateTimeOffset.UtcNow.AddDays(-10),
                    CertificationExpiresAt = DateTimeOffset.UtcNow.AddDays(355),
                    CertificationIssuingBody = "ETDA",
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                });
            db.SaveChanges();
        }

        using var client = fresh.CreateClient();

        // /v1/trustlist (plain JSON)
        var snap = await client.GetAsync("/v1/trustlist");
        _output.WriteLine($"=== GET /v1/trustlist ===");
        _output.WriteLine($"HTTP {(int)snap.StatusCode} {snap.StatusCode}");
        _output.WriteLine($"Content-Type: {snap.Content.Headers.ContentType}");
        var snapBody = await snap.Content.ReadAsStringAsync();
        _output.WriteLine(PrettyJson(snapBody));

        // /v1/{role} (JWS-wrapped; decode payload for human-readable evidence)
        foreach (var path in new[] { "/v1/issuers", "/v1/verifiers", "/v1/wallet-providers" })
        {
            var resp = await client.GetAsync(path);
            _output.WriteLine($"=== GET {path} ===");
            _output.WriteLine($"HTTP {(int)resp.StatusCode} {resp.StatusCode}");
            _output.WriteLine($"Content-Type: {resp.Content.Headers.ContentType}");
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.Content.Headers.ContentType?.MediaType == "application/jwt")
            {
                var parts = body.Split('.');
                var payloadBytes = Base64UrlDecode(parts[1]);
                _output.WriteLine($"JWS payload (decoded from base64url segment 2):");
                _output.WriteLine(PrettyJsonBytes(payloadBytes));
                _output.WriteLine($"JWS header (segment 1): {parts[0]}");
                _output.WriteLine($"Signature length: {Base64UrlDecode(parts[2]).Length} bytes (Ed25519 -> 64)");
            }
            else
            {
                _output.WriteLine(body);
            }
        }

        // /version (binary version, distinct from trustlist_version)
        var version = await client.GetAsync("/version");
        _output.WriteLine("=== GET /version ===");
        _output.WriteLine($"HTTP {(int)version.StatusCode} {version.StatusCode}");
        _output.WriteLine(PrettyJson(await version.Content.ReadAsStringAsync()));

        // Sanity: snapshot's trustlist_version must equal the per-role version.
        var snapshot = JsonDocument.Parse(snapBody).RootElement;
        var snapVersion = snapshot.GetProperty("trustlist_version").GetString();
        foreach (var path in new[] { "/v1/issuers", "/v1/verifiers", "/v1/wallet-providers" })
        {
            var resp = await client.GetAsync(path);
            var body = await resp.Content.ReadAsStringAsync();
            var payload = JsonDocument.Parse(Base64UrlDecode(body.Split('.')[1])).RootElement;
            var listVersion = payload.GetProperty("trustlist_version").GetString();
            Assert.Equal(snapVersion, listVersion);
        }
    }

    private static string PrettyJson(string raw)
    {
        try { return JsonSerializer.Serialize(JsonDocument.Parse(raw), new JsonSerializerOptions { WriteIndented = true }); }
        catch { return raw; }
    }

    private static string PrettyJsonBytes(byte[] raw)
    {
        try { return JsonSerializer.Serialize(JsonDocument.Parse(raw), new JsonSerializerOptions { WriteIndented = true }); }
        catch { return Encoding.UTF8.GetString(raw); }
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4) { case 2: padded += "=="; break; case 3: padded += "="; break; }
        return Convert.FromBase64String(padded);
    }
}