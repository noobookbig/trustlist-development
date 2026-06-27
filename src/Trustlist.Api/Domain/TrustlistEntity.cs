using System.ComponentModel.DataAnnotations;

namespace Trustlist.Api.Domain;

/// <summary>
/// Entity role in the Thailand Trust List.
/// Mirrors the EntityRole enum in the canonical OpenAPI directory spec
/// (src/PRD-1.0/openapi-trustlist-directory.yaml in trustlist-research).
/// </summary>
public enum EntityRole
{
    Issuer = 0,
    Verifier = 1,
    WalletProvider = 2,
    ResolverNode = 3
}

/// <summary>
/// Entity lifecycle status. Cites ETSI TS 119 612 + MAS-335 §3.3.
/// applied -> vetted -> valid; suspended (temporary); withdrawn (permanent); expired.
/// </summary>
public enum EntityStatus
{
    Applied = 0,
    Vetted = 1,
    Valid = 2,
    Suspended = 3,
    Withdrawn = 4,
    Expired = 5
}

/// <summary>
/// Trust anchor key format — openapi-trustlist-directory.yaml `TrustAnchorFormat`.
/// </summary>
public enum TrustAnchorFormat
{
    Jwk = 0,
    X509 = 1
}

/// <summary>
/// Trust anchor lifecycle status — openapi-trustlist-directory.yaml `TrustAnchorStatus`.
/// </summary>
public enum TrustAnchorStatus
{
    Active = 0,
    Retired = 1,
    Compromise = 2
}

/// <summary>
/// OpenID4VP 1.0 §5.9 Client Identifier Prefix vocabulary. Only Verifier records use this.
/// </summary>
public enum ClientIdentifierPrefix
{
    X509SanDns = 0,
    X509SanUri = 1,
    X509Hash = 2,
    VerifierAttestation = 3,
    OpenidFederation = 4,
    RedirectUri = 5,
    DecentralizedIdentifier = 6
}

/// <summary>
/// Trust anchor (kid / format / status + optional JWK or X.509 chain).
/// Populated for Issuer and Wallet-Provider records. Stored as part of
/// <see cref="TrustlistEntity.TrustAnchorsJson"/>.
/// </summary>
public record TrustAnchor(
    string Kid,
    TrustAnchorFormat Format,
    TrustAnchorStatus Status,
    DateTimeOffset? NotBefore = null,
    DateTimeOffset? NotAfter = null,
    object? Jwk = null,
    string[]? X5c = null);

/// <summary>
/// OpenID4VP 1.0 §5.9 Client Identifier — how a Verifier identifies itself to the Wallet.
/// Stored as part of <see cref="TrustlistEntity.ClientIdentifiersJson"/>.
/// </summary>
public record ClientIdentifier(ClientIdentifierPrefix Prefix, string Value);

/// <summary>
/// CertificationReference (per openapi-trustlist-directory.yaml §CertificationReference).
/// Flat columns on TrustlistEntity — no nesting so we can index by certificate_id.
/// </summary>
public record CertificationReference(
    string Scheme,
    string SchemeVersion,
    string CertificateId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string IssuingBody);

/// <summary>
/// Contact block (per openapi-trustlist-directory.yaml §Contact). Stored as two columns.
/// </summary>
public record Contact(string SecurityEmail, string? CsirtEmail = null);

/// <summary>
/// A single record in the Trust List directory: an Issuer, Verifier, Wallet Provider
/// or Resolver Node that has been registered (and possibly certified) within the Thai
/// VC ecosystem.
///
/// Role-specific fields are stored alongside the common ones:
/// - Issuer: trust_anchors[], supported_credential_formats[], credential_types[],
///           status_list_endpoint, scope[], contact.
/// - Verifier: client_identifiers[], scope_allowed[], contact.
/// - Wallet-Provider: trust_anchors[], wia_status_list_uri,
///                    wia_revocation_maintenance_period_days, wia_attestation_format[],
///                    ka_attestation_format[], wallet_unit_audit_log_uri,
///                    supported_credential_formats[], contact.
///
/// List-shaped fields are stored as JSON (nvarchar(max)) — see MAS-676 design note.
/// Endpoint URIs and small integers are flat columns for cheap indexing.
/// </summary>
public class TrustlistEntity
{
    public int Id { get; set; }

