using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Trustlist.Api.Data;
using Trustlist.Api.Domain;
using Trustlist.Api.Dtos;

namespace Trustlist.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TrustlistController(AppDbContext db) : ControllerBase
{
    /// <summary>List Trust List entities. Read access is public so the directory is browsable.</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<TrustlistEntityDto>>> List(
        [FromQuery] EntityRole? role,
        [FromQuery] EntityStatus? status,
        [FromQuery] string? jurisdiction,
        [FromQuery] string? q)
    {
        var query = db.TrustlistEntities.AsNoTracking().AsQueryable();

        if (role is not null) query = query.Where(e => e.Role == role);
        if (status is not null) query = query.Where(e => e.Status == status);
        if (!string.IsNullOrWhiteSpace(jurisdiction))
            query = query.Where(e => e.Jurisdiction == jurisdiction);
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(e => e.EntityName.Contains(q) || e.EntityId.Contains(q));

        var items = await query
            .OrderBy(e => e.Role).ThenBy(e => e.EntityName)
            .Select(e => TrustlistEntityDto.From(e))
            .ToListAsync();

        return Ok(items);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<TrustlistEntityDto>> Get(int id)
    {
        var entity = await db.TrustlistEntities.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);
        return entity is null ? NotFound() : Ok(TrustlistEntityDto.From(entity));
    }

    /// <summary>Create a Trust List entity. Requires authentication (login).</summary>
    [Authorize]
    [HttpPost]
    public async Task<ActionResult<TrustlistEntityDto>> Create([FromBody] CreateTrustlistEntityRequest req)
    {
        var exists = await db.TrustlistEntities.AnyAsync(e => e.EntityId == req.EntityId);
        if (exists)
            return Conflict(new { message = "An entity with this entity_id already exists." });

        var entity = new TrustlistEntity
        {
            Role = req.Role,
            EntityId = req.EntityId,
            EntityName = req.EntityName,
            EntityLegalName = req.EntityLegalName,
            Jurisdiction = req.Jurisdiction,
            RegistrationNumber = req.RegistrationNumber,
            Status = req.Status,
            CertificationScheme = req.CertificationScheme,
            CertificateId = req.CertificateId,
            Scope = req.Scope,
            SecurityEmail = req.SecurityEmail,
            NextUpdate = req.NextUpdate,
            // Role-specific key material / WIA fields (MAS-687).
            TrustAnchorsJson = SerializeOrNull(req.TrustAnchors),
            ClientIdentifiersJson = SerializeOrNull(req.ClientIdentifiers),
            WiaStatusListUri = NullIfBlank(req.WiaStatusListUri),
            WiaRevocationMaintenancePeriodDays = req.WiaRevocationMaintenancePeriodDays,
            WiaAttestationFormatJson = SerializeOrNull(req.WiaAttestationFormat),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.TrustlistEntities.Add(entity);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = entity.Id }, TrustlistEntityDto.From(entity));
    }

    // Serialize a structured array to the canonical snake_case JSON used by the
    // read/projection layer, or null when empty so role validation stays consistent.
    private static string? SerializeOrNull<T>(T[]? values) =>
        values is null || values.Length == 0
            ? null
            : JsonSerializer.Serialize(values, TrustlistJson.Options);

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;

    [Authorize]
    [HttpPut("{id:int}")]
    public async Task<ActionResult<TrustlistEntityDto>> Update(int id, [FromBody] UpdateTrustlistEntityRequest req)
    {
        var entity = await db.TrustlistEntities.FirstOrDefaultAsync(e => e.Id == id);
        if (entity is null) return NotFound();

        entity.EntityName = req.EntityName;
        entity.EntityLegalName = req.EntityLegalName;
        entity.Jurisdiction = req.Jurisdiction;
        entity.RegistrationNumber = req.RegistrationNumber;
        entity.Status = req.Status;
        entity.CertificationScheme = req.CertificationScheme;
        entity.CertificateId = req.CertificateId;
        entity.Scope = req.Scope;
        entity.SecurityEmail = req.SecurityEmail;
        entity.NextUpdate = req.NextUpdate;
        // Role-specific key material / WIA fields (MAS-687). Only overwrite when the
        // caller supplied a value, so a generic edit doesn't wipe existing key material.
        if (req.TrustAnchors is not null)
            entity.TrustAnchorsJson = SerializeOrNull(req.TrustAnchors);
        if (req.ClientIdentifiers is not null)
            entity.ClientIdentifiersJson = SerializeOrNull(req.ClientIdentifiers);
        if (req.WiaStatusListUri is not null)
            entity.WiaStatusListUri = NullIfBlank(req.WiaStatusListUri);
        if (req.WiaRevocationMaintenancePeriodDays is not null)
            entity.WiaRevocationMaintenancePeriodDays = req.WiaRevocationMaintenancePeriodDays;
        if (req.WiaAttestationFormat is not null)
            entity.WiaAttestationFormatJson = SerializeOrNull(req.WiaAttestationFormat);
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();
        return Ok(TrustlistEntityDto.From(entity));
    }

    [Authorize]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var entity = await db.TrustlistEntities.FirstOrDefaultAsync(e => e.Id == id);
        if (entity is null) return NotFound();

        db.TrustlistEntities.Remove(entity);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
