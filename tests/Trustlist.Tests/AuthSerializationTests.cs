using System.Text.Json;
using System.Text.Json.Serialization;
using Trustlist.Web.Services;
using Xunit;

namespace Trustlist.Tests;

/// <summary>
/// Regression tests for the login defect (MAS-685): the API serializes the auth
/// response with a global snake_case naming policy, so the Web client must bind
/// "expires_at" -> AuthResult.ExpiresAt. If that binding breaks, ExpiresAt falls
/// back to its default (0001-01-01), AuthState.IsAuthenticated is always false,
/// and a successful API login looks like a failed one in the UI.
/// </summary>
public class AuthSerializationTests
{
    // Mirrors the API's wire format (Trustlist.Api/Program.cs global JSON options).
    private static string ApiAuthJson(DateTimeOffset expiresAt) =>
        JsonSerializer.Serialize(
            new { token = "tok-123", email = "admin@trustlist.local", expires_at = expiresAt },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

    [Fact]
    public void AuthResult_Binds_SnakeCase_ExpiresAt()
    {
        var expiry = DateTimeOffset.UtcNow.AddMinutes(120);
        var json = ApiAuthJson(expiry);

        // HttpClientJsonExtensions (ReadFromJsonAsync) uses JsonSerializerDefaults.Web.
        var result = JsonSerializer.Deserialize<AuthResult>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal("tok-123", result.Token);
        Assert.Equal("admin@trustlist.local", result.Email);
        // The core of the regression: expiry must round-trip, not default to 0001-01-01.
        Assert.True(result.ExpiresAt.Year > 2000, $"ExpiresAt did not bind (got {result.ExpiresAt:o})");
        Assert.Equal(expiry.ToUnixTimeSeconds(), result.ExpiresAt.ToUnixTimeSeconds());
    }

    [Fact]
    public void AuthState_IsAuthenticated_After_SignIn_With_Future_Expiry()
    {
        var json = ApiAuthJson(DateTimeOffset.UtcNow.AddMinutes(120));
        var result = JsonSerializer.Deserialize<AuthResult>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var auth = new AuthState();
        auth.SignIn(result);

        Assert.True(auth.IsAuthenticated);
    }
}
