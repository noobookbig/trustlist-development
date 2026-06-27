using System.Text.Json;
using System.Text.Json.Serialization;
using Trustlist.Api.Domain;

namespace Trustlist.Api.Dtos;

/// <summary>
/// JSON serialization options used everywhere we serialize role-specific records.
/// <c>PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower</c> aligns the on-the-wire
/// shape with openapi-trustlist-directory.yaml (snake_case).
/// </summary>
public static class TrustlistJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
}

/// <summary>
/// Trust anchor on the wire — openapi-trustlist-directory.yaml §TrustAnchor.
/// </summary>
public record TrustAnchorDto(
    [property: JsonPropertyName("kid")] string Kid,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("not_before")] DateTimeOffset? NotBefore,
    [property: JsonPropertyName("not_after")] DateTimeOffset? NotAfter,
    [property: JsonPropertyName("jwk")] object? Jwk,
    [property: JsonPropertyName("x5c")] string[]? X5c);

public static class TrustAnchorMapping
{
    public static TrustAnchorDto ToDto(TrustAnchor a) => new(
        a.Kid,
        a.Format.ToString().ToLowerInvariant(),
        a.Status.ToString().ToLowerInvariant(),
        a.NotBefore,
        a.NotAfter,
        a.Jwk,
        a.X5c);

    public static TrustAnchorDto[]? Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<TrustAnchorDto[]>(json, TrustlistJson.Options);

    public static string? Serialize(TrustAnchorDto[]? anchors) =>
        anchors is null || anchors.Length == 0
            ? null
            : JsonSerializer.Serialize(anchors, TrustlistJson.Options);
}

/// <summary>
/// OpenID4VP 1.0 §5.9 Client Identifier on the wire.
/// </summary>
public record ClientIdentifierDto(
    [property: JsonPropertyName("prefix")] string Prefix,
    [property: JsonPropertyName("value")] string Value);

public static class ClientIdentifierMapping
{
    public static ClientIdentifierDto ToDto(ClientIdentifier c) => new(
        PrefixWire(c.Prefix),
        c.Value);

    public static ClientIdentifierDto[]? Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<ClientIdentifierDto[]>(json, TrustlistJson.Options);

    public static string? Serialize(ClientIdentifierDto[]? ids) =>
        ids is null || ids.Length == 0
            ? null
            : JsonSerializer.Serialize(ids, TrustlistJson.Options);

    private static string PrefixWire(ClientIdentifierPrefix p) => p switch
    {
        ClientIdentifierPrefix.X509SanDns => "x509_san_dns",
        ClientIdentifierPrefix.X509SanUri => "x509_san_uri",
        ClientIdentifierPrefix.X509Hash => "x509_hash",
        ClientIdentifierPrefix.VerifierAttestation => "verifier_attestation",
        ClientIdentifierPrefix.OpenidFederation => "openid_federation",
        ClientIdentifierPrefix.RedirectUri => "redirect_uri",
        ClientIdentifierPrefix.DecentralizedIdentifier => "decentralized_identifier",
        _ => p.ToString().ToLowerInvariant()
    };
}

/// <summary>
/// CertificationReference on the wire — openapi-trustlist-directory.yaml §CertificationReference.
/// </summary>
public record CertificationReferenceDto(
    [property: JsonPropertyName("scheme")] string Scheme,
    [property: JsonPropertyName("scheme_version")] string SchemeVersion,
    [property: JsonPropertyName("certificate_id")] string CertificateId,
    [property: JsonPropertyName("issued_at")] DateTimeOffset IssuedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("issuing_body")] string IssuingBody);

public static class CertificationMapping
{
    public static CertificationReferenceDto? FromEntity(TrustlistEntity e) =>
        e.CertificationScheme is null || e.CertificateId is null ||
        e.CertificationSchemeVersion is null || e.CertificationIssuedAt is null ||
        e.CertificationExpiresAt is null || e.CertificationIssuingBody is null
            ? null
            : new CertificationReferenceDto(
                e.CertificationScheme,
                e.CertificationSchemeVersion,
                e.CertificateId,
                e.CertificationIssuedAt.Value,
                e.CertificationExpiresAt.Value,
                e.CertificationIssuingBody);
}

/// <summary>
/// Contact block on the wire — openapi-trustlist-directory.yaml §Contact.
/// </summary>
public record ContactDto(
    [property: JsonPropertyName("security_email")] string SecurityEmail,
    [property: JsonPropertyName("csirt_email")] string? CsirtEmail);

public static class ContactMapping
{
    public static ContactDto? FromEntity(TrustlistEntity e) =>
        string.IsNullOrWhiteSpace(e.SecurityEmail)
            ? null
            : new ContactDto(e.SecurityEmail!, e.CsirtEmail);
}

