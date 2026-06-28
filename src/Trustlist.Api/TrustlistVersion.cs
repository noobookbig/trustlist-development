using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Trustlist.Api.Data;
using Trustlist.Api.Domain;

namespace Trustlist.Api;

/// <summary>
/// MAS-725 — exposes a deterministic content version of the directory so a
/// caller (Issuer / Wallet / Verifier hitting <c>/v1/{role}</c>, or the admin
/// frontend on its index page) can pin to a specific snapshot of the
/// directory.
///
/// The version is the first 12 hex chars of SHA-256 over the canonicalised
/// entity set: (entity_id, role, status, updated_at, certification_expiry,
/// trust_anchors_json, client_identifiers_json, status_list_endpoint,
/// wia_status_list_uri, next_update). The set is sorted by (role, entity_id)
/// before hashing so the result is stable regardless of query order.
///
/// Why content-hash and not an incrementing counter:
///   - No state to lose on a fresh boot. A new deployment, a DB restore, or a
///     replica that lags by one row all produce a version that callers can
///     reason about from the wire alone.
///   - The hash is collision-resistant (12 hex chars = 48 bits — fine for a
///     snapshot identifier; full 256-bit digest is preserved in
///     <see cref="ComputeAsync"/>'s audit-log line if callers want it).
///   - When MAS-696's signed directory responses eventually grow a publisher
///     pipeline with a monotonic version (v0 spec §4 calls for SERIAL PK on
///     the snapshots table), the publisher-assigned integer wins; this
///     content-hash stays as a fast cross-check between signed responses and
///     the underlying DB state.
///
/// Cheap to compute: a single SELECT across TrustlistEntities, ordered by
/// (role, entity_id), projected to a 100-byte string per row. At realistic
/// Thai ecosystem sizes (&lt;10k rows) this is sub-millisecond. We do NOT
/// cache; the call sites are list endpoints that already round-trip the DB.
/// </summary>
public static class TrustlistVersion
{
    /// <summary>Length of the public version identifier (hex chars).</summary>
    public const int VersionLength = 12;

    /// <summary>
    /// Compute the current directory version. Returned value is short and
    /// stable for the lifetime of the entity set; it changes whenever any
    /// entity is added, removed, or has any field that contributes to the
    /// canonicalised payload updated.
    /// </summary>
    public static async Task<string> ComputeAsync(AppDbContext db, CancellationToken ct = default)
    {
        // Project only the columns that feed the version. Sorted by (role, entity_id)
        // so the digest is stable regardless of query order or storage layout.
        var rows = await db.TrustlistEntities
            .AsNoTracking()
            .OrderBy(e => e.Role).ThenBy(e => e.EntityId)
            .Select(e => new
            {
                e.Role,
                e.EntityId,
                e.Status,
                e.UpdatedAt,
                e.CertificationExpiresAt,
                e.TrustAnchorsJson,
                e.ClientIdentifiersJson,
                e.StatusListEndpoint,
                e.WiaStatusListUri,
                e.NextUpdate,
            })
            .ToListAsync(ct);

        var sb = new StringBuilder(capacity: 256 + rows.Count * 96);
        foreach (var r in rows)
        {
            sb.Append((int)r.Role).Append('|');
            sb.Append(r.EntityId).Append('|');
            sb.Append((int)r.Status).Append('|');
            sb.Append(r.UpdatedAt.ToUnixTimeMilliseconds()).Append('|');
            sb.Append(r.CertificationExpiresAt?.ToUnixTimeMilliseconds() ?? -1).Append('|');
            sb.Append(r.TrustAnchorsJson ?? "").Append('|');
            sb.Append(r.ClientIdentifiersJson ?? "").Append('|');
            sb.Append(r.StatusListEndpoint ?? "").Append('|');
            sb.Append(r.WiaStatusListUri ?? "").Append('|');
            sb.Append(r.NextUpdate?.ToUnixTimeMilliseconds() ?? -1).Append('\n');
        }

        // Empty directory -> deterministic zero-content version. Useful for
        // first-boot smoke tests.
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var digest = SHA256.HashData(bytes);
        return Convert.ToHexString(digest, 0, VersionLength / 2).ToLowerInvariant();
    }

    /// <summary>
    /// Count entities per role. Returned as a flat record so the
    /// <c>/v1/trustlist</c> endpoint can render it cheaply without a second
    /// grouped query.
    /// </summary>
    public static async Task<TrustlistCounts> CountAsync(AppDbContext db, CancellationToken ct = default)
    {
        var grouped = await db.TrustlistEntities
            .AsNoTracking()
            .GroupBy(e => e.Role)
            .Select(g => new { Role = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var counts = new TrustlistCounts(
            Issuers: 0, Verifiers: 0, WalletProviders: 0, ResolverNodes: 0, Total: 0);
        foreach (var g in grouped)
        {
            counts = g.Role switch
            {
                EntityRole.Issuer        => counts with { Issuers = g.Count },
                EntityRole.Verifier      => counts with { Verifiers = g.Count },
                EntityRole.WalletProvider => counts with { WalletProviders = g.Count },
                EntityRole.ResolverNode  => counts with { ResolverNodes = g.Count },
                _ => counts,
            };
        }
        return counts with { Total = counts.Issuers + counts.Verifiers + counts.WalletProviders + counts.ResolverNodes };
    }
}

/// <summary>
/// Per-role counts returned by <c>GET /v1/trustlist</c>. Immutable record so
/// controllers can project it directly into the JSON response.
/// </summary>
public record TrustlistCounts(
    int Issuers,
    int Verifiers,
    int WalletProviders,
    int ResolverNodes,
    int Total);