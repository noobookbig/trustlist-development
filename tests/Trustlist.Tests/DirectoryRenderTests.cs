using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Trustlist.Web.Services;
using Xunit;
using DirectoryPage = Trustlist.Web.Components.Pages.Directory;

namespace Trustlist.Tests;

/// <summary>
/// Render regression tests for MAS-691 ("Update Frontend"): the public Directory
/// page must surface the role-specific key material that MAS-687/MAS-688 added to
/// the admin write path. Before this change the Directory only showed name / id /
/// status, so an issuer's signing key, a verifier's client_identifier and a wallet
/// provider's WIA were invisible on the read side — the directory did not actually
/// match the research spec it claims to publish.
///
/// These tests drive the real <see cref="TrustlistApiClient"/> over a stub
/// HttpMessageHandler returning the canonical snake_case directory JSON, so they
/// prove the whole path: wire JSON -> model -> rendered HTML.
/// </summary>
public class DirectoryRenderTests : TestContext
{
    private void UseDirectoryJson(string json)
    {
        var handler = new StubHandler(json);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        Services.AddSingleton(new TrustlistApiClient(http));
    }

    [Fact]
    public void Directory_Shows_Issuer_TrustAnchor_Pubkey()
    {
        UseDirectoryJson("""
        [
          {
            "id": 1, "role": "Issuer", "entity_id": "https://issuer.example.go.th",
            "entity_name": "Gov Issuer", "jurisdiction": "TH", "status": "Valid",
            "created_at": "2026-01-01T00:00:00Z", "updated_at": "2026-01-01T00:00:00Z",
            "trust_anchors": [
              { "kid": "k-2026-q2", "format": "jwk", "status": "active",
                "jwk": {"kty":"EC","crv":"P-256","alg":"ES256","kid":"k-2026-q2"} }
            ]
          }
        ]
        """);

        var cut = RenderComponent<DirectoryPage>();

        // Summary column advertises the published key.
        Assert.Contains("1 key(s)", cut.Markup);

        // Expanding the row reveals the actual public key material.
        cut.Find("button.link-btn").Click();
        Assert.Contains("Trust anchors", cut.Markup);
        Assert.Contains("k-2026-q2", cut.Markup);
        Assert.Contains("\"kty\":\"EC\"", cut.Markup);
    }

    [Fact]
    public void Directory_Shows_Verifier_ClientIdentifier()
    {
        UseDirectoryJson("""
        [
          {
            "id": 2, "role": "Verifier", "entity_id": "https://verifier.example.co.th",
            "entity_name": "Verifier Co", "jurisdiction": "TH", "status": "Valid",
            "created_at": "2026-01-01T00:00:00Z", "updated_at": "2026-01-01T00:00:00Z",
            "client_identifiers": [
              { "prefix": "x509_san_dns", "value": "verifier.example.co.th" }
            ]
          }
        ]
        """);

        var cut = RenderComponent<DirectoryPage>();
        Assert.Contains("1 client-id(s)", cut.Markup);

        cut.Find("button.link-btn").Click();
        Assert.Contains("Client identifiers", cut.Markup);
        Assert.Contains("x509_san_dns", cut.Markup);
        Assert.Contains("verifier.example.co.th", cut.Markup);
    }

    [Fact]
    public void Directory_Shows_WalletProvider_Wia()
    {
        UseDirectoryJson("""
        [
          {
            "id": 3, "role": "WalletProvider", "entity_id": "https://wallet.example.co.th",
            "entity_name": "Wallet Co", "jurisdiction": "TH", "status": "Valid",
            "created_at": "2026-01-01T00:00:00Z", "updated_at": "2026-01-01T00:00:00Z",
            "wia_status_list_uri": "https://wallet.example.co.th/wia-statuslist/v1.jwt",
            "wia_revocation_maintenance_period_days": 30,
            "wia_attestation_format": ["oauth-client-attestation+jwt"]
          }
        ]
        """);

        var cut = RenderComponent<DirectoryPage>();
        Assert.Contains("WIA", cut.Markup);

        cut.Find("button.link-btn").Click();
        Assert.Contains("Wallet Instance Attestation", cut.Markup);
        Assert.Contains("wia-statuslist/v1.jwt", cut.Markup);
        Assert.Contains("oauth-client-attestation+jwt", cut.Markup);
        Assert.Contains("30 day(s)", cut.Markup);
    }

    [Fact]
    public void Directory_Without_KeyMaterial_Has_No_Details_Button()
    {
        UseDirectoryJson("""
        [
          {
            "id": 4, "role": "Issuer", "entity_id": "https://bare.example.go.th",
            "entity_name": "Bare Issuer", "jurisdiction": "TH", "status": "Applied",
            "created_at": "2026-01-01T00:00:00Z", "updated_at": "2026-01-01T00:00:00Z"
          }
        ]
        """);

        var cut = RenderComponent<DirectoryPage>();
        // No published key material -> the Keys/WIA column shows the em dash and there
        // is no Details affordance to expand.
        Assert.Empty(cut.FindAll("button.link-btn"));
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }
    }
}