internal static class StringArrayJson
{
    public static string[]? Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<string[]>(json, TrustlistJson.Options);

    public static string? Serialize(string[]? values) =>
        values is null || values.Length == 0
            ? null
            : JsonSerializer.Serialize(values, TrustlistJson.Options);
}

/// <summary>
/// IssuerRecord on the wire — openapi-trustlist-directory.yaml §IssuerRecord.
/// </summary>
public record IssuerRecordDto(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("entity_id")] string EntityId,
    [property: JsonPropertyName("entity_name")] string EntityName,
    [property: JsonPropertyName("entity_legal_name")] string EntityLegalName,
    [property: JsonPropertyName("jurisdiction")] string Jurisdiction,
    [property: JsonPropertyName("registration_number")] string RegistrationNumber,
    [property: JsonPropertyName("certification_reference")] CertificationReferenceDto CertificationReference,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("trust_anchors")] TrustAnchorDto[] TrustAnchors,
    [property: JsonPropertyName("supported_credential_formats")] string[] SupportedCredentialFormats,
    [property: JsonPropertyName("credential_types")] string[] CredentialTypes,
    [property: JsonPropertyName("status_list_endpoint")] string StatusListEndpoint,
    [property: JsonPropertyName("scope")] string[] Scope,
    [property: JsonPropertyName("contact")] ContactDto? Contact,
    [property: JsonPropertyName("next_update")] DateTimeOffset? NextUpdate);

/// <summary>
/// VerifierRecord on the wire — openapi-trustlist-directory.yaml §VerifierRecord.
/// </summary>
public record VerifierRecordDto(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("entity_id")] string EntityId,
    [property: JsonPropertyName("entity_name")] string EntityName,
    [property: JsonPropertyName("entity_legal_name")] string? EntityLegalName,
    [property: JsonPropertyName("jurisdiction")] string Jurisdiction,
    [property: JsonPropertyName("registration_number")] string? RegistrationNumber,
    [property: JsonPropertyName("certification_reference")] CertificationReferenceDto CertificationReference,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("client_identifiers")] ClientIdentifierDto[] ClientIdentifiers,
    [property: JsonPropertyName("scope_allowed")] string[] ScopeAllowed,
    [property: JsonPropertyName("contact")] ContactDto? Contact,
    [property: JsonPropertyName("next_update")] DateTimeOffset? NextUpdate);

/// <summary>
/// WalletProviderRecord on the wire — openapi-trustlist-directory.yaml §WalletProviderRecord.
/// </summary>
public record WalletProviderRecordDto(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("entity_id")] string EntityId,
    [property: JsonPropertyName("entity_name")] string EntityName,
    [property: JsonPropertyName("entity_legal_name")] string EntityLegalName,
    [property: JsonPropertyName("jurisdiction")] string Jurisdiction,
    [property: JsonPropertyName("registration_number")] string RegistrationNumber,
    [property: JsonPropertyName("certification_reference")] CertificationReferenceDto CertificationReference,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("trust_anchors")] TrustAnchorDto[] TrustAnchors,
    [property: JsonPropertyName("wia_status_list_uri")] string WiaStatusListUri,
    [property: JsonPropertyName("wia_revocation_maintenance_period_days")] int WiaRevocationMaintenancePeriodDays,
    [property: JsonPropertyName("wia_attestation_format")] string[] WiaAttestationFormat,
    [property: JsonPropertyName("ka_attestation_format")] string[]? KaAttestationFormat,
    [property: JsonPropertyName("supported_credential_formats")] string[] SupportedCredentialFormats,
    [property: JsonPropertyName("wallet_unit_audit_log_uri")] string? WalletUnitAuditLogUri,
    [property: JsonPropertyName("contact")] ContactDto? Contact,
    [property: JsonPropertyName("next_update")] DateTimeOffset? NextUpdate);

