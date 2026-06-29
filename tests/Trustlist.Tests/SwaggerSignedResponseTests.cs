using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace Trustlist.Tests;

/// <summary>
/// MAS-727 — "why does swagger look like it's not signing?".
///
/// The /v1 directory responses ARE signed (JWS-compact, application/jwt) by
/// JwsResponseMiddleware, but Swagger originally documented them as plain
/// application/json, so the UI implied they were unsigned. These tests pin both
/// halves of the fix:
///   1. The live response on a signed surface really is application/jwt.
///   2. The generated Swagger/OpenAPI doc advertises application/jwt for the 2xx
///      response on the signed surfaces (and leaves the unsigned /api surface alone).
/// </summary>
public class SwaggerSignedResponseTests : IClassFixture<TrustlistApiFactory>
{
    private readonly TrustlistApiFactory _api;

    public SwaggerSignedResponseTests(TrustlistApiFactory api) => _api = api;

    [Fact]
    public async Task SignedSurface_ReturnsApplicationJwt()
    {
        using var client = _api.CreateClient();
        var resp = await client.GetAsync("/v1/issuers");
        resp.EnsureSuccessStatusCode();
        Assert.Equal("application/jwt", resp.Content.Headers.ContentType?.MediaType);

        // Body is a 3-part JWS-compact token.
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Equal(2, body.Split('.').Length - 1);
    }

    [Theory]
    [InlineData("/v1/issuers")]
    [InlineData("/v1/verifiers")]
    [InlineData("/v1/wallet-providers")]
    public async Task Swagger_DocumentsSignedSurface_AsApplicationJwt(string path)
    {
        var doc = await GetSwaggerDoc();
        var content = doc
            .GetProperty("paths").GetProperty(path)
            .GetProperty("get").GetProperty("responses")
            .GetProperty("200").GetProperty("content");

        Assert.True(content.TryGetProperty("application/jwt", out _),
            $"Swagger 200 for {path} should advertise application/jwt");
        Assert.False(content.TryGetProperty("application/json", out _),
            $"Swagger 200 for {path} should NOT still advertise plain application/json");
    }

    [Fact]
    public async Task Swagger_LeavesUnsignedAdminSurface_AsJson()
    {
        var doc = await GetSwaggerDoc();
        var content = doc
            .GetProperty("paths").GetProperty("/api/Trustlist")
            .GetProperty("get").GetProperty("responses")
            .GetProperty("200").GetProperty("content");

        // The legacy /api admin surface is NOT signed; it must stay JSON.
        Assert.True(content.TryGetProperty("application/json", out _),
            "Swagger 200 for /api/Trustlist should remain application/json");
        Assert.False(content.TryGetProperty("application/jwt", out _),
            "Swagger 200 for /api/Trustlist must NOT be marked application/jwt");
    }

    private async Task<JsonElement> GetSwaggerDoc()
    {
        using var client = _api.CreateClient();
        // Swashbuckle serves the OpenAPI document at /swagger/{doc}/swagger.json.
        var resp = await client.GetAsync("/swagger/v1/swagger.json");
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(stream);
        return json.RootElement.Clone();
    }
}
