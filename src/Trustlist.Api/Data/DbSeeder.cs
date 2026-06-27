using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Trustlist.Api.Domain;
using Trustlist.Api.Dtos;

namespace Trustlist.Api.Data;

/// <summary>
/// Applies migrations and seeds a default admin user plus a handful of
/// representative Trust List entities so the app is usable on first run.
/// </summary>
public static class DbSeeder
{
    public const string DefaultAdminEmail = "admin@trustlist.local";
    public const string DefaultAdminPassword = "Admin#12345";

    public static async Task SeedAsync(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

        // Migrate with a retry loop: MS SQL Express can take a while to accept
        // connections when the whole compose stack starts at once.
        await MigrateWithRetryAsync(db, logger);

        // Seed default admin user.
        if (await userManager.FindByEmailAsync(DefaultAdminEmail) is null)
        {
            var admin = new IdentityUser
            {
                UserName = DefaultAdminEmail,
                Email = DefaultAdminEmail,
                EmailConfirmed = true
            };
            var result = await userManager.CreateAsync(admin, DefaultAdminPassword);
            if (result.Succeeded)
                logger.LogInformation("Seeded default admin user {Email}", DefaultAdminEmail);
            else
                logger.LogWarning("Failed to seed admin user: {Errors}",
                    string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        // Seed Trust List entities.
        if (!await db.TrustlistEntities.AnyAsync())
        {
            db.TrustlistEntities.AddRange(SampleEntities());
            await db.SaveChangesAsync();
            logger.LogInformation("Seeded sample Trust List entities (role-specific fields populated)");
        }
    }

    private static async Task MigrateWithRetryAsync(AppDbContext db, ILogger logger)
    {
        const int maxAttempts = 20;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync();
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                logger.LogWarning("Database not ready (attempt {Attempt}/{Max}): {Message}",
                    attempt, maxAttempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static IEnumerable<TrustlistEntity> SampleEntities()
    {
        var now = DateTimeOffset.UtcNow;
        var opts = TrustlistJson.Options;

        // --- Issuers ---
        // Issuer #1 — University Degree Issuer (valid, 2 anchors)
        var issuer1Anchors = new[]
        {
            new TrustAnchorDto(
                Kid: "k-mhesi-2026-q1",
                Format: "jwk",
                Status: "active",
                NotBefore: now.AddMonths(-3),
                NotAfter: now.AddYears(2),
                Jwk: new
                {
                    kty = "EC",
                    crv = "P-256",
                    use = "sig",
                    alg = "ES256",
                    kid = "k-mhesi-2026-q1"
                },
                X5c: null),
            new TrustAnchorDto(
                Kid: "k-mhesi-2026-q2",
                Format: "jwk",
                Status: "active",
                NotBefore: now,
                NotAfter: now.AddYears(2),
                Jwk: new
                {
                    kty = "EC",
                    crv = "P-256",
                    use = "sig",
                    alg = "ES256",
                    kid = "k-mhesi-2026-q2"
                },
                X5c: null)
        };

        var issuer1 = new TrustlistEntity
        {
            Role = EntityRole.Issuer,
            EntityId = "https://issuer.mhesi.go.th",
            EntityName = "กระทรวง อว. (University Degree Issuer)",
            EntityLegalName = "Ministry of Higher Education, Science, Research and Innovation",
            Jurisdiction = "TH",
            RegistrationNumber = "TH-ISS-0001",
            Status = EntityStatus.Valid,
            CertificationScheme = "ETDA-ISSUER-CERT",
            CertificationSchemeVersion = "1.0.0",
            CertificationIssuedAt = now.AddMonths(-2),
            CertificationExpiresAt = now.AddYears(2),
            CertificationIssuingBody = "ETDA (สำนักงานพัฒนาธุรกรรมทางอิเล็กทรอนิกส์)",
            CertificateId = "ISS-CERT-2026-0001",
            Scope = "UniversityDegreeCredential",
            SecurityEmail = "security@issuer.mhesi.go.th",
            CsirtEmail = "csirt@issuer.mhesi.go.th",
            StatusListEndpoint = "https://issuer.mhesi.go.th/statuslist/v1.jwt",
            TrustAnchorsJson = JsonSerializer.Serialize(issuer1Anchors, opts),
            SupportedCredentialFormatsJson = JsonSerializer.Serialize(new[] { "vc+sd-jwt", "mso_mdoc" }, opts),
            CredentialTypesJson = JsonSerializer.Serialize(new[] { "UniversityDegreeCredential" }, opts),
            IssuerScopeJson = JsonSerializer.Serialize(new[] { "UniversityDegreeCredential" }, opts),
            NextUpdate = now.AddMonths(6)
        };

        // Issuer #2 — National ID Issuer (valid, 1 anchor)
        var issuer2Anchors = new[]
        {
            new TrustAnchorDto(
                Kid: "k-dopa-2026-q1",
                Format: "x509",
                Status: "active",
                NotBefore: now.AddMonths(-1),
                NotAfter: now.AddYears(3),
                Jwk: null,
                X5c: new[] { "MIIB+TCCAaCgAwIBAgI...placeholder-base64...=" })
        };

        var issuer2 = new TrustlistEntity
        {
            Role = EntityRole.Issuer,
            EntityId = "https://issuer.dopa.go.th",
            EntityName = "กรมการปกครอง (National ID Issuer)",
            EntityLegalName = "Department of Provincial Administration",
            Jurisdiction = "TH",
            RegistrationNumber = "TH-ISS-0002",
            Status = EntityStatus.Valid,
            CertificationScheme = "ETDA-ISSUER-CERT",
            CertificationSchemeVersion = "1.0.0",
            CertificationIssuedAt = now.AddMonths(-1),
            CertificationExpiresAt = now.AddYears(3),
            CertificationIssuingBody = "ETDA (สำนักงานพัฒนาธุรกรรมทางอิเล็กทรอนิกส์)",
            CertificateId = "ISS-CERT-2026-0002",
            Scope = "org.iso.18013.5.1.mDL,NationalIdCredential",
            SecurityEmail = "csirt@dopa.go.th",
            StatusListEndpoint = "https://issuer.dopa.go.th/statuslist/v1.jwt",
            TrustAnchorsJson = JsonSerializer.Serialize(issuer2Anchors, opts),
            SupportedCredentialFormatsJson = JsonSerializer.Serialize(new[] { "mso_mdoc" }, opts),
            CredentialTypesJson = JsonSerializer.Serialize(new[] { "org.iso.18013.5.1.mDL", "NationalIdCredential" }, opts),
            IssuerScopeJson = JsonSerializer.Serialize(new[] { "org.iso.18013.5.1.mDL:age_over_18", "NationalIdCredential" }, opts),
            NextUpdate = now.AddMonths(6)
        };

        // --- Verifier ---
        var verifierClientIds = new[]
        {
            new ClientIdentifierDto(Prefix: "x509_san_dns", Value: "verifier.bank-example.co.th"),
            new ClientIdentifierDto(Prefix: "redirect_uri", Value: "https://verifier.bank-example.co.th/callback")
        };

        var verifier = new TrustlistEntity
        {
            Role = EntityRole.Verifier,
            EntityId = "https://verifier.bank-example.co.th",
            EntityName = "ธนาคารตัวอย่าง (KYC Verifier)",
            EntityLegalName = "Example Bank PCL",
            Jurisdiction = "TH",
            RegistrationNumber = "TH-VER-0001",
            Status = EntityStatus.Valid,
            CertificationScheme = "ETDA-VERIFIER-CERT",
            CertificationSchemeVersion = "1.0.0",
            CertificationIssuedAt = now.AddMonths(-1),
            CertificationExpiresAt = now.AddYears(1),
            CertificationIssuingBody = "ETDA (สำนักงานพัฒนาธุรกรรมทางอิเล็กทรอนิกส์)",
            CertificateId = "VER-CERT-2026-0001",
            Scope = "request:NationalIdCredential,request:org.iso.18013.5.1.mDL:age_over_18",
            SecurityEmail = "security@bank-example.co.th",
            ClientIdentifiersJson = JsonSerializer.Serialize(verifierClientIds, opts),
            ScopeAllowedJson = JsonSerializer.Serialize(
                new[] { "request:NationalIdCredential", "request:org.iso.18013.5.1.mDL:age_over_18" }, opts),
            NextUpdate = now.AddMonths(3)
        };

        // --- Wallet Provider ---
        var wpAnchors = new[]
        {
            new TrustAnchorDto(
                Kid: "k-wp-2026-q1",
                Format: "jwk",
                Status: "active",
                NotBefore: now,
                NotAfter: now.AddYears(2),
                Jwk: new
                {
                    kty = "EC",
                    crv = "P-256",
                    use = "sig",
                    alg = "ES256",
                    kid = "k-wp-2026-q1"
                },
                X5c: null)
        };

        var wp = new TrustlistEntity
        {
            Role = EntityRole.WalletProvider,
            EntityId = "https://wallet.example.co.th",
            EntityName = "ThaID Wallet (Example WP)",
            EntityLegalName = "Example Wallet Co., Ltd.",
            Jurisdiction = "TH",
            RegistrationNumber = "TH-WP-0001",
            Status = EntityStatus.Suspended,
            CertificationScheme = "ETDA-WP-CERT",
            CertificationSchemeVersion = "1.0.0",
            CertificationIssuedAt = now.AddMonths(-1),
            CertificationExpiresAt = now.AddYears(1),
            CertificationIssuingBody = "ETDA (สำนักงานพัฒนาธุรกรรมทางอิเล็กทรอนิกส์)",
            CertificateId = "WP-CERT-2026-0001",
            Scope = "vc+sd-jwt,mso_mdoc",
            SecurityEmail = "security@wallet.example.co.th",
            WiaStatusListUri = "https://wallet.example.co.th/wia-statuslist/v1.jwt",
            WiaRevocationMaintenancePeriodDays = 14,
            WalletUnitAuditLogUri = "https://wallet.example.co.th/audit/log",
            TrustAnchorsJson = JsonSerializer.Serialize(wpAnchors, opts),
            WiaAttestationFormatJson = JsonSerializer.Serialize(new[] { "oauth-client-attestation+jwt" }, opts),
            KaAttestationFormatJson = JsonSerializer.Serialize(new[] { "oauth-client-attestation+jwt" }, opts),
            SupportedCredentialFormatsJson = JsonSerializer.Serialize(new[] { "vc+sd-jwt", "mso_mdoc" }, opts),
            NextUpdate = now.AddMonths(1)
        };

        return new[] { issuer1, issuer2, verifier, wp };
    }
}
