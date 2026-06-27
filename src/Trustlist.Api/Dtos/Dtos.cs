using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Trustlist.Api.Domain;

namespace Trustlist.Api.Dtos;

public record RegisterRequest(
    [Required, EmailAddress] string Email,
    [Required, MinLength(8)] string Password);

public record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password);

public record AuthResponse(string Token, string Email, DateTimeOffset ExpiresAt);

public record TrustlistEntityDto(
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
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt)
{
    public static TrustlistEntityDto From(TrustlistEntity e) => new(
        e.Id, e.Role.ToString(), e.EntityId, e.EntityName, e.EntityLegalName,
        e.Jurisdiction, e.RegistrationNumber, e.Status.ToString(),
        e.CertificationScheme, e.CertificateId, e.Scope, e.SecurityEmail,
        e.NextUpdate, e.CreatedAt, e.UpdatedAt);
}

public class CreateTrustlistEntityRequest
{
    [Required]
    public EntityRole Role { get; set; }

    [Required, MaxLength(512), JsonPropertyName("entity_id")]
    public string EntityId { get; set; } = string.Empty;

    [Required, MaxLength(256), JsonPropertyName("entity_name")]
    public string EntityName { get; set; } = string.Empty;

    [JsonPropertyName("entity_legal_name")]
    public string? EntityLegalName { get; set; }

    [Required, MaxLength(2), JsonPropertyName("jurisdiction")]
    public string Jurisdiction { get; set; } = "TH";

    [JsonPropertyName("registration_number")]
    public string? RegistrationNumber { get; set; }

    [Required]
    public EntityStatus Status { get; set; }

    [JsonPropertyName("certification_scheme")]
    public string? CertificationScheme { get; set; }

    [JsonPropertyName("certificate_id")]
    public string? CertificateId { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("security_email")]
    public string? SecurityEmail { get; set; }

    [JsonPropertyName("next_update")]
    public DateTimeOffset? NextUpdate { get; set; }
}

public class UpdateTrustlistEntityRequest
{
    [Required, MaxLength(256), JsonPropertyName("entity_name")]
    public string EntityName { get; set; } = string.Empty;

    [JsonPropertyName("entity_legal_name")]
    public string? EntityLegalName { get; set; }

    [Required, MaxLength(2), JsonPropertyName("jurisdiction")]
    public string Jurisdiction { get; set; } = "TH";

    [JsonPropertyName("registration_number")]
    public string? RegistrationNumber { get; set; }

    [Required]
    public EntityStatus Status { get; set; }

    [JsonPropertyName("certification_scheme")]
    public string? CertificationScheme { get; set; }

    [JsonPropertyName("certificate_id")]
    public string? CertificateId { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("security_email")]
    public string? SecurityEmail { get; set; }

    [JsonPropertyName("next_update")]
    public DateTimeOffset? NextUpdate { get; set; }
}
