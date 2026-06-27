using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trustlist.Api.Data;
using Trustlist.Api.Domain;
using Trustlist.Api.Dtos;

namespace Trustlist.Api.Controllers;

/// <summary>
/// Public read-only role-keyed directory surface — openapi-trustlist-directory.yaml §Issuers.
/// Routes match the canonical contract:
///   GET /v1/issuers?status=&amp;since=&amp;jurisdiction=&amp;page=&amp;limit=
///   GET /v1/issuers/{entity-id}
///   GET /v1/issuers/{entity-id}/status
///
/// Read-only and public so Verifiers and Wallets can hit the directory without
/// authentication (TL-1 rule — see MAS-676 plan comment).
/// </summary>
[ApiController]
[Route("v1/issuers")]
public class IssuersController(AppDbContext db) : ControllerBase
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
            .Where(e => e.Role == EntityRole.Issuer);

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

        var data = new List<IssuerRecordDto>(items.Count);
        foreach (var item in items)
        {
            try
            {
                data.Add(RoleRecordMapper.ToIssuer(item));
            }
            catch (InvalidOperationException ex)
            {
                // Skip malformed rows — never let one bad entity kill the whole list.
                // Logged at warning level via the controller's ILogger; surfaced to the
                // caller only via the snapshot_id field once we add a publish pipeline.
                HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Trustlist.Api.Directory")
                    .LogWarning("Skipping malformed issuer record {EntityId}: {Message}",
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
    /// Single-record + lightweight status dispatch. Routes match openapi-trustlist-directory.yaml:
    ///   GET /v1/issuers/{entity-id}            → full IssuerRecord
    ///   GET /v1/issuers/{entity-id}/status     → lightweight RoleStatusResponse
    /// We use a single catch-all route + URL-decode the entity_id because the canonical
    /// entity_id form (e.g. <c>https://issuer.example.go.th</c>) contains literal slashes
    /// that would otherwise split the path. The optional trailing <c>/status</c> picks
    /// the lightweight variant.
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
            .FirstOrDefaultAsync(e => e.Role == EntityRole.Issuer && e.EntityId == entityId);

        if (isStatus)
        {
            if (entity is null)
                return Ok(new { entity_id = entityId, active = false, status = "not_found" });

            var active = entity.Status == EntityStatus.Valid;
            IssuerRecordDto? record = null;
            try { record = active ? RoleRecordMapper.ToIssuer(entity) : null; }
            catch (InvalidOperationException) { record = null; }
            return Ok(new
            {
                entity_id = entity.EntityId,
                active,
                status = StatusWire(entity.Status),
                scope = record?.Scope,
                credential_types = record?.CredentialTypes,
                certification_reference = record?.CertificationReference,
                next_update = entity.NextUpdate
            });
        }

        if (entity is null)
            return NotFound(new { message = $"Issuer '{entityId}' not found." });
        if (entity.Status == EntityStatus.Withdrawn)
            return StatusCode(StatusCodes.Status410Gone, new { message = $"Issuer '{entityId}' has been withdrawn." });

        return Ok(RoleRecordMapper.ToIssuer(entity));
    }

    private static string StatusWire(EntityStatus s) => StatusWireFormat.Wire(s);

    private static bool TryParseStatus(string wire, out EntityStatus status) =>
        StatusWireFormat.TryParse(wire, out status);

    /// <summary>
    /// Decode an entity_id from a URL path segment. The canonical OpenAPI shape stores
    /// entity_id as a URL like <c>https://issuer.example.go.th</c> — clients encode
    /// <c>:</c> as <c>%3A</c> and <c>/</c> as <c>%2F</c>. ASP.NET Core keeps <c>%2F</c>
    /// in the route value (it's a delimiter). We reverse it here.
    /// </summary>
    private static string DecodeEntityId(string raw) =>
        Uri.UnescapeDataString(raw.Replace("%2F", "%2f").Replace("%3A", ":"));
}
