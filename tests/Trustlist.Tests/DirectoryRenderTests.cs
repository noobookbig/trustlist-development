using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trustlist.Api.Signing;
using Trustlist.Web.Services;
using Xunit;
using DirectoryPage = Trustlist.Web.Components.Pages.Directory;

namespace Trustlist.Tests;

/// <summary>
/// MAS-728 — the public Directory page must consume the <b>signed</b> <c>/v1</c>
/// surface and only render after JWS verification passes against the published
/// JWKS. These tests build the real <see cref="VerifiedDirectoryClient"/> over a
/// stub handler that signs the canonical role-record JSON with the same
/// <see cref="TLPublisherSigner"/> the API uses (deterministic test seed), so
/// they prove the whole path end-to-end: signed wire JWS -> JWKS verification ->
/// model -> rendered HTML.
///
/// They also extend the MAS-691 render regressions (role-specific key material is
/// still shown) and add the MAS-728 acceptance gate: a tampered signature is
/// rejected and rendered as an explicit "unverified" state, never as trusted data.
/// </summary>
public class DirectoryRenderTests : TestContext
{
    // Deterministic publisher key shared with the API factory.
    private static readonly TLPublisherSigner Signer = new(new PublisherSigningOptions
    {
        PrivateKeySeedBase64 = TrustlistApiFactory.TestPublisherSeedBase64,
        Kid = TrustlistApiFactory.TestPublisherKid,
    });

    /// <summary>
    /// Register a <see cref="VerifiedDirectoryClient"/> that reads a single role's
    /// records (supplied as canonical snake_case JSON for the <c>data</c> array).
    /// The stub signs every <c>/v1/*</c> response and serves the real JWKS, so the
    /// verifier path is exercised for real. Pass <paramref name="tamper"/> to flip
    /// a payload byte and prove the page rejects an invalid signature.
    /// </summary>
    private void UseSignedRole(string role, string dataArrayJson, bool tamper = false)
    {
        var listPayload = $$"""
        { "data": {{dataArrayJson}}, "pagination": { "page": 1, "limit": 50, "total": 1, "has_more": false }, "snapshot_id": "test" }
        """;
        var handler = new SigningStubHandler(role, listPayload, tamper);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var verifier = new JwsVerifier(http, new JwksCache());

        // No pinned kid -> verifier falls back to the sole JWKS key (v0 single publisher).
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        Services.AddSingleton(new VerifiedDirectoryClient(verifier, config));
    }

    [Fact]
    public void Directory_Shows_VerifiedBanner_And_Issuer_TrustAnchor_Pubkey()
    {
        UseSignedRole("issuers", """
        [
          {
            "role": "issuer", "entity_id": "https://issuer.example.go.th",
            "entity_name": "Gov Issuer", "jurisdiction": "TH", "status": "valid",
            "trust_anchors": [
              { "kid": "k-2026-q2", "format": "jwk", "status": "active",
                "jwk": {"kty":"EC","crv":"P-256","alg":"ES256","kid":"k-2026-q2"} }
            ]
          }
        ]
        """);

        var cut = RenderComponent<DirectoryPage>(p => p.Add(c => c.RoleFilter, "Issuer"));

        // The verified banner proves the JWS check passed before any row rendered.
        Assert.Contains("Signature verified", cut.Markup);
        Assert.Contains("Gov Issuer", cut.Markup);
        Assert.Contains("1 key(s)", cut.Markup);

        cut.Find("button.link-btn").Click();
        Assert.Contains("Trust anchors", cut.Markup);
        Assert.Contains("k-2026-q2", cut.Markup);
        Assert.Contains("\"kty\":\"EC\"", cut.Markup);
    }

