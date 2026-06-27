using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trustlist.Api.Data;
using Trustlist.Api.Domain;
using Trustlist.Api.Dtos;

namespace Trustlist.Api.Controllers;

/// <summary>
/// Public read-only role-keyed directory surface — openapi-trustlist-directory.yaml §WalletProviders.
/// Routes match the canonical contract:
///   GET /v1/wallet-providers?status=&amp;since=&amp;jurisdiction=&amp;page=&amp;limit=
///   GET /v1/wallet-providers/{entity-id}
///   GET /v1/wallet-providers/{entity-id}/status
///
/// WP records carry `trust_anchors[]` + WIA status list URIs — Issuers call this
/// at /as/token handler (cache miss) to verify WIA signature (OpenID4VCI §13.3).
/// </summary>
[ApiController]
[Route("v1/wallet-providers")]
public class WalletProvidersController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] DateTimeOffset? since,
        [FromQuery] string? jurisdiction,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 50)
    {
        page = Math.Max(1, page);
        limit = Math.Clamp(limit, 1, 200);

        var query = db.TrustlistEntities
            .AsNoTracking()
            .Where(e => e.Role == EntityRole.WalletProvider);

        if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseStatus(status, out var s))
                return BadRequest(new { message = $"Unknown status '{status}'" });
            query = query.Where(e => e.Status == s);
        }
        if (!string.IsNullOrWhiteSpace(jurisdiction))
            query = query.Where(e => e.Jurisdiction == jurisdiction);
        if (since is { } s2)
            query = query.Where(e => e.NextUpdate != null && e.NextUpdate >= s2);

        var total = await query.CountAsync();
        var items = await query
            .OrderBy(e => e.EntityName)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        var data = new List<WalletProviderRecordDto>(items.Count);
        foreach (var item in items)
        {
            try
            {
                data.Add(RoleRecordMapper.ToWalletProvider(item));
            }
            catch (InvalidOperationException ex)
            {
                HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Trustlist.Api.Directory")
                    .LogWarning("Skipping malformed wallet-provider record {EntityId}: {Message}",
                        item.EntityId, ex.Message);
            }
        }

        return Ok(new
        {
            data,
            pagination = new
            {
                page,
                limit,
                total,
                has_more = (long)page * limit < total
            },
            snapshot_id = "local",
            next_update = (DateTimeOffset?)items.Max(e => e.NextUpdate)
        });
    }

    /// <summary>
    /// Single-record + lightweight status dispatch. See IssuersController for the rationale
    /// (catch-all + URL-decode; <c>/status</c> suffix picks the lightweight variant).
    /// </summary>
    [HttpGet("{*entityId}")]
    public async Task<IActionResult> GetOrStatus(string entityId)
    {
        var isStatus = false;
        var slashStatus = entityId.IndexOf("/status", StringComparison.OrdinalIgnoreCase);
        if (slashStatus >= 0 && slashStatus == entityId.Length - "/status".Length)
        {
            isStatus = true;
            entityId = entityId[..slashStatus];
        }
        entityId = DecodeEntityId(entityId);

        var entity = await db.TrustlistEntities
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Role == EntityRole.WalletProvider && e.EntityId == entityId);

        if (isStatus)
        {
            if (entity is null)
                return Ok(new { entity_id = entityId, active = false, status = "not_found" });

            var active = entity.Status == EntityStatus.Valid;
            return Ok(new
            {
                entity_id = entity.EntityId,
                active,
                status = StatusWire(entity.Status),
                certification_reference = active ? CertificationMapping.FromEntity(entity) : null,
                next_update = entity.NextUpdate
            });
        }

        if (entity is null)
            return NotFound(new { message = $"Wallet-Provider '{entityId}' not found." });
        if (entity.Status == EntityStatus.Withdrawn)
            return StatusCode(StatusCodes.Status410Gone, new { message = $"Wallet-Provider '{entityId}' has been withdrawn." });

        return Ok(RoleRecordMapper.ToWalletProvider(entity));
    }

    private static string StatusWire(EntityStatus s) => StatusWireFormat.Wire(s);

    private static bool TryParseStatus(string wire, out EntityStatus status) =>
        StatusWireFormat.TryParse(wire, out status);

    private static string DecodeEntityId(string raw) =>
        Uri.UnescapeDataString(raw.Replace("%2F", "%2f").Replace("%3A", ":"));
}
