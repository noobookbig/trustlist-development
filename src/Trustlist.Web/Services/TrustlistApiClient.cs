using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Trustlist.Web.Services;

// The API serializes responses with a global snake_case naming policy
// (see Trustlist.Api/Program.cs), so the auth payload arrives as
// { "token", "email", "expires_at" }. Bind each property explicitly so the
// expiry actually deserializes — otherwise ExpiresAt stays at its default
// (0001-01-01), AuthState.IsAuthenticated is always false, and a successful
// API login looks like a failed one in the UI.
public record AuthResult(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

public record TrustlistEntityModel(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("entity_id")] string EntityId,
    [property: JsonPropertyName("entity_name")] string EntityName,
    [property: JsonPropertyName("entity_legal_name")] string? EntityLegalName,
    [property: JsonPropertyName("jurisdiction")] string Jurisdiction,
    [property: JsonPropertyName("registration_number")] string? RegistrationNumber,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("certification_scheme")] string? CertificationScheme,
    [property: JsonPropertyName("certificate_id")] string? CertificateId,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("security_email")] string? SecurityEmail,
    [property: JsonPropertyName("next_update")] DateTimeOffset? NextUpdate,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public record CreateEntityModel(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("entity_id")] string EntityId,
    [property: JsonPropertyName("entity_name")] string EntityName,
    [property: JsonPropertyName("entity_legal_name")] string? EntityLegalName,
    [property: JsonPropertyName("jurisdiction")] string Jurisdiction,
    [property: JsonPropertyName("registration_number")] string? RegistrationNumber,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("certification_scheme")] string? CertificationScheme,
    [property: JsonPropertyName("certificate_id")] string? CertificateId,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("security_email")] string? SecurityEmail,
    [property: JsonPropertyName("next_update")] DateTimeOffset? NextUpdate);

/// <summary>
/// Thin typed client over the Trustlist Web API. The bearer token is supplied
/// per call from the session-scoped <see cref="AuthState"/>.
/// </summary>
public class TrustlistApiClient(HttpClient http)
{
    public async Task<(AuthResult? result, string? error)> RegisterAsync(string email, string password)
        => await PostAuthAsync("api/auth/register", email, password);

    public async Task<(AuthResult? result, string? error)> LoginAsync(string email, string password)
        => await PostAuthAsync("api/auth/login", email, password);

    private async Task<(AuthResult?, string?)> PostAuthAsync(string path, string email, string password)
    {
        var resp = await http.PostAsJsonAsync(path, new { email, password });
        if (resp.IsSuccessStatusCode)
        {
            var result = await resp.Content.ReadFromJsonAsync<AuthResult>();
            return (result, null);
        }
        var body = await resp.Content.ReadAsStringAsync();
        return (null, $"{(int)resp.StatusCode}: {body}");
    }

    public async Task<List<TrustlistEntityModel>> ListAsync(string? role = null, string? status = null, string? q = null)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(role)) query.Add($"role={Uri.EscapeDataString(role)}");
        if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status)}");
        if (!string.IsNullOrWhiteSpace(q)) query.Add($"q={Uri.EscapeDataString(q)}");
        var url = "api/trustlist" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);

        return await http.GetFromJsonAsync<List<TrustlistEntityModel>>(url) ?? [];
    }

    public async Task<(bool ok, string? error)> CreateAsync(CreateEntityModel model, string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/trustlist")
        {
            Content = JsonContent.Create(model)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, $"{(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
    }

    public async Task<(bool ok, string? error)> DeleteAsync(int id, string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"api/trustlist/{id}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, $"{(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
    }
}

/// <summary>Session-scoped holder for the logged-in user's JWT.</summary>
public class AuthState
{
    public string? Token { get; private set; }
    public string? Email { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }

    public bool IsAuthenticated =>
        !string.IsNullOrEmpty(Token) && ExpiresAt is { } exp && exp > DateTimeOffset.UtcNow;

    public event Action? Changed;

    public void SignIn(AuthResult result)
    {
        Token = result.Token;
        Email = result.Email;
        ExpiresAt = result.ExpiresAt;
        Changed?.Invoke();
    }

    public void SignOut()
    {
        Token = null;
        Email = null;
        ExpiresAt = null;
        Changed?.Invoke();
    }
}