    [Fact]
    public void Directory_Shows_Verifier_ClientIdentifier()
    {
        UseSignedRole("verifiers", """
        [
          {
            "role": "verifier", "entity_id": "https://verifier.example.co.th",
            "entity_name": "Verifier Co", "jurisdiction": "TH", "status": "valid",
            "client_identifiers": [
              { "prefix": "x509_san_dns", "value": "verifier.example.co.th" }
            ]
          }
        ]
        """);

        var cut = RenderComponent<DirectoryPage>(p => p.Add(c => c.RoleFilter, "Verifier"));
        Assert.Contains("Signature verified", cut.Markup);
        Assert.Contains("1 client-id(s)", cut.Markup);

        cut.Find("button.link-btn").Click();
        Assert.Contains("Client identifiers", cut.Markup);
        Assert.Contains("x509_san_dns", cut.Markup);
        Assert.Contains("verifier.example.co.th", cut.Markup);
    }

    [Fact]
    public void Directory_Shows_WalletProvider_Wia()
    {
        UseSignedRole("wallet-providers", """
        [
          {
            "role": "wallet-provider", "entity_id": "https://wallet.example.co.th",
            "entity_name": "Wallet Co", "jurisdiction": "TH", "status": "valid",
            "wia_status_list_uri": "https://wallet.example.co.th/wia-statuslist/v1.jwt",
            "wia_revocation_maintenance_period_days": 30,
            "wia_attestation_format": ["oauth-client-attestation+jwt"]
          }
        ]
        """);

        var cut = RenderComponent<DirectoryPage>(p => p.Add(c => c.RoleFilter, "WalletProvider"));
        Assert.Contains("Signature verified", cut.Markup);
        Assert.Contains("WIA", cut.Markup);

        cut.Find("button.link-btn").Click();
        Assert.Contains("Wallet Instance Attestation", cut.Markup);
        Assert.Contains("wia-statuslist/v1.jwt", cut.Markup);
        Assert.Contains("oauth-client-attestation+jwt", cut.Markup);
        Assert.Contains("30 day(s)", cut.Markup);
    }

    [Fact]
    public void Directory_TamperedSignature_Is_Rejected_NotRenderedAsTrusted()
    {
        UseSignedRole("issuers", """
        [
          {
            "role": "issuer", "entity_id": "https://evil.example.go.th",
            "entity_name": "Tampered Issuer", "jurisdiction": "TH", "status": "valid",
            "trust_anchors": [ { "kid": "x", "format": "jwk", "status": "active" } ]
          }
        ]
        """, tamper: true);

        var cut = RenderComponent<DirectoryPage>(p => p.Add(c => c.RoleFilter, "Issuer"));

        // Fail closed: the explicit "could not be verified" state is shown and the
        // tampered entity is NEVER rendered as a trusted row.
        Assert.Contains("Signature could not be verified", cut.Markup);
        Assert.DoesNotContain("Tampered Issuer", cut.Markup);
        Assert.DoesNotContain("Signature verified", cut.Markup);
        Assert.Empty(cut.FindAll("table.grid.cards"));
    }

    /// <summary>
    /// Signs (or tampers) the canonical role-record list JSON and serves the JWKS,
    /// mimicking the API's <see cref="Trustlist.Api.Middleware.JwsResponseMiddleware"/>
    /// + JWKS endpoint for the consumer under test.
    /// </summary>
    private sealed class SigningStubHandler(string rolePath, string listPayload, bool tamper)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.Equals("/.well-known/trustlist-jwks.json", StringComparison.OrdinalIgnoreCase))
            {
                // Match the API's /.well-known endpoint, which serves camelCase
                // (Results.Json web defaults) -> kty/crv/kid/x/alg/use.
                var jwks = JsonSerializer.Serialize(
                    new { keys = new[] { Signer.PublicJwk } },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                return Json(jwks, "application/json");
            }

            if (path.StartsWith($"/v1/{rolePath}", StringComparison.OrdinalIgnoreCase))
            {
                var jws = Signer.Sign(Encoding.UTF8.GetBytes(listPayload));
                if (tamper)
                {
                    // Flip a character in the payload segment so the Ed25519 check fails.
                    var parts = jws.Split('.');
                    var p = parts[1].ToCharArray();
                    p[0] = p[0] == 'A' ? 'B' : 'A';
                    parts[1] = new string(p);
                    jws = string.Join('.', parts);
                }
                return Json(jws, "application/jwt");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string body, string contentType) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            });
    }
}
