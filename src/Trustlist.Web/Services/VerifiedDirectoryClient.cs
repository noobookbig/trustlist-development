using System.Text.Json;

namespace Trustlist.Web.Services;

/// <summary>
/// MAS-728 — verifier-grade reader for the public directory.
///
/// The public <c>Directory.razor</c> page used to read the <b>unsigned</b> admin
/// endpoint <c>GET /api/trustlist</c>. This client instead consumes the
/// <b>signed</b> public surface (<c>/v1/issuers</c>, <c>/v1/verifiers</c>,
/// <c>/v1/wallet-providers</c>), which the API serves as JWS-compact
/// (<c>application/jwt</c>, EdDSA/Ed25519 — MAS-696). Every payload is verified
/// against the publisher JWKS at <c>/.well-known/trustlist-jwks.json</c> before
/// any record is handed back, so what the page renders is cryptographically
/// attributable to the trust-list publisher — not whatever an unsigned admin
/// endpoint happened to return.
///
/// Trust model: the signature is checked first; on any failure
/// (<see cref="JwsVerificationResult.IsValid"/> false) we return the failure to
/// the caller and render <b>nothing</b> as trusted. Silently rendering
/// unverified data would defeat the entire point of moving to the signed
/// surface (Issuer/Holder/Verifier separation — the directory is now
/// verifier-grade).
/// </summary>
public class VerifiedDirectoryClient(JwsVerifier verifier, IConfiguration config)
{
    // Operator-pinned publisher kid. When unset (v0 single-publisher dev), the
    // verifier falls back to the sole JWKS key. Pinning is the production posture.
    private string? PinnedKid => config["TrustlistPublisher:Kid"];

    /// <summary>
    /// Map the page's role filter (the UI uses PascalCase enum names) to the
    /// canonical <c>/v1/{role}</c> path segment used by the signed surface.
    /// </summary>
    public static string? RolePath(string? role) => role switch
    {
        null or "" => null,
        "Issuer" => "issuers",
        "Verifier" => "verifiers",
        "WalletProvider" => "wallet-providers",
        _ => null,
    };

    private static readonly string[] AllRolePaths = ["issuers", "verifiers", "wallet-providers"];

