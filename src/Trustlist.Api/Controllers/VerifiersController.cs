using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trustlist.Api.Data;
using Trustlist.Api.Domain;
using Trustlist.Api.Dtos;

namespace Trustlist.Api.Controllers;

/// <summary>
/// Public read-only role-keyed directory surface — openapi-trustlist-directory.yaml §Verifiers.
/// Routes match the canonical contract:
///   GET /v1/verifiers?status=&amp;since=&amp;jurisdiction=&amp;page=&amp;limit=
///   GET /v1/verifiers/{entity-id}
///   GET /v1/verifiers/{entity-id}/status
///
/// Verifier records carry `client_identifiers[]` (OpenID4VP §5.9) — wallets call
/// /v1/verifiers/{entity-id} before releasing a credential (Pattern A hot path).
/// </summary>
[ApiController]
[Route("v1/verifiers")]
public class VerifiersController(AppDbContext db) : ControllerBase
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
            .Where(e => e.Role == EntityRole.Verifier);

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

        var data = new List<VerifierRecordDto>(items.Count);
        foreach (var item in items)
        {
            try
            {
                data.Add(RoleRecordMapper.ToVerifier(item));
            }
            catch (InvalidOperationException ex)
            {
                HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Trustlist.Api.Directory")
                    .LogWarning("Skipping malformed verifier record {EntityId}: {Message}",
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
            trustlist_version = await TrustlistVersion.ComputeAsync(db),
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
            .FirstOrDefaultAsync(e => e.Role == EntityRole.Verifier && e.EntityId == entityId);

        if (isStatus)
        {
            if (entity is null)
                return Ok(new { entity_id = entityId, active = false, status = "not_found" });

            var active = entity.Status == EntityStatus.Valid;
            ClientIdentifierDto? primaryId = null;
            if (active)
            {
                try
                {
                    var record = RoleRecordMapper.ToVerifier(entity);
                    primaryId = record.ClientIdentifiers.FirstOrDefault();
                }
                catch (InvalidOperationException) { primaryId = null; }
            }
            return Ok(new
            {
                entity_id = entity.EntityId,
                active,
                status = StatusWire(entity.Status),
                client_identifier = primaryId is null ? null : new { primaryId.Prefix, primaryId.Value },
                certification_reference = active ? CertificationMapping.FromEntity(entity) : null,
                next_update = entity.NextUpdate
            });
        }

        if (entity is null)
            return NotFound(new { message = $"Verifier '{entityId}' not found." });
        if (entity.Status == EntityStatus.Withdrawn)
            return StatusCode(StatusCodes.Status410Gone, new { message = $"Verifier '{entityId}' has been withdrawn." });

        return Ok(RoleRecordMapper.ToVerifier(entity));
    }

    private static string StatusWire(EntityStatus s) => StatusWireFormat.Wire(s);

    private static bool TryParseStatus(string wire, out EntityStatus status) =>
        StatusWireFormat.TryParse(wire, out status);

    private static string DecodeEntityId(string raw) =>
        Uri.UnescapeDataString(raw.Replace("%2F", "%2f").Replace("%3A", ":"));
}
