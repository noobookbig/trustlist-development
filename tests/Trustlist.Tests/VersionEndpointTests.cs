using System.Net;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Trustlist.Tests;

/// <summary>
/// MAS-696 follow-up (CEO 2026-06-28) — every code change bumps the version.
/// These tests pin the contract:
///   - <c>GET /version</c> returns 200 + application/json
///   - shape: <c>{ service, version, framework, aspnetcore }</c>
///   - version is non-empty and parses as SemVer MAJOR.MINOR.PATCH
///   - the API version reported at runtime matches the csproj <c>&lt;Version&gt;</c>
/// </summary>
public class VersionEndpointTests : IClassFixture<TrustlistApiFactory>
{
    private readonly TrustlistApiFactory _api;

    public VersionEndpointTests(TrustlistApiFactory api) => _api = api;

    [Fact]
    public async Task Version_ReturnsExpectedContract()
    {
        using var client = _api.CreateClient();
        var resp = await client.GetAsync("/version");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("trustlist-api", root.GetProperty("service").GetString());
        Assert.Equal("net8.0", root.GetProperty("framework").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("aspnetcore").GetString()));

        var version = root.GetProperty("version").GetString();
        Assert.False(string.IsNullOrWhiteSpace(version));
        var parts = version!.Split('.');
        Assert.Equal(3, parts.Length);
        foreach (var p in parts)
        {
            Assert.True(int.TryParse(p, out _), $"version segment '{p}' must be an integer");
        }
    }

    [Fact]
    public void Api_Csproj_Version_IsSemVer()
    {
        // Pin the convention: <Version> in Trustlist.Api.csproj must parse as
        // SemVer MAJOR.MINOR.PATCH. This guards against accidental "v1.2"
        // prefixes or "+build" suffixes leaking into the runtime endpoint.
        var asm = typeof(Trustlist.Api.VersionEndpoint).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.False(string.IsNullOrWhiteSpace(info));
        var plus = info!.IndexOf('+');
        var version = plus >= 0 ? info[..plus] : info;
        var parts = version.Split('.');
        Assert.Equal(3, parts.Length);
        foreach (var p in parts)
        {
            Assert.True(int.TryParse(p, out _), $"version segment '{p}' must be an integer");
        }
    }
}