using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Trustlist.Api.Data;
using Trustlist.Api.Domain;
using Trustlist.Api.Dtos;
using Trustlist.Web.Services;
using Xunit;

namespace Trustlist.Tests;

/// <summary>
/// MAS-696 acceptance gate — an end-to-end consumer (<c>apps/holder-portal</c>
/// analogue, living in <c>Trustlist.Web.Services.JwsVerifier</c>) verifies a
/// signed <c>/v1/verifiers</c> response against the API's JWKS endpoint.
///
/// This is the "wired consumer" smoke the issue body asks for. We use the same
/// <see cref="TrustlistApiFactory"/> as the API tests so the verifier talks to
/// the real signed endpoint through real HTTP, with the real JWKS, with the
/// real BouncyCastle Ed25519 verifier.
/// </summary>
public class JwsVerifierConsumerTests : IClassFixture<TrustlistApiFactory>
{
    private readonly TrustlistApiFactory _api;

    public JwsVerifierConsumerTests(TrustlistApiFactory api)
    {
        _api = api;
        SeedOneVerifier();
    }

    private const string VerifierEntityId = "https://verifier.example.co.th";

    private void SeedOneVerifier()
    {
        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (db.TrustlistEntities.Any(e => e.EntityId == VerifierEntityId)) return;
        db.TrustlistEntities.Add(new TrustlistEntity
        {
            Role = EntityRole.Verifier,
            EntityId = VerifierEntityId,
            EntityName = "Test Verifier",
            EntityLegalName = "Test Verifier Co., Ltd.",
            Jurisdiction = "TH",
            RegistrationNumber = "VREG-001",
            CertificationScheme = "ETSI",
            CertificationSchemeVersion = "1.1",
            CertificateId = "VCERT-001",
            CertificationIssuedAt = DateTimeOffset.UtcNow.AddDays(-30),
            CertificationExpiresAt = DateTimeOffset.UtcNow.AddDays(335),
            CertificationIssuingBody = "ETDA",
            Status = EntityStatus.Valid,
            ClientIdentifiersJson = JsonSerializer.Serialize(
                new[] { new ClientIdentifierDto("x509_san_dns", "verifier.example.co.th") },
                TrustlistJson.Options),
            ScopeAllowedJson = JsonSerializer.Serialize(new[] { "openid" }, TrustlistJson.Options),
            NextUpdate = DateTimeOffset.UtcNow.AddDays(7),
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task HolderPortal_Verifies_SignedVerifierDirectoryResponse()
    {
        // WebApplicationFactory's CreateClient() returns an HttpClient backed by
        // the in-memory TestServer — its BaseAddress is http://localhost/ but
        // the actual request never hits a real socket. Use that HttpClient as
        // the consumer so the JwsVerifier talks through the same in-memory
        // pipeline (this mirrors what Trustlist.Web would do in production,
        // where the API base URL is a real DNS name).
        using var consumerHttp = _api.CreateClient();
        var cache = new JwksCache();
        var verifier = new JwsVerifier(consumerHttp, cache);

        var directoryUrl = $"/v1/verifiers/{Uri.EscapeDataString(VerifierEntityId)}/status";
        var result = await verifier.FetchAndVerifyAsync(
            directoryUrl,
            expectedKid: TrustlistApiFactory.TestPublisherKid);

        Assert.True(result.IsValid, $"Verifier must accept a valid signed response; failure: {result.FailureMessage}");
        Assert.NotNull(result.Header);
        Assert.Equal("EdDSA", result.Header!.Alg);
        Assert.Equal(TrustlistApiFactory.TestPublisherKid, result.Header.Kid);
        Assert.Equal("trustlist-role-record+jwt", result.Header.Typ);

        Assert.NotNull(result.PayloadJson);
        using var doc = JsonDocument.Parse(result.PayloadJson!);
        Assert.Equal(VerifierEntityId, doc.RootElement.GetProperty("entity_id").GetString());
        Assert.True(doc.RootElement.GetProperty("active").GetBoolean());
        Assert.Equal("valid", doc.RootElement.GetProperty("status").GetString());
    }
}