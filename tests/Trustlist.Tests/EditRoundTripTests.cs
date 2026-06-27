using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Trustlist.Api.Controllers;
using Trustlist.Api.Data;
using Trustlist.Api.Domain;
using Trustlist.Api.Dtos;
using Xunit;

namespace Trustlist.Tests;

/// <summary>
/// MAS-688: the admin Manage page now edits role-specific key material
/// (trust_anchors / client_identifiers / WIA) in-place via PUT /api/trustlist/{id}.
///
/// These tests drive <see cref="TrustlistController.Update"/> directly against an
/// EF Core in-memory <see cref="AppDbContext"/> to prove:
///   1. An edit that changes a trust anchor / client identifier / WIA field is
///      persisted and surfaced again via GET (the round-trip the UI depends on).
///   2. An edit that does NOT touch key material (null role-specific arrays) does
///      NOT wipe the existing trust_anchors / client_identifiers / WIA — the
///      regression guard the PUT already implements, now pinned by a test.
/// </summary>
public class EditRoundTripTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"edit-roundtrip-{Guid.NewGuid()}")
            .Options);

    private static TrustlistEntity SeedIssuerWithAnchor(AppDbContext db)
    {
        var entity = new TrustlistEntity
        {
            Role = EntityRole.Issuer,
            EntityId = "https://issuer.example.go.th",
            EntityName = "Example Issuer",
            EntityLegalName = "Example Issuer Co.",
            Jurisdiction = "TH",
            RegistrationNumber = "TH-ISS-9001",
            Status = EntityStatus.Valid,
            SecurityEmail = "security@issuer.example.go.th",
            // Existing key material the admin set previously.
            TrustAnchorsJson = TrustAnchorMapping.Serialize(new[]
            {
                new TrustAnchorDto("k-2026-q2", "jwk", "active", null, null,
                    System.Text.Json.JsonDocument.Parse("""{"kty":"EC","crv":"P-256","kid":"k-2026-q2"}""").RootElement,
                    null)
            }),
        };
        db.TrustlistEntities.Add(entity);
        db.SaveChanges();
        return entity;
    }

    [Fact]
    public async Task Edit_Changes_TrustAnchor_And_Roundtrips_Via_Get()
    {
        using var db = NewDb();
        var seeded = SeedIssuerWithAnchor(db);

        var controller = new TrustlistController(db);

        // Admin changes the signing key (new kid + rotates the old one to retired).
        var req = new UpdateTrustlistEntityRequest
        {
            EntityName = seeded.EntityName,
            EntityLegalName = seeded.EntityLegalName,
            Jurisdiction = seeded.Jurisdiction,
            RegistrationNumber = seeded.RegistrationNumber,
            Status = EntityStatus.Valid,
            SecurityEmail = seeded.SecurityEmail,
            TrustAnchors = new[]
            {
                new TrustAnchorDto("k-2026-q3", "jwk", "active", null, null, null, null)
            }
        };

        var result = await controller.Update(seeded.Id, req);
        Assert.IsType<OkObjectResult>(result.Result);

        // GET the entity back the way the Manage list / GET /api/trustlist/{id} would.
        var getResult = await controller.Get(seeded.Id);
        var dto = Assert.IsType<TrustlistEntityDto>(Assert.IsType<OkObjectResult>(getResult.Result).Value);

        Assert.NotNull(dto.TrustAnchors);
        Assert.Single(dto.TrustAnchors!);
        Assert.Equal("k-2026-q3", dto.TrustAnchors![0].Kid);
    }

    [Fact]
    public async Task Edit_Without_Touching_KeyMaterial_Preserves_Existing_Anchors()
    {
        using var db = NewDb();
        var seeded = SeedIssuerWithAnchor(db);

        var controller = new TrustlistController(db);

        // A "generic" edit — admin only changes the display name and leaves the
        // role-specific arrays null (exactly what the Manage editor sends when the
        // key-material editor is untouched for a role that owns no extra fields,
        // and what a partial PUT looks like). This MUST NOT wipe the trust anchors.
        var req = new UpdateTrustlistEntityRequest
        {
            EntityName = "Example Issuer (renamed)",
            EntityLegalName = seeded.EntityLegalName,
            Jurisdiction = seeded.Jurisdiction,
            RegistrationNumber = seeded.RegistrationNumber,
            Status = EntityStatus.Valid,
            SecurityEmail = seeded.SecurityEmail,
            TrustAnchors = null,
            ClientIdentifiers = null,
            WiaStatusListUri = null,
            WiaRevocationMaintenancePeriodDays = null,
            WiaAttestationFormat = null,
        };

        var result = await controller.Update(seeded.Id, req);
        Assert.IsType<OkObjectResult>(result.Result);

        var getResult = await controller.Get(seeded.Id);
        var dto = Assert.IsType<TrustlistEntityDto>(Assert.IsType<OkObjectResult>(getResult.Result).Value);

        // Name updated...
        Assert.Equal("Example Issuer (renamed)", dto.EntityName);
        // ...but the previously-set signing key survives the edit (regression guard).
        Assert.NotNull(dto.TrustAnchors);
        Assert.Single(dto.TrustAnchors!);
        Assert.Equal("k-2026-q2", dto.TrustAnchors![0].Kid);
    }
}