    [Required]
    public EntityRole Role { get; set; }

    /// <summary>Stable https(s) identifier for the entity (entity_id in the spec).</summary>
    [Required, MaxLength(512)]
    public string EntityId { get; set; } = string.Empty;

    [Required, MaxLength(256)]
    public string EntityName { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? EntityLegalName { get; set; }

    /// <summary>ISO 3166-1 alpha-2 (e.g. "TH").</summary>
    [Required, MaxLength(2)]
    public string Jurisdiction { get; set; } = "TH";

    [MaxLength(128)]
    public string? RegistrationNumber { get; set; }

    [Required]
    public EntityStatus Status { get; set; } = EntityStatus.Applied;

    /// <summary>Certification scheme id, e.g. ETDA-ISSUER-CERT. Mirrors CertificationReference.scheme.</summary>
    [MaxLength(128)]
    public string? CertificationScheme { get; set; }

    /// <summary>Certificate id. Mirrors CertificationReference.certificate_id.</summary>
    [MaxLength(128)]
    public string? CertificateId { get; set; }

    /// <summary>Scheme version, e.g. 1.0.0. Mirrors CertificationReference.scheme_version.</summary>
    [MaxLength(32)]
    public string? CertificationSchemeVersion { get; set; }

    public DateTimeOffset? CertificationIssuedAt { get; set; }

    public DateTimeOffset? CertificationExpiresAt { get; set; }

    /// <summary>Issuing body (e.g. "ETDA"). Mirrors CertificationReference.issuing_body.</summary>
    [MaxLength(256)]
    public string? CertificationIssuingBody { get; set; }

    /// <summary>Comma-separated credential types / scope (kept simple for the local app).</summary>
    [MaxLength(1024)]
    public string? Scope { get; set; }

    [MaxLength(256)]
    public string? SecurityEmail { get; set; }

    /// <summary>CSIRT email (optional). Mirrors Contact.csirt_email.</summary>
    [MaxLength(256)]
    public string? CsirtEmail { get; set; }

    /// <summary>IETF Token Status List endpoint for issued credentials (Issuer only).</summary>
    [MaxLength(1024)]
    public string? StatusListEndpoint { get; set; }

    /// <summary>WIA Token Status List URI (Wallet-Provider only).</summary>
    [MaxLength(1024)]
    public string? WiaStatusListUri { get; set; }

    /// <summary>WIA revocation maintenance period in days (Wallet-Provider only).</summary>
    public int? WiaRevocationMaintenancePeriodDays { get; set; }

    /// <summary>Wallet unit audit log URI (Wallet-Provider only).</summary>
    [MaxLength(1024)]
    public string? WalletUnitAuditLogUri { get; set; }

    /// <summary>
    /// Trust anchors (Issuer + Wallet-Provider). JSON array of TrustAnchor objects —
    /// one-way-door design choice: see MAS-676 plan comment.
    /// </summary>
    public string? TrustAnchorsJson { get; set; }

    /// <summary>Client identifiers (Verifier only). JSON array of ClientIdentifier.</summary>
    public string? ClientIdentifiersJson { get; set; }

    /// <summary>Supported credential formats (Issuer + Wallet-Provider). JSON array of strings.</summary>
    public string? SupportedCredentialFormatsJson { get; set; }

    /// <summary>Credential types (Issuer only). JSON array of strings (e.g. UniversityDegreeCredential).</summary>
    public string? CredentialTypesJson { get; set; }

    /// <summary>WIA attestation formats (Wallet-Provider). JSON array of strings.</summary>
    public string? WiaAttestationFormatJson { get; set; }

    /// <summary>KA attestation formats (Wallet-Provider). JSON array of strings.</summary>
    public string? KaAttestationFormatJson { get; set; }

    /// <summary>Verifier scope_allowed (Verifier only). JSON array of strings.</summary>
    public string? ScopeAllowedJson { get; set; }

    /// <summary>Issuer scope (subset of cert scope). JSON array of strings.</summary>
    public string? IssuerScopeJson { get; set; }

    public DateTimeOffset? NextUpdate { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
