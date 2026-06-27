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
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    // Role-specific key material / WIA fields (MAS-687): surfaced so the admin UI can
    // show what each role is missing and round-trip what it set.
    [property: JsonPropertyName("trust_anchors")] TrustAnchorDto[]? TrustAnchors,
    [property: JsonPropertyName("client_identifiers")] ClientIdentifierDto[]? ClientIdentifiers,
    [property: JsonPropertyName("wia_status_list_uri")] string? WiaStatusListUri,
    [property: JsonPropertyName("wia_revocation_maintenance_period_days")] int? WiaRevocationMaintenancePeriodDays,
    [property: JsonPropertyName("wia_attestation_format")] string[]? WiaAttestationFormat)
{
    public static TrustlistEntityDto From(TrustlistEntity e) => new(
        e.Id, e.Role.ToString(), e.EntityId, e.EntityName, e.EntityLegalName,
        e.Jurisdiction, e.RegistrationNumber, e.Status.ToString(),
        e.CertificationScheme, e.CertificateId, e.Scope, e.SecurityEmail,
        e.NextUpdate, e.CreatedAt, e.UpdatedAt,
        TrustAnchorMapping.Deserialize(e.TrustAnchorsJson),
        ClientIdentifierMapping.Deserialize(e.ClientIdentifiersJson),
        e.WiaStatusListUri,
        e.WiaRevocationMaintenancePeriodDays,
        StringArrayJson.Deserialize(e.WiaAttestationFormatJson));
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

    // ----- Role-specific fields (MAS-687) -----
    // These let the admin populate the key material / WIA fields the canonical
    // openapi-trustlist-directory.yaml requires per role. All optional on the wire;
    // role-specific validation happens at the read/projection layer (RoleRecordMapper).

    /// <summary>Issuer + Wallet-Provider signing keys (the "pubkey" trust anchors).</summary>
    [JsonPropertyName("trust_anchors")]
    public TrustAnchorDto[]? TrustAnchors { get; set; }

    /// <summary>Verifier OpenID4VP §5.9 client identifiers (the Verifier's identity / key binding).</summary>
    [JsonPropertyName("client_identifiers")]
    public ClientIdentifierDto[]? ClientIdentifiers { get; set; }

    /// <summary>Wallet-Provider WIA Token Status List URI.</summary>
    [JsonPropertyName("wia_status_list_uri")]
    public string? WiaStatusListUri { get; set; }

    /// <summary>Wallet-Provider WIA revocation maintenance period (days).</summary>
    [JsonPropertyName("wia_revocation_maintenance_period_days")]
    public int? WiaRevocationMaintenancePeriodDays { get; set; }

    /// <summary>Wallet-Provider WIA attestation formats.</summary>
    [JsonPropertyName("wia_attestation_format")]
    public string[]? WiaAttestationFormat { get; set; }
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

    // ----- Role-specific fields (MAS-687) -----
    [JsonPropertyName("trust_anchors")]
    public TrustAnchorDto[]? TrustAnchors { get; set; }

    [JsonPropertyName("client_identifiers")]
    public ClientIdentifierDto[]? ClientIdentifiers { get; set; }

    [JsonPropertyName("wia_status_list_uri")]
    public string? WiaStatusListUri { get; set; }

    [JsonPropertyName("wia_revocation_maintenance_period_days")]
    public int? WiaRevocationMaintenancePeriodDays { get; set; }

    [JsonPropertyName("wia_attestation_format")]
    public string[]? WiaAttestationFormat { get; set; }
}