/// <summary>
/// Maps a TrustlistEntity to its role-specific record DTO. Throws when the entity is
/// missing role-required fields so the caller can return a 422 (caller handles).
/// </summary>
public static class RoleRecordMapper
{
    public static IssuerRecordDto ToIssuer(TrustlistEntity e)
    {
        // Missing-field check first so we can return a 422 at the call site rather than
        // 500'ing inside the projection.
        if (e.EntityLegalName is null)
            throw new InvalidOperationException($"Issuer {e.EntityId} missing entity_legal_name");
        if (e.RegistrationNumber is null)
            throw new InvalidOperationException($"Issuer {e.EntityId} missing registration_number");
        if (CertificationMapping.FromEntity(e) is not { } certRef)
            throw new InvalidOperationException($"Issuer {e.EntityId} missing certification_reference");
        if (TrustAnchorMapping.Deserialize(e.TrustAnchorsJson) is not { Length: > 0 } anchors)
            throw new InvalidOperationException($"Issuer {e.EntityId} missing trust_anchors");
        if (StringArrayJson.Deserialize(e.SupportedCredentialFormatsJson) is not { Length: > 0 } formats)
            throw new InvalidOperationException($"Issuer {e.EntityId} missing supported_credential_formats");
        if (StringArrayJson.Deserialize(e.CredentialTypesJson) is not { Length: > 0 } credTypes)
            throw new InvalidOperationException($"Issuer {e.EntityId} missing credential_types");
        if (e.StatusListEndpoint is null)
            throw new InvalidOperationException($"Issuer {e.EntityId} missing status_list_endpoint");

        return new IssuerRecordDto(
            "issuer",
            e.EntityId,
            e.EntityName,
            e.EntityLegalName,
            e.Jurisdiction,
            e.RegistrationNumber,
            certRef,
            StatusWire(e.Status),
            anchors,
            formats,
            credTypes,
            e.StatusListEndpoint,
            StringArrayJson.Deserialize(e.IssuerScopeJson) ?? Array.Empty<string>(),
            ContactMapping.FromEntity(e),
            e.NextUpdate);
    }

    public static VerifierRecordDto ToVerifier(TrustlistEntity e)
    {
        if (CertificationMapping.FromEntity(e) is not { } certRef)
            throw new InvalidOperationException($"Verifier {e.EntityId} missing certification_reference");
        if (ClientIdentifierMapping.Deserialize(e.ClientIdentifiersJson) is not { Length: > 0 } clientIds)
            throw new InvalidOperationException($"Verifier {e.EntityId} missing client_identifiers");

        return new VerifierRecordDto(
            "verifier",
            e.EntityId,
            e.EntityName,
            e.EntityLegalName,
            e.Jurisdiction,
            e.RegistrationNumber,
            certRef,
            StatusWire(e.Status),
            clientIds,
            StringArrayJson.Deserialize(e.ScopeAllowedJson) ?? Array.Empty<string>(),
            ContactMapping.FromEntity(e),
            e.NextUpdate);
    }

    public static WalletProviderRecordDto ToWalletProvider(TrustlistEntity e)
    {
        if (e.EntityLegalName is null)
            throw new InvalidOperationException($"WalletProvider {e.EntityId} missing entity_legal_name");
        if (e.RegistrationNumber is null)
            throw new InvalidOperationException($"WalletProvider {e.EntityId} missing registration_number");
        if (CertificationMapping.FromEntity(e) is not { } certRef)
            throw new InvalidOperationException($"WalletProvider {e.EntityId} missing certification_reference");
        if (TrustAnchorMapping.Deserialize(e.TrustAnchorsJson) is not { Length: > 0 } anchors)
            throw new InvalidOperationException($"WalletProvider {e.EntityId} missing trust_anchors");
        if (e.WiaStatusListUri is null)
            throw new InvalidOperationException($"WalletProvider {e.EntityId} missing wia_status_list_uri");
        if (e.WiaRevocationMaintenancePeriodDays is not { } period)
            throw new InvalidOperationException($"WalletProvider {e.EntityId} missing wia_revocation_maintenance_period_days");
        if (StringArrayJson.Deserialize(e.WiaAttestationFormatJson) is not { Length: > 0 } wiaFormats)
            throw new InvalidOperationException($"WalletProvider {e.EntityId} missing wia_attestation_format");
        if (StringArrayJson.Deserialize(e.SupportedCredentialFormatsJson) is not { Length: > 0 } formats)
            throw new InvalidOperationException($"WalletProvider {e.EntityId} missing supported_credential_formats");

        return new WalletProviderRecordDto(
            "wallet-provider",
            e.EntityId,
            e.EntityName,
            e.EntityLegalName,
            e.Jurisdiction,
            e.RegistrationNumber,
            certRef,
            StatusWire(e.Status),
            anchors,
            e.WiaStatusListUri,
            period,
            wiaFormats,
            StringArrayJson.Deserialize(e.KaAttestationFormatJson),
            formats,
            e.WalletUnitAuditLogUri,
            ContactMapping.FromEntity(e),
            e.NextUpdate);
    }

    private static string StatusWire(EntityStatus s) => s switch
    {
        EntityStatus.Applied => "applied",
        EntityStatus.Vetted => "vetted",
        EntityStatus.Valid => "valid",
        EntityStatus.Suspended => "suspended",
        EntityStatus.Withdrawn => "withdrawn",
        EntityStatus.Expired => "expired",
        _ => s.ToString().ToLowerInvariant()
    };
}
