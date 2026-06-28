using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Trustlist.Api;
using Trustlist.Api.Data;
using Trustlist.Api.Domain;
using Trustlist.Web.Services;
using Xunit;
using DirectoryPage = Trustlist.Web.Components.Pages.Directory;

namespace Trustlist.Tests;

/// <summary>
/// MAS-725 acceptance tests — surface the trustlist version on every public
/// directory endpoint and on the admin frontend index page.
///
/// Acceptance criteria from the CEO delegation comment on MAS-725:
///   1. <c>trustlist_version</c> field present on each <c>/v1/{role}/...</c>
///      list response, identical across roles (same snapshot of the directory).
///   2. <c>GET /v1/trustlist</c> returns the same version plus per-role counts.
///   3. The version is deterministic — same DB state → same digest.
///   4. The version changes when the directory state changes (entity added or
///      updated).
///   5. The Directory.razor admin index page renders the version in the hero.
///
/// The tests run against the real <c>WebApplicationFactory&lt;Program&gt;</c>
/// with an in-memory EF Core DB (per <see cref="TrustlistApiFactory"/>), so
/// the full controller → middleware → response path is exercised.
/// </summary>
public class TrustlistVersionTests : IClassFixture<TrustlistApiFactory>
{
    private readonly TrustlistApiFactory _factory;

    public TrustlistVersionTests(TrustlistApiFactory factory)
    {
        _factory = factory;
    }

    // ---------- helpers --------------------------------------------------