    /// <summary>
    /// Fetch + verify the directory for the given filters. Returns the verified
    /// entities (already filtered) plus a verification verdict the page can use
    /// to render an explicit "unverified" state instead of trusting bad data.
    ///
    /// <paramref name="role"/> uses the UI enum names ("Issuer"/"Verifier"/
    /// "WalletProvider"); <paramref name="status"/> uses the UI status labels
    /// ("Valid"/"Suspended"/…). <paramref name="search"/> is applied client-side
    /// (the signed list endpoints intentionally do not expose a free-text query).
    /// </summary>
    public async Task<VerifiedDirectoryResult> GetAsync(
        string? role = null,
        string? status = null,
        string? search = null,
        CancellationToken ct = default)
    {
        var (kid, kidError) = await verifier.ResolveKidAsync(PinnedKid, ct);
        if (kid is null)
            return VerifiedDirectoryResult.Unverified(kidError ?? "Could not resolve publisher key.");

        // Which signed surfaces to read: one role, or all three for "All roles".
        var paths = RolePath(role) is { } single ? new[] { single } : AllRolePaths;

        var statusWire = StatusWire(status);
        var entities = new List<TrustlistEntityModel>();

        foreach (var path in paths)
        {
            var url = $"/v1/{path}";
            if (statusWire is not null) url += $"?status={Uri.EscapeDataString(statusWire)}";

            var verification = await verifier.FetchAndVerifyAsync(url, kid, ct);
            if (!verification.IsValid)
            {
                // Fail closed: one tampered/unverifiable surface invalidates the
                // whole render. We never mix verified and unverified rows.
                return VerifiedDirectoryResult.Unverified(
                    $"Signature verification failed for /v1/{path}: {verification.FailureMessage}");
            }

            entities.AddRange(ParseList(verification.PayloadJson!));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            entities = entities.FindAll(e =>
                e.EntityName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || e.EntityId.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        return VerifiedDirectoryResult.Verified(entities, kid);
    }

    /// <summary>
    /// Parse a verified <c>/v1/{role}</c> list payload
    /// (<c>{ "data": [ …role records… ], "pagination": … }</c>) into the view
    /// model the page already renders. The signed role records use a different,
    /// richer shape than the legacy admin model, so we translate explicitly.
    /// </summary>
    internal static IEnumerable<TrustlistEntityModel> ParseList(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            yield break;

        var i = 0;
        foreach (var rec in data.EnumerateArray())
        {
            yield return ToViewModel(rec, i++);
        }
    }

    private static TrustlistEntityModel ToViewModel(JsonElement rec, int syntheticId)
    {
        string roleWire = Str(rec, "role") ?? "";
        return new TrustlistEntityModel(
            Id: syntheticId, // signed records have no DB id; the page only uses Id as a UI key
            Role: RoleDisplay(roleWire),
            EntityId: Str(rec, "entity_id") ?? "",
            EntityName: Str(rec, "entity_name") ?? "",
            EntityLegalName: Str(rec, "entity_legal_name"),
            Jurisdiction: Str(rec, "jurisdiction") ?? "",
            RegistrationNumber: Str(rec, "registration_number"),
            Status: StatusDisplay(Str(rec, "status")),
            CertificationScheme: CertField(rec, "scheme"),
            CertificateId: CertField(rec, "certificate_id"),
            Scope: ScopeCsv(rec),
            SecurityEmail: ContactField(rec, "security_email"),
            NextUpdate: Date(rec, "next_update"),
            CreatedAt: default,
            UpdatedAt: default,
            TrustAnchors: TrustAnchors(rec),
            ClientIdentifiers: ClientIdentifiers(rec),
            WiaStatusListUri: Str(rec, "wia_status_list_uri"),
            WiaRevocationMaintenancePeriodDays: Int(rec, "wia_revocation_maintenance_period_days"),
            WiaAttestationFormat: StrArray(rec, "wia_attestation_format"));
    }

    // --- field helpers ----------------------------------------------------

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static DateTimeOffset? Date(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetString(), out var d) ? d : null;

    private static string[]? StrArray(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s) list.Add(s);
        return list.Count == 0 ? null : list.ToArray();
    }

    private static string? CertField(JsonElement rec, string name) =>
        rec.TryGetProperty("certification_reference", out var c) && c.ValueKind == JsonValueKind.Object
            ? Str(c, name) : null;

    private static string? ContactField(JsonElement rec, string name) =>
        rec.TryGetProperty("contact", out var c) && c.ValueKind == JsonValueKind.Object
            ? Str(c, name) : null;

    // Issuer/WalletProvider use "scope", Verifier uses "scope_allowed"; both are arrays.
    private static string? ScopeCsv(JsonElement rec)
    {
        var arr = StrArray(rec, "scope") ?? StrArray(rec, "scope_allowed");
        return arr is null ? null : string.Join(", ", arr);
    }

    private static TrustAnchorModel[]? TrustAnchors(JsonElement rec)
    {
        if (!rec.TryGetProperty("trust_anchors", out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<TrustAnchorModel>();
        foreach (var a in v.EnumerateArray())
        {
            list.Add(new TrustAnchorModel(
                Kid: Str(a, "kid") ?? "",
                Format: Str(a, "format") ?? "",
                Status: Str(a, "status") ?? "",
                NotBefore: Date(a, "not_before"),
                NotAfter: Date(a, "not_after"),
                Jwk: a.TryGetProperty("jwk", out var jwk) && jwk.ValueKind != JsonValueKind.Null
                    ? jwk.Clone() : (JsonElement?)null,
                X5c: StrArray(a, "x5c")));
        }
        return list.Count == 0 ? null : list.ToArray();
    }

    private static ClientIdentifierModel[]? ClientIdentifiers(JsonElement rec)
    {
        if (!rec.TryGetProperty("client_identifiers", out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<ClientIdentifierModel>();
        foreach (var c in v.EnumerateArray())
            list.Add(new ClientIdentifierModel(Str(c, "prefix") ?? "", Str(c, "value") ?? ""));
        return list.Count == 0 ? null : list.ToArray();
    }

    // --- enum / wire translation ------------------------------------------

    // The signed surface emits snake_case role wire values ("issuer",
    // "wallet-provider", "verifier"); the page's CSS + badges key off the
    // PascalCase display names the admin model used.
    private static string RoleDisplay(string wire) => wire switch
    {
        "issuer" => "Issuer",
        "verifier" => "Verifier",
        "wallet-provider" => "WalletProvider",
        _ => wire,
    };

    private static string StatusDisplay(string? wire) => wire switch
    {
        "applied" => "Applied",
        "vetted" => "Vetted",
        "valid" => "Valid",
        "suspended" => "Suspended",
        "withdrawn" => "Withdrawn",
        "expired" => "Expired",
        null => "",
        _ => char.ToUpperInvariant(wire[0]) + wire[1..],
    };

    // UI status label ("Valid") -> signed-surface wire status ("valid").
    private static string? StatusWire(string? uiStatus) =>
        string.IsNullOrWhiteSpace(uiStatus) ? null : uiStatus.ToLowerInvariant();
}

/// <summary>
/// Outcome of a verified directory read. <see cref="IsVerified"/> gates whether
/// the page may render <see cref="Entities"/>; when false, the page MUST show an
/// explicit "signature could not be verified" state and render no rows.
/// </summary>
public record VerifiedDirectoryResult(
    bool IsVerified,
    IReadOnlyList<TrustlistEntityModel> Entities,
    string? Kid,
    string? Error)
{
    public static VerifiedDirectoryResult Verified(IReadOnlyList<TrustlistEntityModel> entities, string kid) =>
        new(true, entities, kid, null);

    public static VerifiedDirectoryResult Unverified(string error) =>
        new(false, Array.Empty<TrustlistEntityModel>(), null, error);
}
