using System.Text.Json;
using Trustlist.Web.Services;
using Xunit;

namespace Trustlist.Tests;

/// <summary>
/// Regression tests for MAS-687 ("ไม่เหมือน spec ใน research"): the admin/create
/// path must carry the role-specific key material the canonical
/// openapi-trustlist-directory.yaml requires:
///   - Issuer + Wallet-Provider: trust_anchors[] (signing keys / "pubkey")
///   - Verifier: client_identifiers[] (its identity / key binding)
///   - Wallet-Provider: WIA fields (wia_status_list_uri, period, attestation_format[])
///
/// Before MAS-687, CreateEntityModel had none of these fields, so an admin could
/// never populate them — exactly the reported gap (wallet had no "add WIA", issuer
/// and verifier had no pubkey).
/// </summary>
public class RoleSpecificFieldsTests
{
    // The Web HttpClient serializes request bodies with JsonSerializerDefaults.Web,
    // and each property is pinned with [JsonPropertyName] to the snake_case wire shape.
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Issuer_Create_Serializes_TrustAnchors_Pubkey()
    {
        var jwk = JsonDocument.Parse(
            """{"kty":"EC","crv":"P-256","use":"sig","alg":"ES256","kid":"k-2026-q2"}""").RootElement;

        var model = new CreateEntityModel(
            Role: "Issuer",
            EntityId: "https://issuer.example.go.th",
            EntityName: "Example Issuer",
            EntityLegalName: "Example Issuer Co.",
            Jurisdiction: "TH",
            RegistrationNumber: "TH-ISS-9001",
            Status: "Valid",
            CertificationScheme: null,
            CertificateId: null,
            Scope: "UniversityDegreeCredential",
            SecurityEmail: "security@issuer.example.go.th",
            NextUpdate: null,
            TrustAnchors: new[]
            {
                new TrustAnchorModel("k-2026-q2", "jwk", "active", null, null, jwk, null)
            });

        var json = JsonSerializer.Serialize(model, WireOptions);

        using var doc = JsonDocument.Parse(json);
        var anchors = doc.RootElement.GetProperty("trust_anchors");
        Assert.Equal(1, anchors.GetArrayLength());
        var anchor = anchors[0];
        Assert.Equal("k-2026-q2", anchor.GetProperty("kid").GetString());
        Assert.Equal("jwk", anchor.GetProperty("format").GetString());
        Assert.Equal("active", anchor.GetProperty("status").GetString());
        // The actual key material round-trips so the directory can publish the pubkey.
        Assert.Equal("EC", anchor.GetProperty("jwk").GetProperty("kty").GetString());
        Assert.Equal("ES256", anchor.GetProperty("jwk").GetProperty("alg").GetString());
    }

    [Fact]
    public void Verifier_Create_Serializes_ClientIdentifiers_Pubkey()
    {
        var model = new CreateEntityModel(
            Role: "Verifier",
            EntityId: "https://verifier.example.co.th",
            EntityName: "Example Verifier",
            EntityLegalName: null,
            Jurisdiction: "TH",
            RegistrationNumber: "TH-VER-9001",
            Status: "Valid",
            CertificationScheme: null,
            CertificateId: null,
            Scope: null,
            SecurityEmail: "security@verifier.example.co.th",
            NextUpdate: null,
            ClientIdentifiers: new[]
            {
                new ClientIdentifierModel("x509_san_dns", "verifier.example.co.th")
            });

        var json = JsonSerializer.Serialize(model, WireOptions);

        using var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.GetProperty("client_identifiers");
        Assert.Equal(1, ids.GetArrayLength());
        Assert.Equal("x509_san_dns", ids[0].GetProperty("prefix").GetString());
        Assert.Equal("verifier.example.co.th", ids[0].GetProperty("value").GetString());
    }

    [Fact]
    public void WalletProvider_Create_Serializes_Wia_And_TrustAnchors()
    {
        var jwk = JsonDocument.Parse(
            """{"kty":"EC","crv":"P-256","use":"sig","alg":"ES256","kid":"k-wp-2026"}""").RootElement;

        var model = new CreateEntityModel(
            Role: "WalletProvider",
            EntityId: "https://wallet.example.co.th",
            EntityName: "Example Wallet",
            EntityLegalName: "Example Wallet Co.",
            Jurisdiction: "TH",
            RegistrationNumber: "TH-WP-9001",
            Status: "Valid",
            CertificationScheme: null,
            CertificateId: null,
            Scope: null,
            SecurityEmail: "security@wallet.example.co.th",
            NextUpdate: null,
            TrustAnchors: new[]
            {
                new TrustAnchorModel("k-wp-2026", "jwk", "active", null, null, jwk, null)
            },
            WiaStatusListUri: "https://wallet.example.co.th/wia-statuslist/v1.jwt",
            WiaRevocationMaintenancePeriodDays: 14,
            WiaAttestationFormat: new[] { "oauth-client-attestation+jwt" });

        var json = JsonSerializer.Serialize(model, WireOptions);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // WIA fields the admin can now add.
        Assert.Equal(
            "https://wallet.example.co.th/wia-statuslist/v1.jwt",
            root.GetProperty("wia_status_list_uri").GetString());
        Assert.Equal(14, root.GetProperty("wia_revocation_maintenance_period_days").GetInt32());
        Assert.Equal(
            "oauth-client-attestation+jwt",
            root.GetProperty("wia_attestation_format")[0].GetString());
        // WP signing key (pubkey) is also carried.
        Assert.Equal(1, root.GetProperty("trust_anchors").GetArrayLength());
        Assert.Equal("k-wp-2026", root.GetProperty("trust_anchors")[0].GetProperty("kid").GetString());
    }

    [Fact]
    public void TrustlistEntityModel_Roundtrips_RoleSpecific_Fields_From_Api()
    {
        // Shape the API now returns from TrustlistEntityDto.From(...) (snake_case wire).
        var apiJson = """
        {
          "id": 1,
          "role": "WalletProvider",
          "entity_id": "https://wallet.example.co.th",
          "entity_name": "Example Wallet",
          "entity_legal_name": "Example Wallet Co.",
          "jurisdiction": "TH",
          "registration_number": "TH-WP-9001",
          "status": "Valid",
          "certification_scheme": null,
          "certificate_id": null,
          "scope": null,
          "security_email": "security@wallet.example.co.th",
          "next_update": null,
          "created_at": "2026-06-27T00:00:00+00:00",
          "updated_at": "2026-06-27T00:00:00+00:00",
          "trust_anchors": [
            { "kid": "k-wp-2026", "format": "jwk", "status": "active", "jwk": { "kty": "EC" } }
          ],
          "wia_status_list_uri": "https://wallet.example.co.th/wia-statuslist/v1.jwt",
          "wia_revocation_maintenance_period_days": 14,
          "wia_attestation_format": ["oauth-client-attestation+jwt"]
        }
        """;

        var model = JsonSerializer.Deserialize<TrustlistEntityModel>(apiJson, WireOptions)!;

        Assert.NotNull(model.TrustAnchors);
        Assert.Single(model.TrustAnchors!);
        Assert.Equal("k-wp-2026", model.TrustAnchors![0].Kid);
        Assert.Equal("https://wallet.example.co.th/wia-statuslist/v1.jwt", model.WiaStatusListUri);
        Assert.Equal(14, model.WiaRevocationMaintenancePeriodDays);
        Assert.NotNull(model.WiaAttestationFormat);
        Assert.Equal("oauth-client-attestation+jwt", model.WiaAttestationFormat![0]);
    }
}