    private void SeedOneIssuer(string entityId, string name = "Test Issuer")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (db.TrustlistEntities.Any(e => e.EntityId == entityId)) return;
        db.TrustlistEntities.Add(new TrustlistEntity
        {
            Role = EntityRole.Issuer,
            EntityId = entityId,
            EntityName = name,
            Jurisdiction = "TH",
            Status = EntityStatus.Valid,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private void AddEntity(string entityId, EntityRole role, string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.TrustlistEntities.Add(new TrustlistEntity
        {
            Role = role,
            EntityId = entityId,
            EntityName = name,
            Jurisdiction = "TH",
            Status = EntityStatus.Valid,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    /// <summary>
    /// Spin up a fresh in-memory factory so tests that assert specific counts
    /// (empty DB, "exactly 1 issuer") are not polluted by other tests in this
    /// class. Mirrors the publisher-key isolation pattern in JwsResponseSigningTests.
    /// </summary>
    private static TrustlistApiFactory NewIsolatedFactory() => new();

    private static void SeedInFactory(
        TrustlistApiFactory factory, string entityId, EntityRole role, string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (db.TrustlistEntities.Any(e => e.EntityId == entityId)) return;
        db.TrustlistEntities.Add(new TrustlistEntity
        {
            Role = role,
            EntityId = entityId,
            EntityName = name,
            Jurisdiction = "TH",
            Status = EntityStatus.Valid,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private async Task<JsonElement> GetUnwrappedJsonAsync(string path)
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // The /v1/{role}/... endpoints are JWS-wrapped (MAS-696). The /v1/trustlist
        // endpoint is NOT (excluded by JwsResponseMiddleware.ShouldSign). For
        // wrapped responses we base64url-decode the payload section; for plain
        // JSON we return the body directly.
        var ct = resp.Content.Headers.ContentType?.MediaType;
        var body = await resp.Content.ReadAsStringAsync();
        if (ct == "application/jwt")
        {
            var parts = body.Split('.');
            Assert.Equal(3, parts.Length);
            var payload = Base64UrlDecode(parts[1]);
            using var jwtDoc = JsonDocument.Parse(payload);
            return jwtDoc.RootElement.Clone();
        }
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4) { case 2: padded += "=="; break; case 3: padded += "="; break; }
        return Convert.FromBase64String(padded);
    }

    // ---------- Test 1: trustlist_version present on each role list ------

    [Fact]
    public async Task List_Issuers_Response_Includes_TrustlistVersion()
    {
        SeedOneIssuer("https://issuer-a.example.go.th");
        var doc = await GetUnwrappedJsonAsync("/v1/issuers");
        Assert.True(doc.TryGetProperty("trustlist_version", out var v));
        var s = v.GetString();
        Assert.False(string.IsNullOrWhiteSpace(s));
        Assert.Equal(TrustlistVersion.VersionLength, s!.Length);
        // First two chars must be hex (sanity — full digest is hex; we lower-case
        // it on the wire per the snake_case naming policy).
        foreach (var c in s) Assert.True(Uri.IsHexDigit(c), $"char '{c}' is not hex");
    }

    [Fact]
    public async Task List_Verifiers_Response_Includes_TrustlistVersion()
    {
        SeedOneIssuer("https://issuer-b.example.go.th");
        var doc = await GetUnwrappedJsonAsync("/v1/verifiers");
        Assert.True(doc.TryGetProperty("trustlist_version", out var v));
        Assert.False(string.IsNullOrWhiteSpace(v.GetString()));
    }

    [Fact]
    public async Task List_WalletProviders_Response_Includes_TrustlistVersion()
    {
        SeedOneIssuer("https://issuer-c.example.go.th");
        var doc = await GetUnwrappedJsonAsync("/v1/wallet-providers");
        Assert.True(doc.TryGetProperty("trustlist_version", out var v));
        Assert.False(string.IsNullOrWhiteSpace(v.GetString()));
    }

    [Fact]
    public async Task List_Responses_No_Longer_Expose_SnapshotId_Local_Placeholder()
    {
        // MAS-696 added snapshot_id="local" as a placeholder; MAS-725 replaces it
        // with trustlist_version. The placeholder field must be gone.
        SeedOneIssuer("https://issuer-d.example.go.th");
        var doc = await GetUnwrappedJsonAsync("/v1/issuers");
        Assert.False(doc.TryGetProperty("snapshot_id", out _),
            "snapshot_id placeholder must be removed in MAS-725; use trustlist_version");
    }

    // ---------- Test 2: /v1/trustlist endpoint --------------------------

[Fact]
    public async Task TopLevel_Endpoint_Returns_Version_And_Counts()
    {
        // Use a private factory so the DB is fresh — other tests in this class
        // share the class-level fixture's in-memory DB and would pollute the
        // counts.
        await using var fresh = NewIsolatedFactory();
        SeedInFactory(fresh, "https://issuer-x.example.go.th", EntityRole.Issuer, "Issuer X");
        SeedInFactory(fresh, "https://verifier-x.example.go.th", EntityRole.Verifier, "Verifier X");
        SeedInFactory(fresh, "https://wp-x.example.go.th", EntityRole.WalletProvider, "Wallet Provider X");

        using var client = fresh.CreateClient();
        var resp = await client.GetAsync("/v1/trustlist");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        Assert.True(doc.RootElement.TryGetProperty("trustlist_version", out var version));
        var v = version.GetString();
        Assert.False(string.IsNullOrWhiteSpace(v));
        Assert.Equal(Trustlist.Api.TrustlistVersion.VersionLength, v!.Length);

        Assert.Equal("sha256-trunc12-content",
            doc.RootElement.GetProperty("version_algorithm").GetString());

        var counts = doc.RootElement.GetProperty("counts");
        // Wire format is snake_case (global JSON naming policy configured for both
        // MVC controllers and minimal-API endpoints; C# property WalletProviders
        // -> wallet_providers; Total -> total; etc.).
        Assert.Equal(1, counts.GetProperty("issuers").GetInt32());
        Assert.Equal(1, counts.GetProperty("verifiers").GetInt32());
        Assert.Equal(1, counts.GetProperty("wallet_providers").GetInt32());
        Assert.Equal(3, counts.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task TopLevel_Endpoint_Is_Not_Jws_Wrapped()
    {
        // /v1/trustlist is the cheap path the admin frontend polls; it must
        // return plain JSON so the Blazor client doesn't need a JWKS round-trip
        // just to render a version line.
        SeedOneIssuer("https://issuer-e.example.go.th");
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/v1/trustlist");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
    }

    // ---------- Test 3: directory-wide consistency ----------------------

    [Fact]
    public async Task Same_Directory_State_Same_Version_Across_Roles()
    {
        // The three role lists must report the SAME trustlist_version because
        // the version is computed against the whole directory state, not per
        // role. (A role-scoped version would be ambiguous when an issuer is
        // added — does the wallet-provider list's version change?)
        SeedOneIssuer("https://issuer-shared.example.go.th");

        var issuers        = await GetUnwrappedJsonAsync("/v1/issuers");
        var verifiers      = await GetUnwrappedJsonAsync("/v1/verifiers");
        var walletProviders = await GetUnwrappedJsonAsync("/v1/wallet-providers");
        var snapshot       = await GetUnwrappedJsonAsync("/v1/trustlist");

        var v1 = issuers.GetProperty("trustlist_version").GetString();
        var v2 = verifiers.GetProperty("trustlist_version").GetString();
        var v3 = walletProviders.GetProperty("trustlist_version").GetString();
        var v4 = snapshot.GetProperty("trustlist_version").GetString();

        Assert.Equal(v1, v2);
        Assert.Equal(v2, v3);
        Assert.Equal(v3, v4);
    }

    // ---------- Test 4: version shifts on directory mutation ------------

    [Fact]
    public async Task Version_Changes_When_An_Entity_Is_Added()
    {
        SeedOneIssuer("https://issuer-pre.example.go.th");
        var before = (await GetUnwrappedJsonAsync("/v1/trustlist"))
            .GetProperty("trustlist_version").GetString();

        AddEntity("https://verifier-post.example.go.th", EntityRole.Verifier, "Verifier Post");

        var after = (await GetUnwrappedJsonAsync("/v1/trustlist"))
            .GetProperty("trustlist_version").GetString();

        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task Version_Is_Deterministic_Across_Calls_On_Same_State()
    {
        SeedOneIssuer("https://issuer-stable.example.go.th");
        var v1 = (await GetUnwrappedJsonAsync("/v1/trustlist"))
            .GetProperty("trustlist_version").GetString();
        var v2 = (await GetUnwrappedJsonAsync("/v1/trustlist"))
            .GetProperty("trustlist_version").GetString();
        var v3 = (await GetUnwrappedJsonAsync("/v1/trustlist"))
            .GetProperty("trustlist_version").GetString();

        Assert.Equal(v1, v2);
        Assert.Equal(v2, v3);
    }

    [Fact]
    public async Task Empty_Directory_Has_Stable_Zero_Version()
    {
        // Use a private factory so the in-memory DB is empty. Other tests in this
        // class share the class-level fixture and would have seeded rows by the
        // time this test runs (xUnit does NOT guarantee test ordering).
        await using var fresh = NewIsolatedFactory();
        using var client = fresh.CreateClient();
        var resp = await client.GetAsync("/v1/trustlist");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        var v = doc.RootElement.GetProperty("trustlist_version").GetString();
        Assert.False(string.IsNullOrWhiteSpace(v));
        Assert.Equal(Trustlist.Api.TrustlistVersion.VersionLength, v!.Length);
        Assert.Equal(0, doc.RootElement.GetProperty("counts").GetProperty("total").GetInt32());
    }

    // ---------- Test 5: Directory.razor renders the version -------------

    [Fact]
    public void Directory_Page_Renders_Trustlist_Version_Line()
    {
        var handler = new StubHandler("[]",
            snapshotJson: """
            {
              "trustlist_version": "abc123def456",
              "version_algorithm": "sha256-trunc12-content",
              "counts": { "issuers": 2, "verifiers": 1, "wallet_providers": 0, "resolver_nodes": 0, "total": 3 }
            }
            """);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(new TrustlistApiClient(http));

        var cut = ctx.RenderComponent<DirectoryPage>();

        Assert.Contains("Trustlist version", cut.Markup);
        Assert.Contains("abc123def456", cut.Markup);
        Assert.Contains("2 issuers", cut.Markup);
        Assert.Contains("1 verifier", cut.Markup);
    }

    [Fact]
    public void Directory_Page_Falls_Back_To_Error_When_Snapshot_Fails()
    {
        // If /v1/trustlist returns 500 the page must NOT crash — it should
        // render the error line in the hero and still show the (possibly empty)
        // table.
        var handler = new StubHandler("[]", snapshotHttpStatus: HttpStatusCode.InternalServerError);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(new TrustlistApiClient(http));

        var cut = ctx.RenderComponent<DirectoryPage>();

        Assert.Contains("Could not load trustlist version", cut.Markup);
    }

    [Fact]
    public void Directory_Page_Renders_Singular_Pluralization_Correctly()
    {
        // "1 issuer" vs "2 issuers" / "1 verifier" vs "2 verifiers" — tiny
        // detail, but a UI thing operators notice immediately when the seed
        // drops back to a single entry.
        var handler = new StubHandler("[]",
            snapshotJson: """
            {
              "trustlist_version": "deadbeef0001",
              "version_algorithm": "sha256-trunc12-content",
              "counts": { "issuers": 1, "verifiers": 1, "wallet_providers": 1, "resolver_nodes": 0, "total": 3 }
            }
            """);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(new TrustlistApiClient(http));

        var cut = ctx.RenderComponent<DirectoryPage>();

        // Singular forms present (followed by comma or separator in the rendered text).
        Assert.Contains("1 issuer,", cut.Markup);
        Assert.Contains("1 verifier,", cut.Markup);
        Assert.Contains("1 wallet provider", cut.Markup);
        // And the plural forms are absent for the singular case.
        Assert.DoesNotContain("1 issuers", cut.Markup);
        Assert.DoesNotContain("1 verifiers", cut.Markup);
        Assert.DoesNotContain("1 wallet providers", cut.Markup);
    }

    // ---------- Test 6: /version endpoint reflects the bump -------------

    [Fact]
    public async Task Version_Endpoint_Reports_030()
    {
        // MAS-725 is a feature change -> minor bump 0.2.0 -> 0.3.0 (per the
        // MAS-696 version policy). Both API and Web must agree on the number.
        using var apiClient = _factory.CreateClient();
        var apiResp = await apiClient.GetAsync("/version");
        Assert.Equal(HttpStatusCode.OK, apiResp.StatusCode);
        using var apiDoc = JsonDocument.Parse(await apiResp.Content.ReadAsStringAsync());
        Assert.Equal("0.3.0", apiDoc.RootElement.GetProperty("version").GetString());

        // Sanity-check the API's informational assembly version via reflection —
        // same trick VersionEndpointTests uses to catch accidental "v1.2" prefixes.
        var asm = typeof(Trustlist.Api.VersionEndpoint).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.NotNull(info);
        var plus = info!.IndexOf('+');
        var v = plus >= 0 ? info[..plus] : info;
        Assert.Equal("0.3.0", v);
    }

    // ---------- stub handler (shared by Directory render tests) ---------

    private sealed class StubHandler(
        string entityListJson,
        string? snapshotJson = null,
        HttpStatusCode snapshotHttpStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            HttpResponseMessage resp;
            if (path.EndsWith("/v1/trustlist", StringComparison.Ordinal))
            {
                resp = new HttpResponseMessage(snapshotHttpStatus);
                if (snapshotHttpStatus == HttpStatusCode.OK && snapshotJson is not null)
                {
                    resp.Content = new StringContent(snapshotJson, Encoding.UTF8, "application/json");
                }
                else
                {
                    resp.Content = new StringContent("{\"error\":\"boom\"}", Encoding.UTF8, "application/json");
                }
            }
            else
            {
                resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(entityListJson, Encoding.UTF8, "application/json"),
                };
            }
            return Task.FromResult(resp);
        }
    }
}